// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osuTK;
using osuTK.Graphics;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Utils;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Layout;

namespace osu.Framework.Graphics.Containers
{
    /// <summary>
    /// A container that renders the background (from the screen) to an internal framebuffer, applies blur, and then
    /// blits the framebuffer to the screen, allowing for frosted glass effects on the background.
    /// If all children are of a specific non-<see cref="Drawable"/> type, use the
    /// generic version <see cref="FrostedGlassContainer{T}"/>.
    /// </summary>
    public partial class FrostedGlassContainer : FrostedGlassContainer<Drawable>
    {
        /// <inheritdoc />
        public FrostedGlassContainer(RenderBufferFormat[] formats = null, bool pixelSnapping = false)
            : base(formats, pixelSnapping)
        {
        }
    }

    /// <summary>
    /// A container that renders the background (from the screen) to an internal framebuffer, applies blur, and then
    /// blits the framebuffer to the screen, allowing for frosted glass effects on the background.
    /// </summary>
    public partial class FrostedGlassContainer<T> : Container<T>, IBufferedContainer, IBufferedDrawable
        where T : Drawable
    {
        private Vector2 blurSigma = Vector2.Zero;

        /// <summary>
        /// Controls the amount of blurring in two orthogonal directions (X and Y if
        /// <see cref="BlurRotation"/> is zero).
        /// Blur is parametrized by a gaussian image filter. This property controls
        /// the standard deviation (sigma) of the gaussian kernel.
        /// </summary>
        public Vector2 BlurSigma
        {
            get => blurSigma;
            set
            {
                if (blurSigma == value)
                    return;

                blurSigma = value;
                ForceRedraw();
            }
        }

        private float blurRotation;

        /// <summary>
        /// Rotates the blur kernel clockwise. In degrees. Has no effect if
        /// <see cref="BlurSigma"/> has the same magnitude in both directions.
        /// </summary>
        public float BlurRotation
        {
            get => blurRotation;
            set
            {
                if (blurRotation == value)
                    return;

                blurRotation = value;
                ForceRedraw();
            }
        }

        private ColourInfo effectColour = Color4.White;

        /// <summary>
        /// The multiplicative colour of drawn buffered object after applying all effects (e.g. blur). Default is <see cref="Color4.White"/>.
        /// </summary>
        public ColourInfo EffectColour
        {
            get => effectColour;
            set
            {
                if (effectColour.Equals(value))
                    return;

                effectColour = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private BlendingParameters effectBlending = BlendingParameters.Inherit;

        /// <summary>
        /// The <see cref="BlendingParameters"/> to use after applying all effects. Default is <see cref="BlendingType.Inherit"/>.
        /// </summary>
        public BlendingParameters EffectBlending
        {
            get => effectBlending;
            set
            {
                if (effectBlending == value)
                    return;

                effectBlending = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private Color4 backgroundColour = new Color4(0, 0, 0, 0);

        /// <summary>
        /// The background colour of the framebuffer. Transparent black by default.
        /// </summary>
        public Color4 BackgroundColour
        {
            get => backgroundColour;
            set
            {
                if (backgroundColour == value)
                    return;

                backgroundColour = value;
                ForceRedraw();
            }
        }

        private Vector2 frameBufferScale = Vector2.One;

        public Vector2 FrameBufferScale
        {
            get => frameBufferScale;
            set
            {
                if (frameBufferScale == value)
                    return;

                frameBufferScale = value;
                ForceRedraw();
            }
        }

        private float grayscaleStrength;

        public float GrayscaleStrength
        {
            get => grayscaleStrength;
            set
            {
                if (grayscaleStrength == value)
                    return;

                grayscaleStrength = value;
                ForceRedraw();
            }
        }

        /// <summary>
        /// Forces a redraw of the framebuffer before it is blitted the next time.
        /// </summary>
        public void ForceRedraw() => Invalidate(Invalidation.DrawNode);

        /// <summary>
        /// In order to signal the draw thread to re-draw the frosted glass container we version it.
        /// Our own version (update) keeps track of which version we are on, whereas the
        /// drawVersion keeps track of the version the draw thread is on.
        /// When forcing a redraw we increment updateVersion, pass it into each new drawnode
        /// and the draw thread will realize its drawVersion is lagging behind, thus redrawing.
        /// </summary>
        private long updateVersion;

        private readonly BufferedContainerDrawNodeSharedData sharedData;

        public IShader TextureShader { get; private set; }

        private IShader blurShader;
        private IShader grayscaleShader;

        /// <summary>
        /// Constructs an empty frosted glass container.
        /// </summary>
        /// <param name="formats">The render buffer formats attached to the frame buffer of this <see cref="FrostedGlassContainer"/>.</param>
        /// <param name="pixelSnapping">
        /// Whether the frame buffer position should be snapped to the nearest pixel when blitting.
        /// This amounts to setting the texture filtering mode to "nearest".
        /// </param>
        public FrostedGlassContainer(RenderBufferFormat[] formats = null, bool pixelSnapping = false)
        {
            sharedData = new BufferedContainerDrawNodeSharedData(formats, pixelSnapping, false);
        }

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            TextureShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
            blurShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.BLUR);
            grayscaleShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.GRAYSCALE);
        }

        protected override DrawNode CreateDrawNode() => new FrostedGlassDrawNode(this, sharedData);

        /// <summary>
        /// The blending which <see cref="FrostedGlassDrawNode"/> uses for the effect.
        /// </summary>
        public BlendingParameters DrawEffectBlending
        {
            get
            {
                BlendingParameters blending = EffectBlending;

                blending.CopyFromParent(Blending);
                blending.ApplyDefaultToInherited();

                return blending;
            }
        }

        protected override bool OnInvalidate(Invalidation invalidation, InvalidationSource source)
        {
            bool result = base.OnInvalidate(invalidation, source);

            if ((invalidation & Invalidation.DrawNode) > 0)
            {
                ++updateVersion;
                result = true;
            }

            return result;
        }

        public DrawColourInfo? FrameBufferDrawColour => base.DrawColourInfo;

        // Children should not receive the true colour to avoid colour doubling when the frame-buffers are rendered to the back-buffer.
        public override DrawColourInfo DrawColourInfo
        {
            get
            {
                var blending = Blending;
                blending.ApplyDefaultToInherited();

                return new DrawColourInfo(Color4.White, blending);
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            sharedData.Dispose();
        }
    }
}
