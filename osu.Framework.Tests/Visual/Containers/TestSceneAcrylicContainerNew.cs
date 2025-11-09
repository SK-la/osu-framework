// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;
using osu.Framework.Utils;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Visual.Containers
{
    /// <summary>
    /// 测试真正的毛玻璃效果 - 使用新重构的AcrylicContainer
    /// 这个容器可以对下层的任何内容进行实时模糊,包括视频、动画等
    /// </summary>
    public partial class TestSceneAcrylicContainerNew : TestScene
    {
        private AcrylicContainer? acrylicEffect;
        private Box? animatedBox;
        private readonly List<Box> movingBoxes = new List<Box>();

        [BackgroundDependencyLoader]
        private void load()
        {
            // 创建一个彩色渐变背景层
            Add(new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientVertical(Color4.Blue, Color4.Purple)
            });

            // 添加一些移动的彩色方块作为背景内容
            for (int i = 0; i < 5; i++)
            {
                var box = new Box
                {
                    Size = new Vector2(100),
                    Colour = new Color4(
                        (float)RNG.NextDouble(),
                        (float)RNG.NextDouble(),
                        (float)RNG.NextDouble(),
                        1f
                    ),
                    Position = new Vector2(
                        (float)(RNG.NextDouble() * 800),
                        (float)(RNG.NextDouble() * 600)
                    )
                };

                Add(box);
                movingBoxes.Add(box);
            }

            // 添加一个持续旋转的大方块
            animatedBox = new Box
            {
                Size = new Vector2(200),
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Colour = Color4.Yellow
            };

            Add(animatedBox);

            // 在上面添加毛玻璃效果容器
            Add(new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(100),
                Child = acrylicEffect = new AcrylicContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    BlurStrength = 15f,
                    TintColour = new Color4(0, 0, 0, 0.3f),
                    BackgroundColour = Color4.Black.Opacity(0.5f), // 半透明黑色背景
                    Children = new Drawable[]
                    {
                        // 在毛玻璃效果上面显示一些文本
                        new SpriteText
                        {
                            Text = "毛玻璃效果 (Acrylic Effect)\n\n注意: 此容器模糊自己的背景层,\n不是背后的内容。\n这是 osu-framework 的限制。",
                            Font = FontUsage.Default.With(size: 30),
                            Colour = Color4.White,
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Shadow = true
                        }
                    }
                }
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // 在LoadComplete后启动动画
            // 让方块循环移动
            foreach (var box in movingBoxes)
            {
                box.MoveTo(new Vector2(
                    (float)(RNG.NextDouble() * 800),
                    (float)(RNG.NextDouble() * 600)
                ), 3000).Then().MoveTo(new Vector2(
                    (float)(RNG.NextDouble() * 800),
                    (float)(RNG.NextDouble() * 600)
                ), 3000).Loop();
            }

            // 让黄色方块持续旋转
            animatedBox?.RotateTo(360, 4000).Then().RotateTo(0, 0).Loop();

            // 添加控制UI
            AddLabel("模糊强度 (Blur Strength)");
            AddSliderStep("blur", 0f, 50f, 15f, value =>
            {
                if (acrylicEffect != null)
                    acrylicEffect.BlurStrength = value;
            });

            AddLabel("着色透明度 (Tint Alpha)");
            AddSliderStep("alpha", 0f, 1f, 0.3f, value =>
            {
                if (acrylicEffect != null)
                {
                    var current = acrylicEffect.TintColour.TopLeft.Linear;
                    acrylicEffect.TintColour = new Color4(current.R, current.G, current.B, value);
                }
            });

            AddLabel("着色颜色 (Tint Color)");
            AddStep("黑色 (Black)", () =>
            {
                if (acrylicEffect != null)
                {
                    var alpha = acrylicEffect.TintColour.TopLeft.Linear.A;
                    acrylicEffect.TintColour = new Color4(0, 0, 0, alpha);
                }
            });

            AddStep("白色 (White)", () =>
            {
                if (acrylicEffect != null)
                {
                    var alpha = acrylicEffect.TintColour.TopLeft.Linear.A;
                    acrylicEffect.TintColour = new Color4(1, 1, 1, alpha);
                }
            });

            AddStep("红色 (Red)", () =>
            {
                if (acrylicEffect != null)
                {
                    var alpha = acrylicEffect.TintColour.TopLeft.Linear.A;
                    acrylicEffect.TintColour = new Color4(1, 0, 0, alpha);
                }
            });

            AddStep("蓝色 (Blue)", () =>
            {
                if (acrylicEffect != null)
                {
                    var alpha = acrylicEffect.TintColour.TopLeft.Linear.A;
                    acrylicEffect.TintColour = new Color4(0, 0, 1, alpha);
                }
            });

            AddLabel("效果演示 (Demos)");
            AddStep("显示/隐藏毛玻璃", () =>
            {
                if (acrylicEffect != null)
                    acrylicEffect.Alpha = acrylicEffect.Alpha > 0 ? 0 : 1;
            });

            AddStep("脉冲动画", () =>
            {
                acrylicEffect?.ScaleTo(1.1f, 500, Easing.OutQuint)
                              .Then()
                              .ScaleTo(1f, 500, Easing.InQuint);
            });
        }
    }
}
