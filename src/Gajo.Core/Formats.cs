using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Gajo.Core
{
    public enum MediaKind { Unknown, Image, Audio, Video, Document }

    /// <summary>A destination format shown as one segment ("gajo") of the wheel.</summary>
    public sealed record FormatTarget(string Id, string Label, string Extension, MediaKind OutputKind);

    public static class Formats
    {
        // ---- Inputs -------------------------------------------------------------
        private static readonly Dictionary<string, MediaKind> InputKinds = new(StringComparer.OrdinalIgnoreCase)
        {
            // Images
            [".jpg"] = MediaKind.Image, [".jpeg"] = MediaKind.Image, [".jfif"] = MediaKind.Image,
            [".png"] = MediaKind.Image, [".webp"] = MediaKind.Image, [".heic"] = MediaKind.Image,
            [".heif"] = MediaKind.Image, [".avif"] = MediaKind.Image, [".tif"] = MediaKind.Image,
            [".tiff"] = MediaKind.Image, [".bmp"] = MediaKind.Image, [".ico"] = MediaKind.Image,
            [".tga"] = MediaKind.Image,

            // Documents
            [".txt"] = MediaKind.Document, [".text"] = MediaKind.Document, [".md"] = MediaKind.Document,
            [".markdown"] = MediaKind.Document, [".docx"] = MediaKind.Document, [".html"] = MediaKind.Document,
            [".htm"] = MediaKind.Document, [".pdf"] = MediaKind.Document,

            // Audio
            [".mp3"] = MediaKind.Audio, [".m4a"] = MediaKind.Audio, [".aac"] = MediaKind.Audio,
            [".wav"] = MediaKind.Audio, [".flac"] = MediaKind.Audio, [".ogg"] = MediaKind.Audio,
            [".oga"] = MediaKind.Audio, [".opus"] = MediaKind.Audio, [".aiff"] = MediaKind.Audio,
            [".aif"] = MediaKind.Audio, [".wma"] = MediaKind.Audio, [".amr"] = MediaKind.Audio,

            // Video (GIF is treated as video, like in Tangerine)
            [".mp4"] = MediaKind.Video, [".m4v"] = MediaKind.Video, [".mov"] = MediaKind.Video,
            [".mkv"] = MediaKind.Video, [".webm"] = MediaKind.Video, [".avi"] = MediaKind.Video,
            [".wmv"] = MediaKind.Video, [".gif"] = MediaKind.Video, [".mpg"] = MediaKind.Video,
            [".mpeg"] = MediaKind.Video, [".flv"] = MediaKind.Video, [".3gp"] = MediaKind.Video,
            [".ts"] = MediaKind.Video, [".mts"] = MediaKind.Video, [".m2ts"] = MediaKind.Video,
        };

        // ---- Outputs (max 8 per kind = one full wheel) -------------------------
        public static readonly IReadOnlyList<FormatTarget> ImageTargets = new[]
        {
            new FormatTarget("jpg",  "JPG",  "jpg",  MediaKind.Image),
            new FormatTarget("png",  "PNG",  "png",  MediaKind.Image),
            new FormatTarget("webp", "WEBP", "webp", MediaKind.Image),
            new FormatTarget("avif", "AVIF", "avif", MediaKind.Image),
            new FormatTarget("tiff", "TIFF", "tiff", MediaKind.Image),
            new FormatTarget("bmp",  "BMP",  "bmp",  MediaKind.Image),
            new FormatTarget("ico",  "ICO",  "ico",  MediaKind.Image),
            new FormatTarget("pdf",  "PDF",  "pdf",  MediaKind.Image),
        };

        public static readonly IReadOnlyList<FormatTarget> AudioTargets = new[]
        {
            new FormatTarget("mp3",  "MP3",  "mp3",  MediaKind.Audio),
            new FormatTarget("m4a",  "M4A",  "m4a",  MediaKind.Audio),
            new FormatTarget("wav",  "WAV",  "wav",  MediaKind.Audio),
            new FormatTarget("flac", "FLAC", "flac", MediaKind.Audio),
            new FormatTarget("ogg",  "OGG",  "ogg",  MediaKind.Audio),
            new FormatTarget("opus", "OPUS", "opus", MediaKind.Audio),
            new FormatTarget("aiff", "AIFF", "aiff", MediaKind.Audio),
            new FormatTarget("wma",  "WMA",  "wma",  MediaKind.Audio),
        };

        public static readonly IReadOnlyList<FormatTarget> VideoTargets = new[]
        {
            new FormatTarget("mp4",  "MP4",  "mp4",  MediaKind.Video),
            new FormatTarget("mov",  "MOV",  "mov",  MediaKind.Video),
            new FormatTarget("mkv",  "MKV",  "mkv",  MediaKind.Video),
            new FormatTarget("webm", "WEBM", "webm", MediaKind.Video),
            new FormatTarget("avi",  "AVI",  "avi",  MediaKind.Video),
            new FormatTarget("wmv",  "WMV",  "wmv",  MediaKind.Video),
            new FormatTarget("gif",  "GIF",  "gif",  MediaKind.Video),
            new FormatTarget("mp3",  "MP3",  "mp3",  MediaKind.Audio),
        };

        public static readonly IReadOnlyList<FormatTarget> DocumentTargets = new[]
        {
            new FormatTarget("txt",  "TXT",  "txt",  MediaKind.Document),
            new FormatTarget("md",   "MD",   "md",   MediaKind.Document),
            new FormatTarget("docx", "DOCX", "docx", MediaKind.Document),
            new FormatTarget("pdf",  "PDF",  "pdf",  MediaKind.Document),
            new FormatTarget("html", "HTML", "html", MediaKind.Document),
            new FormatTarget("rtf",  "RTF",  "rtf",  MediaKind.Document),
            new FormatTarget("epub", "EPUB", "epub", MediaKind.Document),
        };

        public static readonly IReadOnlyList<FormatTarget> PdfImageTargets = new[]
        {
            new FormatTarget("jpg", "JPG", "jpg", MediaKind.Image),
            new FormatTarget("png", "PNG", "png", MediaKind.Image),
        };

        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jpeg"] = "jpg", ["jfif"] = "jpg", ["tif"] = "tiff", ["aif"] = "aiff", ["oga"] = "ogg",
            ["heif"] = "heic", ["m4v"] = "mp4", ["mpeg"] = "mpg",
            ["markdown"] = "md", ["htm"] = "html", ["text"] = "txt",
        };

        public static MediaKind KindOf(string path)
        {
            var ext = Path.GetExtension(path);
            return InputKinds.TryGetValue(ext, out var k) ? k : MediaKind.Unknown;
        }

        /// <summary>Normalised extension without dot (jpeg → jpg, tif → tiff…).</summary>
        public static string NormalizedExtension(string path)
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return Aliases.TryGetValue(ext, out var a) ? a : ext;
        }

        public static IReadOnlyList<FormatTarget> TargetsForKind(MediaKind kind) => kind switch
        {
            MediaKind.Image => ImageTargets,
            MediaKind.Audio => AudioTargets,
            MediaKind.Video => VideoTargets,
            MediaKind.Document => DocumentTargets,
            _ => Array.Empty<FormatTarget>(),
        };

        /// <summary>
        /// Wheel options for a set of dragged files. Only files of the dominant kind are
        /// considered; if every one of them already has a given extension, that target is hidden.
        /// </summary>
        public static WheelPlan PlanFor(IEnumerable<string> paths)
        {
            var files = paths.Where(p => !string.IsNullOrWhiteSpace(p) && !Directory.Exists(p)).ToList();
            var groups = files.GroupBy(KindOf).Where(g => g.Key != MediaKind.Unknown)
                              .OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0)
                return new WheelPlan(MediaKind.Unknown, Array.Empty<string>(), Array.Empty<FormatTarget>(), files.Count);

            var main = groups[0];
            var inputs = main.ToList();
            var sourceExts = inputs.Select(NormalizedExtension).Distinct().ToList();
            var targets = TargetsForKind(main.Key)
                .Where(t => !(sourceExts.Count == 1 && sourceExts[0] == t.Extension && t.OutputKind == main.Key))
                .ToList();
            // An animated GIF has no audio track to extract.
            if (sourceExts.Count == 1 && sourceExts[0] == "gif")
                targets.RemoveAll(t => t.OutputKind == MediaKind.Audio);
            // From a PDF the usual wish is Word: put it at the top of the wheel.

            // From a PDF the usual wish is Word: put it at the top of the wheel.
            if (sourceExts.Count == 1 && sourceExts[0] == "pdf")
            {
                targets = targets.OrderBy(t => t.Id == "docx" ? 0 : 1).ToList();
                targets.AddRange(PdfImageTargets);   // every page as an image
            }
            return new WheelPlan(main.Key, inputs, targets, files.Count - inputs.Count);
        }
    }

    public sealed record WheelPlan(MediaKind Kind, IReadOnlyList<string> Inputs, IReadOnlyList<FormatTarget> Targets, int SkippedCount)
    {
        public bool IsEmpty => Inputs.Count == 0 || Targets.Count == 0;
    }
}
