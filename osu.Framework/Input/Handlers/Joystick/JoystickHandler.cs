// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osu.Framework.Statistics;

namespace osu.Framework.Input.Handlers.Joystick
{
    public class JoystickHandler : InputHandler
    {
        private static readonly GlobalStatistic<ulong> statistic_total_events = GlobalStatistics.Get<ulong>(StatisticGroupFor<JoystickHandler>(), "Total events");

        public BindableFloat DeadzoneThreshold { get; } = new BindableFloat(0.1f)
        {
            MinValue = 0,
            MaxValue = 0.95f,
            Precision = 0.005f,
        };

        /// <summary>
        /// 按设备区分的轴变化（原始值，未经本 Handler 死区重标定）。
        /// </summary>
        public event Action<JoystickDeviceAxis>? DeviceAxisChanged;

        public override string Description => "Joystick / Gamepad";

        public override bool IsActive => true;

        public override bool Initialize(GameHost host)
        {
            if (!base.Initialize(host))
                return false;

            if (!(host.Window is ISDLWindow window))
                return false;

            Enabled.BindValueChanged(e =>
            {
                if (e.NewValue)
                {
                    window.JoystickButtonDown += enqueueJoystickButtonDown;
                    window.JoystickButtonUp += enqueueJoystickButtonUp;
                    window.JoystickAxisChanged += enqueueJoystickAxisChanged;
                    window.JoystickDeviceAxisChanged += enqueueJoystickDeviceAxisChanged;
                }
                else
                {
                    window.JoystickButtonDown -= enqueueJoystickButtonDown;
                    window.JoystickButtonUp -= enqueueJoystickButtonUp;
                    window.JoystickAxisChanged -= enqueueJoystickAxisChanged;
                    window.JoystickDeviceAxisChanged -= enqueueJoystickDeviceAxisChanged;
                }
            }, true);

            return true;
        }

        private void enqueueJoystickEvent(IInput evt)
        {
            PendingInputs.Enqueue(evt);
            FrameStatistics.Increment(StatisticsCounterType.JoystickEvents);
            statistic_total_events.Value++;
        }

        // the window layer already drops (and reports) unrepresentable indices; this is a silent net for any other source.
        private void enqueueJoystickButtonDown(JoystickButton button)
        {
            if (!button.IsRepresentable())
                return;

            enqueueJoystickEvent(new JoystickButtonInput(button, true));
        }

        private void enqueueJoystickButtonUp(JoystickButton button)
        {
            if (!button.IsRepresentable())
                return;

            enqueueJoystickEvent(new JoystickButtonInput(button, false));
        }

        private volatile ImmutableHashSet<JoystickAxisSource> continuousAxes = ImmutableHashSet<JoystickAxisSource>.Empty;

        /// <summary>
        /// Declares which axes report a continuous position rather than a direction, a turntable being the typical case.
        /// No directional <see cref="JoystickButton"/> is synthesised for these.
        /// </summary>
        /// <remarks>
        /// Such an axis rests wherever it was last left rather than returning to centre, so its synthesised button would
        /// stay pressed for the remainder of the session and break every exactly-matched key combination.
        /// </remarks>
        public void SetContinuousAxes(IEnumerable<JoystickAxisSource> axes)
        {
            var updated = axes.ToImmutableHashSet();
            var previous = continuousAxes;

            if (updated.SetEquals(previous))
                return;

            continuousAxes = updated;

            // an axis already resting off-centre reports nothing further until moved, so release explicitly rather
            // than waiting for the next movement to clear the button.
            foreach (var axis in updated.Except(previous))
            {
                enqueueJoystickEvent(new JoystickButtonInput(JoystickButton.FirstAxisNegative + (int)axis, false));
                enqueueJoystickEvent(new JoystickButtonInput(JoystickButton.FirstAxisPositive + (int)axis, false));
            }
        }

        /// <summary>
        /// Enqueues a <see cref="JoystickAxisInput"/> taking into account the axis deadzone.
        /// </summary>
        private void enqueueJoystickAxisChanged(JoystickAxisSource source, float value) =>
            enqueueJoystickEvent(new JoystickAxisInput(
                new JoystickAxis(source, RescaleByDeadzone(value, DeadzoneThreshold.Value)),
                emitDirectionButtons: !continuousAxes.Contains(source)));

        private void enqueueJoystickDeviceAxisChanged(JoystickDeviceAxis axis) => DeviceAxisChanged?.Invoke(axis);

        internal static float RescaleByDeadzone(float axisValue, float deadzoneThreshold)
        {
            float absoluteValue = Math.Abs(axisValue);

            if (absoluteValue < deadzoneThreshold)
                return 0;

            // rescale the given axis value such that the edge of the deadzone is considered the "new zero".
            float absoluteRescaled = (absoluteValue - deadzoneThreshold) / (1f - deadzoneThreshold);
            return Math.Sign(axisValue) * absoluteRescaled;
        }
    }
}
