using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Gajo.Interop
{
    /// <summary>
    /// System-wide low-level mouse and keyboard hooks. They must be installed from a thread
    /// with a message loop (the WPF UI thread) and callbacks must return quickly.
    /// The hooks only observe; every event is passed on unchanged.
    /// </summary>
    internal sealed class GlobalHooks : IDisposable
    {
        private readonly Native.LowLevelProc _mouseProc;   // kept alive: GC must not collect the delegates
        private readonly Native.LowLevelProc _keyboardProc;
        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _keyboardHook = IntPtr.Zero;

        public event Action<int, int>? LeftDown;   // screen px
        public event Action<int, int>? LeftUp;
        public event Action<int, int>? MouseMove;
        public event Action<int, bool>? KeyDown;   // vk, isRepeat
        public event Action<int>? KeyUp;

        /// <summary>When it returns true for a key-down, the key is swallowed (not delivered to any app).</summary>
        public Func<int, bool>? SwallowKey;
        private readonly bool[] _swallowed = new bool[256];

        private readonly bool[] _keyIsDown = new bool[256];

        /// <summary>dwExtraInfo value for keystrokes Gajo sends itself (ignored by the hook).</summary>
        public static readonly IntPtr GajoMarker = new(0x6A6F);

        public GlobalHooks()
        {
            _mouseProc = MouseCallback;
            _keyboardProc = KeyboardCallback;
        }

        public void Install()
        {
            var hMod = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo instalar el gancho de ratón");
            _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
            if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo instalar el gancho de teclado");
        }

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    switch (wParam.ToInt32())
                    {
                        case Native.WM_LBUTTONDOWN: LeftDown?.Invoke(info.pt.X, info.pt.Y); break;
                        case Native.WM_LBUTTONUP: LeftUp?.Invoke(info.pt.X, info.pt.Y); break;
                        case Native.WM_MOUSEMOVE: MouseMove?.Invoke(info.pt.X, info.pt.Y); break;
                    }
                }
                catch { /* never let an exception escape a hook */ }
            }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)(info.vkCode & 0xFF);
                    int msg = wParam.ToInt32();
                    bool injectedByUs = (info.dwExtraInfo == GajoMarker);
                    if (!injectedByUs && (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN))
                    {
                        bool repeat = _keyIsDown[vk];
                        _keyIsDown[vk] = true;
                        KeyDown?.Invoke(vk, repeat);
                        if (SwallowKey != null && SwallowKey(vk)) { _swallowed[vk] = true; return (IntPtr)1; }
                    }
                    else if (!injectedByUs && (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP))
                    {
                        _keyIsDown[vk] = false;
                        KeyUp?.Invoke(vk);
                        if (_swallowed[vk]) { _swallowed[vk] = false; return (IntPtr)1; }
                    }
                }
                catch { }
            }
            return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
            if (_keyboardHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        }
    }
}
