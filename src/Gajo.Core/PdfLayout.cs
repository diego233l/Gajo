using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;

#pragma warning disable CS0618 // PdfPig marks some stable accessors obsolete in favour of renamed ones.

namespace Gajo.Core.Layout
{
    // ===================================================================== model
    // Coordinates are PDF points with the origin at the bottom-left of the page (y grows upwards).

    public sealed record TextStyle(string Font, double Size, bool Bold, bool Italic, string? Color, string? SymbolFont, bool Underline = false);

    public sealed class Glyph
    {
        public string Text = "";
        public bool IsSpace;
        public double Left, Right, Bottom, Top, Baseline, Size;
        public double AdvLeft, AdvRight;   // advance box (where the pen starts and ends), for word spacing
        public TextStyle Style = null!;
        public double CenterX => (Left + Right) / 2;
        public double CenterY => (Bottom + Top) / 2;
    }

    public sealed class TextLine
    {
        public List<Glyph> Glyphs = new();
        public double Left, Right, Top, Bottom, Baseline, Size;
        public string Text => string.Concat(Glyphs.Select(g => g.Text));

        public void Recompute()
        {
            var ink = Glyphs.Where(g => !g.IsSpace).ToList();
            if (ink.Count == 0) ink = Glyphs;
            Left = ink.Min(g => g.Left); Right = ink.Max(g => g.Right);
            Top = ink.Max(g => g.Top); Bottom = ink.Min(g => g.Bottom);
            Baseline = ink.Average(g => g.Baseline);
            var sizes = ink.Select(g => g.Size).OrderBy(s => s).ToList();
            Size = sizes[sizes.Count / 2];
        }
    }

    public enum Align { Left, Center, Right, Justify }

    public abstract class Element
    {
        public double Left, Right, Top, Bottom;
    }

    public sealed class Paragraph : Element
    {
        public List<TextLine> Lines = new();
        public Align Align;
        public bool KeepBreaks;
        public double IndentLeft;        // pt from the box's left edge
        public double FirstLineOffset;   // pt, first line relative to the others
        public double LinePitch;         // pt between baselines (0 = single line)
        public double Size => Lines.Count == 0 ? 10 : Lines.Select(l => l.Size).Max();
    }

    public sealed record Border(double Width, string Color);

    public sealed class Cell
    {
        public int Row, Col, RowSpan = 1, ColSpan = 1;
        public double Left, Right, Top, Bottom;
        public Border? BTop, BBottom, BLeft, BRight;
        public string? Fill;
        public List<Paragraph> Paragraphs = new();
    }

    public sealed class Table : Element
    {
        public double[] Xs = Array.Empty<double>();   // column boundaries, left → right
        public double[] Ys = Array.Empty<double>();   // row boundaries, top → bottom (descending)
        public List<Cell> Cells = new();
        public bool Floating;
        public bool[] RowHasText = Array.Empty<bool>();
    }

    public sealed class Figure : Element
    {
        public byte[]? Png;
        /// <summary>Contains a photo/scan (raster image), so it may hold text worth reading with OCR.</summary>
        public bool IsRaster;
        /// <summary>Text recognised in the picture: replaces it when the picture is mostly text.</summary>
        public List<Documents.Block>? OcrBlocks;
        public double OcrFontSize;
        /// <summary>Recognised text kept as the picture's description (alt text) when it is a photo with some text.</summary>
        public string? AltText;
    }

    /// <summary>Two-column stretch of a page (newspaper/article layout).</summary>
    public sealed class ColumnBlock : Element
    {
        public List<Element> LeftItems = new(), RightItems = new();
        public double LeftL, LeftR, RightL, RightR;
    }

    public sealed class Rule : Element
    {
        public double Thickness;
        public string Color = "000000";
    }

    public sealed class PageModel
    {
        public int Number;
        public double Width, Height;
        public List<Element> Elements = new();
        public bool NeedsOcr;
        public List<Documents.Block>? OcrBlocks;

        // Working data before paragraphs are built
        internal List<TextLine> BodyLines = new();
        internal List<Figure> Figures = new();
        internal List<Rule> Rules = new();
        internal List<Table> Tables = new();
    }

    public sealed class Band
    {
        public double Left, Right, Top, Bottom;
        public int PageNumber;
        public byte[]? Png;

        /// <summary>Page numbers printed in the band (e.g. 30709, 30710…): number shown on the first page.</summary>
        public int? PageNumberStart;
        public TextStyle? NumberStyle;
        public double NumLeft, NumRight, NumTop, NumBottom;
        /// <summary>Areas (PDF coords, page <see cref="PageNumber"/>) painted white in the picture: the page number.</summary>
        public List<(double Left, double Top, double Right, double Bottom)> Masks = new();
    }

    public sealed class DocModel
    {
        public List<PageModel> Pages = new();
        public double ContentLeft, ContentRight, MarginTop, MarginBottom;
        public double PageWidth = 595.28, PageHeight = 841.89;
        public Band? Header, Footer;
        public string BodyFont = "Calibri";
        public double BodySize = 11;
    }

    // ===================================================================== analysis

    /// <summary>Turns a PDF into a layout model: styled text, ruled tables, figures, rules and repeated header/footer.</summary>
    public static class PdfLayoutAnalyzer
    {
        private const double Thin = 2.2;     // max thickness of a ruling line
        private const double Snap = 1.6;     // positions closer than this are the same grid line

        private sealed class Seg
        {
            public bool H; public double Pos, A, B, Th; public string Color = "000000";
        }

        public static DocModel Analyze(string pdfPath, int minTextChars = 25)
        {
            var model = new DocModel();
            using var doc = PdfDocument.Open(pdfPath);
            foreach (var page in doc.GetPages())
            {
                var pm = new PageModel { Number = page.Number, Width = page.Width, Height = page.Height };
                model.PageWidth = page.Width; model.PageHeight = page.Height;
                var glyphs = ExtractGlyphs(page);
                if (glyphs.Count(g => !g.IsSpace) < minTextChars)
                {
                    pm.NeedsOcr = true;
                    model.Pages.Add(pm);
                    continue;
                }
                AnalyzePage(page, pm, glyphs);
                model.Pages.Add(pm);
            }

            DetectBands(model);
            FinishPages(model);
            return model;
        }

        // ------------------------------------------------------------------ glyphs

        private static List<Glyph> ExtractGlyphs(Page page)
        {
            var list = new List<Glyph>();
            foreach (var l in page.Letters)
            {
                if (l.TextOrientation != TextOrientation.Horizontal) continue;
                var size = l.PointSize > 0 ? l.PointSize : 10;
                var g = new Glyph
                {
                    Text = l.Value, Size = Math.Round(size * 2) / 2, Baseline = l.StartBaseLine.Y,
                    AdvLeft = Math.Min(l.StartBaseLine.X, l.EndBaseLine.X), AdvRight = Math.Max(l.StartBaseLine.X, l.EndBaseLine.X),
                };
                if (string.IsNullOrWhiteSpace(l.Value))
                {
                    g.IsSpace = true; g.Text = " ";
                    g.Left = Math.Min(l.StartBaseLine.X, l.EndBaseLine.X);
                    g.Right = Math.Max(l.StartBaseLine.X, l.EndBaseLine.X);
                    g.Bottom = g.Baseline - size * 0.2; g.Top = g.Baseline + size * 0.7;
                }
                else
                {
                    var bb = l.GlyphRectangle;
                    g.Left = bb.Left; g.Right = bb.Right;
                    g.Bottom = Math.Min(bb.Bottom, g.Baseline);
                    g.Top = Math.Max(bb.Top, g.Baseline + size * 0.5);
                    if (bb.Width <= 0.01) { g.Left = l.StartBaseLine.X; g.Right = l.EndBaseLine.X; }
                }
                g.Style = StyleOf(l, g.Size);
                list.Add(g);
            }
            return list;
        }

        private static readonly Dictionary<string, TextStyle> StyleCache = new();

        private static TextStyle StyleOf(Letter l, double size)
        {
            var raw = l.FontName ?? "";
            string color = ColorHex(l.Color) ?? "";
            bool boldFlag = false, italicFlag = false;
            try { boldFlag = l.FontDetails?.IsBold ?? false; italicFlag = l.FontDetails?.IsItalic ?? false; } catch { }
            var key = raw + "|" + size + "|" + color + "|" + boldFlag + italicFlag;
            lock (StyleCache)
            {
                if (StyleCache.TryGetValue(key, out var cached)) return cached;
                var name = Regex.Replace(raw, @"^[A-Z]{6}\+", "");
                bool bold = boldFlag || Regex.IsMatch(name, "Bold|Black|Heavy|Semibold|Demi|,B$", RegexOptions.IgnoreCase);
                bool italic = italicFlag || Regex.IsMatch(name, "Italic|Oblique|,I$", RegexOptions.IgnoreCase);
                string? symbol = SymbolFontName(name);
                var st = new TextStyle(FontFamily(name), size, bold, italic, color.Length == 0 ? null : color, symbol);
                StyleCache[key] = st;
                return st;
            }
        }

        private static string? SymbolFontName(string name)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains("wingdings")) return n.Contains("3") ? "Wingdings 3" : n.Contains("2") ? "Wingdings 2" : "Wingdings";
            if (n.Contains("webdings")) return "Webdings";
            if (n.Contains("zapfdingbats") || n.Contains("dingbats")) return "Wingdings";
            if (n.StartsWith("symbol")) return "Symbol";
            return null;
        }

        private static readonly (string Pattern, string Family)[] FontMap =
        {
            ("^Arial.*Narrow", "Arial Narrow"), ("^Arial", "Arial"), ("^Helvetica", "Arial"),
            ("^TimesNewRoman", "Times New Roman"), ("^Times", "Times New Roman"),
            ("^CourierNew", "Courier New"), ("^Courier", "Courier New"),
            ("^GillSans", "Gill Sans MT"), ("^TrebuchetMS", "Trebuchet MS"), ("^Verdana", "Verdana"),
            ("^Calibri", "Calibri"), ("^Cambria", "Cambria"), ("^Candara", "Candara"), ("^Consolas", "Consolas"),
            ("^Aptos", "Aptos"), ("^Georgia", "Georgia"), ("^Tahoma", "Tahoma"), ("^SegoeUI", "Segoe UI"),
            ("^Garamond|^AGaramond|^EBGaramond", "Garamond"), ("^BookAntiqua", "Book Antiqua"),
            ("^CenturyGothic", "Century Gothic"), ("^Century", "Century"), ("^FranklinGothic", "Franklin Gothic Book"),
            ("^PalatinoLinotype|^Palatino", "Palatino Linotype"), ("^LucidaSans", "Lucida Sans"),
            ("^MyriadPro|^Myriad", "Segoe UI"), ("^Liberation ?Sans|^Carlito", "Calibri"), ("^Caladea", "Cambria"),
            ("^LiberationSerif", "Times New Roman"), ("^LiberationMono", "Courier New"),
            ("^DejaVuSans", "Verdana"), ("^OpenSans", "Segoe UI"), ("^Roboto", "Segoe UI"),
            ("^Symbol", "Symbol"), ("^Wingdings", "Wingdings"),
        };

        private static string FontFamily(string name)
        {
            var bare = name.Split(',', '-')[0].Replace(" ", "");
            foreach (var (pattern, family) in FontMap)
                if (Regex.IsMatch(bare, pattern, RegexOptions.IgnoreCase)) return family;
            // CamelCase → spaced words, without PostScript suffixes.
            var s = Regex.Replace(bare, "(PS)?MT$", "");
            s = Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ").Trim();
            return s.Length == 0 ? "Calibri" : s;
        }

        private static string? ColorHex(IColor? c)
        {
            if (c == null) return null;
            try
            {
                var (r, g, b) = c.ToRGBValues();
                if (r < 0.18 && g < 0.18 && b < 0.18) return null;   // black → automatic
                return $"{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
            }
            catch { return null; }
        }

        private static bool IsWhite(IColor? c)
        {
            if (c == null) return true;
            try { var (r, g, b) = c.ToRGBValues(); return r > 0.97 && g > 0.97 && b > 0.97; } catch { return true; }
        }

        private static string FillHex(IColor? c)
        {
            try
            {
                var (r, g, b) = c!.ToRGBValues();
                return $"{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
            }
            catch { return "000000"; }
        }

        // ------------------------------------------------------------------ page

        private static void AnalyzePage(Page page, PageModel pm, List<Glyph> glyphs)
        {
            var segs = new List<Seg>();
            var fills = new List<(PdfRectangle R, string Color)>();
            var figureParts = new List<PdfRectangle>();

            foreach (var path in page.Paths)
            {
                if (path.IsClipping) continue;
                foreach (var sp in path)
                {
                    var bbn = sp.GetBoundingRectangle();
                    if (!bbn.HasValue) continue;
                    var bb = bbn.Value;
                    double w = bb.Width, h = bb.Height;
                    bool rectLike = sp.IsDrawnAsRectangle || IsAxisRect(sp);

                    if (path.IsFilled)
                    {
                        if (IsWhite(path.FillColor))
                        {
                            if (rectLike && w > 6 && h > 3) fills.Add((bb, "FFFFFF"));   // knock-out over a darker fill
                        }
                        else if (Math.Min(w, h) <= Thin && Math.Max(w, h) >= 2.5 && rectLike)
                            segs.Add(MakeSeg(bb, FillHex(path.FillColor)));
                        else if (rectLike && w > 6 && h > 3)
                            fills.Add((bb, FillHex(path.FillColor)));
                        else if (w > 0.3 || h > 0.3)
                            figureParts.Add(bb);
                    }
                    if (path.IsStroked && !IsWhite(path.StrokeColor))
                    {
                        string col = FillHex(path.StrokeColor);
                        double lw = Math.Max(0.25, path.LineWidth);
                        bool onlyLines = sp.Commands.All(c => c is PdfSubpath.Move || c is PdfSubpath.Line || c is PdfSubpath.Close);
                        if (onlyLines)
                        {
                            PdfPoint? start = null, cur = null;
                            foreach (var cmd in sp.Commands)
                            {
                                switch (cmd)
                                {
                                    case PdfSubpath.Move m: start = cur = m.Location; break;
                                    case PdfSubpath.Line ln: AddStroke(segs, figureParts, ln.From, ln.To, lw, col); cur = ln.To; break;
                                    case PdfSubpath.Close:
                                        if (cur.HasValue && start.HasValue) AddStroke(segs, figureParts, cur.Value, start.Value, lw, col);
                                        cur = start; break;
                                }
                            }
                        }
                        else figureParts.Add(bb);
                    }
                }
            }

            var rasters = new List<PdfRectangle>();
            foreach (var img in page.GetImages())
            {
                var b = img.Bounds;
                if (b.Width > 2 && b.Height > 2) { figureParts.Add(b); rasters.Add(b); }
            }

            // Text underlines (links, emphasis) are not table lines: drop them and underline the text instead.
            var hsRaw = MergeSegs(segs.Where(s => s.H).ToList());
            var vsRaw = segs.Where(s => !s.H).ToList();
            var underlines = new List<Seg>();
            foreach (var h in hsRaw)
            {
                if (h.Th > 1.2) continue;
                var above = glyphs.Where(g => !g.IsSpace && g.CenterX > h.A - 1 && g.CenterX < h.B + 1
                                              && g.Baseline - h.Pos > -0.5 && g.Baseline - h.Pos < g.Size * 0.55).ToList();
                if (above.Count == 0) continue;
                double covered = above.Sum(g => g.Right - g.Left);
                bool endsOnVertical = vsRaw.Any(v => (Math.Abs(v.Pos - h.A) < 2.5 || Math.Abs(v.Pos - h.B) < 2.5) && v.A <= h.Pos + 2 && v.B >= h.Pos - 2);
                // Text sitting right on the line (≤1.6 pt) is underlined even if the line touches cell borders.
                bool tight = above.All(g => g.Baseline - h.Pos < 1.6);
                if ((tight || !endsOnVertical) && covered > (h.B - h.A) * 0.5)
                {
                    underlines.Add(h);
                    foreach (var g in above) g.Style = g.Style with { Underline = true };
                }
            }
            segs.RemoveAll(sg => sg.H && underlines.Any(u => Math.Abs(u.Pos - sg.Pos) <= 1.0 && sg.A >= u.A - 1.5 && sg.B <= u.B + 1.5));

            // Merge collinear pieces, then group connected lines.
            var hs = MergeSegs(segs.Where(s => s.H).ToList());
            var vs = MergeSegs(segs.Where(s => !s.H).ToList());
            var all = hs.Concat(vs).ToList();
            var parent = Enumerable.Range(0, all.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                    if (Touch(all[i], all[j])) parent[Find(i)] = Find(j);

            var comps = all.Select((s, i) => (s, i)).GroupBy(t => Find(t.i)).Select(g => g.Select(t => t.s).ToList()).ToList();
            (double l, double r, double t, double b) Box(List<Seg> c) =>
                (c.Min(s => s.H ? s.A : s.Pos), c.Max(s => s.H ? s.B : s.Pos), c.Max(s => s.H ? s.Pos : s.B), c.Min(s => s.H ? s.Pos : s.A));
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < comps.Count && !merged; i++)
                    for (int j = 0; j < comps.Count; j++)
                    {
                        if (i == j) continue;
                        var a = Box(comps[i]); var o = Box(comps[j]);
                        bool inside = a.l >= o.l - 2 && a.r <= o.r + 2 && a.t <= o.t + 2 && a.b >= o.b - 2;
                        if (inside && o.r - o.l > 25 && o.t - o.b > 8 && comps[j].Any(s => !s.H))
                        {
                            comps[j].AddRange(comps[i]);
                            comps.RemoveAt(i);
                            merged = true;
                            break;
                        }
                    }
            }

            foreach (var comp in comps)
            {
                var ch = comp.Where(s => s.H).ToList();
                var cv = comp.Where(s => !s.H).ToList();
                double l = comp.Min(s => s.H ? s.A : s.Pos - s.Th / 2), r = comp.Max(s => s.H ? s.B : s.Pos + s.Th / 2);
                double t = comp.Max(s => s.H ? s.Pos + s.Th / 2 : s.B), b = comp.Min(s => s.H ? s.Pos - s.Th / 2 : s.A);
                int distinctH = Cluster(ch.Select(s => s.Pos)).Count, distinctV = Cluster(cv.Select(s => s.Pos)).Count;

                if (distinctH >= 2 && distinctV >= 2 && r - l > 25 && t - b > 8)
                    pm.Tables.Add(BuildTable(ch, cv, l, r, t, b));
                else if (cv.Count == 0 && ch.Count > 0 && r - l > 30)
                {
                    foreach (var h in ch)
                        if (h.B - h.A > 30) pm.Rules.Add(new Rule { Left = h.A, Right = h.B, Top = h.Pos + h.Th / 2, Bottom = h.Pos - h.Th / 2, Thickness = h.Th, Color = h.Color });
                }
                else
                {
                    figureParts.Add(new PdfRectangle(l, b, Math.Max(r, l + 0.5), Math.Max(t, b + 0.5)));
                }
            }

            // Cell shading
            foreach (var table in pm.Tables)
                foreach (var cell in table.Cells)
                {
                    double cx = (cell.Left + cell.Right) / 2, cy = (cell.Top + cell.Bottom) / 2;
                    var f = fills.LastOrDefault(fl => fl.R.Left - 1 <= cx && fl.R.Right + 1 >= cx && fl.R.Bottom - 1 <= cy && fl.R.Top + 1 >= cy);
                    if (f.Color != null && f.Color != "FFFFFF")
                    {
                        // A fill much larger than the cell is a background or a "border trick", not shading.
                        bool covers = f.R.Width <= (cell.Right - cell.Left) * 1.6 + 4 && f.R.Height <= (cell.Top - cell.Bottom) * 1.6 + 4;
                        if (covers) cell.Fill = f.Color;
                    }
                }

            // Figures: cluster the drawing pieces that are not inside tables.
            // Drawings are clustered; each photo stays a picture of its own (two photos stacked closely
            // are two pictures), absorbing only drawing pieces that overlap it.
            bool IsRasterRect(PdfRectangle r) => rasters.Any(rr => Math.Abs(rr.Left - r.Left) < 0.01 && Math.Abs(rr.Bottom - r.Bottom) < 0.01 && Math.Abs(rr.Width - r.Width) < 0.01);
            var parts = figureParts.Where(p => !pm.Tables.Any(t => Inside(p, t, 1))).ToList();
            var vectorRegions = ClusterRects(parts.Where(p => !IsRasterRect(p)).ToList(), 2.0);
            var regions = new List<PdfRectangle>();
            foreach (var r in ClusterRects(parts.Where(IsRasterRect).ToList(), -0.5))
            {
                var box = r;
                foreach (var v in vectorRegions.ToList())
                    if (v.Left < box.Right && v.Right > box.Left && v.Bottom < box.Top && v.Top > box.Bottom)
                    {
                        box = new PdfRectangle(Math.Min(box.Left, v.Left), Math.Min(box.Bottom, v.Bottom), Math.Max(box.Right, v.Right), Math.Max(box.Top, v.Top));
                        vectorRegions.Remove(v);
                    }
                regions.Add(box);
            }
            regions.AddRange(vectorRegions);
            foreach (var region in regions)
            {
                if (region.Width < 4 || region.Height < 4 || region.Width * region.Height < 40) continue;
                int textInside = glyphs.Count(g => !g.IsSpace && g.CenterX > region.Left && g.CenterX < region.Right && g.CenterY > region.Bottom && g.CenterY < region.Top);
                if (textInside > 3) continue;   // never swallow real text into a picture
                bool raster = rasters.Any(rr => rr.Centroid.X > region.Left && rr.Centroid.X < region.Right && rr.Centroid.Y > region.Bottom && rr.Centroid.Y < region.Top);
                pm.Figures.Add(new Figure { Left = region.Left, Right = region.Right, Top = region.Top, Bottom = region.Bottom, IsRaster = raster });
            }

            // Text: tables first, then figures, the rest is body text.
            var cellGlyphs = new Dictionary<Cell, List<Glyph>>();
            var free = new List<Glyph>();
            foreach (var g in glyphs)
            {
                var table = pm.Tables.FirstOrDefault(t => g.CenterX > t.Left && g.CenterX < t.Right && g.CenterY < t.Top && g.CenterY > t.Bottom);
                if (table != null)
                {
                    var cell = table.Cells.FirstOrDefault(c => g.CenterX >= c.Left && g.CenterX <= c.Right && g.CenterY <= c.Top && g.CenterY >= c.Bottom);
                    if (cell != null)
                    {
                        if (!cellGlyphs.TryGetValue(cell, out var cl)) cellGlyphs[cell] = cl = new List<Glyph>();
                        cl.Add(g);
                        continue;
                    }
                }
                if (pm.Figures.Any(f => g.CenterX > f.Left && g.CenterX < f.Right && g.CenterY > f.Bottom && g.CenterY < f.Top)) continue;
                free.Add(g);
            }

            foreach (var table in pm.Tables)
            {
                var rowText = new bool[table.Ys.Length - 1];
                foreach (var cell in table.Cells)
                {
                    if (!cellGlyphs.TryGetValue(cell, out var cg)) continue;
                    var lines = BuildLines(cg);
                    cell.Paragraphs = BuildParagraphs(lines, cell.Left + 2, cell.Right - 2);
                    if (lines.Count > 0)
                        for (int r = cell.Row; r < cell.Row + cell.RowSpan; r++)
                            if (lines.Any(ln => ln.Baseline < table.Ys[r] && ln.Baseline > table.Ys[r + 1])) rowText[r] = true;
                }
                table.RowHasText = rowText;
            }

            pm.BodyLines = BuildLines(free, splitGap: page.Width * 0.22);   // only real columns split a line
        }

        private static bool IsAxisRect(PdfSubpath sp)
        {
            var pts = new List<PdfPoint>();
            foreach (var c in sp.Commands)
            {
                if (c is PdfSubpath.Move m) pts.Add(m.Location);
                else if (c is PdfSubpath.Line l) pts.Add(l.To);
                else if (c is PdfSubpath.Close) { }
                else return false;
            }
            if (pts.Count < 4 || pts.Count > 5) return false;
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1]; var b = pts[i];
                if (Math.Abs(a.X - b.X) > 0.3 && Math.Abs(a.Y - b.Y) > 0.3) return false;
            }
            return true;
        }

        private static Seg MakeSeg(PdfRectangle bb, string color)
        {
            bool h = bb.Width >= bb.Height;
            return h
                ? new Seg { H = true, Pos = (bb.Top + bb.Bottom) / 2, A = bb.Left, B = bb.Right, Th = Math.Max(0.25, bb.Height), Color = color }
                : new Seg { H = false, Pos = (bb.Left + bb.Right) / 2, A = bb.Bottom, B = bb.Top, Th = Math.Max(0.25, bb.Width), Color = color };
        }

        private static void AddStroke(List<Seg> segs, List<PdfRectangle> figure, PdfPoint a, PdfPoint b, double lw, string color)
        {
            if (Math.Abs(a.Y - b.Y) < 0.6 && Math.Abs(a.X - b.X) >= 2.5)
                segs.Add(new Seg { H = true, Pos = (a.Y + b.Y) / 2, A = Math.Min(a.X, b.X), B = Math.Max(a.X, b.X), Th = lw, Color = color });
            else if (Math.Abs(a.X - b.X) < 0.6 && Math.Abs(a.Y - b.Y) >= 2.5)
                segs.Add(new Seg { H = false, Pos = (a.X + b.X) / 2, A = Math.Min(a.Y, b.Y), B = Math.Max(a.Y, b.Y), Th = lw, Color = color });
            else
                figure.Add(new PdfRectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 0.5, Math.Max(a.Y, b.Y) + 0.5));
        }

        private static List<Seg> MergeSegs(List<Seg> segs)
        {
            var result = new List<Seg>();
            foreach (var s in segs.OrderBy(s => s.Pos).ThenBy(s => s.A))
            {
                var m = result.LastOrDefault(r => Math.Abs(r.Pos - s.Pos) <= 1.0 && s.A <= r.B + 1.5 && s.B >= r.A - 1.5);
                if (m != null)
                {
                    m.A = Math.Min(m.A, s.A); m.B = Math.Max(m.B, s.B);
                    m.Th = Math.Max(m.Th, s.Th);
                }
                else result.Add(new Seg { H = s.H, Pos = s.Pos, A = s.A, B = s.B, Th = s.Th, Color = s.Color });
            }
            return result;
        }

        private static bool Touch(Seg a, Seg b)
        {
            const double tol = 2.0;
            if (a.H == b.H)
                return Math.Abs(a.Pos - b.Pos) <= 1.0 && a.A <= b.B + tol && b.A <= a.B + tol;
            var h = a.H ? a : b; var v = a.H ? b : a;
            return v.Pos >= h.A - tol && v.Pos <= h.B + tol && h.Pos >= v.A - tol && h.Pos <= v.B + tol;
        }

        private static List<double> Cluster(IEnumerable<double> values, double tol = Snap)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var result = new List<double>();
            var group = new List<double>();
            foreach (var v in sorted)
            {
                if (group.Count > 0 && v - group[^1] > tol) { result.Add(group.Average()); group.Clear(); }
                group.Add(v);
            }
            if (group.Count > 0) result.Add(group.Average());
            return result;
        }

        private static bool Inside(PdfRectangle p, Element e, double tol) =>
            p.Left >= e.Left - tol && p.Right <= e.Right + tol && p.Bottom >= e.Bottom - tol && p.Top <= e.Top + tol;

        private static List<PdfRectangle> ClusterRects(List<PdfRectangle> rects, double gap)
        {
            var boxes = rects.Select(r => (L: r.Left, R: r.Right, B: r.Bottom, T: r.Top)).ToList();
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < boxes.Count && !changed; i++)
                    for (int j = i + 1; j < boxes.Count; j++)
                    {
                        var a = boxes[i]; var b = boxes[j];
                        if (a.L <= b.R + gap && b.L <= a.R + gap && a.B <= b.T + gap && b.B <= a.T + gap)
                        {
                            boxes[i] = (Math.Min(a.L, b.L), Math.Max(a.R, b.R), Math.Min(a.B, b.B), Math.Max(a.T, b.T));
                            boxes.RemoveAt(j);
                            changed = true;
                            break;
                        }
                    }
            }
            return boxes.Select(b => new PdfRectangle(b.L, b.B, b.R, b.T)).ToList();
        }

        // ------------------------------------------------------------------ tables

        private static Table BuildTable(List<Seg> hs, List<Seg> vs, double l, double r, double t, double b)
        {
            var xs = Cluster(vs.Select(v => v.Pos));
            var ys = Cluster(hs.Select(h => h.Pos));
            if (xs[0] - l > 3) xs.Insert(0, l);
            if (r - xs[^1] > 3) xs.Add(r);
            if (ys[0] - b > 3) ys.Insert(0, b);
            if (t - ys[^1] > 3) ys.Add(t);
            if (xs.Count > 62) xs = Cluster(xs, 5);
            ys.Reverse();   // top → bottom

            var table = new Table { Left = xs[0], Right = xs[^1], Top = ys[0], Bottom = ys[^1], Xs = xs.ToArray(), Ys = ys.ToArray() };
            int nr = ys.Count - 1, nc = xs.Count - 1;

            Seg? VAt(double x, double y0, double y1)
            {
                double mid = (y0 + y1) / 2;
                return vs.FirstOrDefault(v => Math.Abs(v.Pos - x) <= Snap + 0.5 && v.A <= mid + 0.5 && v.B >= mid - 0.5);
            }
            Seg? HAt(double y, double x0, double x1)
            {
                double mid = (x0 + x1) / 2;
                return hs.FirstOrDefault(h => Math.Abs(h.Pos - y) <= Snap + 0.5 && h.A <= mid + 0.5 && h.B >= mid - 0.5);
            }

            // Union grid cells that are not separated by a line.
            var id = new int[nr, nc];
            var par = Enumerable.Range(0, nr * nc).ToArray();
            int F(int i) { while (par[i] != i) i = par[i] = par[par[i]]; return i; }
            for (int i = 0; i < nr; i++)
                for (int j = 0; j < nc; j++)
                {
                    if (j + 1 < nc && VAt(xs[j + 1], ys[i], ys[i + 1]) == null) par[F(i * nc + j)] = F(i * nc + j + 1);
                    if (i + 1 < nr && HAt(ys[i + 1], xs[j], xs[j + 1]) == null) par[F(i * nc + j)] = F((i + 1) * nc + j);
                }

            // Each group must be a rectangle for Word; otherwise fall back to horizontal merges only.
            var groups = new Dictionary<int, (int r0, int c0, int r1, int c1, int count)>();
            for (int i = 0; i < nr; i++)
                for (int j = 0; j < nc; j++)
                {
                    int g = F(i * nc + j);
                    groups[g] = groups.TryGetValue(g, out var e)
                        ? (Math.Min(e.r0, i), Math.Min(e.c0, j), Math.Max(e.r1, i), Math.Max(e.c1, j), e.count + 1)
                        : (i, j, i, j, 1);
                }
            bool allRect = groups.Values.All(e => (e.r1 - e.r0 + 1) * (e.c1 - e.c0 + 1) == e.count);
            if (!allRect)
            {
                par = Enumerable.Range(0, nr * nc).ToArray();
                for (int i = 0; i < nr; i++)
                    for (int j = 0; j + 1 < nc; j++)
                        if (VAt(xs[j + 1], ys[i], ys[i + 1]) == null) par[F(i * nc + j)] = F(i * nc + j + 1);
                groups.Clear();
                for (int i = 0; i < nr; i++)
                    for (int j = 0; j < nc; j++)
                    {
                        int g = F(i * nc + j);
                        groups[g] = groups.TryGetValue(g, out var e)
                            ? (Math.Min(e.r0, i), Math.Min(e.c0, j), Math.Max(e.r1, i), Math.Max(e.c1, j), e.count + 1)
                            : (i, j, i, j, 1);
                    }
            }

            foreach (var e in groups.Values.OrderBy(e => e.r0).ThenBy(e => e.c0))
            {
                var cell = new Cell
                {
                    Row = e.r0, Col = e.c0, RowSpan = e.r1 - e.r0 + 1, ColSpan = e.c1 - e.c0 + 1,
                    Left = xs[e.c0], Right = xs[e.c1 + 1], Top = ys[e.r0], Bottom = ys[e.r1 + 1],
                };
                var top = HAt(cell.Top, cell.Left, cell.Right);
                var bottom = HAt(cell.Bottom, cell.Left, cell.Right);
                var left = VAt(cell.Left, cell.Top, cell.Bottom);
                var right = VAt(cell.Right, cell.Top, cell.Bottom);
                if (top != null) cell.BTop = new Border(top.Th, top.Color);
                if (bottom != null) cell.BBottom = new Border(bottom.Th, bottom.Color);
                if (left != null) cell.BLeft = new Border(left.Th, left.Color);
                if (right != null) cell.BRight = new Border(right.Th, right.Color);
                table.Cells.Add(cell);
            }
            return table;
        }

        // ------------------------------------------------------------------ lines & paragraphs

        public static List<TextLine> BuildLines(List<Glyph> glyphs, double splitGap = double.MaxValue)
        {
            var lines = new List<TextLine>();
            foreach (var g in glyphs.OrderByDescending(g => g.Baseline).ThenBy(g => g.Left))
            {
                var line = lines.LastOrDefault(l => Math.Abs(l.Baseline - g.Baseline) <= Math.Max(l.Size, g.Size) * 0.35);
                if (line == null)
                {
                    line = new TextLine { Baseline = g.Baseline, Size = g.Size };
                    lines.Add(line);
                }
                line.Glyphs.Add(g);
                if (!g.IsSpace) line.Size = Math.Max(line.Size, g.Size);
            }

            var result = new List<TextLine>();
            foreach (var line in lines)
            {
                var sorted = line.Glyphs.OrderBy(g => g.Left).ToList();
                // Split far-apart chunks (columns) into separate lines.
                var chunk = new List<Glyph>();
                double lastRight = double.NaN;
                foreach (var g in sorted)
                {
                    if (!g.IsSpace && chunk.Count > 0 && !double.IsNaN(lastRight) && g.Left - lastRight > splitGap)
                    {
                        AddLine(result, chunk);
                        chunk = new List<Glyph>();
                    }
                    chunk.Add(g);
                    if (!g.IsSpace) lastRight = g.Right;
                }
                AddLine(result, chunk);
            }
            return result.OrderByDescending(l => l.Baseline).ThenBy(l => l.Left).ToList();
        }

        private static void AddLine(List<TextLine> result, List<Glyph> chunk)
        {
            // Trim spaces at both ends.
            int a = 0, b = chunk.Count - 1;
            while (a <= b && chunk[a].IsSpace) a++;
            while (b >= a && chunk[b].IsSpace) b--;
            if (a > b) return;
            var line = new TextLine { Glyphs = chunk.GetRange(a, b - a + 1) };
            line.Recompute();
            result.Add(line);
        }

        public static List<Paragraph> BuildParagraphs(List<TextLine> lines, double boxLeft, double boxRight)
        {
            var paras = new List<Paragraph>();
            double width = Math.Max(1, boxRight - boxLeft);
            double center = (boxLeft + boxRight) / 2;

            Align AlignOf(TextLine l)
            {
                double lw = l.Right - l.Left;
                bool nearLeft = l.Left - boxLeft < Math.Max(4, width * 0.03);
                bool nearRight = boxRight - l.Right < Math.Max(4, width * 0.03);
                if (nearLeft && nearRight) return Align.Justify;
                if (!nearLeft && Math.Abs((l.Left + l.Right) / 2 - center) < Math.Max(3, width * 0.02) && lw < width * 0.92) return Align.Center;
                if (nearRight && !nearLeft && l.Left - boxLeft > width * 0.3) return Align.Right;
                return Align.Left;
            }

            Paragraph? cur = null;
            Align curAlign = Align.Left;
            foreach (var line in lines)
            {
                var al = AlignOf(line);
                bool join = false;
                if (cur != null)
                {
                    var prev = cur.Lines[^1];
                    double gap = prev.Bottom - line.Top;
                    double pitch = prev.Baseline - line.Baseline;
                    bool closeEnough = gap < line.Size * 0.75 && pitch < Math.Max(prev.Size, line.Size) * 1.6 && pitch > 0;
                    bool sameSize = Math.Abs(prev.Size - line.Size) <= 1.0;
                    if (closeEnough && sameSize)
                    {
                        if (curAlign == Align.Center && al == Align.Center) join = true;
                        else if (curAlign == Align.Right && al == Align.Right) join = true;
                        else if ((curAlign == Align.Justify || curAlign == Align.Left) && (al == Align.Left || al == Align.Justify))
                        {
                            // Continuation of a flowing paragraph: previous line reached the right edge
                            // and this one starts at the paragraph's left (or the first line was indented).
                            bool prevFull = boxRight - prev.Right < Math.Max(width * 0.12, 18);
                            double paraLeft = cur.Lines.Count > 1 ? cur.Lines[1].Left : cur.Lines[0].Left;
                            // Second line may start left (first-line indent) or right (hanging indent: "☐ Text", "1. Text").
                            bool sameLeft = Math.Abs(line.Left - paraLeft) < 4 || (cur.Lines.Count == 1 && Math.Abs(line.Left - prev.Left) < width * 0.2);
                            join = prevFull && sameLeft;
                        }
                    }
                }

                if (join)
                {
                    cur!.Lines.Add(line);
                    if (al == Align.Justify) curAlign = Align.Justify;
                }
                else
                {
                    if (cur != null) Finish(cur, curAlign);
                    cur = new Paragraph();
                    cur.Lines.Add(line);
                    curAlign = al;
                    paras.Add(cur);
                }
            }
            if (cur != null) Finish(cur, curAlign);
            return paras;

            void Finish(Paragraph p, Align a)
            {
                p.Left = p.Lines.Min(l => l.Left); p.Right = p.Lines.Max(l => l.Right);
                p.Top = p.Lines[0].Top; p.Bottom = p.Lines[^1].Bottom;
                if (p.Lines.Count == 1 && a == Align.Justify) a = Align.Left;
                // Justified: every line but the last ends at the same x, at the right edge.
                if (p.Lines.Count > 1 && (a == Align.Left || a == Align.Justify))
                {
                    var rights = p.Lines.Take(p.Lines.Count - 1).Select(l => l.Right).ToList();
                    double spread = rights.Max() - rights.Min();
                    double fromEdge = boxRight - rights.Max();
                    bool justified = spread < 1.5 && (rights.Count >= 2 ? fromEdge < Math.Max(8, width * 0.04) : fromEdge < 2.5);
                    a = justified ? Align.Justify : Align.Left;
                }
                p.Align = a;
                p.KeepBreaks = a == Align.Center || a == Align.Right || p.Lines.Skip(1).Any(HasTabGap);
                double bodyLeft = p.Lines.Count > 1 ? p.Lines[1].Left : p.Lines[0].Left;
                p.IndentLeft = a == Align.Left || a == Align.Justify ? Math.Max(0, bodyLeft - boxLeft) : 0;
                p.FirstLineOffset = p.Lines.Count > 1 ? p.Lines[0].Left - bodyLeft : 0;
                if (p.Lines.Count > 1)
                    p.LinePitch = (p.Lines[0].Baseline - p.Lines[^1].Baseline) / (p.Lines.Count - 1);
            }
        }

        /// <summary>A gap this wide inside a line is a tab (form fields, "SÍ ☐   NO ☐"), not a space.</summary>
        public static bool IsTabGap(Glyph prev, Glyph next) => AdvanceGap(prev, next) > Math.Max(prev.Size, next.Size) * 1.2;

        /// <summary>Space between the end of one glyph's advance and the start of the next (ink boxes lie about narrow glyphs like "1").</summary>
        public static double AdvanceGap(Glyph prev, Glyph next)
        {
            double a = prev.AdvRight > prev.AdvLeft ? prev.AdvRight : prev.Right;
            double b = next.AdvRight > next.AdvLeft ? next.AdvLeft : next.Left;
            return b - a;
        }

        public static bool HasTabGap(TextLine line)
        {
            Glyph? prev = null;
            foreach (var g in line.Glyphs)
            {
                if (g.IsSpace) continue;
                if (prev != null && IsTabGap(prev, g)) return true;
                prev = g;
            }
            return false;
        }

        // ------------------------------------------------------------------ header / footer

        private static string Signature(string text) =>
            Regex.Replace(Regex.Replace(text.Trim(), @"\d", "#"), @"\s+", " ");

        private static bool IsNumberOnly(string text) =>
            Regex.IsMatch(text.Trim(), @"^(p[áa]g(ina)?\.?\s*)?[\d\s./-]+(\s*(de|of)\s*\d+)?$", RegexOptions.IgnoreCase);

        private static void DetectBands(DocModel model)
        {
            var pages = model.Pages.Where(p => !p.NeedsOcr).ToList();
            if (pages.Count < 2) return;
            model.Header = DetectBand(model, pages, top: true);
            model.Footer = DetectBand(model, pages, top: false);
        }

        private static Band? DetectBand(DocModel model, List<PageModel> pages, bool top)
        {
            bool InBand(PageModel p, double yTop, double yBottom) =>
                top ? yBottom > p.Height * 0.84 : yTop < p.Height * 0.10;

            // Text lines repeated (ignoring digits) on at least two pages.
            var counts = new Dictionary<string, int>();
            foreach (var p in pages)
                foreach (var sig in p.BodyLines.Where(l => InBand(p, l.Top, l.Bottom)).Select(l => Signature(l.Text)).Distinct())
                    counts[sig] = counts.GetValueOrDefault(sig) + 1;
            var repeated = counts.Where(kv => kv.Value >= 2 && kv.Key.Trim('#', ' ').Length > 0).Select(kv => kv.Key).ToHashSet();
            if (repeated.Count == 0) return null;

            Band? band = null;
            var numbers = new List<(int Page, long Value, TextLine Line)>();
            foreach (var p in pages)
            {
                var lines = p.BodyLines.Where(l => InBand(p, l.Top, l.Bottom) && (repeated.Contains(Signature(l.Text)) || IsNumberOnly(l.Text))).ToList();
                var repLines = lines.Where(l => repeated.Contains(Signature(l.Text))).ToList();
                if (repLines.Count == 0) continue;
                // The band reaches from the page edge to the innermost repeated element (plus figures/rules there).
                double edge = top ? repLines.Min(l => l.Bottom) : repLines.Max(l => l.Top);
                var figs = p.Figures.Where(f => top ? f.Bottom >= edge - 25 && f.Bottom > p.Height * 0.80 : f.Top <= edge + 25 && f.Top < p.Height * 0.14).ToList();
                var rules = p.Rules.Where(r => top ? r.Bottom >= edge - 25 && r.Bottom > p.Height * 0.80 : r.Top <= edge + 25 && r.Top < p.Height * 0.14).ToList();
                double cut = top
                    ? new[] { edge }.Concat(figs.Select(f => f.Bottom)).Concat(rules.Select(r => r.Bottom)).Min()
                    : new[] { edge }.Concat(figs.Select(f => f.Top)).Concat(rules.Select(r => r.Top)).Max();

                bool InCut(double yTop, double yBottom) => top ? yBottom >= cut - 0.5 : yTop <= cut + 0.5;
                var bandLines = p.BodyLines.Where(l => InCut(l.Top, l.Bottom)).ToList();
                var bandFigs = p.Figures.Where(f => InCut(f.Top, f.Bottom)).ToList();
                var bandRules = p.Rules.Where(r => InCut(r.Top, r.Bottom)).ToList();

                if (band == null)
                {
                    // Picture of the first page's band, without page numbers.
                    var boxes = bandLines.Where(l => !IsNumberOnly(l.Text)).Select(l => (l.Left, l.Right, l.Top, l.Bottom))
                        .Concat(bandFigs.Select(f => (f.Left, f.Right, f.Top, f.Bottom)))
                        .Concat(bandRules.Select(r => (r.Left, r.Right, r.Top, r.Bottom))).ToList();
                    if (boxes.Count > 0)
                        band = new Band
                        {
                            PageNumber = p.Number,
                            Left = boxes.Min(x => x.Left) - 1, Right = boxes.Max(x => x.Right) + 1,
                            Top = boxes.Max(x => x.Top) + 1, Bottom = boxes.Min(x => x.Bottom) - 1,
                        };
                }

                foreach (var nl in bandLines.Where(l => Regex.IsMatch(l.Text.Trim(), @"^\d{1,7}$")))
                    numbers.Add((p.Number, long.Parse(nl.Text.Trim()), nl));

                p.BodyLines.RemoveAll(l => bandLines.Contains(l));
                p.Figures.RemoveAll(f => bandFigs.Contains(f));
                p.Rules.RemoveAll(r => bandRules.Contains(r));
            }

            // Real page numbers: one number per page, growing by one with the page.
            if (band != null && numbers.Count >= 2)
            {
                var perPage = numbers.GroupBy(n => n.Page).Where(g => g.Count() == 1).Select(g => g.First()).OrderBy(n => n.Page).ToList();
                if (perPage.Count >= 2 && perPage.Select(n => n.Value - n.Page).Distinct().Count() == 1 && perPage[0].Value - (perPage[0].Page - 1) > 0)
                {
                    var first = perPage[0];
                    var bandPage = perPage.FirstOrDefault(n => n.Page == band.PageNumber);
                    band.PageNumberStart = (int)(first.Value - (first.Page - 1));
                    var line = (bandPage.Line ?? first.Line);
                    band.NumberStyle = line.Glyphs.First(g => !g.IsSpace).Style;
                    band.NumLeft = line.Left; band.NumRight = line.Right; band.NumTop = line.Top; band.NumBottom = line.Bottom;
                    if (bandPage.Line != null)
                        band.Masks.Add((line.Left - 1.5, line.Top + 1.5, line.Right + 1.5, line.Bottom - 1.5));
                }
            }
            return band;
        }

        // ------------------------------------------------------------------ finish

        private static void FinishPages(DocModel model)
        {
            var textPages = model.Pages.Where(p => !p.NeedsOcr).ToList();
            var boxes = textPages.SelectMany(p =>
                p.BodyLines.Select(l => (l.Left, l.Right, l.Top, l.Bottom))
                 .Concat(p.Tables.Select(t => (t.Left, t.Right, t.Top, t.Bottom)))
                 .Concat(p.Figures.Select(f => (f.Left, f.Right, f.Top, f.Bottom)))
                 .Concat(p.Rules.Select(r => (r.Left, r.Right, r.Top, r.Bottom)))).ToList();

            if (boxes.Count == 0)
            {
                model.ContentLeft = 56; model.ContentRight = model.PageWidth - 56;
                model.MarginTop = 56; model.MarginBottom = 56;
            }
            else
            {
                model.ContentLeft = Math.Max(14, boxes.Min(b => b.Left));
                model.ContentRight = Math.Min(model.PageWidth - 14, boxes.Max(b => b.Right));
                model.MarginTop = Math.Clamp(model.PageHeight - boxes.Max(b => b.Top), 14, 200);
                model.MarginBottom = Math.Clamp(boxes.Min(b => b.Bottom), 14, 120);
            }

            // Most common font = Normal style.
            var styleCount = new Dictionary<(string, double), int>();
            foreach (var g in textPages.SelectMany(p => p.BodyLines.SelectMany(l => l.Glyphs)
                         .Concat(p.Tables.SelectMany(t => t.Cells.SelectMany(c => c.Paragraphs.SelectMany(pp => pp.Lines.SelectMany(l => l.Glyphs)))))))
            {
                if (g.IsSpace || g.Style.SymbolFont != null) continue;
                var k = (g.Style.Font, g.Style.Size);
                styleCount[k] = styleCount.GetValueOrDefault(k) + 1;
            }
            if (styleCount.Count > 0)
            {
                var best = styleCount.OrderByDescending(kv => kv.Value).First().Key;
                model.BodyFont = best.Item1; model.BodySize = best.Item2;
            }

            foreach (var p in textPages)
            {
                // Lines are built across the whole page, so two columns at the same height share a line:
                // look for columns on pieces split at wide gaps, then cut the real lines at the gutter.
                var pieces = p.BodyLines.SelectMany(l => SplitLine(l, (a, b) => AdvanceGap(a, b) > Math.Max(9, Math.Max(a.Size, b.Size) * 1.2))).ToList();
                var gutter = DetectColumns(pieces, model.ContentLeft, model.ContentRight);
                if (gutter != null)
                {
                    var (cl, cr) = gutter.Value;
                    p.BodyLines = p.BodyLines.SelectMany(l => SplitLine(l, (a, b) => a.Right <= cl + 1 && b.Left >= cr - 1)).ToList();
                }
                if (gutter == null)
                {
                    p.Elements.AddRange(Assemble(p.BodyLines, p.Tables, p.Figures, p.Rules, model.ContentLeft, model.ContentRight));
                    continue;
                }

                var (gl, gr) = gutter.Value;
                if (Environment.GetEnvironmentVariable("GAJO_DEBUG") == "1")
                {
                    Console.WriteLine($"page {p.Number}: gutter {gl:0.0}-{gr:0.0}");
                    foreach (var l in p.BodyLines.Where(l => !(l.Right <= gr && l.Left < gl || l.Left >= gl && l.Right > gr)))
                        Console.WriteLine($"   span: {l.Left:0.0}-{l.Right:0.0} '{l.Text}'");
                    foreach (var f in p.Figures) Console.WriteLine($"   fig {f.Left:0.0}-{f.Right:0.0} {f.Top:0.0}");
                }
                double mid = (gl + gr) / 2;
                int Side(double left, double right) =>
                    right <= gr && (left + right) / 2 < mid ? -1 : left >= gl && (left + right) / 2 > mid ? 1 : 0;
                // Walk top → bottom: full-width items split the page into single- and two-column stretches.
                var items = p.BodyLines.Select(l => (Top: l.Top, Side: Side(l.Left, l.Right), Obj: (object)l))
                    .Concat(p.Tables.Select(t => (Top: t.Top, Side: Side(t.Left, t.Right), Obj: (object)t)))
                    .Concat(p.Figures.Select(f => (Top: f.Top, Side: Side(f.Left, f.Right), Obj: (object)f)))
                    .Concat(p.Rules.Select(r => (Top: r.Top, Side: Side(r.Left, r.Right), Obj: (object)r)))
                    .OrderByDescending(i => i.Top).ToList();

                // Items above the height where both columns are running (a subtitle over the left
                // column, say) belong to the single-column part above.
                var lefts = items.Where(i => i.Side < 0).ToList();
                var rights = items.Where(i => i.Side > 0).ToList();
                if (lefts.Count > 0 && rights.Count > 0)
                {
                    double zoneTop = Math.Min(lefts.Max(i => i.Top), rights.Max(i => i.Top)) + 2;
                    items = items.Select(i => i.Top > zoneTop ? (i.Top, 0, i.Obj) : i).ToList();
                }

                var segment = new List<(double Top, int Side, object Obj)>();
                bool segCols = false;
                void FlushSegment()
                {
                    if (segment.Count == 0) return;
                    if (!segCols)
                    {
                        p.Elements.AddRange(Assemble(segment.Select(i => i.Obj).OfType<TextLine>().ToList(), segment.Select(i => i.Obj).OfType<Table>().ToList(),
                            segment.Select(i => i.Obj).OfType<Figure>().ToList(), segment.Select(i => i.Obj).OfType<Rule>().ToList(), model.ContentLeft, model.ContentRight));
                    }
                    else
                    {
                        var left = segment.Where(i => i.Side < 0).Select(i => i.Obj).ToList();
                        var right = segment.Where(i => i.Side > 0).Select(i => i.Obj).ToList();
                        var block = new ColumnBlock { LeftL = model.ContentLeft, LeftR = gl, RightL = gr, RightR = model.ContentRight };
                        block.LeftItems = Assemble(left.OfType<TextLine>().ToList(), left.OfType<Table>().ToList(), left.OfType<Figure>().ToList(), left.OfType<Rule>().ToList(), model.ContentLeft, gl);
                        block.RightItems = Assemble(right.OfType<TextLine>().ToList(), right.OfType<Table>().ToList(), right.OfType<Figure>().ToList(), right.OfType<Rule>().ToList(), gr, model.ContentRight);
                        var all = block.LeftItems.Concat(block.RightItems).ToList();
                        if (all.Count > 0)
                        {
                            block.Left = model.ContentLeft; block.Right = model.ContentRight;
                            block.Top = all.Max(e => e.Top); block.Bottom = all.Min(e => e.Bottom);
                            p.Elements.Add(block);
                        }
                    }
                    segment.Clear();
                }
                foreach (var it in items)
                {
                    bool cols = it.Side != 0;
                    if (segment.Count > 0 && cols != segCols) FlushSegment();
                    segCols = cols;
                    segment.Add(it);
                }
                FlushSegment();
                p.Elements.Sort((a, b) => b.Top.CompareTo(a.Top));
            }
        }

        /// <summary>Cuts a line between glyphs where <paramref name="cut"/> says so.</summary>
        private static IEnumerable<TextLine> SplitLine(TextLine line, Func<Glyph, Glyph, bool> cut)
        {
            var chunk = new List<Glyph>();
            Glyph? prev = null;
            foreach (var g in line.Glyphs)
            {
                if (!g.IsSpace && prev != null && cut(prev, g))
                {
                    var t = MakeLine(chunk);
                    if (t != null) yield return t;
                    chunk = new List<Glyph>();
                }
                chunk.Add(g);
                if (!g.IsSpace) prev = g;
            }
            var last = MakeLine(chunk);
            if (last != null) yield return last;
        }

        private static TextLine? MakeLine(List<Glyph> chunk)
        {
            int a = 0, b = chunk.Count - 1;
            while (a <= b && chunk[a].IsSpace) a++;
            while (b >= a && chunk[b].IsSpace) b--;
            if (a > b) return null;
            var line = new TextLine { Glyphs = chunk.GetRange(a, b - a + 1) };
            line.Recompute();
            return line;
        }

        /// <summary>Paragraphs, tables, figures and rules of one area, top to bottom.</summary>
        private static List<Element> Assemble(List<TextLine> lines, List<Table> tables, List<Figure> figures, List<Rule> rules, double boxLeft, double boxRight)
        {
            // A table with text beside it floats, so the text can sit next to it as in the PDF.
            foreach (var t in tables)
                t.Floating = lines.Any(l => l.Baseline < t.Top && l.Baseline > t.Bottom && (l.Right < t.Left - 4 || l.Left > t.Right + 4));
            var list = new List<Element>();
            list.AddRange(BuildParagraphs(lines, boxLeft, boxRight));
            list.AddRange(tables);
            list.AddRange(figures);
            list.AddRange(rules);
            list.Sort((a, b) => b.Top.CompareTo(a.Top));
            return list;
        }

        /// <summary>
        /// Finds the gap between two text columns: a vertical strip in the middle of the page that no
        /// narrow line crosses, with a good amount of text on both sides at the same heights.
        /// </summary>
        private static (double Left, double Right)? DetectColumns(List<TextLine> lines, double left, double right)
        {
            double width = right - left;
            if (width < 200) return null;
            var narrow = lines.Where(l => l.Right - l.Left < width * 0.58 && l.Text.Trim().Length > 2).ToList();
            if (narrow.Count < 8) return null;

            int bins = (int)Math.Ceiling(width);
            var cover = new int[bins + 1];
            foreach (var l in narrow)
                for (int x = Math.Max(0, (int)(l.Left - left)); x <= Math.Min(bins, (int)(l.Right - left)); x++) cover[x]++;

            int bestStart = -1, bestLen = 0;
            int lo = (int)(width * 0.3), hi = (int)(width * 0.7);
            for (int x = lo; x <= hi; x++)
            {
                if (cover[x] != 0) continue;
                int s = x;
                while (x <= hi && cover[x] == 0) x++;
                if (x - s > bestLen) { bestLen = x - s; bestStart = s; }
            }
            if (bestLen < 6) return null;
            double gl = left + bestStart, gr = left + bestStart + bestLen;

            var l1 = narrow.Where(l => l.Right <= gl + 1).ToList();
            var r1 = narrow.Where(l => l.Left >= gr - 1).ToList();
            if (l1.Count < 4 || r1.Count < 4) return null;
            // Both columns must run side by side, not one after the other.
            double top = Math.Min(l1.Max(l => l.Top), r1.Max(l => l.Top));
            double bottom = Math.Max(l1.Min(l => l.Bottom), r1.Min(l => l.Bottom));
            double overlap = top - bottom;
            double smaller = Math.Min(l1.Max(l => l.Top) - l1.Min(l => l.Bottom), r1.Max(l => l.Top) - r1.Min(l => l.Bottom));
            if (overlap < smaller * 0.5) return null;
            // Most of the page's text must be in the columns (forms with two short fields per row are not columns).
            int wide = lines.Count(l => l.Left < gl - 1 && l.Right > gr + 1);
            if (wide > (l1.Count + r1.Count) * 0.5) return null;
            // Real columns are mostly flowing text: many lines per side, with several words,
            // most of them reaching the column's right edge (forms with field pairs don't).
            if (l1.Count + r1.Count < 12) return null;
            static int Words(TextLine l) => l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (l1.Average(Words) < 4 || r1.Average(Words) < 4) return null;
            double lRight = l1.Max(l => l.Right), rRight = r1.Max(l => l.Right);
            double lWidth = lRight - l1.Min(l => l.Left), rWidth = rRight - r1.Min(l => l.Left);
            if (l1.Count(l => lRight - l.Right < lWidth * 0.15) < l1.Count * 0.5) return null;
            if (r1.Count(l => rRight - l.Right < rWidth * 0.15) < r1.Count * 0.5) return null;
            return (gl, gr);
        }
    }
}
