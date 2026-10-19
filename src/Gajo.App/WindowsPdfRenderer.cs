using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Gajo.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Gajo
{
    /// <summary>Renders a region of a PDF page to PNG with the PDF engine built into Windows.</summary>
    internal sealed class WindowsPdfRenderer : IPdfRegionRenderer
    {
        /// <summary>Paints rectangles (page points) white on the rendered PNG.</summary>
        private static byte[] WhiteOut(byte[] png, System.Collections.Generic.IReadOnlyList<(double Left, double Top, double Width, double Height)> rects, double left, double top, double dpi)
        {
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(new MemoryStream(png), System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var src = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var wb = new System.Windows.Media.Imaging.WriteableBitmap(src);
            double k = dpi / 72.0;
            foreach (var r in rects)
            {
                int x = (int)Math.Floor((r.Left - left) * k), y = (int)Math.Floor((r.Top - top) * k);
                int w = (int)Math.Ceiling(r.Width * k), h = (int)Math.Ceiling(r.Height * k);
                x = Math.Max(0, x); y = Math.Max(0, y);
                w = Math.Min(w, wb.PixelWidth - x); h = Math.Min(h, wb.PixelHeight - y);
                if (w <= 0 || h <= 0) continue;
                var white = new byte[w * h * 4];
                Array.Fill(white, (byte)255);
                wb.WritePixels(new System.Windows.Int32Rect(x, y, w, h), white, w * 4, 0);
            }
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(wb));
            using var outMs = new MemoryStream();
            enc.Save(outMs);
            return outMs.ToArray();
        }

        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<byte[]?> RenderPngAsync(string pdfPath, int pageNumber, double left, double top, double width, double height, double dpi, CancellationToken ct,
                                                  System.Collections.Generic.IReadOnlyList<(double Left, double Top, double Width, double Height)>? whiteOut = null)
        {
            await _gate.WaitAsync(ct);
            try
            {
                // Loaded per call: keeping it open would lock the user's PDF.
                var file = await StorageFile.GetFileFromPathAsync(pdfPath);
                var doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
                using var page = doc.GetPage((uint)(pageNumber - 1));

                // PDF points → DIPs (1/96 inch), which is what Windows.Data.Pdf uses.
                const double k = 96.0 / 72.0;
                var src = new Windows.Foundation.Rect(
                    Math.Max(0, left * k), Math.Max(0, top * k),
                    Math.Max(1, Math.Min(width * k, page.Size.Width - left * k)),
                    Math.Max(1, Math.Min(height * k, page.Size.Height - top * k)));
                var options = new WinPdf.PdfPageRenderOptions
                {
                    SourceRect = src,
                    DestinationWidth = (uint)Math.Max(1, Math.Round(width * dpi / 72)),
                    DestinationHeight = (uint)Math.Max(1, Math.Round(height * dpi / 72)),
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                };

                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, options);   // PNG by default
                ct.ThrowIfCancellationRequested();
                stream.Seek(0);
                using var net = stream.AsStreamForRead();
                using var ms = new MemoryStream();
                await net.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();
                if (whiteOut is { Count: > 0 })
                {
                    try { bytes = WhiteOut(bytes, whiteOut, left, top, dpi); }
                    catch (Exception ex) { ConversionService.Log("WhiteOut: " + ex); }
                }
                return bytes;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ConversionService.Log("Render: " + ex);
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
