using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gajo.Core.Layout;

namespace Gajo.Core
{
    /// <summary>Renders part of a PDF page to PNG. On Windows: Windows.Data.Pdf (built in).</summary>
    public interface IPdfRegionRenderer
    {
        /// <param name="left">Points from the page's left edge.</param>
        /// <param name="top">Points from the page's top edge.</param>
        /// <param name="whiteOut">Areas (same coordinate system) painted white after rendering.</param>
        Task<byte[]?> RenderPngAsync(string pdfPath, int pageNumber, double left, double top, double width, double height, double dpi, CancellationToken ct,
                                     IReadOnlyList<(double Left, double Top, double Width, double Height)>? whiteOut = null);
    }

    /// <summary>
    /// PDF → Word keeping the layout: real Word tables (merged cells, borders, shading),
    /// fonts, sizes, bold, colours, alignment and spacing, drawings as images and the
    /// repeated page header as a Word header. Scanned pages go through OCR.
    /// </summary>
    public static class PdfToDocx
    {
        public static IPdfRegionRenderer? Renderer { get; set; }

        private const double FigureDpi = 220;

        public static async Task ConvertAsync(string pdfPath, string docxPath, IProgress<double>? progress, CancellationToken ct)
        {
            DocModel model;
            try { model = await Task.Run(() => PdfLayoutAnalyzer.Analyze(pdfPath), ct); }
            catch (Exception ex) when (ex.GetType().Name.Contains("Encrypted"))
            {
                throw new ConversionException("El PDF está protegido con contraseña.");
            }
            progress?.Report(0.3);

            // Scanned pages
            var ocrPages = model.Pages.Where(p => p.NeedsOcr).ToList();
            if (ocrPages.Count > 0)
            {
                var ocr = PdfText.Ocr;
                if (ocr is { UnavailableReason: null })
                {
                    int i = 0;
                    foreach (var p in ocrPages)
                    {
                        ct.ThrowIfCancellationRequested();
                        var lines = await ocr.RecognizePageAsync(pdfPath, p.Number, ct);
                        p.OcrBlocks = PdfText.FromOcrLines(lines);
                        progress?.Report(0.3 + 0.5 * (++i) / ocrPages.Count);
                    }
                }
                else if (ocrPages.Count == model.Pages.Count)
                {
                    throw new ConversionException("Este PDF es una imagen escaneada y no se pudo aplicar OCR: "
                                                  + (ocr?.UnavailableReason ?? "no hay OCR disponible"));
                }
            }

            // Pictures: drawings, header and footer bands.
            var renderer = Renderer;
            if (renderer != null)
            {
                foreach (var p in model.Pages)
                    foreach (var f in p.Elements.SelectMany(Flatten).OfType<Figure>())
                    {
                        ct.ThrowIfCancellationRequested();
                        f.Png = await SafeRender(renderer, pdfPath, p, f.Left, f.Top, f.Right - f.Left, f.Top - f.Bottom, ct);
                    }
                // Pictures inside a text PDF can contain text too (scans pasted in, screenshots): read them.
                var ocrEngine = PdfText.Ocr;
                if (ocrEngine is { UnavailableReason: null })
                {
                    foreach (var f in model.Pages.SelectMany(p => p.Elements.SelectMany(Flatten)).OfType<Figure>())
                    {
                        if (!f.IsRaster || f.Png == null || f.Right - f.Left < 60 || f.Top - f.Bottom < 20) continue;
                        ct.ThrowIfCancellationRequested();
                        IReadOnlyList<OcrLine> lines;
                        try { lines = await ocrEngine.RecognizeImageAsync(f.Png, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch { continue; }
                        int chars = lines.Sum(l => l.Text.Trim().Length);
                        if (chars < 15) continue;
                        double pxPerPt = FigureDpi / 72.0;
                        double area = (f.Right - f.Left) * (f.Top - f.Bottom) * pxPerPt * pxPerPt;
                        double textArea = lines.Sum(l => l.Width * l.Height);
                        var heights = lines.Select(l => l.Height).OrderBy(h => h).ToList();
                        // Mostly text: the recognised lines fill most of the picture (a photo with a sign does not).
                        double bx0 = lines.Min(l => l.X), by0 = lines.Min(l => l.Y), bx1 = lines.Max(l => l.X + l.Width), by1 = lines.Max(l => l.Y + l.Height);
                        double textBox = (bx1 - bx0) * (by1 - by0);
                        if (Environment.GetEnvironmentVariable("GAJO_DEBUG") == "1")
                            Console.WriteLine($"ocr fig {f.Right - f.Left:0}x{f.Top - f.Bottom:0}pt lines={lines.Count} chars={chars} box/area={textBox / area:0.00} text/box={textArea / textBox:0.00}");
                        if (textBox / area >= 0.3 && textArea / textBox >= 0.25)
                        {
                            f.OcrBlocks = PdfText.FromOcrLines(lines);
                            f.OcrFontSize = Math.Round(heights[heights.Count / 2] / pxPerPt * 0.85 * 2) / 2;
                        }
                        else
                        {
                            f.AltText = string.Join(" ", lines.Select(l => l.Text.Trim()));
                        }
                    }
                }

                foreach (var band in new[] { model.Header, model.Footer })
                {
                    if (band == null) continue;
                    var page = model.Pages.First(p => p.Number == band.PageNumber);
                    var masks = band.Masks.Select(m => (m.Left, page.Height - m.Top, m.Right - m.Left, m.Top - m.Bottom)).ToList();
                    band.Png = await SafeRender(renderer, pdfPath, page, band.Left, band.Top, band.Right - band.Left, band.Top - band.Bottom, ct, masks);
                }
            }
            progress?.Report(0.9);

            await Task.Run(() => new DocxWriter(model).Save(docxPath), ct);
        }

        private static IEnumerable<Element> Flatten(Element e) =>
            e is ColumnBlock cb ? cb.LeftItems.Concat(cb.RightItems) : new[] { e };

        private static async Task<byte[]?> SafeRender(IPdfRegionRenderer r, string pdf, PageModel page, double left, double top, double w, double h, CancellationToken ct,
                                                      IReadOnlyList<(double, double, double, double)>? whiteOut = null)
        {
            try
            {
                if (w < 1 || h < 1) return null;
                return await r.RenderPngAsync(pdf, page.Number, left, page.Height - top, w, h, FigureDpi, ct, whiteOut);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        // ===================================================================== writer

        private sealed class DocxWriter
        {
            private readonly DocModel _m;
            private readonly List<(string Id, byte[] Png)> _media = new();
            private int _docPrId = 1;
            private double _boxL, _boxR;   // area being written (page content or a column)
            private int _sections;
            private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            public DocxWriter(DocModel m) { _m = m; }

            private static string T(double pt) => ((int)Math.Round(pt * 20)).ToString(CultureInfo.InvariantCulture);   // twips
            private static string E(double pt) => ((long)Math.Round(pt * 12700)).ToString(CultureInfo.InvariantCulture); // EMU
            private static string X(string s) => SecurityElement.Escape(new string(s.Where(c => c == '\t' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF).ToArray())) ?? "";
            private static string HalfPt(double pt) => Math.Max(2, (int)Math.Round(pt * 2)).ToString(CultureInfo.InvariantCulture);

            private string AddImage(byte[] png)
            {
                var id = "rIdImg" + (_media.Count + 1);
                _media.Add((id, png));
                return id;
            }

            private bool _hasHeader, _hasFooter;

            /// <summary>Section properties. cols = null → one column. Only the first section sets the starting page number.</summary>
            private string SectPr(ColumnBlock? cols)
            {
                var sect = new StringBuilder("<w:sectPr>");
                if (_hasHeader) sect.Append("<w:headerReference w:type=\"default\" r:id=\"rIdHeader\"/>");
                if (_hasFooter) sect.Append("<w:footerReference w:type=\"default\" r:id=\"rIdFooter\"/>");
                if (_sections > 0) sect.Append("<w:type w:val=\"continuous\"/>");
                double headerDist = _m.Header != null ? Math.Max(10, _m.PageHeight - _m.Header.Top) : 28;
                double footerDist = _m.Footer != null ? Math.Max(10, _m.Footer.Bottom) : 28;
                double marginTop = _m.MarginTop, marginBottom = _m.MarginBottom;
                if (_m.Header != null) marginTop = Math.Max(marginTop, _m.PageHeight - _m.Header.Bottom + 4);
                if (_m.Footer != null) marginBottom = Math.Max(marginBottom, _m.Footer.Top + 4);
                sect.Append($"<w:pgSz w:w=\"{T(_m.PageWidth)}\" w:h=\"{T(_m.PageHeight)}\"/>");
                sect.Append($"<w:pgMar w:top=\"{T(marginTop)}\" w:right=\"{T(_m.PageWidth - _m.ContentRight)}\" w:bottom=\"{T(marginBottom)}\" w:left=\"{T(_m.ContentLeft)}\" w:header=\"{T(headerDist)}\" w:footer=\"{T(footerDist)}\" w:gutter=\"0\"/>");
                if (_sections == 0 && _m.Header?.PageNumberStart is int start && _m.Header.Png != null)
                    sect.Append($"<w:pgNumType w:start=\"{start}\"/>");
                if (cols != null)
                {
                    double space = cols.RightL - cols.LeftR;
                    sect.Append($"<w:cols w:num=\"2\" w:space=\"{T(space)}\" w:equalWidth=\"0\"><w:col w:w=\"{T(cols.LeftR - cols.LeftL)}\" w:space=\"{T(space)}\"/><w:col w:w=\"{T(cols.RightR - cols.RightL)}\"/></w:cols>");
                }
                sect.Append("</w:sectPr>");
                _sections++;
                return sect.ToString();
            }

            /// <summary>Ends the current section here (Word keeps section settings in the last paragraph of each section).</summary>
            private string SectionBreak(ColumnBlock? cols) =>
                "<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/>" + SectPr(cols) + "<w:rPr><w:sz w:val=\"2\"/></w:rPr></w:pPr></w:p>";

            public void Save(string path)
            {
                string? headerXml = _m.Header?.Png != null ? BandXml("hdr", _m.Header) : null;
                string? footerXml = _m.Footer?.Png != null ? BandXml("ftr", _m.Footer) : null;
                _hasHeader = headerXml != null;
                _hasFooter = footerXml != null;

                var body = new StringBuilder();
                PageModel? prevPage = null;
                foreach (var page in _m.Pages)
                {
                    // Force a new page only where the PDF page ended early on purpose. Otherwise let Word
                    // flow: if a page comes out slightly taller in Word, a hard break would leave a nearly blank page.
                    if (prevPage != null)
                    {
                        double lastBottom = prevPage.Elements.Count > 0 ? prevPage.Elements.Min(e => e.Bottom) : prevPage.Height;
                        bool endedEarly = prevPage.NeedsOcr || page.NeedsOcr || lastBottom > prevPage.Height * 0.30;
                        if (endedEarly)
                            body.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/></w:pPr><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                    }
                    prevPage = page;
                    if (page.NeedsOcr) WriteOcrPage(body, page);
                    else WritePage(body, page);
                }

                var sect = new StringBuilder(SectPr(null));

                string ns = $"xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"";
                var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {ns}><w:body>{body}{sect}</w:body></w:document>";

                // Relationships: document-level images vs header/footer images are kept separate.
                var docMedia = _media.Where(m => !m.Id.StartsWith("rIdImgH") && !m.Id.StartsWith("rIdImgF")).ToList();
                var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                rels.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
                rels.Append("<Relationship Id=\"rIdSettings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>");
                if (headerXml != null) rels.Append("<Relationship Id=\"rIdHeader\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>");
                if (footerXml != null) rels.Append("<Relationship Id=\"rIdFooter\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>");
                foreach (var m in docMedia)
                    rels.Append($"<Relationship Id=\"{m.Id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{m.Id}.png\"/>");
                rels.Append("</Relationships>");

                var files = new List<(string Name, byte[] Data)>
                {
                    ("[Content_Types].xml", U(ContentTypes(headerXml != null, footerXml != null))),
                    ("_rels/.rels", U("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>")),
                    ("word/document.xml", U(document)),
                    ("word/_rels/document.xml.rels", U(rels.ToString())),
                    ("word/styles.xml", U(Styles())),
                    ("word/settings.xml", U($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:settings xmlns:w=\"{W}\"><w:defaultTabStop w:val=\"708\"/><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>")),
                };
                if (headerXml != null) AddBandPart(files, "header1", headerXml, "rIdImgH");
                if (footerXml != null) AddBandPart(files, "footer1", footerXml, "rIdImgF");
                foreach (var m in _media) files.Add(($"word/media/{m.Id}.png", m.Png));

                using var fs = File.Create(path);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                foreach (var (name, data) in files)
                {
                    var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var s = e.Open();
                    s.Write(data, 0, data.Length);
                }
            }

            private void AddBandPart(List<(string, byte[])> files, string name, string xml, string prefix)
            {
                files.Add(($"word/{name}.xml", U(xml)));
                var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                foreach (var m in _media.Where(m => m.Id.StartsWith(prefix)))
                    rels.Append($"<Relationship Id=\"{m.Id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{m.Id}.png\"/>");
                rels.Append("</Relationships>");
                files.Add(($"word/_rels/{name}.xml.rels", U(rels.ToString())));
            }

            private static byte[] U(string s) => new UTF8Encoding(false).GetBytes(s);

            private static string ContentTypes(bool header, bool footer) =>
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>" +
                (header ? "<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>" : "") +
                (footer ? "<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>" : "") +
                "</Types>";

            private string Styles() =>
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"{W}\">" +
                $"<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"{X(_m.BodyFont)}\" w:hAnsi=\"{X(_m.BodyFont)}\" w:cs=\"{X(_m.BodyFont)}\" w:eastAsia=\"{X(_m.BodyFont)}\"/><w:sz w:val=\"{HalfPt(_m.BodySize)}\"/><w:szCs w:val=\"{HalfPt(_m.BodySize)}\"/><w:lang w:val=\"es-ES\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
                "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TablaNormal\"><w:name w:val=\"Normal Table\"/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"40\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"40\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
                "</w:styles>";

            // ------------------------------------------------------------------ page content

            private void WritePage(StringBuilder sb, PageModel page)
            {
                double prevBottom = page.Height - _m.MarginTop;
                if (_m.Header != null) prevBottom = Math.Min(prevBottom, _m.Header.Bottom - 4);
                var run = new List<Element>();
                foreach (var el in page.Elements)
                {
                    if (el is ColumnBlock block)
                    {
                        prevBottom = WriteArea(sb, page, run, _m.ContentLeft, _m.ContentRight, prevBottom);
                        run.Clear();
                        sb.Append(SectionBreak(null));                       // end of the one-column part
                        WriteArea(sb, page, block.LeftItems, block.LeftL, block.LeftR, prevBottom);
                        // No forced column break: the text flows from the left column into the right one, as Word does.
                        WriteArea(sb, page, block.RightItems, block.RightL, block.RightR, prevBottom);
                        sb.Append(SectionBreak(block));                      // end of the two-column part
                        prevBottom = block.Bottom;
                    }
                    else run.Add(el);
                }
                WriteArea(sb, page, run, _m.ContentLeft, _m.ContentRight, prevBottom);
            }

            /// <returns>Bottom of the last element written.</returns>
            private double WriteArea(StringBuilder sb, PageModel page, List<Element> elements, double boxL, double boxR, double prevBottom)
            {
                _boxL = boxL; _boxR = boxR;
                Element? prev = null;
                foreach (var el in elements)
                {
                    double gap = prevBottom - el.Top;
                    switch (el)
                    {
                        case Paragraph p:
                            WriteParagraph(sb, p, boxL, boxR, Math.Max(0, gap));
                            break;
                        case Table t:
                            if (!t.Floating && (gap > 2 || prev is Table)) Spacer(sb, Math.Max(1, gap));
                            WriteTable(sb, t, page);
                            _boxL = boxL; _boxR = boxR;
                            break;
                        case Figure f:
                            WriteFigure(sb, f, Math.Max(0, gap));
                            break;
                        case Rule r:
                            WriteRule(sb, r, Math.Max(0, gap));
                            break;
                    }
                    if (!(el is Table { Floating: true }))
                        prevBottom = Math.Min(prevBottom, el.Bottom);
                    prev = el;
                }
                if (prev is Table) Spacer(sb, 1);
                return prevBottom;
            }

            private void WriteOcrPage(StringBuilder sb, PageModel page)
            {
                if (page.OcrBlocks == null) return;
                foreach (var b in page.OcrBlocks)
                {
                    if (b.Text.Length == 0) continue;
                    double size = b.Level == 1 ? _m.BodySize * 1.5 : b.Level == 2 ? _m.BodySize * 1.25 : _m.BodySize;
                    sb.Append("<w:p><w:pPr><w:spacing w:before=\"120\" w:after=\"0\"/></w:pPr>");
                    sb.Append("<w:r><w:rPr>").Append(b.Level > 0 ? "<w:b/>" : "").Append($"<w:sz w:val=\"{HalfPt(size)}\"/></w:rPr><w:t xml:space=\"preserve\">{X(b.Text)}</w:t></w:r></w:p>");
                }
            }

            private static void Spacer(StringBuilder sb, double heightPt)
            {
                sb.Append($"<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"{T(Math.Clamp(heightPt, 1, 300))}\" w:lineRule=\"exact\"/><w:rPr><w:sz w:val=\"2\"/></w:rPr></w:pPr></w:p>");
            }

            private void WriteParagraph(StringBuilder sb, Paragraph p, double boxLeft, double boxRight, double gapBefore)
            {
                sb.Append("<w:p><w:pPr>");
                double before = Math.Max(0, gapBefore - p.Size * 0.15);
                string spacing = $"<w:spacing w:before=\"{T(Math.Min(before, 400))}\" w:after=\"0\"";
                if (p.Lines.Count > 1 && p.LinePitch > 0)
                    spacing += $" w:line=\"{T(Math.Max(p.LinePitch, p.Size))}\" w:lineRule=\"exact\"";
                else
                    spacing += $" w:line=\"{T(p.Size * 1.2)}\" w:lineRule=\"exact\"";
                sb.Append(spacing).Append("/>");

                if (p.IndentLeft > 0.5 || Math.Abs(p.FirstLineOffset) > 0.5)
                {
                    sb.Append($"<w:ind w:left=\"{T(p.IndentLeft)}\"");
                    if (p.FirstLineOffset > 0.5) sb.Append($" w:firstLine=\"{T(p.FirstLineOffset)}\"");
                    else if (p.FirstLineOffset < -0.5) sb.Append($" w:hanging=\"{T(-p.FirstLineOffset)}\"");
                    sb.Append("/>");
                }
                // Tab stops at the exact horizontal position of every wide gap, and "______" fill-in
                // lines as underscore-leader tabs that end exactly where the PDF's line ends.
                var tabs = new SortedDictionary<int, bool>();   // pos → leader
                foreach (var line in p.Lines)
                {
                    Glyph? prev = null;
                    var ink = line.Glyphs.Where(g => !g.IsSpace).ToList();
                    for (int k = 0; k < ink.Count; k++)
                    {
                        var g = ink[k];
                        if (prev != null && PdfLayoutAnalyzer.IsTabGap(prev, g)) tabs[(int)Math.Round((g.Left - boxLeft) * 20)] = false;
                        int run = UnderscoreRun(ink, k);
                        if (run >= MinUnderscores)
                        {
                            tabs[(int)Math.Round((ink[k + run - 1].Right - boxLeft) * 20)] = true;
                            prev = ink[k + run - 1];
                            k += run - 1;
                            continue;
                        }
                        prev = g;
                    }
                }
                if (tabs.Count > 0)
                {
                    sb.Append("<w:tabs>");
                    foreach (var (pos, leader) in tabs)
                        sb.Append(leader ? $"<w:tab w:val=\"left\" w:leader=\"underscore\" w:pos=\"{pos}\"/>" : $"<w:tab w:val=\"left\" w:pos=\"{pos}\"/>");
                    sb.Append("</w:tabs>");
                }
                string jc = p.Align switch { Align.Center => "center", Align.Right => "right", Align.Justify => "both", _ => "" };
                if (jc.Length > 0) sb.Append($"<w:jc w:val=\"{jc}\"/>");
                sb.Append("</w:pPr>");

                for (int i = 0; i < p.Lines.Count; i++)
                {
                    var line = p.Lines[i];
                    if (i > 0)
                    {
                        if (p.KeepBreaks) sb.Append("<w:r><w:br/></w:r>");
                        else
                        {
                            // Flowing text: undo hyphenation, otherwise a space.
                            var prevText = p.Lines[i - 1].Text.TrimEnd();
                            bool hyphen = prevText.EndsWith("-") && prevText.Length > 1 && char.IsLetter(prevText[^2]) && line.Text.Length > 0 && char.IsLower(line.Text[0]);
                            if (!hyphen) WriteRun(sb, " ", line.Glyphs[0].Style);
                        }
                    }
                    // "☐ Text…" / "• Text…" with a hanging indent: the text after the marker starts at the indent.
                    double? hangAt = i == 0 && p.FirstLineOffset < -2 ? boxLeft + p.IndentLeft : null;
                    WriteLineRuns(sb, line, trimTrailingHyphen: !p.KeepBreaks && i < p.Lines.Count - 1 && NextStartsLower(p, i), hangAt);
                }
                sb.Append("</w:p>");
            }

            private const int MinUnderscores = 4;

            private static int UnderscoreRun(List<Glyph> ink, int k)
            {
                int n = 0;
                while (k + n < ink.Count && ink[k + n].Text == "_" && (n == 0 || ink[k + n].Left - ink[k + n - 1].Right < ink[k + n].Size * 0.3)) n++;
                return n;
            }

            private static bool NextStartsLower(Paragraph p, int i)
            {
                var t = p.Lines[i].Text.TrimEnd();
                var n = p.Lines[i + 1].Text;
                return t.EndsWith("-") && t.Length > 1 && char.IsLetter(t[^2]) && n.Length > 0 && char.IsLower(n[0]);
            }

            private void WriteLineRuns(StringBuilder sb, TextLine line, bool trimTrailingHyphen, double? hangAt = null)
            {
                var glyphs = line.Glyphs;
                if (trimTrailingHyphen)
                {
                    int last = glyphs.FindLastIndex(g => !g.IsSpace);
                    if (last >= 0 && glyphs[last].Text == "-") glyphs = glyphs.Take(last).ToList();
                }

                var text = new StringBuilder();
                TextStyle? style = null;
                Glyph? prevInk = null;
                bool pendingSpace = false;

                void Flush()
                {
                    if (style != null && text.Length > 0) WriteRun(sb, text.ToString(), style);
                    text.Clear();
                }

                var inkList = glyphs.Where(x => !x.IsSpace).ToList();
                int skipUntil = -1;
                foreach (var g in glyphs)
                {
                    if (g.IsSpace) { pendingSpace = true; continue; }
                    int inkIndex = inkList.IndexOf(g);
                    if (inkIndex <= skipUntil) { prevInk = g; pendingSpace = false; continue; }
                    if (prevInk != null)
                    {
                        double gap = PdfLayoutAnalyzer.AdvanceGap(prevInk, g);
                        double em = Math.Max(g.Size, prevInk.Size);
                        int spaces = 0;
                        if (pendingSpace || gap > em * 0.15) spaces = 1;
                        bool atHang = hangAt.HasValue && Math.Abs(g.Left - hangAt.Value) < 2.5;
                        if (atHang) hangAt = null;
                        if (atHang || PdfLayoutAnalyzer.IsTabGap(prevInk, g))
                        {
                            Flush();
                            sb.Append("<w:r>").Append(RunProps(prevInk.Style.SymbolFont == null ? prevInk.Style : g.Style)).Append("<w:tab/></w:r>");
                            spaces = 0;
                            style = null;
                        }
                        if (spaces > 0)
                        {
                            if (style != null && style.SymbolFont == null) text.Append(' ', spaces);
                            else { Flush(); style = g.Style.SymbolFont == null ? g.Style : prevInk.Style; text.Append(' ', spaces); }
                        }
                    }
                    pendingSpace = false;

                    int ul = UnderscoreRun(inkList, inkIndex);
                    if (ul >= MinUnderscores)
                    {
                        Flush();
                        sb.Append("<w:r>").Append(RunProps(g.Style)).Append("<w:tab/></w:r>");
                        style = null;
                        skipUntil = inkIndex + ul - 1;
                        prevInk = g;
                        continue;
                    }

                    if (g.Style.SymbolFont != null)
                    {
                        Flush();
                        WriteSymbol(sb, g);
                        style = null;
                    }
                    else
                    {
                        if (style != null && !SameStyle(style, g.Style)) Flush();
                        style = g.Style;
                        text.Append(g.Text);
                    }
                    prevInk = g;
                }
                Flush();
            }

            private static bool SameStyle(TextStyle a, TextStyle b) =>
                a.Font == b.Font && Math.Abs(a.Size - b.Size) < 0.3 && a.Bold == b.Bold && a.Italic == b.Italic && a.Color == b.Color && a.Underline == b.Underline;

            private static string RunProps(TextStyle s, string? fontOverride = null)
            {
                var f = X(fontOverride ?? s.Font);
                var sb = new StringBuilder("<w:rPr>");
                sb.Append($"<w:rFonts w:ascii=\"{f}\" w:hAnsi=\"{f}\" w:cs=\"{f}\"/>");
                if (s.Bold) sb.Append("<w:b/><w:bCs/>");
                if (s.Italic) sb.Append("<w:i/><w:iCs/>");
                if (s.Underline) sb.Append("<w:u w:val=\"single\"/>");
                if (s.Color != null) sb.Append($"<w:color w:val=\"{s.Color}\"/>");
                sb.Append($"<w:sz w:val=\"{HalfPt(s.Size)}\"/><w:szCs w:val=\"{HalfPt(s.Size)}\"/>");
                sb.Append("</w:rPr>");
                return sb.ToString();
            }

            private static void WriteRun(StringBuilder sb, string text, TextStyle style)
            {
                sb.Append("<w:r>").Append(RunProps(style)).Append("<w:t xml:space=\"preserve\">").Append(X(text)).Append("</w:t></w:r>");
            }

            private static void WriteSymbol(StringBuilder sb, Glyph g)
            {
                char c = g.Text.Length > 0 ? g.Text[0] : ' ';
                if (c >= 0xF000 && c <= 0xF0FF || c < 0x100)
                {
                    int code = c < 0x100 ? 0xF000 + c : c;
                    sb.Append("<w:r>").Append(RunProps(g.Style, g.Style.SymbolFont))
                      .Append($"<w:sym w:font=\"{X(g.Style.SymbolFont!)}\" w:char=\"{code:X4}\"/></w:r>");
                }
                else
                {
                    // Already a real Unicode character (•, ☐, →…): write it with a font that has it.
                    var st = g.Style with { SymbolFont = null, Font = "Segoe UI Symbol" };
                    WriteRun(sb, g.Text, st);
                }
            }

            private void WriteFigure(StringBuilder sb, Figure f, double gapBefore)
            {
                if (f.OcrBlocks is { Count: > 0 })
                {
                    // A picture that is really text (a scanned paragraph, a screenshot of text): write the text.
                    bool first = true;
                    foreach (var b in f.OcrBlocks)
                    {
                        if (b.Text.Length == 0) continue;
                        double size = Math.Clamp(f.OcrFontSize > 0 ? f.OcrFontSize : _m.BodySize, 6, 28) * (b.Level == 1 ? 1.4 : b.Level == 2 ? 1.2 : 1);
                        double before = first ? Math.Min(gapBefore, 300) : size * 0.4;
                        first = false;
                        sb.Append($"<w:p><w:pPr><w:spacing w:before=\"{T(before)}\" w:after=\"0\"/>");
                        double ocrIndent = f.Left - _boxL;
                        if (ocrIndent > 0.5) sb.Append($"<w:ind w:left=\"{T(ocrIndent)}\"/>");
                        sb.Append("</w:pPr><w:r><w:rPr>").Append(b.Level > 0 ? "<w:b/>" : "")
                          .Append($"<w:sz w:val=\"{HalfPt(size)}\"/><w:szCs w:val=\"{HalfPt(size)}\"/></w:rPr><w:t xml:space=\"preserve\">{X(b.Text)}</w:t></w:r></w:p>");
                    }
                    return;
                }
                if (f.Png == null) return;
                sb.Append("<w:p><w:pPr>");
                sb.Append($"<w:spacing w:before=\"{T(Math.Min(gapBefore, 300))}\" w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/>");
                double indent = f.Left - _boxL;
                if (Math.Abs(indent) > 0.5) sb.Append($"<w:ind w:left=\"{T(indent)}\"/>");
                sb.Append("</w:pPr>");
                sb.Append(Drawing(AddImage(f.Png), f.Right - f.Left, f.Top - f.Bottom, f.AltText));
                sb.Append("</w:p>");
            }

            private string Drawing(string relId, double wPt, double hPt, string? altText = null)
            {
                int id = _docPrId++;
                string descr = string.IsNullOrWhiteSpace(altText) ? "" : $" descr=\"{X(altText!.Length > 2000 ? altText[..2000] : altText)}\"";
                return "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
                       $"<wp:extent cx=\"{E(wPt)}\" cy=\"{E(hPt)}\"/><wp:docPr id=\"{id}\" name=\"Imagen {id}\"{descr}/>" +
                       "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                       "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                       $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"Imagen {id}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                       $"<pic:blipFill><a:blip r:embed=\"{relId}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                       $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{E(wPt)}\" cy=\"{E(hPt)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
                       "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";
            }

            private string BandXml(string kind, Band band)
            {
                string prefix = kind == "hdr" ? "rIdImgH" : "rIdImgF";
                var id = prefix + (_media.Count + 1);
                _media.Add((id, band.Png!));
                string root = kind == "hdr" ? "w:hdr" : "w:ftr";
                string open = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><{root} xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">";

                if (kind == "hdr" && band.PageNumberStart != null && band.NumberStyle != null)
                {
                    // Picture placed exactly where it was on the page, and Word's own page number where the PDF had it.
                    double headerDist = Math.Max(10, _m.PageHeight - band.Top);
                    double numTopFromPage = _m.PageHeight - band.NumTop;
                    double before = Math.Max(0, numTopFromPage - headerDist - band.NumberStyle.Size * 0.15);
                    var st = band.NumberStyle;
                    string rpr = RunProps(st);
                    return open +
                           $"<w:p><w:pPr><w:spacing w:before=\"{T(before)}\" w:after=\"0\"/><w:jc w:val=\"right\"/><w:ind w:right=\"{T(_m.ContentRight - band.NumRight)}\"/></w:pPr>" +
                           AnchoredDrawing(id, band.Left, _m.PageHeight - band.Top, band.Right - band.Left, band.Top - band.Bottom) +
                           $"<w:r>{rpr}<w:fldChar w:fldCharType=\"begin\"/></w:r><w:r>{rpr}<w:instrText xml:space=\"preserve\"> PAGE </w:instrText></w:r>" +
                           $"<w:r>{rpr}<w:fldChar w:fldCharType=\"separate\"/></w:r><w:r>{rpr}<w:t>{band.PageNumberStart}</w:t></w:r><w:r>{rpr}<w:fldChar w:fldCharType=\"end\"/></w:r>" +
                           $"</w:p></{root}>";
                }

                double indent = band.Left - _m.ContentLeft;
                return open +
                       $"<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\"/><w:ind w:left=\"{T(indent)}\" w:right=\"{T(Math.Min(0, _m.ContentRight - band.Right))}\"/></w:pPr>" +
                       Drawing(id, band.Right - band.Left, band.Top - band.Bottom) +
                       $"</w:p></{root}>";
            }

            /// <summary>Picture at a fixed position on the page (points from the page's top-left).</summary>
            private string AnchoredDrawing(string relId, double xPt, double yPt, double wPt, double hPt)
            {
                int id = _docPrId++;
                return "<w:r><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"251658240\" behindDoc=\"1\" locked=\"1\" layoutInCell=\"1\" allowOverlap=\"1\">" +
                       "<wp:simplePos x=\"0\" y=\"0\"/>" +
                       $"<wp:positionH relativeFrom=\"page\"><wp:posOffset>{E(xPt)}</wp:posOffset></wp:positionH>" +
                       $"<wp:positionV relativeFrom=\"page\"><wp:posOffset>{E(yPt)}</wp:posOffset></wp:positionV>" +
                       $"<wp:extent cx=\"{E(wPt)}\" cy=\"{E(hPt)}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/>" +
                       $"<wp:docPr id=\"{id}\" name=\"Encabezado {id}\"/><wp:cNvGraphicFramePr/>" +
                       "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                       "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                       $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"Encabezado {id}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                       $"<pic:blipFill><a:blip r:embed=\"{relId}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                       $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{E(wPt)}\" cy=\"{E(hPt)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
                       "</pic:pic></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>";
            }

            private void WriteRule(StringBuilder sb, Rule r, double gapBefore)
            {
                int sz = Math.Clamp((int)Math.Round(r.Thickness * 8), 2, 96);
                sb.Append("<w:p><w:pPr>");
                sb.Append("<w:pBdr><w:bottom w:val=\"single\" w:sz=\"" + sz + "\" w:space=\"0\" w:color=\"" + r.Color + "\"/></w:pBdr>");
                sb.Append($"<w:spacing w:before=\"{T(Math.Min(Math.Max(0, gapBefore - 2), 300))}\" w:after=\"0\" w:line=\"{T(2)}\" w:lineRule=\"exact\"/>");
                sb.Append($"<w:ind w:left=\"{T(Math.Max(0, r.Left - _boxL))}\" w:right=\"{T(Math.Max(0, _boxR - r.Right))}\"/>");
                sb.Append("<w:rPr><w:sz w:val=\"2\"/></w:rPr></w:pPr></w:p>");
            }

            private void WriteTable(StringBuilder sb, Table t, PageModel page)
            {
                sb.Append("<w:tbl><w:tblPr>");
                if (t.Floating)
                    sb.Append($"<w:tblpPr w:leftFromText=\"142\" w:rightFromText=\"142\" w:vertAnchor=\"page\" w:horzAnchor=\"page\" w:tblpX=\"{T(t.Left)}\" w:tblpY=\"{T(page.Height - t.Top)}\"/><w:tblOverlap w:val=\"never\"/>");
                sb.Append($"<w:tblW w:w=\"{T(t.Right - t.Left)}\" w:type=\"dxa\"/>");
                if (!t.Floating) sb.Append($"<w:tblInd w:w=\"{T(t.Left - _boxL)}\" w:type=\"dxa\"/>");
                sb.Append("<w:tblBorders><w:top w:val=\"nil\"/><w:left w:val=\"nil\"/><w:bottom w:val=\"nil\"/><w:right w:val=\"nil\"/><w:insideH w:val=\"nil\"/><w:insideV w:val=\"nil\"/></w:tblBorders>");
                sb.Append("<w:tblLayout w:type=\"fixed\"/>");
                sb.Append("<w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"40\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"40\" w:type=\"dxa\"/></w:tblCellMar>");
                sb.Append("<w:tblLook w:val=\"0000\"/></w:tblPr><w:tblGrid>");
                for (int c = 0; c + 1 < t.Xs.Length; c++) sb.Append($"<w:gridCol w:w=\"{T(t.Xs[c + 1] - t.Xs[c])}\"/>");
                sb.Append("</w:tblGrid>");

                int nr = t.Ys.Length - 1, nc = t.Xs.Length - 1;
                var owner = new Cell?[nr, nc];
                foreach (var cell in t.Cells)
                    for (int r = cell.Row; r < cell.Row + cell.RowSpan; r++)
                        for (int c = cell.Col; c < cell.Col + cell.ColSpan; c++)
                            owner[r, c] = cell;

                for (int r = 0; r < nr; r++)
                {
                    double h = t.Ys[r] - t.Ys[r + 1];
                    bool text = t.RowHasText.Length > r && t.RowHasText[r];
                    string rule = !text ? "exact" : "atLeast";
                    sb.Append($"<w:tr><w:trPr><w:trHeight w:val=\"{T(Math.Max(h, 0.5))}\" w:hRule=\"{rule}\"/></w:trPr>");
                    for (int c = 0; c < nc;)
                    {
                        var cell = owner[r, c];
                        if (cell == null) { c++; continue; }
                        bool start = cell.Row == r;
                        sb.Append("<w:tc><w:tcPr>");
                        sb.Append($"<w:tcW w:w=\"{T(cell.Right - cell.Left)}\" w:type=\"dxa\"/>");
                        if (cell.ColSpan > 1) sb.Append($"<w:gridSpan w:val=\"{cell.ColSpan}\"/>");
                        if (cell.RowSpan > 1) sb.Append(start ? "<w:vMerge w:val=\"restart\"/>" : "<w:vMerge/>");
                        sb.Append("<w:tcBorders>");
                        sb.Append(BorderXml("top", cell.BTop)).Append(BorderXml("left", cell.BLeft))
                          .Append(BorderXml("bottom", cell.BBottom)).Append(BorderXml("right", cell.BRight));
                        sb.Append("</w:tcBorders>");
                        if (cell.Fill != null) sb.Append($"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{cell.Fill}\"/>");
                        sb.Append("<w:noWrap w:val=\"false\"/>");
                        sb.Append($"<w:vAlign w:val=\"{(cell.Paragraphs.Count <= 1 ? "center" : "top")}\"/>");
                        sb.Append("</w:tcPr>");

                        if (start && cell.Paragraphs.Count > 0)
                        {
                            double prevBottom = cell.Top - 1;
                            foreach (var p in cell.Paragraphs)
                            {
                                double gap = cell.Paragraphs[0] == p ? 0 : Math.Max(0, prevBottom - p.Top);
                                WriteParagraph(sb, p, cell.Left + 2, cell.Right - 2, gap);
                                prevBottom = p.Bottom;
                            }
                        }
                        else
                        {
                            sb.Append(text
                                ? "<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\"/></w:pPr></w:p>"
                                : "<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/><w:rPr><w:sz w:val=\"2\"/></w:rPr></w:pPr></w:p>");
                        }
                        sb.Append("</w:tc>");
                        c += cell.ColSpan;
                    }
                    sb.Append("</w:tr>");
                }
                sb.Append("</w:tbl>");
            }

            private static string BorderXml(string side, Border? b)
            {
                if (b == null) return $"<w:{side} w:val=\"nil\"/>";
                int sz = Math.Clamp((int)Math.Round(b.Width * 8), 2, 96);
                string color = b.Color == "000000" || b.Color.Length != 6 ? "auto" : b.Color;
                return $"<w:{side} w:val=\"single\" w:sz=\"{sz}\" w:space=\"0\" w:color=\"{color}\"/>";
            }
        }
    }
}
