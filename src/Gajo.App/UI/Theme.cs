using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace Gajo.UI
{
    /// <summary>Light/dark palette that follows Windows (Settings → Personalization → Colors → app mode).</summary>
    internal sealed class Theme
    {
        public bool Dark { get; private init; }

        public Brush DiscFill { get; private init; } = null!;
        public Brush DiscStroke { get; private init; } = null!;
        public Color Shadow { get; private init; }
        public Brush PetalFill { get; private init; } = null!;
        public Brush PetalHover { get; private init; } = null!;
        public Brush PetalStroke { get; private init; } = null!;
        public Brush Label { get; private init; } = null!;
        public Brush LabelHover { get; private init; } = null!;
        public Brush HubFill { get; private init; } = null!;
        public Brush HubStroke { get; private init; } = null!;
        public Brush Chip { get; private init; } = null!;
        public Brush ChipText { get; private init; } = null!;

        // Cards (toast, shelf)
        public Brush CardFill { get; private init; } = null!;
        public Brush CardBorder { get; private init; } = null!;
        public Brush Text { get; private init; } = null!;
        public Brush MutedText { get; private init; } = null!;
        public Brush SecondaryButton { get; private init; } = null!;
        public Brush Track { get; private init; } = null!;

        public static readonly Color Accent = Color.FromRgb(0xF0, 0x55, 0x24);

        public static bool WindowsIsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        public static Theme Current => WindowsIsDark() ? DarkTheme : LightTheme;

        private static readonly Theme LightTheme = new()
        {
            Dark = false,
            DiscFill = Radial(0xF4FFF4EA, 0xECFFE2C9),
            DiscStroke = Solid(0x90FFFFFF),
            Shadow = Color.FromRgb(0x7A, 0x2E, 0x00),
            PetalFill = Linear(0xFFFFF8F0, 0xFFFFE6CF),
            PetalHover = Linear(0xFFFF7443, 0xFFE8361C),
            PetalStroke = Solid(0xFFFFF1E4),
            Label = Solid(0xFF8A4419),
            LabelHover = Solid(0xFFFFFFFF),
            HubFill = Linear(0xFFFFFFFF, 0xFFFFEEDD),
            HubStroke = Solid(0x40E8590C),
            Chip = Solid(0xFFFFFFFF),
            ChipText = Solid(0xFFB4501F),
            CardFill = Solid(0xF7FFFBF7),
            CardBorder = Solid(0x30C06020),
            Text = Solid(0xFF2B1A10),
            MutedText = Solid(0xFF7A5C4A),
            SecondaryButton = Solid(0xFFF4E6DC),
            Track = Solid(0xFFF6E3D6),
        };

        private static readonly Theme DarkTheme = new()
        {
            Dark = true,
            DiscFill = Radial(0xF22A221E, 0xEE1E1815),
            DiscStroke = Solid(0x40FFFFFF),
            Shadow = Color.FromRgb(0x00, 0x00, 0x00),
            PetalFill = Linear(0xFF3A2F29, 0xFF2D2420),
            PetalHover = Linear(0xFFFF7443, 0xFFE8361C),
            PetalStroke = Solid(0xFF201A17),
            Label = Solid(0xFFF6CDAE),
            LabelHover = Solid(0xFFFFFFFF),
            HubFill = Linear(0xFF3B302A, 0xFF2A221E),
            HubStroke = Solid(0x60FF7A45),
            Chip = Solid(0xFF3A2F29),
            ChipText = Solid(0xFFFF9A6A),
            CardFill = Solid(0xF5262019),
            CardBorder = Solid(0x40FF8A50),
            Text = Solid(0xFFF5ECE6),
            MutedText = Solid(0xFFC9B3A5),
            SecondaryButton = Solid(0xFF3A2F29),
            Track = Solid(0xFF3A2F29),
        };

        private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        private static Brush Solid(uint argb) { var b = new SolidColorBrush(C(argb)); b.Freeze(); return b; }
        private static Brush Linear(uint top, uint bottom) { var b = new LinearGradientBrush(C(top), C(bottom), 90); b.Freeze(); return b; }
        private static Brush Radial(uint inner, uint outer) { var b = new RadialGradientBrush(C(inner), C(outer)); b.Freeze(); return b; }
    }
}
