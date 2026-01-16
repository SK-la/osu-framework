// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Logging;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// EzOsuLatency 日志模块
    /// 负责记录延迟测量数据、缓冲区参数和驱动类型信息。
    /// 使用框架的 Logger 输出日志。
    /// </summary>
    public class EzLogModule
    {
        /// <summary>
        /// 静态实例，用于全局访问
        /// </summary>
        public static EzLogModule Instance { get; } = new EzLogModule();

        /// <summary>
        /// EzOsuLatency 总开关 - 控制是否启用延迟测量
        /// </summary>
        public bool Enabled { get; set; } = false;

        // 存储延迟记录用于统计
        private readonly List<EzLatencyRecord> latencyRecords = new List<EzLatencyRecord>();

        public event Action<EzLatencyRecord>? OnNewRecord;

        /// <summary>
        /// 延迟统计数据结构体
        /// </summary>
        public struct EzLatencyStatistics
        {
            public int RecordCount;
            public double AvgInputToJudge;
            public double AvgInputToPlayback;
            public double AvgPlaybackToJudge;
            public bool HasData;
        }

        /// <summary>
        /// 记录单个延迟事件并返回延迟信息（用于实时输出）
        /// </summary>
        public EzLatencyRecord? RecordLatencyEventAndGet(double inputTime, double judgeTime, double playbackTime, double driverTime, double outputHardwareTime, double inputHardwareTime,
                                                         double latencyDifference, EzLatencyInputData inputData = default, EzLatencyHardwareData hardwareData = default)
        {
            if (!Enabled)
                return null;

            var record = new EzLatencyRecord
            {
                Timestamp = DateTimeOffset.Now,
                InputTime = inputTime,
                JudgeTime = judgeTime,
                PlaybackTime = playbackTime,
                DriverTime = driverTime,
                OutputHardwareTime = outputHardwareTime,
                InputHardwareTime = inputHardwareTime,
                LatencyDifference = latencyDifference,
                MeasuredMs = playbackTime - inputTime,
                Note = "ezlogmodule"
            };

            // attach optional low-level structs for richer diagnostics
            try
            {
                record.InputData = inputData;
                record.HardwareData = hardwareData;
            }
            catch { }

            latencyRecords.Add(record);

            // notify any subscribers about the new record
            try
            {
                OnNewRecord?.Invoke(record);
            }
            catch { }

            return record;
        }

        /// <summary>
        /// 获取延迟统计数据（不直接输出日志）
        /// </summary>
        /// <returns>延迟统计数据结构体</returns>
        public EzLatencyStatistics GetLatencyStatistics()
        {
            if (!Enabled || latencyRecords.Count == 0)
            {
                return new EzLatencyStatistics { HasData = false };
            }

            // 计算各种延迟的平均值
            double avgInputToJudge = latencyRecords.Average(r => r.JudgeTime - r.InputTime);
            double avgInputToPlayback = latencyRecords.Average(r => r.PlaybackTime - r.InputTime);
            double avgPlaybackToJudge = latencyRecords.Average(r => r.JudgeTime - r.PlaybackTime);

            var statistics = new EzLatencyStatistics
            {
                RecordCount = latencyRecords.Count,
                AvgInputToJudge = avgInputToJudge,
                AvgInputToPlayback = avgInputToPlayback,
                AvgPlaybackToJudge = avgPlaybackToJudge,
                HasData = true
            };

            // 清空记录，为下次统计做准备
            latencyRecords.Clear();

            return statistics;
        }

        /// <summary>
        /// 生成延迟统计报告（兼容旧接口，直接输出日志）
        /// </summary>
        public void LogLatencyStatistics()
        {
            var stats = GetLatencyStatistics();

            if (!stats.HasData)
            {
                Logger.Log($"[EzOsuLatency] No latency data available", LoggingTarget.Runtime, LogLevel.Debug);
                return;
            }

            string message1 =
                $"Input->Judgement: {stats.AvgInputToJudge:F2}ms, Input->Audio: {stats.AvgInputToPlayback:F2}ms, Audio->Judgement: {stats.AvgPlaybackToJudge:F2}ms (based on {stats.RecordCount} complete records)";
            string message2 =
                $"Input->Judgement: {stats.AvgInputToJudge:F2}ms, \nInput->Audio: {stats.AvgInputToPlayback:F2}ms, \nAudio->Judgement: {stats.AvgPlaybackToJudge:F2}ms \n(based on {stats.RecordCount} complete records)";

            Logger.Log($"[EzOsuLatency] Analysis: {message1}");
            Logger.Log($"[EzOsuLatency] Analysis: \n{message2}", LoggingTarget.Runtime, LogLevel.Important);
        }

        /// <summary>
        /// 记录延迟数据（兼容旧接口）
        /// </summary>
        /// <param name="timestamp">时间戳</param>
        /// <param name="driverType">驱动类型</param>
        /// <param name="sampleRate">采样率</param>
        /// <param name="bufferSize">缓冲区大小</param>
        /// <param name="inputLatency">输入延迟</param>
        /// <param name="playbackLatency">播放延迟</param>
        /// <param name="totalLatency">总延迟</param>
        /// <param name="uncontrollableLatency">不可控延迟</param>
        public void LogLatency(double timestamp, string driverType, int sampleRate, int bufferSize, double inputLatency, double playbackLatency, double totalLatency, double uncontrollableLatency)
        {
            if (Enabled)
                Logger.Log(
                    $"[EzOsuLatency] Latency data: Timestamp={timestamp:F2}, Driver={driverType}, SampleRate={sampleRate}, Buffer={bufferSize}, Input={inputLatency:F2}ms, Playback={playbackLatency:F2}ms, Total={totalLatency:F2}ms, Uncontrollable={uncontrollableLatency:F2}ms",
                    LoggingTarget.Runtime, LogLevel.Debug);
        }

        /// <summary>
        /// 记录一般日志信息
        /// </summary>
        /// <param name="message">日志消息</param>
        public void LogInfo(string message)
        {
            if (Enabled)
                Logger.Log($"[EzOsuLatency] {message}", LoggingTarget.Runtime, LogLevel.Important);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        /// <param name="message">错误消息</param>
        public void LogError(string message)
        {
            if (Enabled)
                Logger.Log($"[EzOsuLatency] Error: {message}", LoggingTarget.Runtime, LogLevel.Error);
        }
    }
}
