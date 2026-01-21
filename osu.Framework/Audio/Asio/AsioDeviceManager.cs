// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// 未指定、或设置采样率失败时，使用默认采样率48kHz，性能较好。
        /// </summary>
        public const int DEFAULT_SAMPLE_RATE = 48000;

        /// <summary>
        /// 最大重试次数，用于处理设备繁忙的情况。
        /// </summary>
        private const int max_retry_count = 3;

        /// <summary>
        /// 重试间隔时间（毫秒）。
        /// </summary>
        private const int retry_delay_ms = 100;

        /// <summary>
        /// 设备释放延迟时间（毫秒）。
        /// </summary>
        private const int device_free_delay_ms = 100;

        /// <summary>
        /// 强制重置延迟时间（毫秒）。
        /// </summary>
        private const int force_reset_delay_ms = 200;

        /// <summary>
        /// 采样率容差，用于验证设置是否成功。
        /// </summary>
        private const double sample_rate_tolerance = 1.0;

        /// <summary>
        /// 静音帧日志间隔。
        /// </summary>
        private const int silence_log_interval = 200;

        /// <summary>
        /// ASIO音频路由的全局混音器句柄。
        /// 当ASIO设备初始化时，由音频线程设置。
        /// </summary>
        private static int globalMixerHandle;
        // 自动重新初始化监控
        private static System.Threading.CancellationTokenSource? reinitMonitorCts;
        private static int? lastInitializedDeviceIndex;
        private static double? lastRequestedSampleRate;
        private static int? lastRequestedBufferSize;
        // 用于防止重复的自动重新初始化同时进行
        private static int reinitInProgress = 0;
        private static Action? notifierHandler;

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
        private static bool tryInitializeDevice(int deviceIndex, AsioInitFlags flags, bool allowRetries = true)
        {
            if (!allowRetries)
            {
                // 单次尝试（aggressive 路径会使用此模式）
                if (BassAsio.Init(deviceIndex, flags))
                    return true;

                var bassError = BassAsio.LastError;
                Logger.Log($"ASIO one-shot initialization failed with flags {flags}: {bassError} (Code: {(int)bassError}) - {getAsioErrorDescription((int)bassError)}",
                    LoggingTarget.Runtime, LogLevel.Important);

                return false;
            }

            for (int retryCount = 0; retryCount < max_retry_count; retryCount++)
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
                    if (retryCount < max_retry_count - 1)
                    {
                        Logger.Log($"Device busy, waiting {retry_delay_ms}ms before retry {retryCount + 1}/{max_retry_count}", LoggingTarget.Runtime, LogLevel.Important);
                        Thread.Sleep(retry_delay_ms);
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

            if (Math.Abs(actualRate - rate) >= sample_rate_tolerance)
            {
                Logger.Log($"Failed to set ASIO device sample rate to {rate}Hz (actual: {actualRate}Hz)", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }

            Logger.Log($"Successfully set ASIO device sample rate to {rate}Hz", LoggingTarget.Runtime, LogLevel.Debug);
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
                    _ = BassAsio.DeviceCount.GetHashCode(); // Simple operation to trigger any DLL loading issues
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
        /// 初始化ASIO设备。
        /// </summary>
        /// <param name="deviceIndex">要初始化的ASIO设备的索引。</param>
        /// <param name="sampleRateToTry">要尝试的采样率。如果为null，则使用默认48000Hz。</param>
        /// <param name="bufferSize">ASIO缓冲区大小。如果为null，则使用默认128。</param>
        /// <returns>如果初始化成功则为true，否则为false。</returns>
        /// <param name="waitForDevice">如果为 true，则在设备被占用时阻塞等待直到可用或超时（更“强制”的行为）。</param>
        /// <param name="waitTimeoutMs">等待超时时间（毫秒）。</param>
        /// <param name="aggressive">如果为 true，则使用一次性（无重试）强制初始化路径（只检测占用进程，不终止）。</param>
        public static bool InitializeDevice(int deviceIndex, double? sampleRateToTry = null, int? bufferSize = null, bool waitForDevice = false, int waitTimeoutMs = 30000, bool aggressive = false)
        {
            try
            {
                Logger.Log(
                    $"InitializeDevice called with deviceIndex={deviceIndex}, sampleRateToTry={sampleRateToTry}, bufferSize={bufferSize}, waitForDevice={waitForDevice}, aggressive={aggressive}",
                    LoggingTarget.Runtime,
                    LogLevel.Debug);

                // 获取设备信息
                if (!tryGetDeviceInfo(deviceIndex, out AsioDeviceInfo deviceInfo))
                    return false;

                Logger.Log($"Initializing ASIO device: {deviceInfo.Name} (Driver: {deviceInfo.Driver})", LoggingTarget.Runtime, LogLevel.Debug);

                // 释放之前的设备，确保完全清理
                FreeDevice();

                // 注意：BassAsio没有直接的方法来设置缓冲区大小
                // ASIO缓冲区大小主要由驱动程序决定，无法在运行时动态设置
                // 我们只能记录期望的缓冲区大小用于日志目的

                // 初始化设备：尝试多种 init flags。根据 waitForDevice 可选阻塞直到成功或超时。
                var initFlagsCandidates = new[] { AsioInitFlags.Thread, AsioInitFlags.None };

                bool initialized = false;

                if (aggressive)
                {
                    // 更激进的一次性路径：先强制清理本进程状态
                    ForceReset();
                    Thread.Sleep(50);

                    // 检测可能占用 bassasio.dll 的进程并记录 PID/名称（不终止）
                    var occupying = detectProcessesUsingBassAsio();

                    if (occupying.Count > 0)
                    {
                        Logger.Log($"Detected processes using bassasio.dll: {string.Join(", ", occupying.Select(p => $"{p.Pid}:{p.Name}"))}", LoggingTarget.Runtime, LogLevel.Important);
                    }

                    // 对每个 flags 只尝试一次（不进行内部重试）
                    foreach (var flags in initFlagsCandidates)
                    {
                        Logger.Log($"(aggressive) Attempting ASIO Init on device {deviceIndex} with flags {flags}", LoggingTarget.Runtime, LogLevel.Debug);

                        if (tryInitializeDevice(deviceIndex, flags, allowRetries: false))
                        {
                            initialized = true;
                            break;
                        }

                        Logger.Log($"(aggressive) ASIO init one-shot failed for flags {flags}: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Important);
                    }
                }
                else
                {
                    var deadline = waitForDevice ? DateTime.UtcNow.AddMilliseconds(waitTimeoutMs) : DateTime.UtcNow;

                    do
                    {
                        foreach (var flags in initFlagsCandidates)
                        {
                            Logger.Log($"Attempting ASIO Init on device {deviceIndex} with flags {flags}", LoggingTarget.Runtime, LogLevel.Debug);

                            if (tryInitializeDevice(deviceIndex, flags))
                            {
                                initialized = true;
                                break;
                            }

                            var err = BassAsio.LastError;
                            Logger.Log($"ASIO init attempt failed (device={deviceIndex}, flags={flags}): {err} (Code: {(int)err})", LoggingTarget.Runtime, LogLevel.Important);

                            // 在尝试不同 flags 之前尝试强制重置以清理驱动状态
                            ForceReset();
                            Thread.Sleep(retry_delay_ms);
                        }

                        if (initialized) break;

                        if (!waitForDevice)
                            break;

                        // 如果需要等待，短暂延迟后再次尝试，直到超时
                        Thread.Sleep(200);
                    } while (DateTime.UtcNow < deadline);

                    if (!initialized)
                    {
                        Logger.Log($"Failed to initialize ASIO device {deviceIndex} after attempting flags and waits", LoggingTarget.Runtime, LogLevel.Error);
                        return false;
                    }
                }

                // 尝试采样率：使用传入的值或默认48000
                double rateToTry = sampleRateToTry ?? 48000.0;

                double successfulRate;

                if (!trySetSampleRate(rateToTry))
                {
                    Logger.Log($"Failed to set sample rate {rateToTry}Hz for ASIO device {deviceIndex}, will attempt to use device-reported rate if available",
                        LoggingTarget.Runtime, LogLevel.Important);

                    double deviceRate = 0;

                    try
                    {
                        deviceRate = BassAsio.Rate;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Could not read device sample rate after failed set: {ex.Message}", LoggingTarget.Runtime, LogLevel.Debug);
                    }

                    if (deviceRate > 0)
                    {
                        successfulRate = deviceRate;
                        Logger.Log($"Using ASIO device reported rate {deviceRate}Hz instead of requested {rateToTry}Hz", LoggingTarget.Runtime, LogLevel.Important);
                    }
                    else
                    {
                        Logger.Log($"ASIO device did not report a usable rate; failing initialization", LoggingTarget.Runtime, LogLevel.Error);
                        FreeDevice();
                        return false;
                    }
                }
                else
                {
                    successfulRate = rateToTry;
                }

                Logger.Log($"ASIO device {deviceIndex} initialized successfully with sample rate {successfulRate}Hz", LoggingTarget.Runtime, LogLevel.Important);

                // 记录当前初始化参数并启动后台监控（检测驱动侧参数更改）。
                lastInitializedDeviceIndex = deviceIndex;
                lastRequestedSampleRate = sampleRateToTry ?? successfulRate;
                lastRequestedBufferSize = bufferSize;
                // 先尝试使用 CoreAudio 通知（MMDevice）进行事件驱动的变更检测
                try
                {
                    notifierHandler = () =>
                    {
                        // 确保不会并发执行多个重新初始化
                        if (System.Threading.Interlocked.CompareExchange(ref reinitInProgress, 1, 0) != 0)
                            return;

                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                Logger.Log("ASIO device notification received from system, triggering reinit.", LoggingTarget.Runtime, LogLevel.Important);
                                FreeDevice();
                                if (lastInitializedDeviceIndex.HasValue)
                                {
                                    InitializeDevice(lastInitializedDeviceIndex.Value, lastRequestedSampleRate, lastRequestedBufferSize);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Exception handling ASIO device notification: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                            }
                            finally
                            {
                                System.Threading.Interlocked.Exchange(ref reinitInProgress, 0);
                            }
                        });
                    };

                    AsioDeviceNotifier.DeviceChanged += notifierHandler;
                    AsioDeviceNotifier.Start();
                }
                catch
                {
                    // 如果事件驱动不可用，继续使用轮询作为后备
                }

                // 保留原有的轮询监控作为后备方案
                startReinitMonitor(deviceIndex);
                return true;
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
                // 停止监控（如果在运行）
                stopReinitMonitor();

                // 取消订阅并停止系统通知
                try
                {
                    if (notifierHandler != null)
                    {
                        AsioDeviceNotifier.DeviceChanged -= notifierHandler;
                        notifierHandler = null;
                    }

                    AsioDeviceNotifier.Stop();
                }
                catch { }

                // 先停止音频处理
                BassAsio.Stop();

                // 等待一小段时间确保停止完成
                Thread.Sleep(100);

                // 然后释放设备
                BassAsio.Free();

                // 再等待一段时间确保设备完全释放
                Thread.Sleep(device_free_delay_ms);
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
                // 停止监控以避免在重置过程中触发重新初始化
                stopReinitMonitor();

                // 取消订阅并停止系统通知
                try
                {
                    if (notifierHandler != null)
                    {
                        AsioDeviceNotifier.DeviceChanged -= notifierHandler;
                        notifierHandler = null;
                    }

                    AsioDeviceNotifier.Stop();
                }
                catch { }

                // 先停止设备
                BassAsio.Stop();

                // 短暂延迟
                Thread.Sleep(50);

                // 释放设备
                BassAsio.Free();

                // 更长的延迟确保完全重置
                Thread.Sleep(force_reset_delay_ms);

                globalMixerHandle = 0;
                Logger.Log("ASIO Force Reset", LoggingTarget.Runtime, LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during ASIO force reset: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        /// <summary>
        /// 检查当前ASIO设备是否正在运行
        /// </summary>
        /// <returns>如果ASIO设备正在运行则为true，否则为false</returns>
        public static bool IsDeviceRunning()
        {
            try
            {
                return BassAsio.IsStarted;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 安全地切换ASIO设备，先完全释放当前设备再初始化新设备。
        /// </summary>
        /// <param name="newDeviceIndex">新设备的索引</param>
        /// <param name="sampleRateToTry">要尝试的采样率。如果为null，则使用默认48000Hz。</param>
        /// <param name="bufferSize">ASIO缓冲区大小。如果为null，则使用默认128。</param>
        /// <returns>如果切换成功则为true，否则为false。</returns>
        public static bool SwitchToDevice(int newDeviceIndex, double? sampleRateToTry = null, int? bufferSize = null)
        {
            Logger.Log($"Switching ASIO device to index {newDeviceIndex}, sampleRate: {sampleRateToTry}, bufferSize: {bufferSize}", LoggingTarget.Runtime, LogLevel.Important);

            // 确保设备没有在运行
            if (IsDeviceRunning())
            {
                StopDevice();
            }

            // 先完全释放当前设备
            FreeDevice();

            // 添加足够长的延迟确保设备完全释放
            Thread.Sleep(1000);

            // 然后初始化新设备
            return InitializeDevice(newDeviceIndex, sampleRateToTry, bufferSize);
        }

        /// <summary>
        /// 启动ASIO设备处理。
        /// </summary>
        /// <returns>如果启动成功则为true，否则为false。</returns>
        public static bool StartDevice()
        {
            try
            {
                // 如果设备已经在运行，先停止
                if (IsDeviceRunning())
                {
                    Logger.Log("ASIO device already running, stopping before restart", LoggingTarget.Runtime, LogLevel.Debug);
                    StopDevice();

                    // 稍作延迟
                    Thread.Sleep(100);
                }

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
                    Logger.Log("ASIO device started successfully", LoggingTarget.Runtime, LogLevel.Debug);
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
                if (IsDeviceRunning())
                {
                    BassAsio.Stop();
                    Logger.Log("ASIO device stopped", LoggingTarget.Runtime, LogLevel.Debug);
                }
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
                bool channelsConfigured = configureStereoChannels(info.Value);

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

        #region 配置双通道输出

        /// <summary>
        /// 为ASIO设备配置输出通道。
        /// </summary>
        /// <param name="info">ASIO设备信息。</param>
        /// <returns>如果输出通道成功配置则为true，否则为false。</returns>
        private static bool configureStereoChannels(AsioInfo info)
        {
            try
            {
                AsioProcedure asioCallback = asioProcedure;

                int outputs = Math.Max(0, info.Outputs);
                if (outputs < 2)
                {
                    Logger.Log($"Not enough ASIO outputs ({outputs}) to configure stereo", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                var enabled = new List<int>();
                double targetRate = BassAsio.Rate > 0 ? BassAsio.Rate : DEFAULT_SAMPLE_RATE;

                // 遍历输出通道，启用并配置首两个可用通道
                for (int ch = 0; ch < outputs && enabled.Count < 2; ch++)
                {
                    Logger.Log($"Attempting to enable ASIO output channel {ch}", LoggingTarget.Runtime, LogLevel.Debug);

                    if (!BassAsio.ChannelEnable(false, ch, asioCallback))
                    {
                        Logger.Log($"Failed to enable output channel {ch}: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Debug);
                        continue;
                    }

                    // 尝试设置格式和速率（容错，记录但不失败）
                    if (!BassAsio.ChannelSetFormat(false, ch, AsioSampleFormat.Float))
                        Logger.Log($"Failed to set format Float for channel {ch}: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Debug);

                    if (!BassAsio.ChannelSetRate(false, ch, targetRate))
                        Logger.Log($"Failed to set rate {targetRate} for channel {ch}: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Debug);

                    enabled.Add(ch);
                }

                if (enabled.Count < 2)
                {
                    Logger.Log($"Could not enable two output channels for ASIO device; enabled count={enabled.Count}", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                int left = enabled[0];
                int right = enabled[1];

                // 尝试将右通道 join 到左通道以形成立体声对
                if (!BassAsio.ChannelJoin(false, right, left))
                {
                    Logger.Log($"Failed to join output channels {left} and {right}: {BassAsio.LastError}", LoggingTarget.Runtime, LogLevel.Debug);
                    // 有些驱动不需要 join，继续检查激活状态
                }

                // 最终检查通道激活情况
                var leftActive = BassAsio.ChannelIsActive(false, left);
                var rightActive = BassAsio.ChannelIsActive(false, right);

                Logger.Log($"Channel status - Ch {left}: {leftActive}, Ch {right}: {rightActive}", LoggingTarget.Runtime, LogLevel.Debug);

                if ((int)leftActive == 0 || (int)rightActive == 0)
                {
                    Logger.Log($"ASIO channels {left} or {right} are not active after configuration", LoggingTarget.Runtime, LogLevel.Error);
                    return false;
                }

                Logger.Log($"Stereo output channels configured successfully (left={left}, right={right})", LoggingTarget.Runtime, LogLevel.Debug);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception during output channel configuration: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }
        }

        #endregion

        /// <summary>
        /// 检测可能加载了 bassasio.dll 的进程并返回 PID/名称列表（不终止进程）。
        /// </summary>
        private static List<(int Pid, string Name)> detectProcessesUsingBassAsio()
        {
            var result = new List<(int, string)>();

            try
            {
                var processes = Process.GetProcesses();

                foreach (var proc in processes)
                {
                    try
                    {
                        foreach (ProcessModule mod in proc.Modules)
                        {
                            if (mod.ModuleName?.Equals("bassasio.dll", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                result.Add((proc.Id, proc.ProcessName));
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // 忽略无法访问模块的进程
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception while enumerating processes for bassasio: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }

            return result;
        }

        private static void startReinitMonitor(int deviceIndex, int checkIntervalMs = 1000)
        {
            try
            {
                stopReinitMonitor();

                reinitMonitorCts = new System.Threading.CancellationTokenSource();
                var token = reinitMonitorCts.Token;

                System.Threading.Tasks.Task.Run(() =>
                {
                    AsioInfo lastInfo = default;
                    try
                    {
                        BassAsio.GetInfo(out lastInfo);
                    }
                    catch { }

                    double lastRate = 0;
                    try { lastRate = BassAsio.Rate; } catch { }

                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            // 检查采样率或通道数变化
                            AsioInfo info;
                            if (BassAsio.GetInfo(out info))
                            {
                                double rate = 0;
                                try { rate = BassAsio.Rate; } catch { }

                                if (info.Inputs != lastInfo.Inputs || info.Outputs != lastInfo.Outputs || Math.Abs(rate - lastRate) > 0.5)
                                {
                                    Logger.Log($"ASIO device parameters changed (inputs:{lastInfo.Inputs}->{info.Inputs}, outputs:{lastInfo.Outputs}->{info.Outputs}, rate:{lastRate}->{rate}). Triggering reinit.", LoggingTarget.Runtime, LogLevel.Important);

                                    // 执行重新初始化：释放并使用原参数重新初始化
                                    try
                                    {
                                        FreeDevice();

                                        if (lastInitializedDeviceIndex.HasValue)
                                        {
                                            InitializeDevice(lastInitializedDeviceIndex.Value, lastRequestedSampleRate, lastRequestedBufferSize);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Log($"Exception while auto-reinitializing ASIO: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                                    }

                                    // 更新本地缓存
                                    lastInfo = info;
                                    lastRate = rate;
                                }
                            }
                        }
                        catch
                        {
                            // 忽略单次检测错误
                        }

                        System.Threading.Thread.Sleep(checkIntervalMs);
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to start ASIO reinit monitor: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        private static void stopReinitMonitor()
        {
            try
            {
                if (reinitMonitorCts != null)
                {
                    reinitMonitorCts.Cancel();
                    reinitMonitorCts.Dispose();
                    reinitMonitorCts = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to stop ASIO reinit monitor: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
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
                    if (++silenceFrames % silence_log_interval == 0)
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
        /// 安全枚举ASIO设备，并确保正确处理错误。
        /// </summary>
        /// <returns> 一个可枚举的ASIO设备列表。</returns>
        public static IEnumerable<(int Index, string Name)> EnumerateAsioDevices()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return Enumerable.Empty<(int, string)>();

            try
            {
                return AvailableDevices;
            }
            catch (DllNotFoundException)
            {
                // ASIO native library not available - this is expected in some test environments
                return Enumerable.Empty<(int, string)>();
            }
            catch (EntryPointNotFoundException)
            {
                // ASIO native library not available - this is expected in some test environments
                return Enumerable.Empty<(int, string)>();
            }
            catch (Exception ex)
            {
                // Log other unexpected exceptions but don't fail
                Logger.Log($"Unexpected error enumerating ASIO devices: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return Enumerable.Empty<(int, string)>();
            }
        }

        /// <summary>
        /// Finds the ASIO device index for a given device name.
        /// </summary>
        /// <param name="deviceName">The name of the ASIO device to find.</param>
        /// <returns>The device index if found, null otherwise.</returns>
        public static int? FindAsioDeviceIndex(string deviceName)
        {
            foreach (var device in EnumerateAsioDevices())
            {
                if (device.Name == deviceName)
                    return device.Index;
            }

            return null;
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

// #region 获取受支持的采样率

// /// <summary>
// /// 获取带有支持采样率的可用ASIO设备列表。
// /// 注意：此操作可能较慢，因为需要为每个设备查询支持的采样率。
// /// </summary>
// public static IEnumerable<(int Index, string Name, double[] SupportedSampleRates)> AvailableDevicesWithSampleRates
// {
//     get
//     {
//         foreach (var (index, name) in AvailableDevices)
//         {
//             double[] supportedRates = GetSupportedSampleRates(index).ToArray();
//             yield return (index, name, supportedRates);
//         }
//     }
// }

// /// <summary>
// /// 获取指定ASIO设备支持的采样率列表。
// /// 此方法临时初始化设备以查询支持的速率，然后释放它。
// /// </summary>
// /// <param name="deviceIndex">要查询的ASIO设备的索引。</param>
// /// <returns>支持的采样率列表，如果无法查询设备则为空列表。</returns>
// public static IEnumerable<double> GetSupportedSampleRates(int deviceIndex)
// {
//     var supportedRates = new List<double>();
//
//     try
//     {
//         if (!tryGetDeviceInfo(deviceIndex, out AsioDeviceInfo deviceInfo))
//         {
//             Logger.Log($"Failed to get device info for ASIO device index {deviceIndex}", LoggingTarget.Runtime, LogLevel.Error);
//             return supportedRates;
//         }
//
//         FreeDevice();
//
//         // 临时初始化设备进行查询
//         if (!tryInitializeDevice(deviceIndex, AsioInitFlags.Thread))
//         {
//             Logger.Log($"Failed to temporarily initialize ASIO device {deviceIndex} for rate querying", LoggingTarget.Runtime, LogLevel.Error);
//             return supportedRates;
//         }
//
//         // 只检查默认 48000 是否支持
//         int rate = 48000;
//
//         try
//         {
//             if (BassAsio.CheckRate(rate))
//             {
//                 supportedRates.Add(rate);
//             }
//         }
//         catch (Exception ex)
//         {
//             Logger.Log($"Exception while checking sample rate {rate}Hz: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
//         }
//
//         FreeDevice();
//
//         Logger.Log($"Found {supportedRates.Count} supported sample rates for ASIO device {deviceInfo.Name}: {string.Join(", ", supportedRates)}", LoggingTarget.Runtime, LogLevel.Important);
//     }
//     catch (Exception ex)
//     {
//         Logger.Log($"Exception querying ASIO device sample rates: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
//
//         FreeDevice();
//     }
//
//     return supportedRates;
// }

// #endregion
