using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Gajo.Core;
using Gajo.UI;
using WinForms = System.Windows.Forms;

namespace Gajo
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            using var mutex = new Mutex(true, "Gajo.SingleInstance.7f3c2a1e", out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show("Gajo ya se está ejecutando. Lo encontrarás en la bandeja del sistema, junto al reloj.",
                    "Gajo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                ConversionService.Log(e.Exception.ToString());
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ConversionService.Log(e.ExceptionObject?.ToString());

            var controller = new AppController(app, startedInBackground: args.Contains("--background"));
            app.Startup += (_, _) => controller.Start();
            app.Exit += (_, _) => controller.Dispose();
            app.Run();
        }
    }

    internal sealed partial class AppController : IDisposable
    {
        // Features switched on as each version lands.
        private const bool ActionsEnabled = true;
        private const bool HotkeyEnabled = true;

        private readonly Application _app;
        private readonly bool _background;
        private readonly Settings _settings = Settings.Load();
        private DragWatcher? _watcher;
        private WheelWindow? _wheel;
        private ToastWindow? _toast;
        private ConversionService? _service;
        private ToolRunner? _tools;
        private WinForms.NotifyIcon? _tray;
        private SettingsWindow? _settingsWindow;
        private WindowsOcr? _ocr;

        public AppController(Application app, bool startedInBackground)
        {
            _app = app;
            _background = startedInBackground;
        }

        public void Start()
        {
            _ocr = new WindowsOcr();
            PdfText.Ocr = _ocr;
            PdfToDocx.Renderer = new WindowsPdfRenderer();
            Hardware.Enabled = _settings.UseGpu;
            WheelCatalog.ToolFilter = ToolRunner.IsAvailable;

            _toast = new ToastWindow();
            _service = new ConversionService(_settings, _toast);
            _tools = new ToolRunner(_service);
            _wheel = new WheelWindow();
            _wheel.ItemChosen += OnItemChosen;

            var dispatcher = _app.Dispatcher;
            _watcher = new DragWatcher();
            ApplyKeys();
            // Hook callbacks must return fast: defer the real work to the dispatcher queue.
            _watcher.TriggerPressed += (mode, x, y) =>
            {
                if (mode == WheelMode.Actions && !ActionsEnabled) return;
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => _wheel.Arm(mode, x, y)));
            };
            _watcher.DragEnded += () => dispatcher.BeginInvoke(new Action(() => _wheel.OnMouseReleased()));
            _watcher.Cancelled += () => dispatcher.BeginInvoke(new Action(() => { if (!_wheel.IsKeyboardMode) _wheel.Dismiss(true); }));
            _watcher.HotkeyPressed += () => dispatcher.BeginInvoke(new Action(OpenKeyboardWheel));
            _watcher.NavKey += key => dispatcher.BeginInvoke(new Action(() => OnNavKey(key)));
            try
            {
                _watcher.Start();
            }
            catch (Exception ex)
            {
                ConversionService.Log(ex.ToString());
                MessageBox.Show("Gajo no pudo vigilar el ratón y el teclado:\n" + ex.Message, "Gajo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            _wheel.IsVisibleChanged += (_, _) => { if (_watcher != null) _watcher.CaptureNavigation = _wheel.IsVisible && _wheel.IsKeyboardMode; };

            Autostart.Apply(_settings.StartWithWindows);
            CreateTray();

            // Unpack the built-in engine, then check which graphics card can encode video.
            var custom = _settings.FfmpegPath;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var ffmpeg = Ffmpeg.Locate(custom);
                    if (ffmpeg != null) await Hardware.ProbeAsync(ffmpeg, GpuInfo.Detect());
                }
                catch (Exception ex) { ConversionService.Log(ex.ToString()); }
            });

            if (!_settings.FirstRunDone)
            {
                _settings.FirstRunDone = true;
                _settings.Save();
                _toast.ShowInfo("Gajo está listo",
                    $"Arrastra un archivo y, sin soltarlo, pulsa {KeyName(_settings.TriggerKey)} para convertir o {KeyName(_settings.ToolsKey)} para las herramientas.", 12);
            }
            else if (!_background)
            {
                _toast.ShowInfo("Gajo ya está funcionando", "Está en los iconos ocultos de la barra de tareas, junto al reloj.", 4);
            }
        }

        private void ApplyKeys()
        {
            if (_watcher == null) return;
            _watcher.ConvertKey = _settings.TriggerKey;
            _watcher.ToolsKey = _settings.ToolsKey;
            _watcher.ActionsKey = _settings.ActionsKey;
            _watcher.Hotkey = HotkeyEnabled ? DragWatcher.ParseHotkey(_settings.Hotkey) : (0, 0);
        }

        private void OnItemChosen(WheelMode mode, WheelItem item, System.Collections.Generic.IReadOnlyList<string> files)
        {
            switch (mode)
            {
                case WheelMode.Convert when item.Target != null:
                    _service!.Enqueue(files, item.Target);
                    break;
                case WheelMode.Tools:
                    _tools!.Run(item, files);
                    break;
                case WheelMode.Actions:
                    RunAction(item, files);
                    break;
            }
        }

        partial void RunActionCore(WheelItem item, System.Collections.Generic.IReadOnlyList<string> files);
        private void RunAction(WheelItem item, System.Collections.Generic.IReadOnlyList<string> files) => RunActionCore(item, files);

        partial void OpenKeyboardWheelCore();
        private void OpenKeyboardWheel() => OpenKeyboardWheelCore();

        private void OnNavKey(string key)
        {
            if (_wheel == null || !_wheel.IsOpen || !_wheel.IsKeyboardMode) return;
            switch (key)
            {
                case "left": _wheel.KeyNav(-1); break;
                case "right": _wheel.KeyNav(1); break;
                case "enter": _wheel.KeyActivate(); break;
                case "esc": _wheel.Dismiss(false); break;
                case "tab": _wheel.CycleMode(); if (!ActionsEnabled && _wheel.Mode == WheelMode.Actions) _wheel.CycleMode(); break;
                default:
                    if (key.StartsWith("mode:") && Enum.TryParse<WheelMode>(key[5..], out var m) && (ActionsEnabled || m != WheelMode.Actions))
                        _wheel.SwitchMode(m);
                    break;
            }
        }

        private static string KeyName(TriggerKey k) => k switch
        {
            TriggerKey.Control => "Ctrl",
            TriggerKey.Alt => "Alt",
            _ => "Mayús",
        };

        private void CreateTray()
        {
            System.Drawing.Icon icon;
            try
            {
                var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/gajo.ico"));
                icon = res != null ? new System.Drawing.Icon(res.Stream, 32, 32) : System.Drawing.SystemIcons.Application;
            }
            catch { icon = System.Drawing.SystemIcons.Application; }

            var menu = new WinForms.ContextMenuStrip();
            var header = new WinForms.ToolStripMenuItem("Gajo") { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            AddTrayItems(menu);
            menu.Items.Add("Ajustes…", null, (_, _) => OpenSettings());
            menu.Items.Add("Abrir registro de errores", null, (_, _) =>
            {
                try
                {
                    if (System.IO.File.Exists(ConversionService.LogPath))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ConversionService.LogPath) { UseShellExecute = true });
                    else
                        _toast?.ShowInfo("Sin errores", "Todavía no se ha registrado ningún error.", 3);
                }
                catch { }
            });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Salir", null, (_, _) => _app.Shutdown());
            menu.Opening += (_, _) => header.Text = $"Gajo — {KeyName(_settings.TriggerKey)} convertir · {KeyName(_settings.ToolsKey)} herramientas";

            _tray = new WinForms.NotifyIcon { Icon = icon, Text = "Gajo", Visible = true, ContextMenuStrip = menu };
            _tray.MouseDoubleClick += (_, _) => OpenSettings();
        }

        partial void AddTrayItemsCore(WinForms.ContextMenuStrip menu);
        private void AddTrayItems(WinForms.ContextMenuStrip menu) => AddTrayItemsCore(menu);

        private void OpenSettings()
        {
            if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
            _settingsWindow = new SettingsWindow(_settings, _ocr?.LanguageName);
            _settingsWindow.Saved += () =>
            {
                ApplyKeys();
                Hardware.Enabled = _settings.UseGpu;
                Autostart.Apply(_settings.StartWithWindows);
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        }
    }
}
