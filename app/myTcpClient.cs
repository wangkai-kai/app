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
        private Thread _reconnectThread;
        private bool _isRunning = false;
        private bool _enableAutoReconnect = true;
        private string _targetIp;
        private int _targetPort;

        // 锁与配置
        private readonly object _netLock = new object();
        private const int RECONNECT_DELAY = 2000;

        // 事件与委托（数据处理直接绑定到读取步骤）

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnError;

        #region 构造与初始化
        public MYTcpClient()
        {
            _tcpClient = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true, // 禁用Nagle算法（已设置，保持）
                ReceiveTimeout = 100,
                SendTimeout = 5000,
                ReceiveBufferSize = 8192 // 增大接收缓冲区（默认4096，可根据需求调整）
            };
            // 启用TCP保活并优化参数（保持连接活性）
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // 保活探测间隔（单位：毫秒）
        }
        #endregion

        #region 连接与断开
        public bool Connect(string ip, int port)
        {
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

            if (IsConnected()) Disconnect();

            _targetIp = ip;
            _targetPort = port;
            _isRunning = true;

            try
            {
                lock (_netLock)
                {
                    _tcpClient?.Close();
                    _tcpClient = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                    if (!_tcpClient.ConnectAsync(ip, port).Wait(5000))
                    {
                        OnError?.Invoke("连接超时");
                        StartReconnectThread();
                        return false;
                    }
                    _netStream = _tcpClient.GetStream();
                }

                StartRecvThread();
                OnConnected?.Invoke();
                Console.WriteLine($"已连接 {ip}:{port}");
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"连接失败：{ex.Message}");
                StartReconnectThread();
                return false;
            }
        }

        public void Disconnect()
        {
            _isRunning = false;
            _enableAutoReconnect = false;

            if (_recvThread != null && _recvThread.IsAlive) _recvThread.Join(1000);
            if (_reconnectThread != null && _reconnectThread.IsAlive) _reconnectThread.Join(1000);

            lock (_netLock)
            {
                _netStream?.Dispose();
                _tcpClient?.Close();
            }

            OnDisconnected?.Invoke();
            Console.WriteLine("已断开连接");
            _enableAutoReconnect = true;
        }
        #endregion

        #region 自动重连
        private void StartReconnectThread()
        {
            if (!_isRunning || !_enableAutoReconnect || (_reconnectThread != null && _reconnectThread.IsAlive))
                return;

            _reconnectThread = new Thread(ReconnectLoop) { IsBackground = true };
            _reconnectThread.Start();
        }

        private void ReconnectLoop()
        {
            while (_isRunning && _enableAutoReconnect)
            {
                try
                {
                    Thread.Sleep(RECONNECT_DELAY);
                    if (IsConnected()) continue;

                    lock (_netLock)
                    {
                        _tcpClient?.Close();
                        _tcpClient = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                        if (_tcpClient.ConnectAsync(_targetIp, _targetPort).Wait(5000))
                        {
                            _netStream = _tcpClient.GetStream();
                            StartRecvThread();
                            OnConnected?.Invoke();
                            Console.WriteLine($"重连成功 {_targetIp}:{_targetPort}");
                            return;
                        }
                    }
                }
                catch
                {
                    Console.WriteLine($"重连中...({RECONNECT_DELAY / 1000}秒后重试)");
                }
            }
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
                    Priority = ThreadPriority.AboveNormal // 提高优先级，确保及时调度
                };
                _recvThread.Start();
            }
        }
        public string getReceverData(bool ishex)
        {
            
            lock (_netLock) // 加锁确保线程安全
            {
                if (_recvBufferLen == 0)
                    return "";
                string result;
                if (ishex)
                {
                    // 十六进制转换
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < _recvBufferLen; i++)
                    {
                        sb.Append($"{_recvBuffer[i]:X2} ");
                    }
                    result = sb.ToString().TrimEnd();
                }
                else
                {
                    // UTF8字符串转换
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
        /// <summary>
        /// 接收线程：读取数据后直接完成字符串转换和处理
        /// </summary>
        private void ReceiveProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (!IsConnected() || _netStream == null)
                    {
                        OnError?.Invoke("连接断开，触发重连");
                        StartReconnectThread();
                        break;
                    }
                    int readLen;
                    lock (_netLock)
                    {
                        // 无数据时，用更短的休眠（1ms）减少检测间隔，同时降低CPU占用
                        if (!_netStream.DataAvailable)
                        {
                            Thread.Sleep(100); // 缩短等待间隔，减少延迟
                            continue;
                        }
                        // 读取数据（尽量填满缓冲区剩余空间）
                        readLen = _netStream.Read(_recvBuffer, _recvBufferLen, _recvBuffer.Length - _recvBufferLen);
                        if (readLen > 0)
                        {
                            _recvBufferLen += readLen;
                        }
                        else if (readLen == 0)
                        {
                            OnError?.Invoke("服务器主动断开");
                            StartReconnectThread();
                            break;
                        }
                    }
                }
                catch (TimeoutException) { continue; }
                catch (Exception ex)
                {
                    OnError?.Invoke($"接收处理异常：{ex.Message}");
                    StartReconnectThread();
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
                StartReconnectThread();
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
                catch { return false; }
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