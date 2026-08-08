// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Testing;
using osuTK;
using osuTK.Input;

namespace osu.Framework.Tests.Input
{
    [HeadlessTest]
    public partial class KeyBindingInputTest : ManualInputManagerTestScene
    {
        /// <summary>
        /// Tests that if the current input queue is changed, drawables that originally handled <see cref="IKeyBindingHandler{T}.OnPressed"/>
        /// will receive a corresponding <see cref="IKeyBindingHandler{T}.OnReleased"/> event.
        /// </summary>
        [Test]
        public void TestReleaseAlwaysPressedToOriginalTargets()
        {
            InputReceptor receptorBelow = null;
            InputReceptor receptorAbove = null;

            AddStep("setup", () =>
            {
                Child = new TestKeyBindingContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        receptorBelow = new InputReceptor(true)
                        {
                            Size = new Vector2(100),
                        },
                        receptorAbove = new InputReceptor(false)
                        {
                            Size = new Vector2(100),
                            Position = new Vector2(100),
                        }
                    }
                };
            });

            // Input is positional

            AddStep("move mouse to receptorBelow", () => InputManager.MoveMouseTo(receptorBelow));
            AddStep("press keybind1", () => InputManager.PressKey(Key.Up));
            AddAssert("receptorBelow received press", () => receptorBelow.PressedReceived);

            AddStep("move mouse to receptorAbove", () => InputManager.MoveMouseTo(receptorAbove));
            AddStep("release keybind1", () => InputManager.ReleaseKey(Key.Up));
            AddAssert("receptorBelow received release", () => receptorBelow.ReleasedReceived);
        }

        /// <summary>
        /// Tests that a key with no <see cref="InputKey"/> mapping does not stop exactly-matched bindings from firing.
        /// </summary>
        /// <remarks>
        /// Such a key converts to <see cref="InputKey.None"/>, which belongs to no binding and would therefore fail
        /// every exact match for the remainder of the session if allowed into the pressed key set.
        /// </remarks>
        [Test]
        public void TestUnmappedKeyDoesNotBreakExactMatching()
        {
            InputReceptor receptor = null;

            AddStep("setup", () =>
            {
                Child = new TestExactKeyBindingContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = receptor = new InputReceptor(true) { Size = new Vector2(100) }
                };
            });

            AddStep("move mouse to receptor", () => InputManager.MoveMouseTo(receptor));
            AddStep("press unmapped key", () => InputManager.PressKey(Key.Unknown));

            AddStep("press ctrl+F11", () =>
            {
                InputManager.PressKey(Key.ControlLeft);
                InputManager.PressKey(Key.F11);
            });

            AddAssert("binding still fired", () => receptor.PressedReceived);

            AddStep("release all", () =>
            {
                InputManager.ReleaseKey(Key.F11);
                InputManager.ReleaseKey(Key.ControlLeft);
                InputManager.ReleaseKey(Key.Unknown);
            });
        }

        private partial class InputReceptor : Box, IKeyBindingHandler<TestKeyBinding>
        {
            public bool PressedReceived { get; private set; }
            public bool ReleasedReceived { get; private set; }

            private readonly bool keybindings;

            public InputReceptor(bool keybindings)
            {
                this.keybindings = keybindings;
            }

            public override bool HandlePositionalInput => true; // IsHovered is used

            protected override bool OnKeyDown(KeyDownEvent e)
            {
                if (keybindings)
                    return false;

                if (!IsHovered)
                    return false;

                return true;
            }

            protected override void OnKeyUp(KeyUpEvent e)
            {
            }

            public bool OnPressed(KeyBindingPressEvent<TestKeyBinding> e)
            {
                if (!keybindings)
                    return false;

                if (!IsHovered)
                    return false;

                PressedReceived = true;
                return true;
            }

            public void OnReleased(KeyBindingReleaseEvent<TestKeyBinding> e)
            {
                if (!keybindings)
                    return;

                ReleasedReceived = true;
            }
        }

        private partial class TestKeyBindingContainer : KeyBindingContainer<TestKeyBinding>, IHandleGlobalKeyboardInput
        {
            public TestKeyBindingContainer()
                : base(SimultaneousBindingMode.Unique, KeyCombinationMatchingMode.Modifiers)
            {
            }

            public override IEnumerable<IKeyBinding> DefaultKeyBindings => new[]
            {
                new KeyBinding(InputKey.Up, TestKeyBinding.Binding1),
                new KeyBinding(InputKey.Down, TestKeyBinding.Binding2),
            };
        }

        private partial class TestExactKeyBindingContainer : KeyBindingContainer<TestKeyBinding>, IHandleGlobalKeyboardInput
        {
            public TestExactKeyBindingContainer()
                : base(SimultaneousBindingMode.Unique, KeyCombinationMatchingMode.Exact)
            {
            }

            public override IEnumerable<IKeyBinding> DefaultKeyBindings => new[]
            {
                new KeyBinding(new[] { InputKey.Control, InputKey.F11 }, TestKeyBinding.Binding1),
            };
        }

        private enum TestKeyBinding
        {
            Binding1,
            Binding2
        }
    }
}
