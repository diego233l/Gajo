using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Gajo.Core
{
    /// <summary>Image tools of the Ctrl wheel. Every tool writes a new file next to the original.</summary>
    public static class ImageTools
    {
        private static string I(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

        /// <summary>ffmpeg arguments that apply <paramref name="filter"/> (may be empty) and encode to the extension's format.</summary>
        private static List<string> Encode(string filter, string ext, int jpegQuality = 92)
        {
            var a = new List<string>();
            string f = string.IsNullOrEmpty(filter) ? "" : filter + ",";
            switch (Formats.NormalizedExtension("x." + ext))
            {
                case "jpg":
                    int q = jpegQuality >= 95 ? 2 : jpegQuality >= 90 ? 3 : jpegQuality >= 80 ? 5 : jpegQuality >= 70 ? 7 : 9;
                    a.AddRange(new[] { "-filter_complex", $"[0:v]{f}format=rgba,split[a][b];[a]drawbox=c=white@1:replace=1:t=fill[bg];[bg][b]overlay=format=auto,format=rgb24", "-q:v", q.ToString() });
                    break;
                case "png": if (f.Length > 0) a.AddRange(new[] { "-vf", f.TrimEnd(',') }); a.AddRange(new[] { "-c:v", "png" }); break;
                case "webp": if (f.Length > 0) a.AddRange(new[] { "-vf", f.TrimEnd(',') }); a.AddRange(new[] { "-c:v", "libwebp", "-quality", jpegQuality.ToString() }); break;
                case "avif": a.AddRange(new[] { "-vf", f + "format=yuv420p", "-c:v", "libaom-av1", "-still-picture", "1", "-crf", jpegQuality >= 85 ? "26" : "34", "-cpu-used", "6" }); break;
                case "tiff": if (f.Length > 0) a.AddRange(new[] { "-vf", f.TrimEnd(',') }); a.AddRange(new[] { "-c:v", "tiff", "-compression_algo", "deflate" }); break;
                case "bmp": if (f.Length > 0) a.AddRange(new[] { "-vf", f.TrimEnd(',') }); a.AddRange(new[] { "-c:v", "bmp" }); break;
                case "ico": a.AddRange(new[] { "-vf", f + "scale='min(256,iw)':'min(256,ih)':force_original_aspect_ratio=decrease,format=rgba", "-c:v", "png", "-f", "ico" }); break;
                default: // HEIC, TGA… → saved as PNG
                    if (f.Length > 0) a.AddRange(new[] { "-vf", f.TrimEnd(',') }); a.AddRange(new[] { "-c:v", "png" }); break;
            }
            a.AddRange(new[] { "-frames:v", "1", "-update", "1" });
            return a;
        }

        /// <summary>Output extension for an edited copy: formats ffmpeg can't write go to PNG.</summary>
        public static string EditableExtension(string input)
        {
            var ext = Formats.NormalizedExtension(input);
            return ext is "jpg" or "png" or "webp" or "avif" or "tiff" or "bmp" or "ico" ? ext : "png";
        }

        private static async Task<string> RunToNewFile(string ffmpeg, string input, string suffix, string ext, List<string> encode, CancellationToken ct)
        {
            var output = OutputNaming.UniquePath(input, ext, suffix);
            var temp = TempNear(output);
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
            args.AddRange(encode);
            args.Add(temp);
            try
            {
                await FfmpegRunner.RunAsync(ffmpeg, args, null, ct);
                return Commit(temp, output, input, ext, suffix);
            }
            catch { TryDelete(temp); throw; }
        }

        public static string TempNear(string output) =>
            Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + ".gajo-tmp" + Path.GetExtension(output));

        public static string Commit(string temp, string output, string input, string ext, string suffix)
        {
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new ConversionException("No se generó ningún archivo.");
            if (File.Exists(output)) output = OutputNaming.UniquePath(input, ext, suffix);
            File.Move(temp, output);
            return output;
        }

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        // ------------------------------------------------------------------ tools

        /// <summary>Crop in source pixels.</summary>
        public static Task<string> CropAsync(string ffmpeg, string input, int x, int y, int w, int h, CancellationToken ct = default)
        {
            var ext = EditableExtension(input);
            return RunToNewFile(ffmpeg, input, " (recortada)", ext, Encode($"crop={w}:{h}:{x}:{y}", ext, 95), ct);
        }

        public static Task<string> RotateAsync(string ffmpeg, string input, bool clockwise, CancellationToken ct = default)
        {
            var ext = EditableExtension(input);
            return RunToNewFile(ffmpeg, input, " (girada)", ext, Encode(clockwise ? "transpose=1" : "transpose=2", ext, 95), ct);
        }

        /// <summary>
        /// Smaller copy: at most 2560 px on the long side, lighter encoding, same format
        /// (PNG is reduced to an optimised 256-colour palette, which keeps transparency).
        /// </summary>
        public static async Task<string> CompressAsync(string ffmpeg, string input, CancellationToken ct = default)
        {
            var ext = EditableExtension(input);
            const string fit = "scale='if(gt(iw,ih),min(2560,iw),-2)':'if(gt(iw,ih),-2,min(2560,ih))':flags=lanczos";
            List<string> enc = ext == "png"
                ? new List<string> { "-filter_complex", $"[0:v]{fit},split[a][b];[a]palettegen=max_colors=256:reserve_transparent=1:stats_mode=full[p];[b][p]paletteuse=dither=sierra2_4a:alpha_threshold=128", "-c:v", "png", "-frames:v", "1", "-update", "1" }
                : Encode(fit, ext, 78);
            var output = await RunToNewFile(ffmpeg, input, " (comprimida)", ext, enc, ct);
            if (new FileInfo(output).Length >= new FileInfo(input).Length)
            {
                File.Delete(output);
                throw new ConversionException("La imagen ya estaba optimizada: la copia no pesaba menos.");
            }
            return output;
        }

        /// <summary>Removes EXIF (GPS, camera…), XMP and comments. JPEG and PNG are cleaned without re-compressing.</summary>
        public static async Task<string> StripMetadataAsync(string ffmpeg, string input, CancellationToken ct = default)
        {
            var ext = Formats.NormalizedExtension(input);
            if (ext is "jpg" or "png")
            {
                var output = OutputNaming.UniquePath(input, ext, " (sin metadatos)");
                var bytes = await File.ReadAllBytesAsync(input, ct);
                var clean = ext == "jpg" ? StripJpeg(bytes) : StripPng(bytes);
                await File.WriteAllBytesAsync(output, clean, ct);
                return output;
            }
            var e2 = EditableExtension(input);
            var enc = Encode("", e2, 95);
            enc.InsertRange(0, new[] { "-map_metadata", "-1" });
            return await RunToNewFile(ffmpeg, input, " (sin metadatos)", e2, enc, ct);
        }

        private static byte[] StripJpeg(byte[] d)
        {
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new ConversionException("El JPG no es válido.");
            var o = new MemoryStream();
            o.WriteByte(0xFF); o.WriteByte(0xD8);
            int i = 2;
            while (i + 4 <= d.Length)
            {
                if (d[i] != 0xFF) break;
                byte m = d[i + 1];
                if (m == 0xDA) break;                                  // start of scan: copy the rest
                int len = (d[i + 2] << 8) | d[i + 3];
                bool app1 = m == 0xE1;                                  // EXIF / XMP
                bool drop = app1 || m == 0xED || m == 0xFE || (m >= 0xE3 && m <= 0xEF && m != 0xEE);
                bool isIcc = m == 0xE2;                                  // colour profile: keep
                if (!drop || isIcc) o.Write(d, i, 2 + len);
                i += 2 + len;
            }
            o.Write(d, i, d.Length - i);
            return o.ToArray();
        }

        private static byte[] StripPng(byte[] d)
        {
            var o = new MemoryStream();
            o.Write(d, 0, 8);
            int i = 8;
            var drop = new HashSet<string> { "tEXt", "zTXt", "iTXt", "eXIf", "tIME" };
            while (i + 12 <= d.Length)
            {
                int len = (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
                var type = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
                int total = 12 + len;
                if (i + total > d.Length) break;
                if (!drop.Contains(type)) o.Write(d, i, total);
                i += total;
                if (type == "IEND") break;
            }
            return o.ToArray();
        }

        /// <summary>Decodes any supported image to a temporary PNG (for previews and WPF editing).</summary>
        public static async Task<string> DecodeToPngAsync(string ffmpeg, string input, CancellationToken ct = default)
        {
            var png = Path.Combine(Path.GetTempPath(), "gajo-prev-" + Guid.NewGuid().ToString("N") + ".png");
            await FfmpegRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-i", input, "-frames:v", "1", "-update", "1", "-c:v", "png", png }, null, ct);
            return png;
        }

        /// <summary>Several images → one PDF, one image per page, in the given order.</summary>
        public static async Task<string> ImagesToPdfAsync(string ffmpeg, IReadOnlyList<string> inputs, IProgress<double>? progress, CancellationToken ct = default)
        {
            var first = inputs[0];
            string suffix = inputs.Count > 1 ? $" ({inputs.Count} imágenes)" : "";
            var output = OutputNaming.UniquePath(first, "pdf", suffix);
            var jpgs = new List<string>();
            try
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var jpg = Path.Combine(Path.GetTempPath(), "gajo-" + Guid.NewGuid().ToString("N") + ".jpg");
                    await FfmpegRunner.RunAsync(ffmpeg, FfmpegArgs.BuildPdfJpeg(inputs[i], jpg), null, ct);
                    jpgs.Add(jpg);
                    progress?.Report((i + 1.0) / inputs.Count * 0.9);
                }
                var temp = TempNear(output);
                PdfWriter.WriteJpegsAsPdf(jpgs, temp);
                return Commit(temp, output, first, "pdf", suffix);
            }
            finally
            {
                foreach (var j in jpgs) TryDelete(j);
            }
        }
    }
}
