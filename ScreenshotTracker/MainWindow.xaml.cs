using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms; // For NotifyIcon, ContextMenuStrip
using Application = System.Windows.Application; // Disambiguate from Forms.Application

namespace ScreenshotTracker
{
    public partial class MainWindow : Window
    {
        private NotifyIcon _trayIcon;

        public MainWindow()
        {
            InitializeComponent();

            // Setup tray icon
            _trayIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Visible = true,
                Text = "PVI Time Tracker"
            };

            // Double-click opens Client
            _trayIcon.DoubleClick += async (_, __) => await LaunchClientAsync();

            // Right-click menu
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Open Client", null, async (_, __) => await LaunchClientAsync());
            contextMenu.Items.Add("Exit", null, async (_, __) => await ExitTrackerAsync());
            _trayIcon.ContextMenuStrip = contextMenu;

            // Hide the main window (tray-driven app)
            Hide();

            // Minimize-to-tray: prevent window from actually closing on [X]
            Closing += MainWindow_Closing;
        }

        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                var icoPath = Path.Combine(exeDir, "AppIcon.ico"); // matches your output artifact name
                if (File.Exists(icoPath))
                {
                    return new System.Drawing.Icon(icoPath);
                }
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        private async System.Threading.Tasks.Task LaunchClientAsync()
        {
            try
            {
                // Prefer client EXE located next to tracker (packaged)
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
                    return;
                }

                // Dev fallback: simple message (add extra search heuristics here if you want)
                System.Windows.Forms.MessageBox.Show("Could not find ScreenshotClient.exe next to the Tracker.", "Open Client",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show($"Failed to launch client:\n{ex.Message}", "Open Client",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Tray Exit → coordinated shutdown via App.ExitFromTrayAsync():
        /// broadcasts TrackerExiting (client closes), stops capture, disposes server, then app shuts down.
        /// </summary>
        private async System.Threading.Tasks.Task ExitTrackerAsync()
        {
            try
            {
                // Hide tray immediately to avoid lingering icon
                _trayIcon.Visible = false;

                if (Application.Current is App app)
                {
                    await app.ExitFromTrayAsync(); // defined in App.xaml.cs
                }
                else
                {
                    // Fallback: hard shutdown if somehow not our App
                    Application.Current?.Dispatcher.Invoke(Application.Current.Shutdown);
                }
            }
            finally
            {
                try { _trayIcon.Dispose(); } catch { }
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Minimize to tray instead of closing from [X]
            e.Cancel = true;
            Hide();
        }
    }
}
