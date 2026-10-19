using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gajo.Core;
using Gajo.UI;

namespace Gajo
{
    /// <summary>
    /// A unit of work for the queue: either one operation per file (PerFile) or one operation for
    /// the whole selection (Whole, e.g. several images → one PDF). Each returns the new file/folder.
    /// </summary>
    internal sealed record Job(
        string Title,
        string Summary,
        IReadOnlyList<string> Files,
        Func<string, string, IProgress<double>, CancellationToken, Task<string>>? PerFile = null,
        Func<string, IProgress<double>, CancellationToken, Task<string>>? Whole = null,
        bool Determinate = false);

    /// <summary>Runs jobs one after another (ffmpeg runs out of process), with progress, cancel and result toasts.</summary>
    internal sealed class ConversionService
    {
        private readonly Settings _settings;
        private readonly ToastWindow _toast;
        private readonly Queue<Job> _queue = new();
        private bool _running;
        private CancellationTokenSource? _cts;

        public ConversionService(Settings settings, ToastWindow toast)
        {
            _settings = settings;
            _toast = toast;
        }

        public ToastWindow Toast => _toast;

        public void Enqueue(IReadOnlyList<string> files, FormatTarget target)
        {
            var kind = files.Count > 0 ? Formats.KindOf(files[0]) : MediaKind.Unknown;
            if (kind == MediaKind.Image && target.Id == "pdf" && files.Count > 1)
            {
                // Several images → a single PDF, one page each.
                Enqueue(new Job($"Creando PDF con {files.Count} imágenes", "PDF creado", files,
                    Whole: (ffmpeg, p, ct) => ImageTools.ImagesToPdfAsync(ffmpeg, files, p, ct), Determinate: true));
                return;
            }
            bool determinate = kind is MediaKind.Audio or MediaKind.Video || (kind == MediaKind.Document && Formats.NormalizedExtension(files[0]) == "pdf");
            Enqueue(new Job($"Convirtiendo a {target.Label}", $"convertidos a {target.Label}", files,
                PerFile: (file, ffmpeg, p, ct) => new Converter(ffmpeg, _settings.JpegQuality).ConvertAsync(file, target, p, ct),
                Determinate: determinate));
        }

        public void Enqueue(Job job)
        {
            _queue.Enqueue(job);
            if (!_running) _ = RunAsync();
        }

        private async Task RunAsync()
        {
            _running = true;
            try
            {
                while (_queue.Count > 0)
                {
                    var ffmpeg = await GetFfmpegAsync();
                    if (ffmpeg == null) { _queue.Clear(); break; }
                    await ProcessAsync(_queue.Dequeue(), ffmpeg);
                }
            }
            finally { _running = false; }
        }

        private async Task ProcessAsync(Job job, string ffmpeg)
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var errors = new List<string>();
            string? lastOutput = null;
            int done = 0;

            if (job.Whole != null)
            {
                _toast.ShowProgress(job.Title, job.Files.Count == 1 ? Path.GetFileName(job.Files[0]) : $"{job.Files.Count} archivos",
                    job.Determinate ? 0 : null, () => _cts?.Cancel());
                try
                {
                    lastOutput = await job.Whole(ffmpeg, new Progress<double>(p => _toast.UpdateProgress(p)), ct);
                    done = job.Files.Count;
                }
                catch (OperationCanceledException) { _toast.ShowInfo("Cancelado", "", 3); return; }
                catch (ConversionException ex) { errors.Add(ex.Message); Log(ex.Log); }
                catch (Exception ex) { errors.Add(ex.Message); Log(ex.ToString()); }
                Finish(job, done, job.Files.Count, errors, lastOutput, whole: true);
                return;
            }

            int total = job.Files.Count;
            for (int i = 0; i < total; i++)
            {
                var file = job.Files[i];
                var name = Path.GetFileName(file.TrimEnd('\\'));
                string counter = total > 1 ? $" ({i + 1}/{total})" : "";
                _toast.ShowProgress(job.Title + counter, name, job.Determinate ? 0 : null, () => _cts?.Cancel());

                int index = i;
                var progress = new Progress<double>(p => _toast.UpdateProgress((index + p) / total));
                try
                {
                    lastOutput = await job.PerFile!(file, ffmpeg, progress, ct);
                    done++;
                }
                catch (OperationCanceledException)
                {
                    _toast.ShowInfo("Cancelado", done > 0 ? $"{done} de {total} archivos ya estaban listos." : "", 4);
                    return;
                }
                catch (ConversionException ex)
                {
                    errors.Add(total > 1 ? $"{name}: {ex.Message}" : ex.Message);
                    Log(ex.Log);
                }
                catch (Exception ex)
                {
                    errors.Add(total > 1 ? $"{name}: {ex.Message}" : ex.Message);
                    Log(ex.ToString());
                }
            }
            Finish(job, done, total, errors, lastOutput, whole: false);
        }

        private void Finish(Job job, int done, int total, List<string> errors, string? lastOutput, bool whole)
        {
            if (errors.Count == 0)
            {
                if (_settings.ShowNotifications)
                {
                    string body = total == 1 || whole ? Path.GetFileName(lastOutput!.TrimEnd('\\')) : $"{total} archivos {job.Summary}";
                    _toast.ShowDone("Listo", body, lastOutput);
                }
                else _toast.HideToast();
            }
            else if (done == 0)
                _toast.ShowError(total == 1 || whole ? "No se pudo completar" : "No se pudo con ningún archivo", string.Join("\n", errors.Take(3)));
            else
                _toast.ShowError($"{done} de {total} listos", string.Join("\n", errors.Take(3)));
        }

        // ------------------------------------------------------------------ ffmpeg

        public async Task<string?> GetFfmpegAsync()
        {
            // The first call unpacks the built-in engine (~1 s), so keep it off the UI thread.
            var custom = _settings.FfmpegPath;
            var path = await Task.Run(() => Ffmpeg.Locate(custom));
            if (path == null)
                _toast.ShowError("Falta el motor de conversión",
                    "Este Gajo.exe no lleva FFmpeg integrado. Usa la versión publicada o compílala con compilar.bat.");
            return path;
        }

        // ------------------------------------------------------------------ log

        public static string LogPath =>
            Path.Combine(Path.GetTempPath(), "Gajo", "gajo.log");

        public static void Log(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(LogPath, $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{text}\n");
            }
            catch { }
        }
    }
}
