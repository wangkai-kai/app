using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace app
{
    internal class mySerial
    {
        private SerialPort _serialPort;
        private bool _isDisposed = false;
        private byte[] recvBUF = new byte[1024];
        private int recvNUM = 0;
        public bool IsOpen => _serialPort?.IsOpen ?? false;
        public mySerial()
        {
            _serialPort = new SerialPort();
            _serialPort.DataReceived += recvHexCallback;
        }
        /// <summary>
        /// 打开串口
        /// </summary>
        /// <param name="portName">端口名称</param>
        /// <param name="baudRate">波特率</param>
        /// <param name="dataBits">数据位</param>
        /// <param name="parity">校验位</param>
        /// <param name="stopBits">停止位</param>
        /// <returns>是否成功打开</returns>
        public bool Open(string portName, int baudRate, int dataBits = 8, Parity parity = Parity.None, StopBits stopBits = StopBits.One)
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
                _serialPort.Parity = parity;
                _serialPort.StopBits = stopBits;
                _serialPort.Handshake = Handshake.None;
                _serialPort.ReadTimeout = 500;
                _serialPort.WriteTimeout = 500;

                _serialPort.Open();
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }
        /// <summary>
        /// 关闭串口
        /// </summary>
        public void Close()
        {
            if (IsOpen)
            {
                _serialPort.Close();
            }
           
        }

        /// <summary>
        /// 发送十六进制数据
        /// </summary>
        /// <param name="hexString">十六进制字符串</param>
        /// <returns>是否发送成功</returns>
        public bool SendHex(byte [] buf)
        {
            try
            {
                if (!IsOpen)
                {
                    return false;
                }
                // 发送数据
                _serialPort.Write(buf, 0, buf.Length);
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        private void recvHexCallback(object sender, SerialDataReceivedEventArgs e)
        {
            recvNUM+= _serialPort.Read(recvBUF, recvNUM, recvBUF.Length - recvNUM);
        }
        public void ClearReceiveBuffer()
        {

                if (IsOpen)
                {
                    _serialPort.DiscardInBuffer();
                    recvNUM = 0;
                }
            
           
        }
        public byte[] RecvHex()
        {
            byte[] buffer = new byte[recvNUM];
            Array.Copy(recvBUF, buffer, buffer.Length);
            return buffer;
        }
    }
}
