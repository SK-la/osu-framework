// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.IO.Stores;
using osu.Framework.Threading;

namespace osu.Framework.Tests.Audio
{
    [TestFixture]
    public class AsioTestApp
    {
        [Test]
        public void TestAsioInApplicationContext()
        {
            // Skip if not on Windows
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                Assert.Ignore("ASIO is only supported on Windows");

            Console.WriteLine("ASIO Test Application");
            Console.WriteLine("====================");

            // Initialize audio thread
            AudioThread.PreloadBass();

            var audioThread = new AudioThread();
            var trackStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AsioTestApp).Assembly));
            var sampleStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AsioTestApp).Assembly));

            using (var audioManager = new AudioManager(audioThread, trackStore, sampleStore, null))
            {
                Console.WriteLine("Available audio devices:");
                var devices = audioManager.AudioDeviceNames.ToList();

                for (int i = 0; i < devices.Count; i++)
                {
                    Console.WriteLine($"{i}: {devices[i]}");
                }

                var asioDevices = devices.Where(d => d.Contains("(ASIO)")).ToList();
                Console.WriteLine($"\nFound {asioDevices.Count} ASIO devices:");

                foreach (string? device in asioDevices)
                {
                    Console.WriteLine($"  - {device}");
                }

                if (asioDevices.Count == 0)
                {
                    Console.WriteLine("No ASIO devices found. Make sure ASIO drivers are installed and not in use by other applications.");
                    Assert.Ignore("No ASIO devices available for testing");
                }

                // Test switching to first ASIO device
                string? testDevice = asioDevices.First();
                Console.WriteLine($"\nTesting ASIO device: {testDevice}");

                audioManager.AudioDevice.Value = testDevice;
                System.Threading.Thread.Sleep(2000); // Wait for initialization

                Console.WriteLine($"Successfully switched to: {audioManager.AudioDevice.Value}");

                // Switch back to default
                Console.WriteLine("\nSwitching back to default device...");
                audioManager.AudioDevice.Value = string.Empty;
                System.Threading.Thread.Sleep(1000);
                Console.WriteLine($"Current device: {audioManager.AudioDevice.Value}");

                Console.WriteLine("\nASIO test completed successfully");
            }
        }
    }
}
