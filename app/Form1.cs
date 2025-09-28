using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace app
{
    public partial class Form1 : Form
    {
        private WebView2 webView;
        internal SerialPort serialPort;
        internal BackgroundWorker executionWorker;
        private bool isExecuting = false;
        private bool isWebViewInitialized = false;
        private Timer refreshTimer;
        public AppCore AppCore { get; private set; }

        public Form1()
        {
            InitializeComponent();
            // 初始化顺序调整：先初始化组件，再初始化WebView
            InitializeComponent();
            AppCore = new AppCore(this);
            serialPort = new SerialPort();
            InitializeExecutionWorker();
            // 使用异步方法初始化WebView
            _ = InitializeWebViewAsync();
            InitializeTimer();
        }
        private void InitializeTimer()
        {
            // 初始化定时器
            refreshTimer = new Timer();
            // 设置刷新间隔（毫秒），这里设置为1秒刷新一次
            refreshTimer.Interval = 1000;
            // 绑定定时事件
            refreshTimer.Tick += RefreshTimer_Tick;
            // 启动定时器
           // refreshTimer.Start();
        }
        private void RefreshTimer_Tick(object sender, EventArgs e)
        {

        }

        // 异步初始化软件界面
        private async Task InitializeWebViewAsync()
        {
            webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            // 添加WebView初始化完成事件
            webView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;

            // 初始化WebView环境
            await webView.EnsureCoreWebView2Async(null);

            // 配置WebView
            webView.CoreWebView2.Settings.IsScriptEnabled = true;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

            // 注册供JavaScript调用的对象
            webView.CoreWebView2.AddHostObjectToScript("appCore", AppCore);

            // 注册脚本消息接收事件（用于前端调用）
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            // 加载HTML文件
            string htmlPath1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "htmlPages/step.html");
            if (File.Exists(htmlPath1))
            {
                webView.CoreWebView2.Navigate(new Uri(htmlPath1).AbsoluteUri);
            }
            else
            {
                MessageBox.Show($"找不到HTML文件: {htmlPath1}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                webView.CoreWebView2.NavigateToString("<h1>HTML文件未找到</h1>");
            }

            this.Controls.Add(webView);
            isWebViewInitialized = true;
        }

        //WebView初始化完成事件处理
        private void WebView_CoreWebView2InitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                isWebViewInitialized = true;
                // 初始化完成后立即刷新一次端口列表
                var ports = AppCore.GetAvailableComPorts();
                SendMessageToWebView("comPortsUpdated", ports);
                SendMessageToWebView("info", new { message = "WebView初始化完成，已准备就绪" });
            }
            else
            {
                MessageBox.Show($"WebView初始化失败: {e.InitializationException.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // 处理来自前端的消息，添加详细日志
        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = e.TryGetWebMessageAsString();
                if (!string.IsNullOrEmpty(message))
                {
                    // 调试：输出接收到的消息
                    System.Diagnostics.Debug.WriteLine($"收到前端消息: {message}");

                    // 解析前端消息
                    var command = JsonConvert.DeserializeObject<WebCommand>(message);

                    switch (command.Command)
                    {
                        case "refreshComPorts":
                            System.Diagnostics.Debug.WriteLine("处理刷新COM端口命令");
                            // 刷新COM端口并返回结果
                            var ports = AppCore.GetAvailableComPorts();
                            SendMessageToWebView("comPortsUpdated", ports);
                            break;

                        case "openComPort":
                            System.Diagnostics.Debug.WriteLine("处理打开COM端口命令");
                            // 打开COM端口
                            var portName = command.Parameters["portName"].ToString();
                            var baudRate = int.Parse(command.Parameters["baudRate"].ToString());
                            var result = AppCore.OpenComPort(portName, baudRate);
                            SendMessageToWebView("comPortStatus", new { isOpen = result, portName = portName });
                            break;

                        case "closeComPort":
                            System.Diagnostics.Debug.WriteLine("处理关闭COM端口命令");
                            // 关闭COM端口
                            AppCore.CloseComPort();
                            SendMessageToWebView("comPortStatus", new { isOpen = false });
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"处理前端消息出错: {ex.Message}");
                SendMessageToWebView("error", new { message = $"处理命令时出错: {ex.Message}" });
            }
        }

        // 发送消息到前端，增强错误处理
        public void SendMessageToWebView(string command, object data)
        {
            try
            {
                if (!isWebViewInitialized || webView?.CoreWebView2 == null || this.IsDisposed)
                {
                    System.Diagnostics.Debug.WriteLine("WebView尚未初始化，无法发送消息");
                    return;
                }

                var message = JsonConvert.SerializeObject(new
                {
                    command = command,
                    data = data
                });

                // 调试：输出发送的消息
                System.Diagnostics.Debug.WriteLine($"发送到前端消息: {message}");

                // 确保在UI线程执行
                if (this.InvokeRequired)
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        webView.CoreWebView2.PostWebMessageAsString(message);
                    });
                }
                else
                {
                    webView.CoreWebView2.PostWebMessageAsString(message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"发送消息到前端出错: {ex.Message}");
            }
        }

        // 初始化执行工作线程
        private void InitializeExecutionWorker()
        {
            executionWorker = new BackgroundWorker();
            executionWorker.WorkerSupportsCancellation = true;
            executionWorker.DoWork += ExecutionWorker_DoWork;
            executionWorker.RunWorkerCompleted += ExecutionWorker_RunWorkerCompleted;
        }

        private void ExecutionWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            // 执行测试逻辑
            isExecuting = true;
            // 这里添加实际执行代码
        }

        private void ExecutionWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            isExecuting = false;
            // 执行完成处理
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }

            // 清理资源
            if (serialPort != null && serialPort.IsOpen)
                serialPort.Close();

            serialPort?.Dispose();
            webView?.Dispose();
            executionWorker?.Dispose();

            base.Dispose(disposing);
        }
    }

    // 应用核心功能类 - 供前端调用
    [System.Runtime.InteropServices.ComVisible(true)]
    public class AppCore
    {
        private Form1 _form;

        public AppCore(Form1 form)
        {
            _form = form;
        }

        // 获取可用COM端口列表，添加调试信息
        public string[] GetAvailableComPorts()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("开始扫描COM端口");

                // 获取系统中所有可用的COM端口
                string[] ports = SerialPort.GetPortNames();

                // 调试：输出找到的端口
                System.Diagnostics.Debug.WriteLine($"找到 {ports.Length} 个COM端口: {string.Join(", ", ports)}");

                // 排序端口号（确保COM1, COM2, COM3...的顺序）
                Array.Sort(ports, (a, b) =>
                {
                    if (!int.TryParse(a.Replace("COM", ""), out int numA))
                        return 1;
                    if (!int.TryParse(b.Replace("COM", ""), out int numB))
                        return -1;
                    return numA.CompareTo(numB);
                });

                return ports;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取COM端口失败: {ex.Message}");
                _form.SendMessageToWebView("error", new { message = $"获取COM端口失败: {ex.Message}" });
                return new string[0];
            }
        }

        // 其他方法保持不变...
        public bool OpenComPort(string portName, int baudRate)
        {
            // 保持原有实现
            try
            {
                // 如果端口已打开，先关闭
                if (_form.serialPort.IsOpen)
                    _form.serialPort.Close();

                // 配置串口参数
                _form.serialPort.PortName = portName;
                _form.serialPort.BaudRate = baudRate;
                _form.serialPort.Parity = Parity.None;
                _form.serialPort.DataBits = 8;
                _form.serialPort.StopBits = StopBits.One;
                _form.serialPort.Handshake = Handshake.None;
                _form.serialPort.ReadTimeout = 500;
                _form.serialPort.WriteTimeout = 500;

                // 打开串口
                _form.serialPort.Open();

                // 注册数据接收事件
                _form.serialPort.DataReceived += SerialPort_DataReceived;

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"打开COM端口失败: {ex.Message}");
                _form.SendMessageToWebView("error", new { message = $"打开COM端口失败: {ex.Message}" });
                return false;
            }
        }

        public void CloseComPort()
        {
            // 保持原有实现
            try
            {
                if (_form.serialPort.IsOpen)
                {
                    // 移除事件订阅
                    _form.serialPort.DataReceived -= SerialPort_DataReceived;
                    _form.serialPort.Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"关闭COM端口失败: {ex.Message}");
                _form.SendMessageToWebView("error", new { message = $"关闭COM端口失败: {ex.Message}" });
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            // 保持原有实现
            try
            {
                if (_form.serialPort.IsOpen)
                {
                    string data = _form.serialPort.ReadExisting();
                    // 将接收到的数据发送到前端
                    _form.SendMessageToWebView("serialDataReceived", data);
                }
            }
            catch (Exception ex)
            {
                _form.SendMessageToWebView("error", new { message = $"串口接收错误: {ex.Message}" });
            }
        }

        public bool SendSerialData(string data)
        {
            // 保持原有实现
            try
            {
                if (_form.serialPort.IsOpen)
                {
                    _form.serialPort.Write(data);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _form.SendMessageToWebView("error", new { message = $"串口发送错误: {ex.Message}" });
                return false;
            }
        }
    }

    // 前端命令模型
    public class WebCommand
    {
        public string Command { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }
}
