using Gajo.Core;
using System.Diagnostics;

// Smoke test: converts generated samples to every wheel target with a real ffmpeg.
if (args.Length > 1 && args[0] == "--media")
{
    var ff = Ffmpeg.Locate()!;
    var d = args[1];
    var silent = Path.Combine(d, "silencio.wav");
    var p0 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono:d=1", "-f", "lavfi", "-i", "sine=f=440:d=2", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono:d=1", "-filter_complex", "[0][1][2]concat=n=3:v=0:a=1", silent }))!;
    p0.WaitForExit();
    var clip = Path.Combine(d, "clip.mp4");
    var big = Path.Combine(d, "grande.mp4");
    var p1 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=1280x720:d=8", "-f", "lavfi", "-i", "sine=d=8", "-shortest", "-c:v", "libx264", "-crf", "12", "-pix_fmt", "yuv420p", big }))!;
    p1.WaitForExit();
    async Task Show(string label, Task<string> t) { try { var o = await t; var i = await MediaTools.ProbeAsync(ff, o); Console.WriteLine($"{label,-12} {Path.GetFileName(o),-34} {i.Duration:0.00}s ch={i.Channels} {i.Width}x{i.Height} {OutputNaming.HumanSize(i.Bytes)}"); } catch (Exception e) { Console.WriteLine($"{label,-12} ERROR {e.Message}"); } }
    var info = await MediaTools.ProbeAsync(ff, silent);
    Console.WriteLine($"probe        silencio.wav {info.Duration:0.00}s audio={info.HasAudio} video={info.HasVideo} ch={info.Channels}");
    var peaks = await MediaTools.PeaksAsync(ff, silent, 8);
    Console.WriteLine("peaks        " + string.Join(" ", peaks.Select(x => x.ToString("0.0"))));
    await Show("trim", MediaTools.TrimAsync(ff, silent, 0.5, 2.5, null));
    await Show("silence", MediaTools.TrimSilenceAsync(ff, silent, null));
    await Show("normalize", MediaTools.NormalizeAsync(ff, silent, null));
    await Show("stereo", MediaTools.ChannelsAsync(ff, silent, 2, null));
    await Show("acompress", MediaTools.CompressAudioAsync(ff, silent, null));
    await Show("vtrim", MediaTools.TrimAsync(ff, clip, 1, 2.5, null));
    await Show("mute", MediaTools.MuteAsync(ff, clip));
    await Show("vrotate", MediaTools.RotateVideoAsync(ff, clip, null));
    var bi = await MediaTools.ProbeAsync(ff, big);
    Console.WriteLine($"grande.mp4   {OutputNaming.HumanSize(bi.Bytes)} {bi.Width}x{bi.Height}");
    await Show("vcompress", MediaTools.CompressVideoToSizeAsync(ff, big, 1.0, null));
    var frame = await MediaTools.FrameAsync(ff, big, 2, 320);
    var strip = await MediaTools.ThumbStripAsync(ff, big, bi.Duration, 10, 50);
    Console.WriteLine($"frame {frame?.Length} bytes, strip {strip?.Length} bytes");
    return;
}
if (args.Length > 1 && args[0] == "--tools")
{
    var ff = Ffmpeg.Locate()!;
    var d = args[1];
    var photo = Path.Combine(d, "foto.png");
    var jpg = Path.Combine(d, "gris.jpg");
    Console.WriteLine("crop:     " + Path.GetFileName(await ImageTools.CropAsync(ff, photo, 10, 20, 300, 200)));
    Console.WriteLine("rotate:   " + Path.GetFileName(await ImageTools.RotateAsync(ff, jpg, true)));
    try { Console.WriteLine("compress: " + Path.GetFileName(await ImageTools.CompressAsync(ff, photo))); } catch (ConversionException e) { Console.WriteLine("compress: " + e.Message); }
    Console.WriteLine("strip:    " + Path.GetFileName(await ImageTools.StripMetadataAsync(ff, jpg)));
    Console.WriteLine("img→pdf:  " + Path.GetFileName(await ImageTools.ImagesToPdfAsync(ff, new[] { photo, jpg }, null)));
    PdfToDocx.Renderer = new PopplerRenderer();
    var pdf = Path.Combine(d, "informe.pdf");
    Console.WriteLine("pdf→png:  " + await PdfImages.ExportAsync(ff, pdf, "png", PdfToDocx.Renderer, null, default));
    Console.WriteLine("pdf→jpg:  " + await PdfImages.ExportAsync(ff, pdf, "jpg", PdfToDocx.Renderer, null, default));
    return;
}
if (args.Length > 1 && args[0] == "--pdf")
{
    // dotnet run -- --pdf file.pdf [--tesseract]
    if (args.Contains("--tesseract")) PdfText.Ocr = new TesseractOcr();
    PdfToDocx.Renderer = new PopplerRenderer();
    var docx = await new Converter("ffmpeg").ConvertAsync(args[1], Formats.DocumentTargets.First(t => t.Id == "docx"), new Progress<double>(p => Console.Write($"\r{p:P0}  ")));
    var md = await new Converter("ffmpeg").ConvertAsync(args[1], Formats.DocumentTargets.First(t => t.Id == "md"));
    Console.WriteLine($"\n{docx}\n{md}");
    return;
}
var dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "gajo-smoke");
var ffmpeg = Ffmpeg.Locate() ?? throw new Exception("ffmpeg not found");
Console.WriteLine($"ffmpeg: {ffmpeg}");
Directory.CreateDirectory(dir);
foreach (var f in Directory.GetFiles(dir)) File.Delete(f);

void Gen(string name, params string[] a)
{
    var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true };
    foreach (var x in new[] { "-v", "error", "-y" }.Concat(a).Append(Path.Combine(dir, name))) psi.ArgumentList.Add(x);
    var p = Process.Start(psi)!; var err = p.StandardError.ReadToEnd(); p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception(name + ": " + err);
}
Gen("foto.png", "-f", "lavfi", "-i", "testsrc2=s=641x481", "-frames:v", "1", "-vf", "format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(lt(X,200),0,255)'");
Gen("gris.jpg", "-f", "lavfi", "-i", "testsrc2=s=300x200", "-frames:v", "1", "-vf", "format=gray");
Gen("tono.wav", "-f", "lavfi", "-i", "sine=f=440:d=3");
Gen("clip.mp4", "-f", "lavfi", "-i", "testsrc2=s=321x241:d=3", "-f", "lavfi", "-i", "sine=d=3", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p");
Gen("mudo.webm", "-f", "lavfi", "-i", "testsrc2=s=320x240:d=2", "-c:v", "libvpx-vp9");
Gen("anim.gif", "-f", "lavfi", "-i", "testsrc2=s=200x150:d=2:r=10");

var texto = "Mi lista de la compra\n\nPan, leche y café — ¡también ñoquis! ¿Algo más?\nLínea con (paréntesis) y \\barra\\ y {llaves} y <html> & cosas.\n\n" + string.Concat(Enumerable.Repeat("Una línea muy larga para comprobar que el PDF corta bien las palabras sin salirse del margen derecho de la página. ", 6)) + "\n";
File.WriteAllText(Path.Combine(dir, "nota.txt"), string.Concat(Enumerable.Repeat(texto, 25)));
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
File.WriteAllBytes(Path.Combine(dir, "ansi.txt"), System.Text.Encoding.GetEncoding(1252).GetBytes("Canción con eñe y acentos: áéíóú €\r\n"));
File.WriteAllText(Path.Combine(dir, "apuntes.md"), "# Título principal\n\nTexto con **negrita**, *cursiva* y un [enlace](https://example.com).\n\n## Sección\n\n- uno\n- dos\n\n```\ncódigo\n```\n");
var samplePdf = Environment.GetEnvironmentVariable("GAJO_SAMPLE_PDF");
if (samplePdf != null && File.Exists(samplePdf)) File.Copy(samplePdf, Path.Combine(dir, "informe.pdf"), true);
PdfToDocx.Renderer = new PopplerRenderer();
var conv = new Converter(ffmpeg);
int ok = 0, fail = 0;
foreach (var input in new[] { "foto.png", "gris.jpg", "tono.wav", "clip.mp4", "mudo.webm", "anim.gif", "nota.txt", "ansi.txt", "apuntes.md", "apuntes.docx", "apuntes.html", "informe.pdf" }.Where(f => File.Exists(Path.Combine(dir, f))))
{
    var path = Path.Combine(dir, input);
    var plan = Formats.PlanFor(new[] { path });
    Console.WriteLine($"\n{input} ({plan.Kind}) → {string.Join(", ", plan.Targets.Select(t => t.Label))}");
    foreach (var t in plan.Targets)
    {
        var sw = Stopwatch.StartNew();
        double last = 0;
        try
        {
            var outp = await conv.ConvertAsync(path, t, new Progress<double>(v => last = v));
            Console.WriteLine($"  ✓ {t.Label,-5} {Path.GetFileName(outp),-16} {(Directory.Exists(outp) ? "carpeta" : OutputNaming.HumanSize(new FileInfo(outp).Length)),8}  {sw.ElapsedMilliseconds} ms");
            ok++;
        }
        catch (ConversionException ex)
        {
            Console.WriteLine($"  ✗ {t.Label,-5} {ex.Message}");
            fail++;
        }
    }
}
// Name collision check
var again = await conv.ConvertAsync(Path.Combine(dir, "foto.png"), Formats.ImageTargets[0]);
Console.WriteLine($"\ncollision → {Path.GetFileName(again)}");
Console.WriteLine($"\nOK {ok}  FAIL {fail}");
