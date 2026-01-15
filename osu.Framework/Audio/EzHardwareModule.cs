// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using osu.Framework.Logging;

namespace osu.Framework.Audio
{
    /// <summary>
    /// EzHardwareModule 负责硬件层延迟的测量和管理。
    /// 主要职责包括测量 DAC 转换延迟、总线延迟等，并记录不可控延迟（UncontrollableLatency）。
    /// </summary>
    public class EzHardwareModule
    {
        private readonly Stopwatch stopwatch;

        /// <summary>
        /// EzOsuLatency 总开关 - 控制是否启用延迟测量
        /// </summary>
        public bool Enabled { get; set; } = false;

        // 存储硬件时间
        internal double OutputHardwareTime;
        internal double InputHardwareTime;
        internal double LatencyDifference;

        /// <summary>
        /// 构造函数
        /// </summary>
        public EzHardwareModule()
        {
            stopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// 记录输出硬件播放时间戳
        /// </summary>
        /// <param name="timestamp">测量的时间戳</param>
        public void RecordOutputTimestamp(DateTime timestamp)
        {
            if (Enabled)
            {
                OutputHardwareTime = stopwatch.Elapsed.TotalMilliseconds;
                // 不直接打印日志
            }
        }

        /// <summary>
        /// 记录输入硬件接收时间戳，并计算首尾延迟差值
        /// </summary>
        /// <param name="timestamp">测量的时间戳</param>
        /// <param name="driverTime">驱动时间</param>
        public void RecordInputTimestamp(DateTime timestamp, double driverTime)
        {
            if (Enabled)
            {
                InputHardwareTime = stopwatch.Elapsed.TotalMilliseconds;
                LatencyDifference = OutputHardwareTime - EzInputModule.InputTime; // 首尾延迟差值：T_out - T_in
                // 一次性打印硬件数据的日志
                Logger.Log($"[EzOsuLatency] Driver: {driverTime:F2}ms, OutputHW: {OutputHardwareTime:F2}ms, InputHW: {InputHardwareTime:F2}ms, LatencyDiff: {LatencyDifference:F2}ms", name: "audio", level: LogLevel.Debug);
            }
        }

        /// <summary>
        /// 运行硬件延迟测试
        /// </summary>
        public void RunTest()
        {
            if (Enabled)
                Logger.Log("[EzHardware] Run hardware latency test", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 测量硬件延迟
        /// </summary>
        /// <returns>不可控延迟值</returns>
        public double MeasureHardwareLatency()
        {
            // 测量 DAC 转换延迟、USB/PCIe 总线延迟等
            // 由于无法真实测量，返回默认值
            const double uncontrollable_latency = 2.0; // DAC + 总线延迟估算
            if (Enabled)
                Logger.Log($"[EzHardware] Measure hardware latency: {uncontrollable_latency:F2}ms", name: "audio", level: LogLevel.Debug);
            return uncontrollable_latency;
        }

        /// <summary>
        /// 设置不可控延迟值
        /// </summary>
        /// <param name="uncontrollableLatency">不可控延迟值</param>
        public void SetUncontrollableLatency(double uncontrollableLatency)
        {
            if (Enabled)
                Logger.Log($"[EzHardware] Set uncontrollable latency: {uncontrollableLatency:F2}ms", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置采样率
        /// </summary>
        /// <param name="sampleRate">采样率</param>
        public void SetSampleRate(int sampleRate)
        {
            if (Enabled)
                Logger.Log($"[EzHardware] Set sample rate: {sampleRate}", name: "audio", level: LogLevel.Debug);
        }
    }
}
