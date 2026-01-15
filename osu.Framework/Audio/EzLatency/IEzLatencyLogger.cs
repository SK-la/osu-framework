namespace osu.Framework.Audio.EzLatency
{
    #nullable disable
    using System;

    public interface IEzLatencyLogger : IDisposable
    {
        void Log(EzLatencyRecord record);
        void Flush();

        /// <summary>
        /// Fired whenever a new latency record is logged.
        /// Consumers (e.g. osu! layer) should subscribe to this to perform application-level logging/output.
        /// </summary>
        event Action<EzLatencyRecord> OnRecord;
    }
}
