// ScreenshotClient/MainWindow.xaml.cs
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ScreenshotShared.Messaging;
using ScreenshotShared.Settings;

namespace ScreenshotClient
{
    public partial class MainWindow : Window
    {
        private readonly PipeClient _client = new("ScreenshotPipe");
        private readonly CancellationTokenSource _cts = new();

        public MainViewModel VM { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = VM;

            Loaded += async (_, __) => { await TryConnectPipe(); };
            Unloaded += async (_, __) => { _cts.Cancel(); await _client.DisposeAsync(); };
        }

        private async Task TryConnectPipe()
        {
            try
            {
                await _client.ConnectAsync(TimeSpan.FromSeconds(3), _cts.Token);
                _client.MessageReceived += OnMessage;
                _client.ReceiveFaulted += ex => { /* optional log */ };
            }
            catch
            {
                System.Windows.MessageBox.Show("Unable to connect to Tracker. Please start the Tracker process.");
            }
        }

        private void OnMessage(PipeMessage msg)
        {
            Dispatcher.Invoke(() =>
            {
                switch (msg.Event)
                {
                    case "CaptureStarted":
                        VM.IsCapturing = true;
                        break;
                    case "CaptureStopped":
                        VM.IsCapturing = false;
                        break;
                    case "ScreenshotSaved":
                        if (!string.IsNullOrWhiteSpace(msg.Path))
                            VM.AddScreenshot(msg.Path!);
                        break;
                    case "SettingsSync":
                        if (!string.IsNullOrEmpty(msg.Value))
                        {
                            var parts = msg.Value.Split(';');
                            if (parts.Length == 2 &&
                                int.TryParse(parts[0], out var i) &&
                                int.TryParse(parts[1], out var q))
                            {
                                VM.IntervalSeconds = i;
                                VM.JpegQuality = q;
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(msg.Path))
                            VM.BaseFolder = msg.Path!;
                        break;
                }
            });
        }

        // Wire these handlers to your existing buttons in XAML:
        private async void Start_Click(object sender, RoutedEventArgs e) =>
            await _client.SendAsync(PipeMessage.Cmd("StartCapture"), _cts.Token);

        private async void Stop_Click(object sender, RoutedEventArgs e) =>
            await _client.SendAsync(PipeMessage.Cmd("StopCapture"), _cts.Token);

        private async void Folder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            var result = dlg.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
            {
                VM.BaseFolder = dlg.SelectedPath;
                await _client.SendAsync(new PipeMessage { Command = "SetFolder", Path = dlg.SelectedPath }, _cts.Token);
            }
        }
    }

    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly AppSettings _initial = AppSettings.Load();

        private string _baseFolder;
        private int _intervalSeconds;
        private int _jpegQuality;
        private bool _isCapturing;

        public MainViewModel()
        {
            _baseFolder = _initial.BaseFolder;
            _intervalSeconds = _initial.IntervalSeconds;
            _jpegQuality = _initial.JpegQuality;
        }

        public ObservableCollection<ScreenshotItem> Screenshots { get; } = new();

        public string BaseFolder { get => _baseFolder; set { _baseFolder = value; OnPropertyChanged(nameof(BaseFolder)); } }
        public int IntervalSeconds { get => _intervalSeconds; set { _intervalSeconds = value; OnPropertyChanged(nameof(IntervalSeconds)); } }
        public int JpegQuality { get => _jpegQuality; set { _jpegQuality = value; OnPropertyChanged(nameof(JpegQuality)); } }
        public bool IsCapturing { get => _isCapturing; set { _isCapturing = value; OnPropertyChanged(nameof(IsCapturing)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void AddScreenshot(string path)
        {
            Screenshots.Insert(0, new ScreenshotItem { Path = path, Thumbnail = LoadThumb(path) });
        }

        private static BitmapImage LoadThumb(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 480;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }

    public sealed class ScreenshotItem
    {
        public string Path { get; set; } = "";
        public BitmapImage? Thumbnail { get; set; }
    }
}