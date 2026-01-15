namespace osu.Framework.Audio.EzLatency
{
    #nullable disable
    using System;

    /// <summary>
    /// Standalone singleton service which collects low-level framework latency records
    /// and exposes higher-level EzLatencyRecord events for consumers in the osu layer.
    /// This keeps AudioManager untouched and centralizes latency collection in one place.
    /// </summary>
    public class EzLatencyService
    {
        private static readonly Lazy<EzLatencyService> lazy = new Lazy<EzLatencyService>(() => new EzLatencyService());

        public static EzLatencyService Instance => lazy.Value;

        private EzLatencyService()
        {
        }

        /// <summary>
        /// Allows producers (framework playback module) to push a fully-populated record
        /// into the service for forwarding to osu layer subscribers.
        /// </summary>
        public void PushRecord(EzLatencyRecord record)
        {
            try
            {
                OnMeasurement?.Invoke(record);
            }
            catch { }
        }

        /// <summary>
        /// Fired when a new high-level latency record is available.
        /// osu! layer should subscribe to this event and decide how to output logs.
        /// </summary>
        public event Action<EzLatencyRecord> OnMeasurement;
    }
}
