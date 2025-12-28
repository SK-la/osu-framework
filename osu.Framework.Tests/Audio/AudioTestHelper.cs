// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Development;
using osu.Framework.Threading;

namespace osu.Framework.Tests.Audio
{
    internal static class AudioTestHelper
    {
        /// <summary>
        /// Runs an <paramref name="action"/> on a newly created audio thread, and blocks until it has been run to completion.
        /// </summary>
        /// <param name="action">The action to run on the audio thread.</param>
        public static void RunOnAudioThread(Action action)
        {
            using (var _ = StartNewAudioThread(action))
            {
            }
        }

        /// <summary>
        /// Runs an <paramref name="action"/> on a newly created audio thread.
        /// </summary>
        /// <param name="action">The action to run on the audio thread.</param>
        /// <returns>An <see cref="InvokeOnDisposal"/> that waits for the thread to stop and rethrows any unhandled exceptions thrown by the <paramref name="action"/>.</returns>
        public static IDisposable StartNewAudioThread(Action action)
        {
            var resetEvent = new ManualResetEvent(false);
            Exception? threadException = null;

            new Thread(() =>
            {
                ThreadSafety.IsAudioThread = true;

                try
                {
                    action();
                }
                catch (Exception e)
                {
                    threadException = e;
                }

                resetEvent.Set();
            })
            {
                Name = GameThread.SuffixedThreadNameFor("Audio")
            }.Start();

            return new InvokeOnDisposal(() =>
            {
                if (!resetEvent.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException();

                if (threadException != null)
                    ExceptionDispatchInfo.Throw(threadException);
            });
        }

        /// <summary>
        /// Tests ASIO device enumeration.
        /// </summary>
        public static void TestAsioDevices()
        {
            Console.WriteLine("Testing ASIO device enumeration...");

            try
            {
                int count = 0;
                foreach (var device in osu.Framework.Audio.Asio.BassAsio.EnumerateDevices())
                {
                    Console.WriteLine($"ASIO Device {device.Index}: {device.Name}");
                    count++;
                }

                Console.WriteLine($"Found {count} ASIO devices");

                if (count == 0)
                {
                    Console.WriteLine("No ASIO devices found. Possible reasons:");
                    Console.WriteLine("1. No ASIO drivers installed");
                    Console.WriteLine("2. bassasio.dll not loaded properly");
                    Console.WriteLine("3. ASIO drivers not available");
                }
            }
            catch (DllNotFoundException e)
            {
                Console.WriteLine($"DLL not found: {e.Message}");
            }
            catch (EntryPointNotFoundException e)
            {
                Console.WriteLine($"Entry point not found: {e.Message}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: {e.Message}");
                Console.WriteLine($"Stack trace: {e.StackTrace}");
            }
        }
    }
}
