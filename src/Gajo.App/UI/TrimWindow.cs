using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SPath = System.Windows.Shapes.Path;
using Gajo.Core;

namespace Gajo.UI
{
    /// <summary>"Recortar": waveform (and thumbnails for video) with start/end handles, preview and exact times.</summary>
    internal sealed class TrimWindow : ToolDialog
    {
        private const double W = 600, H = 120;
        private readonly double _duration;
        private readonly string _ffmpeg, _file;
        private readonly bool _video;
        private readonly Canvas _canvas = new() { Width = W, Height = H, ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x26, 0x1E, 0x1A)) };
        private readonly Rectangle _shadeL = new() { Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x0A, 0x08)), IsHitTestVisible = false };
        private readonly Rectangle _shadeR = new() { Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x0A, 0x08)), IsHitTestVisible = false };
        private readonly Rectangle _handleL = Handle(), _handleR = Handle();
        private readonly Line _playhead = new() { Stroke = Brushes.White, StrokeThickness = 1.5, Y1 = 0, Y2 = H, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly TextBox _startBox = new() { Width = 80, Margin = new Thickness(4, 0, 12, 0) };
        private readonly TextBox _endBox = new() { Width = 80, Margin = new Thickness(4, 0, 12, 0) };
        private readonly TextBlock _lengthText = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly Image _frameStart = new() { Width = 180, Height = 101, Stretch = Stretch.Uniform };
        private readonly Image _frameEnd = new() { Width = 180, Height = 101, Stretch = Stretch.Uniform };
        private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private MediaPlayer? _player;
        private string? _previewWav;
        private Button? _playButton;
        private double _start, _end;
        private int _drag = -1;   // 0 start, 1 end, 2 both

        public double Start => _start;
        public double End => _end;

        private static Rectangle Handle() => new()
        {
            Width = 8, Height = H, Fill = new SolidColorBrush(Color.FromRgb(0xF0, 0x55, 0x24)), Cursor = Cursors.SizeWE, RadiusX = 3, RadiusY = 3,
        };

        public TrimWindow(string ffmpeg, string file, MediaTools.MediaInfo info, float[] peaks, byte[]? thumbs) : base(info.HasVideo ? "Recortar vídeo" : "Recortar audio")
        {
            _ffmpeg = ffmpeg; _file = file; _duration = Math.Max(0.1, info.Duration); _video = info.HasVideo;
            _start = 0; _end = _duration;

            if (thumbs != null)
            {
                var strip = LoadJpeg(thumbs);
                if (strip != null) _canvas.Children.Add(new Image { Source = strip, Width = W, Height = H, Stretch = Stretch.Fill, Opacity = 0.75 });
            }
            if (peaks.Length > 0 && info.HasAudio) _canvas.Children.Add(Waveform(peaks, thumbs != null));
            _canvas.Children.Add(_shadeL);
            _canvas.Children.Add(_shadeR);
            _canvas.Children.Add(_playhead);
            _canvas.Children.Add(_handleL);
            _canvas.Children.Add(_handleR);
            _handleL.MouseLeftButtonDown += (_, e) => BeginDrag(0, e);
            _handleR.MouseLeftButtonDown += (_, e) => BeginDrag(1, e);
            _canvas.MouseLeftButtonDown += (_, e) => { if (_drag < 0) BeginDrag(2, e); };
            _canvas.MouseMove += OnMove;
            _canvas.MouseLeftButtonUp += (_, _) => { _drag = -1; _canvas.ReleaseMouseCapture(); };

            var host = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = _canvas, HorizontalAlignment = HorizontalAlignment.Center };
            var stack = new StackPanel();
            if (_video)
            {
                var frames = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
                frames.Children.Add(FrameBox("Inicio", _frameStart));
                frames.Children.Add(new Border { Width = 16 });
                frames.Children.Add(FrameBox("Final", _frameEnd));
                stack.Children.Add(frames);
                _frameTimer.Tick += async (_, _) => { _frameTimer.Stop(); await RefreshFramesAsync(); };
            }
            stack.Children.Add(host);

            var times = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            times.Children.Add(new TextBlock { Text = "Inicio", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            times.Children.Add(_startBox);
            times.Children.Add(new TextBlock { Text = "Final", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            times.Children.Add(_endBox);
            _lengthText.Foreground = Muted;
            times.Children.Add(_lengthText);
            _startBox.LostFocus += (_, _) => ApplyBoxes();
            _endBox.LostFocus += (_, _) => ApplyBoxes();
            _startBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyBoxes(); };
            _endBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyBoxes(); };
            stack.Children.Add(times);
            Body.Children.Add(stack);

            if (!_video && info.HasAudio)
            {
                _playButton = AddButton(FooterLeft, "▶  Escuchar", false, () => _ = TogglePlayAsync());
                _playTimer.Tick += (_, _) => TickPlay();
            }
            AddButton(FooterLeft, "Restablecer", false, () => { _start = 0; _end = _duration; Layout(); });
            AddButton(Footer, "Recortar", true, () => { StopPlay(); DialogResult = true; });
            Closed += (_, _) =>
            {
                StopPlay();
                _player?.Close();
                if (_previewWav != null) try { File.Delete(_previewWav); } catch { }
            };
            Layout();
            if (_video) _ = RefreshFramesAsync();
        }

        private static FrameworkElement FrameBox(string label, Image img)
        {
            var sp = new StackPanel();
            sp.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x26, 0x1E, 0x1A)), CornerRadius = new CornerRadius(8), Child = img, ClipToBounds = true });
            sp.Children.Add(new TextBlock { Text = label, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            return sp;
        }

        private static UIElement Waveform(float[] peaks, bool overThumbs)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                double mid = H / 2, step = W / peaks.Length;
                for (int i = 0; i < peaks.Length; i++)
                {
                    double x = i * step + step / 2, a = Math.Max(1, peaks[i] * (H / 2 - 6));
                    ctx.BeginFigure(new Point(x, mid - a), false, false);
                    ctx.LineTo(new Point(x, mid + a), true, false);
                }
            }
            geo.Freeze();
            return new SPath
            {
                Data = geo, StrokeThickness = Math.Max(1, W / peaks.Length * 0.6), IsHitTestVisible = false,
                Stroke = new SolidColorBrush(overThumbs ? Color.FromArgb(0xD0, 0xFF, 0xB0, 0x80) : Color.FromRgb(0xFF, 0x8A, 0x4C)),
            };
        }

        private static BitmapSource? LoadJpeg(byte[] bytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private double X(double t) => t / _duration * W;
        private double T(double x) => Math.Clamp(x / W * _duration, 0, _duration);

        private Point _dragStartPoint;
        private double _dragStartA, _dragStartB;

        private void BeginDrag(int which, MouseButtonEventArgs e)
        {
            _drag = which;
            _dragStartPoint = e.GetPosition(_canvas);
            _dragStartA = _start; _dragStartB = _end;
            if (which == 2)
            {
                // Click outside the selection moves the nearest handle there.
                double t = T(_dragStartPoint.X);
                if (t < _start || t > _end)
                {
                    if (Math.Abs(t - _start) < Math.Abs(t - _end)) { _start = t; _drag = 0; } else { _end = t; _drag = 1; }
                    _dragStartA = _start; _dragStartB = _end;
                    Layout();
                }
            }
            _canvas.CaptureMouse();
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_drag < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            double dt = (e.GetPosition(_canvas).X - _dragStartPoint.X) / W * _duration;
            double minLen = Math.Min(0.1, _duration / 2);
            if (_drag == 0) _start = Math.Clamp(_dragStartA + dt, 0, _end - minLen);
            else if (_drag == 1) _end = Math.Clamp(_dragStartB + dt, _start + minLen, _duration);
            else
            {
                double len = _dragStartB - _dragStartA;
                _start = Math.Clamp(_dragStartA + dt, 0, _duration - len);
                _end = _start + len;
            }
            Layout();
        }

        private void ApplyBoxes()
        {
            if (TryParse(_startBox.Text, out var s)) _start = Math.Clamp(s, 0, _duration);
            if (TryParse(_endBox.Text, out var en)) _end = Math.Clamp(en, 0, _duration);
            if (_end <= _start) _end = Math.Min(_duration, _start + 0.1);
            Layout();
        }

        private static bool TryParse(string text, out double seconds)
        {
            seconds = 0;
            var parts = text.Trim().Replace(',', '.').Split(':');
            try
            {
                foreach (var p in parts) seconds = seconds * 60 + double.Parse(p, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private static string Fmt(double s)
        {
            var ts = TimeSpan.FromSeconds(s);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss\.f") : ts.ToString(@"m\:ss\.f");
        }

        private void Layout()
        {
            double a = X(_start), b = X(_end);
            Canvas.SetLeft(_shadeL, 0); _shadeL.Width = Math.Max(0, a); _shadeL.Height = H;
            Canvas.SetLeft(_shadeR, b); _shadeR.Width = Math.Max(0, W - b); _shadeR.Height = H;
            Canvas.SetLeft(_handleL, Math.Max(0, a - 4));
            Canvas.SetLeft(_handleR, Math.Min(W - 8, b - 4));
            _startBox.Text = Fmt(_start);
            _endBox.Text = Fmt(_end);
            _lengthText.Text = $"Duración: {Fmt(_end - _start)} de {Fmt(_duration)}";
            if (_video) { _frameTimer.Stop(); _frameTimer.Start(); }
        }

        private async Task RefreshFramesAsync()
        {
            var a = await MediaTools.FrameAsync(_ffmpeg, _file, _start, 360);
            var b = await MediaTools.FrameAsync(_ffmpeg, _file, Math.Max(0, _end - 0.05), 360);
            if (a != null) _frameStart.Source = LoadJpeg(a);
            if (b != null) _frameEnd.Source = LoadJpeg(b);
        }

        // ------------------------------------------------------------------ preview playback

        private async Task TogglePlayAsync()
        {
            if (_player != null && _playTimer.IsEnabled) { StopPlay(); return; }
            if (_previewWav == null)
            {
                _playButton!.Content = "Preparando…";
                try { _previewWav = await MediaTools.PreviewWavAsync(_ffmpeg, _file); }
                catch { _playButton.Content = "▶  Escuchar"; return; }
            }
            _player ??= new MediaPlayer();
            _player.Open(new Uri(_previewWav));
            _player.Position = TimeSpan.FromSeconds(_start);
            _player.Play();
            _playhead.Visibility = Visibility.Visible;
            _playTimer.Start();
            _playButton!.Content = "■  Parar";
        }

        private void TickPlay()
        {
            if (_player == null) return;
            double t = _player.Position.TotalSeconds;
            _playhead.X1 = _playhead.X2 = X(t);
            if (t >= _end) StopPlay();
        }

        private void StopPlay()
        {
            _playTimer.Stop();
            _player?.Stop();
            _playhead.Visibility = Visibility.Collapsed;
            if (_playButton != null) _playButton.Content = "▶  Escuchar";
        }
    }
}
