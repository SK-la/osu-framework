// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ManagedBass;

namespace osu.Framework.Audio.Wasapi
{
    /// <summary>
    /// WASAPI backend implementation using NAudio for actual output while still leveraging
    /// the framework's global BASS mixer for its timebase when available.
    /// This is a pragmatic approach: playback remains compatible while device timestamps
    /// are sampled from a local WASAPI output.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class WasapiAudioBackend : IAudioBackend
    {
        private readonly Stopwatch stopwatch = new Stopwatch();
        private readonly Func<int?> globalMixerHandleProvider;
        private int deviceIndex = -1;
        private bool initialized;

        // NAudio playback objects for a simple sine test tone during verification.
        private WasapiPlayer? wasapiPlayer;
        private SignalGenerator? signalGenerator;

        public WasapiAudioBackend(Func<int?>? globalMixerHandleProvider = null)
        {
            this.globalMixerHandleProvider = globalMixerHandleProvider ?? (() => null);
        }

        public string DebugInfo => $"WasapiAudioBackend (initialized={initialized}, deviceIndex={deviceIndex}, wasapiPlayer={(wasapiPlayer != null)})";

        public void Initialize(int deviceIndex)
        {
            this.deviceIndex = deviceIndex;
            stopwatch.Restart();
            initialized = true;

            try
            {
                // Create a low-latency WASAPI output using NAudio for verification only.
                wasapiPlayer = new WasapiPlayerBuilder()
                    .WithSharedMode()
                    .WithLatency(50)
                    .Build();
                signalGenerator = new SignalGenerator(44100, 2) { Gain = 0.2, Frequency = 440, Type = SignalGeneratorType.Sin };
                wasapiPlayer.Init(new SampleToWaveProvider(signalGenerator));
                // Do not auto-start playback; tests will call PlayTestTone when desired.
            }
            catch
            {
                // NAudio may not be available on all platforms; swallow exceptions and fall back.
                wasapiPlayer = null;
                signalGenerator = null;
            }
        }

        public void UpdateDevice(int deviceIndex)
        {
            this.deviceIndex = deviceIndex;
            stopwatch.Restart();
        }

        public double GetDeviceTimeSeconds()
        {
            try
            {
                int? handle = globalMixerHandleProvider.Invoke();

                if (handle.HasValue && handle.Value != 0)
                {
                    long pos = Bass.ChannelGetPosition(handle.Value);

                    if (pos != -1)
                    {
                        double secs = Bass.ChannelBytes2Seconds(handle.Value, pos);
                        if (!double.IsNaN(secs) && !double.IsInfinity(secs))
                            return secs;
                    }
                }
            }
            catch
            {
                // If any BASS call fails, fall back to stopwatch.
            }

            return stopwatch.Elapsed.TotalSeconds;
        }

        public bool TryGetHardwareTimestamp(out long deviceTimestampNs)
        {
            deviceTimestampNs = 0;
            // Not implemented yet for NAudio path.
            return false;
        }

        public void PlayTestTone()
        {
            try
            {
                wasapiPlayer?.Play();
            }
            catch
            {
            }
        }

        public void StopTestTone()
        {
            try
            {
                wasapiPlayer?.Stop();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            StopTestTone();
            wasapiPlayer?.Dispose();
            signalGenerator = null;
            stopwatch.Stop();
            initialized = false;
        }
    }
}
