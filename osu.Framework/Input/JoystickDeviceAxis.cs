// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Input
{
    /// <summary>
    /// 带设备标识的摇杆轴采样（多手柄/多转盘互不覆盖）。
    /// </summary>
    public readonly struct JoystickDeviceAxis
    {
        /// <summary>
        /// SDL 实例 ID（重插可能变化）。
        /// </summary>
        public readonly uint InstanceId;

        /// <summary>
        /// 设备 GUID 字符串（跨会话相对稳定；两台同型号仍可不同）。
        /// </summary>
        public readonly string Guid;

        /// <summary>
        /// 人类可读设备名。
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// 该设备上的轴下标（0-based）。
        /// </summary>
        public readonly int AxisIndex;

        /// <summary>
        /// 轴值，范围约 [-1, 1]（未经 JoystickHandler 死区重标定）。
        /// </summary>
        public readonly float Value;

        public JoystickDeviceAxis(uint instanceId, string? guid, string? name, int axisIndex, float value)
        {
            InstanceId = instanceId;
            Guid = guid ?? string.Empty;
            Name = name ?? string.Empty;
            AxisIndex = axisIndex;
            Value = value;
        }
    }
}
