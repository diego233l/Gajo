using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Gajo.Interop
{
    /// <summary>Files selected in the Explorer window (or desktop) that has the focus, via the Shell's COM API.</summary>
    internal static class ExplorerSelection
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        public static List<string> Get()
        {
            var result = new List<string>();
            IntPtr fg = GetForegroundWindow();
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string className = cls.ToString();

            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return result;
            dynamic? shell = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                if (shell == null) return result;
                dynamic windows = shell.Windows();

                if (className is "Progman" or "WorkerW")
                {
                    // Desktop
                    object loc = 0, root = Type.Missing;
                    int hwnd;
                    dynamic desktop = windows.FindWindowSW(ref loc, ref root, 8 /* SWC_DESKTOP */, out hwnd, 1 /* SWFO_NEEDDISPATCH */);
                    AddSelected(desktop?.Document, result);
                    return result;
                }

                // Explorer windows: with tabs (Windows 11) several entries share the window handle;
                // the first one with a selection wins.
                foreach (dynamic w in windows)
                {
                    try
                    {
                        long h = Convert.ToInt64(w.HWND);
                        if (h != fg.ToInt64()) continue;
                        var before = result.Count;
                        AddSelected(w.Document, result);
                        if (result.Count > before) break;
                    }
                    catch { /* Internet Explorer windows, closing windows… */ }
                }
            }
            catch { }
            finally
            {
                if (shell != null) try { Marshal.FinalReleaseComObject(shell); } catch { }
            }
            return result;
        }

        private static void AddSelected(dynamic? document, List<string> into)
        {
            if (document == null) return;
            try
            {
                dynamic items = document.SelectedItems();
                foreach (dynamic item in items)
                {
                    string? path = item.Path as string;
                    if (!string.IsNullOrEmpty(path) && (System.IO.File.Exists(path) || System.IO.Directory.Exists(path)))
                        into.Add(path!);
                }
            }
            catch { }
        }
    }
}
