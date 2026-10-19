using System.Diagnostics;
using System.Globalization;
using Gajo.Core;

/// <summary>Test-only stand-in for the Windows renderer: pdftoppm crop.</summary>
sealed class PopplerRenderer : IPdfRegionRenderer
{
    public async Task<byte[]?> RenderPngAsync(string pdfPath, int pageNumber, double left, double top, double width, double height, double dpi, CancellationToken ct,
                                              IReadOnlyList<(double Left, double Top, double Width, double Height)>? whiteOut = null)
    {
        string I(double v) => ((int)Math.Round(v * dpi / 72)).ToString(CultureInfo.InvariantCulture);
        var tmp = Path.Combine(Path.GetTempPath(), "gajo-r-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo("pdftoppm");
        foreach (var a in new[] { "-r", ((int)dpi).ToString(), "-f", pageNumber.ToString(), "-l", pageNumber.ToString(), "-x", I(left), "-y", I(top), "-W", I(width), "-H", I(height), "-singlefile", "-png", pdfPath, tmp })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        var file = tmp + ".png";
        if (!File.Exists(file)) return null;
        if (whiteOut is { Count: > 0 })
        {
            var boxes = string.Join(",", whiteOut.Select(r => $"drawbox=x={I(r.Left - left)}:y={I(r.Top - top)}:w={I(r.Width)}:h={I(r.Height)}:color=white:t=fill"));
            var masked = tmp + "-m.png";
            var m = Process.Start(new ProcessStartInfo("ffmpeg", new[] { "-v", "error", "-y", "-i", file, "-vf", boxes, masked }))!;
            await m.WaitForExitAsync(ct);
            File.Delete(file);
            file = masked;
        }
        var bytes = await File.ReadAllBytesAsync(file, ct);
        File.Delete(file);
        return bytes;
    }
}
