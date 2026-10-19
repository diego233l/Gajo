using System;
using System.IO;
using System.Text.Json;

namespace Gajo.Core
{
    public enum TriggerKey { Shift, Control, Alt }

    /// <summary>
    /// Portable settings: stored in "Gajo.settings.json" next to Gajo.exe. Only if that folder
    /// is read-only (e.g. Program Files) they fall back to %APPDATA%\Gajo.
    /// </summary>
    public sealed class Settings
    {
        /// <summary>Key for the conversion wheel (named TriggerKey for compatibility with older settings files).</summary>
        public TriggerKey TriggerKey { get; set; } = TriggerKey.Shift;
        /// <summary>Key for the tools wheel.</summary>
        public TriggerKey ToolsKey { get; set; } = TriggerKey.Control;
        /// <summary>Key for the actions wheel.</summary>
        public TriggerKey ActionsKey { get; set; } = TriggerKey.Alt;
        /// <summary>Encode video on the graphics card when one is available.</summary>
        public bool UseGpu { get; set; } = true;
        /// <summary>Keyboard shortcut that opens the wheel for the files selected in Explorer.</summary>
        public string Hotkey { get; set; } = "Ctrl+Alt+G";
        public bool StartWithWindows { get; set; } = false;
        public bool ShowNotifications { get; set; } = true;
        public int JpegQuality { get; set; } = 92;
        public string? FfmpegPath { get; set; }
        public bool FirstRunDone { get; set; }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        private const string FileName = "Gajo.settings.json";

        private static string PortablePath =>
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory, FileName);

        private static string FallbackPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gajo", FileName);

        public static Settings Load()
        {
            foreach (var path in new[] { PortablePath, FallbackPath })
            {
                try
                {
                    if (File.Exists(path))
                        return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
                }
                catch { /* corrupt file → defaults */ }
            }
            return new Settings();
        }

        public void Save()
        {
            var text = JsonSerializer.Serialize(this, Json);
            try
            {
                File.WriteAllText(PortablePath, text);
                return;
            }
            catch { /* read-only folder */ }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
                File.WriteAllText(FallbackPath, text);
            }
            catch { }
        }
    }
}
