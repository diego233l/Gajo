using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Gajo.UI
{
    /// <summary>Base for the small tool windows (crop, background, trim…): rounded card, title, close button, footer.</summary>
    internal class ToolDialog : Window
    {
        protected static readonly Color Accent = Color.FromRgb(0xF0, 0x55, 0x24);
        protected static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Accent));
        protected static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x1A, 0x10)));
        protected static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x58)));
        protected static readonly Brush Soft = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xE6, 0xDC)));
        protected static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");

        protected readonly Grid Body = new();
        protected readonly StackPanel Footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        protected readonly StackPanel FooterLeft = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };

        public ToolDialog(string title)
        {
            Title = title;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            FontFamily = UiFont;
            FontSize = 12.5;

            var titleText = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var close = new Button
            {
                Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
                Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand,
                Background = Soft, Foreground = Ink, BorderThickness = new Thickness(0), Template = Rounded(13),
            };
            close.Click += (_, _) => { DialogResult = false; };
            var titleBar = new Grid { Height = 34, Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
            titleBar.Children.Add(titleText);
            titleBar.Children.Add(close);
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

            var footerGrid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            footerGrid.Children.Add(FooterLeft);
            footerGrid.Children.Add(Footer);

            var layout = new DockPanel();
            DockPanel.SetDock(titleBar, Dock.Top);
            DockPanel.SetDock(footerGrid, Dock.Bottom);
            layout.Children.Add(titleBar);
            layout.Children.Add(footerGrid);
            layout.Children.Add(Body);

            Content = new Border
            {
                Margin = new Thickness(18),
                Padding = new Thickness(16, 10, 16, 16),
                CornerRadius = new CornerRadius(16),
                Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0xF8, 0xF2), Color.FromRgb(0xFF, 0xEF, 0xE2), 90),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x80, 0x40)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.3, Color = Color.FromRgb(0x50, 0x20, 0x00) },
                Child = layout,
            };

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            };
        }

        protected Button AddButton(StackPanel panel, string text, bool primary, Action onClick)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = primary ? Brushes.White : Ink, Background = primary ? AccentBrush : Soft,
                BorderThickness = new Thickness(0), Template = Rounded(8),
            };
            b.Click += (_, _) => onClick();
            panel.Children.Add(b);
            return b;
        }

        protected static TextBlock Label(string text) => new()
        {
            Text = text, Foreground = Muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Width = 70,
        };

        /// <summary>Row of pill toggles (e.g. aspect ratios). Returns the panel; onPick gets the index.</summary>
        protected StackPanel Chips(string[] options, int selected, Action<int> onPick)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var buttons = new ToggleButton[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var t = new ToggleButton
                {
                    Content = options[i], Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand, BorderThickness = new Thickness(0), FontSize = 11.5, Template = RoundedToggle(),
                    IsChecked = i == selected,
                };
                t.Click += (_, _) =>
                {
                    foreach (var b in buttons) b.IsChecked = b == t;
                    onPick(idx);
                };
                buttons[i] = t;
                panel.Children.Add(t);
            }
            return panel;
        }

        protected static ControlTemplate Rounded(double radius)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            var t = new ControlTemplate(typeof(ButtonBase)) { VisualTree = border };
            t.Seal();
            return t;
        }

        private static ControlTemplate? _toggle;
        protected static ControlTemplate RoundedToggle()
        {
            if (_toggle != null) return _toggle;
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.BackgroundProperty, Soft);
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(cp);
            var t = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
            var trig = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            trig.Setters.Add(new Setter(Border.BackgroundProperty, AccentBrush, "bd"));
            trig.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            t.Triggers.Add(trig);
            t.Seal();
            return _toggle = t;
        }

        protected static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
    }
}
