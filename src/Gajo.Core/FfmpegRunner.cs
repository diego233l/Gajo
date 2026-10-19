using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Gajo.Core
{
    /// <summary>Runs ffmpeg with progress reporting, cancellation and friendly error messages.</summary>
    public static class FfmpegRunner
    {
        private static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

        public static async Task RunAsync(string ffmpeg, System.Collections.Generic.IEnumerable<string> args, IProgress<double>? progress, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };
            var log = new StringBuilder();
            double durationSec = 0;

            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (log) { if (log.Length < 200_000) log.AppendLine(e.Data); }
                if (durationSec <= 0)
                {
                    var m = DurationRx.Match(e.Data);
                    if (m.Success)
                        durationSec = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                                      + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                }
            };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null || progress == null || durationSec <= 0) return;
                // -progress lines: out_time_us=1234567  (older builds: out_time_ms, also in µs)
                if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms="))
                {
                    var v = e.Data.Substring(e.Data.IndexOf('=') + 1);
                    if (long.TryParse(v, out var us) && us > 0)
                        progress.Report(Math.Clamp(us / 1_000_000.0 / durationSec, 0, 0.99));
                }
            };

            try { p.Start(); }
            catch (Exception ex) { throw new ConversionException("No se pudo iniciar FFmpeg: " + ex.Message); }
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();

            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                await p.WaitForExitAsync(CancellationToken.None);

            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0)
            {
                string text;
                lock (log) text = log.ToString();
                throw new ConversionException(FriendlyError(text), text);
            }
        }

        public static string FriendlyError(string log)
        {
            if (log.Contains("matches no streams") || log.Contains("Output file does not contain any stream")
                || log.Contains("does not contain any stream"))
                return "El archivo no tiene una pista compatible (¿un vídeo sin audio?).";
            if (log.Contains("Unknown encoder") || log.Contains("Encoder not found"))
                return "Tu FFmpeg no incluye el codificador necesario. Descarga la versión completa desde Ajustes.";
            if (log.Contains("Invalid data found when processing input"))
                return "El archivo está dañado o su formato no es compatible.";
            if (log.Contains("Permission denied"))
                return "Sin permiso para escribir en esa carpeta.";

            var last = log.Split('\n').Select(l => l.Trim())
                          .LastOrDefault(l => l.Length > 0 && !l.StartsWith("Conversion failed") && !l.StartsWith("frame=")
                                              && !l.StartsWith("[out#") && !l.StartsWith("size="));
            return string.IsNullOrEmpty(last) ? "La conversión falló." : last!;
        }


        /// <summary>Runs a command and returns its standard output (for ffprobe-like queries with ffmpeg).</summary>
        public static async Task<(int Exit, byte[] Stdout, string Stderr)> CaptureAsync(string exe, System.Collections.Generic.IEnumerable<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = new Process { StartInfo = psi };
            p.Start();
            var ms = new System.IO.MemoryStream();
            var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            var errTask = p.StandardError.ReadToEndAsync();
            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
            {
                await p.WaitForExitAsync(CancellationToken.None);
                try { await outTask; } catch { }
            }
            ct.ThrowIfCancellationRequested();
            return (p.ExitCode, ms.ToArray(), await errTask);
        }
    }
}
