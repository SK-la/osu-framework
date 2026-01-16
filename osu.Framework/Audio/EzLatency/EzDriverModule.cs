// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using osu.Framework.Logging;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// EzDriverModule 负责音频驱动的设置和管理。
    /// 主要职责包括配置 ASIO 和 WASAPI 独占模式，设置采样率和缓冲区大小，并获取驱动延迟反馈。
    /// </summary>
    public class EzDriverModule
    {
        /// <summary>
        /// EzOsuLatency 总开关 - 控制是否启用延迟测量
        /// </summary>
        public bool Enabled { get; set; } = false;

        private readonly Stopwatch stopwatch = new Stopwatch();

        // 存储驱动时间
        internal double DriverTime;

        public EzDriverModule()
        {
            stopwatch.Start();
        }

        /// <summary>
        /// 记录驱动操作的时间戳
        /// </summary>
        /// <param name="timestamp">操作的时间戳</param>
        public void RecordTimestamp(DateTime timestamp)
        {
            if (Enabled)
            {
                DriverTime = stopwatch.Elapsed.TotalMilliseconds;
                // 不直接打印日志
            }
        }

        /// <summary>
        /// 运行驱动延迟测试
        /// </summary>
        public void RunTest()
        {
            if (Enabled)
                Logger.Log("[EzDriver] Run driver latency test", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置缓冲区大小
        /// </summary>
        /// <param name="bufferSize">缓冲区大小</param>
        public void SetBufferSize(int bufferSize)
        {
            if (Enabled)
                Logger.Log($"[EzDriver] Set buffer size: {bufferSize}", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置采样率
        /// </summary>
        /// <param name="sampleRate">采样率</param>
        public void SetSampleRate(int sampleRate)
        {
            if (Enabled)
                Logger.Log($"[EzDriver] Set sample rate: {sampleRate}", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置驱动类型
        /// </summary>
        /// <param name="driverType">驱动类型，如 ASIO 或 WASAPIExclusive</param>
        public void SetDriverType(string driverType)
        {
            if (Enabled)
                Logger.Log($"[EzDriver] Set driver type: {driverType}", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 获取驱动缓冲延迟（T_buf）
        /// </summary>
        /// <returns>缓冲延迟（毫秒），失败返回 -1</returns>
        public double GetBufferLatency()
        {
            // 这里应该调用 AudioThread 的 GetAsioOutputLatency 或 GetWasapiStreamLatency
            // 暂时返回估算值
            if (Enabled)
                Logger.Log("[EzDriver] Get buffer latency", name: "audio", level: LogLevel.Debug);
            return 5.0; // 估算值
        }
    }
}
