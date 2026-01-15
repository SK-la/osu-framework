namespace osu.Framework.Audio.EzLatency
{
    #nullable disable
    using System;

    public interface IEzLatencyPlayback : IDisposable
    {
        void PlayTestTone();
        void StopTestTone();
        void SetSampleRate(int sampleRate);
    }
}
