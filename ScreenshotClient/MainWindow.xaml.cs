using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotShared.Logging;
using ScreenshotShared.Messaging;

namespace ScreenshotClient
{
    public partial class MainWindow : Window
    {
        private readonly PipeClient _pipeClient = new("ScreenshotPipe");
        private readonly CancellationTokenSource _cts = new();

        private string _baseFolder = "";
        private bool _isCapturing = false;

        private FileSystemWatcher? _dayWatcher;     // watches files in the selected day folder
        private FileSystemWatcher? _baseWatcher;    // watches the base folder for day folders
        private string _currentDayFolder = "";

        // Timeline state for the selected day
        private readonly List<string> _currentDayImages = new();
        private bool _suppressSlider;

        // Suppress command echo when applying SettingsSync
        private bool _suppressSettingsUpdate;

        // === Auto-follow heuristic (with 5s cooldown) ===
        private bool _isUserScrubbing = false;
        private DateTime _lastUserScrubbedAtUtc = DateTime.MinValue;
        private static readonly TimeSpan AutoFollowCooldown = TimeSpan.FromSeconds(5);
        private const double TailTolerance = 0.5; // within half a tick counts as "at tail"

        // === Debounce for heavy FS bursts ===
        private CancellationTokenSource? _debounceCts;
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

        public MainWindow()
        {
            InitializeComponent();

            try
            {
                // Disable controls until pipe connects
                SetControlsEnabled(false);
                UpdateServiceVisuals(isCapturing: false);

                // TOP is status-only (non-clickable); BOTTOM is the toggle
                PauseBtn.Click += async (_, __) =>
                {
                    if (_isCapturing) await SafeSendAsync(PipeMessage.Cmd("StopCapture"));
                    else await SafeSendAsync(PipeMessage.Cmd("StartCapture"));
                };

                FolderBtn.Click += FolderBtn_Click;

                IntervalBox.TextChanged += async (_, __) =>
                {
                    if (_suppressSettingsUpdate) return;
                    if (!_pipeClient.IsConnected) return;
                    if (int.TryParse(IntervalBox.Text, out var s) && s > 0)
                        await SafeSendAsync(PipeMessage.Cmd("SetInterval", value: s.ToString()));
                };

                QualityBox.TextChanged += async (_, __) =>
                {
                    if (_suppressSettingsUpdate) return;
                    if (!_pipeClient.IsConnected) return;
                    if (int.TryParse(QualityBox.Text, out var q) && q is >= 1 and <= 100)
                        await SafeSendAsync(PipeMessage.Cmd("SetQuality", value: q.ToString()));
                };

                // Pipe events
                _pipeClient.MessageReceived += OnPipeMessageReceived;
                _pipeClient.ReceiveFaulted += ex =>
                {
                    Logger.LogError(ex, "Pipe receive faulted", "client");
                    Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
                };

                // Slider → load that index
                TimelineSlider.ValueChanged += (_, __) =>
                {
                    if (_suppressSlider) return;
                    var idx = (int)Math.Round(TimelineSlider.Value);
                    if (idx >= 0 && idx < _currentDayImages.Count)
                        LoadImageToViewer(_currentDayImages[idx]);
                };

                // Track scrubbing state explicitly (prevents auto-follow while user is dragging)
                TimelineSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, __) =>
                {
                    _isUserScrubbing = true;
                }));
                TimelineSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, __) =>
                {
                    _isUserScrubbing = false;
                    _lastUserScrubbedAtUtc = DateTime.UtcNow;

                    // If the user let go near the end, snap to the latest and re-enable auto-follow immediately
                    if ((TimelineSlider.Maximum - TimelineSlider.Value) <= TailTolerance && _currentDayImages.Count > 0)
                    {
                        _suppressSlider = true;
                        try
                        {
                            TimelineSlider.Value = TimelineSlider.Maximum; // jump to newest
                            LoadImageToViewer(_currentDayImages[(int)TimelineSlider.Value]);
                        }
                        finally { _suppressSlider = false; }

                        // Bypass cooldown when the user explicitly returns to the tail
                        _lastUserScrubbedAtUtc = DateTime.UtcNow - AutoFollowCooldown;
                    }
                }));

                // On load, connect (quietly retry) and let Tracker push state on connect
                Loaded += async (_, __) =>
                {
                    try
                    {
                        RefreshDaySelector();
                        SetupBaseWatcher();
                        SetupWatcherForToday();

                        var connected = await TryConnectWithRetriesAsync(
                            totalTimeout: TimeSpan.FromSeconds(10),
                            attemptDelay: TimeSpan.FromMilliseconds(500));

                        if (connected)
                        {
                            SetControlsEnabled(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Loaded handler failed", "client");
                    }
                };

                // Dispose pipe on unload
                Unloaded += async (_, __) =>
                {
                    try { _cts.Cancel(); } catch { /* ignore */ }
                    try { await _pipeClient.DisposeAsync(); } catch { }
                    DisposeWatchers();
                };

                // Ensure clean async dispose on window close (no UI-thread blocking)
                this.Closing += MainWindow_Closing;

                // Day selector change → rebuild timeline for that day
                DaySelector.SelectionChanged += (_, __) =>
                {
                    try
                    {
                        if (DaySelector.SelectedItem is not string day) return;
                        var dayPath = Path.Combine(_baseFolder, day);
                        BuildTimelineForDay(dayPath);
                        SetupWatcherForSelectedDay();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "DaySelector.SelectionChanged failed", "client");
                    }
                };
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "MainWindow ctor failed", "client");
                throw;
            }
        }

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                _cts.Cancel();
                await _pipeClient.DisposeAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Closing dispose failed", "client");
            }
            finally
            {
                DisposeWatchers();
            }
        }

        private async Task SafeSendAsync(PipeMessage msg)
        {
            try
            {
                if (!_pipeClient.IsConnected)
                {
                    System.Windows.MessageBox.Show("Not connected to the tracker yet.");
                    return;
                }
                await _pipeClient.SendAsync(msg, _cts.Token);
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogError(ex, "SafeSendAsync failed", "client");
                System.Windows.Application.Current.Shutdown();
            }
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
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Connect attempt failed", "client");
                    // ignore and retry
                }

                await Task.Delay(attemptDelay);
            }

            return false;
        }

        private async void FolderBtn_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog();
                var result = dlg.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
                {
                    var chosen = dlg.SelectedPath;
                    await SafeSendAsync(PipeMessage.Cmd("SetFolder", path: chosen));
                    // We’ll get SettingsSync back and update UI then.
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "FolderBtn_Click failed", "client");
            }
        }

        private void OnPipeMessageReceived(PipeMessage msg)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        switch (msg.Event)
                        {
                            case "SettingsSync":
                                _suppressSettingsUpdate = true;
                                try
                                {
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
                                }
                                finally { _suppressSettingsUpdate = false; }

                                if (!string.IsNullOrWhiteSpace(msg.Path))
                                {
                                    _baseFolder = msg.Path!;
                                    RefreshDaySelector();
                                    SetupBaseWatcher();
                                    SetupWatcherForToday();

                                    if (DaySelector.SelectedItem is string day)
                                    {
                                        var dayPath = Path.Combine(_baseFolder, day);
                                        BuildTimelineForDay(dayPath);
                                    }
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

                                    var savedDir = Path.GetDirectoryName(msg.Path!) ?? "";
                                    if (!string.IsNullOrWhiteSpace(savedDir) &&
                                        string.Equals(savedDir, _currentDayFolder, StringComparison.OrdinalIgnoreCase))
                                    {
                                        AddOrRefreshNewImage(msg.Path!);
                                    }
                                }
                                break;

                            case "TrackerExiting":
                                System.Windows.Application.Current.Shutdown();
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "OnPipeMessageReceived switch failed", "client");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "OnPipeMessageReceived outer failed", "client");
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
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
            catch (Exception ex)
            {
                Logger.LogError(ex, "RefreshDaySelector failed", "client");
            }
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
            catch (Exception ex)
            {
                Logger.LogError(ex, "EnsureDayInSelector failed", "client");
            }
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
            catch (Exception ex)
            {
                Logger.LogError(ex, $"LoadImageToViewer failed for '{path}'", "client");
            }
        }

        // === Auto-follow logic ===
        private bool ShouldAutoFollowTail()
        {
            if (!TimelineSlider.IsEnabled) return false;
            if (_isUserScrubbing) return false;

            var elapsed = DateTime.UtcNow - _lastUserScrubbedAtUtc;
            if (elapsed < AutoFollowCooldown) return false;

            return (TimelineSlider.Maximum <= TimelineSlider.Minimum)
                   || ((TimelineSlider.Maximum - TimelineSlider.Value) <= TailTolerance);
        }

        // === Timeline ===
        private void BuildTimelineForDay(string dayPath)
        {
            try
            {
                _currentDayFolder = dayPath;
                _currentDayImages.Clear();

                if (Directory.Exists(dayPath))
                {
                    _currentDayImages.AddRange(
                        Directory.EnumerateFiles(dayPath, "*.jpg")
                                 .OrderBy(File.GetCreationTimeUtc));
                }

                _suppressSlider = true;
                try
                {
                    if (_currentDayImages.Count == 0)
                    {
                        TimelineSlider.IsEnabled = false;
                        TimelineSlider.Minimum = 0;
                        TimelineSlider.Maximum = 0;
                        TimelineSlider.Value = 0;
                        ScreenshotView.Source = null;
                    }
                    else
                    {
                        TimelineSlider.IsEnabled = true;
                        TimelineSlider.Minimum = 0;
                        TimelineSlider.Maximum = _currentDayImages.Count - 1;
                        TimelineSlider.Value = TimelineSlider.Maximum;  // start at newest
                        LoadImageToViewer(_currentDayImages.Last());

                        _lastUserScrubbedAtUtc = DateTime.UtcNow - AutoFollowCooldown;
                    }
                }
                finally { _suppressSlider = false; }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "BuildTimelineForDay failed", "client");
            }
        }

        private void AddOrRefreshNewImage(string path)
        {
            try
            {
                if (!_currentDayImages.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _currentDayImages.Add(path);
                    _currentDayImages.Sort((a, b) =>
                        File.GetCreationTimeUtc(a).CompareTo(File.GetCreationTimeUtc(b)));

                    _suppressSlider = true;
                    try
                    {
                        TimelineSlider.Maximum = Math.Max(0, _currentDayImages.Count - 1);

                        if (ShouldAutoFollowTail())
                        {
                            TimelineSlider.Value = TimelineSlider.Maximum;
                        }
                    }
                    finally { _suppressSlider = false; }

                    var idx = (int)Math.Round(TimelineSlider.Value);
                    if (idx >= 0 && idx < _currentDayImages.Count)
                        LoadImageToViewer(_currentDayImages[idx]);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "AddOrRefreshNewImage failed", "client");
            }
        }

        private void RemoveImageIfPresent(string path)
        {
            try
            {
                var idx = _currentDayImages.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return;

                _currentDayImages.RemoveAt(idx);

                _suppressSlider = true;
                try
                {
                    if (_currentDayImages.Count == 0)
                    {
                        TimelineSlider.IsEnabled = false;
                        TimelineSlider.Minimum = 0;
                        TimelineSlider.Maximum = 0;
                        TimelineSlider.Value = 0;
                        ScreenshotView.Source = null;
                    }
                    else
                    {
                        TimelineSlider.Maximum = _currentDayImages.Count - 1;
                        if (TimelineSlider.Value > TimelineSlider.Maximum)
                            TimelineSlider.Value = TimelineSlider.Maximum;

                        var v = (int)Math.Round(TimelineSlider.Value);
                        if (v >= 0 && v < _currentDayImages.Count)
                            LoadImageToViewer(_currentDayImages[v]);
                    }
                }
                finally { _suppressSlider = false; }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "RemoveImageIfPresent failed", "client");
            }
        }

        // === Watchers ===

        private void DisposeWatchers()
        {
            try { _dayWatcher?.Dispose(); } catch (Exception ex) { Logger.LogError(ex, "Dispose dayWatcher", "client"); }
            try { _baseWatcher?.Dispose(); } catch (Exception ex) { Logger.LogError(ex, "Dispose baseWatcher", "client"); }
            _dayWatcher = null;
            _baseWatcher = null;
        }

        private void SetupBaseWatcher()
        {
            try
            {
                _baseWatcher?.Dispose(); _baseWatcher = null;
                if (string.IsNullOrWhiteSpace(_baseFolder) || !Directory.Exists(_baseFolder)) return;

                _baseWatcher = new FileSystemWatcher(_baseFolder)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                _baseWatcher.Created += (_, e) =>
                {
                    if (Directory.Exists(e.FullPath))
                    {
                        DebounceUi(() => EnsureDayInSelector(e.FullPath));
                    }
                };

                _baseWatcher.Renamed += (_, __) =>
                {
                    DebounceUi(RefreshDaySelector);
                };

                _baseWatcher.Deleted += (_, __) =>
                {
                    DebounceUi(() =>
                    {
                        RefreshDaySelector();
                    });
                };
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "SetupBaseWatcher failed", "client");
            }
        }

        private void SetupWatcherForToday()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_baseFolder)) return;
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var dayPath = Path.Combine(_baseFolder, today);
                Directory.CreateDirectory(dayPath);

                if (!string.Equals(_currentDayFolder, dayPath, StringComparison.OrdinalIgnoreCase))
                {
                    SetupWatcherForDay(dayPath);
                }

                if (DaySelector.SelectedItem is string day && string.Equals(day, today, StringComparison.OrdinalIgnoreCase))
                {
                    BuildTimelineForDay(dayPath);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "SetupWatcherForToday failed", "client");
            }
        }

        private void SetupWatcherForSelectedDay()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_baseFolder)) return;
                if (DaySelector.SelectedItem is not string dayName) return;

                var dayPath = Path.Combine(_baseFolder, dayName);
                if (!Directory.Exists(dayPath)) return;

                SetupWatcherForDay(dayPath);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "SetupWatcherForSelectedDay failed", "client");
            }
        }

        private void SetupWatcherForDay(string dayPath)
        {
            try
            {
                _dayWatcher?.Dispose();
                _dayWatcher = null;

                _dayWatcher = new FileSystemWatcher(dayPath, "*.jpg")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                _dayWatcher.Created += (_, e) => Dispatcher.Invoke(() =>
                {
                    DebounceUi(() =>
                    {
                        if (string.Equals(dayPath, _currentDayFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            AddOrRefreshNewImage(e.FullPath);
                        }
                        EnsureDayInSelector(dayPath);
                    });
                });

                _dayWatcher.Deleted += (_, e) => Dispatcher.Invoke(() =>
                {
                    DebounceUi(() =>
                    {
                        if (string.Equals(dayPath, _currentDayFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            RemoveImageIfPresent(e.FullPath);
                        }
                    });
                });

                _dayWatcher.Renamed += (_, e) => Dispatcher.Invoke(() =>
                {
                    DebounceUi(() =>
                    {
                        if (string.Equals(dayPath, _currentDayFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            RemoveImageIfPresent(e.OldFullPath);
                            if (Path.GetExtension(e.FullPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                                AddOrRefreshNewImage(e.FullPath);
                        }
                    });
                });

                _currentDayFolder = dayPath;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "SetupWatcherForDay failed", "client");
            }
        }

        // === Debounce helper for UI work (coalesce bursts) ===
        private void DebounceUi(Action action)
        {
            try { _debounceCts?.Cancel(); } catch { }
            var cts = new CancellationTokenSource();
            _debounceCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DebounceDelay, cts.Token);
                    if (!cts.IsCancellationRequested)
                    {
                        Dispatcher.Invoke(action);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "DebounceUi background task failed", "client");
                }
            });
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
            catch (Exception ex)
            {
                Logger.LogError(ex, "LoadMostRecentIn failed", "client");
            }
        }
    }
}
