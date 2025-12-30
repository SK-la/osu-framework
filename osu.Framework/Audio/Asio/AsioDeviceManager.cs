// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.Asio;
using osu.Framework.Logging;

namespace osu.Framework.Audio.Asio
{
    /// <summary>
    /// Audio format configuration for ASIO devices.
    /// </summary>
    internal static class AsioAudioFormat
    {
        /// <summary>
        /// Common sample rates supported by most ASIO devices.
        /// Prioritizes 48kHz for new audio modes, with 44.1kHz as fallback.
        /// </summary>
        public static readonly double[] SupportedSampleRates = { 48000, 44100, 96000, 176400, 192000 };

        /// <summary>
        /// Default sample rate to use when none is specified.
        /// Uses 48kHz as the primary default for new audio modes.
        /// </summary>
        public const double DEFAULT_SAMPLE_RATE = 48000;

        /// <summary>
        /// Checks if a sample rate is commonly supported.
        /// </summary>
        public static bool IsSupportedSampleRate(double sampleRate)
        {
            foreach (double rate in SupportedSampleRates)
            {
                if (Math.Abs(rate - sampleRate) < 1)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the closest supported sample rate to the requested rate.
        /// </summary>
        public static double GetClosestSupportedSampleRate(double requestedRate)
        {
            double closest = DEFAULT_SAMPLE_RATE;
            double minDiff = double.MaxValue;

            foreach (double rate in SupportedSampleRates)
            {
                double diff = Math.Abs(rate - requestedRate);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = rate;
                }
            }

            return closest;
        }
    }

    /// <summary>
    /// Manages ASIO audio devices and their initialization.
    /// </summary>
    public static class AsioDeviceManager
    {
        /// <summary>
        /// The global mixer handle for ASIO audio routing.
        /// This is set by the audio thread when ASIO device is initialized.
        /// </summary>
        private static int globalMixerHandle = 0;

        /// <summary>
        /// Sets the global mixer handle for ASIO audio routing.
        /// </summary>
        /// <param name="mixerHandle">The handle of the global mixer.</param>
        public static void SetGlobalMixerHandle(int mixerHandle)
        {
            globalMixerHandle = mixerHandle;
            Logger.Log($"ASIO global mixer handle set: {mixerHandle}", LoggingTarget.Runtime, LogLevel.Debug);
        }
        /// <summary>
        /// Gets a list of available ASIO devices.
        /// </summary>
        public static IEnumerable<(int Index, string Name)> AvailableDevices
        {
            get
            {
                var devices = new List<(int, string)>();

                int deviceCount = BassAsio.DeviceCount;
                for (int i = 0; i < deviceCount; i++)
                {
                    var info = new AsioDeviceInfo();
                    if (BassAsio.GetDeviceInfo(i, out info))
                    {
                        devices.Add((i, info.Name));
                    }
                }

                return devices;
            }
        }

        /// <summary>
        /// Initializes an ASIO device.
        /// </summary>
        /// <param name="deviceIndex">The index of the ASIO device to initialize.</param>
        /// <param name="sampleRatesToTry">The sample rates to try in order. If null, will try common rates.</param>
        /// <returns>True if initialization was successful, false otherwise.</returns>
        public static bool InitializeDevice(int deviceIndex, double[]? sampleRatesToTry = null)
        {
            try
            {
                Logger.Log($"InitializeDevice called with deviceIndex={deviceIndex}, sampleRatesToTry={string.Join(",", sampleRatesToTry ?? Array.Empty<double>())}", LoggingTarget.Runtime, LogLevel.Debug);

                // Check if the device index is valid
                if (deviceIndex < 0 || deviceIndex >= BassAsio.DeviceCount)
                {
                    Logger.Log($"Invalid ASIO device index: {deviceIndex} (DeviceCount: {BassAsio.DeviceCount})", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                // Get device info
                var deviceInfo = new AsioDeviceInfo();
                if (!BassAsio.GetDeviceInfo(deviceIndex, out deviceInfo))
                {
                    Logger.Log($"Failed to get ASIO device info for index: {deviceIndex}", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                Logger.Log($"Initializing ASIO device: {deviceInfo.Name} (Driver: {deviceInfo.Driver})", LoggingTarget.Runtime, LogLevel.Debug);

                FreeDevice();

                // Try different initialization flags to handle ASIO initialization
                // Use Thread flag to ensure BassAsio creates a dedicated thread with message queue
                // This is REQUIRED for ASIO to work properly according to documentation
                AsioInitFlags[] initFlagsToTry = new[] { AsioInitFlags.Thread };

                bool deviceInitialized = false;
                double successfulRate = 0;

                foreach (var flags in initFlagsToTry)
                {
                    Logger.Log($"Trying ASIO initialization with flags: {flags}", LoggingTarget.Runtime, LogLevel.Debug);

                    // Try initialization with retry logic for busy devices (3 attempts within 1 second)
                    int retryCount = 0;
                    const int maxRetries = 3;

                    while (retryCount < maxRetries)
                    {
                        if (BassAsio.Init(deviceIndex, flags))
                        {
                            Logger.Log($"ASIO device initialized successfully with flags: {flags} (attempt {retryCount + 1})", LoggingTarget.Runtime, LogLevel.Debug);
                            deviceInitialized = true;
                            break;
                        }

                        var bassError = BassAsio.LastError;
                        Logger.Log($"ASIO initialization failed with flags {flags} (attempt {retryCount + 1}): {bassError} (Code: {(int)bassError}) - {GetAsioErrorDescription((int)bassError)}", LoggingTarget.Runtime, LogLevel.Important);

                        // If device is busy, wait and retry (100ms delay for quick retries within 1 second total)
                        if ((int)bassError == 3 || bassError == Errors.Busy)
                        {
                            retryCount++;
                            if (retryCount < maxRetries)
                            {
                                Logger.Log($"Device busy, waiting 100ms before retry {retryCount + 1}/{maxRetries}", LoggingTarget.Runtime, LogLevel.Important);
                                System.Threading.Thread.Sleep(100);
                                continue;
                            }
                        }

                        // For other errors, don't retry
                        break;
                    }

                    if (deviceInitialized)
                    {
                        // Try sample rates in order
                        double[] ratesToTry = sampleRatesToTry ?? new double[] { 44100.0, 48000.0 };

                        foreach (double rate in ratesToTry)
                        {
                            if (BassAsio.CheckRate(rate))
                            {
                                BassAsio.Rate = rate;
                                successfulRate = rate;
                                Logger.Log($"Set ASIO device sample rate to {rate}Hz", LoggingTarget.Runtime, LogLevel.Debug);
                                break;
                            }
                            else
                            {
                                Logger.Log($"Device does not support {rate}Hz sample rate", LoggingTarget.Runtime, LogLevel.Debug);
                            }
                        }

                        if (successfulRate == 0)
                        {
                            Logger.Log("Device does not support any of the requested sample rates", LoggingTarget.Runtime, LogLevel.Error);
                            BassAsio.Free();
                            return false;
                        }

                        return true;
                    }

                    // If we get BufferLost error, try different approach
                    if (BassAsio.LastError == Errors.BufferLost)
                    {
                        Logger.Log("BufferLost error detected, trying alternative initialization approach", LoggingTarget.Runtime, LogLevel.Important);
                        FreeDevice();
                        // Driver reset handled by FreeDevice(), no sleep needed in audio thread
                    }
                }

                // If all flag combinations failed, log the final error
                var finalError = BassAsio.LastError;
                Logger.Log($"All ASIO initialization attempts failed for device {deviceIndex}. Last error: {finalError} (Code: {(int)finalError}) - {GetAsioErrorDescription((int)finalError)}", LoggingTarget.Runtime, LogLevel.Important);
                Logger.Log($"Device info: Name='{deviceInfo.Name}', Driver='{deviceInfo.Driver}'", LoggingTarget.Runtime, LogLevel.Important);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during ASIO device initialization: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Frees the currently initialized ASIO device.
        /// </summary>
        public static void FreeDevice()
        {
            try
            {
                // Stop the device first if it's running
                BassAsio.Stop();
                Logger.Log("ASIO device stopped", LoggingTarget.Runtime, LogLevel.Debug);

                // Free the ASIO device
                BassAsio.Free();
                Logger.Log("ASIO device freed successfully", LoggingTarget.Runtime, LogLevel.Debug);

                // Add a small delay to ensure the device is completely released
                // This prevents device busy errors when switching devices quickly
                System.Threading.Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during ASIO device freeing: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        /// <summary>
        /// Forces a complete reset of ASIO state, useful for recovery from error conditions.
        /// </summary>
        public static void ForceReset()
        {
            try
            {
                Logger.Log("Forcing complete ASIO state reset", LoggingTarget.Runtime, LogLevel.Debug);

                // Stop and free any existing ASIO device
                BassAsio.Stop();
                BassAsio.Free();

                // Clear the global mixer handle
                globalMixerHandle = 0;

                // Add longer delay to ensure complete reset
                System.Threading.Thread.Sleep(200);

                Logger.Log("ASIO state reset completed", LoggingTarget.Runtime, LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during ASIO force reset: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        /// <summary>
        /// Starts the ASIO device processing.
        /// </summary>
        /// <returns>True if start was successful, false otherwise.</returns>
        public static bool StartDevice()
        {
            try
            {
                // Configure default output channels before starting
                if (!ConfigureDefaultChannels())
                {
                    Logger.Log("Failed to configure default ASIO channels", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                // Check if channels are properly enabled before starting
                var channel0Active = BassAsio.ChannelIsActive(false, 0);
                var channel1Active = BassAsio.ChannelIsActive(false, 1);

                Logger.Log($"Channel 0 active state: {channel0Active}, Channel 1 active state: {channel1Active}",
                          LoggingTarget.Runtime, LogLevel.Debug);

                if (BassAsio.Start())
                {
                    Logger.Log("ASIO device started successfully", LoggingTarget.Runtime, LogLevel.Debug);

                    // Verify that channels are actually processing after start
                    channel0Active = BassAsio.ChannelIsActive(false, 0);
                    channel1Active = BassAsio.ChannelIsActive(false, 1);

                    Logger.Log($"After start - Channel 0 active state: {channel0Active}, Channel 1 active state: {channel1Active}",
                              LoggingTarget.Runtime, LogLevel.Debug);

                    return true;
                }
                else
                {
                    var bassError = BassAsio.LastError;
                    Logger.Log($"Failed to start ASIO device: {bassError} (Code: {(int)bassError})",
                              LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during ASIO device start: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Stops the ASIO device processing.
        /// </summary>
        public static void StopDevice()
        {
            try
            {
                BassAsio.Stop();
                Logger.Log("ASIO device stopped successfully", LoggingTarget.Runtime, LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during ASIO device stop: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        /// <summary>
        /// Gets information about the currently initialized ASIO device.
        /// </summary>
        /// <returns>ASIO device information, or null if no device is initialized.</returns>
        public static AsioInfo? GetCurrentDeviceInfo()
        {
            try
            {
                var info = new AsioInfo();
                if (BassAsio.GetInfo(out info))
                    return info;

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception getting ASIO device info: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Gets the current sample rate of the ASIO device.
        /// </summary>
        /// <returns>The current sample rate, or 0 if no device is initialized or an error occurs.</returns>
        public static double GetCurrentSampleRate()
        {
            try
            {
                return BassAsio.Rate;
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception getting ASIO sample rate: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return 0;
            }
        }

        /// <summary>
        /// Configures default input and output channels for ASIO device.
        /// </summary>
        /// <returns>True if channel configuration was successful, false otherwise.</returns>
        private static bool ConfigureDefaultChannels()
        {
            try
            {
                // Get device info to determine available channels
                var info = GetCurrentDeviceInfo();
                if (info == null)
                    return false;

                Logger.Log($"ASIO device has {info.Value.Inputs} inputs and {info.Value.Outputs} outputs available", LoggingTarget.Runtime, LogLevel.Debug);

                // Try to configure at least one stereo output channel pair
                bool channelsConfigured = ConfigureOutputChannels(info.Value);

                if (channelsConfigured)
                {
                    Logger.Log("ASIO output channels configured successfully", LoggingTarget.Runtime, LogLevel.Debug);
                }
                else
                {
                    Logger.Log("Failed to configure ASIO channels, falling back to driver default configuration", LoggingTarget.Runtime, LogLevel.Important);
                    // Fallback to basic availability check
                    channelsConfigured = info.Value.Inputs > 0 || info.Value.Outputs > 0;
                }

                return channelsConfigured;
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during channel configuration: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Configures output channels for ASIO device.
        /// </summary>
        /// <param name="info">The ASIO device information.</param>
        /// <returns>True if output channels were successfully configured, false otherwise.</returns>
        private static bool ConfigureOutputChannels(AsioInfo info)
        {
            if (info.Outputs < 2)
            {
                Logger.Log($"Not enough output channels available ({info.Outputs}), cannot configure stereo output", LoggingTarget.Runtime, LogLevel.Important);
                return false;
            }

            try
            {
                // Configure first stereo output pair (channels 0 and 1)
                Logger.Log($"Configuring stereo output channels (0 and 1) for ASIO device", LoggingTarget.Runtime, LogLevel.Debug);

                // Create ASIO procedure callback
                AsioProcedure asioCallback = AsioProcedure;

                // Enable channel 0 (left)
                if (!BassAsio.ChannelEnable(false, 0, asioCallback))
                {
                    Logger.Log($"Failed to enable output channel 0: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                // Enable channel 1 (right) and join it to channel 0 for stereo
                if (!BassAsio.ChannelEnable(false, 1, asioCallback))
                {
                    Logger.Log($"Failed to enable output channel 1: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                // 设置输出格式为 Float 以与我们提供的 Float 数据匹配
                if (!BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float))
                    Logger.Log($"Channel 0 set format Float failed: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                if (!BassAsio.ChannelSetFormat(false, 1, AsioSampleFormat.Float))
                    Logger.Log($"Channel 1 set format Float failed: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Error);

                // 与 Rate（44100 或 48000）对齐
                double targetRate = BassAsio.Rate > 0 ? BassAsio.Rate : 44100.0;
                if (!BassAsio.ChannelSetRate(false, 0, targetRate))
                    Logger.Log($"Channel 0 set rate {targetRate} failed: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Debug);
                if (!BassAsio.ChannelSetRate(false, 1, targetRate))
                    Logger.Log($"Channel 1 set rate {targetRate} failed: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Debug);

                // Join channel 1 to channel 0 to form stereo pair
                if (!BassAsio.ChannelJoin(false, 1, 0))
                {
                    Logger.Log($"Failed to join output channels 0 and 1: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                Logger.Log("Stereo output channels configured successfully", LoggingTarget.Runtime, LogLevel.Debug);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during output channel configuration: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// ASIO procedure callback for channel processing.
        /// </summary>
        private static int silenceFrames = 0;
        private static int AsioProcedure(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            if (input)
            {
                // For input channels, we don't process anything
                return 0;
            }

            // For output channels, we need to provide audio data from the game's audio system
            // Get the global mixer handle from the audio thread
            int globalMixerHandle = GetGlobalMixerHandle();
            if (globalMixerHandle == 0)
            {
                // If global mixer is not available, fill with silence
                FillBufferWithSilence(buffer, length);
                return length;
            }

            try
            {
                // Get audio data from the global mixer
                // Use DataFlags.Float flag to get float data directly
                int bytesRead = Bass.ChannelGetData(globalMixerHandle, buffer, length | (int)DataFlags.Float);

                if (bytesRead <= 0)
                {
                    // No audio data available, fill with silence
                    FillBufferWithSilence(buffer, length);
                    if (++silenceFrames % 200 == 0)
                        Logger.Log($"[AudioDebug] ASIO callback silence count={silenceFrames}, globalMixer={globalMixerHandle}", LoggingTarget.Runtime, LogLevel.Debug);
                }
                else if (bytesRead < length)
                {
                    // Partial data received, fill the rest with silence
                    unsafe
                    {
                        float* bufferPtr = (float*)buffer;
                        float* silenceStart = bufferPtr + (bytesRead / sizeof(float));
                        int silenceSamples = (length - bytesRead) / sizeof(float);

                        for (int i = 0; i < silenceSamples; i++)
                        {
                            silenceStart[i] = 0.0f;
                        }
                    }
                }

                return length; // Always return requested length (driver expects full buffer)
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception in ASIO callback: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                FillBufferWithSilence(buffer, length);
                return length;
            }
        }

        /// <summary>
        /// Gets the global mixer handle from the audio thread.
        /// </summary>
        private static int GetGlobalMixerHandle()
        {
            // Return the global mixer handle that was set by the audio thread
            return globalMixerHandle;
        }

        /// <summary>
        /// Fills the buffer with silence (zeroes).
        /// </summary>
        private static unsafe void FillBufferWithSilence(IntPtr buffer, int length)
        {
            float* bufferPtr = (float*)buffer;
            for (int i = 0; i < length / sizeof(float); i++)
            {
                bufferPtr[i] = 0.0f;
            }
        }

        /// <summary>
        /// Gets a description for an ASIO error code.
        /// </summary>
        private static string GetAsioErrorDescription(int errorCode)
        {
            return errorCode switch
            {
                3 => "ASIO driver unavailable, busy, incompatible, or failed to open. For Voicemeeter drivers, ensure the Voicemeeter application is running. For FiiO ASIO drivers, try routing through Voicemeeter or ensure no other applications are using the FiiO device.",
                1 => "ASIO driver not present or invalid.",
                2 => "No input/output channels present.",
                6 => "Unsupported sample format. The ASIO driver may not support the requested audio format.",
                8 => "Already initialized. This may indicate a driver conflict or improper cleanup.",
                23 => "Device not present. The ASIO device may have been disconnected or is not available.",
                _ => $"Unknown ASIO error (code {errorCode})."
            };
        }
    }
}
