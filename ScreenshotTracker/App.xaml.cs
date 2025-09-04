// ScreenshotTracker/App.xaml.cs
using System.Windows;
using ScreenshotShared.Logging;
using ScreenshotTracker.Core;

namespace ScreenshotTracker
{
    public partial class App : System.Windows.Application
    {
        private TrackerService? _service;
        private TrackerRunner? _runner;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _service = new TrackerService("ScreenshotPipe");
            _service.Start();

            _runner = new TrackerRunner(_service);

            // Headless WPF app (tray only)
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try { _runner?.Dispose(); } catch { }

            try
            {
                if (_service is not null)
                {
                    await _service.DisposeAsync();
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError(ex, "App.OnExit dispose failed");
            }

            base.OnExit(e);
        }
    }
}
