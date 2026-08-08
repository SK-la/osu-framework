// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Extensions;
using osu.Framework.Input;
using osu.Framework.Input.Events;
using osu.Framework.Input.StateChanges;
using osu.Framework.Input.States;
using osu.Framework.Testing;
using osuTK;

namespace osu.Framework.Tests.Input
{
    [HeadlessTest]
    public partial class JoystickInputTest : ManualInputManagerTestScene
    {
        /// <summary>
        /// Tests that if the hierarchy is changed while a joystick button is held, the <see cref="Drawable.OnJoystickRelease"/> event is
        /// only propagated to the hierarchy that originally handled <see cref="Drawable.OnJoystickPress"/>.
        /// </summary>
        [Test]
        public void TestJoystickReleaseOnlyPropagatedToOriginalTargets()
        {
            var receptors = new InputReceptor[3];

            AddStep("create hierarchy", () =>
            {
                Children = new Drawable[]
                {
                    receptors[0] = new InputReceptor
                    {
                        Size = new Vector2(100),
                        Press = () => true
                    },
                    receptors[1] = new InputReceptor { Size = new Vector2(100) }
                };
            });

            AddStep("press a button", () => InputManager.PressJoystickButton(JoystickButton.Button1));
            AddStep("add receptor above", () =>
            {
                Add(receptors[2] = new InputReceptor
                {
                    Size = new Vector2(100),
                    Press = () => true,
                    Release = () => true
                });
            });

            AddStep("release key", () => InputManager.ReleaseJoystickButton(JoystickButton.Button1));

            AddAssert("receptor 0 handled key down", () => receptors[0].PressReceived);
            AddAssert("receptor 0 handled key up", () => receptors[0].ReleaseReceived);
            AddAssert("receptor 1 handled key down", () => receptors[1].PressReceived);
            AddAssert("receptor 1 handled key up", () => receptors[1].ReleaseReceived);
            AddAssert("receptor 2 did not handle key down", () => !receptors[2].PressReceived);
            AddAssert("receptor 2 did not handle key up", () => !receptors[2].ReleaseReceived);
        }

        /// <summary>
        /// Tests that an axis declared continuous does not synthesise a directional <see cref="JoystickButton"/>.
        /// </summary>
        /// <remarks>
        /// Any non-zero axis value presses the direction button, with no threshold beyond the handler deadzone. A
        /// turntable rests wherever it was left rather than returning to centre, so its button would stay pressed for
        /// the remainder of the session and break every exactly-matched key combination.
        /// </remarks>
        [Test]
        public void TestContinuousAxisDoesNotSynthesiseDirectionButton()
        {
            InputReceptor receptor = null;

            AddStep("create hierarchy", () => Child = receptor = new InputReceptor { Size = new Vector2(100) });

            AddStep("move ordinary axis", () => InputManager.Input(new JoystickAxisInput(new JoystickAxis(JoystickAxisSource.Axis1, 1f))));
            AddAssert("direction button synthesised", () => receptor.PressReceived);

            AddStep("recentre and reset", () =>
            {
                InputManager.Input(new JoystickAxisInput(new JoystickAxis(JoystickAxisSource.Axis1, 0)));
                receptor.Reset();
            });

            AddStep("move continuous axis", () => InputManager.Input(new JoystickAxisInput(new JoystickAxis(JoystickAxisSource.Axis2, 1f), emitDirectionButtons: false)));
            AddAssert("no direction button synthesised", () => !receptor.PressReceived);
        }

        /// <summary>
        /// Tests which raw button indices survive the check applied where SDL events enter the framework.
        /// </summary>
        [Test]
        public void TestButtonRepresentability()
        {
            Assert.That(JoystickButton.Button1.IsRepresentable(), Is.True);
            Assert.That(JoystickButton.Button128.IsRepresentable(), Is.True);

            // beyond what JoystickButton can name; a device reporting these would otherwise occupy the pressed key set forever.
            Assert.That((JoystickButton.FirstButton + 224).IsRepresentable(), Is.False);
            Assert.That((JoystickButton.Button128 + 1).IsRepresentable(), Is.False);

            // synthesised internally rather than reported by a device, so always allowed through.
            Assert.That(JoystickButton.FirstAxisNegative.IsRepresentable(), Is.True);
            Assert.That(JoystickButton.FirstHatRight.IsRepresentable(), Is.True);
        }

        private partial class InputReceptor : Box
        {
            public bool PressReceived { get; private set; }
            public bool ReleaseReceived { get; private set; }

            public void Reset() => PressReceived = ReleaseReceived = false;

            public Func<bool> Press;
            public Func<bool> Release;

            protected override bool OnJoystickPress(JoystickPressEvent e)
            {
                PressReceived = true;
                return Press?.Invoke() ?? false;
            }

            protected override void OnJoystickRelease(JoystickReleaseEvent e)
            {
                ReleaseReceived = true;
                Release?.Invoke();
            }
        }
    }
}
