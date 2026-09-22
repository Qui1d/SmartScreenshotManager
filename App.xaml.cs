using System;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SmartScreenshotManager.Services;
using Windows.Storage;

namespace SmartScreenshotManager
{
    public partial class App : Application
    {
        private MainWindow? _window;
        private AppInstance? _instance;
        private DispatcherQueue? _dispatcher;
        private bool _restoreRequested;
        private static readonly object StartupLogGate = new();

        public App()
        {
            WriteStartupLog("App constructor entered");
            UnhandledException += (_, e) => WriteStartupError("Unhandled UI exception", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception exception)
                    WriteStartupError("Unhandled process exception", exception);
            };
            InitializeComponent();
            WriteStartupLog("App initialized");
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            WriteStartupLog("OnLaunched entered");
            try
            {
                _dispatcher = DispatcherQueue.GetForCurrentThread();
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                WriteStartupLog($"Activation kind: {activation.Kind}");
                _instance = AppInstance.FindOrRegisterForKey("SmartScreenshotManager.Main");
                if (!_instance.IsCurrent)
                {
                    WriteStartupLog("Redirecting to existing instance");
                    await _instance.RedirectActivationToAsync(activation);
                    WriteStartupLog("Redirection completed");
                    Exit();
                    return;
                }
                _instance.Activated += Instance_Activated;
                WriteStartupLog("Creating main window");
                _window = new MainWindow();
                _window.Closed += (_, _) =>
                {
                    WriteStartupLog("Main window closed");
                    _instance.Activated -= Instance_Activated;
                    _instance.UnregisterKey();
                    Exit();
                };

                // Initialize the WinUI window before hiding it for a startup activation.
                // If the tray is unavailable, leave the window accessible.
                _window.Activate();
                WriteStartupLog("Main window activated");
                if (activation.Kind == ExtendedActivationKind.StartupTask
                    && new SettingsService().CloseToTray)
                {
                    bool queued = _dispatcher.TryEnqueue(() =>
                    {
                        if (_restoreRequested) return;
                        try
                        {
                            bool hidden = _window.TryHideToTray();
                            WriteStartupLog(hidden ? "Startup ready: hidden in tray" : "Startup ready: tray unavailable, window left visible");
                        }
                        catch (Exception exception)
                        {
                            WriteStartupError("Could not hide startup window", exception);
                            _window.RestoreFromTray();
                        }
                    });
                    if (!queued) WriteStartupLog("Startup ready: window left visible, hide could not be queued");
                }
                else WriteStartupLog("Startup ready: window visible");
            }
            catch (Exception exception)
            {
                WriteStartupError("Launch failed", exception);
                throw;
            }
        }

        private void Instance_Activated(object? sender, AppActivationArguments args)
        {
            WriteStartupLog($"Redirected activation: {args.Kind}");
            if (args.Kind != ExtendedActivationKind.StartupTask)
                _dispatcher?.TryEnqueue(() =>
                {
                    _restoreRequested = true;
                    _window?.RestoreFromTray();
                });
        }

        private static void WriteStartupError(string stage, Exception exception)
        {
            // Record error identity and stack, without messages that might contain user data.
            WriteStartupLog($"{stage}: {exception.GetType().FullName}, HRESULT 0x{exception.HResult:X8}\n{exception.StackTrace}");
        }

        private static void WriteStartupLog(string message)
        {
            try
            {
                lock (StartupLogGate)
                {
                    string path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "startup.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                        File.Move(path, path + ".previous", true);
                    File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} PID {Environment.ProcessId}: {message}{Environment.NewLine}");
                }
            }
            catch { /* Diagnostics must not prevent launch. */ }
        }
    }
}
