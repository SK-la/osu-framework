// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Statistics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;
using ManagedBass.Asio;
using osu.Framework.Audio.Asio;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Logging;
using osu.Framework.Platform.Linux.Native;

namespace osu.Framework.Threading
{
    public class AudioThread : GameThread
    {
        private static int wasapiNativeUnavailableLogged;
        private static int asioResolverRegistered;

        private const uint SEM_FAILCRITICALERRORS = 0x0001;
        private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
        private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint mode);

        public AudioThread()
            : base(name: "Audio")
        {
            // Ensure this AudioThread instance is always reachable from native WASAPI callbacks.
            wasapiUserHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            wasapiUserPtr = GCHandle.ToIntPtr(wasapiUserHandle);

            OnNewFrame += onNewFrame;
            PreloadBass();
        }

        public override bool IsCurrent => ThreadSafety.IsAudioThread;

        internal sealed override void MakeCurrent()
        {
            base.MakeCurrent();

            ThreadSafety.IsAudioThread = true;
        }

        internal override IEnumerable<StatisticsCounterType> StatisticsCounters => new[]
        {
            StatisticsCounterType.TasksRun,
            StatisticsCounterType.Tracks,
            StatisticsCounterType.Samples,
            StatisticsCounterType.SChannels,
            StatisticsCounterType.Components,
            StatisticsCounterType.MixChannels,
        };

        private readonly List<AudioManager> managers = new List<AudioManager>();

        private static readonly HashSet<int> initialised_devices = new HashSet<int>();

        private static readonly GlobalStatistic<double> cpu_usage = GlobalStatistics.Get<double>("Audio", "Bass CPU%");

        private long frameCount;

        private void onNewFrame()
        {
            if (frameCount++ % 1000 == 0)
                cpu_usage.Value = Bass.CPUUsage;

            lock (managers)
            {
                for (int i = 0; i < managers.Count; i++)
                {
                    var m = managers[i];
                    m.Update();
                }
            }
        }

        internal void RegisterManager(AudioManager manager)
        {
            lock (managers)
            {
                if (managers.Contains(manager))
                    throw new InvalidOperationException($"{manager} was already registered");

                managers.Add(manager);
            }

            // Set the manager reference for event triggering
            Manager ??= manager;

            manager.GlobalMixerHandle.BindTo(globalMixerHandle);
        }

        internal void UnregisterManager(AudioManager manager)
        {
            lock (managers)
                managers.Remove(manager);

            manager.GlobalMixerHandle.UnbindFrom(globalMixerHandle);
        }

        protected override void OnExit()
        {
            base.OnExit();

            lock (managers)
            {
                // AudioManagers are iterated over backwards since disposal will unregister and remove them from the list.
                for (int i = managers.Count - 1; i >= 0; i--)
                {
                    var m = managers[i];

                    m.Dispose();

                    // Audio component disposal (including the AudioManager itself) is scheduled and only runs when the AudioThread updates.
                    // But the AudioThread won't run another update since it's exiting, so an update must be performed manually in order to finish the disposal.
                    m.Update();
                }

                managers.Clear();
            }

            // Safety net to ensure we have freed all devices before exiting.
            // This is mainly required for device-lost scenarios.
            // See https://github.com/ppy/osu-framework/pull/3378 for further discussion.
            foreach (int d in initialised_devices.ToArray())
                FreeDevice(d);

            if (wasapiUserHandle.IsAllocated)
                wasapiUserHandle.Free();
        }

        #region BASS Initialisation

        // TODO: All this bass init stuff should probably not be in this class.

        // WASAPI callbacks must never be allowed to be GC'd while native code may still call into them.
        // Use static delegates with a stable user pointer back to this thread instance.
        private static readonly WasapiProcedure wasapiProcedureStatic = (buffer, length, user) =>
        {
            var thread = getWasapiOwner(user);
            if (thread == null)
                return 0;

            int? mixer = thread.globalMixerHandle.Value;
            if (mixer == null)
                return 0;

            return Bass.ChannelGetData(mixer.Value, buffer, length);
        };

        private static readonly WasapiNotifyProcedure wasapiNotifyProcedureStatic = (notify, device, user) =>
        {
            var thread = getWasapiOwner(user);
            if (thread == null)
                return;

            thread.Scheduler.Add(() =>
            {
                if (notify == WasapiNotificationType.DefaultOutput)
                {
                    thread.freeWasapi();
                    thread.initWasapi(device, thread.wasapiExclusiveActive);
                }
            });
        };

        private static AudioThread? getWasapiOwner(IntPtr user)
        {
            if (user == IntPtr.Zero)
                return null;

            try
            {
                return (AudioThread?)GCHandle.FromIntPtr(user).Target;
            }
            catch
            {
                return null;
            }
        }

        private readonly GCHandle wasapiUserHandle;
        private readonly IntPtr wasapiUserPtr;
        private bool wasapiExclusiveActive;

        /// <summary>
        /// Reference to the AudioManager that owns this AudioThread.
        /// Used to trigger events when ASIO devices are initialized.
        /// </summary>
        internal AudioManager? Manager { get; set; }

        /// <summary>
        /// If a global mixer is being used, this will be the BASS handle for it.
        /// If non-null, all game mixers should be added to this mixer.
        /// </summary>
        private readonly Bindable<int?> globalMixerHandle = new Bindable<int?>();

        internal bool InitDevice(int deviceId, AudioThreadOutputMode outputMode, int? asioDeviceIndex = null, double? preferredSampleRate = null)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);
            Trace.Assert(deviceId != -1); // The real device ID should always be used, as the -1 device has special cases which are hard to work with.

            // Important: stop any existing output first.
            // In particular, WASAPI exclusive can hold the device such that a subsequent Bass.Init() returns Busy.
            // If we can't initialise BASS, we also won't get a chance to clean up the previous output mode.
            freeAsio();
            freeWasapi();

            // For ASIO mode, add extra delay before initialization to ensure device is fully released
            if (outputMode == AudioThreadOutputMode.Asio)
            {
                Logger.Log("ASIO mode detected, adding extra delay before device initialization", name: "audio", level: LogLevel.Debug);
                System.Threading.Thread.Sleep(300);
            }

            // Try to initialise the device, or request a re-initialise.
            // 128 == BASS_DEVICE_REINIT. Only use it when the device is already initialised.
            var initFlags = initialised_devices.Contains(deviceId) ? (DeviceInitFlags)128 : 0;
            if (!Bass.Init(deviceId, Flags: initFlags))
            {
                Logger.Log($"BASS.Init({deviceId}) failed: {Bass.LastError}", name: "audio", level: LogLevel.Error);
                return false;
            }

            switch (outputMode)
            {
                case AudioThreadOutputMode.Default:
                    break;

                case AudioThreadOutputMode.WasapiShared:
                    if (!attemptWasapiInitialisation(deviceId, exclusive: false))
                    {
                        Logger.Log($"BassWasapi initialisation failed (shared mode). BASS error: {Bass.LastError}", name: "audio", level: LogLevel.Error);
                        return false;
                    }

                    break;

                case AudioThreadOutputMode.WasapiExclusive:
                    if (!attemptWasapiInitialisation(deviceId, exclusive: true))
                    {
                        Logger.Log($"BassWasapi initialisation failed (exclusive mode). BASS error: {Bass.LastError}", name: "audio", level: LogLevel.Error);
                        return false;
                    }

                    break;

                case AudioThreadOutputMode.Asio:
                    if (asioDeviceIndex == null)
                    {
                        Logger.Log("ASIO output mode selected but no ASIO device index was provided.", name: "audio", level: LogLevel.Error);
                        return false;
                    }

                    if (!initAsio(asioDeviceIndex.Value, preferredSampleRate))
                        return false;

                    break;
            }

            initialised_devices.Add(deviceId);
            return true;
        }

        internal void FreeDevice(int deviceId)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);

            int selectedDevice = Bass.CurrentDevice;

            if (canSelectDevice(deviceId))
            {
                Bass.CurrentDevice = deviceId;
                Bass.Free();
            }

            freeAsio();
            freeWasapi();

            if (selectedDevice != deviceId && canSelectDevice(selectedDevice))
                Bass.CurrentDevice = selectedDevice;

            initialised_devices.Remove(deviceId);

            static bool canSelectDevice(int deviceId) => Bass.GetDeviceInfo(deviceId, out var deviceInfo) && deviceInfo.IsInitialized;
        }

        /// <summary>
        /// Makes BASS available to be consumed.
        /// </summary>
        internal static void PreloadBass()
        {
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                registerBassAsioResolver();

            if (RuntimeInfo.OS == RuntimeInfo.Platform.Linux)
            {
                // required for the time being to address libbass_fx.so load failures (see https://github.com/ppy/osu/issues/2852)
                Library.Load("libbass.so", Library.LoadFlags.RTLD_LAZY | Library.LoadFlags.RTLD_GLOBAL);
            }
        }

        private static void registerBassAsioResolver()
        {
            if (System.Threading.Interlocked.Exchange(ref asioResolverRegistered, 1) == 1)
                return;

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(ManagedBass.Asio.BassAsio).Assembly, resolveBassAsio);
            }
            catch (InvalidOperationException)
            {
                // Resolver was already registered elsewhere.
            }
        }

        private static IntPtr resolveBassAsio(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return IntPtr.Zero;

            if (!libraryName.Equals("bassasio", StringComparison.OrdinalIgnoreCase)
                && !libraryName.Equals("bassasio.dll", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            // Prefer loading from known output directories (x64/x86). This avoids relying on the default DLL search path,
            // which doesn't include these subdirectories, and also prevents Windows from showing error dialogs.
            string baseDir = RuntimeInfo.StartupDirectory;
            string arch = IntPtr.Size == 8 ? "x64" : "x86";
            string runtimeRid = IntPtr.Size == 8 ? "win-x64" : "win-x86";

            string[] candidatePaths =
            {
                Path.Combine(baseDir, "runtimes", runtimeRid, "native", "bassasio.dll"),
                Path.Combine(baseDir, arch, "bassasio.dll"),
            };

            foreach (string path in candidatePaths)
            {
                if (!File.Exists(path))
                    continue;

                uint oldMode = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);

                try
                {
                    return NativeLibrary.Load(path);
                }
                finally
                {
                    SetErrorMode(oldMode);
                }
            }

            // Throw to avoid the runtime falling back to default resolution, which may trigger a Windows error dialog.
            throw new DllNotFoundException($"bassasio.dll was not found in expected locations. Checked: {string.Join("; ", candidatePaths)}");
        }

        private bool attemptWasapiInitialisation() => attemptWasapiInitialisation(Bass.CurrentDevice, exclusive: false);

        private bool attemptWasapiInitialisation(int bassDeviceId, bool exclusive)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return false;

            try
            {
                return attemptWasapiInitialisationInternal(bassDeviceId, exclusive);
            }
            catch (DllNotFoundException e)
            {
                logWasapiNativeUnavailableOnce($"WASAPI output is unavailable because basswasapi.dll could not be loaded ({e.Message}).");
                return false;
            }
            catch (EntryPointNotFoundException e)
            {
                logWasapiNativeUnavailableOnce($"WASAPI output is unavailable because basswasapi.dll is incompatible/mismatched ({e.Message}).");
                return false;
            }
            catch (Exception e)
            {
                Logger.Log($"WASAPI initialisation failed with exception: {e}", name: "audio", level: LogLevel.Error);
                return false;
            }
        }

        private bool attemptWasapiInitialisationInternal(int bassDeviceId, bool exclusive)
        {
            Logger.Log($"Attempting local BassWasapi initialisation (exclusive: {exclusive})", name: "audio", level: LogLevel.Verbose);

            int wasapiDevice = -1;

            // WASAPI device indices don't match normal BASS devices.
            // Each device is listed multiple times with each supported channel/frequency pair.
            //
            // Working backwards to find the correct device is how bass does things internally (see BassWasapi.GetBassDevice).
            if (bassDeviceId > 0)
            {
                string driver = Bass.GetDeviceInfo(bassDeviceId).Driver;

                if (!string.IsNullOrEmpty(driver))
                {
                    var candidates = new List<(int index, int freq, int chans)>();

                    // WASAPI device indices don't match normal BASS devices.
                    // Each device is listed multiple times with each supported channel/frequency pair.
                    //
                    // Working backwards to find the correct device is how bass does things internally (see BassWasapi.GetBassDevice).
                    // We replicate this by scanning the full list and remembering the last match.
                    for (int i = 0; i < 16384; i++)
                    {
                        if (!BassWasapi.GetDeviceInfo(i, out WasapiDeviceInfo info))
                            break;

                        // Only consider output devices (not input/loopback), since we're initialising audio output.
                        if (info.ID == driver && info.IsEnabled && !info.IsInput && !info.IsLoopback)
                            candidates.Add((i, info.MixFrequency, info.MixChannels));
                    }

                    if (candidates.Count > 0)
                    {
                        // Prefer common stereo formats.
                        var best = candidates
                                   .OrderBy(c => c.chans == 2 ? 0 : 1)
                                   .ThenBy(c => Math.Abs(c.freq - 48000))
                                   .ThenBy(c => Math.Abs(c.freq - 44100))
                                   .First();

                        wasapiDevice = best.index;
                        Logger.Log($"Mapped BASS device {bassDeviceId} (driver '{driver}') to WASAPI device {wasapiDevice} (mix: {best.freq}Hz/{best.chans}ch).", name: "audio", level: LogLevel.Verbose);

                        // In exclusive mode, the chosen (freq/chans) pair matters. If the preferred candidate fails,
                        // we will retry other candidates below.
                        if (exclusive)
                        {
                            foreach (var candidate in candidates
                                                     .OrderBy(c => c.chans == 2 ? 0 : 1)
                                                     .ThenBy(c => Math.Abs(c.freq - 48000))
                                                     .ThenBy(c => Math.Abs(c.freq - 44100)))
                            {
                                freeWasapi();

                                if (initWasapi(candidate.index, exclusive))
                                    return true;
                            }

                            Logger.Log($"All WASAPI exclusive format candidates failed for BASS device {bassDeviceId} (driver '{driver}').", name: "audio", level: LogLevel.Verbose);
                            return false;
                        }
                    }
                    else
                    {
                        // If the user selected a specific non-default device, do not fall back to system default.
                        // Fallback would likely be busy (e.g. browser playing on default), and would mask the real issue.
                        if (bassDeviceId != Bass.DefaultDevice)
                        {
                            Logger.Log($"Could not map BASS device {bassDeviceId} (driver '{driver}') to a WASAPI output device; refusing to fall back to default (-1).", name: "audio", level: LogLevel.Verbose);
                            return false;
                        }

                        Logger.Log($"Could not map BASS default device (driver '{driver}') to a WASAPI output device; falling back to default WASAPI device (-1).", name: "audio", level: LogLevel.Verbose);
                    }
                }
                else
                {
                    Logger.Log($"BASS device {bassDeviceId} did not provide a driver identifier; falling back to default WASAPI device (-1).", name: "audio", level: LogLevel.Verbose);
                }
            }

            if (wasapiDevice == -1)
                Logger.Log("Using default WASAPI device (-1).", name: "audio", level: LogLevel.Verbose);

            // To keep things in a sane state let's only keep one device initialised via wasapi.
            freeWasapi();
            return initWasapi(wasapiDevice, exclusive);
        }

        private bool initWasapi(int wasapiDevice, bool exclusive)
        {
            try
            {
                wasapiExclusiveActive = exclusive;

                // BASSWASAPI flags:
                // - 0x1  = EXCLUSIVE
                // - 0x10 = EVENT (event-driven)
                // ManagedBass bindings used here do not currently expose Exclusive, so we use the documented value.
                const WasapiInitFlags exclusiveFlag = (WasapiInitFlags)0x1;

                int requestedFrequency = 0;
                int requestedChannels = 0;

                // Shared mode can use event-driven callbacks and auto-format.
                // Exclusive mode should use an explicit supported format (freq/chans) from the chosen WASAPI entry.
                var flags = (WasapiInitFlags)0;

                if (exclusive)
                {
                    flags |= exclusiveFlag;

                    if (wasapiDevice >= 0 && BassWasapi.GetDeviceInfo(wasapiDevice, out WasapiDeviceInfo selectedInfo))
                    {
                        requestedFrequency = selectedInfo.MixFrequency;
                        requestedChannels = selectedInfo.MixChannels;
                    }
                }
                else
                {
                    flags |= WasapiInitFlags.EventDriven | WasapiInitFlags.AutoFormat;
                }

                // Important: in exclusive mode, the underlying implementation may not support event-driven callbacks
                // and can fall back to polling. Using a near-zero period (float.Epsilon) can then cause a busy-loop,
                // leading to time running far too fast (and eventual instability elsewhere).
                float bufferSeconds = exclusive ? 0.05f : 0f;
                float periodSeconds = exclusive ? 0.01f : float.Epsilon;

                bool initialised = BassWasapi.Init(wasapiDevice, Frequency: requestedFrequency, Channels: requestedChannels, Procedure: wasapiProcedureStatic, Flags: flags, Buffer: bufferSeconds, Period: periodSeconds, User: wasapiUserPtr);
                Logger.Log($"Initialising BassWasapi for device {wasapiDevice} (exclusive: {exclusive}, buffer: {bufferSeconds:0.###}s, period: {periodSeconds:0.###}s)...{(initialised ? "success!" : "FAILED")}", name: "audio", level: LogLevel.Verbose);

                if (!initialised)
                    return false;

                BassWasapi.GetInfo(out var wasapiInfo);
                Logger.Log($"WASAPI info: Freq={wasapiInfo.Frequency}, Chans={wasapiInfo.Channels}, Format={wasapiInfo.Format}", name: "audio", level: LogLevel.Verbose);
                globalMixerHandle.Value = BassMix.CreateMixerStream(wasapiInfo.Frequency, wasapiInfo.Channels, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
                BassWasapi.Start();

                BassWasapi.SetNotify(wasapiNotifyProcedureStatic, wasapiUserPtr);
                return true;
            }
            catch (DllNotFoundException e)
            {
                logWasapiNativeUnavailableOnce($"WASAPI output is unavailable because basswasapi.dll could not be loaded ({e.Message}).");
                freeWasapi();
                return false;
            }
            catch (EntryPointNotFoundException e)
            {
                logWasapiNativeUnavailableOnce($"WASAPI output is unavailable because basswasapi.dll is incompatible/mismatched ({e.Message}).");
                freeWasapi();
                return false;
            }
            catch (Exception e)
            {
                Logger.Log($"WASAPI init failed with exception: {e}", name: "audio", level: LogLevel.Error);
                freeWasapi();
                return false;
            }
        }

        private void freeWasapi()
        {
            int? mixerToFree = globalMixerHandle.Value;

            try
            {
                // The mixer probably doesn't need to be recycled. Just keeping things sane for now.
                // Stop WASAPI first to prevent callbacks from accessing disposed resources.
                BassWasapi.SetNotify(null, IntPtr.Zero);
                BassWasapi.Stop();
                BassWasapi.Free();

                if (mixerToFree != null)
                    Bass.StreamFree(mixerToFree.Value);
            }
            catch (DllNotFoundException e)
            {
                logWasapiNativeUnavailableOnce($"WASAPI cleanup failed because basswasapi.dll could not be loaded ({e.Message}).");
            }
            catch (EntryPointNotFoundException e)
            {
                logWasapiNativeUnavailableOnce($"WASAPI cleanup failed because basswasapi.dll is incompatible/mismatched ({e.Message}).");
            }
            catch (Exception e)
            {
                Logger.Log($"WASAPI cleanup failed with exception: {e}", name: "audio", level: LogLevel.Error);
            }
            finally
            {
                globalMixerHandle.Value = null;
            }
        }

        private static void logWasapiNativeUnavailableOnce(string message)
        {
            if (System.Threading.Interlocked.Exchange(ref wasapiNativeUnavailableLogged, 1) == 1)
                return;

            Logger.Log(message, name: "audio", level: LogLevel.Error);
        }

        private bool initAsio(int asioDeviceIndex, double? preferredSampleRate = null)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return false;

            Logger.Log($"Attempting ASIO initialisation for device {asioDeviceIndex}", name: "audio", level: LogLevel.Verbose);

            freeAsio();

            // Use the new AsioDeviceManager for initialization
            // Use the unified sample rate from AudioManager
            double[] commonRates = { 48000.0, 44100.0, 96000.0, 176400.0, 192000.0 };
            List<double> ratesToTry = new List<double>();

            if (preferredSampleRate.HasValue)
                ratesToTry.Add(preferredSampleRate.Value);

            foreach (double rate in commonRates)
            {
                if (!ratesToTry.Contains(rate))
                    ratesToTry.Add(rate);
            }

            double[] sampleRatesToTry = ratesToTry.ToArray();

            if (!AsioDeviceManager.InitializeDevice(asioDeviceIndex, sampleRatesToTry))
            {
                Logger.Log($"AsioDeviceManager.InitializeDevice({asioDeviceIndex}, [{string.Join(",", sampleRatesToTry)}]) failed", name: "audio", level: LogLevel.Error);
                // Don't automatically free the BASS device - let AudioManager handle fallback decisions
                // This prevents overly aggressive device switching that reduces device availability
                return false;
            }

            // Get device information after initialization
            var deviceInfo = AsioDeviceManager.GetCurrentDeviceInfo();
            if (deviceInfo == null)
            {
                Logger.Log("Failed to get ASIO device info after initialization", name: "audio", level: LogLevel.Error);
                freeAsio();
                // Don't automatically free the BASS device - let AudioManager handle fallback decisions
                return false;
            }

            int outputChannels = Math.Max(1, deviceInfo.Value.Outputs);
            int inputChannels = Math.Max(0, deviceInfo.Value.Inputs);

            // Validate device info
            if (outputChannels < 2)
            {
                Logger.Log($"ASIO device has insufficient output channels ({outputChannels}), requires at least 2 for stereo", name: "audio", level: LogLevel.Important);
                freeAsio();
                FreeDevice(Bass.CurrentDevice);
                return false;
            }

            double sampleRate = BassAsio.Rate;
            Logger.Log($"ASIO device sample rate: {sampleRate}Hz", name: "audio", level: LogLevel.Verbose);

            // Validate sample rate
            if (sampleRate <= 0 || sampleRate > 1000000 || double.IsNaN(sampleRate) || double.IsInfinity(sampleRate))
            {
                Logger.Log($"Invalid sample rate detected ({sampleRate}Hz), using {AsioAudioFormat.DEFAULT_SAMPLE_RATE}Hz as fallback", name: "audio", level: LogLevel.Important);
                sampleRate = AsioAudioFormat.DEFAULT_SAMPLE_RATE;
            }

            Logger.Log($"Using ASIO device config - Outputs: {outputChannels}, Inputs: {inputChannels}, SampleRate: {sampleRate}Hz", name: "audio", level: LogLevel.Verbose);

            // Create mixer with stereo channels (game audio is always stereo)
            const int mixerChannels = 2;
            Logger.Log($"Creating ASIO mixer stream: sampleRate={sampleRate}, mixerChannels={mixerChannels} (stereo)", name: "audio", level: LogLevel.Verbose);
            globalMixerHandle.Value = BassMix.CreateMixerStream((int)sampleRate, mixerChannels, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
            if (globalMixerHandle.Value == 0)
            {
                var mixerError = Bass.LastError;
                Logger.Log($"Failed to create ASIO mixer stream: {(int)mixerError} ({mixerError}), sampleRate={sampleRate}, channels={mixerChannels}", name: "audio", level: LogLevel.Error);
                freeAsio();
                // Free the BASS device so AudioManager can retry with a different device
                FreeDevice(Bass.CurrentDevice);
                return false;
            }
            Logger.Log($"Created ASIO mixer stream with {mixerChannels} channels at {sampleRate}Hz (handle: {globalMixerHandle.Value})", name: "audio", level: LogLevel.Verbose);

            // Set the global mixer handle for the ASIO device manager
            AsioDeviceManager.SetGlobalMixerHandle(globalMixerHandle.Value.Value);

            // Start the ASIO device using the device manager
            if (!AsioDeviceManager.StartDevice())
            {
                Logger.Log("AsioDeviceManager.StartDevice() failed", name: "audio", level: LogLevel.Error);
                freeAsio();
                // Don't automatically free the BASS device - let AudioManager handle fallback decisions
                return false;
            }

            Logger.Log($"ASIO device initialized successfully - SampleRate: {sampleRate}Hz, Outputs: {outputChannels}, Inputs: {inputChannels}", name: "audio", level: LogLevel.Debug);

            // Notify that ASIO device was initialized with the actual sample rate
            Manager?.OnAsioDeviceInitialized?.Invoke(sampleRate);

            return true;
        }

        private static string GetAsioErrorDescription(int errorCode)
        {
            return errorCode switch
            {
                3 => "This usually means the ASIO driver is unavailable, busy (possibly used by another application), incompatible, or failed to open. For Voicemeeter drivers, ensure the Voicemeeter application is running. For FiiO ASIO drivers, try routing through Voicemeeter or ensure no other applications are using the FiiO device.",
                1 => "The ASIO driver is not present or invalid.",
                2 => "No input/output is present.",
                6 => "Unsupported sample format. The ASIO driver may not support the requested audio format.",
                8 => "Already initialized. This may indicate a driver conflict or improper cleanup.",
                23 => "Device not present. The ASIO device may have been disconnected or is not available.",
                _ => $"Unknown ASIO error (code {errorCode})."
            };
        }

        private void freeAsio()
        {
            try
            {
                AsioDeviceManager.StopDevice();
                AsioDeviceManager.FreeDevice();
            }
            catch
            {
            }

            if (globalMixerHandle.Value != null)
            {
                Bass.StreamFree(globalMixerHandle.Value.Value);
                globalMixerHandle.Value = null;
            }

            // Add additional delay after freeing ASIO device to ensure complete release
            // This prevents device busy errors when switching between ASIO devices
            System.Threading.Thread.Sleep(200);
        }

        internal enum AudioThreadOutputMode
        {
            Default,
            WasapiShared,
            WasapiExclusive,
            Asio,
        }

        #endregion
    }
}
