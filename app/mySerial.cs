using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;

namespace app
{
    internal class MYSerial : IDisposable
    {
        private SerialPort _serialPort;
        private bool _isDisposed = false;
        private byte[] recvBUF = new byte[1024];
        private int recvNUM = 0;
        private readonly object _serialLock = new object(); // 锁对象

        public bool IsOpen => _serialPort?.IsOpen ?? false;

        public MYSerial()
        {
            _serialPort = new SerialPort();
            _serialPort.DataReceived += recvHexCallback;
        }

        /// <summary>
        /// 打开串口（使用传统switch语句，兼容低版本C#）
        /// </summary>
        public bool Open(
            string portName,
            int baudRate,
            int dataBits = 8,
            string parity = "none",
            string stopBits = "1"
        )
        {
            lock (_serialLock)
            {
                try
                {
                    if (IsOpen)
                    {
                        Close();
                    }

                    _serialPort.PortName = portName;
                    _serialPort.BaudRate = baudRate;
                    _serialPort.DataBits = dataBits;

                    // 校验位设置（传统switch语句，兼容所有C#版本）
                    if (string.IsNullOrEmpty(parity))
                    {
                        // 为空时默认无校验
                        _serialPort.Parity = Parity.None;
                    }
                    else
                    {
                        switch (parity.ToLower())
                        {
                            case "odd":
                                _serialPort.Parity = Parity.Odd;
                                break;
                            case "even":
                                _serialPort.Parity = Parity.Even;
                                break;
                            default:
                                // 无效值默认无校验，并输出日志
                                Console.WriteLine($"无效校验位：{parity}，默认使用「无校验」");
                                _serialPort.Parity = Parity.None;
                                break;
                        }
                    }

                    // 停止位设置（传统switch语句）
                    if (string.IsNullOrEmpty(stopBits))
                    {
                        // 为空时默认1位停止位
                        _serialPort.StopBits = StopBits.One;
                    }
                    else
                    {
                        switch (stopBits)
                        {
                            case "1.5":
                                _serialPort.StopBits = StopBits.OnePointFive;
                                break;
                            case "2":
                                _serialPort.StopBits = StopBits.Two;
                                break;
                            case "1":
                                _serialPort.StopBits = StopBits.One;
                                break;
                            default:
                                // 无效值默认1位停止位，并输出日志
                                Console.WriteLine($"无效停止位：{stopBits}，默认使用「1位停止位」");
                                _serialPort.StopBits = StopBits.One;
                                break;
                        }
                    }

                    _serialPort.Handshake = Handshake.None;
                    _serialPort.ReadTimeout = 500;
                    _serialPort.WriteTimeout = 500;

                    _serialPort.Open();
                    ClearReceiveBuffer(); // 打开后清空缓冲区
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"串口打开失败：{ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 关闭串口
        /// </summary>
        public void Close()
        {
            lock (_serialLock)
            {
                if (IsOpen)
                {
                    _serialPort.DataReceived -= recvHexCallback; // 解绑事件
                    _serialPort.Close();
                }
                recvNUM = 0; // 重置缓冲区计数
            }
        }

        /// <summary>
        /// 发送字节数组
        /// </summary>
        public bool SendHex(byte[] buf)
        {
            if (buf == null || buf.Length == 0) return false;

            lock (_serialLock)
            {
                try
                {
                    if (!IsOpen) return false;

                    _serialPort.Write(buf, 0, buf.Length);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"发送失败：{ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 接收数据回调（加锁确保线程安全）
        /// </summary>
        private void recvHexCallback(object sender, SerialDataReceivedEventArgs e)
        {
            lock (_serialLock)
            {
                try
                {
                    if (!IsOpen) return;

                    int availableSpace = recvBUF.Length - recvNUM;
                    if (availableSpace <= 0)
                    {
                        Console.WriteLine("接收缓冲区已满，已清空");
                        _serialPort.DiscardInBuffer();
                        recvNUM = 0;
                        return;
                    }

                    int readLen = _serialPort.Read(recvBUF, recvNUM, availableSpace);
                    if (readLen > 0)
                    {
                        recvNUM += readLen;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"接收异常：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 清空接收缓冲区
        /// </summary>
        public void ClearReceiveBuffer()
        {
            lock (_serialLock)
            {
                if (IsOpen)
                {
                    _serialPort.DiscardInBuffer();
                }
                recvNUM = 0;
            }
        }

        /// <summary>
        /// 获取接收数据
        /// </summary>
        public string getReceverData(bool ishex)
        {
            lock (_serialLock)
            {
                if (recvNUM == 0) return "";

                string result;
                if (ishex)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < recvNUM; i++)
                    {
                        sb.Append($"{recvBUF[i]:X2} ");
                    }
                    result = sb.ToString().TrimEnd();
                }
                else
                {
                    result = Encoding.UTF8.GetString(recvBUF, 0, recvNUM);
                }

                return result;
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            if (disposing)
            {
                Close();
                _serialPort?.Dispose();
            }

            _isDisposed = true;
        }

        ~MYSerial()
        {
            Dispose(false);
        }
    }
}