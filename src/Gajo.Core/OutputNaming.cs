using System.IO;

namespace Gajo.Core
{
    public static class OutputNaming
    {
        /// <summary>"foto.heic" → "foto.jpg"; if it exists → "foto 2.jpg", "foto 3.jpg"…</summary>
        public static string UniquePath(string inputPath, string extension, string? suffix = null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".";
            var name = Path.GetFileNameWithoutExtension(inputPath) + (suffix ?? "");
            var candidate = Path.Combine(dir, $"{name}.{extension}");
            int n = 2;
            while (File.Exists(candidate) || Directory.Exists(candidate))
            {
                candidate = Path.Combine(dir, $"{name} {n}.{extension}");
                n++;
            }
            return candidate;
        }

        public static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
            return u == 0 ? $"{bytes} B" : (v < 10 ? $"{v:0.0} {units[u]}" : $"{v:0} {units[u]}");
        }
    }
}
