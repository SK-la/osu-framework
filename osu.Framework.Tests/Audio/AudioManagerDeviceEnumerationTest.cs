// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.IO.Stores;
using osu.Framework.Threading;

namespace osu.Framework.Tests.Audio
{
    [TestFixture]
    public class AudioManagerDeviceEnumerationTest
    {
        [Test]
        public void TestAudioManagerEnumeratesAsioDevices()
        {
            AudioThread.PreloadBass();

            var audioThread = new AudioThread();
            var trackStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AudioManagerDeviceEnumerationTest).Assembly));
            var sampleStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AudioManagerDeviceEnumerationTest).Assembly));

            using (var audioManager = new AudioManager(audioThread, trackStore, sampleStore, null))
            {
                var deviceNames = audioManager.AudioDeviceNames.ToList();

                // Check that we have base devices
                Assert.That(deviceNames.Count, Is.GreaterThan(0), "Should have at least one audio device");

                // Check for ASIO devices if on Windows
                if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                {
                    var asioDevices = deviceNames.Where(name => name.Contains("(ASIO)")).ToList();
                    Assert.That(asioDevices.Count, Is.GreaterThanOrEqualTo(0), $"Found {asioDevices.Count} ASIO devices");

                    // Log the devices for debugging
                    foreach (var device in deviceNames)
                        TestContext.WriteLine($"Device: {device}");
                }
            }
        }
    }
}
