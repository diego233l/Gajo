using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Gajo.Core
{
    /// <summary>Wraps baseline JPEGs in a PDF, one image per page (no external libraries).</summary>
    public static class PdfWriter
    {
        public static void WriteJpegAsPdf(string jpegPath, string pdfPath) => WriteJpegsAsPdf(new[] { jpegPath }, pdfPath);

        /// <summary>Each image becomes a page of its own proportions; wide images are scaled to A4 width.</summary>
        public static void WriteJpegsAsPdf(IReadOnlyList<string> jpegPaths, string pdfPath)
        {
            string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
            using var fs = new FileStream(pdfPath, FileMode.Create, FileAccess.Write);
            var offsets = new SortedDictionary<int, long>();
            void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }

            int n = jpegPaths.Count;
            // Object numbers: 1 catalog, 2 pages, then per page: page, image, content.
            Ascii("%PDF-1.4\n");
            fs.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);
            offsets[1] = fs.Position;
            Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets[2] = fs.Position;
            var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + i * 3} 0 R"));
            Ascii($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {n} >>\nendobj\n");

            for (int i = 0; i < n; i++)
            {
                var jpeg = File.ReadAllBytes(jpegPaths[i]);
                var (w, h, comps) = ReadJpegInfo(jpeg);
                string colorSpace = comps switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" };
                double pw = w * 0.75, ph = h * 0.75;          // 96 dpi → points
                if (pw > 595.28) { ph *= 595.28 / pw; pw = 595.28; }
                int pageId = 3 + i * 3, imgId = pageId + 1, contentId = pageId + 2;

                offsets[pageId] = fs.Position;
                Ascii($"{pageId} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pw)} {F(ph)}] /Resources << /XObject << /Im0 {imgId} 0 R >> >> /Contents {contentId} 0 R >>\nendobj\n");

                offsets[imgId] = fs.Position;
                Ascii($"{imgId} 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
                fs.Write(jpeg, 0, jpeg.Length);
                Ascii("\nendstream\nendobj\n");

                var content = $"q {F(pw)} 0 0 {F(ph)} 0 0 cm /Im0 Do Q";
                offsets[contentId] = fs.Position;
                Ascii($"{contentId} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
            }

            long xref = fs.Position;
            int count = 2 + n * 3;
            var sb = new StringBuilder();
            sb.Append($"xref\n0 {count + 1}\n0000000000 65535 f \n");
            for (int id = 1; id <= count; id++) sb.Append(offsets[id].ToString("D10")).Append(" 00000 n \n");
            sb.Append($"trailer\n<< /Size {count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Ascii(sb.ToString());
        }

        private static (int w, int h, int comps) ReadJpegInfo(byte[] d)
        {
            int i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                byte marker = d[i + 1];
                if (marker == 0xFF) { i++; continue; }
                int len = (d[i + 2] << 8) | d[i + 3];
                bool isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isSof)
                {
                    int h = (d[i + 5] << 8) | d[i + 6];
                    int w = (d[i + 7] << 8) | d[i + 8];
                    int c = d[i + 9];
                    return (w, h, c);
                }
                i += 2 + len;
            }
            throw new InvalidDataException("JPEG sin cabecera SOF");
        }
    }
}
