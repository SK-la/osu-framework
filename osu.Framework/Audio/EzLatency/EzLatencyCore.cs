// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Audio.EzLatency
{
    #nullable disable

    // --- Structures and Record -------------------------------------------------

    public struct EzLatencyInputData
    {
        public double InputTime;
        public object? KeyValue;
        public double JudgeTime;
        public double PlaybackTime;
        public bool IsValid => InputTime > 0 && JudgeTime > 0 && PlaybackTime > 0;
    }

    public struct EzLatencyHardwareData
    {
        public double DriverTime;
        public double OutputHardwareTime;
        public double InputHardwareTime;
        public double LatencyDifference;
        public bool IsValid => OutputHardwareTime > 0 && DriverTime > 0;
    }

    public class EzLatencyRecord
    {
        public DateTimeOffset Timestamp { get; set; }
        public double MeasuredMs { get; set; }
        public string Note { get; set; }

        public double InputTime { get; set; }
        public double JudgeTime { get; set; }
        public double PlaybackTime { get; set; }
        public double DriverTime { get; set; }
        public double OutputHardwareTime { get; set; }
        public double InputHardwareTime { get; set; }
        public double LatencyDifference { get; set; }

        public EzLatencyInputData InputData { get; set; }
        public EzLatencyHardwareData HardwareData { get; set; }

        public bool IsComplete => InputData.IsValid && HardwareData.IsValid;
    }

    public enum AudioOutputMode
    {
        Default,
        WasapiShared,
        WasapiExclusive,
        Asio,
    }

    // --- Analyzer --------------------------------------------------------------

    public class EzLatencyAnalyzer
    {
        private readonly Stopwatch stopwatch;
        public bool Enabled { get; set; } = false;
        public event Action<EzLatencyRecord>? OnNewRecord;

        private EzLatencyInputData currentInputData;
        private EzLatencyHardwareData currentHardwareData;
        private double recordStartTime;
        private const double timeout_ms = 5000;

        public EzLatencyAnalyzer()
        {
            stopwatch = Stopwatch.StartNew();
        }

        public void RecordInputData(double inputTime, object? keyValue = null)
        {
            if (!Enabled) return;
            if (currentInputData.InputTime > 0)
            {
                currentInputData = default;
                currentHardwareData = default;
            }

            currentInputData.InputTime = inputTime;
            currentInputData.KeyValue = keyValue;
            recordStartTime = stopwatch.Elapsed.TotalMilliseconds;
        }

        public void RecordJudgeData(double judgeTime)
        {
            if (!Enabled) return;
            currentInputData.JudgeTime = judgeTime;
            checkTimeout();
        }

        public void RecordPlaybackData(double playbackTime)
        {
            if (!Enabled) return;
            currentInputData.PlaybackTime = playbackTime;
            tryGenerateCompleteRecord();
        }

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

            tryGenerateCompleteRecord();
        }

        private void tryGenerateCompleteRecord()
        {
            if (!currentInputData.IsValid || !currentHardwareData.IsValid)
            {
                checkTimeout();
                return;
            }

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

            try
            {
                OnNewRecord?.Invoke(record);
                EzLatencyService.Instance.PushRecord(record);
                Logger.Log(string.Format("EzLatency 完整记录已生成: Input→Playback={0:F2}ms", record.PlaybackTime - record.InputTime), LoggingTarget.Runtime, LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.Log(string.Format("延迟记录事件处理出错: {0}", ex.Message), LoggingTarget.Runtime, LogLevel.Error);
            }

            ClearCurrentData();
        }

        public double GetCurrentTimestamp() => stopwatch.Elapsed.TotalMilliseconds;

        private void checkTimeout()
        {
            if (recordStartTime > 0)
            {
                double elapsed = stopwatch.Elapsed.TotalMilliseconds - recordStartTime;
                if (elapsed > timeout_ms)
                {
                    Logger.Log(string.Format("EzLatency 数据收集超时 ({0:F0}ms)，清除旧数据", elapsed), LoggingTarget.Runtime, LogLevel.Debug);
                    ClearCurrentData();
                }
            }
        }

        public void ClearCurrentData()
        {
            currentInputData = default;
            currentHardwareData = default;
            recordStartTime = 0;
        }
    }

    // --- Service ---------------------------------------------------------------

    public class EzLatencyService
    {
        private static readonly Lazy<EzLatencyService> lazy = new Lazy<EzLatencyService>(() => new EzLatencyService());
        public static EzLatencyService Instance => lazy.Value;
        private EzLatencyService() { }

        public void PushRecord(EzLatencyRecord record)
        {
            try { OnMeasurement?.Invoke(record); } catch (Exception ex) { Logger.Log($"EzLatencyService.PushRecord 异常: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error); }
        }

        public event Action<EzLatencyRecord> OnMeasurement;
    }

    // --- Logger types ---------------------------------------------------------

    public interface IEzLatencyLogger : IDisposable
    {
        void Log(EzLatencyRecord record);
        void Flush();
        event Action<EzLatencyRecord> OnRecord;
    }

    public class EzLoggerAdapter : IEzLatencyLogger
    {
        private readonly Scheduler scheduler;
        private readonly string filePath;
        private StreamWriter fileWriter;
        public event Action<EzLatencyRecord> OnRecord;

        public EzLoggerAdapter(Scheduler scheduler = null, string filePath = null)
        {
            this.scheduler = scheduler;
            this.filePath = filePath;
            if (!string.IsNullOrEmpty(filePath))
            {
                try { fileWriter = new StreamWriter(File.Open(filePath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true }; }
                catch (Exception ex) { Logger.Log($"EzLoggerAdapter: failed to open file {filePath}: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error); }
            }
        }

        public void Log(EzLatencyRecord record)
        {
            try { if (scheduler != null) scheduler.Add(() => OnRecord?.Invoke(record)); else OnRecord?.Invoke(record); } catch { }
            try { string line = $"[{record.Timestamp:O}] {record.MeasuredMs} ms - {record.Note}"; fileWriter?.WriteLine(line); } catch { }
        }

        public void Flush() { try { fileWriter?.Flush(); } catch { } }
        public void Dispose() { try { fileWriter?.Dispose(); } catch { } }
    }

    // --- Interfaces & basic tracker ------------------------------------------

    public interface IEzLatencyTracker : IDisposable
    {
        void Start();
        void Stop();
        event Action<EzLatencyRecord> OnMeasurement;
        void SetSampleRate(int sampleRate);
    }

    public interface IEzLatencyPlayback : IDisposable
    {
        void PlayTestTone();
        void StopTestTone();
        void SetSampleRate(int sampleRate);
    }

    public class BasicEzLatencyTracker : IEzLatencyTracker
    {
        public event Action<EzLatencyRecord> OnMeasurement;
        private readonly EzLatencyAnalyzer analyzer;
        private readonly Action<EzLatencyRecord> recordHandler;

        public BasicEzLatencyTracker()
        {
            analyzer = new EzLatencyAnalyzer();
            recordHandler = record => OnMeasurement?.Invoke(record);
            analyzer.OnNewRecord += recordHandler;
        }

        public void Start() { analyzer.Enabled = true; }
        public void Stop() { analyzer.Enabled = false; }
        public void SetSampleRate(int sampleRate) { }
        public void PushMeasurement(EzLatencyRecord record) => OnMeasurement?.Invoke(record);

        public void Dispose()
        {
            analyzer.Enabled = false;
            if (recordHandler != null) analyzer.OnNewRecord -= recordHandler;
        }

        public void RecordInputData(double inputTime, object keyValue = null) => analyzer.RecordInputData(inputTime, keyValue);
        public void RecordJudgeData(double judgeTime) => analyzer.RecordJudgeData(judgeTime);
        public void RecordPlaybackData(double playbackTime) => analyzer.RecordPlaybackData(playbackTime);
        public void RecordHardwareData(double driverTime, double outputHardwareTime, double inputHardwareTime, double latencyDifference) => analyzer.RecordHardwareData(driverTime, outputHardwareTime, inputHardwareTime, latencyDifference);
    }

    // --- Statistics & Collector ----------------------------------------------

    public class EzLatencyStatistics
    {
        public bool HasData => RecordCount > 0;
        public int RecordCount { get; set; }
        public double AvgInputToJudge { get; set; }
        public double AvgInputToPlayback { get; set; }
        public double AvgPlaybackToJudge { get; set; }
        public double MinInputToJudge { get; set; }
        public double MaxInputToJudge { get; set; }
        public double AvgHardwareLatency { get; set; }
    }

    internal class EzLatencyCollector
    {
        private readonly List<EzLatencyRecord> records = new List<EzLatencyRecord>();
        private readonly object lockObject = new object();

        public void AddRecord(EzLatencyRecord record)
        {
            if (!record.IsComplete)
                return;

            lock (lockObject)
            {
                records.Add(record);
            }
        }

        public void Clear()
        {
            lock (lockObject)
            {
                records.Clear();
            }
        }

        public EzLatencyStatistics GetStatistics()
        {
            lock (lockObject)
            {
                if (records.Count == 0)
                    return new EzLatencyStatistics { RecordCount = 0 };

                var inputToJudge = records.Select(r => (double)(r.JudgeTime - r.InputTime)).ToList();
                var inputToPlayback = records.Select(r => (double)(r.PlaybackTime - r.InputTime)).ToList();
                var playbackToJudge = records.Select(r => (double)(r.JudgeTime - r.PlaybackTime)).ToList();
                var hardwareLatency = records.Select(r => (double)r.OutputHardwareTime).Where(h => h > 0).ToList();

                return new EzLatencyStatistics
                {
                    RecordCount = records.Count,
                    AvgInputToJudge = inputToJudge.Average(),
                    AvgInputToPlayback = inputToPlayback.Average(),
                    AvgPlaybackToJudge = playbackToJudge.Average(),
                    MinInputToJudge = inputToJudge.Min(),
                    MaxInputToJudge = inputToJudge.Max(),
                    AvgHardwareLatency = hardwareLatency.Count > 0 ? hardwareLatency.Average() : 0
                };
            }
        }

        public int Count
        {
            get
            {
                lock (lockObject)
                {
                    return records.Count;
                }
            }
        }
    }

    // --- Hardware timestamp provider (pluggable) -----------------------------

    public interface IHwTimestampProvider
    {
        bool TryGetHardwareTimestamps(int channelHandle, out double driverTimeMs, out double outputHardwareTimeMs, out double inputHardwareTimeMs, out double latencyDifferenceMs);
    }

    public class NullHwTimestampProvider : IHwTimestampProvider
    {
        public bool TryGetHardwareTimestamps(int channelHandle, out double driverTimeMs, out double outputHardwareTimeMs, out double inputHardwareTimeMs, out double latencyDifferenceMs)
        {
            driverTimeMs = outputHardwareTimeMs = inputHardwareTimeMs = latencyDifferenceMs = 0;
            return false;
        }
    }
}
