// ScreenshotTracker/TrackerRunner.cs
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using ScreenshotShared.Logging;
using ScreenshotTracker.Core;

namespace ScreenshotTracker
{
    /// <summary>
    /// Manages the NotifyIcon tray UI and routes actions to TrackerService.
    /// </summary>
    public sealed class TrackerRunner : IDisposable
    {
        private readonly TrackerService _service;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _openClientItem;
        private readonly ToolStripMenuItem _startWithWindowsItem;
        private readonly ToolStripMenuItem _autoStartCaptureItem;
        private readonly ToolStripMenuItem _exitItem;

        public TrackerRunner(TrackerService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));

            _tray = new NotifyIcon
            {
                Text = "TimeTrackerSolution",
                Icon = SystemIcons.Application,
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };

            _openClientItem = new ToolStripMenuItem("Open Client");
            _openClientItem.Click += (_, __) => LaunchClient();

            _tray.ContextMenuStrip.Items.Add(_openClientItem);
            _tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());

            // Step 7 toggles
            _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
            {
                CheckOnClick = true,
                Checked = _service.GetStartWithWindows()
            };
            _startWithWindowsItem.Click += (_, __) =>
            {
                try
                {
                    var desired = _startWithWindowsItem.Checked;
                    _service.SetStartWithWindows(desired);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Toggle Start with Windows failed");
                    _startWithWindowsItem.Checked = _service.GetStartWithWindows();
                }
            };

            _autoStartCaptureItem = new ToolStripMenuItem("Auto-start capture")
            {
                CheckOnClick = true,
                Checked = _service.GetAutoStartCapture()
            };
            _autoStartCaptureItem.Click += async (_, __) =>
            {
                try
                {
                    var desired = _autoStartCaptureItem.Checked;
                    await _service.SetAutoStartCaptureAsync(desired);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Toggle Auto-start capture failed");
                    _autoStartCaptureItem.Checked = _service.GetAutoStartCapture();
                }
            };

            _tray.ContextMenuStrip.Items.Add(_startWithWindowsItem);
            _tray.ContextMenuStrip.Items.Add(_autoStartCaptureItem);
            _tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());

            _exitItem = new ToolStripMenuItem("Exit");
            _exitItem.Click += async (_, __) =>
            {
                try
                {
                    await _service.RequestShutdownAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "RequestShutdown from tray failed");
                }
                finally
                {
                    try { System.Windows.Application.Current.Shutdown(); } catch { }
                }
            };
            _tray.ContextMenuStrip.Items.Add(_exitItem);
        }

        private void LaunchClient()
        {
            try
            {
                var exe = "ScreenshotClient.exe";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to launch client", "tracker");
                System.Windows.MessageBox.Show("Failed to launch client.", "TimeTrackerSolution",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void Dispose()
        {
            try { _tray.Visible = false; } catch { }
            try { _tray.Dispose(); } catch { }
        }
    }
}
