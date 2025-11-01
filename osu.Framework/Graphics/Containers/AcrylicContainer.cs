// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osuTK;
using osuTK.Graphics;
using osu.Framework.Graphics.Rendering;

namespace osu.Framework.Graphics.Containers
{
    /// <summary>
    /// A container that applies an acrylic/mica effect by blurring the content behind it.
    /// This creates a frosted glass-like appearance where content below the container is visible through a blur.
    /// </summary>
    public partial class AcrylicContainer : BufferedContainer
    {
        /// <summary>
        /// The strength of the blur effect applied to the background content.
        /// </summary>
        public float BlurStrength { get; set; } = 10f;

        /// <summary>
        /// The background texture to blur. If null, blurs the container's children.
        /// </summary>
        public IFrameBuffer? BackgroundBuffer { get; set; }

        /// <summary>
        /// The tint colour applied over the blurred background.
        /// </summary>
        public Color4 TintColour { get; set; } = new Color4(1f, 1f, 1f, 0.8f);

        /// <summary>
        /// Constructs a new acrylic container.
        /// </summary>
        public AcrylicContainer()
            : base(formats: null, pixelSnapping: false, cachedFrameBuffer: false)
        {
            // Enable drawing original content with blur effect
            DrawOriginal = true;

            // Set up blur for the acrylic effect
            BlurSigma = new Vector2(BlurStrength);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Schedule a capture of the background after the scene is fully loaded
            Schedule(() =>
            {
                // Capture the background by temporarily hiding ourselves
                float wasAlpha = Alpha;
                Alpha = 0;

                // Force a redraw to capture the background
                Invalidate(Invalidation.DrawNode);

                Alpha = wasAlpha;
            });
        }

        protected override DrawNode CreateDrawNode() => new AcrylicContainerDrawNode(this);
    }
}
