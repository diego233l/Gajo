using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Gajo
{
    /// <summary>Graphics cards installed, read from the display-adapter class in the registry (no WMI needed).</summary>
    internal static class GpuInfo
    {
        private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        public static IReadOnlyList<string> Detect()
        {
            var names = new List<string>();
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
                if (cls == null) return names;
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                {
                    try
                    {
                        using var k = cls.OpenSubKey(sub);
                        if (k?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc)
                            && !desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                            && !desc.Contains("Remote", StringComparison.OrdinalIgnoreCase)
                            && !desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            && !names.Contains(desc))
                            names.Add(desc);
                    }
                    catch { /* access denied to one adapter key */ }
                }
            }
            catch { }
            return names;
        }
    }
}
