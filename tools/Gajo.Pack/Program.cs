using System.Diagnostics;
using Gajo.Core;

if (args.Length < 2)
{
    Console.Error.WriteLine("Uso: Gajo.Pack <Gajo.exe> <ffmpeg.exe> [calidad 0-11]");
    return 2;
}
string host = args[0], payload = args[1];
int quality = args.Length > 2 ? int.Parse(args[2]) : 7;
if (!File.Exists(host)) { Console.Error.WriteLine($"No existe {host}"); return 1; }
if (!File.Exists(payload)) { Console.Error.WriteLine($"No existe {payload}"); return 1; }

// Re-publishing without a clean may leave an already-packed exe: strip the old payload first.
if (EmbeddedPayload.Read(host) is { } old)
{
    using var fs = new FileStream(host, FileMode.Open, FileAccess.Write);
    fs.SetLength(old.PayloadOffset);
    Console.WriteLine("Gajo.Pack: sustituyendo el motor que ya estaba integrado");
}

var sw = Stopwatch.StartNew();
var info = EmbeddedPayload.Append(host, payload, quality);
Console.WriteLine($"Gajo.Pack: {Path.GetFileName(payload)} {info.Length / 1e6:0.0} MB -> {info.CompressedLength / 1e6:0.0} MB integrado en {Path.GetFileName(host)} ({sw.Elapsed.TotalSeconds:0.0} s)");
return 0;
