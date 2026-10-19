using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace Gajo.Core
{
    /// <summary>PDF → one image per page (300 dpi). A single page gives one file; several go into a folder.</summary>
    public static class PdfImages
    {
        public const double Dpi = 300;

        public static async Task<string> ExportAsync(string ffmpeg, string pdf, string ext, IPdfRegionRenderer renderer, IProgress<double>? progress, CancellationToken ct)
        {
            List<(double W, double H)> sizes;
            try
            {
                sizes = await Task.Run(() =>
                {
                    using var doc = PdfDocument.Open(pdf);
                    return doc.GetPages().Select(p => (p.Width, p.Height)).ToList();
                }, ct);
            }
            catch (Exception ex) when (ex.GetType().Name.Contains("Encrypted"))
            {
                throw new ConversionException("El PDF está protegido con contraseña.");
            }
            if (sizes.Count == 0) throw new ConversionException("El PDF no tiene páginas.");

            string name = Path.GetFileNameWithoutExtension(pdf);
            string dir = Path.GetDirectoryName(Path.GetFullPath(pdf))!;
            string targetDir = dir;
            if (sizes.Count > 1)
            {
                targetDir = Path.Combine(dir, $"{name} (páginas {ext.ToUpperInvariant()})");
                int n = 2;
                while (Directory.Exists(targetDir) || File.Exists(targetDir)) targetDir = Path.Combine(dir, $"{name} (páginas {ext.ToUpperInvariant()}) {n++}");
                Directory.CreateDirectory(targetDir);
            }

            int digits = Math.Max(2, sizes.Count.ToString().Length);
            string? single = null;
            for (int i = 0; i < sizes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var png = await renderer.RenderPngAsync(pdf, i + 1, 0, 0, sizes[i].W, sizes[i].H, Dpi, ct)
                          ?? throw new ConversionException($"No se pudo dibujar la página {i + 1}.");
                string file = sizes.Count == 1
                    ? OutputNaming.UniquePath(pdf, ext)
                    : Path.Combine(targetDir, $"{name} - {(i + 1).ToString().PadLeft(digits, '0')}.{ext}");

                if (ext == "png")
                {
                    await File.WriteAllBytesAsync(file, png, ct);
                }
                else
                {
                    var tmpPng = Path.Combine(Path.GetTempPath(), "gajo-" + Guid.NewGuid().ToString("N") + ".png");
                    try
                    {
                        await File.WriteAllBytesAsync(tmpPng, png, ct);
                        await FfmpegRunner.RunAsync(ffmpeg, FfmpegArgs.BuildPdfJpeg(tmpPng, file), null, ct);
                    }
                    finally { try { File.Delete(tmpPng); } catch { } }
                }
                single = file;
                progress?.Report((i + 1.0) / sizes.Count);
            }
            return sizes.Count == 1 ? single! : targetDir;
        }
    }
}
