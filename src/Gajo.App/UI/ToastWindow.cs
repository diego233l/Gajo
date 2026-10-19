using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Gajo.UI
{
    /// <summary>Small card in the bottom-right corner: progress, result, errors and questions.</summary>
    internal sealed class ToastWindow : Window
    {
        private static readonly Color Accent = Color.FromRgb(0xF0, 0x55, 0x24);
        private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x1A, 0x10));
        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x5C, 0x4A));

        private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _body = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        private readonly ProgressBar _bar = new() { Height = 5, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(0) };
        private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        private readonly Border _dot = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        private readonly DispatcherTimer _autoHide = new();
        private bool _mouseInside;
        private Border _card = null!;
        private Theme _theme = Theme.Current;

        public ToastWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Width = 380;
            SizeToContent = SizeToContent.Height;
            Title = "Gajo";
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            _title.Foreground = TextBrush;
            _body.Foreground = MutedBrush;
            _bar.Foreground = new SolidColorBrush(Accent);
            _bar.Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xE3, 0xD6));

            var text = new StackPanel();
            text.Children.Add(_title);
            text.Children.Add(_body);
            text.Children.Add(_bar);
            text.Children.Add(_buttons);

            var row = new DockPanel();
            DockPanel.SetDock(_dot, Dock.Left);
            row.Children.Add(_dot);
            row.Children.Add(text);

            var card = _card = new Border
            {
                Margin = new Thickness(16),
                Padding = new Thickness(16, 14, 16, 14),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(0xF7, 0xFF, 0xFB, 0xF7)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xC0, 0x60, 0x20)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.25, Color = Color.FromRgb(0x50, 0x20, 0x00) },
                Child = row,
            };
            Content = card;

            MouseEnter += (_, _) => { _mouseInside = true; };
            MouseLeave += (_, _) => { _mouseInside = false; };
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is not DependencyObject d || FindButton(d) == null) HideToast();
            };
            _autoHide.Tick += (_, _) =>
            {
                if (_mouseInside) return;
                _autoHide.Stop();
                HideToast();
            };
            SizeChanged += (_, _) => Reposition();
        }

        private static Button? FindButton(DependencyObject? d)
        {
            try
            {
                while (d != null)
                {
                    if (d is Button b) return b;
                    d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ public API

        public void ShowProgress(string title, string body, double? progress, Action? onCancel = null)
        {
            Prepare(title, body, Accent);
            _bar.Visibility = Visibility.Visible;
            _bar.IsIndeterminate = progress == null;
            if (progress != null) _bar.Value = progress.Value;
            if (onCancel != null) AddButton("Cancelar", onCancel, primary: false);
            Present(autoHideSeconds: 0);
        }

        /// <summary>Update the bar without rebuilding the card.</summary>
        public void UpdateProgress(double progress)
        {
            if (_bar.IsIndeterminate) _bar.IsIndeterminate = false;
            _bar.Value = progress;
        }

        public void ShowDone(string title, string body, string? revealPath)
        {
            Prepare(title, body, Color.FromRgb(0x2E, 0xB8, 0x5C));
            if (revealPath != null)
                AddButton("Mostrar en carpeta", () => Reveal(revealPath), primary: true);
            Present(autoHideSeconds: 5);
        }

        public void ShowError(string title, string body)
        {
            Prepare(title, body, Color.FromRgb(0xD9, 0x30, 0x25));
            Present(autoHideSeconds: 9);
        }

        public void ShowInfo(string title, string body, int seconds = 7)
        {
            Prepare(title, body, Accent);
            Present(autoHideSeconds: seconds);
        }

        public void ShowQuestion(string title, string body, string yes, Action onYes, string no, Action onNo)
        {
            Prepare(title, body, Accent);
            AddButton(no, () => { HideToast(); onNo(); }, primary: false);
            AddButton(yes, onYes, primary: true);
            Present(autoHideSeconds: 0);
        }

        public void HideToast()
        {
            _autoHide.Stop();
            if (!IsVisible) return;
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) => { if (Opacity < 0.05) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        }

        // ------------------------------------------------------------------ internals

        private void Prepare(string title, string body, Color dot)
        {
            // Follow Windows light/dark mode each time a card is shown.
            _theme = Theme.Current;
            _card.Background = _theme.CardFill;
            _card.BorderBrush = _theme.CardBorder;
            _title.Foreground = _theme.Text;
            _body.Foreground = _theme.MutedText;
            _bar.Background = _theme.Track;
            _title.Text = title;
            _body.Text = body;
            _body.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
            _dot.Background = new SolidColorBrush(dot);
            _bar.Visibility = Visibility.Collapsed;
            _bar.IsIndeterminate = false;
            _bar.Value = 0;
            _buttons.Children.Clear();
            _buttons.Visibility = Visibility.Collapsed;
        }

        private void AddButton(string text, Action onClick, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 12.5,
                Cursor = Cursors.Hand,
                Foreground = primary ? Brushes.White : _theme.Text,
                Background = primary ? new SolidColorBrush(Accent) : _theme.SecondaryButton,
                BorderThickness = new Thickness(0),
                Template = RoundedButtonTemplate(),
            };
            b.Click += (_, _) => onClick();
            _buttons.Children.Add(b);
            _buttons.Visibility = Visibility.Visible;
        }

        private static ControlTemplate? _template;
        private static ControlTemplate RoundedButtonTemplate()
        {
            if (_template != null) return _template;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            _template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            _template.Seal();
            return _template;
        }

        private void Present(int autoHideSeconds)
        {
            _autoHide.Stop();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (!IsVisible) Show();
            UpdateLayout();
            Reposition();
            if (autoHideSeconds > 0)
            {
                _autoHide.Interval = TimeSpan.FromSeconds(autoHideSeconds);
                _autoHide.Start();
            }
        }

        private void Reposition()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth;
            Top = wa.Bottom - ActualHeight;
        }

        private static void Reveal(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch { }
        }
    }
}
