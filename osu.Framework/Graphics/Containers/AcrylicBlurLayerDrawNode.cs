// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Shaders.Types;
using osuTK;
using osuTK.Graphics;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Graphics.Containers
{
    internal partial class AcrylicBlurLayer
    {
        private class AcrylicBlurLayerDrawNode : DrawNode
        {
            protected new AcrylicBlurLayer Source => (AcrylicBlurLayer)base.Source;

            private RectangleF drawRectangle;
            private IShader acrylicShader;
            private IFrameBuffer backgroundBuffer;
            private IUniformBuffer<AcrylicParameters> acrylicParametersBuffer;

            public AcrylicBlurLayerDrawNode(AcrylicBlurLayer source)
                : base(source)
            {
            }

            public override void ApplyState()
            {
                base.ApplyState();
                drawRectangle = Source.ScreenSpaceDrawQuad.AABBFloat;
            }

            protected override void Draw(IRenderer renderer)
            {
                // 检查依赖是否可用
                if (Source.renderer == null || Source.shaderManager == null)
                {
                    // 如果依赖不可用，跳过绘制
                    return;
                }

                // 获取或创建背景缓冲区（延迟到绘制线程）
                if (backgroundBuffer == null)
                {
                    try
                    {
                        backgroundBuffer = renderer.CreateFrameBuffer(null, TextureFilteringMode.Linear);
                    }
                    catch
                    {
                        // 如果创建失败，跳过绘制
                        return;
                    }
                }

                // 捕获当前屏幕到缓冲区
                try
                {
                    renderer.CaptureScreenToFrameBuffer(backgroundBuffer);
                }
                catch
                {
                    // 如果捕获失败，使用固定的背景色
                    renderer.Clear(new ClearInfo(Color4.Gray));
                    return;
                }

                // 尝试加载毛玻璃着色器
                if (acrylicShader == null)
                {
                    try
                    {
                        acrylicShader = Source.shaderManager.Load("AcrylicBlur", "Texture");
                        acrylicParametersBuffer = renderer.CreateUniformBuffer<AcrylicParameters>();
                    }
                    catch (Exception ex)
                    {
                        // 如果加载失败，使用备用方案：直接绘制背景
                        Console.WriteLine($"Failed to load acrylic shader: {ex.Message}");
                        renderer.DrawFrameBuffer(backgroundBuffer, drawRectangle, ColourInfo.SingleColour(Source.TintColour));
                        return;
                    }
                }

                // 使用着色器绘制
                if (acrylicShader != null && acrylicParametersBuffer != null)
                {
                    acrylicParametersBuffer.Data = acrylicParametersBuffer.Data with
                    {
                        TexSize = backgroundBuffer.Size,
                        Radius = (int)Source.BlurStrength,
                        Sigma = Source.BlurStrength / 2f,
                        BlurDirection = Vector2.One,
                        TintColour = new Vector4(Source.TintColour.R, Source.TintColour.G, Source.TintColour.B, Source.TintColour.A),
                        DarkenFactor = Source.DarkenFactor
                    };

                    acrylicShader.BindUniformBlock("m_AcrylicParameters", acrylicParametersBuffer);
                    acrylicShader.Bind();

                    // 绘制背景到当前区域
                    renderer.DrawFrameBuffer(backgroundBuffer, drawRectangle, ColourInfo.SingleColour(new Color4(1, 1, 1, 1)));

                    acrylicShader.Unbind();
                }
                else
                {
                    // 备用方案：直接绘制背景
                    renderer.DrawFrameBuffer(backgroundBuffer, drawRectangle, ColourInfo.SingleColour(Source.TintColour));
                }
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);
                acrylicParametersBuffer?.Dispose();
                backgroundBuffer?.Dispose();
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private record struct AcrylicParameters
            {
                public UniformVector2 TexSize;
                public UniformInt Radius;
                public UniformFloat Sigma;
                public UniformVector2 BlurDirection;
                public UniformVector4 TintColour;
                public UniformFloat DarkenFactor;
                private readonly UniformPadding12 pad1;
            }
        }
    }
}
