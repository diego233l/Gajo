using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Gajo.Core
{
    public enum HwEncoder { None, Nvenc, Qsv, Amf }

    /// <summary>
    /// Video encoding on the graphics card. Gajo first looks at which GPUs the PC has, then
    /// checks with a real 0.5 s test encode that the encoder actually works (driver present,
    /// card supported). It is only used where it helps — H.264 output — and any failure falls
    /// back to the CPU automatically.
    /// </summary>
    public static class Hardware
    {
        /// <summary>Names of the graphics cards, e.g. "NVIDIA GeForce RTX 3060".</summary>
        public static IReadOnlyList<string> Gpus { get; private set; } = Array.Empty<string>();

        /// <summary>Encoder that passed the test (None = CPU only).</summary>
        public static HwEncoder Available { get; private set; } = HwEncoder.None;

        /// <summary>User setting: allow using the GPU.</summary>
        public static bool Enabled { get; set; } = true;

        public static bool Probed { get; private set; }

        /// <summary>The encoder conversions should use right now.</summary>
        public static HwEncoder Active => Enabled ? Available : HwEncoder.None;

        public static string AvailableName => Available switch
        {
            HwEncoder.Nvenc => "NVIDIA NVENC",
            HwEncoder.Qsv => "Intel Quick Sync",
            HwEncoder.Amf => "AMD AMF",
            _ => "",
        };

        /// <param name="gpuNames">Detected card names (from the Windows registry); order of preference is decided here.</param>
        public static async Task ProbeAsync(string ffmpeg, IReadOnlyList<string> gpuNames)
        {
            Gpus = gpuNames;
            var candidates = new List<HwEncoder>();
            bool Has(string s) => gpuNames.Any(n => n.Contains(s, StringComparison.OrdinalIgnoreCase));
            // Dedicated cards first (laptops often have Intel + NVIDIA/AMD).
            if (Has("NVIDIA") || Has("GeForce") || Has("Quadro") || Has("RTX")) candidates.Add(HwEncoder.Nvenc);
            if (Has("AMD") || Has("Radeon")) candidates.Add(HwEncoder.Amf);
            if (Has("Intel")) candidates.Add(HwEncoder.Qsv);

            foreach (var enc in candidates)
            {
                if (await TestAsync(ffmpeg, enc))
                {
                    Available = enc;
                    break;
                }
            }
            Probed = true;
        }

        private static async Task<bool> TestAsync(string ffmpeg, HwEncoder enc)
        {
            try
            {
                var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=0.5" };
                args.AddRange(VideoArgs(enc, crf: 23));
                args.AddRange(new[] { "-f", "null", "-" });
                var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var exited = p.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(15000)) != exited)
                {
                    try { p.Kill(true); } catch { }
                    return false;
                }
                await Task.WhenAll(errTask, outTask);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>H.264 encoder arguments: constant quality (crf) or a target bitrate (kbps).</summary>
        public static string[] VideoArgs(HwEncoder enc, int crf = 20, int? kbps = null)
        {
            string? b = kbps.HasValue ? kbps.Value + "k" : null;
            string? max = kbps.HasValue ? (int)(kbps.Value * 1.3) + "k" : null;
            string? buf = kbps.HasValue ? kbps.Value * 2 + "k" : null;
            return enc switch
            {
                HwEncoder.Nvenc => b == null
                    ? new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", (crf + 1).ToString(), "-b:v", "0", "-pix_fmt", "yuv420p" }
                    : new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-b:v", b, "-maxrate", max!, "-bufsize", buf!, "-pix_fmt", "yuv420p" },
                HwEncoder.Qsv => b == null
                    ? new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", (crf + 2).ToString(), "-pix_fmt", "nv12" }
                    : new[] { "-c:v", "h264_qsv", "-preset", "medium", "-b:v", b, "-maxrate", max!, "-bufsize", buf!, "-pix_fmt", "nv12" },
                HwEncoder.Amf => b == null
                    ? new[] { "-c:v", "h264_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", crf.ToString(), "-qp_p", (crf + 2).ToString(), "-qp_b", (crf + 4).ToString(), "-pix_fmt", "yuv420p" }
                    : new[] { "-c:v", "h264_amf", "-quality", "quality", "-rc", "vbr_peak", "-b:v", b, "-maxrate", max!, "-bufsize", buf!, "-pix_fmt", "yuv420p" },
                _ => b == null
                    ? new[] { "-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(), "-pix_fmt", "yuv420p" }
                    : new[] { "-c:v", "libx264", "-preset", "medium", "-b:v", b, "-maxrate", max!, "-bufsize", buf!, "-pix_fmt", "yuv420p" },
            };
        }
    }
}
