using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Gajo.Interop
{
    /// <summary>Windows "Share" panel (Nearby Share, Mail, apps…) for desktop apps, the Windows equivalent of AirDrop.</summary>
    internal static class ShareHelper
    {
        [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
            void ShowShareUIForWindow(IntPtr appWindow);
        }

        private static readonly Guid DtmIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

        public static void Share(IReadOnlyList<string> paths)
        {
            // The share panel needs a top-level window to belong to: a tiny invisible one at the cursor.
            Native.GetCursorPos(out var pt);
            var host = new Window
            {
                Width = 1, Height = 1, WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0,
                ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize, Title = "Gajo",
                Background = System.Windows.Media.Brushes.Transparent,
            };
            host.Show();
            var hwnd = new WindowInteropHelper(host).Handle;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, pt.X, pt.Y, 1, 1, Native.SWP_NOACTIVATE);
            host.Activate();

            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DtmIid;
            IntPtr ptr = interop.GetForWindow(hwnd, ref iid);
            var dtm = WinRT.MarshalInterface<DataTransferManager>.FromAbi(ptr);

            dtm.DataRequested += async (sender, args) =>
            {
                var deferral = args.Request.GetDeferral();
                try
                {
                    var request = args.Request;
                    request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} archivos";
                    request.Data.Properties.Description = "Compartido con Gajo";
                    var items = new List<IStorageItem>();
                    foreach (var p in paths)
                    {
                        if (Directory.Exists(p)) items.Add(await StorageFolder.GetFolderFromPathAsync(p));
                        else if (File.Exists(p)) items.Add(await StorageFile.GetFileFromPathAsync(p));
                    }
                    request.Data.SetStorageItems(items);
                }
                catch (Exception ex)
                {
                    args.Request.FailWithDisplayText("No se pudieron preparar los archivos: " + ex.Message);
                }
                finally { deferral.Complete(); }
            };
            dtm.TargetApplicationChosen += (_, _) => host.Dispatcher.BeginInvoke(new Action(() => CloseLater(host, 5)));

            interop.ShowShareUIForWindow(hwnd);
            CloseLater(host, 180);
        }

        private static void CloseLater(Window host, int seconds)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (_, _) => { t.Stop(); try { host.Close(); } catch { } };
            t.Start();
        }
    }
}
