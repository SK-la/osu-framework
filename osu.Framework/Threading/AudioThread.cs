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

        private BassAsio.AsioProcedure asioProcedure = null!;

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
                NativeLibrary.SetDllImportResolver(typeof(BassAsio).Assembly, resolveBassAsio);
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

            // Log ASIO device information before attempting initialization
            try
            {
                var devices = BassAsio.EnumerateDevices().ToList();
                if (asioDeviceIndex >= 0 && asioDeviceIndex < devices.Count)
                {
                    var device = devices[asioDeviceIndex];
                    Logger.Log($"ASIO device info - Index: {device.Index}, Name: '{device.Name}'", name: "audio", level: LogLevel.Verbose);
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to get ASIO device info: {e.Message}", name: "audio", level: LogLevel.Important);
            }

            Logger.Log($"Attempting BassAsio initialisation for device {asioDeviceIndex}", name: "audio", level: LogLevel.Verbose);

            freeAsio();

            // Some ASIO drivers (like Voicemeeter) may need a moment to be ready
            // or may be busy if another application is using them
            const int maxRetries = 3;
            const int retryDelay = 500; // ms

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    Logger.Log($"Retrying ASIO initialisation for device {asioDeviceIndex} (attempt {attempt + 1}/{maxRetries})", name: "audio", level: LogLevel.Verbose);
                    System.Threading.Thread.Sleep(retryDelay);
                }

                try
                {
                    // CRITICAL: Use AsioInitFlags.Thread for proper ASIO initialization
                    // This creates a dedicated thread with message queue, which is REQUIRED for ASIO to work
                    const int BASS_ASIO_THREAD = 1; // AsioInitFlags.Thread
                    if (!BassAsio.Init(asioDeviceIndex, BASS_ASIO_THREAD))
                    {
                        int code = BassAsio.ErrorGetCode();
                        string errorMessage = GetAsioErrorDescription(code);

                        if (attempt == maxRetries - 1)
                        {
                            Logger.Log($"BassAsio.Init({asioDeviceIndex}) failed after {maxRetries} attempts (code {code} / {(Errors)code}). {errorMessage}", name: "audio", level: LogLevel.Error);
                            return false;
                        }
                        else
                        {
                            Logger.Log($"BassAsio.Init({asioDeviceIndex}) failed (code {code} / {(Errors)code}). {errorMessage} Retrying...", name: "audio", level: LogLevel.Important);
                            continue;
                        }
                    }
                }
                catch (DllNotFoundException e)
                {
                    Logger.Log($"ASIO output is unavailable because bassasio.dll could not be loaded ({e.Message}). Ensure bassasio.dll is deployed alongside the other BASS native libraries (usually under x64/x86; referencing the BASS.ASIO NuGet package should copy it automatically).", name: "audio", level: LogLevel.Error);
                    return false;
                }
                catch (EntryPointNotFoundException e)
                {
                    Logger.Log($"ASIO output is unavailable because bassasio.dll is incompatible/mismatched ({e.Message}). Ensure the correct bassasio.dll (matching the BASS native libraries and process architecture) is deployed.", name: "audio", level: LogLevel.Error);
                    return false;
                }

                // If we get here, BassAsio.Init succeeded
                break;
            }

            // Check and set sample rate
            double initialRate = BassAsio.GetRate();
            Logger.Log($"ASIO device initial sample rate: {initialRate}Hz", name: "audio", level: LogLevel.Verbose);

            // Try to set the preferred sample rate, or fall back to a common sample rate that most ASIO drivers support
            double targetSampleRate = preferredSampleRate ?? AsioAudioFormat.DefaultSampleRate;
            bool rateSetPreferred = BassAsio.SetRate(targetSampleRate);
            Logger.Log($"Set ASIO sample rate to {targetSampleRate}Hz: {rateSetPreferred}", name: "audio", level: LogLevel.Verbose);

            double rateAfterPreferred = BassAsio.GetRate();
            Logger.Log($"ASIO sample rate after setting {targetSampleRate}Hz: {rateAfterPreferred}Hz", name: "audio", level: LogLevel.Verbose);

            if (!rateSetPreferred || Math.Abs(rateAfterPreferred - targetSampleRate) > 1)
            {
                // Try alternative sample rates, starting with the user's preferred rate if it wasn't tried yet
                var ratesToTry = new List<double>(AsioAudioFormat.SupportedSampleRates);
                if (preferredSampleRate.HasValue && !ratesToTry.Contains(preferredSampleRate.Value))
                    ratesToTry.Insert(0, preferredSampleRate.Value);

                foreach (double altRate in ratesToTry)
                {
                    if (Math.Abs(altRate - targetSampleRate) < 1) continue; // Skip the one we already tried

                    bool rateSetAlt = BassAsio.SetRate(altRate);
                    Logger.Log($"Set ASIO sample rate to {altRate}Hz: {rateSetAlt}", name: "audio", level: LogLevel.Verbose);

                    double rateAfterAlt = BassAsio.GetRate();
                    Logger.Log($"ASIO sample rate after setting {altRate}Hz: {rateAfterAlt}Hz", name: "audio", level: LogLevel.Verbose);

                    if (rateSetAlt && Math.Abs(rateAfterAlt - altRate) < 1)
                    {
                        preferredSampleRate = altRate;
                        Logger.Log($"Successfully set ASIO sample rate to {altRate}Hz", name: "audio", level: LogLevel.Verbose);
                        break;
                    }
                }
            }

            double finalSampleRate = BassAsio.GetRate();
            Logger.Log($"ASIO device final sample rate: {finalSampleRate}Hz", name: "audio", level: LogLevel.Verbose);

            // Validate and set final sample rate
            if (finalSampleRate <= 0 || finalSampleRate > 1000000 || double.IsNaN(finalSampleRate) || double.IsInfinity(finalSampleRate))
            {
                Logger.Log($"Invalid sample rate detected ({finalSampleRate}Hz), using {AsioAudioFormat.DefaultSampleRate}Hz as fallback", name: "audio", level: LogLevel.Important);
                finalSampleRate = AsioAudioFormat.DefaultSampleRate;
                // Try to set it again
                BassAsio.SetRate(finalSampleRate);
            }

            double sampleRate = finalSampleRate;
            Logger.Log($"Using ASIO sample rate: {sampleRate}Hz", name: "audio", level: LogLevel.Verbose);

            globalMixerHandle.Value = BassMix.CreateMixerStream((int)sampleRate, 2, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);

            // If we don't store the procedure, it gets GC'd away.
            asioProcedure = (input, channel, buffer, length, user) =>
            {
                if (globalMixerHandle.Value == null)
                    return 0;

                return Bass.ChannelGetData(globalMixerHandle.Value!.Value, buffer, length);
            };

            // Skip GetInfo for now as it returns garbage values - use safe defaults
            // TODO: Fix AsioInfo struct definition for proper device info retrieval
            int outputChannels = 2; // Default to stereo
            int inputChannels = 0;
            int bufferSize = 1024; // Default buffer size

            Logger.Log($"Using default ASIO device config - Outputs: {outputChannels}, Inputs: {inputChannels}, BufferSize: {bufferSize} samples", name: "audio", level: LogLevel.Verbose);

            // Configure output channels properly
            if (outputChannels >= 2)
            {
                // Enable channel 0 (left)
                if (!BassAsio.ChannelEnable(false, 0, asioProcedure, IntPtr.Zero))
                {
                    Logger.Log($"Failed to enable ASIO output channel 0: {BassAsio.ErrorGetCode()}", name: "audio", level: LogLevel.Error);
                    freeAsio();
                    return false;
                }

                // Enable channel 1 (right)
                if (!BassAsio.ChannelEnable(false, 1, asioProcedure, IntPtr.Zero))
                {
                    Logger.Log($"Failed to enable ASIO output channel 1: {BassAsio.ErrorGetCode()}", name: "audio", level: LogLevel.Error);
                    freeAsio();
                    return false;
                }

                Logger.Log("ASIO stereo output channels configured successfully", name: "audio", level: LogLevel.Verbose);
            }
            else if (outputChannels >= 1)
            {
                // Mono output - enable only channel 0
                if (!BassAsio.ChannelEnable(false, 0, asioProcedure, IntPtr.Zero))
                {
                    Logger.Log($"Failed to enable ASIO output channel 0: {BassAsio.ErrorGetCode()}", name: "audio", level: LogLevel.Error);
                    freeAsio();
                    return false;
                }

                Logger.Log("ASIO mono output channel configured (only 1 output available)", name: "audio", level: LogLevel.Important);
            }
            else
            {
                Logger.Log("ASIO device has no output channels", name: "audio", level: LogLevel.Error);
                freeAsio();
                return false;
            }

            Logger.Log($"ASIO device initialized - SampleRate: {sampleRate}Hz, Outputs: {outputChannels}, Inputs: {inputChannels}, BufferSize: {bufferSize} samples", name: "audio", level: LogLevel.Verbose);

            // Lock the sample rate before starting ASIO to prevent it from changing
            double lockedSampleRate = sampleRate;
            Logger.Log($"Locked ASIO sample rate: {lockedSampleRate}Hz", name: "audio", level: LogLevel.Verbose);

            if (!BassAsio.Start(0))
            {
                int startError = BassAsio.ErrorGetCode();
                Logger.Log($"BassAsio.Start() failed (code {startError} / {(Errors)startError})", name: "audio", level: LogLevel.Error);
                freeAsio();
                return false;
            }

            Logger.Log($"BassAsio initialised (Rate: {BassAsio.GetRate()}, OutChans: {outputChannels})");

            // Verify the sample rate is still correct after start
            double rateAfterStart = BassAsio.GetRate();
            if (Math.Abs(rateAfterStart - lockedSampleRate) > 1)
            {
                Logger.Log($"Warning: ASIO sample rate changed after start (was {lockedSampleRate}Hz, now {rateAfterStart}Hz)", name: "audio", level: LogLevel.Important);
            }
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
                BassAsio.Stop();
                BassAsio.Free();
            }
            catch
            {
            }

            if (globalMixerHandle.Value != null)
            {
                Bass.StreamFree(globalMixerHandle.Value.Value);
                globalMixerHandle.Value = null;
            }
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
