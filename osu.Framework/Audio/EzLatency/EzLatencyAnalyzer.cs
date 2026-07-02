// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using osu.Framework.Logging;

namespace osu.Framework.Audio.EzLatency
{
#nullable disable

    /// <summary>
    /// Correlates input/judge/playback events by assigning a unique <see cref="Guid"/> to each input
    /// and matching subsequent events within a sane time window. Thread-safe via <c>lock</c>.
    /// </summary>
    public class EzLatencyAnalyzer
    {
        private readonly Stopwatch stopwatch;
        public bool Enabled { get; set; }
        public event Action<EzLatencyRecord> OnNewRecord;

        // Ring-buffer pool of slots; each occupied slot holds one in-flight input.
        private readonly EzLatencyInputData[] slots;
        private const int pool_size = 16;
        private readonly object gate = new object();

        private double recordStartTime;
        private const double timeout_ms = 5000;

        public EzLatencyAnalyzer()
        {
            stopwatch = Stopwatch.StartNew();
            slots = new EzLatencyInputData[pool_size];
        }

        /// <summary>
        /// Record an input event. Assigns a unique correlation ID and claims a free slot.
        /// If no free slot is available, the oldest occupied slot is evicted (LRU policy).
        /// </summary>
        public void RecordInputData(double inputTime, object keyValue = null)
        {
            if (!Enabled) return;

            lock (gate)
            {
                // Try to find a free slot.
                for (int i = 0; i < pool_size; i++)
                {
                    if (slots[i].IsFree)
                    {
                        slots[i] = new EzLatencyInputData
                        {
                            CorrelationId = Guid.NewGuid(),
                            InputTime = inputTime,
                            KeyValue = keyValue,
                        };
                        recordStartTime = stopwatch.Elapsed.TotalMilliseconds;
                        return;
                    }
                }

                // All slots occupied: evict the slot with the oldest InputTime (LRU).
                int oldestIdx = 0;
                double oldestTime = double.MaxValue;

                for (int i = 0; i < pool_size; i++)
                {
                    if (slots[i].InputTime < oldestTime)
                    {
                        oldestTime = slots[i].InputTime;
                        oldestIdx = i;
                    }
                }

                Logger.Log($"EzLatency 缓冲池已满，淘汰最旧输入 (InputTime={slots[oldestIdx].InputTime:F2})", LoggingTarget.Runtime, LogLevel.Debug);

                slots[oldestIdx] = new EzLatencyInputData
                {
                    CorrelationId = Guid.NewGuid(),
                    InputTime = inputTime,
                    KeyValue = keyValue,
                };
                recordStartTime = stopwatch.Elapsed.TotalMilliseconds;
            }
        }

        /// <summary>
        /// Record a judge event. Matches to the closest occupied input slot within the reasonable window.
        /// </summary>
        public void RecordJudgeData(double judgeTime)
        {
            if (!Enabled) return;

            lock (gate)
            {
                int bestIdx = findBestMatchSlot(judgeTime, isJudge: true);

                if (bestIdx < 0)
                {
                    checkTimeout();
                    return;
                }

                var data = slots[bestIdx];
                data.JudgeTime = judgeTime;
                slots[bestIdx] = data;

                tryGenerateCompleteRecord(bestIdx);
            }
        }

        /// <summary>
        /// Record a playback event. Matches to the closest occupied input slot within the reasonable window.
        /// </summary>
        public void RecordPlaybackData(double playbackTime)
        {
            if (!Enabled) return;

            lock (gate)
            {
                int bestIdx = findBestMatchSlot(playbackTime, isJudge: false);

                if (bestIdx < 0)
                {
                    checkTimeout();
                    return;
                }

                var data = slots[bestIdx];
                data.PlaybackTime = playbackTime;
                slots[bestIdx] = data;

                tryGenerateCompleteRecord(bestIdx);
            }
        }

        public void RecordHardwareData(double driverTime, double outputHardwareTime, double inputHardwareTime, double latencyDifference)
        {
            if (!Enabled) return;

            // Hardware data is associated with the last matched slot (best-effort: find the most recent input).
            lock (gate)
            {
                // Find the slot with the latest InputTime that is still occupied.
                int latestIdx = -1;
                double latestTime = double.MinValue;

                for (int i = 0; i < pool_size; i++)
                {
                    if (slots[i].IsOccupied && slots[i].InputTime > latestTime)
                    {
                        latestTime = slots[i].InputTime;
                        latestIdx = i;
                    }
                }

                if (latestIdx < 0)
                    return;

                // Hardware data is handled separately: we don't try to match it to a specific slot here.
                // The data will be consumed when the slot generates a complete record.
                // For simplicity, we store it in a separate field and attach to the next generated record.
                pendingHardwareData = new EzLatencyHardwareData
                {
                    DriverTime = driverTime,
                    OutputHardwareTime = outputHardwareTime,
                    InputHardwareTime = inputHardwareTime,
                    LatencyDifference = latencyDifference
                };
            }
        }

        private EzLatencyHardwareData pendingHardwareData;

        private void tryGenerateCompleteRecord(int slotIdx)
        {
            var currentInputData = slots[slotIdx];

            if (!currentInputData.IsValid)
            {
                checkTimeout();
                return;
            }

            // Sanity check: if PlaybackTime - InputTime exceeds threshold, this is a mismatched event.
            if (currentInputData.PlaybackTime > 0)
            {
                double delta = currentInputData.PlaybackTime - currentInputData.InputTime;

                if (delta < 0 || delta > EzLatencyInputData.MAX_REASONABLE_INPUT_TO_PLAYBACK_MS)
                {
                    Logger.Log($"EzLatency 丢弃不匹配记录: Input→Playback={delta:F2}ms (槽#{slotIdx} CorrId={currentInputData.CorrelationId})",
                        LoggingTarget.Runtime, LogLevel.Debug);
                    clearSlot(slotIdx);
                    return;
                }
            }

            // Sanity check: JudgeTime should always be >= InputTime.
            if (currentInputData.JudgeTime > 0 && currentInputData.JudgeTime < currentInputData.InputTime)
            {
                Logger.Log($"EzLatency 丢弃不匹配记录: JudgeTime({currentInputData.JudgeTime:F2}) < InputTime({currentInputData.InputTime:F2})",
                    LoggingTarget.Runtime, LogLevel.Debug);
                clearSlot(slotIdx);
                return;
            }

            double measuredMs = currentInputData.PlaybackTime > 0
                ? currentInputData.PlaybackTime - currentInputData.InputTime
                : currentInputData.JudgeTime - currentInputData.InputTime;

            var record = new EzLatencyRecord
            {
                CorrelationId = currentInputData.CorrelationId,
                Timestamp = DateTimeOffset.Now,
                InputTime = currentInputData.InputTime,
                JudgeTime = currentInputData.JudgeTime,
                PlaybackTime = currentInputData.PlaybackTime,
                DriverTime = pendingHardwareData.DriverTime,
                OutputHardwareTime = pendingHardwareData.OutputHardwareTime,
                InputHardwareTime = pendingHardwareData.InputHardwareTime,
                LatencyDifference = pendingHardwareData.LatencyDifference,
                MeasuredMs = measuredMs,
                Note = pendingHardwareData.IsValid ? "complete-latency-measurement" : "best-effort-no-hw",
                InputData = currentInputData,
                HardwareData = pendingHardwareData
            };

            try
            {
                OnNewRecord?.Invoke(record);
                EzLatencyService.Instance.PushRecord(record);
                Logger.Log(
                    pendingHardwareData.IsValid
                        ? $"EzLatency 完整记录已生成: Input→Playback={record.PlaybackTime - record.InputTime:F2}ms"
                        : $"EzLatency 最佳尝试记录（无硬件时间戳）: Input→Playback={record.PlaybackTime - record.InputTime:F2}ms",
                    LoggingTarget.Runtime, LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.Log($"EzLatencyAnalyzer: tryGenerateCompleteRecord failed: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }

            clearSlot(slotIdx);
            pendingHardwareData = default;
        }

        /// <summary>
        /// Find the occupied slot whose InputTime is closest to (now - expectedLatencyMs)
        /// and within the reasonable time window.
        /// </summary>
        private int findBestMatchSlot(double eventTime, bool isJudge)
        {
            double maxDelta = isJudge
                ? EzLatencyInputData.MAX_REASONABLE_INPUT_TO_JUDGE_MS
                : EzLatencyInputData.MAX_REASONABLE_INPUT_TO_PLAYBACK_MS;

            int bestIdx = -1;
            double bestDelta = double.MaxValue;

            for (int i = 0; i < pool_size; i++)
            {
                if (!slots[i].IsOccupied)
                    continue;

                double delta = eventTime - slots[i].InputTime;

                // Event must occur after the input.
                if (delta < 0)
                    continue;

                // Must be within the reasonable window.
                if (delta > maxDelta)
                    continue;

                // Pick the closest match.
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIdx = i;
                }
            }

            return bestIdx;
        }

        public double GetCurrentTimestamp() => stopwatch.Elapsed.TotalMilliseconds;

        private void checkTimeout()
        {
            if (recordStartTime <= 0)
                return;

            double elapsed = stopwatch.Elapsed.TotalMilliseconds - recordStartTime;

            if (elapsed > timeout_ms)
            {
                Logger.Log($"EzLatency 数据收集超时 ({elapsed:F0}ms)，清除旧数据", LoggingTarget.Runtime, LogLevel.Debug);
                clearAllSlots();
            }
        }

        public void ClearCurrentData()
        {
            lock (gate)
            {
                clearAllSlots();
            }
        }

        private void clearSlot(int idx)
        {
            slots[idx] = default;
        }

        private void clearAllSlots()
        {
            for (int i = 0; i < pool_size; i++)
                slots[i] = default;

            pendingHardwareData = default;
            recordStartTime = 0;
        }
    }
}
