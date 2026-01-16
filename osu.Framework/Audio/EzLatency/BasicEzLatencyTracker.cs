namespace osu.Framework.Audio.EzLatency
{
    #nullable disable
    using System;

    /// <summary>
    /// Basic tracker that exposes an API to record measurements.
    /// This implementation does not perform audio operations; it merely collects and forwards measurements.
    /// </summary>
    public class BasicEzLatencyTracker : IEzLatencyTracker
    {
        public event Action<EzLatencyRecord> OnMeasurement;

        private int sampleRate = 48000;
        private readonly EzLatencyAnalyzer analyzer;

        public BasicEzLatencyTracker()
        {
            analyzer = new EzLatencyAnalyzer();
            analyzer.OnNewRecord += record => OnMeasurement?.Invoke(record);
        }

        public void Start() 
        { 
            analyzer.Enabled = true;
        }
        
        public void Stop() 
        { 
            analyzer.Enabled = false;
        }

        public void SetSampleRate(int sampleRate) => this.sampleRate = sampleRate;

        public void PushMeasurement(EzLatencyRecord record) => OnMeasurement?.Invoke(record);

        public void Dispose() 
        { 
            analyzer.Enabled = false;
            analyzer.OnNewRecord -= record => OnMeasurement?.Invoke(record);
        }
        
        /// <summary>
        /// 记录输入数据
        /// </summary>
        /// <param name="inputTime">输入时间</param>
        /// <param name="keyValue">按键值</param>
        public void RecordInputData(double inputTime, object keyValue = null)
        {
            analyzer.RecordInputData(inputTime, keyValue);
        }

        /// <summary>
        /// 记录判定数据
        /// </summary>
        /// <param name="judgeTime">判定时间</param>
        public void RecordJudgeData(double judgeTime)
        {
            analyzer.RecordJudgeData(judgeTime);
        }

        /// <summary>
        /// 记录播放数据
        /// </summary>
        /// <param name="playbackTime">播放时间</param>
        public void RecordPlaybackData(double playbackTime)
        {
            analyzer.RecordPlaybackData(playbackTime);
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
            analyzer.RecordHardwareData(driverTime, outputHardwareTime, inputHardwareTime, latencyDifference);
        }
    }
}