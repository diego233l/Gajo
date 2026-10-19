using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Gajo.Core
{
    /// <summary>Spreadsheet values (no formatting): XLSX, ODS, CSV and TSV readers/writers, plus a simple table PDF.</summary>
    public static class Spreadsheets
    {
        public sealed class Sheet
        {
            public string Name = "Hoja1";
            public List<List<object?>> Rows = new();   // string, double, DateTime, bool or null
        }

        static Spreadsheets() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

        public static List<Sheet> Read(string path) => Formats.NormalizedExtension(path) switch
        {
            "xlsx" or "xlsm" => ReadXlsx(path),
            "ods" => ReadOds(path),
            "tsv" => new List<Sheet> { ReadDelimited(path, '\t') },
            _ => new List<Sheet> { ReadDelimited(path, null) },
        };

        public static void Write(List<Sheet> sheets, string output, string targetId)
        {
            switch (targetId)
            {
                case "xlsx": WriteXlsx(sheets, output); break;
                case "ods": WriteOds(sheets, output); break;
                case "csv": WriteDelimited(sheets[0], output, CsvSeparator()); break;
                case "tsv": WriteDelimited(sheets[0], output, '\t'); break;
                case "pdf": SheetPdf.Write(sheets, output); break;
                default: throw new ConversionException("Formato de hoja de cálculo no compatible.");
            }
        }

        /// <summary>Excel in Spanish (and most of Europe) expects ";" in CSV files; English expects ",".</summary>
        public static char CsvSeparator()
        {
            var ls = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
            return ls == ";" ? ';' : ',';
        }

        public static string CellText(object? v) => v switch
        {
            null => "",
            double d => d.ToString(CultureInfo.CurrentCulture),
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("d", CultureInfo.CurrentCulture) : dt.ToString("g", CultureInfo.CurrentCulture),
            bool b => b ? "VERDADERO" : "FALSO",
            _ => v.ToString() ?? "",
        };

        // ================================================================== CSV / TSV

        private static Sheet ReadDelimited(string path, char? sep)
        {
            var text = Documents.ReadText(path);
            if (sep == null)
            {
                var sample = string.Join("\n", text.Split('\n').Take(20));
                int semis = sample.Count(c => c == ';'), commas = sample.Count(c => c == ','), tabs = sample.Count(c => c == '\t');
                sep = tabs > semis && tabs > commas ? '\t' : semis >= commas && semis > 0 ? ';' : ',';
            }
            var sheet = new Sheet { Name = Path.GetFileNameWithoutExtension(path) };
            var row = new List<object?>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cell.Append(c);
                    continue;
                }
                if (c == '"' && cell.Length == 0) quoted = true;
                else if (c == sep) { row.Add(Typed(cell.ToString())); cell.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(Typed(cell.ToString())); cell.Clear();
                    sheet.Rows.Add(row); row = new List<object?>();
                }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(Typed(cell.ToString())); sheet.Rows.Add(row); }
            // Drop a trailing empty row.
            while (sheet.Rows.Count > 0 && sheet.Rows[^1].All(v => v == null || v is string s && s.Length == 0)) sheet.Rows.RemoveAt(sheet.Rows.Count - 1);
            return sheet;
        }

        /// <summary>Numbers written as text in a CSV ("1.234,5" in Spanish, "1234.5" in English) become numbers.</summary>
        private static object? Typed(string s)
        {
            if (s.Length == 0) return null;
            var t = s.Trim();
            if (Regex.IsMatch(t, @"^-?\d+([.,]\d+)?$") && !(t.Length > 1 && t[0] == '0' && t[1] != '.' && t[1] != ','))
            {
                if (double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            }
            return s;
        }

        private static void WriteDelimited(Sheet sheet, string output, char sep)
        {
            var sb = new StringBuilder();
            foreach (var row in sheet.Rows)
            {
                for (int i = 0; i < row.Count; i++)
                {
                    if (i > 0) sb.Append(sep);
                    string v = row[i] switch
                    {
                        double d => sep == ';' ? d.ToString(CultureInfo.CurrentCulture) : d.ToString(CultureInfo.InvariantCulture),
                        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm:ss"),
                        _ => CellText(row[i]),
                    };
                    if (v.IndexOfAny(new[] { sep, '"', '\n', '\r' }) >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
                    sb.Append(v);
                }
                sb.Append("\r\n");
            }
            // BOM so Excel recognises UTF-8 (accents, ñ…).
            File.WriteAllText(output, sb.ToString(), new UTF8Encoding(true));
        }

        // ================================================================== XLSX

        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private static List<Sheet> ReadXlsx(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            XDocument? Load(string name)
            {
                var e = zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(x => x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (e == null) return null;
                using var s = e.Open();
                return XDocument.Load(s);
            }

            var shared = new List<string>();
            var sst = Load("xl/sharedStrings.xml");
            if (sst != null)
                foreach (var si in sst.Root!.Elements(S + "si"))
                    shared.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));

            // Which cell styles are dates.
            var dateStyles = new HashSet<int>();
            var styles = Load("xl/styles.xml");
            if (styles != null)
            {
                var custom = styles.Root!.Element(S + "numFmts")?.Elements(S + "numFmt")
                    .ToDictionary(n => (int)n.Attribute("numFmtId")!, n => (string?)n.Attribute("formatCode") ?? "") ?? new Dictionary<int, string>();
                var xfs = styles.Root.Element(S + "cellXfs")?.Elements(S + "xf").ToList() ?? new List<XElement>();
                for (int i = 0; i < xfs.Count; i++)
                {
                    int id = (int?)xfs[i].Attribute("numFmtId") ?? 0;
                    bool isDate = id is >= 14 and <= 22 or >= 45 and <= 47 ||
                                  custom.TryGetValue(id, out var code) && Regex.IsMatch(Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]", ""), "[dmyhs]", RegexOptions.IgnoreCase);
                    if (isDate) dateStyles.Add(i);
                }
            }

            var wb = Load("xl/workbook.xml") ?? throw new ConversionException("El XLSX no es válido.");
            var rels = Load("xl/_rels/workbook.xml.rels");
            var result = new List<Sheet>();
            foreach (var sh in wb.Root!.Element(S + "sheets")?.Elements(S + "sheet") ?? Enumerable.Empty<XElement>())
            {
                string name = (string?)sh.Attribute("name") ?? $"Hoja{result.Count + 1}";
                string? rid = (string?)sh.Attribute(RelNs + "id");
                string? target = rels?.Root?.Elements().FirstOrDefault(r => (string?)r.Attribute("Id") == rid)?.Attribute("Target")?.Value;
                if (target == null) continue;
                target = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                var doc = Load(target);
                if (doc == null) continue;

                var sheet = new Sheet { Name = name };
                foreach (var r in doc.Descendants(S + "row"))
                {
                    int rowIndex = ((int?)r.Attribute("r") ?? sheet.Rows.Count + 1) - 1;
                    while (sheet.Rows.Count < rowIndex) sheet.Rows.Add(new List<object?>());
                    var row = new List<object?>();
                    foreach (var c in r.Elements(S + "c"))
                    {
                        int col = ColumnIndex((string?)c.Attribute("r")) ?? row.Count;
                        while (row.Count < col) row.Add(null);
                        string? t = (string?)c.Attribute("t");
                        string? v = c.Element(S + "v")?.Value;
                        int style = (int?)c.Attribute("s") ?? 0;
                        object? value = t switch
                        {
                            "s" => int.TryParse(v, out var si) && si < shared.Count ? shared[si] : null,
                            "inlineStr" => string.Concat(c.Descendants(S + "t").Select(x => x.Value)),
                            "str" => v,
                            "b" => v == "1",
                            "e" => v,
                            _ => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                                ? (dateStyles.Contains(style) && d > 0 && d < 2958466 ? DateTime.FromOADate(d) : d)
                                : null,
                        };
                        row.Add(value);
                    }
                    sheet.Rows.Add(row);
                }
                result.Add(sheet);
            }
            if (result.Count == 0) throw new ConversionException("El XLSX no tiene hojas.");
            return result;
        }

        private static int? ColumnIndex(string? cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return null;
            int n = 0;
            foreach (char ch in cellRef)
            {
                if (!char.IsLetter(ch)) break;
                n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return n == 0 ? null : n - 1;
        }

        private static string ColumnName(int index)
        {
            var s = "";
            for (index++; index > 0; index = (index - 1) / 26) s = (char)('A' + (index - 1) % 26) + s;
            return s;
        }

        private static string X(string s) => SecurityElement.Escape(new string(s.Where(c => c == '\t' || c == '\n' || c == '\r' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF).ToArray())) ?? "";

        private static void WriteXlsx(List<Sheet> sheets, string output)
        {
            var files = new List<(string, string)>();
            var ct = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sheets.Count; i++)
            {
                string name = Regex.Replace(sheets[i].Name, @"[\[\]\*\?/\\:]", "_");
                if (name.Length == 0) name = $"Hoja{i + 1}";
                if (name.Length > 31) name = name[..31];
                string baseName = name; int k = 2;
                while (!used.Add(name)) name = (baseName.Length > 28 ? baseName[..28] : baseName) + " " + k++;
                ct.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                wb.Append($"<sheet name=\"{X(name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");

                var sd = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
                for (int r = 0; r < sheets[i].Rows.Count; r++)
                {
                    var row = sheets[i].Rows[r];
                    sd.Append($"<row r=\"{r + 1}\">");
                    for (int c = 0; c < row.Count; c++)
                    {
                        var v = row[c];
                        string refc = ColumnName(c) + (r + 1);
                        switch (v)
                        {
                            case null: break;
                            case double d: sd.Append($"<c r=\"{refc}\"><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>"); break;
                            case DateTime dt: sd.Append($"<c r=\"{refc}\" s=\"{(dt.TimeOfDay == TimeSpan.Zero ? 1 : 2)}\"><v>{dt.ToOADate().ToString("R", CultureInfo.InvariantCulture)}</v></c>"); break;
                            case bool b: sd.Append($"<c r=\"{refc}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>"); break;
                            default: sd.Append($"<c r=\"{refc}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{X(v.ToString() ?? "")}</t></is></c>"); break;
                        }
                    }
                    sd.Append("</row>");
                }
                sd.Append("</sheetData></worksheet>");
                files.Add(($"xl/worksheets/sheet{i + 1}.xml", sd.ToString()));
            }
            ct.Append("</Types>");
            wb.Append("</sheets></workbook>");
            rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            files.Add(("[Content_Types].xml", ct.ToString()));
            files.Add(("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"));
            files.Add(("xl/workbook.xml", wb.ToString()));
            files.Add(("xl/_rels/workbook.xml.rels", rels.ToString()));
            files.Add(("xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy hh:mm\"/></numFmts>" +
                "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
                "</styleSheet>"));
            WriteZip(output, files, null);
        }

        // ================================================================== ODS

        private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        private static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

        private static List<Sheet> ReadOds(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("content.xml") ?? throw new ConversionException("El ODS no es válido.");
            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s);
            var result = new List<Sheet>();
            foreach (var t in doc.Descendants(Table + "table"))
            {
                var sheet = new Sheet { Name = (string?)t.Attribute(Table + "name") ?? $"Hoja{result.Count + 1}" };
                foreach (var r in t.Descendants(Table + "table-row"))
                {
                    int rep = Math.Min((int?)r.Attribute(Table + "number-rows-repeated") ?? 1, 1000);
                    var row = new List<object?>();
                    foreach (var c in r.Elements().Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell"))
                    {
                        int crep = Math.Min((int?)c.Attribute(Table + "number-columns-repeated") ?? 1, 1000);
                        string? type = (string?)c.Attribute(Office + "value-type");
                        object? v = type switch
                        {
                            "float" or "percentage" or "currency" => double.TryParse((string?)c.Attribute(Office + "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
                            "date" => DateTime.TryParse((string?)c.Attribute(Office + "date-value"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null,
                            "boolean" => (string?)c.Attribute(Office + "boolean-value") == "true",
                            null => null,
                            _ => string.Join("\n", c.Elements(TextNs + "p").Select(p => p.Value)),
                        };
                        for (int k = 0; k < crep; k++) row.Add(v);
                    }
                    while (row.Count > 0 && row[^1] == null) row.RemoveAt(row.Count - 1);
                    for (int k = 0; k < rep; k++) sheet.Rows.Add(new List<object?>(row));
                }
                while (sheet.Rows.Count > 0 && sheet.Rows[^1].Count == 0) sheet.Rows.RemoveAt(sheet.Rows.Count - 1);
                result.Add(sheet);
            }
            return result;
        }

        private static void WriteOds(List<Sheet> sheets, string output)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" office:version=\"1.2\"><office:body><office:spreadsheet>");
            foreach (var sh in sheets)
            {
                sb.Append($"<table:table table:name=\"{X(sh.Name)}\">");
                foreach (var row in sh.Rows)
                {
                    sb.Append("<table:table-row>");
                    foreach (var v in row)
                    {
                        switch (v)
                        {
                            case null: sb.Append("<table:table-cell/>"); break;
                            case double d:
                                var ds = d.ToString("R", CultureInfo.InvariantCulture);
                                sb.Append($"<table:table-cell office:value-type=\"float\" office:value=\"{ds}\"><text:p>{X(d.ToString(CultureInfo.CurrentCulture))}</text:p></table:table-cell>");
                                break;
                            case DateTime dt:
                                sb.Append($"<table:table-cell office:value-type=\"date\" office:date-value=\"{dt:yyyy-MM-ddTHH:mm:ss}\"><text:p>{X(CellText(dt))}</text:p></table:table-cell>");
                                break;
                            case bool b:
                                sb.Append($"<table:table-cell office:value-type=\"boolean\" office:boolean-value=\"{(b ? "true" : "false")}\"><text:p>{CellText(b)}</text:p></table:table-cell>");
                                break;
                            default:
                                sb.Append("<table:table-cell office:value-type=\"string\">");
                                foreach (var line in (v.ToString() ?? "").Split('\n')) sb.Append($"<text:p>{X(line)}</text:p>");
                                sb.Append("</table:table-cell>");
                                break;
                        }
                    }
                    sb.Append("</table:table-row>");
                }
                if (sh.Rows.Count == 0) sb.Append("<table:table-row><table:table-cell/></table:table-row>");
                sb.Append("</table:table>");
            }
            sb.Append("</office:spreadsheet></office:body></office:document-content>");
            var files = new List<(string, string)>
            {
                ("META-INF/manifest.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>"),
                ("content.xml", sb.ToString()),
            };
            WriteZip(output, files, "application/vnd.oasis.opendocument.spreadsheet");
        }

        private static void WriteZip(string output, List<(string Name, string Content)> files, string? mimetype)
        {
            using var fs = File.Create(output);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            if (mimetype != null)
            {
                var m = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using var s = m.Open();
                var b = Encoding.ASCII.GetBytes(mimetype);
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

    /// <summary>Spreadsheet → PDF without Excel: one table per sheet in a monospaced grid, landscape when wide.</summary>
    internal static class SheetPdf
    {
        public static void Write(List<Spreadsheets.Sheet> sheets, string output)
        {
            var blocks = new List<Documents.Block>();
            foreach (var sh in sheets)
            {
                if (blocks.Count > 0) blocks.Add(new Documents.Block(-1, ""));
                if (sheets.Count > 1) blocks.Add(new Documents.Block(2, sh.Name));
                int cols = sh.Rows.Count == 0 ? 0 : sh.Rows.Max(r => r.Count);
                var widths = new int[cols];
                foreach (var r in sh.Rows)
                    for (int c = 0; c < r.Count; c++) widths[c] = Math.Min(30, Math.Max(widths[c], Spreadsheets.CellText(r[c]).Length));
                foreach (var r in sh.Rows)
                {
                    var sb = new StringBuilder();
                    for (int c = 0; c < cols; c++)
                    {
                        var t = c < r.Count ? Spreadsheets.CellText(r[c]).Replace('\n', ' ') : "";
                        if (t.Length > 30) t = t[..29] + "…";
                        bool num = c < r.Count && r[c] is double;
                        sb.Append(num ? t.PadLeft(widths[c]) : t.PadRight(widths[c]));
                        if (c < cols - 1) sb.Append(" | ");
                    }
                    blocks.Add(new Documents.Block(0, sb.ToString().TrimEnd()));
                }
            }
            TextPdf.Write(blocks, output, noWrapSmallFont: true);
        }
    }
}
