// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework;
using osu.Framework.Graphics;
using osuTK;
using osuTK.Graphics;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Containers;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Rendering;

namespace SampleGame
{
    public partial class SampleGameGame : Game
    {
        private Box backgroundBox = null!;
        private AcrylicContainer acrylicContainer = null!;
        private IFrameBuffer? backgroundBuffer;

        [BackgroundDependencyLoader]
        private void load()
        {
            // Background
            Add(backgroundBox = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Color4.Blue
            });

            // Add some additional background elements
            Add(new Box
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Size = new Vector2(100, 100),
                Colour = Color4.Red
            });

            Add(new Box
            {
                Anchor = Anchor.BottomRight,
                Origin = Anchor.BottomRight,
                Size = new Vector2(150, 150),
                Colour = Color4.Green
            });

            // Acrylic container on top - now uses background buffer for true acrylic effect
            Add(acrylicContainer = new AcrylicContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(200, 200),
                TintColour = new Color4(1f, 1f, 1f, 0.5f), // Semi-transparent white
                BlurStrength = 5f, // Apply blur to background
            });
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            // Capture background after children are updated
            if (backgroundBuffer == null)
            {
                backgroundBuffer = Host.Renderer.CreateFrameBuffer(null, TextureFilteringMode.Linear);
            }

            // Capture the current screen content as background
            if (Host.Renderer is Renderer concreteRenderer)
            {
                concreteRenderer.CaptureScreenToFrameBuffer(backgroundBuffer);
            }

            // Set the background buffer to the acrylic container
            if (acrylicContainer != null)
            {
                acrylicContainer.BackgroundBuffer = backgroundBuffer;
            }
        }

        protected override void Update()
        {
            base.Update();
            backgroundBox.Rotation += (float)Time.Elapsed / 20;
            acrylicContainer.Rotation -= (float)Time.Elapsed / 15;
        }
    }
}
