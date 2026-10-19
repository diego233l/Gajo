using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Gajo.Core
{
    /// <summary>Finds the conversion engine. Normally it is the copy built into Gajo.exe.</summary>
    public static class Ffmpeg
    {
        private static readonly object Gate = new();
        private static string? _embedded;
        private static bool _embeddedTried;

        private static string ExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

        /// <summary>Where the built-in engine is unpacked (temporary folder: Windows may clean it, Gajo recreates it).</summary>
        public static string CacheFolder => Path.Combine(Path.GetTempPath(), "Gajo");

        /// <summary>Unpacks the engine built into the running exe, once. Safe to call from any thread.</summary>
        public static string? EnsureEmbedded()
        {
            lock (Gate)
            {
                if (_embedded != null && File.Exists(_embedded)) return _embedded;
                if (_embeddedTried && _embedded == null) return null;
                _embeddedTried = true;
                var exe = Environment.ProcessPath;
                if (exe == null) return null;
                _embedded = EmbeddedPayload.Extract(exe, CacheFolder, "ffmpeg", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");
                if (_embedded != null && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    File.SetUnixFileMode(_embedded, (UnixFileMode)0b111_101_101);
                return _embedded;
            }
        }

        public static bool HasEmbedded => Environment.ProcessPath is { } p && EmbeddedPayload.Read(p) != null;

        /// <summary>Order: custom path from settings → built-in engine → next to Gajo.exe → PATH.</summary>
        public static string? Locate(string? customPath = null)
        {
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath)) return customPath;

            var embedded = EnsureEmbedded();
            if (embedded != null) return embedded;

            var baseDir = AppContext.BaseDirectory;
            foreach (var c in new[] { Path.Combine(baseDir, ExeName), Path.Combine(baseDir, "ffmpeg", "bin", ExeName) })
                if (File.Exists(c)) return c;

            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var c = Path.Combine(dir.Trim('"'), ExeName);
                    if (File.Exists(c)) return c;
                }
                catch { /* malformed PATH entry */ }
            }
            return null;
        }
    }
}
