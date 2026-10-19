using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Gajo.Core
{
    /// <summary>Audio and video tools of the Ctrl wheel. Every tool writes a new file next to the original.</summary>
    public static class MediaTools
    {
        public sealed record MediaInfo(double Duration, bool HasAudio, bool HasVideo, int Width, int Height, int Channels, long Bytes);

        private static string S(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ probing

        public static async Task<MediaInfo> ProbeAsync(string ffmpeg, string file, CancellationToken ct = default)
        {
            var (_, _, err) = await FfmpegRunner.CaptureAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-i", file }, ct);
            double dur = 0;
            var m = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (m.Success) dur = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            bool audio = Regex.IsMatch(err, @"Stream #\d+:\d+.*?: Audio:");
            // Cover art in audio files is an "attached pic" video stream: not a real video.
            var vlines = err.Split('\n').Where(l => Regex.IsMatch(l, @"Stream #\d+:\d+.*?: Video:") && !l.Contains("attached pic") && !l.Contains("(attached pic)")).ToList();
            int w = 0, h = 0;
            if (vlines.Count > 0)
            {
                var sz = Regex.Match(vlines[0], @"(\d{2,5})x(\d{2,5})");
                if (sz.Success) { w = int.Parse(sz.Groups[1].Value); h = int.Parse(sz.Groups[2].Value); }
            }
            int ch = 2;
            var am = Regex.Match(err, @"Audio:.*?, (mono|stereo|5\.1|7\.1|(\d+) channels)");
            if (am.Success) ch = am.Groups[1].Value == "mono" ? 1 : am.Groups[1].Value == "stereo" ? 2 : am.Groups[2].Success ? int.Parse(am.Groups[2].Value) : 6;
            long bytes = File.Exists(file) ? new FileInfo(file).Length : 0;
            return new MediaInfo(dur, audio, vlines.Count > 0, w, h, ch, bytes);
        }

        /// <summary>Loudness peaks for drawing a waveform (0..1), <paramref name="buckets"/> values.</summary>
        public static async Task<float[]> PeaksAsync(string ffmpeg, string file, int buckets, CancellationToken ct = default)
        {
            const int rate = 4000;
            var (code, pcm, _) = await FfmpegRunner.CaptureAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", file, "-vn", "-ac", "1", "-ar", rate.ToString(), "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1" }, ct);
            var peaks = new float[buckets];
            int samples = pcm.Length / 2;
            if (code != 0 || samples == 0) return peaks;
            for (int b = 0; b < buckets; b++)
            {
                int from = (int)((long)samples * b / buckets), to = (int)((long)samples * (b + 1) / buckets);
                int max = 0;
                for (int i = from; i < to; i++)
                {
                    int v = Math.Abs((short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)));
                    if (v > max) max = v;
                }
                peaks[b] = max / 32768f;
            }
            float top = Math.Max(0.05f, peaks.Max());
            for (int b = 0; b < buckets; b++) peaks[b] /= top;
            return peaks;
        }

        /// <summary>One frame as JPEG bytes (video previews).</summary>
        public static async Task<byte[]?> FrameAsync(string ffmpeg, string file, double at, int width, CancellationToken ct = default)
        {
            var (code, jpg, _) = await FfmpegRunner.CaptureAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-v", "error", "-ss", S(Math.Max(0, at)), "-i", file, "-frames:v", "1", "-vf", $"scale={width}:-2", "-f", "image2", "-c:v", "mjpeg", "-q:v", "4", "pipe:1" }, ct);
            return code == 0 && jpg.Length > 0 ? jpg : null;
        }

        /// <summary>Strip of <paramref name="count"/> thumbnails across the video, as one JPEG.</summary>
        public static async Task<byte[]?> ThumbStripAsync(string ffmpeg, string file, double duration, int count, int height, CancellationToken ct = default)
        {
            if (duration <= 0) return null;
            var (code, jpg, _) = await FfmpegRunner.CaptureAsync(ffmpeg, new[]
            {
                "-hide_banner", "-nostdin", "-v", "error", "-i", file, "-an",
                "-vf", $"fps={S(count / duration)},scale=-2:{height},tile={count}x1", "-frames:v", "1", "-f", "image2", "-c:v", "mjpeg", "-q:v", "5", "pipe:1",
            }, ct);
            return code == 0 && jpg.Length > 0 ? jpg : null;
        }

        /// <summary>Temporary WAV for playback in the trim window.</summary>
        public static async Task<string> PreviewWavAsync(string ffmpeg, string file, CancellationToken ct = default)
        {
            var wav = Path.Combine(Path.GetTempPath(), "gajo-play-" + Guid.NewGuid().ToString("N") + ".wav");
            await FfmpegRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-i", file, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", wav }, null, ct);
            return wav;
        }

        // ------------------------------------------------------------------ helpers

        private static FormatTarget SameFormat(string file)
        {
            var ext = Formats.NormalizedExtension(file);
            var kind = Formats.KindOf(file);
            var t = Formats.TargetsForKind(kind).FirstOrDefault(x => x.Extension == ext && x.OutputKind == kind);
            if (t != null) return t;
            // Inputs we read but don't write (aac, amr, mpg, flv…): write the closest common format.
            return kind == MediaKind.Audio ? Formats.AudioTargets.First(x => x.Id == "m4a") : Formats.VideoTargets.First(x => x.Id == "mp4");
        }

        /// <summary>Re-encodes <paramref name="file"/> to its own format with extra options before the output, and optional input seeking.</summary>
        private static async Task<string> ReencodeAsync(string ffmpeg, string file, string suffix, IReadOnlyList<string> extra, IProgress<double>? progress, CancellationToken ct,
                                                        double? start = null, double? duration = null, FormatTarget? target = null, bool dropVideoFilter = false)
        {
            target ??= SameFormat(file);
            var kind = Formats.KindOf(file);
            var output = OutputNaming.UniquePath(file, target.Extension, suffix);
            var temp = ImageTools.TempNear(output);
            var hw = kind == MediaKind.Video ? Hardware.Active : HwEncoder.None;

            List<string> Build(HwEncoder enc)
            {
                var a = FfmpegArgs.Build(file, temp, target, kind, 92, enc);
                int i = a.IndexOf("-i");
                if (start.HasValue) a.InsertRange(i, new[] { "-ss", S(start.Value) });
                if (duration.HasValue) a.InsertRange(a.IndexOf("-i") + 2, new[] { "-t", S(duration.Value) });
                if (dropVideoFilter)
                {
                    int vf = a.IndexOf("-vf");
                    if (vf >= 0) a.RemoveRange(vf, 2);
                }
                a.InsertRange(a.Count - 1, extra);
                a.InsertRange(a.Count - 1, new[] { "-progress", "pipe:1", "-nostats" });
                return a;
            }

            try
            {
                try { await FfmpegRunner.RunAsync(ffmpeg, Build(hw), progress, ct); }
                catch (ConversionException) when (hw != HwEncoder.None && !ct.IsCancellationRequested)
                {
                    try { File.Delete(temp); } catch { }
                    await FfmpegRunner.RunAsync(ffmpeg, Build(HwEncoder.None), progress, ct);
                }
                return ImageTools.Commit(temp, output, file, target.Extension, suffix);
            }
            catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } throw; }
        }

        // ------------------------------------------------------------------ tools

        /// <summary>Keeps [start, end] (seconds). Re-encodes for an exact cut.</summary>
        public static Task<string> TrimAsync(string ffmpeg, string file, double start, double end, IProgress<double>? progress, CancellationToken ct = default) =>
            ReencodeAsync(ffmpeg, file, Formats.KindOf(file) == MediaKind.Video ? " (recortado)" : " (recortado)", Array.Empty<string>(), progress, ct, start, Math.Max(0.05, end - start));

        /// <summary>Removes silence at the beginning and end, keeping pauses inside the recording.</summary>
        public static Task<string> TrimSilenceAsync(string ffmpeg, string file, IProgress<double>? progress, CancellationToken ct = default) =>
            ReencodeAsync(ffmpeg, file, " (sin silencios)", new[]
            {
                "-af", "silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.1,areverse,silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.1,areverse",
            }, progress, ct);

        /// <summary>Evens out the volume (EBU R128 loudness, −16 LUFS, like podcasts and streaming).</summary>
        public static Task<string> NormalizeAsync(string ffmpeg, string file, IProgress<double>? progress, CancellationToken ct = default) =>
            ReencodeAsync(ffmpeg, file, " (normalizado)", new[] { "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "48000" }, progress, ct);

        public static Task<string> ChannelsAsync(string ffmpeg, string file, int channels, IProgress<double>? progress, CancellationToken ct = default) =>
            ReencodeAsync(ffmpeg, file, channels == 1 ? " (mono)" : " (estéreo)", new[] { "-ac", channels.ToString() }, progress, ct);

        /// <summary>Smaller audio: lossy formats at a lower bitrate; lossless (WAV/FLAC/AIFF) become M4A.</summary>
        public static async Task<string> CompressAudioAsync(string ffmpeg, string file, IProgress<double>? progress, CancellationToken ct = default)
        {
            var ext = Formats.NormalizedExtension(file);
            FormatTarget target = ext is "wav" or "flac" or "aiff" ? Formats.AudioTargets.First(t => t.Id == "m4a") : SameFormat(file);
            string[] codec = target.Id switch
            {
                "mp3" => new[] { "-c:a", "libmp3lame", "-b:a", "96k" },
                "m4a" => new[] { "-c:a", "aac", "-b:a", ext is "wav" or "flac" or "aiff" ? "128k" : "96k" },
                "ogg" => new[] { "-c:a", "libvorbis", "-q:a", "2" },
                "opus" => new[] { "-c:a", "libopus", "-b:a", "48k" },
                "wma" => new[] { "-c:a", "wmav2", "-b:a", "96k" },
                _ => new[] { "-c:a", "aac", "-b:a", "128k" },
            };
            var output = await ReencodeAsync(ffmpeg, file, " (comprimido)", codec, progress, ct, target: target);
            if (new FileInfo(output).Length >= new FileInfo(file).Length)
            {
                File.Delete(output);
                throw new ConversionException("El audio ya estaba comprimido: la copia no pesaba menos.");
            }
            return output;
        }

        /// <summary>Video without its sound track (no re-encoding: instant and lossless).</summary>
        public static async Task<string> MuteAsync(string ffmpeg, string file, CancellationToken ct = default)
        {
            var ext = Path.GetExtension(file).TrimStart('.');
            var output = OutputNaming.UniquePath(file, ext, " (sin audio)");
            var temp = ImageTools.TempNear(output);
            try
            {
                await FfmpegRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-i", file, "-map", "0:v", "-c", "copy", "-an", temp }, null, ct);
                return ImageTools.Commit(temp, output, file, ext, " (sin audio)");
            }
            catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } throw; }
        }

        public static Task<string> RotateVideoAsync(string ffmpeg, string file, IProgress<double>? progress, CancellationToken ct = default) =>
            ReencodeAsync(ffmpeg, file, " (girado)", new[] { "-vf", "transpose=1,scale=trunc(iw/2)*2:trunc(ih/2)*2" }, progress, ct, dropVideoFilter: true);

        /// <summary>Plan for "compress to size": bitrates and resolution that fit the target.</summary>
        public sealed record SizePlan(int VideoKbps, int AudioKbps, int MaxHeight, bool TooSmall);

        public static SizePlan PlanForSize(MediaInfo info, double targetMB)
        {
            double seconds = Math.Max(1, info.Duration);
            double totalKbps = targetMB * 8192 * 0.96 / seconds;      // 4 % for the container
            int audio = info.HasAudio ? (int)Math.Clamp(totalKbps * 0.12, 32, 128) : 0;
            int video = (int)(totalKbps - audio);
            int h = info.Height > 0 ? info.Height : 1080;
            int maxH = video < 250 ? 360 : video < 500 ? 480 : video < 1200 ? 720 : video < 3500 ? 1080 : h;
            return new SizePlan(Math.Max(video, 60), audio, Math.Min(h, maxH), video < 120);
        }

        /// <summary>MP4 that weighs at most <paramref name="targetMB"/> (H.264 + AAC, graphics card if available).</summary>
        public static async Task<string> CompressVideoToSizeAsync(string ffmpeg, string file, double targetMB, IProgress<double>? progress, CancellationToken ct = default)
        {
            var info = await ProbeAsync(ffmpeg, file, ct);
            if (info.Duration <= 0) throw new ConversionException("No se pudo leer la duración del vídeo.");
            if (info.Bytes > 0 && info.Bytes <= targetMB * 1024 * 1024)
                throw new ConversionException($"El vídeo ya ocupa menos de {targetMB:0.#} MB.");
            var plan = PlanForSize(info, targetMB);
            var output = OutputNaming.UniquePath(file, "mp4", " (comprimido)");
            var temp = ImageTools.TempNear(output);
            string scale = $"scale=-2:'min({plan.MaxHeight},ih)',scale=trunc(iw/2)*2:trunc(ih/2)*2";
            double limit = targetMB * 1024 * 1024;

            async Task Encode(int videoKbps, HwEncoder hw)
            {
                var common = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", file, "-map", "0:v:0", "-map", "0:a:0?", "-vf", scale };
                if (hw == HwEncoder.None)
                {
                    // Two passes on the CPU: the size comes out right first time.
                    var log = Path.Combine(Path.GetTempPath(), "gajo-2pass-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        var p1 = new List<string>(common);
                        p1.AddRange(Hardware.VideoArgs(HwEncoder.None, kbps: videoKbps));
                        p1.AddRange(new[] { "-pass", "1", "-passlogfile", log, "-an", "-progress", "pipe:1", "-nostats", "-f", "mp4", OperatingSystem.IsWindows() ? "NUL" : "/dev/null" });
                        await FfmpegRunner.RunAsync(ffmpeg, p1, progress == null ? null : new Progress<double>(v => progress.Report(v * 0.45)), ct);
                        var p2 = new List<string>(common);
                        p2.AddRange(Hardware.VideoArgs(HwEncoder.None, kbps: videoKbps));
                        p2.AddRange(new[] { "-pass", "2", "-passlogfile", log });
                        if (info.HasAudio) p2.AddRange(new[] { "-c:a", "aac", "-b:a", plan.AudioKbps + "k" });
                        p2.AddRange(new[] { "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", temp });
                        await FfmpegRunner.RunAsync(ffmpeg, p2, progress == null ? null : new Progress<double>(v => progress.Report(0.45 + v * 0.55)), ct);
                    }
                    finally
                    {
                        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(log) + "*")) try { File.Delete(f); } catch { }
                    }
                }
                else
                {
                    var a = new List<string>(common);
                    a.AddRange(Hardware.VideoArgs(hw, kbps: videoKbps));
                    if (info.HasAudio) a.AddRange(new[] { "-c:a", "aac", "-b:a", plan.AudioKbps + "k" });
                    a.AddRange(new[] { "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", temp });
                    await FfmpegRunner.RunAsync(ffmpeg, a, progress, ct);
                }
            }

            try
            {
                var hw = Hardware.Active;
                try { await Encode(plan.VideoKbps, hw); }
                catch (ConversionException) when (hw != HwEncoder.None && !ct.IsCancellationRequested) { hw = HwEncoder.None; await Encode(plan.VideoKbps, hw); }

                // Graphics cards aim less precisely: if it came out too big, once more with a lower bitrate.
                long size = new FileInfo(temp).Length;
                if (size > limit)
                {
                    int retry = (int)(plan.VideoKbps * (limit / size) * 0.92);
                    await Encode(Math.Max(50, retry), hw);
                }
                return ImageTools.Commit(temp, output, file, "mp4", " (comprimido)");
            }
            catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } throw; }
        }
    }
}
