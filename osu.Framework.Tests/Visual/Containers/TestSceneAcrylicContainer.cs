// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Testing;
using osuTK;

namespace osu.Framework.Tests.Visual.Containers
{
    public partial class TestSceneAcrylicContainer : TestScene
    {
        private bool isWhiteTint = true;

        [BackgroundDependencyLoader]
        private void load(TextureStore textures)
        {
            Children = new Drawable[]
            {
                // Background texture (full screen)
                new Sprite
                {
                    RelativeSizeAxes = Axes.Both,
                    Texture = textures.Get("sample-texture")
                },

                // Left side: Semi-transparent overlay to show original texture
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Width = 0.5f,
                    Child = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Colour4.White.Opacity(0.1f) // Very subtle overlay
                    }
                },

                // Right side: AcrylicContainer with blur effect (buffered container)
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Width = 0.5f,
                    Child = new AcrylicContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        BlurStrength = 50f,
                        TintColour = Colour4.Red.Opacity(0.8f),
                    }
                },

                // Labels
                new SpriteText
                {
                    Text = "Original Texture (No Blur)",
                    Font = FontUsage.Default.With(size: 24),
                    Colour = Colour4.White,
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(-200, 20),
                },
                new SpriteText
                {
                    Text = "AcrylicContainer (Buffered Blur)",
                    Font = FontUsage.Default.With(size: 24),
                    Colour = Colour4.White,
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(200, 20),
                }
            };

            // Blur strength control
            AddSliderStep("blur strength", 0f, 20f, 10f, strength =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null)
                    acrylic.BlurStrength = strength;
            });

            // Tint colour controls
            AddSliderStep("tint alpha", 0f, 1f, 0.8f, alpha =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null)
                    acrylic.TintColour = Colour4.White.Opacity(alpha);
            });

            AddSliderStep("tint red", 0f, 1f, 1f, red =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null)
                {
                    var currentColour = acrylic.TintColour;
                    if (currentColour.TryExtractSingleColour(out var colour))
                        acrylic.TintColour = new Colour4(red, colour.Linear.G, colour.Linear.B, colour.Linear.A);
                }
            });

            AddSliderStep("tint green", 0f, 1f, 1f, green =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null)
                {
                    var currentColour = acrylic.TintColour;
                    if (currentColour.TryExtractSingleColour(out var colour))
                        acrylic.TintColour = new Colour4(colour.Linear.R, green, colour.Linear.B, colour.Linear.A);
                }
            });

            AddSliderStep("tint blue", 0f, 1f, 1f, blue =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null)
                {
                    var currentColour = acrylic.TintColour;
                    if (currentColour.TryExtractSingleColour(out var colour))
                        acrylic.TintColour = new Colour4(colour.Linear.R, colour.Linear.G, blue, colour.Linear.A);
                }
            });

            AddStep("toggle tint colour", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null)
                {
                    isWhiteTint = !isWhiteTint;
                    acrylic.TintColour = isWhiteTint
                        ? Colour4.White.Opacity(0.8f)
                        : Colour4.Blue.Opacity(0.8f);
                }
            });

            // Test different blur scenarios
            AddStep("no blur", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null) acrylic.BlurStrength = 0;
            });
            AddStep("light blur", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null) acrylic.BlurStrength = 5;
            });
            AddStep("medium blur", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null) acrylic.BlurStrength = 10;
            });
            AddStep("heavy blur", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null) acrylic.BlurStrength = 20;
            });

            // Test tint scenarios
            AddStep("no tint", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null) acrylic.TintColour = Colour4.White.Opacity(0);
            });
            AddStep("subtle tint", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null) acrylic.TintColour = Colour4.White.Opacity(0.3f);
            });
            AddStep("medium tint", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null) acrylic.TintColour = Colour4.White.Opacity(0.6f);
            });
            AddStep("strong tint", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null) acrylic.TintColour = Colour4.White.Opacity(0.9f);
            });

            // Debug presets
            AddStep("debug: high contrast", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null)
                {
                    acrylic.BlurStrength = 15f;
                    acrylic.TintColour = Colour4.Red.Opacity(0.7f);
                }
            });

            AddStep("debug: subtle effect", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null)
                {
                    acrylic.BlurStrength = 3f;
                    acrylic.TintColour = Colour4.Black.Opacity(0.2f);
                }
            });

            AddStep("debug: reset to default", () =>
            {
                var acrylic = Children[2] as AcrylicContainer;
                if (acrylic != null)
                {
                    acrylic.BlurStrength = 10f;
                    acrylic.TintColour = Colour4.White.Opacity(0.8f);
                }
            });
        }
    }
}
