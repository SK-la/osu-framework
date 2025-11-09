// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace osu.Framework.Graphics.Containers
{
    /// <summary>
    /// Simple test class to verify AcrylicContainer functionality
    /// </summary>
    public partial class AcrylicTestContainer : Container
    {
        public AcrylicTestContainer()
        {
            RelativeSizeAxes = Axes.Both;

            // Add some background content to blur
            Add(new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Red,
            });

            // Add text overlay
            Add(new SpriteText
            {
                Text = "Background Content",
                Font = FontUsage.Default.With(size: 24),
                Colour = Colour4.White,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
            });

            // Add the acrylic container on top
            Add(new AcrylicContainer
            {
                RelativeSizeAxes = Axes.Both,
                BlurStrength = 10f,
                TintColour = Colour4.White.Opacity(0.8f),
                Children = new Drawable[]
                {
                    new SpriteText
                    {
                        Text = "Acrylic Effect Test",
                        Font = FontUsage.Default.With(size: 32),
                        Colour = Colour4.Black,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                    }
                }
            });
        }
    }
}
