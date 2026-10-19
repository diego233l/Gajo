using System.Diagnostics;
using Gajo.Core;

/// <summary>Test-only stand-in for Windows OCR: pdftoppm + tesseract (Linux).</summary>
sealed class TesseractOcr : IPdfOcr
{
    public string? UnavailableReason => null;

    public async Task<IReadOnlyList<OcrLine>> RecognizePageAsync(string pdfPath, int pageNumber, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "gajo-ocr-" + Guid.NewGuid().ToString("N"));
        await Run("pdftoppm", "-r", "300", "-f", pageNumber.ToString(), "-l", pageNumber.ToString(), "-singlefile", "-png", pdfPath, tmp);
        var tsv = await Run("tesseract", tmp + ".png", "-", "-l", "eng", "tsv");
        File.Delete(tmp + ".png");
        var lines = new List<OcrLine>();
        foreach (var g in tsv.Split('\n').Skip(1).Select(l => l.Split('\t')).Where(c => c.Length >= 12 && c[0] == "5" && c[11].Trim().Length > 0)
                     .GroupBy(c => (c[2], c[3], c[4])))
        {
            var w = g.ToList();
            double x0 = w.Min(c => double.Parse(c[6])), y0 = w.Min(c => double.Parse(c[7]));
            double x1 = w.Max(c => double.Parse(c[6]) + double.Parse(c[8])), y1 = w.Max(c => double.Parse(c[7]) + double.Parse(c[9]));
            lines.Add(new OcrLine(string.Join(" ", w.Select(c => c[11].Trim())), x0, y0, x1 - x0, y1 - y0));
        }
        return lines;
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeImageAsync(byte[] png, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "gajo-ocri-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(tmp, png, ct);
        var tsv = await Run("tesseract", tmp, "-", "-l", "eng", "tsv");
        File.Delete(tmp);
        return Parse(tsv);
    }

    static List<OcrLine> Parse(string tsv)
    {
        var lines = new List<OcrLine>();
        foreach (var g in tsv.Split('\n').Skip(1).Select(l => l.Split('\t')).Where(c => c.Length >= 12 && c[0] == "5" && c[11].Trim().Length > 0)
                     .GroupBy(c => (c[2], c[3], c[4])))
        {
            var w = g.ToList();
            double x0 = w.Min(c => double.Parse(c[6])), y0 = w.Min(c => double.Parse(c[7]));
            double x1 = w.Max(c => double.Parse(c[6]) + double.Parse(c[8])), y1 = w.Max(c => double.Parse(c[7]) + double.Parse(c[9]));
            lines.Add(new OcrLine(string.Join(" ", w.Select(c => c[11].Trim())), x0, y0, x1 - x0, y1 - y0));
        }
        return lines;
    }

    static async Task<string> Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return o;
    }
}
