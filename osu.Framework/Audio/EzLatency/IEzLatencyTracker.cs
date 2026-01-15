namespace osu.Framework.Audio.EzLatency
{
    #nullable disable
    using System;

    public interface IEzLatencyTracker : IDisposable
    {
        void Start();
        void Stop();
        event Action<EzLatencyRecord> OnMeasurement;
        void SetSampleRate(int sampleRate);
    }
}
