// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace osu.Framework.Audio.Asio
{
    /// <summary>
    /// Audio format configuration for ASIO devices.
    /// </summary>
    internal static class AsioAudioFormat
    {
        /// <summary>
        /// Common sample rates supported by most ASIO devices.
        /// </summary>
        public static readonly double[] SupportedSampleRates = { 44100, 48000, 96000, 192000 };

        /// <summary>
        /// Default sample rate to use when none is specified.
        /// </summary>
        public const double DefaultSampleRate = 48000;

        /// <summary>
        /// Checks if a sample rate is commonly supported.
        /// </summary>
        public static bool IsSupportedSampleRate(double sampleRate)
        {
            foreach (double rate in SupportedSampleRates)
            {
                if (Math.Abs(rate - sampleRate) < 1)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the closest supported sample rate to the requested rate.
        /// </summary>
        public static double GetClosestSupportedSampleRate(double requestedRate)
        {
            double closest = DefaultSampleRate;
            double minDiff = double.MaxValue;

            foreach (double rate in SupportedSampleRates)
            {
                double diff = Math.Abs(rate - requestedRate);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = rate;
                }
            }

            return closest;
        }
    }

    internal static class BassAsioPI
    {
        private const string dll = "bassasio";

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int AsioProcedure([MarshalAs(UnmanagedType.Bool)] bool input, int channel, IntPtr buffer, int length, IntPtr user);

        [StructLayout(LayoutKind.Sequential)]
        private struct AsioDeviceInfoNative
        {
            public IntPtr Name;
            public IntPtr Driver;
            public int Flags;
        }

        internal readonly struct AsioDeviceInfo
        {
            public AsioDeviceInfo(int index, string name)
            {
                Index = index;
                Name = name;
            }

            public int Index { get; }
            public string Name { get; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AsioInfo
        {
            public int MinBuffer;        // minimum buffer size (in samples)
            public int MaxBuffer;        // maximum buffer size (in samples)
            public int PreferredBuffer;  // preferred buffer size (in samples)
            public int Granularity;      // buffer size granularity
            public int Inputs;           // number of inputs
            public int Outputs;          // number of outputs
        }

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_GetDeviceInfo")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool getDeviceInfo(int device, out AsioDeviceInfoNative info);

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_Init")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Init(int device, int flags = 0);

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_Free")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Free();

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_Start")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Start(int bufferLength, int threads = 0);

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_Stop")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Stop();

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_ErrorGetCode")]
        internal static extern int ErrorGetCode();

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_GetInfo")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetInfo(out AsioInfo info);

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_GetRate")]
        internal static extern double GetRate();

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_SetRate")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetRate(double rate);

        [DllImport(dll, CallingConvention = CallingConvention.Winapi, EntryPoint = "BASS_ASIO_ChannelEnable")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChannelEnable([MarshalAs(UnmanagedType.Bool)] bool input, int channel, AsioProcedure procedure, IntPtr user);

        internal static IEnumerable<AsioDeviceInfo> EnumerateDevices(int maxDevices = 64)
        {
            for (int i = 0; i < maxDevices; i++)
            {
                if (!getDeviceInfo(i, out var info))
                    yield break;

                string name = Marshal.PtrToStringAnsi(info.Name);
                if (!string.IsNullOrEmpty(name))
                    yield return new AsioDeviceInfo(i, name);
            }
        }

        internal static bool TryFindDeviceIndexByName(string name, out int index)
        {
            foreach (var d in EnumerateDevices())
            {
                if (d.Name == name)
                {
                    index = d.Index;
                    return true;
                }
            }

            index = -1;
            return false;
        }
    }
}
