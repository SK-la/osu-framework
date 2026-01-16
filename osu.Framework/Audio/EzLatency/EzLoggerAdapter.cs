// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Audio.EzLatency
{
#nullable disable
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
                try
                {
                    fileWriter = new StreamWriter(File.Open(filePath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                }
                catch (Exception ex)
                {
                    Logger.Log($"EzLoggerAdapter: failed to open file {filePath}: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                }
            }
        }

        public void Log(EzLatencyRecord record)
        {
            // Raise OnRecord for consumers to handle (osu! will subscribe and use Logger there)
            try
            {
                if (scheduler != null)
                    scheduler.Add(() => OnRecord?.Invoke(record));
                else
                    OnRecord?.Invoke(record);
            }
            catch
            {
            }

            // Also write to file if enabled (diagnostic only)
            try
            {
                string line = $"[{record.Timestamp:O}] {record.MeasuredMs} ms - {record.Note}";
                fileWriter?.WriteLine(line);
            }
            catch
            {
            }
        }

        public void Flush()
        {
            try
            {
                fileWriter?.Flush();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                fileWriter?.Dispose();
            }
            catch { }
        }
    }
}
