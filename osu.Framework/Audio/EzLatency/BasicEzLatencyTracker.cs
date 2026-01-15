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

        public void Start() { }
        public void Stop() { }

        public void SetSampleRate(int sampleRate) => this.sampleRate = sampleRate;

        public void PushMeasurement(EzLatencyRecord record) => OnMeasurement?.Invoke(record);

        public void Dispose() { }
    }
}
