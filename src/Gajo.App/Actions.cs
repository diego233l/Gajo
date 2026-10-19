using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Gajo.Core;
using Gajo.Interop;
using Gajo.UI;
using WinForms = System.Windows.Forms;

namespace Gajo
{
    /// <summary>Actions wheel, shelf and keyboard use.</summary>
    internal sealed partial class AppController
    {
        private ShelfWindow? _shelf;

        private ShelfWindow Shelf => _shelf ??= new ShelfWindow();

        partial void RunActionCore(WheelItem item, IReadOnlyList<string> files)
        {
            try
            {
                switch (item.Id)
                {
                    case "copy":
                        var list = new StringCollection();
                        list.AddRange(files.ToArray());
                        Clipboard.SetFileDropList(list);
                        _toast!.ShowInfo("Copiado", files.Count == 1 ? $"{Path.GetFileName(files[0].TrimEnd('\\'))} está en el portapapeles: pégalo con Ctrl+V." : $"{files.Count} elementos en el portapapeles: pégalos con Ctrl+V.", 4);
                        break;

                    case "path":
                        Clipboard.SetText(string.Join("\r\n", files));
                        _toast!.ShowInfo(files.Count == 1 ? "Ruta copiada" : "Rutas copiadas", files.Count == 1 ? files[0] : $"{files.Count} rutas, una por línea.", 4);
                        break;

                    case "move":
                        MoveTo(files);
                        break;

                    case "terminal":
                        OpenTerminal(Directory.Exists(files[0]) ? files[0] : Path.GetDirectoryName(files[0])!);
                        break;

                    case "share":
                        ShareHelper.Share(files);
                        break;

                    case "shelf":
                        Shelf.Add(files);
                        break;
                }
            }
            catch (Exception ex)
            {
                ConversionService.Log(ex.ToString());
                _toast?.ShowError($"No se pudo «{item.Label}»", ex.Message);
            }
        }

        private void MoveTo(IReadOnlyList<string> files)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = files.Count == 1 ? $"Mover «{Path.GetFileName(files[0].TrimEnd('\\'))}» a…" : $"Mover {files.Count} elementos a…",
                InitialDirectory = Path.GetDirectoryName(files[0].TrimEnd('\\')),
            };
            if (dlg.ShowDialog() != true) return;
            var dest = dlg.FolderName;
            int moved = 0;
            var errors = new List<string>();
            string? last = null;
            foreach (var src in files)
            {
                try
                {
                    var target = UniqueIn(dest, Path.GetFileName(src.TrimEnd('\\')));
                    if (string.Equals(Path.GetDirectoryName(src.TrimEnd('\\')), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.Exists(src)) MoveDirectory(src, target);
                    else MoveFile(src, target);
                    last = target;
                    moved++;
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(src)}: {ex.Message}"); }
            }
            if (errors.Count == 0) _toast!.ShowDone(moved == 1 ? "Movido" : $"{moved} elementos movidos", dest, last);
            else _toast!.ShowError($"{moved} de {files.Count} movidos", string.Join("\n", errors.Take(3)));
        }

        private static string UniqueIn(string dir, string name)
        {
            var candidate = Path.Combine(dir, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (int n = 2; ; n++)
            {
                candidate = Path.Combine(dir, $"{stem} {n}{ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        private static void MoveFile(string src, string dst)
        {
            try { File.Move(src, dst); }
            catch (IOException) when (!SameVolume(src, dst))
            {
                File.Copy(src, dst);
                File.Delete(src);
            }
        }

        private static void MoveDirectory(string src, string dst)
        {
            if (SameVolume(src, dst)) { Directory.Move(src, dst); return; }
            CopyDirectory(src, dst);
            Directory.Delete(src, recursive: true);
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        private static bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

        private static void OpenTerminal(string folder)
        {
            // Windows Terminal if present, otherwise PowerShell.
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true, ArgumentList = { "-d", folder } });
                return;
            }
            catch { }
            Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder, ArgumentList = { "-NoExit" } });
        }

        // ------------------------------------------------------------------ keyboard

        partial void OpenKeyboardWheelCore()
        {
            if (_wheel == null) return;
            var files = ExplorerSelection.Get();
            if (files.Count == 0)
            {
                _toast?.ShowInfo("Nada seleccionado", "Selecciona uno o varios archivos en el Explorador o en el escritorio y vuelve a pulsar el atajo.", 4);
                return;
            }
            Native.GetCursorPos(out var pt);
            _wheel.OpenForFiles(WheelMode.Convert, files, pt.X, pt.Y);
            if (_watcher != null) _watcher.CaptureNavigation = true;
        }

        partial void AddTrayItemsCore(WinForms.ContextMenuStrip menu)
        {
            menu.Items.Add("Mostrar estante", null, (_, _) => Shelf.ShowShelf());
        }
    }
}
