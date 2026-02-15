// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using System.Threading;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.IO.Stores;
using osu.Framework.Threading;

namespace osu.Framework.Tests.Audio
{
    [TestFixture]
    public class AsioDeviceSwitchingTest
    {
        [Test]
        public void TestAsioDeviceSwitching()
        {
            // Skip if not on Windows
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                Assert.Ignore("ASIO is only supported on Windows");

            AudioThread.PreloadBass();

            var audioThread = new AudioThread();
            var trackStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AsioDeviceSwitchingTest).Assembly));
            var sampleStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AsioDeviceSwitchingTest).Assembly));

            using (var audioManager = new AudioManager(audioThread, trackStore, sampleStore, null))
            {
                var deviceNames = audioManager.AudioDeviceNames.ToList();
                var asioDevices = deviceNames.Where(name => name.Contains("(ASIO)")).ToList();

                if (asioDevices.Count == 0)
                    Assert.Ignore("No ASIO devices available for testing");

                // Test switching to each ASIO device
                foreach (string? asioDevice in asioDevices)
                {
                    TestContext.WriteLine($"Testing switch to ASIO device: {asioDevice}");

                    // Set the audio device - this should trigger device initialization
                    audioManager.AudioDevice.Value = asioDevice;

                    // Wait for device initialization to complete
                    Thread.Sleep(500);

                    // Verify the device was set
                    Assert.AreEqual(asioDevice, audioManager.AudioDevice.Value, $"Failed to switch to {asioDevice}");

                    TestContext.WriteLine($"Successfully switched to {asioDevice}");
                }

                // Test switching back to default device
                TestContext.WriteLine("Testing switch back to default device");
                audioManager.AudioDevice.Value = string.Empty;

                Thread.Sleep(500);
                Assert.AreEqual(string.Empty, audioManager.AudioDevice.Value, "Failed to switch back to default device");

                TestContext.WriteLine("ASIO device switching test completed successfully");
            }
        }
    }
}
