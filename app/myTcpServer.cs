using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace app
{
    internal class myTcpServer
    {
        private TcpListener _tcpListener;
        private bool _isRunning;
        private readonly object _clientLock = new object();
        private List<TcpClient> _connectedClients = new List<TcpClient>();
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isDisposed = false;


        /// <summary>
        /// 服务是否正在运行
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// 接收到数据事件
        /// </summary>
        public event Action<IPEndPoint, byte[]> DataReceived;

        /// <summary>
        /// 客户端连接事件
        /// </summary>
        public event Action<IPEndPoint> ClientConnected;

        /// <summary>
        /// 客户端断开事件
        /// </summary>
        public event Action<IPEndPoint> ClientDisconnected;

        /// <summary>
        /// 通信错误事件
        /// </summary>
        public event Action<string> ErrorOccurred;

        public bool Start(string ipAddress, int port)
        {
            try
            {
                if (IsRunning)
                {
                    Stop();
                }
                IPAddress address = IPAddress.Parse(ipAddress);
                _tcpListener = new TcpListener(address, port);
                _cancellationTokenSource = new CancellationTokenSource();

                _tcpListener.Start();
                _isRunning = true;

                // 开始接受客户端连接
                Task.Run(() => AcceptClientsAsync(_cancellationTokenSource.Token));

                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"启动TCP服务失败: {ex.Message}");
                return false;
            }
        }
        /// <summary>
        /// 停止TCP服务
        /// </summary>
        public void Stop()
        {
            if (!IsRunning) return;
            _isRunning = false;
            _cancellationTokenSource?.Cancel();
            // 断开所有客户端连接
            lock (_clientLock)
            {
                foreach (var client in _connectedClients)
                {
                    try
                    {
                        client.Close();
                    }
                    catch { }
                }
                _connectedClients.Clear();
            }
            _tcpListener?.Stop();
        }

        /// <summary>
        /// 处理客户端断开连接
        /// </summary>
        private void HandleClientDisconnect(TcpClient client)
        {
            IPEndPoint endPoint = null;

            try
            {
                endPoint = (IPEndPoint)client.Client.RemoteEndPoint;
            }
            catch { }

            lock (_clientLock)
            {
                _connectedClients.Remove(client);
            }

            try
            {
                client.Close();
            }
            catch { }

            // 触发客户端断开事件
            if (endPoint != null)
            {
                ClientDisconnected?.Invoke(endPoint);
            }
        }
        private bool SendDataToClient(TcpClient client, byte[] data)
        {
            try
            {
                if (!client.Connected)
                {
                    HandleClientDisconnect(client);
                    return false;
                }

                client.GetStream().Write(data, 0, data.Length);
                return true;
            }
            catch (Exception ex)
            {
                
                HandleClientDisconnect(client);
                return false;
            }
        }
        public bool SendHexToAll(byte[] sbuf)
        {
            try
            {
                if (!IsRunning)
                {
                   
                    return false;
                }

              
                // 发送数据到所有客户端
                lock (_clientLock)
                {
                    foreach (var client in _connectedClients.ToArray())
                    {
                        SendDataToClient(client, sbuf);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                
                return false;
            }

        }

        /// <summary>
        /// 处理客户端通信
        /// </summary>
        private async Task HandleClientCommunicationsAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var endPoint = (IPEndPoint)client.Client.RemoteEndPoint;
            NetworkStream stream = null;
            try
            {
                stream = client.GetStream();
                byte[] buffer = new byte[1024];

                while (IsRunning && !cancellationToken.IsCancellationRequested && client.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead > 0)
                    {
                        // 复制接收到的数据
                        byte[] receivedData = new byte[bytesRead];
                        Array.Copy(buffer, receivedData, bytesRead);
                        // 触发数据接收事件
                        DataReceived?.Invoke(endPoint, receivedData);
                    }
                    else if (bytesRead == 0)
                    {
                        // 客户端断开连接
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 预期的取消操作，无需处理
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"与客户端 {endPoint} 通信错误: {ex.Message}");
            }
            finally
            {
                HandleClientDisconnect(client);
                stream?.Dispose();
            }
        }
        /// <summary>
        /// 异步接受客户端连接
        /// </summary>
        private async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (IsRunning && !cancellationToken.IsCancellationRequested)
                {
                    var client = await _tcpListener.AcceptTcpClientAsync();

                    lock (_clientLock)
                    {
                        _connectedClients.Add(client);
                    }

                    // 触发客户端连接事件
                    var endPoint = (IPEndPoint)client.Client.RemoteEndPoint;
                    ClientConnected?.Invoke(endPoint);

                    // 开始监听该客户端的数据
                    _ = HandleClientCommunicationsAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 预期的取消操作，无需处理
            }
            catch (Exception ex)
            {
                if (IsRunning) // 只有服务仍在运行时才报告错误
                {
                    ErrorOccurred?.Invoke($"接受客户端连接失败: {ex.Message}");
                }
            }
        }
        /// <summary>
        /// 获取所有连接的客户端
        /// </summary>
        /// <returns>客户端端点列表</returns>
        public List<IPEndPoint> GetConnectedClients()
        {
            var clients = new List<IPEndPoint>();

            lock (_clientLock)
            {
                foreach (var client in _connectedClients)
                {
                    try
                    {
                        clients.Add((IPEndPoint)client.Client.RemoteEndPoint);
                    }
                    catch { }
                }
            }

            return clients;
        }


        /// <summary>
        /// 清空所有客户端的接收缓冲区
        /// </summary>
        public void ClearAllReceiveBuffers()
        {
            try
            {
                if (!IsRunning)
                {
                    ErrorOccurred?.Invoke("TCP服务未运行，无法清空缓冲区");
                    return;
                }

                lock (_clientLock)
                {
                    foreach (var client in _connectedClients.ToArray())
                    {
                        try
                        {
                            if (client.Available > 0)
                            {
                                // 读取并丢弃数据
                                byte[] buffer = new byte[client.Available];
                                client.GetStream().Read(buffer, 0, buffer.Length);
                            }
                        }
                        catch (Exception ex)
                        {
                            ErrorOccurred?.Invoke($"清空客户端缓冲区失败: {ex.Message}");
                            HandleClientDisconnect(client);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"清空缓冲区失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 读取所有客户端发送的数据
        /// </summary>
        /// <returns>包含客户端端点和对应数据的字典</returns>
        public Dictionary<IPEndPoint, byte[]> ReadAllData()
        {
            var result = new Dictionary<IPEndPoint, byte[]>();

            try
            {
                if (!IsRunning)
                {
                    ErrorOccurred?.Invoke("TCP服务未运行，无法读取数据");
                    return result;
                }

                lock (_clientLock)
                {
                    foreach (var client in _connectedClients.ToArray())
                    {
                        try
                        {
                            if (client.Available > 0)
                            {
                                byte[] buffer = new byte[client.Available];
                                client.GetStream().Read(buffer, 0, buffer.Length);
                                var endPoint = (IPEndPoint)client.Client.RemoteEndPoint;
                                result[endPoint] = buffer;
                            }
                        }
                        catch (Exception ex)
                        {
                            ErrorOccurred?.Invoke($"读取客户端数据失败: {ex.Message}");
                            HandleClientDisconnect(client);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"读取数据失败: {ex.Message}");
            }

            return result;
        }
    }

}
