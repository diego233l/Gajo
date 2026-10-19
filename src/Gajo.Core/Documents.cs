using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Gajo.Core
{
    /// <summary>
    /// Plain-text documents without external libraries. Every input is reduced to a list of
    /// blocks (headings and paragraphs) and written out in the chosen format.
    /// </summary>
    public static class Documents
    {
        /// <summary>Level 0 = paragraph, 1–6 = heading, -1 = page break. Empty text = blank line.</summary>
        public sealed record Block(int Level, string Text);

        static Documents()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static void Convert(string input, string output, string targetId) =>
            Write(Read(input), output, targetId, Path.GetFileNameWithoutExtension(input));

        public static void Write(List<Block> blocks, string output, string targetId, string title)
        {
            switch (targetId)
            {
                case "txt": File.WriteAllText(output, ToText(blocks), new UTF8Encoding(false)); break;
                case "md": File.WriteAllText(output, ToMarkdown(blocks), new UTF8Encoding(false)); break;
                case "html": File.WriteAllText(output, ToHtml(blocks, title), new UTF8Encoding(false)); break;
                case "rtf": File.WriteAllText(output, ToRtf(blocks), Encoding.ASCII); break;
                case "docx": WriteDocx(blocks, output); break;
                case "epub": WriteEpub(blocks, title, output); break;
                case "pdf": TextPdf.Write(blocks, output); break;
                default: throw new ConversionException("Formato de documento no compatible.");
            }
        }

        // ================================================================== readers

        public static List<Block> Read(string path)
        {
            return Formats.NormalizedExtension(path) switch
            {
                "md" => FromMarkdown(ReadText(path)),
                "html" => FromHtml(ReadText(path)),
                "docx" => FromDocx(path),
                "odt" => FromOdt(path),
                "rtf" => FromRtf(File.ReadAllBytes(path)),
                _ => FromText(ReadText(path)),
            };
        }

        /// <summary>UTF-8 (with or without BOM), UTF-16 with BOM, or legacy Windows-1252 ("ANSI").</summary>
        public static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
                return new StreamReader(new MemoryStream(bytes), true).ReadToEnd();
            try
            {
                var s = new UTF8Encoding(false, true).GetString(bytes);
                return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
        }

        private static IEnumerable<string> Lines(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static List<Block> FromText(string text) =>
            Lines(text).Select(l => new Block(0, l.TrimEnd())).ToList();

        private static readonly Regex MdHeading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
        private static readonly Regex MdInline = new(@"(\*\*|__|\*|_|`)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled);
        private static readonly Regex MdLink = new(@"!?\[([^\]]*)\]\(([^)]*)\)", RegexOptions.Compiled);

        private static List<Block> FromMarkdown(string text)
        {
            var list = new List<Block>();
            foreach (var raw in Lines(text))
            {
                var line = raw.TrimEnd();
                var m = MdHeading.Match(line);
                if (m.Success) { list.Add(new Block(m.Groups[1].Length, StripInline(m.Groups[2].Value))); continue; }
                if (Regex.IsMatch(line, @"^\s*(```|~~~)")) continue;             // code fences
                if (Regex.IsMatch(line, @"^\s*([-*_]\s*){3,}$")) { list.Add(new Block(0, "")); continue; } // rules
                line = Regex.Replace(line, @"^\s*>\s?", "");                       // quotes
                line = Regex.Replace(line, @"^(\s*)[-*+]\s+", "$1• ");             // bullets
                list.Add(new Block(0, StripInline(line)));
            }
            return list;
        }

        private static string StripInline(string s)
        {
            s = MdLink.Replace(s, m => m.Groups[1].Value);
            for (int i = 0; i < 3; i++) s = MdInline.Replace(s, "$2");
            return s;
        }

        private static List<Block> FromHtml(string html)
        {
            html = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"\s+", " ");
            html = Regex.Replace(html, @"<h([1-6])[^>]*>(.*?)</h\1>", m => $"\n\u0001{m.Groups[1].Value}{m.Groups[2].Value}\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</?(p|div|tr|ul|ol|table|section|article|blockquote|pre)[^>]*>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", "");
            var list = new List<Block>();
            foreach (var raw in Lines(WebUtility.HtmlDecode(html)))
            {
                var line = raw.Trim();
                if (line.Length > 1 && line[0] == '\u0001')
                    list.Add(new Block(line[1] - '0', line[2..].Trim()));
                else if (line.Length > 0)
                    list.Add(new Block(0, line));
            }
            return SpaceParagraphs(list);
        }

        private static List<Block> FromDocx(string path)
        {
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml") ?? throw new ConversionException("El DOCX no tiene contenido legible.");
            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s);

            var list = new List<Block>();
            foreach (var p in doc.Descendants(w + "p"))
            {
                var sb = new StringBuilder();
                foreach (var node in p.Descendants())
                {
                    if (node.Name == w + "t") sb.Append(node.Value);
                    else if (node.Name == w + "tab") sb.Append('\t');
                    else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
                }
                var style = (string?)p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val") ?? "";
                int level = 0;
                var hm = Regex.Match(style, @"^(Heading|Ttulo|Titulo|Título|berschrift)\s*(\d)", RegexOptions.IgnoreCase);
                if (hm.Success) level = int.Parse(hm.Groups[2].Value);
                else if (style.Equals("Title", StringComparison.OrdinalIgnoreCase) || style.Equals("Ttulo", StringComparison.OrdinalIgnoreCase)) level = 1;
                bool isList = p.Element(w + "pPr")?.Element(w + "numPr") != null;

                var lines = sb.ToString().Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    list.Add(new Block(level, (isList && i == 0 ? "• " : "") + lines[i]));
            }
            return SpaceParagraphs(list);
        }

        private static List<Block> FromOdt(string path)
        {
            XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("content.xml") ?? throw new ConversionException("El ODT no tiene contenido legible.");
            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s);
            var list = new List<Block>();
            string Inner(XElement e)
            {
                var sb = new StringBuilder();
                foreach (var n in e.Nodes())
                {
                    if (n is XText t) sb.Append(t.Value);
                    else if (n is XElement x)
                    {
                        if (x.Name == text + "s") sb.Append(' ', (int?)x.Attribute(text + "c") ?? 1);
                        else if (x.Name == text + "tab") sb.Append('\t');
                        else if (x.Name == text + "line-break") sb.Append('\n');
                        else if (x.Name != text + "note") sb.Append(Inner(x));
                    }
                }
                return sb.ToString();
            }
            foreach (var e in doc.Descendants().Where(e => e.Name == text + "h" || e.Name == text + "p"))
            {
                if (e.Ancestors(text + "note").Any()) continue;
                bool inList = e.Ancestors(text + "list-item").Any();
                int level = e.Name == text + "h" ? Math.Clamp((int?)e.Attribute(text + "outline-level") ?? 1, 1, 6) : 0;
                foreach (var line in Inner(e).Split('\n'))
                    list.Add(new Block(level, (inList && level == 0 ? "• " : "") + line));
            }
            return SpaceParagraphs(list);
        }

        /// <summary>Plain text of an RTF document (formatting is dropped; accents and Unicode kept).</summary>
        public static List<Block> FromRtf(byte[] data)
        {
            var enc = Encoding.GetEncoding(1252);
            var sb = new StringBuilder();
            var skip = new Stack<bool>();
            bool skipping = false;
            int ucSkip = 1, pendingSkip = 0;
            string[] destinations = { "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "footer", "headerl", "headerr", "footerl", "footerr", "object", "themedata", "colorschememapping", "datastore", "latentstyles", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "mmathPr" };
            int i = 0;
            while (i < data.Length)
            {
                char c = (char)data[i];
                if (c == '{') { skip.Push(skipping); i++; continue; }
                if (c == '}') { skipping = skip.Count > 0 && skip.Pop(); i++; continue; }
                if (c == '\\')
                {
                    i++;
                    if (i >= data.Length) break;
                    char n = (char)data[i];
                    if (n == '\'')
                    {
                        if (i + 2 < data.Length && int.TryParse(enc.GetString(data, i + 1, 2), NumberStyles.HexNumber, null, out var code))
                        {
                            if (!skipping) { if (pendingSkip > 0) pendingSkip--; else sb.Append(enc.GetString(new[] { (byte)code })); }
                        }
                        i += 3; continue;
                    }
                    if (n == '*') { skipping = true; i++; continue; }
                    if (n == '\\' || n == '{' || n == '}') { if (!skipping) sb.Append(n); i++; continue; }
                    if (n == '~') { if (!skipping) sb.Append('\u00A0'); i++; continue; }
                    if (!char.IsLetter(n)) { i++; continue; }
                    int start = i;
                    while (i < data.Length && char.IsLetter((char)data[i])) i++;
                    string word = enc.GetString(data, start, i - start);
                    int numStart = i;
                    if (i < data.Length && ((char)data[i] == '-' || char.IsDigit((char)data[i]))) { i++; while (i < data.Length && char.IsDigit((char)data[i])) i++; }
                    string num = enc.GetString(data, numStart, i - numStart);
                    if (i < data.Length && data[i] == ' ') i++;
                    if (destinations.Contains(word)) { skipping = true; continue; }
                    if (skipping) continue;
                    switch (word)
                    {
                        case "par": case "line": case "sect": case "row": sb.Append('\n'); break;
                        case "tab": case "cell": sb.Append('\t'); break;
                        case "uc": if (int.TryParse(num, out var uc)) ucSkip = uc; break;
                        case "u":
                            if (int.TryParse(num, out var u)) { sb.Append((char)(u < 0 ? u + 65536 : u)); pendingSkip = ucSkip; }
                            break;
                        case "emdash": sb.Append('—'); break;
                        case "endash": sb.Append('–'); break;
                        case "bullet": sb.Append('•'); break;
                        case "lquote": sb.Append('‘'); break;
                        case "rquote": sb.Append('’'); break;
                        case "ldblquote": sb.Append('“'); break;
                        case "rdblquote": sb.Append('”'); break;
                    }
                    continue;
                }
                if (c == '\r' || c == '\n') { i++; continue; }
                if (!skipping)
                {
                    if (pendingSkip > 0) pendingSkip--;
                    else sb.Append(enc.GetString(data, i, 1));
                }
                i++;
            }
            return SpaceParagraphs(Lines(sb.ToString()).Select(l => new Block(0, l.TrimEnd())).ToList());
        }

        /// <summary>Formats that don't keep blank lines get one between paragraphs, so TXT/MD stay readable.</summary>
        private static List<Block> SpaceParagraphs(List<Block> blocks)
        {
            var result = new List<Block>();
            foreach (var b in blocks.Where(b => b.Text.Length > 0))
            {
                if (result.Count > 0) result.Add(new Block(0, ""));
                result.Add(b);
            }
            return result;
        }

        // ================================================================== writers

        private static string ToText(List<Block> blocks) =>
            string.Join("\r\n", blocks.Select(b => b.Text)) + "\r\n";

        private static string ToMarkdown(List<Block> blocks)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (b.Level > 0) sb.Append(new string('#', b.Level)).Append(' ').Append(b.Text);
                else
                {
                    sb.Append(b.Text);
                    // A single newline would merge lines in Markdown: keep them as hard breaks.
                    bool nextIsText = i + 1 < blocks.Count && blocks[i + 1].Level == 0 && blocks[i + 1].Text.Length > 0;
                    if (b.Text.Length > 0 && nextIsText) sb.Append("  ");
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Groups consecutive non-empty paragraph lines (separated by blank lines) for HTML/EPUB/DOCX.</summary>
        private static IEnumerable<(int Level, List<string> Lines)> Paragraphs(List<Block> blocks)
        {
            List<string>? current = null;
            foreach (var b in blocks)
            {
                if (b.Level > 0)
                {
                    if (current != null) { yield return (0, current); current = null; }
                    yield return (b.Level, new List<string> { b.Text });
                }
                else if (b.Text.Length == 0)
                {
                    if (current != null) { yield return (0, current); current = null; }
                }
                else (current ??= new List<string>()).Add(b.Text);
            }
            if (current != null) yield return (0, current);
        }

        private static string Esc(string s) => WebUtility.HtmlEncode(s);

        private static string HtmlBody(List<Block> blocks)
        {
            var sb = new StringBuilder();
            foreach (var (level, lines) in Paragraphs(blocks))
            {
                var inner = string.Join("<br/>\n", lines.Select(Esc));
                sb.Append(level > 0 ? $"<h{level}>{inner}</h{level}>\n" : $"<p>{inner}</p>\n");
            }
            return sb.ToString();
        }

        private static string ToHtml(List<Block> blocks, string title) =>
            "<!DOCTYPE html>\n<html lang=\"es\">\n<head>\n<meta charset=\"utf-8\"/>\n" +
            $"<title>{Esc(title)}</title>\n" +
            "<style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:46rem;margin:3rem auto;padding:0 1rem;line-height:1.6;color:#222}</style>\n" +
            "</head>\n<body>\n" + HtmlBody(blocks) + "</body>\n</html>\n";

        private static string ToRtf(List<Block> blocks)
        {
            var sb = new StringBuilder();
            sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Calibri;}}\viewkind4\uc1\f0\fs22 ");
            foreach (var b in blocks)
            {
                if (b.Level < 0)
                    sb.Append(@"\page ");
                else if (b.Level > 0)
                    sb.Append(@"{\b\fs").Append(b.Level == 1 ? 36 : b.Level == 2 ? 30 : 26).Append(' ').Append(RtfEscape(b.Text)).Append(@"}\par ");
                else
                    sb.Append(RtfEscape(b.Text)).Append(@"\par ");
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string RtfEscape(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
                else if (c == '\t') sb.Append(@"\tab ");
                else if (c < 0x80) sb.Append(c);
                else sb.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
            }
            return sb.ToString();
        }

        private static string X(string s) => System.Security.SecurityElement.Escape(StripInvalidXml(s)) ?? "";

        private static string StripInvalidXml(string s) =>
            new string(s.Where(c => c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF)).ToArray());

        private static void WriteDocx(List<Block> blocks, string output)
        {
            var body = new StringBuilder();
            foreach (var b in blocks)
            {
                if (b.Level < 0) { body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>"); continue; }
                body.Append("<w:p>");
                if (b.Level > 0) body.Append($"<w:pPr><w:pStyle w:val=\"Heading{Math.Min(b.Level, 3)}\"/></w:pPr>");
                if (b.Text.Length > 0)
                {
                    var parts = b.Text.Split('\t');
                    body.Append("<w:r>");
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (i > 0) body.Append("<w:tab/>");
                        body.Append("<w:t xml:space=\"preserve\">").Append(X(parts[i])).Append("</w:t>");
                    }
                    body.Append("</w:r>");
                }
                body.Append("</w:p>");
            }

            const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var files = new Dictionary<string, string>
            {
                ["[Content_Types].xml"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                    "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                    "</Types>",
                ["_rels/.rels"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                    "</Relationships>",
                ["word/_rels/document.xml.rels"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>",
                ["word/styles.xml"] =
                    $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"{W}\">" +
                    "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\" w:eastAsia=\"Calibri\"/><w:sz w:val=\"22\"/><w:lang w:val=\"es-ES\"/></w:rPr></w:rPrDefault>" +
                    "<w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                    "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
                    HeadingStyle(1, 32) + HeadingStyle(2, 28) + HeadingStyle(3, 24) +
                    "</w:styles>",
                ["word/document.xml"] =
                    $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\"><w:body>" +
                    body +
                    "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1417\" w:right=\"1701\" w:bottom=\"1417\" w:left=\"1701\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>" +
                    "</w:body></w:document>",
            };
            WriteZip(output, files, null);
        }

        private static string HeadingStyle(int level, int halfPoints) =>
            $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
            $"<w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"80\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>" +
            $"<w:rPr><w:b/><w:color w:val=\"C2410C\"/><w:sz w:val=\"{halfPoints}\"/></w:rPr></w:style>";

        private static void WriteEpub(List<Block> blocks, string title, string output)
        {
            var id = "urn:uuid:" + Guid.NewGuid();
            var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var xhtmlBody = new StringBuilder();
            foreach (var (level, lines) in Paragraphs(blocks))
            {
                var inner = string.Join("<br/>", lines.Select(X));
                xhtmlBody.Append(level > 0 ? $"<h{level}>{inner}</h{level}>\n" : $"<p>{inner}</p>\n");
            }
            var t = X(title);
            var files = new Dictionary<string, string>
            {
                ["META-INF/container.xml"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
                    "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>",
                ["OEBPS/content.opf"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" xml:lang=\"es\">" +
                    $"<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"bookid\">{id}</dc:identifier><dc:title>{t}</dc:title><dc:language>es</dc:language>" +
                    $"<meta property=\"dcterms:modified\">{modified}</meta></metadata>" +
                    "<manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>" +
                    "<item id=\"text\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>" +
                    "<spine><itemref idref=\"text\"/></spine></package>",
                ["OEBPS/nav.xhtml"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"es\">" +
                    $"<head><title>{t}</title></head><body><nav epub:type=\"toc\"><ol><li><a href=\"text.xhtml\">{t}</a></li></ol></nav></body></html>",
                ["OEBPS/text.xhtml"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\" lang=\"es\">" +
                    $"<head><title>{t}</title><style>body{{line-height:1.5}} h1,h2,h3{{color:#c2410c}}</style></head><body>\n" +
                    xhtmlBody + "</body></html>",
            };
            WriteZip(output, files, "application/epub+zip");
        }

        private static void WriteZip(string output, Dictionary<string, string> files, string? epubMimetype)
        {
            using var fs = File.Create(output);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            if (epubMimetype != null)
            {
                // EPUB rule: "mimetype" first and uncompressed.
                var m = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using var s = m.Open();
                var b = Encoding.ASCII.GetBytes(epubMimetype);
                s.Write(b, 0, b.Length);
            }
            foreach (var (name, content) in files)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open();
                var b = new UTF8Encoding(false).GetBytes(content);
                s.Write(b, 0, b.Length);
            }
        }
    }

    /// <summary>Minimal text PDF: A4, Courier (built into every PDF reader), Windows-1252 characters.</summary>
    internal static class TextPdf
    {
        private static double PageW = 595.28, PageH = 841.89, Margin = 56;

        /// <param name="noWrapSmallFont">Tables: never wrap; shrink the font (and turn the page) so the widest line fits.</param>
        public static void Write(List<Documents.Block> blocks, string output, bool noWrapSmallFont = false)
        {
            PageW = 595.28; PageH = 841.89; Margin = 56;
            double bodySize = 10.5;
            if (noWrapSmallFont)
            {
                Margin = 36;
                int widest = Math.Max(1, blocks.Where(b => b.Level == 0).Select(b => b.Text.Length).DefaultIfEmpty(1).Max());
                bodySize = Math.Min(10, (PageW - 2 * Margin) / (0.6 * widest));
                if (bodySize < 7) { (PageW, PageH) = (PageH, PageW); bodySize = Math.Min(10, (PageW - 2 * Margin) / (0.6 * widest)); }
                bodySize = Math.Max(4.5, Math.Floor(bodySize * 2) / 2);
            }
            // Lay out lines: (font size, bold, text)
            var lines = new List<(double size, bool bold, string text)>();
            foreach (var b in blocks)
            {
                if (b.Level < 0) { lines.Add((-1, false, "")); continue; }   // page break
                double size = b.Level switch { 1 => 16, 2 => 14, 3 => 12.5, 0 => bodySize, _ => 11.5 };
                bool bold = b.Level > 0;
                int maxChars = (int)((PageW - 2 * Margin) / (size * 0.6));   // Courier: every glyph is 0.6 em
                if (b.Level > 0 && lines.Count > 0) lines.Add((size * 0.5, false, ""));
                var text = b.Text.Replace("\t", "    ");
                if (noWrapSmallFont && b.Level == 0) lines.Add((size, bold, text.Length > maxChars ? text[..maxChars] : text));
                else foreach (var l in Wrap(text, maxChars)) lines.Add((size, bold, l));
            }
            if (lines.Count == 0) lines.Add((10.5, false, ""));

            // Paginate
            var pages = new List<StringBuilder>();
            StringBuilder? page = null;
            double y = 0;
            foreach (var (size, bold, text) in lines)
            {
                if (size < 0) { page = null; continue; }   // page break: next line starts a new page
                double lead = size * 1.35;
                if (page == null || y - lead < Margin)
                {
                    page = new StringBuilder();
                    pages.Add(page);
                    y = PageH - Margin;
                }
                y -= lead;
                if (text.Length > 0)
                    page.Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(N(size)).Append(" Tf ")
                        .Append(N(Margin)).Append(' ').Append(N(y)).Append(" Td (").Append(PdfString(text)).Append(") Tj ET\n");
            }

            // Objects: 1 catalog, 2 pages, 3 F1, 4 F2, then (page, content) pairs
            var objects = new List<byte[]>();
            var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{5 + i * 2} 0 R"));
            objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
            objects.Add(Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>"));
            objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"));
            objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Courier-Bold /Encoding /WinAnsiEncoding >>"));
            for (int i = 0; i < pages.Count; i++)
            {
                int contentId = 6 + i * 2;
                objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageW)} {N(PageH)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>"));
                var content = Latin(pages[i].ToString());
                var head = Ascii($"<< /Length {content.Length} >>\nstream\n");
                var tail = Ascii("\nendstream");
                objects.Add(head.Concat(content).Concat(tail).ToArray());
            }

            using var fs = File.Create(output);
            var offsets = new List<long>();
            void W(byte[] b) => fs.Write(b, 0, b.Length);
            W(Ascii("%PDF-1.4\n"));
            W(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
            for (int i = 0; i < objects.Count; i++)
            {
                offsets.Add(fs.Position);
                W(Ascii($"{i + 1} 0 obj\n"));
                W(objects[i]);
                W(Ascii("\nendobj\n"));
            }
            long xref = fs.Position;
            var sb = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
            sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            W(Ascii(sb.ToString()));
        }

        private static IEnumerable<string> Wrap(string text, int max)
        {
            if (text.Length <= max) { yield return text; yield break; }
            int pos = 0;
            while (pos < text.Length)
            {
                if (text.Length - pos <= max) { yield return text[pos..]; yield break; }
                int cut = text.LastIndexOf(' ', pos + max, max);
                if (cut <= pos) cut = pos + max;                       // one very long word
                yield return text[pos..cut].TrimEnd();
                pos = cut;
                while (pos < text.Length && text[pos] == ' ') pos++;
            }
        }

        private static string PdfString(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '(' || c == ')' || c == '\\') sb.Append('\\').Append(c);
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static readonly Encoding Win1252 = CreateWin1252();
        private static Encoding CreateWin1252()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        }

        private static byte[] Latin(string s) => Win1252.GetBytes(s);
        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
        private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
