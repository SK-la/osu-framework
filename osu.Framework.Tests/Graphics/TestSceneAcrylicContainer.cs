// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class TestSceneAcrylicContainer : TestScene
    {
        [Test]
        public void TestAcrylicContainerCreation()
        {
            AddStep("create acrylic container", () =>
            {
                Child = new AcrylicTestContainer
                {
                    RelativeSizeAxes = Axes.Both,
                };
            });
        }
    }
}
