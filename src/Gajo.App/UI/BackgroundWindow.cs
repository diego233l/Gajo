using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Gajo.UI
{
    /// <summary>"Añadir fondo": puts the image on a gradient or colour, with margin, rounded corners and shadow.</summary>
    internal sealed class BackgroundWindow : ToolDialog
    {
        private readonly BitmapSource _image;
        private readonly Viewbox _preview = new() { Width = 460, Height = 340, Stretch = Stretch.Uniform };
        private readonly Slider _padding = new() { Minimum = 0, Maximum = 50, Value = 12, Width = 220 };
        private readonly Slider _corners = new() { Minimum = 0, Maximum = 25, Value = 4, Width = 220 };
        private readonly Slider _shadow = new() { Minimum = 0, Maximum = 100, Value = 40, Width = 220 };
        private readonly TextBlock _padText = new() { Width = 60, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _cornerText = new() { Width = 60, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _shadowText = new() { Width = 60, VerticalAlignment = VerticalAlignment.Center };
        private Brush _background;
        private double? _ratio;   // null = auto

        public byte[]? ResultPng { get; private set; }

        private static readonly Brush[] Presets =
        {
            Grad("#FF8A3D", "#E8361C", "#B4127A"),
            Grad("#FDE68A", "#F59E0B", "#EF4444"),
            Grad("#A7F3D0", "#34D399", "#0EA5E9"),
            Grad("#C4B5FD", "#8B5CF6", "#EC4899"),
            Grad("#BFDBFE", "#60A5FA", "#1D4ED8"),
            Grad("#FBCFE8", "#F9A8D4", "#FDE68A"),
            Grad("#1F2937", "#111827", "#4B5563"),
            Grad("#FFEDD5", "#FED7AA", "#FDBA74"),
        };

        private static readonly string[] Solids = { "#FFFFFF", "#F5F5F4", "#FACC15", "#22C55E", "#14B8A6", "#3B82F6", "#6366F1", "#111827", "#F05524" };

        public BackgroundWindow(BitmapSource image) : base("Añadir fondo")
        {
            _image = image;
            _background = Presets[0];

            var previewHost = new Border { Padding = new Thickness(10), Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xE2, 0xD8)), CornerRadius = new CornerRadius(10), Child = _preview };

            var swatches = new WrapPanel { Width = 330 };
            foreach (var b in Presets) swatches.Children.Add(Swatch(b));
            foreach (var s in Solids) swatches.Children.Add(Swatch(Solid(s)));
            var bgRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 6) };
            bgRow.Children.Add(Label("Fondo"));
            bgRow.Children.Add(swatches);

            var stack = new StackPanel();
            stack.Children.Add(previewHost);
            stack.Children.Add(bgRow);
            stack.Children.Add(SliderRow("Margen", _padding, _padText));
            stack.Children.Add(SliderRow("Esquinas", _corners, _cornerText));
            stack.Children.Add(SliderRow("Sombra", _shadow, _shadowText));
            var ratioRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            ratioRow.Children.Add(Label("Proporción"));
            var ratios = new (string, double?)[] { ("Auto", null), ("1:1", 1), ("16:9", 16.0 / 9), ("4:5", 4.0 / 5), ("9:16", 9.0 / 16) };
            ratioRow.Children.Add(Chips(Array.ConvertAll(ratios, r => r.Item1), 0, i => { _ratio = ratios[i].Item2; Refresh(); }));
            stack.Children.Add(ratioRow);
            Body.Children.Add(stack);

            foreach (var s in new[] { _padding, _corners, _shadow }) s.ValueChanged += (_, _) => Refresh();

            AddButton(FooterLeft, "Restablecer", false, () => { _padding.Value = 12; _corners.Value = 4; _shadow.Value = 40; _background = Presets[0]; Refresh(); });
            AddButton(Footer, "Guardar con fondo", true, () =>
            {
                Cursor = Cursors.Wait;
                try { ResultPng = RenderPng(); DialogResult = true; }
                finally { Cursor = null; }
            });
            Refresh();
        }

        private FrameworkElement Swatch(Brush b)
        {
            var e = new Border
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(7),
                Background = b, BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            };
            e.MouseLeftButtonUp += (_, _) => { _background = b; Refresh(); };
            return e;
        }

        private StackPanel SliderRow(string label, Slider slider, TextBlock value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(Label(label));
            row.Children.Add(slider);
            value.Foreground = Muted;
            value.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(value);
            return row;
        }

        private void Refresh()
        {
            var (w, h, pad, radius, blur) = Metrics();
            _padText.Text = $"{(int)pad} px";
            _cornerText.Text = $"{(int)radius} px";
            _shadowText.Text = $"{(int)_shadow.Value} %";
            _preview.Child = Compose(w, h, pad, radius, blur);
        }

        private (int W, int H, double Pad, double Radius, double Blur) Metrics()
        {
            int iw = _image.PixelWidth, ih = _image.PixelHeight;
            double pad = Math.Round(Math.Max(iw, ih) * _padding.Value / 100.0);
            double radius = Math.Round(Math.Min(iw, ih) * _corners.Value / 100.0);
            double blur = Math.Max(iw, ih) * 0.04 * _shadow.Value / 100.0;
            double w = iw + pad * 2, h = ih + pad * 2;
            if (_ratio is double k)
            {
                if (w / h < k) w = h * k; else h = w / k;
            }
            // Keep the result within a sensible size.
            double max = Math.Max(w, h);
            if (max > 8000) { double f = 8000 / max; w *= f; h *= f; pad *= f; radius *= f; blur *= f; }
            return ((int)Math.Round(w), (int)Math.Round(h), pad, radius, blur);
        }

        private FrameworkElement Compose(int w, int h, double pad, double radius, double blur)
        {
            double scale = Math.Min(1, Math.Min((w - pad * 2) / _image.PixelWidth, (h - pad * 2) / _image.PixelHeight));
            double iw = _image.PixelWidth * scale, ih = _image.PixelHeight * scale;
            var picture = new Border
            {
                Width = iw, Height = ih, CornerRadius = new CornerRadius(radius),
                Background = new ImageBrush(_image) { Stretch = Stretch.Fill },
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            if (_shadow.Value > 0)
                picture.Effect = new DropShadowEffect { BlurRadius = blur * 2, ShadowDepth = blur * 0.5, Direction = 270, Opacity = 0.25 + _shadow.Value / 300.0, Color = Colors.Black };
            return new Grid { Width = w, Height = h, Background = _background, Children = { picture } };
        }

        private byte[] RenderPng()
        {
            var (w, h, pad, radius, blur) = Metrics();
            var element = Compose(w, h, pad, radius, blur);
            element.Measure(new Size(w, h));
            element.Arrange(new Rect(0, 0, w, h));
            element.UpdateLayout();
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(element);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        private static Brush Grad(string a, string b, string c)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(a), 0));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(b), 0.55));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), 1));
            g.Freeze();
            return g;
        }

        private static Brush Solid(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    }
}
