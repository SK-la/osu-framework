// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Audio.EzLatency
{
#nullable disable
    public class EzLatencyRecord
    {
        // 高级字段
        public DateTimeOffset Timestamp { get; set; }
        public double MeasuredMs { get; set; }
        public string Note { get; set; }

        // 低级字段
        public double InputTime { get; set; }
        public double JudgeTime { get; set; }
        public double PlaybackTime { get; set; }
        public double DriverTime { get; set; }
        public double OutputHardwareTime { get; set; }
        public double InputHardwareTime { get; set; }
        public double LatencyDifference { get; set; }

        // 可选的低级结构体，包含完整的输入和硬件数据
        public EzLatencyInputData InputData { get; set; }
        public EzLatencyHardwareData HardwareData { get; set; }
        
        /// <summary>
        /// 检查记录是否包含完整的输入和硬件数据
        /// </summary>
        public bool IsComplete => InputData.IsValid && HardwareData.IsValid;
    }
}