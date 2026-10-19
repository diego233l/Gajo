using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Block = Gajo.Core.Documents.Block;

namespace Gajo.Core
{
    /// <summary>One recognised line, in pixels of the rendered page (origin top-left).</summary>
    public sealed record OcrLine(string Text, double X, double Y, double Width, double Height);

    /// <summary>OCR backend. On Windows it is Windows.Media.Ocr (built into the OS).</summary>
    public interface IPdfOcr
    {
        /// <summary>Null when OCR can be used; otherwise a user-facing reason.</summary>
        string? UnavailableReason { get; }
        Task<IReadOnlyList<OcrLine>> RecognizePageAsync(string pdfPath, int pageNumber, CancellationToken ct);
        /// <summary>Reads text in a PNG image (coordinates in the image's pixels).</summary>
        Task<IReadOnlyList<OcrLine>> RecognizeImageAsync(byte[] png, CancellationToken ct);
    }

    /// <summary>
    /// PDF → document blocks. Pages that contain real text are read directly (exact, fast);
    /// pages that are only an image (scans, photos) go through OCR.
    /// </summary>
    public static class PdfText
    {
        public static IPdfOcr? Ocr { get; set; }

        /// <summary>A page with fewer visible characters than this is treated as a scan.</summary>
        private const int MinTextChars = 25;

        public sealed record Result(List<Block> Blocks, int Pages, int OcrPages);

        public static async Task<Result> ReadAsync(string path, IProgress<double>? progress, CancellationToken ct)
        {
            // 1) Text layer of every page (PdfPig is synchronous: keep it off the UI thread).
            var pages = await Task.Run(() => ReadTextLayer(path, ct), ct);

            // 2) Body font size of the whole document, to recognise headings.
            var textParas = pages.Where(p => !p.NeedsOcr).SelectMany(p => p.Paragraphs).ToList();
            double body = BodySize(textParas);
            var levels = HeadingLevels(textParas, body);

            var blocks = new List<Block>();
            int ocrPages = 0;
            string? ocrProblem = null;

            for (int i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = pages[i];
                List<Block> pageBlocks;

                if (page.NeedsOcr && Ocr is { UnavailableReason: null } ocr)
                {
                    var lines = await ocr.RecognizePageAsync(path, page.Number, ct);
                    pageBlocks = FromOcrLines(lines);
                    ocrPages++;
                }
                else
                {
                    if (page.NeedsOcr) ocrProblem ??= Ocr?.UnavailableReason ?? "no hay OCR disponible";
                    pageBlocks = page.Paragraphs.Select(p => new Block(LevelOf(p, body, levels), p.Text)).ToList();
                }

                // A paragraph that continues on the next page is joined instead of split by a page break.
                if (blocks.Count > 0 && pageBlocks.Count > 0 && ContinuesOnNextPage(blocks[^1], pageBlocks[0]))
                {
                    blocks[^1] = blocks[^1] with { Text = JoinLines(new[] { blocks[^1].Text, pageBlocks[0].Text }) };
                    pageBlocks.RemoveAt(0);
                }
                else if (blocks.Count > 0) blocks.Add(new Block(-1, ""));   // page break

                foreach (var b in pageBlocks)
                {
                    if (blocks.Count > 0 && blocks[^1].Level != -1) blocks.Add(new Block(0, ""));
                    blocks.Add(b);
                }
                progress?.Report((i + 1.0) / pages.Count * 0.95);
            }

            if (!blocks.Any(b => b.Text.Length > 0))
            {
                throw new ConversionException(ocrProblem != null
                    ? "Este PDF es una imagen escaneada y no se pudo aplicar OCR: " + ocrProblem
                    : "El PDF no contiene texto.");
            }
            return new Result(blocks, pages.Count, ocrPages);
        }

        // ------------------------------------------------------------------ text layer

        private sealed record Para(string Text, double FontSize, int Lines, bool Bold);
        private sealed record PageText(int Number, bool NeedsOcr, List<Para> Paragraphs);

        private static List<PageText> ReadTextLayer(string path, CancellationToken ct)
        {
            var result = new List<PageText>();
            PdfDocument doc;
            try { doc = PdfDocument.Open(path); }
            catch (Exception ex) when (ex.GetType().Name.Contains("Encrypted"))
            {
                throw new ConversionException("El PDF está protegido con contraseña.");
            }
            catch (Exception ex)
            {
                throw new ConversionException("No se pudo abrir el PDF: " + ex.Message, ex.ToString());
            }

            using (doc)
            {
                foreach (var page in doc.GetPages())
                {
                    ct.ThrowIfCancellationRequested();
                    var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
                    if (letters.Count < MinTextChars)
                    {
                        result.Add(new PageText(page.Number, true, new List<Para>()));
                        continue;
                    }

                    var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
                    var segmented = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                    var ordered = UnsupervisedReadingOrderDetector.Instance.Get(segmented);

                    var paras = new List<Para>();
                    foreach (var block in ordered)
                        foreach (var para in SplitBlock(block))
                            paras.Add(para);
                    result.Add(new PageText(page.Number, false, paras.Where(p => !IsPageNumber(p.Text)).ToList()));
                }
            }
            return result;
        }

        /// <summary>A layout block can hold several paragraphs: split where the line gap is clearly larger.</summary>
        private static IEnumerable<Para> SplitBlock(TextBlock block)
        {
            var lines = block.TextLines.ToList();
            if (lines.Count == 0) yield break;

            var gaps = new List<double>();
            for (int i = 1; i < lines.Count; i++)
                gaps.Add(lines[i - 1].BoundingBox.Bottom - lines[i].BoundingBox.Top);
            double typical = gaps.Count > 0 ? gaps.OrderBy(g => g).ElementAt(gaps.Count / 2) : 0;

            var current = new List<TextLine> { lines[0] };
            for (int i = 1; i < lines.Count; i++)
            {
                double gap = gaps[i - 1];
                double h = lines[i].BoundingBox.Height;
                if (gap > Math.Max(typical * 1.6, h * 0.9))
                {
                    yield return MakePara(current);
                    current = new List<TextLine>();
                }
                current.Add(lines[i]);
            }
            yield return MakePara(current);
        }

        private static Para MakePara(List<TextLine> lines)
        {
            var text = JoinLines(lines.Select(l => l.Text));
            var sizes = lines.SelectMany(l => l.Words).SelectMany(w => w.Letters).Select(l => l.PointSize).Where(s => s > 0).ToList();
            double size = sizes.Count > 0 ? sizes.OrderBy(s => s).ElementAt(sizes.Count / 2) : 0;
            var letters = lines.SelectMany(l => l.Words).SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            bool bold = letters.Count > 0 && letters.Count(l => IsBoldFont(l.FontName)) >= letters.Count * 0.8;
            return new Para(text, size, lines.Count, bold);
        }

        /// <summary>Joins wrapped lines into one paragraph, undoing end-of-line hyphenation.</summary>
        public static string JoinLines(IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            foreach (var raw in lines)
            {
                var line = Regex.Replace(raw.Trim(), @"\s{2,}", " ");
                if (line.Length == 0) continue;
                if (sb.Length == 0) { sb.Append(line); continue; }
                bool hyphen = sb.Length > 1 && sb[^1] == '-' && char.IsLetter(sb[^2]) && char.IsLower(line[0]);
                if (hyphen) sb.Length--;
                else sb.Append(' ');
                sb.Append(line);
            }
            return sb.ToString();
        }

        private static bool IsPageNumber(string text) =>
            Regex.IsMatch(text.Trim(), @"^(-\s*)?(p[áa]g(ina)?\.?\s*)?\d{1,4}(\s*(/|de|of)\s*\d{1,4})?(\s*-)?$", RegexOptions.IgnoreCase);

        private static double BodySize(IEnumerable<Para> paras)
        {
            // Font size covering the most characters = body text.
            var hist = new Dictionary<double, int>();
            foreach (var p in paras)
            {
                if (p.FontSize <= 0) continue;
                double k = Math.Round(p.FontSize * 2) / 2;
                hist[k] = hist.GetValueOrDefault(k) + p.Text.Length;
            }
            return hist.Count == 0 ? 0 : hist.OrderByDescending(kv => kv.Value).First().Key;
        }

        private static bool IsBoldFont(string? name) =>
            name != null && Regex.IsMatch(name, "Bold|Black|Heavy|Semibold|Demi", RegexOptions.IgnoreCase);

        /// <summary>Short bold line at body size, not ending like a sentence: a minor heading.</summary>
        private static bool IsBoldHeading(Para p, double body) =>
            body > 0 && p.Bold && p.Lines == 1 && p.Text.Length <= 90 && p.FontSize >= body * 0.95
            && ".,;:".IndexOf(p.Text.TrimEnd()[^1]) < 0;

        private static bool IsHeadingCandidate(Para p, double body) =>
            body > 0 && p.FontSize >= body * 1.08 && p.Text.Length <= 160 && p.Lines <= 3;

        /// <summary>The distinct heading sizes, largest first, become levels 1, 2, 3.</summary>
        private static Dictionary<double, int> HeadingLevels(List<Para> paras, double body)
        {
            var sizes = paras.Where(p => IsHeadingCandidate(p, body))
                             .Select(p => Math.Round(p.FontSize * 2) / 2)
                             .Distinct().OrderByDescending(x => x).ToList();
            var map = new Dictionary<double, int>();
            for (int i = 0; i < sizes.Count; i++) map[sizes[i]] = Math.Min(i + 1, 3);
            return map;
        }

        private static int LevelOf(Para p, double body, Dictionary<double, int> levels)
        {
            if (IsHeadingCandidate(p, body) && levels.TryGetValue(Math.Round(p.FontSize * 2) / 2, out var l)) return l;
            if (IsBoldHeading(p, body)) return Math.Min(levels.Count + 1, 3);
            return 0;
        }

        private static bool ContinuesOnNextPage(Block last, Block next)
        {
            if (last.Level != 0 || next.Level != 0 || last.Text.Length == 0 || next.Text.Length == 0) return false;
            char end = last.Text.TrimEnd()[^1];
            if (".!?:;»\")”".IndexOf(end) >= 0) return false;
            return char.IsLower(next.Text[0]) || end == ',' || end == '-';
        }

        // ------------------------------------------------------------------ OCR pages

        public static List<Block> FromOcrLines(IReadOnlyList<OcrLine> lines)
        {
            var result = new List<Block>();
            var useful = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
            if (useful.Count == 0) return result;

            double medianH = useful.Select(l => l.Height).OrderBy(h => h).ElementAt(useful.Count / 2);
            var current = new List<OcrLine>();

            void Flush()
            {
                if (current.Count == 0) return;
                var text = JoinLines(current.Select(l => l.Text));
                double h = current.Average(l => l.Height);
                // OCR boxes are a rough measure of font size (accents and descenders change them),
                // so a heading is mainly a lone short line that starts with a capital and doesn't end like a sentence.
                int level = 0;
                bool lone = current.Count == 1 && text.Length <= 70 && char.IsUpper(text[0])
                            && ".,;:".IndexOf(text[^1]) < 0;
                if (lone) level = h > medianH * 1.15 ? 1 : 2;
                else if (current.Count <= 2 && text.Length < 90 && h > medianH * 1.5) level = 1;
                if (!IsPageNumber(text)) result.Add(new Block(level, text));
                current.Clear();
            }

            foreach (var line in useful)
            {
                if (current.Count > 0)
                {
                    var prev = current[^1];
                    double gap = line.Y - (prev.Y + prev.Height);
                    bool bigGap = gap > medianH * 0.85;
                    bool wentUp = line.Y < prev.Y - medianH;                      // new column
                    bool sizeJump = Math.Abs(line.Height - prev.Height) > medianH * 0.4;
                    bool indented = line.X - current[0].X > medianH * 1.5 && current.Count > 1;
                    if (bigGap || wentUp || sizeJump || indented) Flush();
                }
                current.Add(line);
            }
            Flush();
            return result;
        }
    }
}
