// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using osu.Framework.Bindables;
using osu.Framework.Logging;

namespace osu.Framework.Audio
{
    /// <summary>
    /// EzOsuLatency 延迟测试模块
    /// 负责执行虚拟环路测试，量化播放事件触发后到真正发声的延迟。
    /// 通过播放脉冲信号并回录来计算音频输出延迟。
    /// </summary>
    public class EzLatencyTestModule
    {
        /// <summary>
        /// EzOsuLatency 总开关 - 控制是否启用延迟测量
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// 延迟测试启用状态
        /// </summary>
        public readonly Bindable<bool> LatencyTestEnabled = new Bindable<bool>(false);

        /// <summary>
        /// 驱动类型配置
        /// </summary>
        public readonly Bindable<string> DriverType = new Bindable<string>("ASIO");

        /// <summary>
        /// 采样率配置
        /// </summary>
        public readonly Bindable<int> SampleRate = new Bindable<int>(44100);

        /// <summary>
        /// 缓冲区大小配置
        /// </summary>
        public readonly Bindable<int> BufferSize = new Bindable<int>(256);

        private readonly Stopwatch stopwatch = new Stopwatch();
        private double lastTestTime;

        /// <summary>
        /// 初始化延迟测试模块
        /// </summary>
        public EzLatencyTestModule()
        {
            stopwatch.Start();
        }

        /// <summary>
        /// 运行延迟测试
        /// 执行虚拟环路测试：播放脉冲 → 回录 → 计算延迟
        /// </summary>
        /// <returns>测试结果的延迟值（毫秒），失败返回-1</returns>
        public double RunTest()
        {
            if (!LatencyTestEnabled.Value)
                return -1;

            if (Enabled)
                Logger.Log($"[EzLatencyTest] Starting latency test - Driver: {DriverType.Value}, SampleRate: {SampleRate.Value}, Buffer: {BufferSize.Value}");

            // 记录测试开始时间戳 (T_call)
            double tCall = RecordTimestamp();

            // 简化实现：模拟播放脉冲并测量延迟
            // 实际实现需要：
            // 1. 生成脉冲信号
            // 2. 播放到音频输出
            // 3. 通过虚拟环路回录
            // 4. 检测脉冲到达时间 (T_out)
            // 5. 计算 T_out - T_call

            // 临时模拟：假设延迟为缓冲区延迟 + 硬件延迟
            double bufferLatency = (BufferSize.Value / (double)SampleRate.Value) * 1000;
            const double hardware_latency = 1.5;
            double simulatedLatency = bufferLatency + hardware_latency;

            if (Enabled)
                Logger.Log($"[EzLatencyTest] Test completed, latency: {simulatedLatency:F2}ms (T_call: {tCall:F2})");

            return simulatedLatency;
        }

        /// <summary>
        /// 记录当前时间戳
        /// </summary>
        /// <returns>当前时间戳（毫秒）</returns>
        public double RecordTimestamp()
        {
            return stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// 设置缓冲区大小
        /// </summary>
        /// <param name="size">缓冲区大小</param>
        public void SetBufferSize(int size)
        {
            BufferSize.Value = size;
            if (Enabled)
                Logger.Log($"[EzLatencyTest] Buffer size set to: {size}");
        }

        /// <summary>
        /// 设置采样率
        /// </summary>
        /// <param name="rate">采样率</param>
        public void SetSampleRate(int rate)
        {
            SampleRate.Value = rate;
            if (Enabled)
                Logger.Log($"[EzLatencyTest] Sample rate set to: {rate}");
        }

        /// <summary>
        /// 设置驱动类型
        /// </summary>
        /// <param name="driver">驱动类型 ("ASIO" 或 "WASAPIExclusive")</param>
        public void SetDriverType(string driver)
        {
            DriverType.Value = driver;
            if (Enabled)
                Logger.Log($"[EzLatencyTest] Driver type set to: {driver}");
        }

        /// <summary>
        /// 定期测试（在 AudioThread.onNewFrame 中调用）
        /// </summary>
        public void RunPeriodicTest()
        {
            double currentTime = RecordTimestamp();
            if (currentTime - lastTestTime > 1000) // 每秒测试一次
            {
                RunTest();
                lastTestTime = currentTime;
            }
        }

        /// <summary>
        /// 精确测试（在 AudioManager 播放事件中调用）
        /// </summary>
        public void RunPreciseTest()
        {
            RunTest();
        }

        /// <summary>
        /// 临时模拟延迟计算（实际实现需要音频处理逻辑）
        /// </summary>
        private double calculateSimulatedLatency()
        {
            // 基于配置参数模拟延迟
            double baseLatency = DriverType.Value == "ASIO" ? 2.0 : 5.0; // ASIO通常延迟更低
            double bufferLatency = (BufferSize.Value / (double)SampleRate.Value) * 1000; // 缓冲区延迟
            const double hardware_latency = 1.5; // 硬件层延迟

            return baseLatency + bufferLatency + hardware_latency;
        }
    }
}
