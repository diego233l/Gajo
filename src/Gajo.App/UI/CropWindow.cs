using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Gajo.UI
{
    /// <summary>"Recortar imagen": drag the frame or its handles, pick a ratio, or type the size.</summary>
    internal sealed class CropWindow : ToolDialog
    {
        private const double MaxView = 560;
        private readonly int _w, _h;              // source pixels
        private readonly double _s;               // view scale
        private Rect _crop;                       // source pixels
        private double? _ratio;                   // w/h, null = free
        private readonly Canvas _canvas = new();
        private readonly Path _mask = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0x20, 0x14, 0x0E)), IsHitTestVisible = false };
        private readonly Rectangle _frame = new() { Stroke = new SolidColorBrush(Color.FromRgb(0xF0, 0x40, 0x20)), StrokeThickness = 2, Fill = Brushes.Transparent, Cursor = Cursors.SizeAll };
        private readonly Rectangle[] _handles = new Rectangle[8];
        private readonly TextBox _wBox = new() { Width = 64, Margin = new Thickness(4, 0, 10, 0) };
        private readonly TextBox _hBox = new() { Width = 64, Margin = new Thickness(4, 0, 4, 0) };
        private bool _updatingBoxes;

        private int _dragHandle = -1;             // -1 none, 8 = move
        private Point _dragStart;
        private Rect _dragOrig;

        public Int32Rect Result { get; private set; }

        private static readonly (string Label, double? Ratio)[] Ratios =
        {
            ("Libre", null), ("1:1", 1), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("4:3", 4.0 / 3), ("3:4", 3.0 / 4),
        };

        public CropWindow(BitmapSource image) : base("Recortar imagen")
        {
            _w = image.PixelWidth; _h = image.PixelHeight;
            _s = Math.Min(1, Math.Min(MaxView / _w, MaxView / _h));
            if (_w * _s < 200 && _h * _s < 200) _s = Math.Min(MaxView / _w, MaxView / _h);
            _crop = new Rect(0, 0, _w, _h);

            double vw = _w * _s, vh = _h * _s;
            _canvas.Width = vw; _canvas.Height = vh;
            _canvas.ClipToBounds = false;
            _canvas.Children.Add(new Image { Source = image, Width = vw, Height = vh, Stretch = Stretch.Fill });
            _canvas.Children.Add(_mask);
            _canvas.Children.Add(_frame);
            var cursors = new[] { Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE };
            for (int i = 0; i < 8; i++)
            {
                _handles[i] = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xF0, 0x40, 0x20)), Stroke = Brushes.White, StrokeThickness = 1.5, Cursor = cursors[i] };
                int idx = i;
                _handles[i].MouseLeftButtonDown += (_, e) => BeginDrag(idx, e);
                _canvas.Children.Add(_handles[i]);
            }
            _frame.MouseLeftButtonDown += (_, e) => BeginDrag(8, e);
            _canvas.MouseMove += OnMove;
            _canvas.MouseLeftButtonUp += (_, _) => { _dragHandle = -1; _canvas.ReleaseMouseCapture(); };

            var imageHost = new Border { Padding = new Thickness(8), Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xE2, 0xD8)), CornerRadius = new CornerRadius(10), Child = _canvas, HorizontalAlignment = HorizontalAlignment.Center };

            var ratioRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 6) };
            ratioRow.Children.Add(Label("Proporción"));
            ratioRow.Children.Add(Chips(Array.ConvertAll(Ratios, r => r.Label), 0, i => SetRatio(Ratios[i].Ratio)));

            var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
            sizeRow.Children.Add(Label("Tamaño"));
            sizeRow.Children.Add(new TextBlock { Text = "An", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            sizeRow.Children.Add(_wBox);
            sizeRow.Children.Add(new TextBlock { Text = "Al", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            sizeRow.Children.Add(_hBox);
            sizeRow.Children.Add(new TextBlock { Text = "px", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            _wBox.LostFocus += (_, _) => ApplyBoxes(true);
            _hBox.LostFocus += (_, _) => ApplyBoxes(false);
            _wBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyBoxes(true); };
            _hBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyBoxes(false); };

            var stack = new StackPanel();
            stack.Children.Add(imageHost);
            stack.Children.Add(ratioRow);
            stack.Children.Add(sizeRow);
            Body.Children.Add(stack);

            AddButton(FooterLeft, "Restablecer", false, () => { _crop = new Rect(0, 0, _w, _h); if (_ratio.HasValue) SetRatio(_ratio); Layout(); });
            AddButton(Footer, "Aplicar", true, () =>
            {
                var r = Normalize(_crop);
                Result = new Int32Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));
                DialogResult = true;
            });
            Layout();
        }

        private void SetRatio(double? ratio)
        {
            _ratio = ratio;
            if (ratio is double k)
            {
                // Largest centred rectangle of that ratio inside the current one.
                double w = _crop.Width, h = _crop.Height;
                if (w / h > k) w = h * k; else h = w / k;
                _crop = new Rect(_crop.X + (_crop.Width - w) / 2, _crop.Y + (_crop.Height - h) / 2, w, h);
            }
            Layout();
        }

        private void ApplyBoxes(bool widthChanged)
        {
            if (_updatingBoxes) return;
            if (!int.TryParse(_wBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ||
                !int.TryParse(_hBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) { Layout(); return; }
            w = Math.Clamp(w, 1, _w); h = Math.Clamp(h, 1, _h);
            if (_ratio is double k) { if (widthChanged) h = (int)Math.Round(w / k); else w = (int)Math.Round(h * k); }
            w = Math.Clamp(w, 1, _w); h = Math.Clamp(h, 1, _h);
            double cx = _crop.X + _crop.Width / 2, cy = _crop.Y + _crop.Height / 2;
            _crop = new Rect(Math.Clamp(cx - w / 2.0, 0, _w - w), Math.Clamp(cy - h / 2.0, 0, _h - h), w, h);
            Layout();
        }

        private void BeginDrag(int handle, MouseButtonEventArgs e)
        {
            _dragHandle = handle;
            _dragStart = e.GetPosition(_canvas);
            _dragOrig = _crop;
            _canvas.CaptureMouse();
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_dragHandle < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(_canvas);
            double dx = (p.X - _dragStart.X) / _s, dy = (p.Y - _dragStart.Y) / _s;
            var r = _dragOrig;
            if (_dragHandle == 8)
            {
                r.X = Math.Clamp(r.X + dx, 0, _w - r.Width);
                r.Y = Math.Clamp(r.Y + dy, 0, _h - r.Height);
                _crop = r;
                Layout();
                return;
            }

            double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
            // handles: 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L
            if (_dragHandle is 0 or 6 or 7) l = Math.Clamp(l + dx, 0, rt - 8);
            if (_dragHandle is 2 or 3 or 4) rt = Math.Clamp(rt + dx, l + 8, _w);
            if (_dragHandle is 0 or 1 or 2) t = Math.Clamp(t + dy, 0, b - 8);
            if (_dragHandle is 4 or 5 or 6) b = Math.Clamp(b + dy, t + 8, _h);
            var nr = new Rect(new Point(l, t), new Point(rt, b));

            if (_ratio is double k)
            {
                bool horizontalEdge = _dragHandle is 1 or 5;
                double w = nr.Width, h = nr.Height;
                if (horizontalEdge) w = h * k; else h = w / k;
                // Anchor at the side opposite to the handle.
                double ax = _dragHandle is 0 or 6 or 7 ? nr.Right - w : _dragHandle is 1 or 5 ? nr.X + (nr.Width - w) / 2 : nr.Left;
                double ay = _dragHandle is 0 or 1 or 2 ? nr.Bottom - h : _dragHandle is 3 or 7 ? nr.Y + (nr.Height - h) / 2 : nr.Top;
                if (ax < 0 || ay < 0 || ax + w > _w || ay + h > _h) return;   // would leave the image
                nr = new Rect(ax, ay, w, h);
            }
            _crop = nr;
            Layout();
        }

        private static Rect Normalize(Rect r) => new(Math.Max(0, r.X), Math.Max(0, r.Y), r.Width, r.Height);

        private void Layout()
        {
            var v = new Rect(_crop.X * _s, _crop.Y * _s, _crop.Width * _s, _crop.Height * _s);
            Canvas.SetLeft(_frame, v.X); Canvas.SetTop(_frame, v.Y);
            _frame.Width = Math.Max(1, v.Width); _frame.Height = Math.Max(1, v.Height);
            var outer = new RectangleGeometry(new Rect(0, 0, _canvas.Width, _canvas.Height));
            _mask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, new RectangleGeometry(v));

            var pts = new[]
            {
                new Point(v.Left, v.Top), new Point(v.Left + v.Width / 2, v.Top), new Point(v.Right, v.Top), new Point(v.Right, v.Top + v.Height / 2),
                new Point(v.Right, v.Bottom), new Point(v.Left + v.Width / 2, v.Bottom), new Point(v.Left, v.Bottom), new Point(v.Left, v.Top + v.Height / 2),
            };
            for (int i = 0; i < 8; i++) { Canvas.SetLeft(_handles[i], pts[i].X - 5); Canvas.SetTop(_handles[i], pts[i].Y - 5); }

            _updatingBoxes = true;
            _wBox.Text = ((int)Math.Round(_crop.Width)).ToString(CultureInfo.InvariantCulture);
            _hBox.Text = ((int)Math.Round(_crop.Height)).ToString(CultureInfo.InvariantCulture);
            _updatingBoxes = false;
        }
    }
}
