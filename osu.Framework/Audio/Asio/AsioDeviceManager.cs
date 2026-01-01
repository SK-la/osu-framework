// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ManagedBass;
using ManagedBass.Asio;
using osu.Framework.Logging;

namespace osu.Framework.Audio.Asio
{
    /// <summary>
    /// 管理ASIO音频设备及其初始化。
    /// </summary>
    public static class AsioDeviceManager
    {
        /// <summary>
        /// 常见采样率列表, 用于尝试验证设备支持的采样率。
        /// 按优先级排序，48kHz优先于44.1kHz。
        /// 使用int类型因为采样率通常是整数值，double仅在底层API交互时使用。
        /// </summary>
        public static readonly int[] SUPPORTED_SAMPLE_RATES = { 48000, 44100, 96000, 192000, 384000 };

        /// <summary>
        /// 未指定、或设置采样率失败时，使用默认采样率48kHz，性能较好。
        /// </summary>
        public const int DEFAULT_SAMPLE_RATE = 48000;

        /// <summary>
        /// 最大重试次数，用于处理设备繁忙的情况。
        /// </summary>
        private const int MAX_RETRY_COUNT = 3;

        /// <summary>
        /// 重试间隔时间（毫秒）。
        /// </summary>
        private const int RETRY_DELAY_MS = 100;

        /// <summary>
        /// 设备释放延迟时间（毫秒）。
        /// </summary>
        private const int DEVICE_FREE_DELAY_MS = 100;

        /// <summary>
        /// 强制重置延迟时间（毫秒）。
        /// </summary>
        private const int FORCE_RESET_DELAY_MS = 200;

        /// <summary>
        /// 采样率容差，用于验证设置是否成功。
        /// </summary>
        private const double SAMPLE_RATE_TOLERANCE = 1.0;

        /// <summary>
        /// 静音帧日志间隔。
        /// </summary>
        private const int SILENCE_LOG_INTERVAL = 200;

        /// <summary>
        /// ASIO音频路由的全局混音器句柄。
        /// 当ASIO设备初始化时，由音频线程设置。
        /// </summary>
        private static int globalMixerHandle;

        /// <summary>
        /// 设置ASIO音频路由的全局混音器句柄。
        /// </summary>
        /// <param name="mixerHandle">混音器的句柄。</param>
        public static void SetGlobalMixerHandle(int mixerHandle)
        {
            globalMixerHandle = mixerHandle;
            Logger.Log($"ASIO global mixer handle set: {mixerHandle}", LoggingTarget.Runtime, LogLevel.Debug);
        }

        /// <summary>
        /// 验证设备索引是否有效。
        /// </summary>
        /// <param name="deviceIndex">要验证的设备索引。</param>
        /// <returns>如果索引有效则为true，否则为false。</returns>
        private static bool isValidDeviceIndex(int deviceIndex)
        {
            return deviceIndex >= 0 && deviceIndex < BassAsio.DeviceCount;
        }

        /// <summary>
        /// 安全地获取ASIO设备信息。
        /// </summary>
        /// <param name="deviceIndex">设备索引。</param>
        /// <param name="deviceInfo">输出设备信息。</param>
        /// <returns>如果获取成功则为true，否则为false。</returns>
        private static bool tryGetDeviceInfo(int deviceIndex, out AsioDeviceInfo deviceInfo)
        {
            deviceInfo = default;

            if (!isValidDeviceIndex(deviceIndex))
            {
                Logger.Log($"Invalid ASIO device index: {deviceIndex} (DeviceCount: {BassAsio.DeviceCount})", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }

            if (!BassAsio.GetDeviceInfo(deviceIndex, out deviceInfo))
            {
                Logger.Log($"Failed to get ASIO device info for index {deviceIndex}", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 尝试初始化ASIO设备，支持重试逻辑。
        /// </summary>
        /// <param name="deviceIndex">设备索引。</param>
        /// <param name="flags">初始化标志。</param>
        /// <returns>如果初始化成功则为true，否则为false。</returns>
        private static bool tryInitializeDevice(int deviceIndex, AsioInitFlags flags)
        {
            for (int retryCount = 0; retryCount < MAX_RETRY_COUNT; retryCount++)
            {
                if (BassAsio.Init(deviceIndex, flags))
                {
                    return true;
                }

                var bassError = BassAsio.LastError;
                Logger.Log($"ASIO initialization failed with flags {flags} (attempt {retryCount + 1}): {bassError} (Code: {(int)bassError}) - {getAsioErrorDescription((int)bassError)}",
                    LoggingTarget.Runtime, LogLevel.Important);

                // 如果设备繁忙，等待并重试
                if ((int)bassError == 3 || bassError == Errors.Busy)
                {
                    if (retryCount < MAX_RETRY_COUNT - 1)
                    {
                        Logger.Log($"Device busy, waiting {RETRY_DELAY_MS}ms before retry {retryCount + 1}/{MAX_RETRY_COUNT}", LoggingTarget.Runtime, LogLevel.Important);
                        Thread.Sleep(RETRY_DELAY_MS);
                        continue;
                    }
                }

                // 对于其他错误，不重试
                break;
            }

            return false;
        }

        /// <summary>
        /// 尝试设置采样率。
        /// </summary>
        /// <param name="rate">要设置的采样率。</param>
        /// <returns>如果设置成功则为true，否则为false。</returns>
        private static bool trySetSampleRate(double rate)
        {
            if (!BassAsio.CheckRate(rate))
            {
                return false;
            }

            BassAsio.Rate = rate;

            var rateError = BassAsio.LastError;

            if (rateError != Errors.OK)
            {
                Logger.Log($"Failed to set ASIO device sample rate to {rate}Hz: {rateError} (Code: {(int)rateError})", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }

            double actualRate = BassAsio.Rate;

            if (Math.Abs(actualRate - rate) >= SAMPLE_RATE_TOLERANCE)
            {
                Logger.Log($"Failed to set ASIO device sample rate to {rate}Hz (actual: {actualRate}Hz)", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 获取可用ASIO设备的列表。
        /// </summary>
        public static IEnumerable<(int Index, string Name)> AvailableDevices
        {
            get
            {
                // Check if ASIO is available before attempting to enumerate
                try
                {
                    // Try to get device count to test if ASIO is available
                    BassAsio.DeviceCount.GetHashCode(); // Simple operation to trigger any DLL loading issues
                }
                catch
                {
                    // ASIO is not available
                    yield break;
                }

                int deviceCount = BassAsio.DeviceCount;

                for (int i = 0; i < deviceCount; i++)
                {
                    if (BassAsio.GetDeviceInfo(i, out AsioDeviceInfo info))
                    {
                        yield return (i, info.Name);
                    }
                }
            }
        }

        /// <summary>
        /// 获取带有支持采样率的可用ASIO设备列表。
        /// 注意：此操作可能较慢，因为需要为每个设备查询支持的采样率。
        /// </summary>
        public static IEnumerable<(int Index, string Name, double[] SupportedSampleRates)> AvailableDevicesWithSampleRates
        {
            get
            {
                foreach (var (index, name) in AvailableDevices)
                {
                    double[] supportedRates = GetSupportedSampleRates(index).ToArray();
                    yield return (index, name, supportedRates);
                }
            }
        }

        /// <summary>
        /// 初始化ASIO设备。
        /// </summary>
        /// <param name="deviceIndex">要初始化的ASIO设备的索引。</param>
        /// <param name="sampleRatesToTry">按顺序尝试的采样率。如果为null，将尝试常见速率。</param>
        /// <returns>如果初始化成功则为true，否则为false。</returns>
        public static bool InitializeDevice(int deviceIndex, double[]? sampleRatesToTry = null)
        {
            try
            {
                Logger.Log($"InitializeDevice called with deviceIndex={deviceIndex}, sampleRatesToTry={string.Join(",", sampleRatesToTry ?? Array.Empty<double>())}", LoggingTarget.Runtime,
                    LogLevel.Debug);

                // 获取设备信息
                if (!tryGetDeviceInfo(deviceIndex, out AsioDeviceInfo deviceInfo))
                    return false;

                Logger.Log($"Initializing ASIO device: {deviceInfo.Name} (Driver: {deviceInfo.Driver})", LoggingTarget.Runtime, LogLevel.Debug);

                FreeDevice();

                // 尝试不同的初始化标志
                AsioInitFlags[] initFlagsToTry = { AsioInitFlags.Thread };

                foreach (var flags in initFlagsToTry)
                {
                    if (tryInitializeDevice(deviceIndex, flags))
                    {
                        // 尝试采样率
                        double[] ratesToTry = sampleRatesToTry ?? SUPPORTED_SAMPLE_RATES.Select(rate => (double)rate).ToArray();
                        double successfulRate = 0;

                        foreach (double rate in ratesToTry)
                        {
                            if (trySetSampleRate(rate))
                            {
                                successfulRate = rate;
                                break;
                            }
                        }

                        if (successfulRate > 0)
                            return true;

                        // 如果采样率设置失败，释放设备
                        BassAsio.Free();
                        break;
                    }

                    // 处理BufferLost错误
                    if (BassAsio.LastError == Errors.BufferLost)
                    {
                        Logger.Log("BufferLost error detected, trying alternative initialization method", LoggingTarget.Runtime, LogLevel.Important);
                        FreeDevice();
                    }
                }

                // 记录最终错误
                var finalError = BassAsio.LastError;
                Logger.Log($"All ASIO initialization attempts failed for device {deviceIndex}. Final error: {finalError} (Code: {(int)finalError}) - {getAsioErrorDescription((int)finalError)}",
                    LoggingTarget.Runtime, LogLevel.Important);
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
        /// 释放当前初始化的ASIO设备。
        /// </summary>
        public static void FreeDevice()
        {
            try
            {
                BassAsio.Stop();
                BassAsio.Free();
                Thread.Sleep(DEVICE_FREE_DELAY_MS);
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during ASIO device release: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        /// <summary>
        /// 强制完全重置ASIO状态，用于从错误条件中恢复。
        /// </summary>
        public static void ForceReset()
        {
            try
            {
                FreeDevice();
                globalMixerHandle = 0;
                Thread.Sleep(FORCE_RESET_DELAY_MS);
                Logger.Log("ASIO Force Reset", LoggingTarget.Runtime, LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during ASIO force reset: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        /// <summary>
        /// 启动ASIO设备处理。
        /// </summary>
        /// <returns>如果启动成功则为true，否则为false。</returns>
        public static bool StartDevice()
        {
            try
            {
                // 启动前配置默认输出通道
                if (!configureDefaultChannels())
                {
                    Logger.Log("ASIO Configure Default Channels Fail", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                // 启动前检查通道是否正确启用
                if (!areChannelsActive())
                {
                    Logger.Log("ASIO channels not properly configured before start", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                if (BassAsio.Start())
                {
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
        /// 检查立体声输出通道是否激活。
        /// </summary>
        /// <returns>如果两个通道都激活则为true，否则为false。</returns>
        private static bool areChannelsActive()
        {
            var channel0Active = BassAsio.ChannelIsActive(false, 0);
            var channel1Active = BassAsio.ChannelIsActive(false, 1);

            Logger.Log($"Channel status - Channel 0: {channel0Active}, Channel 1: {channel1Active}",
                LoggingTarget.Runtime, LogLevel.Debug);

            // 检查两个通道是否都处于激活状态
            return (int)channel0Active != 0 && (int)channel1Active != 0;
        }

        /// <summary>
        /// 停止ASIO设备处理。
        /// </summary>
        public static void StopDevice()
        {
            try
            {
                BassAsio.Stop();
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during ASIO device stop: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        /// <summary>
        /// 获取当前初始化ASIO设备的信息。
        /// </summary>
        /// <returns>ASIO设备信息，如果没有设备初始化则为null。</returns>
        public static AsioInfo? GetCurrentDeviceInfo()
        {
            try
            {
                AsioInfo info;
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
        /// 获取ASIO设备的当前采样率。
        /// </summary>
        /// <returns>当前采样率，如果没有设备初始化或出错则为0。</returns>
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
        /// 获取指定ASIO设备支持的采样率列表。
        /// 此方法临时初始化设备以查询支持的速率，然后释放它。
        /// </summary>
        /// <param name="deviceIndex">要查询的ASIO设备的索引。</param>
        /// <returns>支持的采样率列表，如果无法查询设备则为空列表。</returns>
        public static IEnumerable<double> GetSupportedSampleRates(int deviceIndex)
        {
            var supportedRates = new List<double>();

            try
            {
                if (!tryGetDeviceInfo(deviceIndex, out AsioDeviceInfo deviceInfo))
                {
                    Logger.Log($"Failed to get device info for ASIO device index {deviceIndex}", LoggingTarget.Runtime, LogLevel.Error);
                    return supportedRates;
                }

                FreeDevice();

                // 临时初始化设备进行查询
                if (!tryInitializeDevice(deviceIndex, AsioInitFlags.Thread))
                {
                    Logger.Log($"Failed to temporarily initialize ASIO device {deviceIndex} for rate querying", LoggingTarget.Runtime, LogLevel.Error);
                    return supportedRates;
                }

                foreach (int rate in SUPPORTED_SAMPLE_RATES)
                {
                    try
                    {
                        if (BassAsio.CheckRate(rate))
                        {
                            supportedRates.Add(rate);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Exception while checking sample rate {rate}Hz: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                    }
                }

                FreeDevice();

                Logger.Log($"Found {supportedRates.Count} supported sample rates for ASIO device {deviceInfo.Name}: {string.Join(", ", supportedRates)}", LoggingTarget.Runtime, LogLevel.Important);
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception querying ASIO device sample rates: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);

                FreeDevice();
            }

            return supportedRates;
        }

        /// <summary>
        /// 为ASIO设备配置默认输入和输出通道。
        /// </summary>
        /// <returns>如果通道配置成功则为true，否则为false。</returns>
        private static bool configureDefaultChannels()
        {
            try
            {
                // 获取设备信息以确定可用通道
                var info = GetCurrentDeviceInfo();
                if (info == null)
                    return false;

                Logger.Log($"ASIO device has {info.Value.Inputs} inputs and {info.Value.Outputs} outputs available", LoggingTarget.Runtime, LogLevel.Debug);

                // 尝试配置至少一个立体声输出通道对
                bool channelsConfigured = configureOutputChannels(info.Value);

                if (channelsConfigured)
                {
                    Logger.Log("ASIO output channels configured successfully", LoggingTarget.Runtime, LogLevel.Debug);
                }
                else
                {
                    Logger.Log("Channel configuration failed, falling back to driver default configuration", LoggingTarget.Runtime, LogLevel.Important);
                    // 回退到基本可用性检查
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
        /// 为ASIO设备配置输出通道。
        /// </summary>
        /// <param name="info">ASIO设备信息。</param>
        /// <returns>如果输出通道成功配置则为true，否则为false。</returns>
        private static bool configureOutputChannels(AsioInfo info)
        {
            if (info.Outputs < 2)
            {
                Logger.Log($"Insufficient output channels available ({info.Outputs}), cannot configure stereo output", LoggingTarget.Runtime, LogLevel.Important);
                return false;
            }

            try
            {
                // 配置第一个立体声输出对（通道0和1）
                Logger.Log("Configuring stereo output channels (0 and 1) for ASIO device", LoggingTarget.Runtime, LogLevel.Debug);

                // 创建ASIO过程回调
                AsioProcedure asioCallback = asioProcedure;

                // 启用通道0（左）
                if (!BassAsio.ChannelEnable(false, 0, asioCallback))
                {
                    Logger.Log($"Failed to enable output channel 0: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                // 启用通道1（右）并将其连接到通道0以形成立体声
                if (!BassAsio.ChannelEnable(false, 1, asioCallback))
                {
                    Logger.Log($"Failed to enable output channel 1: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                // 设置输出格式为Float以与我们提供的Float数据匹配
                if (!BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float))
                    Logger.Log($"Failed to set format Float for channel 0: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Error);
                if (!BassAsio.ChannelSetFormat(false, 1, AsioSampleFormat.Float))
                    Logger.Log($"Failed to set format Float for channel 1: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Error);

                // 对齐
                double targetRate = BassAsio.Rate > 0 ? BassAsio.Rate : DEFAULT_SAMPLE_RATE;
                if (!BassAsio.ChannelSetRate(false, 0, targetRate))
                    Logger.Log($"Failed to set rate {targetRate} for channel 0: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Debug);
                if (!BassAsio.ChannelSetRate(false, 1, targetRate))
                    Logger.Log($"Failed to set rate {targetRate} for channel 1: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Debug);

                // 将通道1连接到通道0以形成立体声对
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
        /// ASIO过程回调，用于通道处理。
        /// </summary>
        private static int silenceFrames;

        private static int asioProcedure(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            if (input)
            {
                // 对于输入通道，我们不处理任何内容
                return 0;
            }

            // 对于输出通道，直接从游戏音频系统中提供音频数据
            // 从音频线程获取全局混音器句柄
            int mixerHandle = getGlobalMixerHandle();

            if (mixerHandle == 0)
            {
                // 如果全局混音器不可用，用静音填充
                fillBufferWithSilence(buffer, length);
                return length;
            }

            try
            {
                // 从全局混音器获取音频数据
                // 使用DataFlags.Float标志直接获取float数据
                int bytesRead = Bass.ChannelGetData(mixerHandle, buffer, length | (int)DataFlags.Float);

                if (bytesRead <= 0)
                {
                    // 没有音频数据可用，用静音填充
                    fillBufferWithSilence(buffer, length);
                    if (++silenceFrames % SILENCE_LOG_INTERVAL == 0)
                        Logger.Log($"[AudioDebug] ASIO callback silence count={silenceFrames}, globalMixer={mixerHandle}", LoggingTarget.Runtime, LogLevel.Debug);
                }
                else if (bytesRead < length)
                {
                    // 接收到部分数据，用静音填充其余部分
                    unsafe
                    {
                        float* bufferPtr = (float*)buffer;
                        float* silenceStart = bufferPtr + bytesRead / sizeof(float);
                        int silenceSamples = (length - bytesRead) / sizeof(float);

                        for (int i = 0; i < silenceSamples; i++)
                        {
                            silenceStart[i] = 0.0f;
                        }
                    }
                }

                return length; // 总是返回请求的长度（驱动程序期望完整缓冲区）
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception in ASIO callback: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                fillBufferWithSilence(buffer, length);
                return length;
            }
        }

        /// <summary>
        /// 从音频线程获取全局混音器句柄。
        /// </summary>
        private static int getGlobalMixerHandle()
        {
            // 返回音频线程设置的全局混音器句柄
            return globalMixerHandle;
        }

        /// <summary>
        /// 用静音（零）填充缓冲区。
        /// </summary>
        private static unsafe void fillBufferWithSilence(IntPtr buffer, int length)
        {
            float* bufferPtr = (float*)buffer;

            for (int i = 0; i < length / sizeof(float); i++)
            {
                bufferPtr[i] = 0.0f;
            }
        }

        /// <summary>
        /// 获取ASIO错误代码的描述。
        /// </summary>
        private static string getAsioErrorDescription(int errorCode)
        {
            return errorCode switch
            {
                3 =>
                    "ASIO驱动程序不可用、繁忙、不兼容或打开失败。对于VoiceMeeter驱动程序，确保VoiceMeeter应用程序正在运行。对于硬件ASIO驱动程序，请尝试通过VoiceMeeter路由或确保没有其他应用程序正在使用硬件设备。",
                1 => "ASIO驱动程序不存在或无效。",
                2 => "没有输入/输出通道存在。",
                6 => "不支持的采样格式。ASIO驱动程序可能不支持请求的音频格式。",
                8 => "已初始化。这可能表示驱动程序冲突或清理不当。",
                23 => "设备不存在。ASIO设备可能已断开连接或不可用。",
                _ => $"未知ASIO错误（代码{errorCode}）。"
            };
        }
    }
}
