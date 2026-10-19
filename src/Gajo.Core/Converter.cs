using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Gajo.Core
{
    public sealed class ConversionException : Exception
    {
        public ConversionException(string message, string? log = null) : base(message) { Log = log; }
        public string? Log { get; }
    }

    /// <summary>Runs one conversion through ffmpeg. Never touches the original file.</summary>
    public sealed class Converter
    {
        private static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

        private readonly string _ffmpeg;
        private readonly int _jpegQuality;

        public Converter(string ffmpegPath, int jpegQuality = 92)
        {
            _ffmpeg = ffmpegPath;
            _jpegQuality = jpegQuality;
        }

        /// <returns>Full path of the new file.</returns>
        public async Task<string> ConvertAsync(string input, FormatTarget target, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (!File.Exists(input)) throw new ConversionException("El archivo ya no existe.");
            var kind = Formats.KindOf(input);

            // PDF pages → images: one file, or a folder for several pages.
            if (kind == MediaKind.Document && Formats.NormalizedExtension(input) == "pdf" && target.OutputKind == MediaKind.Image)
            {
                var renderer = PdfToDocx.Renderer ?? throw new ConversionException("No hay motor para dibujar páginas de PDF.");
                return await PdfImages.ExportAsync(_ffmpeg, input, target.Extension, renderer, progress, ct);
            }

            var output = OutputNaming.UniquePath(input, target.Extension);
            // Write to a temp name first so a half-written file never looks finished.
            var temp = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + ".gajo-tmp." + target.Extension);

            try
            {
                if (kind == MediaKind.Document)
                {
                    if (Formats.NormalizedExtension(input) == "pdf" && target.Id == "docx")
                    {
                        await PdfToDocx.ConvertAsync(input, temp, progress, ct);
                    }
                    else if (Formats.NormalizedExtension(input) == "pdf")
                    {
                        var pdf = await PdfText.ReadAsync(input, progress, ct);
                        await Task.Run(() => Documents.Write(pdf.Blocks, temp, target.Id, Path.GetFileNameWithoutExtension(input)), ct);
                    }
                    else await Task.Run(() =>
                    {
                        try { Documents.Convert(input, temp, target.Id); }
                        catch (ConversionException) { throw; }
                        catch (Exception ex) { throw new ConversionException("No se pudo leer el documento: " + ex.Message, ex.ToString()); }
                    }, ct);
                }
                else if (kind == MediaKind.Image && target.Id == "pdf")
                {
                    var jpg = Path.Combine(Path.GetTempPath(), "gajo-" + Guid.NewGuid().ToString("N") + ".jpg");
                    try
                    {
                        await RunAsync(FfmpegArgs.BuildPdfJpeg(input, jpg), null, ct);
                        progress?.Report(0.8);
                        PdfWriter.WriteJpegAsPdf(jpg, temp);
                    }
                    finally { TryDelete(jpg); }
                }
                else
                {
                    var hw = kind == MediaKind.Video ? Hardware.Active : HwEncoder.None;
                    var args = FfmpegArgs.Build(input, temp, target, kind, _jpegQuality, hw);
                    if (kind == MediaKind.Video || kind == MediaKind.Audio)
                        args.InsertRange(args.Count - 1, new[] { "-progress", "pipe:1", "-nostats" });
                    try
                    {
                        await RunAsync(args, kind == MediaKind.Image ? null : progress, ct);
                    }
                    catch (ConversionException) when (hw != HwEncoder.None && !ct.IsCancellationRequested)
                    {
                        // The graphics card refused this video (size, format, driver…): do it on the CPU.
                        TryDelete(temp);
                        var cpu = FfmpegArgs.Build(input, temp, target, kind, _jpegQuality, HwEncoder.None);
                        cpu.InsertRange(cpu.Count - 1, new[] { "-progress", "pipe:1", "-nostats" });
                        await RunAsync(cpu, progress, ct);
                    }
                }

                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                    throw new ConversionException("FFmpeg no generó ningún archivo.");

                // Destination might have appeared meanwhile.
                if (File.Exists(output)) output = OutputNaming.UniquePath(input, target.Extension);
                File.Move(temp, output);
                progress?.Report(1.0);
                return output;
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        private Task RunAsync(System.Collections.Generic.IEnumerable<string> args, IProgress<double>? progress, CancellationToken ct) =>
            FfmpegRunner.RunAsync(_ffmpeg, args, progress, ct);

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
