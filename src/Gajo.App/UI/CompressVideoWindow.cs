using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Gajo.Core;

namespace Gajo.UI
{
    /// <summary>"Comprimir a tamaño": pick a target size; shows the resolution and quality it implies.</summary>
    internal sealed class CompressVideoWindow : ToolDialog
    {
        private readonly MediaTools.MediaInfo _info;
        private readonly TextBox _custom = new() { Width = 70, Margin = new Thickness(6, 0, 4, 0) };
        private readonly TextBlock _estimate = new() { TextWrapping = TextWrapping.Wrap, Width = 420, Margin = new Thickness(0, 12, 0, 0) };
        private double _mb;

        public double TargetMB => _mb;

        private static readonly (string Label, double MB)[] Presets =
        {
            ("8 MB · Discord", 8), ("16 MB · WhatsApp", 16), ("25 MB · Correo", 25), ("50 MB", 50), ("100 MB", 100),
        };

        public CompressVideoWindow(string fileName, MediaTools.MediaInfo info) : base("Comprimir vídeo")
        {
            _info = info;
            double current = info.Bytes / 1024.0 / 1024.0;
            int defaultIndex = Array.FindIndex(Presets, p => p.MB < current);
            if (defaultIndex < 0) defaultIndex = 0;
            _mb = Presets[defaultIndex].MB;

            var stack = new StackPanel { Width = 440 };
            stack.Children.Add(new TextBlock
            {
                Text = $"{fileName}\n{current:0.#} MB · {TimeSpan.FromSeconds(info.Duration):h\\:mm\\:ss} · {info.Width}×{info.Height}",
                Foreground = Muted, Margin = new Thickness(0, 0, 0, 12),
            });
            stack.Children.Add(new TextBlock { Text = "Tamaño máximo", Foreground = Ink, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            var chips = Chips(Array.ConvertAll(Presets, p => p.Label), defaultIndex, i => { _mb = Presets[i].MB; _custom.Text = ""; Refresh(); });
            var wrap = new WrapPanel();
            wrap.Children.Add(chips);
            stack.Children.Add(wrap);
            var customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            customRow.Children.Add(new TextBlock { Text = "Otro:", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            customRow.Children.Add(_custom);
            customRow.Children.Add(new TextBlock { Text = "MB", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            _custom.TextChanged += (_, _) =>
            {
                if (double.TryParse(_custom.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0.2) { _mb = v; Refresh(); }
            };
            stack.Children.Add(customRow);
            _estimate.Foreground = Ink;
            stack.Children.Add(_estimate);
            Body.Children.Add(stack);

            AddButton(Footer, "Comprimir", true, () =>
            {
                if (_mb * 1024 * 1024 >= _info.Bytes) { _estimate.Text = "El vídeo ya ocupa menos que eso."; return; }
                DialogResult = true;
            });
            Refresh();
        }

        private void Refresh()
        {
            var plan = MediaTools.PlanForSize(_info, _mb);
            string gpu = Hardware.Active != HwEncoder.None ? $" Se usará la tarjeta gráfica ({Hardware.AvailableName})." : "";
            _estimate.Text = plan.TooSmall
                ? $"Para {_mb:0.#} MB el vídeo quedaría con muy poca calidad ({plan.VideoKbps} kbps). Prueba un tamaño mayor o recórtalo antes."
                : $"Resultado: MP4 de hasta {_mb:0.#} MB, {plan.MaxHeight}p, vídeo a {plan.VideoKbps} kbps" + (plan.AudioKbps > 0 ? $" y audio a {plan.AudioKbps} kbps." : ", sin audio.") + gpu;
        }
    }
}
