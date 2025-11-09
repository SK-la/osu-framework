// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Colour;
using osuTK.Graphics;

namespace osu.Framework.Graphics.Containers
{
    /// <summary>
    /// A container that applies an acrylic/frosted glass visual effect.
    /// This is achieved by using a <see cref="BufferedContainer"/> with blur and a tinted overlay.
    /// The container blurs its own background (a solid color or drawable), not content behind it.
    ///
    /// Usage:
    /// <code>
    /// var acrylicEffect = new AcrylicContainer
    /// {
    ///     RelativeSizeAxes = Axes.Both,
    ///     BlurStrength = 10f,
    ///     TintColour = new Color4(0, 0, 0, 0.3f),
    ///     BackgroundColour = Color4.White, // The color to blur
    ///     Children = new Drawable[] { /* your content */ }
    /// };
    /// </code>
    /// </summary>
    public partial class AcrylicContainer : BufferedContainer
    {
        private float blurStrength = 10f;

        /// <summary>
        /// The strength of the blur effect.
        /// Higher values create a stronger blur. Range: 0-100, typical values: 5-20.
        /// </summary>
        public float BlurStrength
        {
            get => blurStrength;
            set
            {
                if (blurStrength == value)
                    return;

                blurStrength = value;
                updateBlur();
            }
        }

        private ColourInfo tintColour = ColourInfo.SingleColour(new Color4(0, 0, 0, 0.3f));

        /// <summary>
        /// The tint colour applied over the blurred background.
        /// Typically a semi-transparent color like Color4(0, 0, 0, 0.3f).
        /// </summary>
        public new ColourInfo TintColour
        {
            get => tintColour;
            set
            {
                if (tintColour.Equals(value))
                    return;

                tintColour = value;
                updateTint();
            }
        }

        private Drawable? backgroundBox;
        private Drawable? tintOverlay;

        public AcrylicContainer()
            : base(cachedFrameBuffer: true)
        {
            BackgroundColour = Color4.White; // Default white background to blur
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // Add a background box that will be blurred
            AddInternal(backgroundBox = new Shapes.Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = BackgroundColour,
                Depth = float.MaxValue // Behind everything
            });

            // Add a tint overlay on top
            AddInternal(tintOverlay = new Shapes.Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = tintColour,
                Depth = float.MinValue, // In front of everything
                Alpha = tintColour.TopLeft.Linear.A
            });

            updateBlur();
        }

        private void updateBlur()
        {
            if (!IsLoaded)
                return;

            // Convert BlurStrength to sigma for the blur shader
            // sigma controls the Gaussian distribution width
            float sigma = Math.Max(0.5f, blurStrength * 0.5f);
            this.BlurTo(new osuTK.Vector2(sigma), 200);
        }

        private void updateTint()
        {
            if (tintOverlay != null)
            {
                tintOverlay.Colour = tintColour;
                tintOverlay.Alpha = tintColour.TopLeft.Linear.A;
            }
        }

        public new Color4 BackgroundColour
        {
            get => base.BackgroundColour;
            set
            {
                base.BackgroundColour = value;

                if (backgroundBox != null)
                    backgroundBox.Colour = value;
            }
        }
    }
}
