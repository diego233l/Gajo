using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Gajo.Interop;

namespace Gajo.UI
{
    /// <summary>
    /// Floating shelf ("Estante"): a small panel pinned to a screen edge where files wait so they can be
    /// dragged into another app later. Files are only referenced, never copied or moved by the shelf.
    /// </summary>
    internal sealed class ShelfWindow : Window
    {
        private readonly List<string> _items = new();
        private readonly StackPanel _list = new();
        private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _card;
        private bool _right = true;
        private Point _dragStart;
        private string? _dragCandidate;
        private Theme _theme = Theme.Current;

        private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        public ShelfWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Width = 250;
            SizeToContent = SizeToContent.Height;
            AllowDrop = true;
            Title = "Estante de Gajo";
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
            var pin = HeaderButton("", "Arrastra desde aquí para llevar todos los archivos");
            pin.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragCandidate = "*"; };
            pin.PreviewMouseMove += OnItemMouseMove;
            var side = HeaderButton("", "Cambiar de lado");
            side.Click += (_, _) => { _right = !_right; Dock(); };
            var clear = HeaderButton("", "Vaciar el estante");
            clear.Click += (_, _) => { _items.Clear(); Rebuild(); Hide(); };
            var close = HeaderButton("", "Ocultar");
            close.Click += (_, _) => Hide();
            DockPanel.SetDock(pin, System.Windows.Controls.Dock.Left);
            foreach (var b in new[] { close, clear, side }) { DockPanel.SetDock(b, System.Windows.Controls.Dock.Right); header.Children.Add(b); }
            header.Children.Add(pin);
            header.Children.Add(_title);

            var scroll = new ScrollViewer { Content = _list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(scroll);
            stack.Children.Add(new TextBlock
            {
                Text = "Suelta aquí archivos para guardarlos un momento y arrástralos después a otra app.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0),
            });

            _card = new Border
            {
                Margin = new Thickness(14),
                Padding = new Thickness(12, 10, 12, 12),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.3, Color = Colors.Black },
                Child = stack,
            };
            Content = _card;
            ApplyTheme();

            DragEnter += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link | DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? (e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.Copy) : DragDropEffects.None; e.Handled = true; };
            Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Add(files);
                e.Effects = e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.Copy;
                e.Handled = true;
            };
            SizeChanged += (_, _) => Dock();
        }

        private Button HeaderButton(string glyph, string tip)
        {
            return new Button
            {
                Content = glyph, FontFamily = IconFont, FontSize = 12, Width = 26, Height = 26, Margin = new Thickness(2, 0, 0, 0),
                ToolTip = tip, Cursor = Cursors.Hand, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            };
        }

        private void ApplyTheme()
        {
            _theme = Theme.Current;
            _card.Background = _theme.CardFill;
            _card.BorderBrush = _theme.CardBorder;
            Foreground = _theme.Text;
            _title.Foreground = _theme.Text;
        }

        public int Count => _items.Count;

        public void Add(IEnumerable<string> paths)
        {
            foreach (var p in paths)
                if (!_items.Contains(p, StringComparer.OrdinalIgnoreCase) && (File.Exists(p) || Directory.Exists(p)))
                    _items.Add(p);
            ApplyTheme();
            Rebuild();
            if (!IsVisible) Show();
            Dock();
        }

        public void ShowShelf()
        {
            ApplyTheme();
            Rebuild();
            Show();
            Dock();
        }

        private void Rebuild()
        {
            _items.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
            _list.Children.Clear();
            _title.Text = _items.Count == 0 ? "Estante vacío" : _items.Count == 1 ? "Estante · 1 archivo" : $"Estante · {_items.Count} archivos";
            foreach (var path in _items)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path };
                var icon = new Image { Source = ShellIcons.Get(path), Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
                row.Children.Add(icon);
                row.Children.Add(new TextBlock { Text = Path.GetFileName(path.TrimEnd('\\')), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = _theme.Text });
                string p = path;
                row.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragCandidate = p; };
                row.PreviewMouseMove += OnItemMouseMove;
                var menu = new ContextMenu();
                var remove = new MenuItem { Header = "Quitar del estante" };
                remove.Click += (_, _) => { _items.Remove(p); Rebuild(); if (_items.Count == 0) Hide(); };
                var reveal = new MenuItem { Header = "Mostrar en carpeta" };
                reveal.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true }); } catch { } };
                menu.Items.Add(reveal);
                menu.Items.Add(remove);
                row.ContextMenu = menu;
                _list.Children.Add(row);
            }
        }

        private void OnItemMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var files = _dragCandidate == "*" ? _items.ToArray() : new[] { _dragCandidate };
            _dragCandidate = null;
            if (files.Length == 0) return;
            var data = new DataObject(DataFormats.FileDrop, files);
            var result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (result == DragDropEffects.Move) Rebuild();   // moved elsewhere: drop the ones that no longer exist
        }

        private void Dock()
        {
            var wa = SystemParameters.WorkArea;
            Left = _right ? wa.Right - ActualWidth : wa.Left;
            Top = wa.Top + Math.Max(0, (wa.Height - ActualHeight) / 2);
        }
    }
}
