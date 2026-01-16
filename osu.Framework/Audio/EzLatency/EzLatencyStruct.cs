// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// EzOsuLatency 输入数据结构体
    /// </summary>
    public struct EzLatencyInputData
    {
        public double InputTime;
        public object KeyValue;
        public double JudgeTime;
        public double PlaybackTime;
    }

    /// <summary>
    /// EzOsuLatency 硬件数据结构体
    /// </summary>
    public struct EzLatencyHardwareData
    {
        public double DriverTime;
        public double OutputHardwareTime;
        public double InputHardwareTime;
        public double LatencyDifference;
    }

    public enum AudioOutputMode
    {
        Default,
        WasapiShared,
        WasapiExclusive,
        Asio,
    }
}
