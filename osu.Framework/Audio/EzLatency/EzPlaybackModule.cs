// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// EzPlaybackModule 负责处理播放事件的触发和时间戳记录。
    /// 主要职责包括监听播放事件，记录播放触发时间，并测量从触发到实际发声的延迟。
    /// </summary>
    public class EzPlaybackModule
    {
        /// <summary>
        /// EzOsuLatency 总开关 - 控制是否启用延迟测量
        /// </summary>
        public bool Enabled { get; set; }

        private readonly System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();

        // 存储播放时间
        internal double PlaybackTime;

        // AudioThread 引用，用于访问其他模块
        private readonly AudioThread audioThread;

        public EzPlaybackModule(AudioThread audioThread)
        {
            this.audioThread = audioThread;
            stopwatch.Start();
        }

        /// <summary>
        /// 记录播放事件的时间戳（T_call）
        /// </summary>
        /// <param name="timestamp">播放事件的时间戳</param>
        public void RecordTimestamp(DateTime timestamp)
        {
            if (Enabled)
            {
                PlaybackTime = stopwatch.Elapsed.TotalMilliseconds;

                // 只有在有有效的输入数据时才打印日志（避免长按note的情况）
                if (EzInputModule.InputTime > 0)
                {
                    // build local low-level structs for richer diagnostics (do not mutate AudioThread fields)
                    EzLatencyInputData inputData = default;
                    EzLatencyHardwareData hardwareData = default;

                    try
                    {
                        inputData.InputTime = EzInputModule.InputTime;
                        inputData.KeyValue = EzInputModule.KeyValue ?? "";
                        inputData.JudgeTime = EzJudgeModule.JudgeTime;
                        inputData.PlaybackTime = PlaybackTime;

                        hardwareData.DriverTime = audioThread.DriverModule.DriverTime;
                        hardwareData.OutputHardwareTime = audioThread.HardwareModule.OutputHardwareTime;
                        hardwareData.InputHardwareTime = audioThread.HardwareModule.InputHardwareTime;
                        hardwareData.LatencyDifference = audioThread.HardwareModule.LatencyDifference;
                    }
                    catch { }

                    // 生成最终 EzLatencyRecord 并直接推送到 EzLatencyService
                    var record = new osu.Framework.Audio.EzLatency.EzLatencyRecord
                    {
                        Timestamp = DateTimeOffset.Now,
                        MeasuredMs = PlaybackTime - EzInputModule.InputTime,
                        Note = "playback-record",
                        InputTime = EzInputModule.InputTime,
                        JudgeTime = EzJudgeModule.JudgeTime,
                        PlaybackTime = PlaybackTime,
                        DriverTime = audioThread.DriverModule.DriverTime,
                        OutputHardwareTime = audioThread.HardwareModule.OutputHardwareTime,
                        InputHardwareTime = audioThread.HardwareModule.InputHardwareTime,
                        LatencyDifference = audioThread.HardwareModule.LatencyDifference,
                        InputData = inputData,
                        HardwareData = hardwareData
                    };

                    // push to central service for osu layer to consume
                    try
                    {
                        osu.Framework.Audio.EzLatency.EzLatencyService.Instance.PushRecord(record);
                    }
                    catch { }

                    // 兼容：仍然把事件记录到 EzLogModule 的内部统计（可选）
                    try
                    {
                        if (EzLogModule.Instance != null && EzLogModule.Instance.Enabled)
                            EzLogModule.Instance.RecordLatencyEventAndGet(record.InputTime, record.JudgeTime, record.PlaybackTime, record.DriverTime, record.OutputHardwareTime, record.InputHardwareTime, record.LatencyDifference, inputData, hardwareData);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// 运行播放延迟测试
        /// </summary>
        public void RunTest()
        {
            if (Enabled)
                Logger.Log("[EzPlayback] Run playback latency test", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置播放缓冲区大小
        /// </summary>
        /// <param name="bufferSize">缓冲区大小</param>
        public void SetBufferSize(int bufferSize)
        {
            if (Enabled)
                Logger.Log($"[EzPlayback] Set buffer size: {bufferSize}", name: "audio", level: LogLevel.Debug);
        }

        /// <summary>
        /// 设置采样率
        /// </summary>
        /// <param name="sampleRate">采样率</param>
        public void SetSampleRate(int sampleRate)
        {
            if (Enabled)
                Logger.Log($"[EzPlayback] Set sample rate: {sampleRate}", name: "audio", level: LogLevel.Debug);
        }
    }
}
