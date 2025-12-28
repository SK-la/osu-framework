using System;
using System.Runtime.InteropServices;
using osu.Framework.Audio.Asio;

class Program
{
    static void Main()
    {
        Console.WriteLine("Testing ASIO device enumeration...");

        try
        {
            int count = 0;
            foreach (var device in BassAsio.EnumerateDevices())
            {
                Console.WriteLine($"ASIO Device {device.Index}: {device.Name}");
                count++;
            }

            if (count == 0)
                Console.WriteLine("No ASIO devices found. This could mean:");
            Console.WriteLine("- No ASIO drivers installed");
            Console.WriteLine("- ASIO drivers not available");
            Console.WriteLine("- bassasio.dll not loaded properly");
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
        }
    }
}
