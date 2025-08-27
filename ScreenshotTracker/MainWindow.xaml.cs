using System;
using System.Windows;
using System.Windows.Forms; // For NotifyIcon
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
                Icon = new System.Drawing.Icon("appicon.ico"), // Make sure icon.ico is in the output folder
                Visible = true,
                Text = "PVI Time Tracker"
            };

            // Double-click opens Client
            _trayIcon.DoubleClick += (s, e) => LaunchClient();

            // Right-click menu
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Open Client", null, (s, e) => LaunchClient());
            contextMenu.Items.Add("Exit", null, (s, e) => ExitTracker());
            _trayIcon.ContextMenuStrip = contextMenu;

            // Hide the main window
            this.Hide();

            // Optional: handle when window is closed
            this.Closing += MainWindow_Closing;
        }

        private void LaunchClient()
        {
            try
            {
                System.Diagnostics.Process.Start("ScreenshotClient.exe");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to launch client: " + ex.Message);
            }
        }

        private void ExitTracker()
        {
            // Clean up tray icon
            _trayIcon.Visible = false;
            _trayIcon.Dispose();

            // Shutdown app
            Application.Current.Shutdown();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Minimize to tray instead of closing
            e.Cancel = true;
            this.Hide();
        }
    }
}
