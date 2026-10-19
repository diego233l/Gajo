using System.Collections.Generic;

namespace Gajo.Core
{
    /// <summary>Encoder settings for every wheel target. Kept separate so it can be tested.</summary>
    public static class FfmpegArgs
    {
        private const string EvenDims = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        private const string FlattenOnWhite =
            "[0:v]format=rgba,split[a][b];[a]drawbox=c=white@1:replace=1:t=fill[bg];[bg][b]overlay=format=auto,format=rgb24";

        public static List<string> Build(string input, string output, FormatTarget target, MediaKind inputKind, int jpegQuality = 92, HwEncoder hw = HwEncoder.None)
        {
            var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };

            if (inputKind == MediaKind.Image)
            {
                AddImage(a, target.Id, jpegQuality);
            }
            else if (target.OutputKind == MediaKind.Audio)
            {
                // Audio from audio, or audio track extracted from video.
                a.AddRange(new[] { "-map", "0:a:0", "-vn", "-map_metadata", "0" });
                AddAudioCodec(a, target.Id);
            }
            else
            {
                AddVideo(a, target.Id, hw);
            }

            a.Add(output);
            return a;
        }

        /// <summary>JPEG frame used to build a PDF.</summary>
        public static List<string> BuildPdfJpeg(string input, string jpegOut) => new()
        {
            "-hide_banner", "-nostdin", "-y", "-i", input,
            "-filter_complex", FlattenOnWhite, "-frames:v", "1", "-update", "1", "-q:v", "2", jpegOut,
        };

        private static void AddImage(List<string> a, string id, int jpegQuality)
        {
            switch (id)
            {
                case "jpg":
                    // ffmpeg's -q:v scale is 2 (best) .. 31 (worst)
                    int q = jpegQuality >= 95 ? 2 : jpegQuality >= 90 ? 3 : jpegQuality >= 80 ? 5 : 8;
                    a.AddRange(new[] { "-filter_complex", FlattenOnWhite, "-q:v", q.ToString() });
                    break;
                case "png":
                    a.AddRange(new[] { "-c:v", "png" });
                    break;
                case "webp":
                    a.AddRange(new[] { "-c:v", "libwebp", "-quality", "90", "-compression_level", "5" });
                    break;
                case "avif":
                    a.AddRange(new[] { "-c:v", "libaom-av1", "-still-picture", "1", "-crf", "26", "-cpu-used", "6", "-pix_fmt", "yuv420p" });
                    break;
                case "tiff":
                    a.AddRange(new[] { "-c:v", "tiff", "-compression_algo", "deflate" });
                    break;
                case "bmp":
                    a.AddRange(new[] { "-c:v", "bmp" });
                    break;
                case "ico":
                    a.AddRange(new[] { "-vf", "scale='min(256,iw)':'min(256,ih)':force_original_aspect_ratio=decrease,format=rgba", "-c:v", "png", "-f", "ico" });
                    break;
            }
            a.AddRange(new[] { "-frames:v", "1", "-update", "1" });
        }

        private static void AddAudioCodec(List<string> a, string id)
        {
            switch (id)
            {
                case "mp3":  a.AddRange(new[] { "-c:a", "libmp3lame", "-q:a", "2" }); break;
                case "m4a":  a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" }); break;
                case "wav":  a.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
                case "flac": a.AddRange(new[] { "-c:a", "flac" }); break;
                case "ogg":  a.AddRange(new[] { "-c:a", "libvorbis", "-q:a", "5" }); break;
                case "opus": a.AddRange(new[] { "-c:a", "libopus", "-b:a", "128k" }); break;
                case "aiff": a.AddRange(new[] { "-c:a", "pcm_s16be" }); break;
                case "wma":  a.AddRange(new[] { "-c:a", "wmav2", "-b:a", "192k" }); break;
            }
        }

        private static void AddVideo(List<string> a, string id, HwEncoder hw)
        {
            if (id == "gif")
            {
                a.AddRange(new[]
                {
                    "-an",
                    "-filter_complex",
                    "[0:v:0]fps=15,scale='min(720,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5",
                    "-loop", "0",
                });
                return;
            }

            a.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-vf", EvenDims });
            switch (id)
            {
                case "mp4":
                case "mov":
                case "mkv":
                    a.AddRange(Hardware.VideoArgs(hw, crf: 20));
                    a.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
                    if (id != "mkv") a.AddRange(new[] { "-movflags", "+faststart" });
                    break;
                case "webm":
                    a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k" });
                    break;
                case "avi":
                    a.AddRange(new[] { "-c:v", "mpeg4", "-q:v", "3", "-vtag", "xvid", "-c:a", "libmp3lame", "-q:a", "3" });
                    break;
                case "wmv":
                    a.AddRange(new[] { "-c:v", "wmv2", "-b:v", "6M", "-c:a", "wmav2", "-b:a", "192k" });
                    break;
            }
        }
    }
}
