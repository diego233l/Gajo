using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Gajo.Core;

namespace Gajo.UI
{
    internal sealed class SettingsWindow : Window
    {
        private readonly Settings _settings;
        private readonly ComboBox _convertKey = KeyCombo();
        private readonly ComboBox _toolsKey = KeyCombo();
        private readonly ComboBox _actionsKey = KeyCombo();
        private readonly TextBox _hotkey = new() { Width = 160, IsReadOnly = true, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly CheckBox _autostart = new() { Content = "Iniciar Gajo con Windows (desmárcalo antes de borrar Gajo)" };
        private readonly CheckBox _notify = new() { Content = "Mostrar aviso al terminar cada conversión" };
        private readonly CheckBox _gpu = new() { Content = "Usar la tarjeta gráfica para el vídeo cuando acelere" };
        private readonly TextBlock _gpuStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 2, 0, 0) };
        private readonly Slider _quality = new() { Minimum = 60, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 220 };
        private readonly TextBlock _qualityValue = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _ffmpegStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        private string _hotkeyValue;

        public event Action? Saved;

        public SettingsWindow(Settings settings, string? ocrLanguage)
        {
            _settings = settings;
            _hotkeyValue = settings.Hotkey;

            Title = "Ajustes de Gajo";
            Width = 520;
            Height = 720;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFB, 0xF7));

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(new TextBlock { Text = "Gajo", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x48, 0x1C)) });
            root.Children.Add(new TextBlock
            {
                Text = "Arrastra un archivo y, sin soltarlo, pulsa una tecla: aparece la rueda. Suelta encima de una opción. " +
                       "El resultado se guarda junto al original, que no se toca.",
                TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x52, 0x44)), Margin = new Thickness(0, 4, 0, 10),
            });

            root.Children.Add(Header("Teclas de las ruedas"));
            root.Children.Add(KeyRow("Convertir", _convertKey, settings.TriggerKey));
            root.Children.Add(KeyRow("Herramientas", _toolsKey, settings.ToolsKey));
            root.Children.Add(KeyRow("Acciones", _actionsKey, settings.ActionsKey));
            root.Children.Add(Hint("Con la rueda abierta, pulsa otra de estas teclas para cambiar de rueda."));

            root.Children.Add(Header("Atajo de teclado"));
            _hotkey.Text = _hotkeyValue;
            _hotkey.PreviewKeyDown += CaptureHotkey;
            root.Children.Add(_hotkey);
            root.Children.Add(Hint("Selecciona archivos en el Explorador o el escritorio y pulsa el atajo: se abre la rueda. " +
                                   "Elige con las flechas, Intro para aplicar, Tab o las teclas de rueda para cambiar de rueda y Esc para cerrar. " +
                                   "Haz clic en la casilla y pulsa la combinación nueva."));

            root.Children.Add(Header("Comportamiento"));
            _autostart.IsChecked = settings.StartWithWindows;
            _notify.IsChecked = settings.ShowNotifications;
            _autostart.Margin = new Thickness(0, 0, 0, 6);
            root.Children.Add(_autostart);
            root.Children.Add(_notify);

            root.Children.Add(Header("Tarjeta gráfica"));
            _gpu.IsChecked = settings.UseGpu;
            root.Children.Add(_gpu);
            root.Children.Add(_gpuStatus);
            RefreshGpu();

            root.Children.Add(Header("Calidad de JPG"));
            _quality.Value = Math.Clamp(settings.JpegQuality, 60, 100);
            _qualityValue.Text = ((int)_quality.Value).ToString();
            _quality.ValueChanged += (_, _) => _qualityValue.Text = ((int)_quality.Value).ToString();
            var q = new StackPanel { Orientation = Orientation.Horizontal };
            q.Children.Add(_quality);
            q.Children.Add(_qualityValue);
            root.Children.Add(q);

            root.Children.Add(Header("Motor de conversión"));
            root.Children.Add(_ffmpegStatus);
            var ffButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var pick = new Button { Content = "Usar otro ffmpeg.exe…", Padding = new Thickness(12, 4, 12, 4) };
            var reset = new Button { Content = "Volver al integrado", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            reset.Click += (_, _) => { _settings.FfmpegPath = null; _settings.Save(); RefreshFfmpeg(); };
            pick.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|Programas (*.exe)|*.exe", Title = "Busca ffmpeg.exe" };
                if (dlg.ShowDialog(this) == true) { _settings.FfmpegPath = dlg.FileName; _settings.Save(); RefreshFfmpeg(); }
            };
            ffButtons.Children.Add(pick);
            ffButtons.Children.Add(reset);
            root.Children.Add(ffButtons);
            RefreshFfmpeg();

            root.Children.Add(Header("Reconocimiento de texto (OCR) para PDF escaneados"));
            root.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = ocrLanguage != null
                    ? $"Usa el OCR integrado en Windows, en {ocrLanguage}. Para otros idiomas, añádelos en Configuración → Hora e idioma → Idioma."
                    : "No disponible: añade el reconocimiento de texto en Configuración → Hora e idioma → Idioma → Español → Opciones.",
            });

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 10, 24, 16) };
            var cancel = new Button { Content = "Cancelar", Padding = new Thickness(16, 5, 16, 5), IsCancel = true };
            var save = new Button { Content = "Guardar", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
            cancel.Click += (_, _) => Close();
            save.Click += (_, _) => { if (Apply()) Close(); };
            footer.Children.Add(cancel);
            footer.Children.Add(save);

            var dock = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);
            dock.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = dock;
        }

        private static ComboBox KeyCombo()
        {
            var c = new ComboBox { Width = 150 };
            c.Items.Add(new ComboBoxItem { Content = "Mayús (Shift)", Tag = TriggerKey.Shift });
            c.Items.Add(new ComboBoxItem { Content = "Ctrl", Tag = TriggerKey.Control });
            c.Items.Add(new ComboBoxItem { Content = "Alt", Tag = TriggerKey.Alt });
            return c;
        }

        private static StackPanel KeyRow(string name, ComboBox combo, TriggerKey value)
        {
            combo.SelectedIndex = (int)value;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = name, Width = 120, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(combo);
            return row;
        }

        private static TextBlock Header(string text) => new()
        {
            Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x1A, 0x10)),
        };

        private static TextBlock Hint(string text) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x58)),
        };

        private void CaptureHotkey(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if (key is Key.Back or Key.Delete) { _hotkeyValue = ""; _hotkey.Text = "(desactivado)"; return; }
            var parts = new System.Collections.Generic.List<string>();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            string? k = key switch
            {
                >= Key.A and <= Key.Z => key.ToString(),
                >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
                >= Key.F1 and <= Key.F12 => key.ToString(),
                Key.Space => "Space",
                _ => null,
            };
            if (k == null || parts.Count == 0) { _hotkey.Text = "Usa Ctrl/Alt + letra"; return; }
            parts.Add(k);
            _hotkeyValue = string.Join("+", parts);
            _hotkey.Text = _hotkeyValue;
        }

        private void RefreshGpu()
        {
            var gpus = Hardware.Gpus;
            string cards = gpus.Count == 0 ? "No se detectó ninguna tarjeta gráfica." : "Detectada: " + string.Join(", ", gpus) + ".";
            string state = !Hardware.Probed ? " Comprobando…"
                : Hardware.Available != HwEncoder.None ? $" Codifica vídeo con {Hardware.AvailableName}; si un vídeo concreto falla, se usa el procesador."
                : " No ofrece codificación de vídeo compatible: se usa el procesador.";
            _gpuStatus.Text = cards + state;
            _gpuStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x58));
            _gpuStatus.FontSize = 11.5;
        }

        private void RefreshFfmpeg()
        {
            if (!string.IsNullOrWhiteSpace(_settings.FfmpegPath) && System.IO.File.Exists(_settings.FfmpegPath))
                _ffmpegStatus.Text = "Usando uno externo: " + _settings.FfmpegPath;
            else if (Ffmpeg.HasEmbedded)
                _ffmpegStatus.Text = "Integrado en Gajo.exe (FFmpeg, licencia GPL). No necesitas instalar nada.";
            else
                _ffmpegStatus.Text = "Este Gajo.exe no lleva el motor integrado. Elige un ffmpeg.exe o compílalo con compilar.bat.";
        }

        private static TriggerKey KeyOf(ComboBox c) => c.SelectedItem is ComboBoxItem { Tag: TriggerKey k } ? k : TriggerKey.Shift;

        private bool Apply()
        {
            var keys = new[] { KeyOf(_convertKey), KeyOf(_toolsKey), KeyOf(_actionsKey) };
            if (keys.Distinct().Count() != 3)
            {
                MessageBox.Show(this, "Cada rueda necesita una tecla distinta.", "Gajo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            _settings.TriggerKey = keys[0];
            _settings.ToolsKey = keys[1];
            _settings.ActionsKey = keys[2];
            _settings.Hotkey = _hotkeyValue;
            _settings.StartWithWindows = _autostart.IsChecked == true;
            _settings.ShowNotifications = _notify.IsChecked == true;
            _settings.UseGpu = _gpu.IsChecked == true;
            _settings.JpegQuality = (int)_quality.Value;
            _settings.Save();
            Saved?.Invoke();
            return true;
        }
    }
}
