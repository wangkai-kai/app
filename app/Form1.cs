using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;


namespace app
{
    public partial class Form1 : Form
    {
        private WebView2 _webView;
        private WebInterface _webInterface; 
        internal BackgroundWorker executionWorker;
        private bool isExecuting = false;
        private Timer refreshTimer;
        private AllStep allStep = new AllStep();
        private bool portTcp = false;
        private bool tcpOnline = false;
        private TestTask testTask = new TestTask();
        public AppCore AppCore { get; private set; }

        private MYTcpClient _tcpClient;
        private MYSerial _serial;

        public Form1()
        {
            InitializeComponent();
            InitializeExecutionWorker();
            _tcpClient = new MYTcpClient();
            _serial=new MYSerial();

            AppCore = new AppCore(SendMessageToWebView);
            _ = InitializeWebViewAsync();
            InitializeTimer();

        }

        private void InitializeTimer()
        {
            refreshTimer = new Timer
            {
                Interval = 200
            };
            refreshTimer.Tick += RefreshTimer_Tick;
             refreshTimer.Start();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            if (portTcp)
            {
                if (tcpOnline != _tcpClient.IsConnected())
                {
                    tcpOnline = _tcpClient.IsConnected();
                    _webInterface.SendMessage("tcpState", new { online = tcpOnline });
                }
            }
        }

        /// <summary>
        /// 初始化WebView并创建前端交互接口
        /// </summary>
        private async Task InitializeWebViewAsync()
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            // 初始化前端交互接口
            _webInterface = new WebInterface(this, _webView);
            _webInterface.CommandReceived += OnWebCommandReceived; // 订阅前端命令

            // 初始化WebView环境
            await _webView.EnsureCoreWebView2Async(null);

            // 加载HTML页面
            _webInterface.LoadHtmlPage();

            this.Controls.Add(_webView);
        }

        /// <summary>
        /// 处理前端命令（由WebInterface触发）
        /// </summary>
        private void OnWebCommandReceived(WebCommand command)
        {
            switch (command.Command)
            {
                case "Run":
                    HandleRunCommand(command);
                    break;
                case "clearCount":
                    HandleClearCount();
                    break;
                case "refreshComPorts":
                    HandleRefreshComPorts();
                    break;
                case "openComPort":
                    HandleOpenComPort(command);
                    break;
                case "closeComPort":
                    HandleCloseComPort();
                    break;
                case "ConfigLoaded":
                    HandleConfigLoaded(command);
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"未知命令: {command.Command}");
                    break;
            }
        }

        #region 命令处理方法
        private void HandleRunCommand(WebCommand command)
        {
            var once = command.Parameters["once"].ToString();
            var time = int.Parse(command.Parameters["time"].ToString());
            var run = bool.Parse(command.Parameters["run"].ToString());
            if (run)
            {
                if (once == "single")
                {
                    start_task(portTcp, true, 1);
                }
                else
                {
                    start_task(portTcp, false, time);
                }
                _webInterface.SendMessage("comRunSet", new { run = true });
            }
            else
            {
                testTask.Stop();
            }
        }
        private void HandleClearCount()
        {
            
        }

        private void HandleRefreshComPorts()
        {
            System.Diagnostics.Debug.WriteLine("处理刷新COM端口命令");
            var ports= AppCore.GetAvailableComPorts();
            _webInterface.SendMessage("comPortsUpdated", ports);
        }

        private void HandleOpenComPort(WebCommand command)
        {
            System.Diagnostics.Debug.WriteLine("处理打开通信接口命令");
            var portType = command.Parameters["portType"].ToString();

            if (portType == "serial")
            {
                var portName = command.Parameters["portName"].ToString();
                var baudRate = int.Parse(command.Parameters["baudRate"].ToString());
                var dataBits = int.Parse(command.Parameters["dataBits"].ToString());
                var parity = command.Parameters["parity"].ToString();
                var stop = command.Parameters["stops"].ToString();
                // 打开串口逻辑
                portTcp = false;
                _serial.Open(portName, baudRate, dataBits, parity, stop);


            }
            else if (portType == "tcp")
            {
                var tcpIp = command.Parameters["tcpIp"].ToString();
                var tcpPort = int.Parse(command.Parameters["tcpPort"].ToString());
                // 打开TCP服务逻辑
                portTcp = true;
                _tcpClient.Connect(tcpIp, tcpPort);
             
            }

            _webInterface.SendMessage("comPortStatus", new { isOpen = true });
        }

        private void HandleCloseComPort()
        {
            if (portTcp)
            {
                _tcpClient.Disconnect();
            }
            else _serial.Close();
            _webInterface.SendMessage("comPortStatus", new { isOpen = false });
        }
        private void HandleConfigLoaded(WebCommand command)
        {
            try
            {
             
                if (command.data == null)
                {
                    _webInterface.SendMessage("error", new { message = "ConfigLoaded 命令的 data 为空" });
                    return;
                }
                string dataJson = JsonConvert.SerializeObject(command.data);
                List<TestStep> newSteps = JsonConvert.DeserializeObject<List<TestStep>>(dataJson);

                if (newSteps == null || newSteps.Count == 0)
                {
                    _webInterface.SendMessage("error", new { message = "解析配置数据为空或格式错误" });
                    return;
                }
                // 3. 正常添加步骤
                allStep.AddStep(newSteps);
            }
            catch (Exception ex)
            {
                _webInterface.SendMessage("error", new { message = $"解析配置失败：{ex.Message}" });
                System.Diagnostics.Debug.WriteLine($"HandleConfigLoaded 异常：{ex.Message}\n{ex.StackTrace}");
            }
        }

        #endregion

        /// <summary>
        /// 供外部调用的发送消息方法（如AppCore）
        /// </summary>
        public void SendMessageToWebView(string command, object data)
        {
            _webInterface?.SendMessage(command, data);
        }

        private void InitializeExecutionWorker()
        {
            executionWorker = new BackgroundWorker();
            executionWorker.WorkerSupportsCancellation = true;
            executionWorker.DoWork += ExecutionWorker_DoWork;
            executionWorker.RunWorkerCompleted += ExecutionWorker_RunWorkerCompleted;
        }

        private void ExecutionWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            isExecuting = true;
            // 执行测试逻辑
        }

        private void ExecutionWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            isExecuting = false;
            // 执行完成处理
        }

        public void start_task(bool istcp, bool once, int intervalSeconds)
        {
            // 绑定测试任务与前端的交互委托
            testTask.SyncStep = (index, active) =>
            {
                _webInterface.SendMessage("stepUpdate", new { index = index, active = active});
                return true;
            };
            testTask.SyncStop = () =>
            {
                _webInterface.SendMessage("comRunSet", new { run = false });
                return true;
            };
            testTask.SyncResult = (result) =>
            {
                _webInterface.SendMessage("comResult", new { result = result});
                return true;
            };
            testTask.resetStep = () =>
            {
                _webInterface.SendMessage("resetStep", null);
                return true;
            };
            testTask.SyncInfo = (message) =>
            {
                _webInterface.SendMessage("hdmessage", new { info = message });
                return true;
            };
            // 绑定硬件通信委托
            testTask.SendDataFunc = (data) =>
            {
                try
                {
                    if (istcp)
                    {
                        // TCP发送逻辑
                        _tcpClient.Send(data);
                    }
                    else
                    {
                        // 串口发送逻辑
                        _serial.SendHex(data);
                    }
              
                    return true;
                }
                catch (Exception ex)
                {
                    _webInterface.SendMessage("error", new { message = $"发送失败: {ex.Message}" });
                    return false;
                }
            };
            testTask.ReadReceivedDataFunc = (ishex) =>
            {
                if (istcp)
                {
                    // TCP发送逻辑
                    return _tcpClient.getReceverData(ishex);

                }
                else
                {
                    // 串口发送逻辑
                    return _serial.getReceverData(ishex);
                }
            };
            testTask.ClearReceiveBufferFunc = () =>
            {
                if (istcp)
                {
                    // TCP发送逻辑
                     _tcpClient.clearFifo();
                }
                else
                {
                    // 串口发送逻辑
                    _serial.ClearReceiveBuffer();
                }
            };
            // 启动测试任务
            _ = testTask.RunTaskAsync(once, intervalSeconds, allStep.GetAllStep());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            _webView?.Dispose();
            executionWorker?.Dispose();
            refreshTimer?.Dispose();

            base.Dispose(disposing);
        }
    }



}