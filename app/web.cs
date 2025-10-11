using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace app
{
    [ComVisible(true)]
    public class AppCore
    {
        private Form1 _mainForm;  // 持有 Form1 引用，用于调用前端通信方法

        // 移除对 Form1 的直接引用，改用回调委托传递消息
        private readonly Action<string, object> _sendMessage;

        // 构造函数：仅接收消息发送回调，不持有 Form1 实例
        public AppCore(Action<string, object> sendMessageCallback)
        {
            _sendMessage = sendMessageCallback ?? throw new ArgumentNullException(nameof(sendMessageCallback));
        }
        // 示例1：获取系统中所有可用的 COM 端口（供前端调用或 Form1 使用）
        public string[] GetAvailableComPorts()
        {
            try
            {
                // 1. 获取系统所有 COM 端口
                string[] ports = SerialPort.GetPortNames();

                // 2. 排序端口（确保 COM1、COM2、COM3... 顺序，避免乱序）
                Array.Sort(ports, (portA, portB) =>
                {
                    // 提取端口号（如从 "COM3" 中提取 3）
                    if (!int.TryParse(portA.Replace("COM", ""), out int numA)) return 1;
                    if (!int.TryParse(portB.Replace("COM", ""), out int numB)) return -1;
                    return numA.CompareTo(numB);
                });

                System.Diagnostics.Debug.WriteLine($"找到 {ports.Length} 个 COM 端口：{string.Join(", ", ports)}");
                return ports;
            }
            catch (Exception ex)
            {
                // 出错时通知前端
                _mainForm.SendMessageToWebView("error", new { message = $"获取 COM 端口失败：{ex.Message}" });
                System.Diagnostics.Debug.WriteLine($"获取 COM 端口异常：{ex.Message}");
                return Array.Empty<string>();
            }
        }

        // 示例2：可添加其他与前端/硬件交互的方法（如设备状态检查、配置保存等）
        public bool CheckDeviceConnection(string portName)
        {
            try
            {
                using (var tempPort = new SerialPort(portName))
                {
                    tempPort.Open();
                    tempPort.Close();
                    return true;  // 端口能打开，视为连接正常
                }
            }
            catch
            {
                return false;  // 端口无法打开，连接失败
            }
        }
    }

    public class WebInterface
    {
        private readonly WebView2 _webView;
        private readonly Form1 _form;
        private bool _isInitialized;

        // 事件：供主窗体订阅前端命令
        public event Action<WebCommand> CommandReceived;

        public WebInterface(Form1 form, WebView2 webView)
        {
            _form = form;
            _webView = webView;
            _isInitialized = false;

            // 初始化WebView事件
            InitWebViewEvents();
        }

        /// <summary>
        /// 初始化WebView相关事件
        /// </summary>
        private void InitWebViewEvents()
        {
            // WebView初始化完成事件
            _webView.CoreWebView2InitializationCompleted += (s, e) =>
            {
                if (e.IsSuccess)
                {
                    _isInitialized = true;
                    ConfigureWebViewSettings();
                    // 接收前端消息事件
                    _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                    _form.SendMessageToWebView("info", new { message = "WebView初始化完成，已准备就绪" });
                }
                else
                {
                    MessageBox.Show($"WebView初始化失败: {e.InitializationException.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

           
        }

        /// <summary>
        /// 配置WebView设置
        /// </summary>
        private void ConfigureWebViewSettings()
        {
            _webView.CoreWebView2.Settings.IsScriptEnabled = true;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

            // 确保 _form.AppCore 已初始化且不为 null
            if (_form.AppCore != null)
            {
                try
                {
                    _webView.CoreWebView2.AddHostObjectToScript("appCore", _form.AppCore);
                }
                catch (Exception ex)
                {
                    // 捕获并输出详细错误（关键：查看具体哪个参数无效）
                    System.Diagnostics.Debug.WriteLine($"注册 appCore 失败：{ex.Message}\n{ex.StackTrace}");
                    _form.SendMessageToWebView("error", $"注册对象失败：{ex.Message}");
                }
            }
            else
            {
                _form.SendMessageToWebView("error", "AppCore 未初始化，无法注册");
            }
        }

        /// <summary>
        /// 加载前端HTML页面
        /// </summary>
        public void LoadHtmlPage()
        {
            string htmlPath = Path.Combine(Application.StartupPath, "htmlPages", "home.html");
            System.Diagnostics.Debug.WriteLine($"尝试加载HTML路径：{htmlPath}");
            if (File.Exists(htmlPath))
            {
                _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            else
            {
                MessageBox.Show($"找不到HTML文件: {htmlPath}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _webView.CoreWebView2.NavigateToString("<h1>HTML文件未找到</h1>");
            }
        }

        /// <summary>
        /// 处理来自前端的消息
        /// </summary>
        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(message)) return;

                System.Diagnostics.Debug.WriteLine($"收到前端消息: {message}");
                var command = JsonConvert.DeserializeObject<WebCommand>(message);

                // 触发命令事件，由主窗体处理
                CommandReceived?.Invoke(command);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"处理前端消息出错: {ex.Message}");
                SendMessage("error", new { message = $"处理命令时出错: {ex.Message}" });
            }
        }

        /// <summary>
        /// 发送消息到前端
        /// </summary>
        /// <param name="command">命令标识</param>
        /// <param name="data">携带数据</param>
        public void SendMessage(string command, object data)
        {
            try
            {
                if (!_isInitialized || _webView?.CoreWebView2 == null || _form.IsDisposed)
                {
                    System.Diagnostics.Debug.WriteLine("WebView尚未初始化，无法发送消息");
                    return;
                }

                string message = JsonConvert.SerializeObject(new
                {
                    command = command,
                    data = data
                });

                //System.Diagnostics.Debug.WriteLine($"发送到前端消息: {message}");

                // 确保在UI线程执行
                if (_form.InvokeRequired)
                {
                    _form.Invoke((MethodInvoker)(() => _webView.CoreWebView2.PostWebMessageAsString(message)));
                }
                else
                {
                    _webView.CoreWebView2.PostWebMessageAsString(message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"发送消息到前端出错: {ex.Message}");
            }
        }
    }
    public class WebCommand
    {
        public string Command { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public dynamic data { get; set; }   // 对应前端的 "data"（直接用 dynamic 接收数组，无需 Dictionary）
    }


}