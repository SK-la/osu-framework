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
        }

        #region BASS Initialisation

        // TODO: All this bass init stuff should probably not be in this class.

        private WasapiProcedure? wasapiProcedure;
        private WasapiNotifyProcedure? wasapiNotifyProcedure;

        private BassAsio.AsioProcedure asioProcedure = null!;

        /// <summary>
        /// If a global mixer is being used, this will be the BASS handle for it.
        /// If non-null, all game mixers should be added to this mixer.
        /// </summary>
        private readonly Bindable<int?> globalMixerHandle = new Bindable<int?>();

        internal bool InitDevice(int deviceId, AudioThreadOutputMode outputMode, int? asioDeviceIndex = null)
        {
            Debug.Assert(ThreadSafety.IsAudioThread);
            Trace.Assert(deviceId != -1); // The real device ID should always be used, as the -1 device has special cases which are hard to work with.

            // Try to initialise the device, or request a re-initialise.
            if (!Bass.Init(deviceId, Flags: (DeviceInitFlags)128)) // 128 == BASS_DEVICE_REINIT
            {
                Logger.Log($"BASS.Init({deviceId}) failed: {Bass.LastError}", name: "audio", level: LogLevel.Error);
                return false;
            }

            switch (outputMode)
            {
                case AudioThreadOutputMode.Default:
                    freeAsio();
                    freeWasapi();

                    break;

                case AudioThreadOutputMode.WasapiShared:
                    freeAsio();
                    if (!attemptWasapiInitialisation(exclusive: false))
                    {
                        Logger.Log($"BassWasapi initialisation failed (shared mode). BASS error: {Bass.LastError}", name: "audio", level: LogLevel.Error);
                        return false;
                    }

                    break;

                case AudioThreadOutputMode.WasapiExclusive:
                    freeAsio();
                    if (!attemptWasapiInitialisation(exclusive: true))
                    {
                        Logger.Log($"BassWasapi initialisation failed (exclusive mode). BASS error: {Bass.LastError}", name: "audio", level: LogLevel.Error);
                        return false;
                    }

                    break;

                case AudioThreadOutputMode.Asio:
                    freeWasapi();

                    if (asioDeviceIndex == null)
                    {
                        Logger.Log("ASIO output mode selected but no ASIO device index was provided.", name: "audio", level: LogLevel.Error);
                        return false;
                    }

                    if (!initAsio(asioDeviceIndex.Value))
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

        private bool attemptWasapiInitialisation() => attemptWasapiInitialisation(exclusive: false);

        private bool attemptWasapiInitialisation(bool exclusive)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return false;

            try
            {
                return attemptWasapiInitialisationInternal(exclusive);
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

        private bool attemptWasapiInitialisationInternal(bool exclusive)
        {
            Logger.Log($"Attempting local BassWasapi initialisation (exclusive: {exclusive})", name: "audio", level: LogLevel.Verbose);

            int wasapiDevice = -1;

            // WASAPI device indices don't match normal BASS devices.
            // Each device is listed multiple times with each supported channel/frequency pair.
            //
            // Working backwards to find the correct device is how bass does things internally (see BassWasapi.GetBassDevice).
            if (Bass.CurrentDevice > 0)
            {
                string driver = Bass.GetDeviceInfo(Bass.CurrentDevice).Driver;

                if (!string.IsNullOrEmpty(driver))
                {
                    // In the normal execution case, BassWasapi.GetDeviceInfo will return false as soon as we reach the end of devices.
                    // This while condition is just a safety to avoid looping forever.
                    // It's intentionally quite high because if a user has many audio devices, this list can get long.
                    //
                    // Retrieving device info here isn't free. In the future we may want to investigate a better method.
                    while (wasapiDevice < 16384)
                    {
                        if (!BassWasapi.GetDeviceInfo(++wasapiDevice, out WasapiDeviceInfo info))
                            break;

                        if (info.ID == driver)
                            break;
                    }
                }
            }

            // To keep things in a sane state let's only keep one device initialised via wasapi.
            freeWasapi();
            return initWasapi(wasapiDevice, exclusive);
        }

        private bool initWasapi(int wasapiDevice, bool exclusive)
        {
            try
            {
                // This is intentionally initialised inline and stored to a field.
                // If we don't do this, it gets GC'd away.
                wasapiProcedure = (buffer, length, _) =>
                {
                    if (globalMixerHandle.Value == null)
                        return 0;

                    return Bass.ChannelGetData(globalMixerHandle.Value!.Value, buffer, length);
                };
                wasapiNotifyProcedure = (notify, device, _) => Scheduler.Add(() =>
                {
                    if (notify == WasapiNotificationType.DefaultOutput)
                    {
                        freeWasapi();
                        initWasapi(device, exclusive);
                    }
                });

                var flags = WasapiInitFlags.EventDriven | WasapiInitFlags.AutoFormat;
                if (exclusive)
                    flags |= (WasapiInitFlags)16; // WasapiInitFlags.Exclusive (not available in older bindings).

                // Important: in exclusive mode, the underlying implementation may not support event-driven callbacks
                // and can fall back to polling. Using a near-zero period (float.Epsilon) can then cause a busy-loop,
                // leading to time running far too fast (and eventual instability elsewhere).
                float bufferSeconds = exclusive ? 0.05f : 0f;
                float periodSeconds = exclusive ? 0.01f : float.Epsilon;

                bool initialised = BassWasapi.Init(wasapiDevice, Procedure: wasapiProcedure, Flags: flags, Buffer: bufferSeconds, Period: periodSeconds);
                Logger.Log($"Initialising BassWasapi for device {wasapiDevice} (exclusive: {exclusive}, buffer: {bufferSeconds:0.###}s, period: {periodSeconds:0.###}s)...{(initialised ? "success!" : "FAILED")}", name: "audio", level: LogLevel.Verbose);

                if (!initialised)
                    return false;

                BassWasapi.GetInfo(out var wasapiInfo);
                globalMixerHandle.Value = BassMix.CreateMixerStream(wasapiInfo.Frequency, wasapiInfo.Channels, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
                BassWasapi.Start();

                BassWasapi.SetNotify(wasapiNotifyProcedure);
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
            if (globalMixerHandle.Value == null) return;

            try
            {
                // The mixer probably doesn't need to be recycled. Just keeping things sane for now.
                Bass.StreamFree(globalMixerHandle.Value.Value);
                BassWasapi.Stop();
                BassWasapi.Free();
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

        private bool initAsio(int asioDeviceIndex)
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return false;

            Logger.Log($"Attempting BassAsio initialisation for device {asioDeviceIndex}", name: "audio", level: LogLevel.Verbose);

            freeAsio();

            try
            {
                if (!BassAsio.Init(asioDeviceIndex))
                {
                    Logger.Log($"BassAsio.Init({asioDeviceIndex}) failed (code {BassAsio.ErrorGetCode()})", name: "audio", level: LogLevel.Error);
                    return false;
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

            int sampleRate = (int)Math.Round(BassAsio.GetRate());
            if (sampleRate <= 0)
                sampleRate = 44100;

            globalMixerHandle.Value = BassMix.CreateMixerStream(sampleRate, 2, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);

            // If we don't store the procedure, it gets GC'd away.
            asioProcedure = (input, channel, buffer, length, user) =>
            {
                if (globalMixerHandle.Value == null)
                    return 0;

                return Bass.ChannelGetData(globalMixerHandle.Value!.Value, buffer, length);
            };

            // Enable stereo output (first two output channels) and feed the global mixer stream into it.
            // We ignore additional channels for now.
            BassAsio.ChannelEnable(false, 0, asioProcedure, IntPtr.Zero);
            BassAsio.ChannelEnable(false, 1, asioProcedure, IntPtr.Zero);

            BassAsio.GetInfo(out var info);

            if (!BassAsio.Start(0))
            {
                Logger.Log($"BassAsio.Start() failed (code {BassAsio.ErrorGetCode()})", name: "audio", level: LogLevel.Error);
                freeAsio();
                return false;
            }

            Logger.Log($"BassAsio initialised (Rate: {BassAsio.GetRate()}, OutChans: {info.Outputs})");
            return true;
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
