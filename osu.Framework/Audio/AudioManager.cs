// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using JetBrains.Annotations;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using osu.Framework.Audio.Asio;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Development;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Audio
{
    public class AudioManager : AudioCollectionManager<AudioComponent>
    {
        /// <summary>
        /// The number of BASS audio devices preceding the first real audio device.
        /// Consisting of <see cref="Bass.NoSoundDevice"/> and <see cref="bass_default_device"/>.
        /// </summary>
        protected const int BASS_INTERNAL_DEVICE_COUNT = 2;

        /// <summary>
        /// The index of the BASS audio device denoting the OS default.
        /// </summary>
        /// <remarks>
        /// See http://www.un4seen.com/doc/#bass/BASS_CONFIG_DEV_DEFAULT.html for more information on the included device.
        /// </remarks>
        private const int bass_default_device = 1;

        /// <summary>
        /// Preferred sample rate for ASIO devices.
        /// This is set by the UI and used during ASIO device initialization.
        /// </summary>
        private static double? preferredAsioSampleRate;

        /// <summary>
        /// The manager component responsible for audio tracks (e.g. songs).
        /// </summary>
        public ITrackStore Tracks => globalTrackStore.Value;

        /// <summary>
        /// The manager component responsible for audio samples (e.g. sound effects).
        /// </summary>
        public ISampleStore Samples => globalSampleStore.Value;

        /// <summary>
        /// The thread audio operations (mainly Bass calls) are ran on.
        /// </summary>
        private readonly AudioThread thread;

        /// <summary>
        /// The global mixer which all tracks are routed into by default.
        /// </summary>
        public readonly AudioMixer TrackMixer;

        /// <summary>
        /// The global mixer which all samples are routed into by default.
        /// </summary>
        public readonly AudioMixer SampleMixer;

        /// <summary>
        /// Configuration for ASIO audio settings.
        /// </summary>
        public readonly AsioConfig AsioConfig = new AsioConfig();

        /// <summary>
        /// The names of all available audio devices.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This property does not contain the names of disabled audio devices.
        /// </para>
        /// <para>
        /// This property may also not necessarily contain the name of the default audio device provided by the OS.
        /// Consumers should provide a "Default" audio device entry which sets <see cref="AudioDevice"/> to an empty string.
        /// </para>
        /// </remarks>
        public IEnumerable<string> AudioDeviceNames => getAudioDeviceEntries();

        /// <summary>
        /// Is fired whenever a new audio device is discovered and provides its name.
        /// </summary>
        public event Action<string> OnNewDevice;

        /// <summary>
        /// Is fired whenever an audio device is lost and provides its name.
        /// </summary>
        public event Action<string> OnLostDevice;

        /// <summary>
        /// The preferred audio device we should use. A value of
        /// <see cref="string.Empty"/> denotes the OS default.
        /// </summary>
        public readonly Bindable<string> AudioDevice = new Bindable<string>();

        /// <summary>
        /// Whether to use experimental WASAPI initialisation on windows.
        /// This generally results in lower audio latency, but also changes the audio synchronisation from
        /// historical expectations, meaning users / application will have to account for different offsets.
        /// </summary>
        public readonly BindableBool UseExperimentalWasapi = new BindableBool();

        /// <summary>
        /// Volume of all samples played game-wide.
        /// </summary>
        public readonly BindableDouble VolumeSample = new BindableDouble(1)
        {
            MinValue = 0,
            MaxValue = 1
        };

        /// <summary>
        /// Volume of all tracks played game-wide.
        /// </summary>
        public readonly BindableDouble VolumeTrack = new BindableDouble(1)
        {
            MinValue = 0,
            MaxValue = 1
        };

        /// <summary>
        /// Whether a global mixer is being used for audio routing.
        /// For now, this is only the case on Windows when using shared mode WASAPI initialisation.
        /// </summary>
        public IBindable<bool> UsingGlobalMixer => usingGlobalMixer;

        private readonly Bindable<bool> usingGlobalMixer = new BindableBool();

        /// <summary>
        /// If a global mixer is being used, this will be the BASS handle for it.
        /// If non-null, all game mixers should be added to this mixer.
        /// </summary>
        /// <remarks>
        /// When this is non-null, all mixers created via <see cref="CreateAudioMixer"/>
        /// will themselves be added to the global mixer, which will handle playback itself.
        ///
        /// In this mode of operation, nested mixers will be created with the <see cref="BassFlags.Decode"/>
        /// flag, meaning they no longer handle playback directly.
        ///
        /// An eventual goal would be to use a global mixer across all platforms as it can result
        /// in more control and better playback performance.
        /// </remarks>
        internal readonly IBindable<int?> GlobalMixerHandle = new Bindable<int?>();

        public override bool IsLoaded => base.IsLoaded &&
                                         // bass default device is a null device (-1), not the actual system default.
                                         Bass.CurrentDevice != Bass.DefaultDevice;

        // Mutated by multiple threads, must be thread safe.
        private ImmutableArray<DeviceInfo> audioDevices = ImmutableArray<DeviceInfo>.Empty;
        private ImmutableList<string> bassDeviceNames = ImmutableList<string>.Empty;

        private static int asioNativeUnavailableLogged;

        private const string type_wasapi_exclusive = "WASAPI Exclusive";
        private const string type_asio = "ASIO";

        private bool syncingSelection;

        private void setUserBindableValueLeaseSafe<T>(Bindable<T> bindable, T newValue)
        {
            if (EqualityComparer<T>.Default.Equals(bindable.Value, newValue))
                return;

            // These bindables are bound into UI (osu!) and can trigger transforms/animations.
            // Ensure mutations happen on the update thread (Game.Scheduler) to avoid cross-thread Drawable mutations.
            if (ThreadSafety.IsUpdateThread || EventScheduler == null)
            {
                setBindableValueLeaseSafe(bindable, newValue);
                return;
            }

            eventScheduler.Add(() => setBindableValueLeaseSafe(bindable, newValue));
        }

        private static void setBindableValueLeaseSafe<T>(Bindable<T> bindable, T newValue)
        {
            if (EqualityComparer<T>.Default.Equals(bindable.Value, newValue))
                return;

            // Bindables may be in a leased state (Disabled=true), in which case Value setter throws.
            // We still want internal state/config to reflect the effective output fallback.
            if (bindable.Disabled)
                bindable.SetValue(bindable.Value, newValue, true);
            else
                bindable.Value = newValue;
        }

        protected enum AudioOutputMode
        {
            Default,
            WasapiShared,
            WasapiExclusive,
            Asio,
        }

        private const string legacy_type_bass = "BASS";
        private const string legacy_type_wasapi_shared = "WASAPI Shared";

        private Scheduler scheduler => thread.Scheduler;

        private Scheduler eventScheduler => EventScheduler ?? scheduler;

        private readonly CancellationTokenSource cancelSource = new CancellationTokenSource();

        /// <summary>
        /// The scheduler used for invoking publicly exposed delegate events.
        /// </summary>
        public Scheduler EventScheduler;

        internal IBindableList<AudioMixer> ActiveMixers => activeMixers;
        private readonly BindableList<AudioMixer> activeMixers = new BindableList<AudioMixer>();

        private readonly Lazy<TrackStore> globalTrackStore;
        private readonly Lazy<SampleStore> globalSampleStore;

        /// <summary>
        /// Sets the preferred sample rate for ASIO devices.
        /// This will be used during ASIO device initialization.
        /// </summary>
        /// <param name="sampleRate">The preferred sample rate in Hz, or null to use default.</param>
        public static void SetPreferredAsioSampleRate(double? sampleRate)
        {
            preferredAsioSampleRate = sampleRate;
        }

        /// <summary>
        /// Sets the preferred sample rate for ASIO devices using the AsioConfig.
        /// This will be used during ASIO device initialization.
        /// </summary>
        /// <param name="sampleRate">The preferred sample rate in Hz.</param>
        public void SetPreferredAsioSampleRate(double sampleRate)
        {
            AsioConfig.SampleRate.Value = sampleRate;
        }

        /// <summary>
        /// Gets the preferred sample rate for ASIO devices.
        /// </summary>
        /// <returns>The preferred sample rate in Hz, or null if not set.</returns>
        public static double? GetPreferredAsioSampleRate()
        {
            return preferredAsioSampleRate;
        }

        /// <summary>
        /// Constructs an AudioStore given a track resource store, and a sample resource store.
        /// </summary>
        /// <param name="audioThread">The host's audio thread.</param>
        /// <param name="trackStore">The resource store containing all audio tracks to be used in the future.</param>
        /// <param name="sampleStore">The sample store containing all audio samples to be used in the future.</param>
        /// <param name="config"></param>
        public AudioManager(AudioThread audioThread, ResourceStore<byte[]> trackStore, ResourceStore<byte[]> sampleStore, [CanBeNull] FrameworkConfigManager config)
        {
            thread = audioThread;

            thread.RegisterManager(this);

            if (config != null)
            {
                // attach config bindables
                config.BindWith(FrameworkSetting.AudioDevice, AudioDevice);
                config.BindWith(FrameworkSetting.AudioUseExperimentalWasapi, UseExperimentalWasapi);
                config.BindWith(FrameworkSetting.VolumeUniversal, Volume);
                config.BindWith(FrameworkSetting.VolumeEffect, VolumeSample);
                config.BindWith(FrameworkSetting.VolumeMusic, VolumeTrack);
            }

            AudioDevice.ValueChanged += _ =>
            {
                if (syncingSelection)
                    return;

                scheduler.AddOnce(initCurrentDevice);
            };
            UseExperimentalWasapi.ValueChanged += e =>
            {
                if (syncingSelection)
                    return;

                // Option (1): when experimental audio is enabled, the dropdown should not expose ASIO/Exclusive entries.
                // Coerce any typed selection back to an allowed value.
                if (UseExperimentalWasapi.Value && RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                {
                    string selection = AudioDevice.Value;

                    if (tryParseSuffixed(selection, type_wasapi_exclusive, out string baseName))
                    {
                        syncingSelection = true;

                        try
                        {
                            setBindableValueLeaseSafe(AudioDevice, baseName);
                        }
                        finally
                        {
                            syncingSelection = false;
                        }
                    }
                    else if (tryParseSuffixed(selection, type_asio, out _))
                    {
                        syncingSelection = true;

                        try
                        {
                            // An ASIO selection is incompatible with experimental(shared) mode.
                            // Fall back to OS default.
                            setBindableValueLeaseSafe(AudioDevice, string.Empty);
                        }
                        finally
                        {
                            syncingSelection = false;
                        }
                    }
                }

                // Shared-mode WASAPI is still controlled by this checkbox.
                // Keep dropdown values as the raw BASS device name to preserve historical UX.
                scheduler.AddOnce(initCurrentDevice);
            };
            // initCurrentDevice not required for changes to `GlobalMixerHandle` as it is only changed when experimental wasapi is toggled (handled above).
            GlobalMixerHandle.ValueChanged += handle => usingGlobalMixer.Value = handle.NewValue.HasValue;

            AddItem(TrackMixer = createAudioMixer(null, nameof(TrackMixer)));
            AddItem(SampleMixer = createAudioMixer(null, nameof(SampleMixer)));

            globalTrackStore = new Lazy<TrackStore>(() =>
            {
                var store = new TrackStore(trackStore, TrackMixer);
                AddItem(store);
                store.AddAdjustment(AdjustableProperty.Volume, VolumeTrack);
                return store;
            });

            globalSampleStore = new Lazy<SampleStore>(() =>
            {
                var store = new SampleStore(sampleStore, SampleMixer);
                AddItem(store);
                store.AddAdjustment(AdjustableProperty.Volume, VolumeSample);
                return store;
            });

            syncAudioDevices();

            // check for changes in any audio devices every 1000ms (slightly expensive operation)
            CancellationToken token = cancelSource.Token;
            scheduler.AddDelayed(() =>
            {
                new Thread(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (CheckForDeviceChanges(audioDevices))
                                syncAudioDevices();
                            Thread.Sleep(1000);
                        }
                        catch
                        {
                        }
                    }
                })
                {
                    IsBackground = true
                }.Start();
            }, 1000);
        }

        protected override void Dispose(bool disposing)
        {
            cancelSource.Cancel();

            thread.UnregisterManager(this);

            OnNewDevice = null;
            OnLostDevice = null;

            base.Dispose(disposing);
        }

        private static int userMixerID;

        /// <summary>
        /// Creates a new <see cref="AudioMixer"/>.
        /// </summary>
        /// <remarks>
        /// Channels removed from this <see cref="AudioMixer"/> fall back to the global <see cref="SampleMixer"/>.
        /// </remarks>
        /// <param name="identifier">An identifier displayed on the audio mixer visualiser.</param>
        public AudioMixer CreateAudioMixer(string identifier = default) => createAudioMixer(SampleMixer, !string.IsNullOrEmpty(identifier) ? identifier : $"user #{Interlocked.Increment(ref userMixerID)}");

        private AudioMixer createAudioMixer(AudioMixer fallbackMixer, string identifier)
        {
            var mixer = new BassAudioMixer(this, fallbackMixer, identifier);
            AddItem(mixer);
            return mixer;
        }

        protected override void ItemAdded(AudioComponent item)
        {
            base.ItemAdded(item);
            if (item is AudioMixer mixer)
                activeMixers.Add(mixer);
        }

        protected override void ItemRemoved(AudioComponent item)
        {
            base.ItemRemoved(item);
            if (item is AudioMixer mixer)
                activeMixers.Remove(mixer);
        }

        /// <summary>
        /// Obtains the <see cref="TrackStore"/> corresponding to a given resource store.
        /// Returns the global <see cref="TrackStore"/> if no resource store is passed.
        /// </summary>
        /// <param name="store">The <see cref="IResourceStore{T}"/> of which to retrieve the <see cref="TrackStore"/>.</param>
        /// <param name="mixer">The <see cref="AudioMixer"/> to use for tracks created by this store. Defaults to the global <see cref="TrackMixer"/>.</param>
        public ITrackStore GetTrackStore(IResourceStore<byte[]> store = null, AudioMixer mixer = null)
        {
            if (store == null) return globalTrackStore.Value;

            TrackStore tm = new TrackStore(store, mixer ?? TrackMixer);
            globalTrackStore.Value.AddItem(tm);
            return tm;
        }

        /// <summary>
        /// Obtains the <see cref="SampleStore"/> corresponding to a given resource store.
        /// Returns the global <see cref="SampleStore"/> if no resource store is passed.
        /// </summary>
        /// <remarks>
        /// By default, <c>.wav</c> and <c>.ogg</c> extensions will be automatically appended to lookups on the returned store
        /// if the lookup does not correspond directly to an existing filename.
        /// Additional extensions can be added via <see cref="ISampleStore.AddExtension"/>.
        /// </remarks>
        /// <param name="store">The <see cref="IResourceStore{T}"/> of which to retrieve the <see cref="SampleStore"/>.</param>
        /// <param name="mixer">The <see cref="AudioMixer"/> to use for samples created by this store. Defaults to the global <see cref="SampleMixer"/>.</param>
        public ISampleStore GetSampleStore(IResourceStore<byte[]> store = null, AudioMixer mixer = null)
        {
            if (store == null) return globalSampleStore.Value;

            SampleStore sm = new SampleStore(store, mixer ?? SampleMixer);
            globalSampleStore.Value.AddItem(sm);
            return sm;
        }

        /// <summary>
        /// (Re-)Initialises BASS for the current <see cref="AudioDevice"/>.
        /// This will automatically fall back to the system default device on failure.
        /// </summary>
        private void initCurrentDevice()
        {
            // Note: normalisation may write back to bindables; ensure those writes are update-thread-safe.
            normaliseLegacySelection();

            var (mode, deviceName, asioIndex) = parseSelection(AudioDevice.Value);

            bool isExplicitSelection = !string.IsNullOrEmpty(AudioDevice.Value);
            bool isTypedSelection = hasTypeSuffix(AudioDevice.Value);

            // keep legacy setting and dropdown selection in sync.
            if (!syncingSelection)
            {
                syncingSelection = true;

                try
                {
                    // Option (1): experimental(shared) mode hides Exclusive/ASIO entries.
                    // If we still see these modes (eg. from config), force experimental off to keep behaviour consistent.
                    if (UseExperimentalWasapi.Value && (mode == AudioOutputMode.WasapiExclusive || mode == AudioOutputMode.Asio))
                        setUserBindableValueLeaseSafe(UseExperimentalWasapi, false);
                }
                finally
                {
                    syncingSelection = false;
                }
            }

            // try using the specified device
            if (mode == AudioOutputMode.Asio)
            {
                // ASIO output still requires BASS to be initialised, but output is performed by BassAsio.
                // Use the OS default BASS device as a fallback initialisation target.
                if (trySetDevice(bass_default_device, mode, asioIndex)) return;
            }
            else
            {
                // try using the specified device
                int deviceIndex = bassDeviceNames.FindIndex(d => d == deviceName);
                if (deviceIndex >= 0 && trySetDevice(BASS_INTERNAL_DEVICE_COUNT + deviceIndex, mode, asioIndex)) return;
            }

            // try using the system default if there is any device present.
            // mobiles are an exception as the built-in speakers may not be provided as an audio device name,
            // but they are still provided by BASS under the internal device name "Default".
            if ((bassDeviceNames.Count > 0 || RuntimeInfo.IsMobile) && trySetDevice(bass_default_device, mode, asioIndex)) return;

            // If an explicit selection failed, revert to Default and try again in default output mode.
            // Keep checkbox state unless Exclusive/ASIO was chosen.
            if (isExplicitSelection)
            {
                if (trySetDevice(bass_default_device, AudioOutputMode.Default, null))
                {
                    revertSelectionToDefault();
                    return;
                }

                // If even default failed, still revert selection (we'll fall through to NoSound).
                revertSelectionToDefault();
            }

            // no audio devices can be used, so try using Bass-provided "No sound" device as last resort.
            trySetDevice(Bass.NoSoundDevice, AudioOutputMode.Default, null);

            // we're boned. even "No sound" device won't initialise.
            return;

            bool trySetDevice(int deviceId, AudioOutputMode outputMode, int? asioDeviceIndex)
            {
                var device = audioDevices.ElementAtOrDefault(deviceId);

                // device is invalid
                if (!device.IsEnabled)
                    return false;

                // we don't want bass initializing with real audio device on headless test runs.
                if (deviceId != Bass.NoSoundDevice && DebugUtils.IsNUnitRunning)
                    return false;

                // initialize new device
                if (!InitBass(deviceId, outputMode, asioDeviceIndex))
                    return false;

                //we have successfully initialised a new device.
                UpdateDevice(deviceId);

                return true;
            }

            void revertSelectionToDefault()
            {
                if (syncingSelection)
                    return;

                syncingSelection = true;

                try
                {
                    // Ensure "Default" means OS default device.
                    // Preserve shared-WASAPI checkbox unless an exclusive/ASIO entry was explicitly selected.
                    if (isTypedSelection || mode == AudioOutputMode.WasapiExclusive || mode == AudioOutputMode.Asio)
                        setUserBindableValueLeaseSafe(UseExperimentalWasapi, false);

                    setUserBindableValueLeaseSafe(AudioDevice, string.Empty);
                }
                finally
                {
                    syncingSelection = false;
                }
            }
        }

        private void normaliseLegacySelection()
        {
            if (syncingSelection)
                return;

            string selection = AudioDevice.Value;
            if (string.IsNullOrEmpty(selection))
                return;

            // Earlier iterations stored typed entries for BASS/Shared WASAPI directly in AudioDevice.
            // The dropdown now shows raw device names (plus appended Exclusive/ASIO), so rewrite old values.
            if (tryParseSuffixed(selection, legacy_type_bass, out string baseName))
            {
                syncingSelection = true;

                try
                {
                    setUserBindableValueLeaseSafe(AudioDevice, baseName);
                }
                finally
                {
                    syncingSelection = false;
                }

                return;
            }

            if (tryParseSuffixed(selection, legacy_type_wasapi_shared, out baseName))
            {
                syncingSelection = true;

                try
                {
                    setUserBindableValueLeaseSafe(AudioDevice, baseName);
                    setUserBindableValueLeaseSafe(UseExperimentalWasapi, true);
                }
                finally
                {
                    syncingSelection = false;
                }
            }
        }

        private static bool hasTypeSuffix(string value)
            => value.EndsWith($" ({type_wasapi_exclusive})", StringComparison.Ordinal)
               || value.EndsWith($" ({type_asio})", StringComparison.Ordinal);

        /// <summary>
        /// This method calls <see cref="Bass.Init(int, int, DeviceInitFlags, IntPtr, IntPtr)"/>.
        /// It can be overridden for unit testing.
        /// </summary>
        /// <param name="device">The device to initialise.</param>
        /// <param name="outputMode">The output mode to use for playback.</param>
        /// <param name="asioDeviceIndex">When <paramref name="outputMode"/> is ASIO, the selected ASIO device index.</param>
        protected virtual bool InitBass(int device, AudioOutputMode outputMode, int? asioDeviceIndex)
        {
            // this likely doesn't help us but also doesn't seem to cause any issues or any cpu increase.
            Bass.UpdatePeriod = 5;

            // reduce latency to a known sane minimum.
            Bass.DeviceBufferLength = 10;
            Bass.PlaybackBufferLength = 100;

            // ensure there are no brief delays on audio operations (causing stream stalls etc.) after periods of silence.
            Bass.DeviceNonStop = true;

            // without this, if bass falls back to directsound legacy mode the audio playback offset will be way off.
            Bass.Configure(ManagedBass.Configuration.TruePlayPosition, 0);

            // Set BASS_IOS_SESSION_DISABLE here to leave session configuration in our hands (see iOS project).
            Bass.Configure(ManagedBass.Configuration.IOSSession, 16);

            // Always provide a default device. This should be a no-op, but we have asserts for this behaviour.
            Bass.Configure(ManagedBass.Configuration.IncludeDefaultDevice, true);

            // Enable custom BASS_CONFIG_MP3_OLDGAPS flag for backwards compatibility.
            // - This disables support for ItunSMPB tag parsing to match previous expectations.
            // - This also disables a change which assumes a 529 sample (2116 byte in stereo 16-bit) delay if the MP3 file doesn't specify one.
            //   (That was added in Bass for more consistent results across platforms and standard/mp3-free BASS versions, because OSX/iOS's MP3 decoder always removes 529 samples)
            // Bass.Configure((ManagedBass.Configuration)68, 1);

            // Disable BASS_CONFIG_DEV_TIMEOUT flag to keep BASS audio output from pausing on device processing timeout.
            // See https://www.un4seen.com/forum/?topic=19601 for more information.
            Bass.Configure((ManagedBass.Configuration)70, false);

            bool attemptInit()
            {
                bool innerSuccess;

                try
                {
                    innerSuccess = thread.InitDevice(device, toThreadOutputMode(outputMode), asioDeviceIndex, outputMode == AudioOutputMode.Asio ? (double?)AsioConfig.SampleRate.Value : null);
                }
                catch (Exception e)
                {
                    Logger.Log($"Audio device initialisation threw an exception (mode: {outputMode}, device: {device}): {e}", name: "audio", level: LogLevel.Error);
                    return false;
                }

                // For ASIO mode, if InitDevice fails, we should always return false to trigger fallback
                // even if BASS was successfully initialized, because ASIO is what the user requested
                if (outputMode == AudioOutputMode.Asio && !innerSuccess)
                {
                    Logger.Log("ASIO device initialization failed, falling back to default audio", name: "audio", level: LogLevel.Important);
                    return false;
                }

                bool alreadyInitialised = Bass.LastError == Errors.Already;

                if (alreadyInitialised)
                {
                    // For ASIO mode, even if BASS is already initialized, we need to ensure ASIO was properly initialized
                    if (outputMode == AudioOutputMode.Asio && !innerSuccess)
                    {
                        Logger.Log("ASIO initialization failed even though BASS was already initialized", name: "audio", level: LogLevel.Error);
                        return false;
                    }
                    return true;
                }

                if (BassUtils.CheckFaulted(false))
                    return false;

                if (!innerSuccess)
                {
                    Logger.Log("BASS failed to initialize but did not provide an error code", name: "audio", level: LogLevel.Error);
                    return false;
                }

                var deviceInfo = audioDevices.ElementAtOrDefault(device);

                Logger.Log($@"🔈 BASS initialised
                          BASS version:           {Bass.Version}
                          BASS FX version:        {BassFx.Version}
                          BASS MIX version:       {BassMix.Version}
                          Device:                 {deviceInfo.Name}
                          Driver:                 {deviceInfo.Driver}
                          Update period:          {Bass.UpdatePeriod} ms
                          Device buffer length:   {Bass.DeviceBufferLength} ms
                          Playback buffer length: {Bass.PlaybackBufferLength} ms");

                return true;
            }

            return attemptInit();
        }

        private static AudioThread.AudioThreadOutputMode toThreadOutputMode(AudioOutputMode mode)
        {
            switch (mode)
            {
                case AudioOutputMode.WasapiShared:
                    return AudioThread.AudioThreadOutputMode.WasapiShared;

                case AudioOutputMode.WasapiExclusive:
                    return AudioThread.AudioThreadOutputMode.WasapiExclusive;

                case AudioOutputMode.Asio:
                    return AudioThread.AudioThreadOutputMode.Asio;

                default:
                    return AudioThread.AudioThreadOutputMode.Default;
            }
        }

        private void syncAudioDevices()
        {
            audioDevices = GetAllDevices();

            // Bass should always be providing "No sound" and "Default" device.
            Trace.Assert(audioDevices.Length >= BASS_INTERNAL_DEVICE_COUNT, "Bass did not provide any audio devices.");

            var oldDeviceNames = bassDeviceNames;
            var newDeviceNames = bassDeviceNames = audioDevices.Skip(BASS_INTERNAL_DEVICE_COUNT).Where(d => d.IsEnabled).Select(d => d.Name).ToImmutableList();

            scheduler.Add(() =>
            {
                if (cancelSource.IsCancellationRequested)
                    return;

                if (!IsCurrentDeviceValid())
                    initCurrentDevice();
            }, false);

            var newDevices = newDeviceNames.Except(oldDeviceNames).ToList();
            var lostDevices = oldDeviceNames.Except(newDeviceNames).ToList();

            if (newDevices.Count > 0 || lostDevices.Count > 0)
            {
                eventScheduler.Add(delegate
                {
                    foreach (string d in newDevices)
                        OnNewDevice?.Invoke(d);
                    foreach (string d in lostDevices)
                        OnLostDevice?.Invoke(d);
                });
            }
        }

        private IEnumerable<string> getAudioDeviceEntries()
        {
            var entries = new List<string>();

            // Base BASS devices (historical UX: raw device names).
            entries.AddRange(bassDeviceNames);

            if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
            {
                // Option (1): when experimental(shared) audio is enabled, do not expose Exclusive/ASIO entries.
                if (UseExperimentalWasapi.Value)
                    return entries;

                // Only append extra entries; shared WASAPI remains controlled by the checkbox.
                entries.AddRange(bassDeviceNames.Select(d => formatEntry(d, type_wasapi_exclusive)));

                // ASIO drivers.
                try
                {
                    int asioCount = 0;
                    foreach (var device in BassAsioPI.EnumerateDevices())
                    {
                        entries.Add(formatEntry(device.Name, type_asio));
                        asioCount++;
                    }
                    Logger.Log($"Found {asioCount} ASIO devices", name: "audio", level: LogLevel.Verbose);
                }
                catch (DllNotFoundException e)
                {
                    logAsioNativeUnavailableOnce(e);
                }
                catch (EntryPointNotFoundException e)
                {
                    logAsioNativeUnavailableOnce(e);
                }
                catch
                {
                    // ASIO is optional and may not be available.
                }
            }

            return entries;
        }

        private static string formatEntry(string name, string type) => $"{name} ({type})";

        private (AudioOutputMode mode, string deviceName, int? asioDeviceIndex) parseSelection(string selection)
        {
            // Default device.
            if (string.IsNullOrEmpty(selection))
            {
                return (UseExperimentalWasapi.Value && RuntimeInfo.OS == RuntimeInfo.Platform.Windows
                    ? AudioOutputMode.WasapiShared
                    : AudioOutputMode.Default, string.Empty, null);
            }

            // Option (1): if experimental(shared) is enabled, typed entries should be treated as legacy/config leftovers.
            // Coerce them back to shared mode.
            if (UseExperimentalWasapi.Value && RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
            {
                if (tryParseSuffixed(selection, type_wasapi_exclusive, out string baseName))
                    return (AudioOutputMode.WasapiShared, baseName, null);

                if (tryParseSuffixed(selection, type_asio, out _))
                    return (AudioOutputMode.WasapiShared, string.Empty, null);
            }

            if (tryParseSuffixed(selection, type_wasapi_exclusive, out string name))
                return (AudioOutputMode.WasapiExclusive, name, null);

            if (tryParseSuffixed(selection, type_asio, out name))
            {
                int? index = null;

                if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                {
                    try
                    {
                        if (BassAsioPI.TryFindDeviceIndexByName(name, out int found))
                            index = found;
                    }
                    catch (DllNotFoundException e)
                    {
                        logAsioNativeUnavailableOnce(e);
                    }
                    catch (EntryPointNotFoundException e)
                    {
                        logAsioNativeUnavailableOnce(e);
                    }
                    catch
                    {
                    }
                }

                return (AudioOutputMode.Asio, name, index);
            }

            // Legacy value (raw BASS device name). Keep old behaviour: the experimental flag decides shared WASAPI.
            if (UseExperimentalWasapi.Value && RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                return (AudioOutputMode.WasapiShared, selection, null);

            return (AudioOutputMode.Default, selection, null);
        }

        private static bool tryParseSuffixed(string value, string type, out string baseName)
        {
            string suffix = $" ({type})";

            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                baseName = value[..^suffix.Length];
                return true;
            }

            baseName = string.Empty;
            return false;
        }

        private static void logAsioNativeUnavailableOnce(Exception e)
        {
            if (Interlocked.Exchange(ref asioNativeUnavailableLogged, 1) == 1)
                return;

            // Keep message actionable but non-intrusive (no UI popups).
            Logger.Log($"ASIO output is unavailable because the native bassasio library could not be loaded ({e.GetType().Name}: {e.Message}). Ensure bassasio.dll is present alongside other BASS native libraries (typically in the x64/x86 subdirectories; the BASS.ASIO NuGet package will copy it automatically when referenced).", name: "audio", level: LogLevel.Error);
        }

        /// <summary>
        /// Check whether any audio device changes have occurred.
        ///
        /// Changes supported are:
        /// - A new device is added
        /// - An existing device is Enabled/Disabled or set as Default
        /// </summary>
        /// <remarks>
        /// This method is optimised to incur the lowest overhead possible.
        /// </remarks>
        /// <param name="previousDevices">The previous audio devices array.</param>
        /// <returns>Whether a change was detected.</returns>
        protected virtual bool CheckForDeviceChanges(ImmutableArray<DeviceInfo> previousDevices)
        {
            int deviceCount = Bass.DeviceCount;

            if (previousDevices.Length != deviceCount)
                return true;

            for (int i = 0; i < deviceCount; i++)
            {
                var prevInfo = previousDevices[i];

                Bass.GetDeviceInfo(i, out var info);

                if (info.IsEnabled != prevInfo.IsEnabled)
                    return true;

                if (info.IsDefault != prevInfo.IsDefault)
                    return true;
            }

            return false;
        }

        protected virtual ImmutableArray<DeviceInfo> GetAllDevices()
        {
            int deviceCount = Bass.DeviceCount;

            var devices = ImmutableArray.CreateBuilder<DeviceInfo>(deviceCount);
            for (int i = 0; i < deviceCount; i++)
                devices.Add(Bass.GetDeviceInfo(i));

            return devices.MoveToImmutable();
        }

        // The current device is considered valid if it is enabled, initialized, and not a fallback device.
        protected virtual bool IsCurrentDeviceValid()
        {
            var device = audioDevices.ElementAtOrDefault(Bass.CurrentDevice);
            var (mode, selectedName, _) = parseSelection(AudioDevice.Value);

            // ASIO output selection does not map to a BASS device name; just ensure we're initialised.
            if (mode == AudioOutputMode.Asio)
                return device.IsEnabled && device.IsInitialized;

            bool isFallback = string.IsNullOrEmpty(selectedName) ? !device.IsDefault : device.Name != selectedName;
            return device.IsEnabled && device.IsInitialized && !isFallback;
        }

        public override string ToString()
        {
            string deviceName = audioDevices.ElementAtOrDefault(Bass.CurrentDevice).Name;
            return $@"{GetType().ReadableName()} ({deviceName ?? "Unknown"})";
        }
    }
}
