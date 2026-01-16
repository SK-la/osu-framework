// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using osu.Framework.Logging;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// 延迟检测管理器，负责协调输入和硬件数据的收集
    /// </summary>
    public class EzLatencyAnalyzer
    {
        private readonly Stopwatch stopwatch;
        
        /// <summary>
        /// 是否启用延迟测量
        /// </summary>
        public bool Enabled { get; set; } = false;
        
        /// <summary>
        /// 当有新的延迟记录时触发
        /// </summary>
        public event Action<EzLatencyRecord>? OnNewRecord;

        // 当前收集的数据
        private EzLatencyInputData currentInputData;
        private EzLatencyHardwareData currentHardwareData;

        public EzLatencyAnalyzer()
        {
            stopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// 记录输入数据
        /// </summary>
        /// <param name="inputTime">输入时间</param>
        /// <param name="keyValue">按键值</param>
        public void RecordInputData(double inputTime, object? keyValue = null)
        {
            if (!Enabled) return;

            currentInputData = new EzLatencyInputData
            {
                InputTime = inputTime,
                KeyValue = keyValue,
                JudgeTime = currentInputData.JudgeTime, // 保持已有的值
                PlaybackTime = currentInputData.PlaybackTime // 保持已有的值
            };
        }

        /// <summary>
        /// 记录判定数据
        /// </summary>
        /// <param name="judgeTime">判定时间</param>
        public void RecordJudgeData(double judgeTime)
        {
            if (!Enabled) return;

            currentInputData = new EzLatencyInputData
            {
                InputTime = currentInputData.InputTime,
                KeyValue = currentInputData.KeyValue,
                JudgeTime = judgeTime,
                PlaybackTime = currentInputData.PlaybackTime
            };
        }

        /// <summary>
        /// 记录播放数据
        /// </summary>
        /// <param name="playbackTime">播放时间</param>
        public void RecordPlaybackData(double playbackTime)
        {
            if (!Enabled) return;

            currentInputData = new EzLatencyInputData
            {
                InputTime = currentInputData.InputTime,
                KeyValue = currentInputData.KeyValue,
                JudgeTime = currentInputData.JudgeTime,
                PlaybackTime = playbackTime
            };

            // 尝试生成完整记录
            TryGenerateCompleteRecord();
        }

        /// <summary>
        /// 记录硬件数据
        /// </summary>
        /// <param name="driverTime">驱动时间</param>
        /// <param name="outputHardwareTime">输出硬件时间</param>
        /// <param name="inputHardwareTime">输入硬件时间</param>
        /// <param name="latencyDifference">延迟差异</param>
        public void RecordHardwareData(double driverTime, double outputHardwareTime, double inputHardwareTime, double latencyDifference)
        {
            if (!Enabled) return;

            currentHardwareData = new EzLatencyHardwareData
            {
                DriverTime = driverTime,
                OutputHardwareTime = outputHardwareTime,
                InputHardwareTime = inputHardwareTime,
                LatencyDifference = latencyDifference
            };

            // 尝试生成完整记录
            TryGenerateCompleteRecord();
        }

        /// <summary>
        /// 尝试生成完整的延迟记录（当输入数据和硬件数据都有效时）
        /// </summary>
        private void TryGenerateCompleteRecord()
        {
            if (currentInputData.IsValid && currentHardwareData.IsValid)
            {
                var record = new EzLatencyRecord
                {
                    Timestamp = DateTimeOffset.Now,
                    InputTime = currentInputData.InputTime,
                    JudgeTime = currentInputData.JudgeTime,
                    PlaybackTime = currentInputData.PlaybackTime,
                    DriverTime = currentHardwareData.DriverTime,
                    OutputHardwareTime = currentHardwareData.OutputHardwareTime,
                    InputHardwareTime = currentHardwareData.InputHardwareTime,
                    LatencyDifference = currentHardwareData.LatencyDifference,
                    MeasuredMs = currentInputData.PlaybackTime - currentInputData.InputTime,
                    Note = "complete-latency-measurement",
                    InputData = currentInputData,
                    HardwareData = currentHardwareData
                };

                // 触发事件
                try
                {
                    OnNewRecord?.Invoke(record);
                    
                    // 同时推送到中央服务
                    EzLatencyService.Instance.PushRecord(record);
                }
                catch (Exception ex)
                {
                    Logger.Log($"延迟记录事件处理出错: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                }

                // 清空已使用的数据，等待下一轮收集
                currentInputData = default;
                currentHardwareData = default;
            }
        }

        /// <summary>
        /// 获取当前时间戳（毫秒）
        /// </summary>
        /// <returns>当前时间戳</returns>
        public double GetCurrentTimestamp() => stopwatch.Elapsed.TotalMilliseconds;
    }
}