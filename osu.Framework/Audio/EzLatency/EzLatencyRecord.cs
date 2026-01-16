// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Audio.EzLatency
{
#nullable disable
    public class EzLatencyRecord
    {
        // high-level fields
        public DateTimeOffset Timestamp { get; set; }
        public double MeasuredMs { get; set; }
        public string Note { get; set; }

        // low-level fields (originating from EzLogModule)
        public double InputTime { get; set; }
        public double JudgeTime { get; set; }
        public double PlaybackTime { get; set; }
        public double DriverTime { get; set; }
        public double OutputHardwareTime { get; set; }
        public double InputHardwareTime { get; set; }
        public double LatencyDifference { get; set; }

        // optional low-level structs copied from AudioThread for richer diagnostics
        public EzLatencyInputData InputData { get; set; }
        public EzLatencyHardwareData HardwareData { get; set; }
    }
}
