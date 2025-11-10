using osuTK.Graphics;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Shapes;

namespace osu.Framework.Graphics.Containers
{
    /// <summary>
    /// A container that applies a true acrylic/mica effect by blurring the content behind it.
    /// This implementation uses a layered approach with a blur background layer and a darkening overlay.
    /// The effect is applied regardless of drawing order and adapts to background changes in real-time.
    /// </summary>
    public partial class AcrylicContainer : Container
    {
        /// <summary>
        /// The strength of the blur effect applied to the background content.
        /// </summary>
        public float BlurStrength { get; set; } = 10f;

        /// <summary>
        /// The tint colour applied over the blurred background.
        /// </summary>
        public Color4 TintColour { get; set; } = Color4.White;

        /// <summary>
        /// The darkening factor applied to create depth.
        /// </summary>
        public float DarkenFactor { get; set; } = 0.1f;

        private AcrylicBlurLayer blurLayer = null!;
        private Box darkenLayer = null!;

        [Resolved]
        private IRenderer? renderer { get; set; }

        [Resolved]
        private ShaderManager shaderManager { get; set; } = null!;

        /// <summary>
        /// Constructs a new acrylic container.
        /// </summary>
        public AcrylicContainer()
        {
            // 默认不设置RelativeSizeAxes，让用户决定
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // 添加虚化背景层
            Add(blurLayer = new AcrylicBlurLayer
            {
                RelativeSizeAxes = Axes.Both,
                BlurStrength = BlurStrength,
                TintColour = TintColour,
                DarkenFactor = DarkenFactor
            });

            // 添加暗化层
            Add(darkenLayer = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = new Color4(0, 0, 0, DarkenFactor),
                Depth = -1 // 确保在虚化层之上
            });
        }

        protected override void Update()
        {
            base.Update();

            // 同步属性到层
            blurLayer.BlurStrength = BlurStrength;
            blurLayer.TintColour = TintColour;
            blurLayer.DarkenFactor = DarkenFactor;

            darkenLayer.Colour = new Color4(0, 0, 0, DarkenFactor);
        }
    }
}
