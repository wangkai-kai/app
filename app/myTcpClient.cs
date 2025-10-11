using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace app
{
    public class MYTcpClient : IDisposable
    {
        // 核心网络对象
        private TcpClient _tcpClient;
        private NetworkStream _netStream;
        private readonly byte[] _recvBuffer = new byte[2048]; // 接收缓冲区
        private int _recvBufferLen = 0; // 当前缓冲区数据长度

        // 线程与状态控制
        private Thread _recvThread;
        private Thread _connectThread; // 合并连接和重连的统一线程
        private bool _isRunning = false;
        private bool _enableAutoReconnect = true;
        private string _targetIp;
        private int _targetPort;
        private bool _isConnecting = false; // 标记是否正在进行连接操作

        // 锁与配置
        private readonly object _netLock = new object();
        private const int RECONNECT_DELAY = 2000;

        // 事件与委托
        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnError;

        #region 构造与初始化
        public MYTcpClient()
        {
            _tcpClient = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true,
                ReceiveTimeout = 100,
                SendTimeout = 5000,
                ReceiveBufferSize = 8192
            };
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        #endregion

        #region 连接与断开
        public bool Connect(string ip, int port)
        {
            // 参数验证
            if (!IPAddress.TryParse(ip, out _) && !ip.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                OnError?.Invoke("无效IP");
                return false;
            }
            if (port < 1 || port > 65535)
            {
                OnError?.Invoke("无效端口");
                return false;
            }

            // 如果已连接则先断开
            if (IsConnected())
                Disconnect();

            // 保存目标地址
            _targetIp = ip;
            _targetPort = port;
            _isRunning = true;
            _enableAutoReconnect = true;

            // 启动统一的连接管理线程（首次连接和重连共用）
            StartConnectThread();
            return true;
        }

        public void Disconnect()
        {
            _isRunning = false;
            _enableAutoReconnect = false;

            // 终止接收线程和连接管理线程
            if (_recvThread != null && _recvThread.IsAlive)
                _recvThread.Join(1000);
            if (_connectThread != null && _connectThread.IsAlive)
                _connectThread.Join(1000);

            // 释放网络资源
            lock (_netLock)
            {
                _netStream?.Dispose();
                _tcpClient?.Close();
            }

            OnDisconnected?.Invoke();
            Console.WriteLine("已断开连接");
        }
        #endregion

        #region 连接管理线程（合并首次连接和重连）
        private void StartConnectThread()
        {
            // 避免重复创建线程
            if (_isConnecting || (_connectThread != null && _connectThread.IsAlive))
                return;

            _isConnecting = true;
            _connectThread = new Thread(ConnectAndReconnectLoop)
            {
                IsBackground = true,
                Name = "TcpConnectManager"
            };
            _connectThread.Start();
        }

        // 统一处理首次连接和重连逻辑
        private void ConnectAndReconnectLoop()
        {
            while (_isRunning && _enableAutoReconnect)
            {
                try
                {
                    // 如果已连接则退出循环（仅重连时需要）
                    if (IsConnected())
                    {
                        _isConnecting = false;
                        return;
                    }

                    // 执行连接操作
                    lock (_netLock)
                    {
                        _tcpClient?.Close();
                        _tcpClient = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                        Console.WriteLine($"尝试连接 {_targetIp}:{_targetPort}");

                        // 连接超时5秒
                        if (_tcpClient.ConnectAsync(_targetIp, _targetPort).Wait(5000))
                        {
                            _netStream = _tcpClient.GetStream();
                            StartRecvThread(); // 启动接收线程
                            OnConnected?.Invoke();
                            Console.WriteLine($"连接成功 {_targetIp}:{_targetPort}");
                            _isConnecting = false;
                            return; // 连接成功，退出循环
                        }
                        else
                        {
                            OnError?.Invoke("连接超时");
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"连接失败：{ex.Message}");
                }

                // 连接失败，延迟后重试（首次连接失败后自动进入重连逻辑）
                Thread.Sleep(RECONNECT_DELAY);
                Console.WriteLine($"重连中...({RECONNECT_DELAY / 1000}秒后重试)");
            }

            _isConnecting = false;
        }
        #endregion

        #region 核心：读取数据
        private void StartRecvThread()
        {
            if (_recvThread == null || !_recvThread.IsAlive)
            {
                _recvThread = new Thread(ReceiveProcessLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _recvThread.Start();
            }
        }

        public string getReceverData(bool ishex)
        {
            lock (_netLock)
            {
                if (_recvBufferLen == 0)
                    return "";

                string result;
                if (ishex)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < _recvBufferLen; i++)
                    {
                        sb.Append($"{_recvBuffer[i]:X2} ");
                    }
                    result = sb.ToString().TrimEnd();
                }
                else
                {
                    result = Encoding.UTF8.GetString(_recvBuffer, 0, _recvBufferLen);
                }
                return result;
            }
        }

        public void clearFifo()
        {
            lock (_netLock)
            {
                _recvBufferLen = 0;
            }
        }

        private void ReceiveProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (!IsConnected() || _netStream == null)
                    {
                        OnError?.Invoke("连接断开，触发重连");
                        StartConnectThread(); // 调用统一的连接管理线程进行重连
                        break;
                    }

                    int readLen;
                    lock (_netLock)
                    {
                        if (!_netStream.DataAvailable)
                        {
                            Thread.Sleep(5);
                            continue;
                        }

                        readLen = _netStream.Read(_recvBuffer, _recvBufferLen, _recvBuffer.Length - _recvBufferLen);
                        if (readLen > 0)
                        {
                            _recvBufferLen += readLen;
                        }
                        else if (readLen == 0)
                        {
                            OnError?.Invoke("服务器主动断开");
                            StartConnectThread(); // 重连
                            break;
                        }
                    }
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"接收处理异常：{ex.Message}");
                    StartConnectThread(); // 重连
                    break;
                }
            }
        }
        #endregion

        #region 发送方法
        public bool Send(string data) => Send(Encoding.UTF8.GetBytes(data));

        public bool Send(byte[] data)
        {
            if (!_isRunning || !IsConnected() || data == null || data.Length == 0)
                return false;

            try
            {
                lock (_netLock)
                {
                    _netStream.Write(data, 0, data.Length);
                    _netStream.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"发送失败：{ex.Message}");
                StartConnectThread(); // 重连
                return false;
            }
        }
        #endregion

        #region 连接状态检测
        public bool IsConnected()
        {
            lock (_netLock)
            {
                if (_tcpClient == null || !_tcpClient.Connected)
                    return false;

                try
                {
                    return !_tcpClient.Client.Poll(1, SelectMode.SelectRead) || _tcpClient.Client.Available > 0;
                }
                catch
                {
                    return false;
                }
            }
        }
        #endregion

        #region 资源释放
        public void Dispose()
        {
            Disconnect();
            GC.SuppressFinalize(this);
        }

        ~MYTcpClient() => Dispose();
        #endregion
    }
}