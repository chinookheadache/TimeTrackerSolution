using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotShared.Messaging;

namespace ScreenshotClient
{
    public partial class MainWindow : Window
    {
        private readonly PipeClient _pipeClient = new("ScreenshotPipe");
        private readonly CancellationTokenSource _cts = new();

        private string _baseFolder = "";
        private bool _isCapturing = false;

        private FileSystemWatcher? _watcher;
        private string _currentDayFolder = "";

        public MainWindow()
        {
            InitializeComponent();

            // Disable controls until pipe connects
            SetControlsEnabled(false);
            UpdateServiceVisuals(isCapturing: false);

            // TOP is status-only (non-clickable); BOTTOM is the toggle
            PauseBtn.Click += async (_, __) =>
            {
                if (_isCapturing)
                    await SendCommandAsync("StopCapture");
                else
                    await SendCommandAsync("StartCapture");
            };

            FolderBtn.Click += FolderBtn_Click;

            IntervalBox.TextChanged += async (_, __) =>
            {
                if (!_pipeClient.IsConnected) return;
                if (int.TryParse(IntervalBox.Text, out var s) && s > 0)
                    await SendCommandAsync("SetInterval", value: s.ToString());
            };

            QualityBox.TextChanged += async (_, __) =>
            {
                if (!_pipeClient.IsConnected) return;
                if (int.TryParse(QualityBox.Text, out var q) && q is >= 1 and <= 100)
                    await SendCommandAsync("SetQuality", value: q.ToString());
            };

            // Pipe events
            _pipeClient.MessageReceived += OnPipeMessageReceived;
            _pipeClient.ReceiveFaulted += ex =>
            {
                // Server disappeared or pipe broke: close cleanly
                Dispatcher.Invoke(() =>
                {
                    System.Windows.Application.Current.Shutdown();
                });
            };

            // On load, connect (quietly retry) and let Tracker push state on connect
            Loaded += async (_, __) =>
            {
                RefreshDaySelector();
                SetupWatcherForToday();

                var connected = await TryConnectWithRetriesAsync(
                    totalTimeout: TimeSpan.FromSeconds(10),
                    attemptDelay: TimeSpan.FromMilliseconds(500));

                if (connected)
                {
                    SetControlsEnabled(true);
                    // Do NOT send anything here. Tracker will push SettingsSync + CaptureState on connect.
                }
                else
                {
                    // Leave UI disabled; user can start Tracker and re-open Client.
                }
            };

            // Dispose pipe on unload
            Unloaded += async (_, __) =>
            {
                try { _cts.Cancel(); } catch { /* ignore */ }
                await _pipeClient.DisposeAsync();
            };

            // Ensure clean async dispose on window close (no UI-thread blocking)
            this.Closing += MainWindow_Closing;

            // Day selector change → load newest image & watch that folder
            DaySelector.SelectionChanged += (_, __) =>
            {
                try
                {
                    if (DaySelector.SelectedItem is not string day) return;
                    var dayPath = Path.Combine(_baseFolder, day);
                    LoadMostRecentIn(dayPath);
                    SetupWatcherForSelectedDay();
                }
                catch { }
            };
        }

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                _cts.Cancel();
                await _pipeClient.DisposeAsync();
            }
            catch { /* ignore on close */ }
        }

        private async Task SendCommandAsync(string command, string? value = null, string? path = null)
        {
            if (!_pipeClient.IsConnected)
            {
                System.Windows.MessageBox.Show("Not connected to the tracker yet.");
                return;
            }
            await _pipeClient.SendAsync(new PipeMessage { Command = command, Value = value, Path = path }, _cts.Token);
        }

        private async Task<bool> TryConnectWithRetriesAsync(TimeSpan totalTimeout, TimeSpan attemptDelay)
        {
            var deadline = DateTime.UtcNow + totalTimeout;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await _pipeClient.ConnectAsync(TimeSpan.FromSeconds(2), _cts.Token);
                    if (_pipeClient.IsConnected)
                        return true;
                }
                catch
                {
                    // ignore and retry
                }

                await Task.Delay(attemptDelay);
            }

            return false;
        }

        private async void FolderBtn_Click(object? sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            var result = dlg.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
            {
                // Client does NOT persist — send to Tracker (source of truth)
                var chosen = dlg.SelectedPath;
                await SendCommandAsync("SetFolder", path: chosen);
                // We’ll get SettingsSync back and update UI then.
            }
        }

        private void OnPipeMessageReceived(PipeMessage msg)
        {
            Dispatcher.Invoke(() =>
            {
                switch (msg.Event)
                {
                    case "SettingsSync":
                        // Value: "interval;jpeg", Path: BaseFolder (authoritative from Tracker)
                        if (!string.IsNullOrEmpty(msg.Value))
                        {
                            var parts = msg.Value.Split(';');
                            if (parts.Length == 2
                                && int.TryParse(parts[0], out var i)
                                && int.TryParse(parts[1], out var q))
                            {
                                IntervalBox.Text = i.ToString();
                                QualityBox.Text = q.ToString();
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(msg.Path))
                        {
                            _baseFolder = msg.Path!;
                            RefreshDaySelector();
                            SetupWatcherForToday();
                        }
                        break;

                    case "CaptureStarted":
                        _isCapturing = true;
                        UpdateServiceVisuals(true);
                        break;

                    case "CaptureStopped":
                        _isCapturing = false;
                        UpdateServiceVisuals(false);
                        break;

                    case "CaptureState":
                        _isCapturing = string.Equals(msg.Value, "Running", StringComparison.OrdinalIgnoreCase);
                        UpdateServiceVisuals(_isCapturing);
                        break;

                    case "ScreenshotSaved":
                        if (!string.IsNullOrWhiteSpace(msg.Path))
                        {
                            LoadImageToViewer(msg.Path!);
                            EnsureDayInSelector(Path.GetDirectoryName(msg.Path!) ?? "");
                        }
                        break;

                    case "TrackerExiting":
                        // Close the client immediately when the tracker exits
                        System.Windows.Application.Current.Shutdown();
                        break;
                }
            });
        }

        private void SetControlsEnabled(bool enabled)
        {
            // Status display remains enabled (non-clickable via XAML)
            PauseBtn.IsEnabled = enabled;
            FolderBtn.IsEnabled = enabled;
            IntervalBox.IsEnabled = enabled;
            QualityBox.IsEnabled = enabled;
        }

        /// <summary>Top display shows status (green/red). Bottom toggle shows Start/Stop label.</summary>
        private void UpdateServiceVisuals(bool isCapturing)
        {
            _isCapturing = isCapturing;

            if (isCapturing)
            {
                StartBtn.Content = "Service is Running";
                StartBtn.Background = System.Windows.Media.Brushes.Green;
                StartBtn.Foreground = System.Windows.Media.Brushes.White;

                PauseBtn.Content = "Stop service";
                PauseBtn.Background = System.Windows.Media.Brushes.LightGray;
                PauseBtn.Foreground = System.Windows.Media.Brushes.Black;
            }
            else
            {
                StartBtn.Content = "Service is Stopped";
                StartBtn.Background = System.Windows.Media.Brushes.Red;
                StartBtn.Foreground = System.Windows.Media.Brushes.White;

                PauseBtn.Content = "Start service";
                PauseBtn.Background = System.Windows.Media.Brushes.LightGray;
                PauseBtn.Foreground = System.Windows.Media.Brushes.Black;
            }
        }

        private void RefreshDaySelector()
        {
            try
            {
                DaySelector.Items.Clear();
                if (!Directory.Exists(_baseFolder)) return;

                var days = Directory.EnumerateDirectories(_baseFolder)
                                    .Select(Path.GetFileName)
                                    .Where(d => !string.IsNullOrWhiteSpace(d))
                                    .OrderByDescending(d => d)
                                    .ToList();

                foreach (var d in days) DaySelector.Items.Add(d);
                if (DaySelector.Items.Count > 0) DaySelector.SelectedIndex = 0;
            }
            catch { /* ignore */ }
        }

        private void EnsureDayInSelector(string dayFolder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dayFolder)) return;
                var day = Path.GetFileName(dayFolder);
                if (string.IsNullOrWhiteSpace(day)) return;

                if (!DaySelector.Items.OfType<string>().Contains(day))
                {
                    DaySelector.Items.Insert(0, day);
                }
            }
            catch { }
        }

        private void LoadImageToViewer(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();
                ScreenshotView.Source = bmp;
            }
            catch { /* ignore display errors for now */ }
        }

        // === Watchers ===
        private void SetupWatcherForToday()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_baseFolder)) return;
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var dayPath = Path.Combine(_baseFolder, today);
                Directory.CreateDirectory(dayPath);

                if (string.Equals(_currentDayFolder, dayPath, StringComparison.OrdinalIgnoreCase))
                    return;

                _watcher?.Dispose();
                _watcher = new FileSystemWatcher(dayPath, "*.jpg")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _watcher.Created += (_, e) => Dispatcher.Invoke(() =>
                {
                    LoadImageToViewer(e.FullPath);
                    EnsureDayInSelector(dayPath);
                });

                _currentDayFolder = dayPath;
            }
            catch { }
        }

        private void SetupWatcherForSelectedDay()
        {
            try
            {
                _watcher?.Dispose();
                _watcher = null;

                if (string.IsNullOrWhiteSpace(_baseFolder)) return;
                if (DaySelector.SelectedItem is not string dayName) return;

                var dayPath = Path.Combine(_baseFolder, dayName);
                if (!Directory.Exists(dayPath)) return;

                _watcher = new FileSystemWatcher(dayPath, "*.jpg")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _watcher.Created += (_, e) => Dispatcher.Invoke(() =>
                {
                    LoadImageToViewer(e.FullPath);
                });

                _currentDayFolder = dayPath;
            }
            catch { }
        }

        private void LoadMostRecentIn(string dayPath)
        {
            try
            {
                if (!Directory.Exists(dayPath)) return;
                var latest = Directory.EnumerateFiles(dayPath, "*.jpg")
                                      .OrderByDescending(File.GetCreationTimeUtc)
                                      .FirstOrDefault();
                if (latest != null) LoadImageToViewer(latest);
            }
            catch { }
        }
    }
}
