// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Rendering;

namespace osu.Framework.Graphics.Containers
{
    public partial class AcrylicContainer
    {
        private class AcrylicContainerDrawNode : BufferedDrawNode
        {
            protected new AcrylicContainer Source => (AcrylicContainer)base.Source;

            public AcrylicContainerDrawNode(AcrylicContainer source)
                : base(source, new CompositeDrawableDrawNode(source), new BufferedDrawNodeSharedData())
            {
            }

            protected override void DrawContents(IRenderer renderer)
            {
                if (Source.BackgroundBuffer != null)
                {
                    // Draw the background buffer directly with tint
                    ColourInfo finalColour = DrawColourInfo.Colour;
                    finalColour.ApplyChild(Source.TintColour);

                    renderer.DrawFrameBuffer(Source.BackgroundBuffer, DrawRectangle, finalColour);
                }
                else
                {
                    // Apply tint to the blurred content
                    ColourInfo finalColour = DrawColourInfo.Colour;
                    finalColour.ApplyChild(Source.TintColour);

                    renderer.DrawFrameBuffer(SharedData.CurrentEffectBuffer, DrawRectangle, finalColour);
                }
            }
        }
    }
}
