using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Gajo.Core;
using Gajo.UI;

namespace Gajo
{
    /// <summary>Runs the tools of the Ctrl wheel. Tools with options open a small window first.</summary>
    internal sealed partial class ToolRunner
    {
        private readonly ConversionService _svc;

        public ToolRunner(ConversionService svc) { _svc = svc; }

        /// <summary>Tools implemented in this version (the catalog hides the rest).</summary>
        public static bool IsAvailable(string id) => Implemented.Contains(id);

        private static readonly HashSet<string> Implemented = new()
        {
            "crop", "background", "compress", "strip", "rotate",
        };

        public async void Run(WheelItem item, IReadOnlyList<string> files)
        {
            try
            {
                switch (item.Id)
                {
                    case "crop": await CropAsync(files[0]); break;
                    case "background": await BackgroundAsync(files[0]); break;
                    case "compress":
                        _svc.Enqueue(new Job("Comprimiendo", "comprimidos", files, PerFile: (f, ff, p, ct) => ImageTools.CompressAsync(ff, f, ct)));
                        break;
                    case "strip":
                        _svc.Enqueue(new Job("Quitando metadatos", "sin metadatos", files, PerFile: (f, ff, p, ct) => ImageTools.StripMetadataAsync(ff, f, ct)));
                        break;
                    case "rotate":
                        _svc.Enqueue(new Job("Girando", "girados", files, PerFile: (f, ff, p, ct) => ImageTools.RotateAsync(ff, f, clockwise: true, ct)));
                        break;
                    default:
                        RunMedia(item, files);
                        break;
                }
            }
            catch (Exception ex)
            {
                ConversionService.Log(ex.ToString());
                _svc.Toast.ShowError("No se pudo abrir la herramienta", ex.Message);
            }
        }

        partial void RunMediaCore(WheelItem item, IReadOnlyList<string> files, ref bool handled);

        private void RunMedia(WheelItem item, IReadOnlyList<string> files)
        {
            bool handled = false;
            RunMediaCore(item, files, ref handled);
            if (!handled) _svc.Toast.ShowInfo("Próximamente", $"«{item.Label}» llegará en una próxima versión.", 4);
        }

        /// <summary>Decodes the image (any format ffmpeg reads) into a WPF bitmap.</summary>
        private async Task<BitmapSource?> LoadAsync(string file)
        {
            var ffmpeg = await _svc.GetFfmpegAsync();
            if (ffmpeg == null) return null;
            _svc.Toast.ShowProgress("Abriendo imagen", Path.GetFileName(file), null);
            string? png = null;
            try
            {
                png = await ImageTools.DecodeToPngAsync(ffmpeg, file);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(png);
                bmp.EndInit();
                bmp.Freeze();
                _svc.Toast.HideToast();
                return bmp;
            }
            finally
            {
                if (png != null) try { File.Delete(png); } catch { }
            }
        }

        private async Task CropAsync(string file)
        {
            var img = await LoadAsync(file);
            if (img == null) return;
            var dlg = new CropWindow(img);
            if (dlg.ShowDialog() != true) return;
            var r = dlg.Result;
            if (r.X == 0 && r.Y == 0 && r.Width == img.PixelWidth && r.Height == img.PixelHeight)
            {
                _svc.Toast.ShowInfo("Sin cambios", "El recorte ocupa toda la imagen.", 3);
                return;
            }
            _svc.Enqueue(new Job("Recortando", "recortados", new[] { file },
                PerFile: (f, ff, p, ct) => ImageTools.CropAsync(ff, f, r.X, r.Y, r.Width, r.Height, ct)));
        }

        private async Task BackgroundAsync(string file)
        {
            var img = await LoadAsync(file);
            if (img == null) return;
            var dlg = new BackgroundWindow(img);
            if (dlg.ShowDialog() != true || dlg.ResultPng == null) return;
            var output = OutputNaming.UniquePath(file, "png", " (con fondo)");
            await File.WriteAllBytesAsync(output, dlg.ResultPng);
            _svc.Toast.ShowDone("Listo", Path.GetFileName(output), output);
        }
    }
}
