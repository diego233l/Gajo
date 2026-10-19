using System;
using System.Collections.Generic;
using System.Linq;
using Gajo.Core;
using Gajo.Interop;

namespace Gajo
{
    /// <summary>
    /// Turns raw global input into high-level signals:
    ///   • TriggerPressed — a wheel key (Mayús/Ctrl/Alt) was tapped while the left button is held and the
    ///     mouse moved (the user is most likely dragging something);
    ///   • DragEnded / Cancelled — the button was released or Escape was pressed;
    ///   • HotkeyPressed — the keyboard shortcut (default Ctrl+Alt+G);
    ///   • while a keyboard wheel is open, arrows/Enter/Esc/Tab are captured and reported.
    /// </summary>
    internal sealed class DragWatcher : IDisposable
    {
        private readonly GlobalHooks _hooks = new();
        private bool _leftDown;
        private bool _dragging;
        private int _downX, _downY;
        private int _thresholdX = 8, _thresholdY = 8;

        public TriggerKey ConvertKey { get; set; } = TriggerKey.Shift;
        public TriggerKey ToolsKey { get; set; } = TriggerKey.Control;
        public TriggerKey ActionsKey { get; set; } = TriggerKey.Alt;

        /// <summary>Hotkey as (modifiers, vk). Modifiers: 1 Ctrl, 2 Alt, 4 Shift, 8 Win.</summary>
        public (int Mods, int Vk) Hotkey { get; set; } = (3, 'G');

        /// <summary>Set while a keyboard-driven wheel is open: navigation keys are captured.</summary>
        public bool CaptureNavigation { get; set; }

        public event Action<WheelMode, int, int>? TriggerPressed;   // cursor position in physical px
        public event Action? DragEnded;
        public event Action? Cancelled;
        public event Action? HotkeyPressed;
        public event Action<string>? NavKey;      // "left", "right", "enter", "esc", "tab", "mode:Convert"…

        public void Start()
        {
            try
            {
                _thresholdX = Math.Max(4, Native.GetSystemMetrics(Native.SM_CXDRAG));
                _thresholdY = Math.Max(4, Native.GetSystemMetrics(Native.SM_CYDRAG));
            }
            catch { }

            _hooks.LeftDown += (x, y) => { _leftDown = true; _dragging = false; _downX = x; _downY = y; };
            _hooks.MouseMove += (x, y) =>
            {
                if (_leftDown && !_dragging &&
                    (Math.Abs(x - _downX) > _thresholdX || Math.Abs(y - _downY) > _thresholdY))
                    _dragging = true;
            };
            _hooks.LeftUp += (_, _) =>
            {
                bool was = _dragging;
                _leftDown = false;
                _dragging = false;
                if (was) DragEnded?.Invoke();
            };
            _hooks.KeyDown += OnKeyDown;
            _hooks.SwallowKey = vk => CaptureNavigation && IsNavKey(vk) || IsHotkey(vk);
            _hooks.Install();
        }

        private void OnKeyDown(int vk, bool repeat)
        {
            if (CaptureNavigation)
            {
                switch (vk)
                {
                    case 0x25: case 0x26: NavKey?.Invoke("left"); return;    // ← ↑
                    case 0x27: case 0x28: NavKey?.Invoke("right"); return;   // → ↓
                    case 0x0D: if (!repeat) NavKey?.Invoke("enter"); return;
                    case Native.VK_ESCAPE: NavKey?.Invoke("esc"); return;
                    case 0x09: if (!repeat) NavKey?.Invoke("tab"); return;
                }
                if (!repeat && ModeOf(vk) is { } m) { NavKey?.Invoke("mode:" + m); return; }
            }

            if (vk == Native.VK_ESCAPE) { Cancelled?.Invoke(); return; }
            if (!repeat && IsHotkey(vk)) { HotkeyPressed?.Invoke(); return; }
            if (repeat || !_dragging) return;
            if (ModeOf(vk) is { } mode)
            {
                Native.GetCursorPos(out var pt);
                TriggerPressed?.Invoke(mode, pt.X, pt.Y);
            }
        }

        private static bool IsNavKey(int vk) => vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x0D or 0x09 or Native.VK_ESCAPE;

        private bool IsHotkey(int vk)
        {
            if (Hotkey.Vk == 0 || vk != Hotkey.Vk) return false;
            bool Down(int k) => (Native.GetAsyncKeyState(k) & 0x8000) != 0;
            int mods = (Down(Native.VK_CONTROL) ? 1 : 0) | (Down(Native.VK_MENU) ? 2 : 0) | (Down(Native.VK_SHIFT) ? 4 : 0)
                       | (Down(0x5B) || Down(0x5C) ? 8 : 0);
            return mods == Hotkey.Mods;
        }

        private WheelMode? ModeOf(int vk)
        {
            if (Matches(ConvertKey, vk)) return WheelMode.Convert;
            if (Matches(ToolsKey, vk)) return WheelMode.Tools;
            if (Matches(ActionsKey, vk)) return WheelMode.Actions;
            return null;
        }

        private static bool Matches(TriggerKey k, int vk) => k switch
        {
            TriggerKey.Shift => vk is Native.VK_LSHIFT or Native.VK_RSHIFT or Native.VK_SHIFT,
            TriggerKey.Control => vk is Native.VK_LCONTROL or Native.VK_RCONTROL or Native.VK_CONTROL,
            TriggerKey.Alt => vk is Native.VK_LMENU or Native.VK_RMENU or Native.VK_MENU,
            _ => false,
        };

        /// <summary>"Ctrl+Alt+G" → (3, 'G'). Unknown text → (0, 0) = disabled.</summary>
        public static (int Mods, int Vk) ParseHotkey(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return (0, 0);
            int mods = 0, vk = 0;
            foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = raw.ToUpperInvariant();
                switch (p)
                {
                    case "CTRL": case "CONTROL": mods |= 1; break;
                    case "ALT": mods |= 2; break;
                    case "SHIFT": case "MAYÚS": case "MAYUS": mods |= 4; break;
                    case "WIN": mods |= 8; break;
                    case "ESPACIO": case "SPACE": vk = 0x20; break;
                    default:
                        if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vk = p[0];
                        else if (p.StartsWith("F") && int.TryParse(p[1..], out var f) && f is >= 1 and <= 12) vk = 0x70 + f - 1;
                        break;
                }
            }
            return mods == 0 || vk == 0 ? (0, 0) : (mods, vk);
        }

        public void Dispose() => _hooks.Dispose();
    }
}
