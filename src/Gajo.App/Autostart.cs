using System;
using Microsoft.Win32;

namespace Gajo
{
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Gajo";

        public static void Apply(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled && Environment.ProcessPath is { } exe)
                    key.SetValue(ValueName, $"\"{exe}\" --background");
                else if (key.GetValue(ValueName) != null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch { /* policy-restricted machines: ignore */ }
        }
    }
}
