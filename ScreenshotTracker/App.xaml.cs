using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ScreenshotTracker.Core;

namespace ScreenshotTracker
{
    public partial class App : System.Windows.Application
    {
        private TrackerService? _service;
        private TrackerRunner? _runner;

        // Single-instance mutex. Use "Local\" so it’s per-user session and avoids admin/global rights.
        private static Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // --- Single-instance guard ---
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\TimeTrackerSolution.ScreenshotTracker", out bool createdNew);
            if (!createdNew)
            {
                // Another instance is already running.
                // Optional convenience: try to launch the Client, then exit.
                TryLaunchClientSideBySide();

                // Exit immediately; do NOT start a second server
                Shutdown();
                return;
            }

            // Start pipe server + capture engine
            _service = new TrackerService("ScreenshotPipe");
            _service.Start();

            // Build tray (Open Client / Exit) — no WPF window at all
            _runner = TrackerRunner.Initialize();

            // Stay headless until we explicitly shut down
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        /// <summary>
        /// Called by tray Exit menu.
        /// Broadcasts TrackerExiting, stops capture, disposes, and shuts down.
        /// </summary>
        public async Task ExitFromTrayAsync()
        {
            try
            {
                if (_service is not null)
                {
                    await _service.RequestShutdownAsync();
                    await _service.DisposeAsync();
                }
            }
            catch { /* swallow on exit */ }
            finally
            {
                _runner?.Dispose();
                // Release the instance mutex before shutting down
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
                try { _singleInstanceMutex?.Dispose(); } catch { }
                Dispatcher.Invoke(Shutdown);
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                _runner?.Dispose();
                if (_service is not null)
                {
                    await _service.DisposeAsync();
                }
            }
            catch { /* ignore on exit */ }
            finally
            {
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
                try { _singleInstanceMutex?.Dispose(); } catch { }
            }
            base.OnExit(e);
        }

        /// <summary>
        /// Convenience: when a second instance is started, try to open ScreenshotClient.exe next to the Tracker.
        /// </summary>
        private static void TryLaunchClientSideBySide()
        {
            try
            {
                var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                var clientPath = Path.Combine(exeDir, "ScreenshotClient.exe");
                if (File.Exists(clientPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = clientPath,
                        WorkingDirectory = exeDir,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // Best-effort only; ignore failures.
            }
        }
    }
}
