// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Visual.Graphics
{
    public partial class TestSceneFrostedGlass : FrameworkTestScene
    {
        [BackgroundDependencyLoader]
        private void load(TextureStore textures)
        {
            Container backgroundCircles;

            Children = new Drawable[]
            {
                // Background with animated circles
                backgroundCircles = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                },
                // Frosted glass container overlaying part of the screen
                // new FrostedGlassContainer
                // {
                //     RelativeSizeAxes = Axes.Both,
                //     Width = 0.5f,
                //     BlurSigma = new Vector2(10),
                //     Children = new Drawable[]
                //     {
                //         new Box
                //         {
                //             RelativeSizeAxes = Axes.Both,
                //             Colour = new Color4(1, 1, 1, 0.5f),
                //         },
                //         new SpriteText
                //         {
                //             Text = "Frosted Glass Effect",
                //             Anchor = Anchor.Centre,
                //             Origin = Anchor.Centre,
                //             Colour = Color4.Black,
                //         }
                //     }
                // },
                new Label("Background"),
                new Label("FrostedGlassContainer")
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight
                }
            };

            const float circle_radius = 0.05f;
            const float spacing = 0.01f;

            for (float xPos = 0; xPos < 1; xPos += circle_radius + spacing)
            {
                for (float yPos = 0; yPos < 1; yPos += circle_radius + spacing)
                {
                    backgroundCircles.Add(new CircularProgress
                    {
                        RelativeSizeAxes = Axes.Both,
                        Size = new Vector2(circle_radius),
                        RelativePositionAxes = Axes.Both,
                        Position = new Vector2(xPos, yPos),
                        Progress = 1,
                        Colour = Color4.HotPink,
                    });
                }
            }
        }

        private partial class Label : Container
        {
            public Label(string text)
            {
                AutoSizeAxes = Axes.Both;
                Margin = new MarginPadding(10);
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Color4.Black
                    },
                    new SpriteText
                    {
                        Text = text,
                        Margin = new MarginPadding(10)
                    }
                };
            }
        }
    }
}
