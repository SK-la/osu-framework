// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Threading;

namespace osu.Framework.Tests.Audio
{
    [TestFixture]
    public class AsioDeviceTest
    {
        [Test]
        public void TestAsioDeviceEnumeration()
        {
            // Initialize audio thread to register DLL resolvers
            AudioThread.PreloadBass();

            // This will output to console during test run
            AudioTestHelper.TestAsioDevices();
            Assert.Pass("ASIO device enumeration completed");
        }
    }
}
