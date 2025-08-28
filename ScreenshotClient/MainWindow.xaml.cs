using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotShared.Messaging;
using ScreenshotShared.Settings;

namespace ScreenshotClient
{
    public partial class MainWindow : Window
    {
        private readonly PipeClient _pipeClient = new("ScreenshotPipe");
        private readonly CancellationTokenSource _cts = new();

        private string _baseFolder = "";
        private bool _isCapturing = false;

        public MainWindow()
        {
            InitializeComponent();

            // Disable controls until pipe connects; default visuals
            SetControlsEnabled(false);
            UpdateServiceVisuals(isCapturing: false);

            // Hook UI events
            StartBtn.Click += async (_, __) => await SendCommandAsync("StartCapture");
            PauseBtn.Click += async (_, __) => await SendCommandAsync("StopCapture");
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

            // Pipe wiring
            _pipeClient.MessageReceived += OnPipeMessageReceived;
            _pipeClient.ReceiveFaulted += ex =>
            {
                Dispatcher.Invoke(() =>
                {
                    SetControlsEnabled(false);
                    UpdateServiceVisuals(isCapturing: false);
                });
            };

            Loaded += async (_, __) =>
            {
                // 1) Load last-used settings locally so UI isn't blank
                var settings = AppSettings.Load();
                _baseFolder = settings.BaseFolder;
                IntervalBox.Text = settings.IntervalSeconds.ToString();
                QualityBox.Text = settings.JpegQuality.ToString();
                RefreshDaySelector();

                // 2) Connect to tracker and sync current state (if tracker running)
                try
                {
                    await _pipeClient.ConnectAsync(TimeSpan.FromSeconds(3), _cts.Token);
                    if (_pipeClient.IsConnected)
                    {
                        SetControlsEnabled(true);

                        // Ask tracker to reply with Settings + State
                        await _pipeClient.SendAsync(PipeMessage.Cmd("QueryState"), _cts.Token);

                        // Tell tracker our folder (if valid)
                        if (Directory.Exists(_baseFolder))
                        {
                            await _pipeClient.SendAsync(new PipeMessage { Command = "SetFolder", Path = _baseFolder }, _cts.Token);
                        }
                    }
                    else
                    {
                        System.Windows.MessageBox.Show("Failed to connect to ScreenshotTracker.");
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Pipe connection failed: {ex.Message}");
                }
            };

            Unloaded += async (_, __) =>
            {
                try { _cts.Cancel(); } catch { }
                await _pipeClient.DisposeAsync();
            };
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

        private async void FolderBtn_Click(object? sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            var result = dlg.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
            {
                _baseFolder = dlg.SelectedPath;

                // Persist locally (shared settings)
                var s = AppSettings.Load();
                s.BaseFolder = _baseFolder;
                Directory.CreateDirectory(s.BaseFolder);
                s.Save();

                RefreshDaySelector();

                // Notify tracker
                if (_pipeClient.IsConnected)
                    await _pipeClient.SendAsync(new PipeMessage { Command = "SetFolder", Path = _baseFolder }, _cts.Token);
            }
        }

        private void OnPipeMessageReceived(PipeMessage msg)
        {
            Dispatcher.Invoke(() =>
            {
                switch (msg.Event)
                {
                    case "SettingsSync":
                        // Value: "interval;jpeg", Path: BaseFolder
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
                        // Value: "Running" | "Stopped"
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
                }
            });
        }

        private void SetControlsEnabled(bool enabled)
        {
            StartBtn.IsEnabled = enabled;
            PauseBtn.IsEnabled = enabled;
            FolderBtn.IsEnabled = enabled;
            IntervalBox.IsEnabled = enabled;
            QualityBox.IsEnabled = enabled;
        }

        /// <summary>
        /// Updates Start/Pause button look AND Start button text to:
        /// "Service is Running" / "Service is Stopped"
        /// </summary>
        private void UpdateServiceVisuals(bool isCapturing)
        {
            if (isCapturing)
            {
                StartBtn.Content = "Service is Running";
                StartBtn.Background = System.Windows.Media.Brushes.Green;
                StartBtn.Foreground = System.Windows.Media.Brushes.White;

                PauseBtn.Background = System.Windows.Media.Brushes.LightGray;
                PauseBtn.Foreground = System.Windows.Media.Brushes.Black;
            }
            else
            {
                StartBtn.Content = "Service is Stopped";
                StartBtn.Background = System.Windows.Media.Brushes.LightGray;
                StartBtn.Foreground = System.Windows.Media.Brushes.Black;

                PauseBtn.Background = System.Windows.Media.Brushes.Red;
                PauseBtn.Foreground = System.Windows.Media.Brushes.White;
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
    }
}
