using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace Gajo.Core
{
    /// <summary>PowerPoint without PowerPoint: text of PPTX slides, and PDF → PPTX (one page per slide).</summary>
    public static class Presentations
    {
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

        /// <summary>Slide texts as document blocks (title as heading, a page break between slides).</summary>
        public static List<Documents.Block> ReadPptxText(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            var slides = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
                .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value)).ToList();
            var blocks = new List<Documents.Block>();
            foreach (var e in slides)
            {
                XDocument doc;
                using (var s = e.Open()) doc = XDocument.Load(s);
                if (blocks.Count > 0) blocks.Add(new Documents.Block(-1, ""));
                foreach (var sp in doc.Descendants(P + "sp"))
                {
                    var ph = sp.Descendants(P + "ph").FirstOrDefault();
                    bool title = ph != null && ((string?)ph.Attribute("type")) is "title" or "ctrTitle";
                    foreach (var para in sp.Descendants(A + "p"))
                    {
                        var text = string.Concat(para.Descendants(A + "t").Select(t => t.Value)).Trim();
                        if (text.Length == 0) continue;
                        bool bullet = !title && para.Element(A + "pPr")?.Element(A + "buNone") == null && ph != null && (string?)ph.Attribute("type") is null or "body";
                        blocks.Add(new Documents.Block(title ? 1 : 0, (bullet ? "• " : "") + text));
                    }
                }
            }
            if (blocks.Count == 0) throw new ConversionException("La presentación no tiene texto.");
            return blocks;
        }

        /// <summary>PDF → PPTX: every page becomes a slide with the page as a picture (exact look).</summary>
        public static async Task<string> PdfToPptxAsync(string pdf, string output, IPdfRegionRenderer renderer, IProgress<double>? progress, CancellationToken ct)
        {
            List<(double W, double H)> sizes;
            using (var doc = PdfDocument.Open(pdf)) sizes = doc.GetPages().Select(p => (p.Width, p.Height)).ToList();
            if (sizes.Count == 0) throw new ConversionException("El PDF no tiene páginas.");

            var images = new List<byte[]>();
            for (int i = 0; i < sizes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                images.Add(await renderer.RenderPngAsync(pdf, i + 1, 0, 0, sizes[i].W, sizes[i].H, 150, ct)
                           ?? throw new ConversionException($"No se pudo dibujar la página {i + 1}."));
                progress?.Report((i + 1.0) / sizes.Count * 0.9);
            }

            long E(double pt) => (long)Math.Round(pt * 12700);
            long cx = Math.Clamp(E(sizes[0].W), 914400, 51206400), cy = Math.Clamp(E(sizes[0].H), 914400, 51206400);
            const string NsP = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
            const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
            const string EmptyTree = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

            var files = new List<(string Name, byte[] Data)>();
            void Add(string name, string xml) => files.Add((name, new UTF8Encoding(false).GetBytes(xml)));

            var ct2 = new StringBuilder(Head + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/>" +
                "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
                "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
                "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
                "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
            var presRels = new StringBuilder(Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"theme/theme1.xml\"/>");
            var sldIds = new StringBuilder();

            for (int i = 0; i < images.Count; i++)
            {
                int n = i + 1;
                // Each page keeps its proportions inside the slide (centred).
                double scale = Math.Min(cx / (double)E(sizes[i].W), cy / (double)E(sizes[i].H));
                long w = (long)(E(sizes[i].W) * scale), h = (long)(E(sizes[i].H) * scale);
                long x = (cx - w) / 2, y = (cy - h) / 2;
                ct2.Append($"<Override PartName=\"/ppt/slides/slide{n}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
                presRels.Append($"<Relationship Id=\"rId{n + 2}\" Type=\"{Rel}/slide\" Target=\"slides/slide{n}.xml\"/>");
                sldIds.Append($"<p:sldId id=\"{255 + n}\" r:id=\"rId{n + 2}\"/>");
                Add($"ppt/slides/slide{n}.xml", Head + $"<p:sld {NsP}><p:cSld><p:spTree>{EmptyTree}" +
                    $"<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"Página {n}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
                    "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                    $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>" +
                    "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
                Add($"ppt/slides/_rels/slide{n}.xml.rels", Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/image\" Target=\"../media/image{n}.png\"/></Relationships>");
                files.Add(($"ppt/media/image{n}.png", images[i]));
            }
            ct2.Append("</Types>");
            presRels.Append("</Relationships>");

            Add("[Content_Types].xml", ct2.ToString());
            Add("_rels/.rels", Head + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>");
            Add("ppt/presentation.xml", Head + $"<p:presentation {NsP} saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
                $"<p:sldIdLst>{sldIds}</p:sldIdLst><p:sldSz cx=\"{cx}\" cy=\"{cy}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
            Add("ppt/_rels/presentation.xml.rels", presRels.ToString());
            Add("ppt/slideMasters/slideMaster1.xml", Head + $"<p:sldMaster {NsP}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{EmptyTree}</p:spTree></p:cSld>" +
                "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
                "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>");
            Add("ppt/slideMasters/_rels/slideMaster1.xml.rels", Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"../theme/theme1.xml\"/></Relationships>");
            Add("ppt/slideLayouts/slideLayout1.xml", Head + $"<p:sldLayout {NsP} type=\"blank\" preserve=\"1\"><p:cSld name=\"En blanco\"><p:spTree>{EmptyTree}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
            Add("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Head + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
            Add("ppt/theme/theme1.xml", Head + Theme);

            await Task.Run(() =>
            {
                using var fs = File.Create(output);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                foreach (var (name, data) in files)
                {
                    var e = zip.CreateEntry(name, name.EndsWith(".png") ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using var s = e.Open();
                    s.Write(data, 0, data.Length);
                }
            }, ct);
            return output;
        }

        private const string Theme =
            "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Gajo\"><a:themeElements>" +
            "<a:clrScheme name=\"Gajo\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
            "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"F05524\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2>" +
            "<a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
            "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
            "<a:fontScheme name=\"Gajo\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
            "<a:fmtScheme name=\"Gajo\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
            "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
            "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
            "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>" +
            "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }

    /// <summary>PDF → XLSX: tables become cell grids, the rest of the text one row per paragraph; one sheet per page.</summary>
    public static class PdfToXlsx
    {
        public static void Convert(string pdf, string output)
        {
            var model = Layout.PdfLayoutAnalyzer.Analyze(pdf);
            var sheets = new List<Spreadsheets.Sheet>();
            foreach (var page in model.Pages)
            {
                var sheet = new Spreadsheets.Sheet { Name = model.Pages.Count == 1 ? "Hoja1" : $"Página {page.Number}" };
                foreach (var el in page.Elements.SelectMany(e => e is Layout.ColumnBlock cb ? cb.LeftItems.Concat(cb.RightItems) : new[] { e }))
                {
                    switch (el)
                    {
                        case Layout.Table t:
                        {
                            int rows = t.Ys.Length - 1, cols = t.Xs.Length - 1;
                            // Skip empty grid rows/columns (spacing lines of the PDF).
                            var usedCols = Enumerable.Range(0, cols).Where(c => t.Cells.Any(cell => cell.Col == c && cell.Paragraphs.Count > 0)).ToList();
                            for (int r = 0; r < rows; r++)
                            {
                                var cellsInRow = t.Cells.Where(c => c.Row == r && c.Paragraphs.Count > 0).ToList();
                                if (cellsInRow.Count == 0) continue;
                                var row = new List<object?>(new object?[Math.Max(1, usedCols.Count)]);
                                foreach (var cell in cellsInRow)
                                {
                                    int idx = usedCols.IndexOf(cell.Col);
                                    if (idx < 0) idx = usedCols.Count(c => c < cell.Col);
                                    var text = string.Join("\n", cell.Paragraphs.Select(p => string.Join(" ", p.Lines.Select(l => l.Text.Trim()))));
                                    row[Math.Min(idx, row.Count - 1)] = TypedValue(text);
                                }
                                sheet.Rows.Add(row);
                            }
                            sheet.Rows.Add(new List<object?>());
                            break;
                        }
                        case Layout.Paragraph p:
                            sheet.Rows.Add(new List<object?> { string.Join(" ", p.Lines.Select(l => l.Text.Trim())) });
                            break;
                    }
                }
                sheets.Add(sheet);
            }
            if (sheets.All(s => s.Rows.Count == 0)) throw new ConversionException("El PDF no contiene texto ni tablas (¿es escaneado?).");
            Spreadsheets.Write(sheets, output, "xlsx");
        }

        /// <summary>"1.234,56", "1,234.56", "12 %" → numbers; everything else stays text.</summary>
        private static object TypedValue(string text)
        {
            var t = text.Trim().Replace(" ", "").Replace(" ", "");
            if (Regex.IsMatch(t, @"^-?\d{1,3}(\.\d{3})+(,\d+)?$") || Regex.IsMatch(t, @"^-?\d+,\d+$"))
                if (double.TryParse(t.Replace(".", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var es)) return es;
            if (Regex.IsMatch(t, @"^-?\d{1,3}(,\d{3})+(\.\d+)?$") || Regex.IsMatch(t, @"^-?\d+(\.\d+)?$"))
                if (double.TryParse(t.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var en)) return en;
            return text;
        }
    }
}
