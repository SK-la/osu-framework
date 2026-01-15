// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Logging;

namespace osu.Framework.Audio
{
    /// <summary>
    /// EzJudgeModule 负责处理判定时间的计算和记录。
    /// 主要职责包括基于输入事件计算判定时间，记录判定延迟，并为整体延迟分析提供数据。
    /// </summary>
    public static class EzJudgeModule
    {
        /// <summary>
        /// EzOsuLatency 总开关 - 控制是否启用延迟测量
        /// </summary>
        public static bool Enabled { get; set; } = false;

        private static readonly System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();

        // 存储判定时间
        internal static double JudgeTime;

        static EzJudgeModule()
        {
            stopwatch.Start();
        }

        /// <summary>
        /// 记录判定时间戳（T_judge）
        /// </summary>
        /// <param name="timestamp">判定事件的时间戳</param>
        public static void RecordTimestamp(DateTime timestamp)
        {
            if (Enabled)
            {
                JudgeTime = stopwatch.Elapsed.TotalMilliseconds;
                // 不直接打印日志
            }
        }

        /// <summary>
        /// 运行判定延迟测试
        /// </summary>
        public static void RunTest()
        {
            if (Enabled)
                Logger.Log("[EzJudge] Run judgement latency test", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置判定缓冲区大小
        /// </summary>
        /// <param name="bufferSize">缓冲区大小</param>
        public static void SetBufferSize(int bufferSize)
        {
            if (Enabled)
                Logger.Log($"[EzJudge] Set buffer size: {bufferSize}", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置采样率
        /// </summary>
        /// <param name="sampleRate">采样率</param>
        public static void SetSampleRate(int sampleRate)
        {
            if (Enabled)
                Logger.Log($"[EzJudge] Set sample rate: {sampleRate}", name: "audio", level: LogLevel.Debug);
        }
    }
}
