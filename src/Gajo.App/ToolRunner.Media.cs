using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Gajo.Core;
using Gajo.UI;

namespace Gajo
{
    /// <summary>Audio and video tools.</summary>
    internal sealed partial class ToolRunner
    {
        static ToolRunner()
        {
            foreach (var id in new[] { "trim", "silence", "normalize", "mono", "stereo", "acompress", "vcompress", "mute", "vrotate" })
                Implemented.Add(id);
        }

        partial void RunMediaCore(WheelItem item, IReadOnlyList<string> files, ref bool handled)
        {
            handled = true;
            switch (item.Id)
            {
                case "trim": _ = TrimAsync(files[0]); break;
                case "silence": Queue("Quitando silencios", "sin silencios", files, (f, ff, p, ct) => MediaTools.TrimSilenceAsync(ff, f, p, ct)); break;
                case "normalize": Queue("Normalizando volumen", "normalizados", files, (f, ff, p, ct) => MediaTools.NormalizeAsync(ff, f, p, ct)); break;
                case "mono": Queue("Pasando a mono", "en mono", files, (f, ff, p, ct) => MediaTools.ChannelsAsync(ff, f, 1, p, ct)); break;
                case "stereo": Queue("Pasando a estéreo", "en estéreo", files, (f, ff, p, ct) => MediaTools.ChannelsAsync(ff, f, 2, p, ct)); break;
                case "acompress": Queue("Comprimiendo audio", "comprimidos", files, (f, ff, p, ct) => MediaTools.CompressAudioAsync(ff, f, p, ct)); break;
                case "mute": _svc.Enqueue(new Job("Quitando audio", "sin audio", files, PerFile: (f, ff, p, ct) => MediaTools.MuteAsync(ff, f, ct))); break;
                case "vrotate": Queue("Girando vídeo", "girados", files, (f, ff, p, ct) => MediaTools.RotateVideoAsync(ff, f, p, ct)); break;
                case "vcompress": _ = CompressVideoAsync(files); break;
                default: handled = false; break;
            }
        }

        private void Queue(string title, string summary, IReadOnlyList<string> files, Func<string, string, IProgress<double>, System.Threading.CancellationToken, Task<string>> op) =>
            _svc.Enqueue(new Job(title, summary, files, PerFile: op, Determinate: true));

        private async Task TrimAsync(string file)
        {
            try
            {
                var ffmpeg = await _svc.GetFfmpegAsync();
                if (ffmpeg == null) return;
                _svc.Toast.ShowProgress("Abriendo", Path.GetFileName(file), null);
                var info = await MediaTools.ProbeAsync(ffmpeg, file);
                if (info.Duration <= 0) { _svc.Toast.ShowError("No se pudo abrir", "No se pudo leer la duración del archivo."); return; }
                var peaks = info.HasAudio ? await MediaTools.PeaksAsync(ffmpeg, file, 300) : Array.Empty<float>();
                var thumbs = info.HasVideo ? await MediaTools.ThumbStripAsync(ffmpeg, file, info.Duration, 10, 72) : null;
                _svc.Toast.HideToast();

                var dlg = new TrimWindow(ffmpeg, file, info, peaks, thumbs);
                if (dlg.ShowDialog() != true) return;
                double start = dlg.Start, end = dlg.End;
                if (start <= 0.01 && end >= info.Duration - 0.01) { _svc.Toast.ShowInfo("Sin cambios", "La selección ocupa todo el archivo.", 3); return; }
                Queue("Recortando", "recortados", new[] { file }, (f, ff, p, ct) => MediaTools.TrimAsync(ff, f, start, end, p, ct));
            }
            catch (Exception ex)
            {
                ConversionService.Log(ex.ToString());
                _svc.Toast.ShowError("No se pudo abrir la herramienta", ex.Message);
            }
        }

        private async Task CompressVideoAsync(IReadOnlyList<string> files)
        {
            try
            {
                var ffmpeg = await _svc.GetFfmpegAsync();
                if (ffmpeg == null) return;
                var info = await MediaTools.ProbeAsync(ffmpeg, files[0]);
                var dlg = new CompressVideoWindow(files.Count == 1 ? Path.GetFileName(files[0]) : $"{Path.GetFileName(files[0])} (y {files.Count - 1} más)", info);
                if (dlg.ShowDialog() != true) return;
                double mb = dlg.TargetMB;
                Queue($"Comprimiendo a {mb:0.#} MB", "comprimidos", files, (f, ff, p, ct) => MediaTools.CompressVideoToSizeAsync(ff, f, mb, p, ct));
            }
            catch (Exception ex)
            {
                ConversionService.Log(ex.ToString());
                _svc.Toast.ShowError("No se pudo abrir la herramienta", ex.Message);
            }
        }
    }
}
