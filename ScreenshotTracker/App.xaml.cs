// ScreenshotTracker/App.xaml.cs
using System;
using System.Runtime.InteropServices;
using System.Windows;
using ScreenshotTracker.Core;

namespace ScreenshotTracker
{
    public partial class App : System.Windows.Application
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
        private static readonly IntPtr PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

        private TrackerService? _service;

        protected override void OnStartup(StartupEventArgs e)
        {
            SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2);
            base.OnStartup(e);
            _service = new TrackerService();
            // Keep tray initialization as-is if you have it.
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_service is not null) await _service.DisposeAsync();
            base.OnExit(e);
        }
    }
}