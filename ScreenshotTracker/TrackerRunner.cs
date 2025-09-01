using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;                      // WPF Application
using WinForms = System.Windows.Forms;     // alias to avoid ambiguity

namespace ScreenshotTracker
{
    /// <summary>
    /// Hosts the system tray icon and context menu for the Tracker.
    /// Call TrackerRunner.Initialize() once at startup, and Dispose() on exit.
    /// </summary>
    public sealed class TrackerRunner : IDisposable
    {
        private readonly WinForms.NotifyIcon _tray;
        private readonly WinForms.ContextMenuStrip _menu;
        private readonly WinForms.ToolStripMenuItem _openClientItem;
        private readonly WinForms.ToolStripMenuItem _exitItem;

        private bool _disposed;

        private TrackerRunner()
        {
            // Build menu
            _menu = new WinForms.ContextMenuStrip();

            _openClientItem = new WinForms.ToolStripMenuItem("Open Client");
            _openClientItem.Click += async (_, __) => await OpenClientAsync();

            _exitItem = new WinForms.ToolStripMenuItem("Exit");
            _exitItem.Click += async (_, __) => await ExitFromTrayAsync();

            _menu.Items.Add(_openClientItem);
            _menu.Items.Add(new WinForms.ToolStripSeparator());
            _menu.Items.Add(_exitItem);

            // Create tray icon
            _tray = new WinForms.NotifyIcon
            {
                Text = "PVI Time Tracker – Tracker",
                Visible = true,
                ContextMenuStrip = _menu
            };

            // Try to load an icon. Falls back to a system icon if not found.
            try
            {
                var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                var icoPath = Path.Combine(exeDir, "AppIcon.ico");
                if (File.Exists(icoPath))
                {
                    _tray.Icon = new System.Drawing.Icon(icoPath);
                }
                else
                {
                    _tray.Icon = System.Drawing.SystemIcons.Application;
                }
            }
            catch
            {
                _tray.Icon = System.Drawing.SystemIcons.Application;
            }

            // Double-click tray to open client
            _tray.DoubleClick += async (_, __) => await OpenClientAsync();
        }

        /// <summary>Call once from App.OnStartup.</summary>
        public static TrackerRunner Initialize() => new TrackerRunner();

        /// <summary>
        /// Launch the ScreenshotClient.exe located alongside the Tracker (packaged).
        /// </summary>
        private static Task OpenClientAsync()
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
                    return Task.CompletedTask;
                }

                WinForms.MessageBox.Show(
                    "Could not find ScreenshotClient.exe next to the Tracker.",
                    "Open Client",
                    WinForms.MessageBoxButtons.OK,
                    WinForms.MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                WinForms.MessageBox.Show(
                    $"Failed to launch client:\n{ex.Message}",
                    "Open Client",
                    WinForms.MessageBoxButtons.OK,
                    WinForms.MessageBoxIcon.Error);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Coordinated shutdown: broadcast TrackerExiting via App.ExitFromTrayAsync(), then close app.
        /// </summary>
        private static async Task ExitFromTrayAsync()
        {
            if (System.Windows.Application.Current is App app)
            {
                await app.ExitFromTrayAsync();
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(System.Windows.Application.Current.Shutdown);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _tray.Visible = false; } catch { }
            try { _tray.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }
        }
    }
}
