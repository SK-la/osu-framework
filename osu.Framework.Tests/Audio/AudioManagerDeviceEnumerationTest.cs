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
        public void TestAsioDeviceSupportedSampleRates()
        {
            AudioThread.PreloadBass();

            var audioThread = new AudioThread();
            var trackStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AudioManagerDeviceEnumerationTest).Assembly));
            var sampleStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AudioManagerDeviceEnumerationTest).Assembly));

            using (var audioManager = new AudioManager(audioThread, trackStore, sampleStore, null))
            {
                var deviceNames = audioManager.AudioDeviceNames.ToList();

                // Check for ASIO devices if on Windows
                if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                {
                    var asioDevices = deviceNames.Where(name => name.Contains("(ASIO)")).ToList();

                    TestContext.WriteLine($"Found {asioDevices.Count} ASIO devices");

                    foreach (var device in asioDevices)
                    {
                        string deviceName = device.Replace(" (ASIO)", "");
                        TestContext.WriteLine($"Testing ASIO device: {deviceName}");

                        try
                        {
                            var rates = audioManager.GetAsioDeviceSupportedSampleRates(deviceName);
                            TestContext.WriteLine($"  Supported rates: {(rates != null ? string.Join(", ", rates) : "null")}");

                            if (rates != null && rates.Length > 0)
                            {
                                TestContext.WriteLine($"  Rate count: {rates.Length}");
                            }
                            else
                            {
                                TestContext.WriteLine("  No supported rates found!");
                            }
                        }
                        catch (System.Exception ex)
                        {
                            TestContext.WriteLine($"  Error: {ex.Message}");
                        }
                    }
                }
            }
        }
    }
}
