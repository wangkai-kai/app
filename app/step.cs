using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace app
{
    public class AllStep
    {
        private List<TestStep> _globalTestConfig = new List<TestStep>();
      
        public void clear()                 // 清空步骤
        { 
           _globalTestConfig.Clear();
        }
        public List<TestStep> GetAllStep()  // 获取所有步骤
        {
            return _globalTestConfig;
          
        }
        public void AddStep(List<TestStep> newSteps)  // 添加步骤
        {
            _globalTestConfig.Clear();
            _globalTestConfig.AddRange(newSteps);
           
        }
       
    }
    public class TestStep
    {
        public string id { get; set; }       // 步骤ID
        public string type { get; set; }     // 步骤类型（send/delay/receive/clear）
        public string name { get; set; }     // 步骤名称
        public string content { get; set; }  // 发送内容（仅send类型）
        public bool isHex { get; set; }      // 是否16进制（仅send/receive类型）
        public int time { get; set; }        // 延时时间（仅delay类型）
        public Validation validation { get; set; }  // 验证条件（仅receive类型）
    }
    public class Validation
    {
        public string type { get; set; }     // 验证类型（exists/equals/contains）
        public string value { get; set; }    // 验证值

    }
    public class TestTask
    {
        // 线程安全的运行状态标识（volatile确保多线程可见性）
        private volatile bool _isRunning = true;
        // 取消令牌源，用于安全终止异步操作
        private CancellationTokenSource _cts = new CancellationTokenSource();

        #region 委托定义（与外部模块交互）
        /// <summary>
        /// 上报步骤状态到前端
        /// </summary>
        /// <param name="index">步骤索引</param>
        /// <returns>是否允许继续执行</returns>
        public Func<int,string, bool> SyncStep { get; set; } = (index,active) => true;

        /// <summary>
        /// 上报步骤状态到前端
        /// </summary>
        /// <param name="index">步骤索引</param>
        /// <returns>是否允许继续执行</returns>
        public Func<string, bool>SyncInfo { get; set; } = (message) => true;


        /// <summary>
        /// 重置所有步骤状态到前端
        /// </summary>
        /// <param name="index">步骤索引</param>
        /// <returns>是否允许继续执行</returns>
        public Func< bool> resetStep { get; set; } = () => true;


        /// <summary>
        /// 上报测试结果到前端
        /// </summary>
        /// <param name="result">测试结果（true=成功，false=失败）</param>
        /// <returns>是否上报成功</returns>
        public Func<bool, bool> SyncResult { get; set; } = (result) => true;

        /// <summary>
        /// 上报测试停止状态到前端
        /// </summary>
        /// <returns>是否上报成功</returns>
        public Func<bool> SyncStop { get; set; } = () => true;

        /// <summary>
        /// 发送数据函数（由外部实现具体通信逻辑）
        /// </summary>
        /// <param name="data">要发送的字节数组</param>
        /// <returns>发送是否成功</returns>
        public Func<byte[], bool> SendDataFunc { get; set; } = (data) => false;

        /// <summary>
        /// 清除接收缓存函数
        /// </summary>
        public Action ClearReceiveBufferFunc { get; set; } = () => { };

        /// <summary>
        /// 读取接收数据函数
        /// </summary>
        /// <param name="isHex">是否按16进制格式读取</param>
        /// <returns>接收到的字符串</returns>
        public Func<bool, string> ReadReceivedDataFunc { get; set; } = (isHex) => string.Empty;
        #endregion

        /// <summary>
        /// 异步延时函数（支持取消）
        /// </summary>
        /// <param name="delayMs">延时毫秒数</param>
        public async Task StepDelay(int delayMs)
        {
            if (delayMs <= 0) return;

            try
            {
                // 使用取消令牌，支持中途终止延时
                await Task.Delay(delayMs, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 延时被取消时正常退出（不视为错误）
            }
        }

        /// <summary>
        /// 停止测试任务（线程安全）
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts.Cancel();       // 取消所有异步操作
            _ = SyncStop?.Invoke();  // 通知前端测试已停止
        }
        /// <summary>
        /// 字符串转字节数组（支持普通字符串和16进制字符串）
        /// </summary>
        /// <param name="input">输入字符串</param>
        /// <param name="isHex">是否为16进制格式</param>
        /// <returns>转换后的字节数组</returns>
        public byte[] ConvertToBytes(string input, bool isHex = false)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Array.Empty<byte>();
            }

            return isHex ? HexStringToBytes(input) : Encoding.ASCII.GetBytes(input);
        }

        /// <summary>
        /// 16进制字符串转字节数组（内部辅助方法）
        /// </summary>
        private byte[] HexStringToBytes(string hexString)
        {
            // 移除所有空格
            string cleaned = hexString.Replace(" ", string.Empty);

            // 验证长度是否为偶数（1个字节对应2个16进制字符）
            if (cleaned.Length % 2 != 0)
            {
                throw new ArgumentException("16进制字符串长度必须为偶数（每个字节对应2个字符）", nameof(hexString));
            }

            // 验证是否包含无效的16进制字符
            if (!System.Text.RegularExpressions.Regex.IsMatch(cleaned, @"^[0-9A-Fa-f]+$"))
            {
                throw new ArgumentException("16进制字符串包含无效字符（仅允许0-9、A-F、a-f）", nameof(hexString));
            }

            // 转换为字节数组
            byte[] bytes = new byte[cleaned.Length / 2];
            for (int i = 0; i < cleaned.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(cleaned.Substring(i, 2), 16);
            }

            return bytes;
        }

        /// <summary>
        /// 验证接收数据是否符合预期规则
        /// </summary>
        /// <param name="receivedData">接收到的数据</param>
        /// <param name="validation">验证规则</param>
        /// <returns>验证是否通过</returns>
        private bool ValidateReceivedData(string receivedData, Validation validation)
        {
            if (validation == null) return true;

            switch (validation.type?.ToLower())
            {
                case "exists":
                    // 只要收到数据即视为通过
                    return !string.IsNullOrEmpty(receivedData);
                case "equals":
                    // 完全匹配
                    return receivedData == validation.value;
                case "contains":
                    // 包含子串
                    return receivedData.Contains(validation.value);
                default:
                    return false;
            }
        }

        /// <summary>
        /// 执行单次测试流程（异步）
        /// </summary>
        /// <param name="steps">测试步骤列表</param>
        /// <returns>单次测试是否成功</returns>
        public async Task<bool> RunOnceAsync(List<TestStep> steps)
        {
            bool suc = true;
            if (steps == null || steps.Count == 0)
            {
                return false;
            }
            resetStep();
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];

                // 上报当前步骤状态到前端，若前端返回false则终止测试
                bool stepSuccess = false;
                try
                {
                    switch (step.type?.ToLower())
                    {
                        case "send":
                            // 发送数据（使用step.content作为发送内容，修复原代码错误）
                            byte[] dataToSend = ConvertToBytes(step.content, step.isHex);
                            stepSuccess = SendDataFunc(dataToSend);
                            break;
                        case "receive":
                            // 读取接收数据并验证
                            string receivedData="";
                            for (int t = 0; t < 30; t++)
                            {
                                 receivedData = ReadReceivedDataFunc(step.isHex);
                                if (receivedData != "")
                                { 
                                    break;
                                }
                                else await StepDelay(10);
                            }
                            stepSuccess = ValidateReceivedData(receivedData, step.validation);
                            SyncInfo(receivedData);
                            break;

                        case "delay":
                            // 执行异步延时
                            await StepDelay(step.time);
                            stepSuccess = true;
                            break;

                        case "clear":
                            // 清除接收缓存
                            ClearReceiveBufferFunc();
                            stepSuccess = true;
                            break;

                        default:
                            // 未知步骤类型视为失败
                            stepSuccess = false;
                            break;
                    }
                    if (!SyncStep(i, stepSuccess? "pass" : "fail"))
                    {
                        
                    }
                    if (!stepSuccess)
                    {
                        suc = false;
                    }
                }
                catch (Exception ex)
                {
                    // 捕获步骤执行异常，标记为失败
                    Console.WriteLine($"步骤 {i + 1} 执行异常: {ex.Message}");
                    stepSuccess = false;
                    suc = false;
                }          
            }
                return suc;
        }

        /// <summary>
        /// 启动测试任务（异步非阻塞）
        /// </summary>
        /// <param name="once">是否为单次测试</param>
        /// <param name="intervalSeconds">循环测试间隔（秒）</param>
        /// <param name="steps">测试步骤列表</param>
        /// <returns>异步任务</returns>
        public async Task RunTaskAsync(bool once, int intervalSeconds, List<TestStep> steps)
        {
            // 重置运行状态和取消令牌
            _isRunning = true;
            _cts = new CancellationTokenSource();
            try
            {
                if (once)
                {
                    // 执行单次测试
                    bool result = await RunOnceAsync(steps);
                    _ = SyncResult(result); // 上报测试结果
                 
                }
                else
                {
                    // 执行循环测试
                    while (_isRunning && !_cts.Token.IsCancellationRequested)
                    {
                        bool result = await RunOnceAsync(steps);
                        _ = SyncResult(result); // 上报本次循环结果

                        // 若测试失败或已停止，退出循环
                        if (!_isRunning)
                        {
                            break;
                        }

                        // 循环间隔（支持取消）
                        await StepDelay(intervalSeconds * 1000);
                    }
                
                }
            }
            finally
            {
                // 确保最终状态为停止
                _isRunning = false;
                _ = SyncStop(); // 上报停止状态
            }
        }
    }

}
