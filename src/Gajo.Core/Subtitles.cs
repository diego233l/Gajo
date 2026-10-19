using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Gajo.Core
{
    /// <summary>SRT ↔ VTT, and either one to plain text.</summary>
    public static class Subtitles
    {
        public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

        private static readonly Regex TimeLine = new(@"(?<a>(\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*(?<b>(\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})", RegexOptions.Compiled);

        public static List<Cue> Read(string path)
        {
            var text = Documents.ReadText(path).Replace("\r\n", "\n").Replace('\r', '\n');
            var cues = new List<Cue>();
            foreach (var block in Regex.Split(text, @"\n\s*\n"))
            {
                var lines = block.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
                int t = lines.FindIndex(l => TimeLine.IsMatch(l));
                if (t < 0) continue;
                var m = TimeLine.Match(lines[t]);
                var body = string.Join("\n", lines.Skip(t + 1));
                cues.Add(new Cue(Parse(m.Groups["a"].Value), Parse(m.Groups["b"].Value), body));
            }
            if (cues.Count == 0) throw new ConversionException("No se encontraron subtítulos en el archivo.");
            return cues;
        }

        private static TimeSpan Parse(string s)
        {
            var parts = s.Replace(',', '.').Split(':');
            double h = parts.Length == 3 ? double.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
            double m = double.Parse(parts[^2], CultureInfo.InvariantCulture);
            double sec = double.Parse(parts[^1], CultureInfo.InvariantCulture);
            return TimeSpan.FromSeconds(h * 3600 + m * 60 + sec);
        }

        private static string Fmt(TimeSpan t, char sep) =>
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{sep}{t.Milliseconds:000}";

        public static void Write(List<Cue> cues, string output, string targetId)
        {
            var sb = new StringBuilder();
            switch (targetId)
            {
                case "srt":
                    for (int i = 0; i < cues.Count; i++)
                    {
                        sb.Append(i + 1).Append('\n');
                        sb.Append(Fmt(cues[i].Start, ',')).Append(" --> ").Append(Fmt(cues[i].End, ',')).Append('\n');
                        sb.Append(Regex.Replace(cues[i].Text, "<(?!/?[ibu]>)[^>]+>", "")).Append("\n\n");   // SRT keeps only <i>, <b>, <u>
                    }
                    break;
                case "vtt":
                    sb.Append("WEBVTT\n\n");
                    foreach (var c in cues)
                        sb.Append(Fmt(c.Start, '.')).Append(" --> ").Append(Fmt(c.End, '.')).Append('\n').Append(c.Text).Append("\n\n");
                    break;
                case "txt":
                    foreach (var c in cues) sb.Append(Regex.Replace(c.Text, "<[^>]+>", "")).Append("\n");
                    break;
                default: throw new ConversionException("Formato de subtítulos no compatible.");
            }
            File.WriteAllText(output, sb.ToString().Replace("\n", "\r\n"), new UTF8Encoding(targetId == "srt"));
        }
    }
}
