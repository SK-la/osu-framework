using osuTK.Graphics;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Allocation;

namespace osu.Framework.Graphics.Containers
{
    /// <summary>
    /// A specialized layer for acrylic blur effects that fills the entire area and applies blur to background content.
    /// This layer handles the actual blurring logic using a custom shader and manages background capture automatically.
    /// </summary>
    internal partial class AcrylicBlurLayer : Drawable
    {
        /// <summary>
        /// The strength of the blur effect.
        /// </summary>
        public float BlurStrength { get; set; } = 10f;

        /// <summary>
        /// The tint colour applied over the blurred background.
        /// </summary>
        public Color4 TintColour { get; set; } = Color4.White;

        /// <summary>
        /// The darkening factor for depth effect.
        /// </summary>
        public float DarkenFactor { get; set; } = 0.1f;

        [Resolved]
        private IRenderer? renderer { get; set; }

        [Resolved]
        private ShaderManager shaderManager { get; set; } = null!;

        public AcrylicBlurLayer()
        {
            RelativeSizeAxes = Axes.Both;
        }

        protected override DrawNode CreateDrawNode() => new AcrylicBlurLayerDrawNode(this);
    }
}
