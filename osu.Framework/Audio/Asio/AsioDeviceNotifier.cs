using System;
using System.Runtime.InteropServices;

namespace osu.Framework.Audio.Asio
{
    // Minimal COM interop for Core Audio notifications (IMMNotificationClient)
    // Only implements the methods we need and forwards events to managed code.
    internal static class AsioDeviceNotifier
    {
        public static event Action? DeviceChanged;

        private static IMMNotificationClientImpl? client;
        private static IMMDeviceEnumerator? enumerator;

        public static void Start()
        {
            if (client != null) return;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                client = new IMMNotificationClientImpl();
                client.DeviceChanged += () => DeviceChanged?.Invoke();
                try
                {
                    // RegisterEndpointNotificationCallback returns an HRESULT; ignore non-zero gracefully
                    _ = enumerator.RegisterEndpointNotificationCallback(client);
                }
                catch
                {
                    // swallow registry failures and stop notifier to allow fallback polling
                    Stop();
                    return;
                }
            }
            catch
            {
                // If CoreAudio APIs aren't available, silently fail - caller should fall back to polling.
                Stop();
            }
        }

        public static void Stop()
        {
                try
                {
                    if (enumerator != null && client != null)
                    {
                        _ = enumerator.UnregisterEndpointNotificationCallback(client);
                    }
                }
                catch { }
            finally
            {
                client = null;
                enumerator = null;
            }
        }

        private class IMMNotificationClientImpl : IMMNotificationClient
        {
            public event Action? DeviceChanged;

            public void OnDeviceStateChanged(string pwstrDeviceId, int dwNewState)
            {
                DeviceChanged?.Invoke();
            }

            public void OnDeviceAdded(string pwstrDeviceId)
            {
                DeviceChanged?.Invoke();
            }

            public void OnDeviceRemoved(string pwstrDeviceId)
            {
                DeviceChanged?.Invoke();
            }

            public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
            {
                DeviceChanged?.Invoke();
            }

            public void OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key)
            {
                DeviceChanged?.Invoke();
            }
        }

        #region COM interop

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int NotImpl1();
            int NotImpl2();
            int RegisterEndpointNotificationCallback(IMMNotificationClient client);
            int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }

        [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMNotificationClient
        {
            void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int dwNewState);
            void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
            void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
            void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
            void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PROPERTYKEY key);
        }

        #endregion
    }
}
