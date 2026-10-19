using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gajo.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using OcrLine = Gajo.Core.OcrLine;
using WinPdf = Windows.Data.Pdf;

namespace Gajo
{
    /// <summary>
    /// OCR with what Windows 10/11 already includes: Windows.Data.Pdf renders the page and
    /// Windows.Media.Ocr reads it, in the languages installed in Windows (Settings → Language).
    /// Nothing is downloaded or bundled.
    /// </summary>
    internal sealed class WindowsOcr : IPdfOcr
    {
        private const double Dpi = 300;
        private readonly OcrEngine? _engine;

        public WindowsOcr()
        {
            try
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine == null)
                {
                    // Prefer Spanish, then English, then anything available.
                    var langs = OcrEngine.AvailableRecognizerLanguages.ToList();
                    var pick = langs.FirstOrDefault(l => l.LanguageTag.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                               ?? langs.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                               ?? langs.FirstOrDefault();
                    if (pick != null) _engine = OcrEngine.TryCreateFromLanguage(pick);
                }
            }
            catch (Exception ex)
            {
                ConversionService.Log(ex.ToString());
                _engine = null;
            }
        }

        public string? UnavailableReason => _engine == null
            ? "Windows no tiene instalado el reconocimiento de texto. Añádelo en Configuración → Hora e idioma → Idioma → Español → Opciones → Reconocimiento óptico de caracteres."
            : null;

        /// <summary>Shown in Settings, e.g. "Español (España)".</summary>
        public string? LanguageName => _engine?.RecognizerLanguage?.DisplayName;

        public async Task<IReadOnlyList<OcrLine>> RecognizeImageAsync(byte[] png, CancellationToken ct)
        {
            if (_engine == null) return Array.Empty<OcrLine>();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            // The engine has a maximum image size: scale big pictures down (coordinates are scaled back).
            double scale = 1;
            double max = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            if (max > OcrEngine.MaxImageDimension) scale = OcrEngine.MaxImageDimension / max;
            SoftwareBitmap bitmap;
            if (scale < 1)
            {
                var tf = new BitmapTransform { ScaledWidth = (uint)(decoder.PixelWidth * scale), ScaledHeight = (uint)(decoder.PixelHeight * scale) };
                bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tf, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            }
            else bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using (bitmap)
            {
                ct.ThrowIfCancellationRequested();
                var result = await _engine.RecognizeAsync(bitmap);
                var lines = new List<OcrLine>();
                foreach (var line in result.Lines)
                {
                    var words = line.Words;
                    if (words.Count == 0) continue;
                    double x0 = words.Min(w => w.BoundingRect.X), y0 = words.Min(w => w.BoundingRect.Y);
                    double x1 = words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                    double y1 = words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                    lines.Add(new OcrLine(line.Text, x0 / scale, y0 / scale, (x1 - x0) / scale, (y1 - y0) / scale));
                }
                return lines;
            }
        }

        public async Task<IReadOnlyList<OcrLine>> RecognizePageAsync(string pdfPath, int pageNumber, CancellationToken ct)
        {
            if (_engine == null) return Array.Empty<OcrLine>();

            var file = await StorageFile.GetFileFromPathAsync(pdfPath);
            var doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
            using var page = doc.GetPage((uint)(pageNumber - 1));
            ct.ThrowIfCancellationRequested();

            // Page.Size is in DIPs (1/96 inch). Render at ~300 dpi, within the OCR engine's limit.
            double scale = Dpi / 96.0;
            double max = Math.Max(page.Size.Width, page.Size.Height) * scale;
            double limit = Math.Max(1000, OcrEngine.MaxImageDimension - 16);
            if (max > limit) scale *= limit / max;
            var options = new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };

            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            ct.ThrowIfCancellationRequested();

            var result = await _engine.RecognizeAsync(bitmap);
            var lines = new List<OcrLine>();
            foreach (var line in result.Lines)
            {
                var words = line.Words;
                if (words.Count == 0) continue;
                double x0 = words.Min(w => w.BoundingRect.X), y0 = words.Min(w => w.BoundingRect.Y);
                double x1 = words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                double y1 = words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                lines.Add(new OcrLine(line.Text, x0, y0, x1 - x0, y1 - y0));
            }
            return lines;
        }
    }
}
