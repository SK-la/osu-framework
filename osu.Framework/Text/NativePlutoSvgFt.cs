// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using FreeTypeSharp;
using osu.Framework.Logging;
using static FreeTypeSharp.FT;
using static FreeTypeSharp.FT_Error;

namespace osu.Framework.Text
{
    /// <summary>
    /// Loads the PlutoSVG FreeType OT-SVG hooks shipped under <c>runtimes/*/native</c>
    /// and registers them on a FreeType library instance.
    /// </summary>
    internal static unsafe class NativePlutoSvgFt
    {
        private const string library_name = "plutosvgft";

        private static readonly Lock resolver_lock = new Lock();
        private static bool resolverRegistered;
        private static bool freetypePreloaded;

        /// <summary>
        /// Whether SVG hooks were successfully registered on the shared FreeType library.
        /// </summary>
        public static bool HooksRegistered { get; private set; }

        [DllImport(library_name, EntryPoint = "plutosvg_get_ft_hooks", CallingConvention = CallingConvention.Cdecl)]
        private static extern void* get_ft_hooks();

        /// <summary>
        /// Attempt to register PlutoSVG as the FreeType <c>ot-svg</c> renderer.
        /// Soft-fails (logs and returns false) when the native library is missing for the current RID.
        /// </summary>
        public static bool TryRegister(FT_LibraryRec_* library)
        {
            if (HooksRegistered)
                return true;

            if (library == null)
                return false;

            ensureResolver();

            try
            {
                void* hooks = get_ft_hooks();

                if (hooks == null)
                {
                    Logger.Log("plutosvg_get_ft_hooks returned null; OT-SVG colour emoji unavailable.", LoggingTarget.Runtime, LogLevel.Important);
                    return false;
                }

                // FreeType expects a pointer to SVG_RendererHooks.
                byte* module = stackalloc byte[] { (byte)'o', (byte)'t', (byte)'-', (byte)'s', (byte)'v', (byte)'g', 0 };
                byte* property = stackalloc byte[]
                {
                    (byte)'s', (byte)'v', (byte)'g', (byte)'-', (byte)'h', (byte)'o', (byte)'o', (byte)'k', (byte)'s', 0
                };

                FT_Error error = FT_Property_Set(library, module, property, hooks);

                if (error != FT_Err_Ok)
                {
                    Logger.Log($"FT_Property_Set(ot-svg, svg-hooks) failed: {error}", LoggingTarget.Runtime, LogLevel.Important);
                    return false;
                }

                HooksRegistered = true;
                Logger.Log("Registered PlutoSVG FreeType OT-SVG hooks.", LoggingTarget.Runtime);
                return true;
            }
            catch (DllNotFoundException e)
            {
                Logger.Log($"plutosvgft native library not found ({e.Message}); OT-SVG colour emoji unavailable.", LoggingTarget.Runtime);
                return false;
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to register PlutoSVG OT-SVG hooks: {e.Message}", LoggingTarget.Runtime, LogLevel.Important);
                return false;
            }
        }

        private static void ensureResolver()
        {
            lock (resolver_lock)
            {
                if (resolverRegistered)
                    return;

                NativeLibrary.SetDllImportResolver(typeof(NativePlutoSvgFt).Assembly, resolve);
                resolverRegistered = true;
            }
        }

        private static IntPtr resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(name, library_name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "libplutosvgft", StringComparison.OrdinalIgnoreCase))
            {
                return IntPtr.Zero;
            }

            // plutosvgft.dll imports freetype.dll by bare name; Windows only searches the loaded
            // DLL's directory / PATH — not .NET's RID folder — unless freetype is already mapped.
            preloadFreetype(assembly);

            if (NativeLibrary.TryLoad(library_name, assembly, searchPath, out IntPtr handle))
                return handle;

            if (NativeLibrary.TryLoad("libplutosvgft", assembly, searchPath, out handle))
                return handle;

            string baseDir = AppContext.BaseDirectory;

            foreach (string candidate in enumeratePlutoSvgCandidates(baseDir))
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                    return handle;
            }

            // ProjectReference / single-file layouts may leave natives next to the Framework assembly.
            string? asmDir = Path.GetDirectoryName(assembly.Location);

            if (!string.IsNullOrEmpty(asmDir) && !string.Equals(asmDir, baseDir, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string candidate in enumeratePlutoSvgCandidates(asmDir))
                {
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                        return handle;
                }
            }

            return IntPtr.Zero;
        }

        private static void preloadFreetype(Assembly assembly)
        {
            if (freetypePreloaded)
                return;

            foreach (string candidate in enumerateFreetypeCandidates(AppContext.BaseDirectory))
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _))
                {
                    freetypePreloaded = true;
                    return;
                }
            }

            string? asmDir = Path.GetDirectoryName(assembly.Location);

            if (!string.IsNullOrEmpty(asmDir))
            {
                foreach (string candidate in enumerateFreetypeCandidates(asmDir))
                {
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _))
                    {
                        freetypePreloaded = true;
                        return;
                    }
                }
            }

            // Last resort: already loaded by FreeTypeSharp under another search path.
            if (NativeLibrary.TryLoad("freetype", assembly, null, out _)
                || NativeLibrary.TryLoad("libfreetype", assembly, null, out _))
            {
                freetypePreloaded = true;
            }
        }

        private static IEnumerable<string> enumerateFreetypeCandidates(string baseDir)
        {
            if (OperatingSystem.IsWindows())
            {
                yield return Path.Combine(baseDir, "freetype.dll");
                yield return Path.Combine(baseDir, "runtimes", "win-x64", "native", "freetype.dll");
                yield return Path.Combine(baseDir, "runtimes", "win-arm64", "native", "freetype.dll");
                yield return Path.Combine(baseDir, "runtimes", "win-x86", "native", "freetype.dll");
            }
            else if (OperatingSystem.IsLinux())
            {
                yield return Path.Combine(baseDir, "libfreetype.so");
                yield return Path.Combine(baseDir, "runtimes", "linux-x64", "native", "libfreetype.so");
                yield return Path.Combine(baseDir, "runtimes", "linux-arm64", "native", "libfreetype.so");
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return Path.Combine(baseDir, "libfreetype.dylib");
                yield return Path.Combine(baseDir, "runtimes", "osx", "native", "libfreetype.dylib");
                yield return Path.Combine(baseDir, "runtimes", "osx-x64", "native", "libfreetype.dylib");
                yield return Path.Combine(baseDir, "runtimes", "osx-arm64", "native", "libfreetype.dylib");
            }
        }

        private static IEnumerable<string> enumeratePlutoSvgCandidates(string baseDir)
        {
            // Published RID layouts place natives next to the app; test/dev layouts keep runtimes/<rid>/native.
            if (OperatingSystem.IsWindows())
            {
                yield return Path.Combine(baseDir, "plutosvgft.dll");
                yield return Path.Combine(baseDir, "runtimes", "win-x64", "native", "plutosvgft.dll");
                yield return Path.Combine(baseDir, "runtimes", "win-arm64", "native", "plutosvgft.dll");
                yield return Path.Combine(baseDir, "runtimes", "win-x86", "native", "plutosvgft.dll");
            }
            else if (OperatingSystem.IsLinux())
            {
                yield return Path.Combine(baseDir, "libplutosvgft.so");
                yield return Path.Combine(baseDir, "plutosvgft.so");
                yield return Path.Combine(baseDir, "runtimes", "linux-x64", "native", "libplutosvgft.so");
                yield return Path.Combine(baseDir, "runtimes", "linux-arm64", "native", "libplutosvgft.so");
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return Path.Combine(baseDir, "libplutosvgft.dylib");
                yield return Path.Combine(baseDir, "runtimes", "osx", "native", "libplutosvgft.dylib");
                yield return Path.Combine(baseDir, "runtimes", "osx-x64", "native", "libplutosvgft.dylib");
                yield return Path.Combine(baseDir, "runtimes", "osx-arm64", "native", "libplutosvgft.dylib");
            }
        }
    }
}
