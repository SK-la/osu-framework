// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Input;

namespace osu.Framework.Extensions
{
    public static class JoystickButtonExtensions
    {
        /// <summary>
        /// Whether a button index reported by a device can be represented by <see cref="JoystickButton"/>.
        /// </summary>
        /// <remarks>
        /// SDL reports a raw per-device button index. An index past <see cref="JoystickButton.Button128"/> has no
        /// representation here (nor in <see cref="Input.Bindings.InputKey"/>) so it can never be bound, but if let
        /// through it still occupies the pressed key set for the remainder of the session and breaks every
        /// exactly-matched key combination.
        /// </remarks>
        public static bool IsRepresentable(this JoystickButton button)
        {
            // axis and hat pseudo-buttons are synthesised internally and always in range.
            if (button >= JoystickButton.FirstAxisNegative)
                return true;

            return button >= JoystickButton.FirstButton && button <= JoystickButton.Button128;
        }
    }
}
