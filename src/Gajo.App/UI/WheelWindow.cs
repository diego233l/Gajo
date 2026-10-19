using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Gajo.Core;
using Gajo.Interop;

namespace Gajo.UI
{
    /// <summary>
    /// The radial menu. A borderless, topmost, non-activating window that is itself an OLE drop
    /// target: it pops up under the cursor in the middle of an Explorer drag, Windows tells it which
    /// files are being dragged (DragEnter), and the user drops on a segment. The original file is
    /// never moved: the drag is always answered with "Copy".
    /// Three modes share the window: conversions, tools and actions. It can also be driven from the
    /// keyboard (arrows, Enter, Esc, Tab) for files selected in Explorer.
    /// </summary>
    internal sealed class WheelWindow : Window
    {
        private const double Size = 420;
        private const double C = Size / 2;
        private const double DiscR = 184;
        private const double OuterR = 174;
        private const double InnerR = 68;
        private const double HubR = 60;
        private const double ArmedOpacity = 0.02;   // invisible but still hit-testable for OLE

        private readonly Canvas _root = new() { Width = Size, Height = Size };
        private readonly Canvas _petalLayer = new() { Width = Size, Height = Size };
        private readonly Ellipse _disc = new() { Width = DiscR * 2, Height = DiscR * 2, StrokeThickness = 1.5 };
        private readonly Ellipse _hub = new() { Width = HubR * 2, Height = HubR * 2, StrokeThickness = 1.5 };
        private readonly TextBlock _hubMode = new();
        private readonly TextBlock _hubTop = new();
        private readonly TextBlock _hubMain = new();
        private readonly TextBlock _hubSub = new();
        private readonly ScaleTransform _scale = new(1, 1, C, C);
        private readonly DropTargetHelper _dragImage = new();
        private readonly DispatcherTimer _armTimeout = new() { Interval = TimeSpan.FromMilliseconds(1500) };
        private readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

        private readonly List<(Path Petal, FrameworkElement Label, List<TextBlock> Texts, WheelItem Item)> _petals = new();
        private Theme _theme = Theme.Current;
        private WheelMode _mode;
        private IReadOnlyList<string>? _files;
        private WheelContent? _content;
        private int _hover = -1;
        private bool _revealed;
        private bool _dropped;
        private IntPtr _hwnd;

        public event Action<WheelMode, WheelItem, IReadOnlyList<string>>? ItemChosen;

        public bool IsOpen { get; private set; }
        public bool IsKeyboardMode { get; private set; }
        public WheelMode Mode => _mode;

        private static readonly FontFamily UiFont = new("Segoe UI Variable Display, Segoe UI");
        private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        public WheelWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Width = Size;
            Height = Size;
            AllowDrop = true;
            Title = "Gajo";

            BuildVisuals();
            Content = _root;

            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeNoActivateToolWindow(_hwnd);
            };
            new WindowInteropHelper(this).EnsureHandle();

            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;
            MouseMove += (_, e) => { if (IsKeyboardMode) SetHover(IndexAt(e.GetPosition(_root))); };
            MouseLeftButtonUp += (_, e) =>
            {
                if (!IsKeyboardMode) return;
                int idx = IndexAt(e.GetPosition(_root));
                if (idx >= 0) Choose(idx); else Dismiss(false);
            };

            _armTimeout.Tick += (_, _) => { _armTimeout.Stop(); if (!_revealed) Dismiss(true); };
            _releaseTimer.Tick += (_, _) => { _releaseTimer.Stop(); if (!_dropped) Dismiss(false); };
        }

        // ------------------------------------------------------------------ visuals

        private void BuildVisuals()
        {
            Canvas.SetLeft(_disc, C - DiscR);
            Canvas.SetTop(_disc, C - DiscR);
            _root.Children.Add(_disc);
            _root.Children.Add(_petalLayer);

            Canvas.SetLeft(_hub, C - HubR);
            Canvas.SetTop(_hub, C - HubR);
            _root.Children.Add(_hub);

            foreach (var t in new[] { _hubMode, _hubTop, _hubMain, _hubSub })
            {
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.TextAlignment = TextAlignment.Center;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.MaxWidth = HubR * 2 - 18;
                t.FontFamily = UiFont;
            }
            _hubMode.FontSize = 9; _hubMode.FontWeight = FontWeights.Bold;
            _hubTop.FontSize = 10.5; _hubTop.Opacity = 0.8;
            _hubMain.FontSize = 17; _hubMain.FontWeight = FontWeights.Bold;
            _hubSub.FontSize = 10.5; _hubSub.Opacity = 0.75;

            var stack = new StackPanel { Width = HubR * 2, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(_hubMode);
            stack.Children.Add(_hubTop);
            stack.Children.Add(_hubMain);
            stack.Children.Add(_hubSub);
            var hubHost = new Grid { Width = HubR * 2, Height = HubR * 2, IsHitTestVisible = false };
            hubHost.Children.Add(stack);
            Canvas.SetLeft(hubHost, C - HubR);
            Canvas.SetTop(hubHost, C - HubR);
            _root.Children.Add(hubHost);

            _root.RenderTransform = _scale;
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            _disc.Fill = _theme.DiscFill;
            _disc.Stroke = _theme.DiscStroke;
            _disc.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = _theme.Dark ? 0.55 : 0.35, Color = _theme.Shadow };
            _hub.Fill = _theme.HubFill;
            _hub.Stroke = _theme.HubStroke;
            _hub.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Direction = 270, Opacity = 0.2, Color = _theme.Shadow };
            _hubMode.Foreground = _theme.ChipText;
            foreach (var t in new[] { _hubTop, _hubMain, _hubSub }) t.Foreground = _theme.Label;
        }

        private void BuildPetals()
        {
            _petalLayer.Children.Clear();
            _petals.Clear();
            var items = _content?.Items ?? Array.Empty<WheelItem>();
            int n = items.Count;
            if (n == 0) return;
            double step = 360.0 / n;

            for (int i = 0; i < n; i++)
            {
                double mid = -90 + i * step;
                var path = new Path
                {
                    Data = Sector(mid - step / 2, mid + step / 2, InnerR, OuterR),
                    Fill = _theme.PetalFill,
                    Stroke = _theme.PetalStroke,
                    StrokeThickness = 6,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                _petalLayer.Children.Add(path);

                var item = items[i];
                var texts = new List<TextBlock>();
                FrameworkElement label;
                if (item.Glyph == null)
                {
                    var t = new TextBlock { Text = item.Label, FontSize = n > 6 ? 16 : 18, FontWeight = FontWeights.SemiBold, FontFamily = UiFont, Foreground = _theme.Label };
                    texts.Add(t);
                    label = t;
                }
                else
                {
                    var icon = new TextBlock { Text = item.Glyph, FontFamily = IconFont, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, Foreground = _theme.Label };
                    var cap = new TextBlock
                    {
                        Text = item.Label.ToUpperInvariant(), FontFamily = UiFont, FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 84,
                        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0), Foreground = _theme.Label,
                    };
                    texts.Add(icon); texts.Add(cap);
                    var sp = new StackPanel();
                    sp.Children.Add(icon);
                    sp.Children.Add(cap);
                    label = sp;
                }
                label.IsHitTestVisible = false;
                label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                double r = (InnerR + OuterR) / 2 + 4;
                double rad = mid * Math.PI / 180;
                Canvas.SetLeft(label, C + r * Math.Cos(rad) - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, C + r * Math.Sin(rad) - label.DesiredSize.Height / 2);
                _petalLayer.Children.Add(label);
                _petals.Add((path, label, texts, item));
            }
        }

        private static Geometry Sector(double a0Deg, double a1Deg, double r0, double r1)
        {
            Point P(double r, double deg)
            {
                double rad = deg * Math.PI / 180;
                return new Point(C + r * Math.Cos(rad), C + r * Math.Sin(rad));
            }
            bool large = (a1Deg - a0Deg) > 180;
            var fig = new PathFigure { StartPoint = P(r1, a0Deg), IsClosed = true, IsFilled = true };
            fig.Segments.Add(new ArcSegment(P(r1, a1Deg), new System.Windows.Size(r1, r1), 0, large, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(P(r0, a1Deg), true));
            fig.Segments.Add(new ArcSegment(P(r0, a0Deg), new System.Windows.Size(r0, r0), 0, large, SweepDirection.Counterclockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            g.Freeze();
            return g;
        }

        private void SetHover(int index)
        {
            if (index == _hover) return;
            if (_hover >= 0 && _hover < _petals.Count)
            {
                _petals[_hover].Petal.Fill = _theme.PetalFill;
                foreach (var t in _petals[_hover].Texts) t.Foreground = _theme.Label;
            }
            _hover = index;
            if (_hover >= 0 && _hover < _petals.Count)
            {
                _petals[_hover].Petal.Fill = _theme.PetalHover;
                foreach (var t in _petals[_hover].Texts) t.Foreground = _theme.LabelHover;
            }
            UpdateHub();
        }

        private void UpdateHub()
        {
            _hubMode.Text = WheelCatalog.ModeName(_mode).ToUpperInvariant();
            if (_content == null) { _hubTop.Text = ""; _hubMain.Text = ""; _hubSub.Text = ""; return; }

            if (_content.Items.Count == 0 || _content.Inputs.Count == 0)
            {
                _hubTop.Text = "";
                _hubMain.Text = "—";
                _hubSub.Text = _content.EmptyText;
                return;
            }

            if (_hover >= 0 && _hover < _petals.Count)
            {
                var item = _petals[_hover].Item;
                _hubTop.Text = _mode == WheelMode.Convert ? "Convertir a" : "";
                _hubMain.Text = item.Label;
                _hubMain.FontSize = item.Label.Length > 9 ? 12.5 : 17;
                _hubSub.Text = _content.Inputs.Count == 1 ? "" : $"{_content.Inputs.Count} elementos";
                return;
            }

            long bytes = 0;
            foreach (var f in _content.Inputs) { try { if (System.IO.File.Exists(f)) bytes += new System.IO.FileInfo(f).Length; } catch { } }
            _hubTop.Text = _content.Inputs.Count == 1 ? System.IO.Path.GetFileName(_content.Inputs[0].TrimEnd('\\')) : $"{_content.Inputs.Count} elementos";
            _hubMain.FontSize = 17;
            _hubMain.Text = bytes > 0 ? OutputNaming.HumanSize(bytes) : "Carpeta";
            _hubSub.Text = KindName(_content.Kind);
        }

        private static string KindName(MediaKind k) => k switch
        {
            MediaKind.Image => "Imagen",
            MediaKind.Audio => "Audio",
            MediaKind.Video => "Vídeo",
            MediaKind.Document => "Documento",
            _ => "",
        };

        // ------------------------------------------------------------------ lifecycle

        private void Reset(WheelMode mode)
        {
            _theme = Theme.Current;
            ApplyTheme();
            _mode = mode;
            _revealed = false;
            _dropped = false;
            _files = null;
            _content = null;
            _hover = -1;
            _petalLayer.Children.Clear();
            _petals.Clear();
            UpdateHub();
            _root.BeginAnimation(OpacityProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleX = _scale.ScaleY = 1;
        }

        /// <summary>A trigger key was tapped mid-drag. Cursor in physical pixels.</summary>
        public void Arm(WheelMode mode, int x, int y)
        {
            if (IsOpen) { SwitchMode(mode); return; }
            IsOpen = true;
            IsKeyboardMode = false;
            Reset(mode);
            _root.Opacity = ArmedOpacity;
            PlaceCenteredAt(x, y);
            Show();
            PlaceCenteredAt(x, y);   // again, now that the window has the target monitor's DPI
            _armTimeout.Start();
        }

        /// <summary>Keyboard use: wheel for files selected in Explorer.</summary>
        public void OpenForFiles(WheelMode mode, IReadOnlyList<string> files, int x, int y)
        {
            if (IsOpen) Dismiss(true);
            IsOpen = true;
            IsKeyboardMode = true;
            Reset(mode);
            _files = files;
            _content = WheelCatalog.For(mode, files);
            BuildPetals();
            _root.Opacity = ArmedOpacity;
            PlaceCenteredAt(x, y);
            Show();
            PlaceCenteredAt(x, y);
            SetHover(_petals.Count > 0 ? 0 : -1);
            UpdateHub();
            Reveal();
        }

        public void SwitchMode(WheelMode mode)
        {
            if (!IsOpen || mode == _mode) return;
            _mode = mode;
            if (_files != null)
            {
                _content = WheelCatalog.For(mode, _files);
                _hover = -1;
                BuildPetals();
                if (IsKeyboardMode && _petals.Count > 0) SetHover(0);
            }
            UpdateHub();
            if (_revealed)
            {
                var pulse = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
        }

        public void CycleMode()
        {
            SwitchMode(_mode switch { WheelMode.Convert => WheelMode.Tools, WheelMode.Tools => WheelMode.Actions, _ => WheelMode.Convert });
        }

        /// <summary>Arrow keys: +1 clockwise, -1 counter-clockwise.</summary>
        public void KeyNav(int delta)
        {
            if (!IsOpen || _petals.Count == 0) return;
            int i = _hover < 0 ? 0 : ((_hover + delta) % _petals.Count + _petals.Count) % _petals.Count;
            SetHover(i);
        }

        public void KeyActivate()
        {
            if (!IsOpen) return;
            if (_hover >= 0) Choose(_hover); else Dismiss(false);
        }

        private void Choose(int idx)
        {
            var files = _content?.Inputs.ToList();
            var item = idx >= 0 && idx < _petals.Count ? _petals[idx].Item : null;
            var mode = _mode;
            _dropped = true;
            Dismiss(false);
            if (files != null && item != null && files.Count > 0)
                Dispatcher.BeginInvoke(new Action(() => ItemChosen?.Invoke(mode, item, files)));
        }

        private void PlaceCenteredAt(int x, int y)
        {
            if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
            double scale = 1.0;
            try
            {
                uint dpi = IsVisible ? Native.GetDpiForWindow(_hwnd) : 0;
                scale = dpi > 0 ? dpi / 96.0 : Native.DpiScaleAt(new Native.POINT { X = x, Y = y });
            }
            catch { }
            int px = (int)Math.Round(Size * scale);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x - px / 2, y - px / 2, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private void Reveal()
        {
            if (_revealed) return;
            _revealed = true;
            _armTimeout.Stop();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(170);
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(ArmedOpacity, 1, dur) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.72, 1, dur) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.72, 1, dur) { EasingFunction = ease });
        }

        /// <summary>Mouse button released somewhere. Give the OLE Drop a moment to arrive first.</summary>
        public void OnMouseReleased()
        {
            if (!IsOpen || IsKeyboardMode) return;
            _releaseTimer.Stop();
            _releaseTimer.Start();
        }

        public void Dismiss(bool immediate)
        {
            if (!IsOpen) return;
            IsOpen = false;
            IsKeyboardMode = false;
            _armTimeout.Stop();
            _releaseTimer.Stop();

            if (immediate || !_revealed)
            {
                Hide();
                return;
            }
            var fade = new DoubleAnimation(_root.Opacity, 0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) => { if (!IsOpen) Hide(); };
            _root.BeginAnimation(OpacityProperty, fade);
        }

        // ------------------------------------------------------------------ drag & drop

        private static string[]? FilesFrom(DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    return e.Data.GetData(DataFormats.FileDrop) as string[];
            }
            catch { }
            return null;
        }

        private static DragDropEffects CopyIfAllowed(DragEventArgs e) =>
            (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (!IsOpen || IsKeyboardMode) { e.Effects = DragDropEffects.None; e.Handled = true; return; }

            if (_files == null)
            {
                var files = FilesFrom(e);
                if (files == null || files.Length == 0)
                {
                    // Not a file drag (text, a window being moved…): stay invisible and go away.
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    Dismiss(true);
                    return;
                }
                _files = files;
                _content = WheelCatalog.For(_mode, files);
                BuildPetals();
                UpdateHub();
                Reveal();
            }

            _dragImage.DragEnter(_hwnd, e.Data, (int)DragDropEffects.None);
            OnDragOver(sender, e);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            int idx = IndexAt(e.GetPosition(_root));
            SetHover(idx);
            e.Effects = idx >= 0 ? CopyIfAllowed(e) : DragDropEffects.None;
            e.Handled = true;
            _dragImage.DragOver((int)e.Effects);
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            SetHover(-1);
            _dragImage.DragLeave();
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            int idx = IndexAt(e.GetPosition(_root));
            var effect = idx >= 0 ? CopyIfAllowed(e) : DragDropEffects.None;
            e.Effects = effect;
            e.Handled = true;
            _dragImage.Drop(e.Data, (int)effect);
            if (effect == DragDropEffects.None) { _dropped = true; Dismiss(false); return; }
            Choose(idx);
        }

        private int IndexAt(Point p)
        {
            if (_petals.Count == 0) return -1;
            double dx = p.X - C, dy = p.Y - C;
            double r = Math.Sqrt(dx * dx + dy * dy);
            if (r < InnerR - 2 || r > DiscR + 6) return -1;
            double step = 360.0 / _petals.Count;
            double deg = Math.Atan2(dy, dx) * 180 / Math.PI;   // screen coords: clockwise
            double rel = (deg + 90 + step / 2) % 360;
            if (rel < 0) rel += 360;
            return Math.Clamp((int)(rel / step), 0, _petals.Count - 1);
        }
    }
}
