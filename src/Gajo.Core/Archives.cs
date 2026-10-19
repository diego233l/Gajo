using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Gajo.Core
{
    /// <summary>ZIP, TAR and GZIP: create, convert and extract. RAR and 7z can be opened (not created).</summary>
    public static class Archives
    {
        public static bool IsArchive(string path) =>
            Formats.KindOf(path) == MediaKind.Archive;

        /// <summary>Name for a new archive made of several items: their folder's name, or the first item.</summary>
        public static string BaseFor(IReadOnlyList<string> items)
        {
            if (items.Count == 1) return items[0].TrimEnd('\\', '/');
            var parent = Path.GetDirectoryName(items[0].TrimEnd('\\', '/'))!;
            var name = Path.GetFileName(parent.TrimEnd('\\', '/'));
            return Path.Combine(parent, string.IsNullOrEmpty(name) ? "Archivos" : name);
        }

        private static string Unique(string pathWithoutExt, string ext)
        {
            var candidate = pathWithoutExt + ext;
            for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++) candidate = $"{pathWithoutExt} {n}{ext}";
            return candidate;
        }

        private static string StripArchiveExt(string path)
        {
            var p = path.TrimEnd('\\', '/');
            foreach (var e in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".zip", ".tar", ".gz", ".rar", ".7z" })
                if (p.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return p[..^e.Length];
            return Path.Combine(Path.GetDirectoryName(p)!, Path.GetFileNameWithoutExtension(p));
        }

        // ------------------------------------------------------------------ create

        /// <summary>Packs files and folders. targetId: zip, tar, gz (single file → .gz, otherwise .tar.gz).</summary>
        public static async Task<string> CreateAsync(IReadOnlyList<string> items, string targetId, IProgress<double>? progress, CancellationToken ct)
        {
            var baseName = BaseFor(items);
            if (items.Count == 1 && File.Exists(items[0])) baseName = Path.Combine(Path.GetDirectoryName(items[0])!, Path.GetFileName(items[0]));
            var entries = Expand(items).ToList();
            long total = Math.Max(1, entries.Sum(e => e.File != null ? new FileInfo(e.File).Length : 0));
            long done = 0;
            void Count(long n) { done += n; progress?.Report(Math.Min(0.99, (double)done / total)); }

            string output;
            switch (targetId)
            {
                case "zip":
                {
                    output = Unique(items.Count == 1 && File.Exists(items[0]) ? Path.Combine(Path.GetDirectoryName(items[0])!, Path.GetFileNameWithoutExtension(items[0])) : baseName, ".zip");
                    var temp = output + ".gajo-tmp";
                    await Task.Run(() =>
                    {
                        using var fs = File.Create(temp);
                        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                        foreach (var (file, name) in entries)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (file == null) { zip.CreateEntry(name + "/"); continue; }
                            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                            e.LastWriteTime = File.GetLastWriteTime(file);
                            using var src = File.OpenRead(file);
                            using var dst = e.Open();
                            src.CopyTo(dst);
                            Count(src.Length);
                        }
                    }, ct);
                    File.Move(temp, output);
                    break;
                }
                case "tar":
                case "gz":
                {
                    bool singleGz = targetId == "gz" && items.Count == 1 && File.Exists(items[0]);
                    output = singleGz ? Unique(items[0], ".gz") : Unique(items.Count == 1 && File.Exists(items[0]) ? Path.Combine(Path.GetDirectoryName(items[0])!, Path.GetFileNameWithoutExtension(items[0])) : baseName, targetId == "gz" ? ".tar.gz" : ".tar");
                    var temp = output + ".gajo-tmp";
                    await Task.Run(() =>
                    {
                        using var fs = File.Create(temp);
                        Stream outStream = targetId == "gz" ? new GZipStream(fs, CompressionLevel.Optimal) : fs;
                        try
                        {
                            if (singleGz)
                            {
                                using var src = File.OpenRead(items[0]);
                                src.CopyTo(outStream);
                                Count(src.Length);
                                return;
                            }
                            using var tar = new TarWriter(outStream, TarEntryFormat.Pax, leaveOpen: true);
                            foreach (var (file, name) in entries)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (file == null) { tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name + "/")); continue; }
                                tar.WriteEntry(file, name);
                                Count(new FileInfo(file).Length);
                            }
                        }
                        finally { if (outStream != fs) outStream.Dispose(); }
                    }, ct);
                    File.Move(temp, output);
                    break;
                }
                default: throw new ConversionException("Formato de archivo comprimido no compatible.");
            }
            progress?.Report(1);
            return output;
        }

        /// <summary>(file or null for a folder, name inside the archive with '/' separators).</summary>
        private static IEnumerable<(string? File, string Name)> Expand(IReadOnlyList<string> items)
        {
            foreach (var item in items)
            {
                var clean = item.TrimEnd('\\', '/');
                if (File.Exists(clean)) { yield return (clean, Path.GetFileName(clean)); continue; }
                if (!Directory.Exists(clean)) continue;
                var root = Path.GetDirectoryName(clean)!;
                yield return (null, Rel(root, clean));
                foreach (var d in Directory.EnumerateDirectories(clean, "*", SearchOption.AllDirectories)) yield return (null, Rel(root, d));
                foreach (var f in Directory.EnumerateFiles(clean, "*", SearchOption.AllDirectories)) yield return (f, Rel(root, f));
            }
        }

        private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

        // ------------------------------------------------------------------ extract / convert

        /// <summary>Extracts into a new folder next to the archive (never overwrites anything).</summary>
        public static async Task<string> ExtractAsync(string archive, IProgress<double>? progress, CancellationToken ct)
        {
            var folder = Unique(StripArchiveExt(archive), "");
            Directory.CreateDirectory(folder);
            await Task.Run(() => ExtractTo(archive, folder, progress, ct), ct);
            // An archive holding a single top folder: use that folder directly instead of nesting.
            var subdirs = Directory.GetDirectories(folder);
            if (subdirs.Length == 1 && Directory.GetFiles(folder).Length == 0)
            {
                var only = subdirs[0];
                var target = Unique(Path.Combine(Path.GetDirectoryName(folder)!, Path.GetFileName(only)), "");
                if (!string.Equals(target, folder, StringComparison.OrdinalIgnoreCase))
                {
                    var tmp = folder + ".gajo-tmp";
                    Directory.Move(only, tmp);
                    Directory.Delete(folder);
                    Directory.Move(tmp, Unique(Path.Combine(Path.GetDirectoryName(folder)!, Path.GetFileName(only)), ""));
                    return Path.Combine(Path.GetDirectoryName(folder)!, Path.GetFileName(only));
                }
            }
            return folder;
        }

        private static void ExtractTo(string archive, string folder, IProgress<double>? progress, CancellationToken ct)
        {
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            string Safe(string key)
            {
                // Never write outside the target folder ("../" in malicious archives).
                var full = Path.GetFullPath(Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ConversionException("El archivo comprimido contiene rutas no seguras.");
                return full;
            }

            var name = archive.ToLowerInvariant();
            if (name.EndsWith(".gz") && !name.EndsWith(".tar.gz") && !name.EndsWith(".tgz"))
            {
                // Plain .gz: a single compressed file.
                var outFile = Safe(Path.GetFileNameWithoutExtension(archive));
                using var src = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
                using var dst = File.Create(outFile);
                src.CopyTo(dst);
                return;
            }

            try
            {
                using var arc = ArchiveFactory.Open(archive);
                var files = arc.Entries.Where(e => !e.IsDirectory).ToList();
                long total = Math.Max(1, files.Sum(e => e.Size));
                long done = 0;
                foreach (var e in arc.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(e.Key)) continue;
                    var target = Safe(e.Key);
                    if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var src = e.OpenEntryStream())
                    using (var dst = File.Create(target))
                        src.CopyTo(dst);
                    done += e.Size;
                    progress?.Report(Math.Min(0.99, (double)done / total));
                }
            }
            catch (CryptographicException) { throw new ConversionException("El archivo comprimido está protegido con contraseña."); }
            catch (InvalidFormatException) { throw new ConversionException("El archivo comprimido está dañado o no es compatible."); }
        }

        /// <summary>Archive → another archive format (via a temporary folder).</summary>
        public static async Task<string> ConvertAsync(string archive, string targetId, IProgress<double>? progress, CancellationToken ct)
        {
            var temp = Path.Combine(Path.GetTempPath(), "gajo-arc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                await Task.Run(() => ExtractTo(archive, temp, progress == null ? null : new Progress<double>(p => progress.Report(p * 0.5)), ct), ct);
                var items = Directory.GetFileSystemEntries(temp).ToList();
                var built = await CreateAsync(items, targetId, progress == null ? null : new Progress<double>(p => progress.Report(0.5 + p * 0.5)), ct);
                var stem = StripArchiveExt(archive);
                var ext = built.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : Path.GetExtension(built);
                var output = Unique(stem, ext);
                File.Move(built, output);
                return output;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }
}
