using System;
using System.Runtime.InteropServices;
using osu.Framework.Audio.Asio;
using osu.Framework.Logging;

namespace AsioTest
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("Testing ASIO device enumeration...");

            try
            {
                Logger.Enabled = true;
                Logger.Level = LogLevel.Verbose;

                int count = 0;
                foreach (var device in BassAsio.EnumerateDevices())
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
