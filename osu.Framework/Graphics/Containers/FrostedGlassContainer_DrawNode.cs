// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Collections.Generic;
using osuTK;
using osuTK.Graphics;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Shaders;
using System;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders.Types;
using osu.Framework.Utils;
using osu.Framework.Statistics;
using osu.Framework.Allocation;

namespace osu.Framework.Graphics.Containers
{
    public partial class FrostedGlassContainer<T>
    {
        private class FrostedGlassDrawNode : BufferedDrawNode, ICompositeDrawNode
        {
            protected new FrostedGlassContainer<T> Source => (FrostedGlassContainer<T>)base.Source;

            protected new CompositeDrawableDrawNode Child => (CompositeDrawableDrawNode)base.Child;

            private ColourInfo effectColour;
            private BlendingParameters effectBlending;

            private Vector2 blurSigma;
            private Vector2I blurRadius;
            private float blurRotation;
            private float grayscaleStrength;

            private long updateVersion;
            private IShader blurShader;
            private IShader grayscaleShader;

            public FrostedGlassDrawNode(FrostedGlassContainer<T> source, BufferedContainerDrawNodeSharedData sharedData)
                : base(source, new CompositeDrawableDrawNode(source), sharedData)
            {
            }

            public override void ApplyState()
            {
                base.ApplyState();

                updateVersion = Source.updateVersion;

                effectColour = Source.EffectColour;
                effectBlending = Source.DrawEffectBlending;

                blurSigma = Source.BlurSigma;
                blurRadius = new Vector2I(Blur.KernelSize(blurSigma.X), Blur.KernelSize(blurSigma.Y));
                blurRotation = Source.BlurRotation;
                grayscaleStrength = Source.GrayscaleStrength;

                blurShader = Source.blurShader;
                grayscaleShader = Source.grayscaleShader;
            }

            protected override long GetDrawVersion() => updateVersion;

            protected override void PopulateContents(IRenderer renderer)
            {
                // Capture the screen to the main buffer
                renderer.CaptureScreenToFrameBuffer(SharedData.MainBuffer);

                // Then apply effects
                base.PopulateContents(renderer);
            }

            protected override void DrawContents(IRenderer renderer)
            {
                renderer.SetBlend(effectBlending);

                ColourInfo finalEffectColour = DrawColourInfo.Colour;
                finalEffectColour.ApplyChild(effectColour);

                renderer.DrawFrameBuffer(SharedData.CurrentEffectBuffer, DrawRectangle, finalEffectColour);

                // Draw children on top
                DrawOther(Child, renderer);
            }

            private IUniformBuffer<BlurParameters> blurParametersBuffer;

            private void drawBlurredFrameBuffer(IRenderer renderer, int kernelRadius, float sigma, float blurRotation)
            {
                blurParametersBuffer ??= renderer.CreateUniformBuffer<BlurParameters>();

                IFrameBuffer current = SharedData.CurrentEffectBuffer;
                IFrameBuffer target = SharedData.GetNextEffectBuffer();

                renderer.SetBlend(BlendingParameters.None);

                using (BindFrameBuffer(target))
                {
                    float radians = float.DegreesToRadians(blurRotation);

                    blurParametersBuffer.Data = blurParametersBuffer.Data with
                    {
                        Radius = kernelRadius,
                        Sigma = sigma,
                        TexSize = current.Size,
                        Direction = new Vector2(MathF.Cos(radians), MathF.Sin(radians))
                    };

                    blurShader.BindUniformBlock("m_BlurParameters", blurParametersBuffer);
                    blurShader.Bind();
                    renderer.DrawFrameBuffer(current, new RectangleF(0, 0, current.Texture.Width, current.Texture.Height), ColourInfo.SingleColour(Color4.White));
                    blurShader.Unbind();
                }
            }

            public List<DrawNode> Children
            {
                get => Child.Children;
                set => Child.Children = value;
            }

            public bool AddChildDrawNodes => RequiresRedraw;

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);
                blurParametersBuffer?.Dispose();
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private record struct BlurParameters
            {
                public UniformVector2 TexSize;
                public UniformInt Radius;
                public UniformFloat Sigma;
                public UniformVector2 Direction;
                private readonly UniformPadding8 pad1;
            }
        }

        private class BufferedContainerDrawNodeSharedData : BufferedDrawNodeSharedData
        {
            public BufferedContainerDrawNodeSharedData(RenderBufferFormat[] mainBufferFormats, bool pixelSnapping, bool clipToRootNode)
                : base(2, mainBufferFormats, pixelSnapping, clipToRootNode)
            {
            }
        }
    }
}
