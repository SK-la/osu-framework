// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using osu.Framework.Logging;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// EzInputModule 负责处理输入事件的捕获和时间戳记录。
    /// 主要职责包括监听用户输入事件，记录输入发生的时间戳，并为延迟测试提供输入基准。
    /// </summary>
    public static class EzInputModule
    {
        /// <summary>
        /// EzOsuLatency 总开关 - 控制是否启用延迟测量
        /// </summary>
        public static bool Enabled { get; set; } = false;

        private static readonly Stopwatch stopwatch = new Stopwatch();

        // 存储输入数据
        public static double InputTime;
        internal static object? KeyValue;

        static EzInputModule()
        {
            stopwatch.Start();
        }

        /// <summary>
        /// 记录输入事件的时间戳（T_in）
        /// </summary>
        /// <param name="timestamp">输入事件的时间戳</param>
        /// <param name="keyValue">按键值</param>
        public static void RecordTimestamp(DateTime timestamp, object keyValue)
        {
            if (Enabled)
            {
                InputTime = stopwatch.Elapsed.TotalMilliseconds;
                KeyValue = keyValue;
                // 不直接打印日志
            }
        }

        /// <summary>
        /// 清空输入数据（用于长按note的情况）
        /// </summary>
        public static void ClearInputData()
        {
            InputTime = 0;
            KeyValue = null;
        }

        public static void RunTest()
        {
            // 实现逻辑：执行测试
            if (Enabled)
                Logger.Log("[EzInput] Run input latency test", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置输入缓冲区大小
        /// </summary>
        /// <param name="bufferSize">缓冲区大小</param>
        public static void SetBufferSize(int bufferSize)
        {
            if (Enabled)
                Logger.Log($"[EzInput] Set buffer size: {bufferSize}", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置采样率
        /// </summary>
        /// <param name="sampleRate">采样率</param>
        public static void SetSampleRate(int sampleRate)
        {
            if (Enabled)
                Logger.Log($"[EzInput] Set sample rate: {sampleRate}", name: "audio", level: LogLevel.Debug);
        }
    }
}
