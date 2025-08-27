// ScreenshotTracker/Core/TrackerService.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScreenshotShared.Messaging;
using ScreenshotShared.Settings;

namespace ScreenshotTracker.Core
{
    public sealed class TrackerService : IAsyncDisposable
    {
        private readonly PipeServer _server;
        private readonly ScreenshotCollector _collector;
        private readonly CancellationTokenSource _cts = new();
        private readonly AppSettings _settings;

        public TrackerService()
        {
            _settings = AppSettings.Load();
            Directory.CreateDirectory(_settings.BaseFolder);

            _server = new PipeServer("ScreenshotPipe");
            _server.MessageReceived += OnMessageReceived;
            _server.Start();

            _collector = new ScreenshotCollector(
                () => _settings.BaseFolder,
                () => _settings.IntervalSeconds,
                () => _settings.JpegQuality);

            _collector.ScreenshotSaved += async (_, path) =>
            {
                try { await _server.BroadcastAsync(PipeMessage.Ev("ScreenshotSaved", path: path), _cts.Token); }
                catch { }
            };
        }

        private async void OnMessageReceived(int clientId, PipeMessage msg)
        {
            try
            {
                switch (msg.Command)
                {
                    case "StartCapture":
                        _collector.Start();
                        await _server.BroadcastAsync(PipeMessage.Ev("CaptureStarted", correlationId: msg.CorrelationId), _cts.Token);
                        break;

                    case "StopCapture":
                        _collector.Stop();
                        await _server.BroadcastAsync(PipeMessage.Ev("CaptureStopped", correlationId: msg.CorrelationId), _cts.Token);
                        break;

                    case "SetInterval":
                        if (int.TryParse(msg.Value, out var seconds) && seconds > 0)
                        {
                            _settings.IntervalSeconds = seconds; _settings.Save();
                            await BroadcastSettingsSync();
                        }
                        break;

                    case "SetQuality":
                        if (int.TryParse(msg.Value, out var quality) && quality is >= 1 and <= 100)
                        {
                            _settings.JpegQuality = quality; _settings.Save();
                            await BroadcastSettingsSync();
                        }
                        break;

                    case "SetFolder":
                        if (!string.IsNullOrWhiteSpace(msg.Path))
                        {
                            _settings.BaseFolder = msg.Path!;
                            Directory.CreateDirectory(_settings.BaseFolder);
                            _settings.Save();
                            await BroadcastSettingsSync();
                        }
                        break;

                    case "Shutdown":
                        await _server.BroadcastAsync(PipeMessage.Ev("TrackerExiting"), _cts.Token);
                        await DisposeAsync();
                        Environment.Exit(0);
                        break;
                }
            }
            catch { }
        }

        private Task BroadcastSettingsSync() =>
            _server.BroadcastAsync(new PipeMessage
            {
                Event = "SettingsSync",
                Value = $"{_settings.IntervalSeconds};{_settings.JpegQuality}",
                Path = _settings.BaseFolder
            }, _cts.Token);

        public async ValueTask DisposeAsync()
        {
            try
            {
                _cts.Cancel();
                _collector.Stop();
                await _server.DisposeAsync();
            }
            catch { }
        }
    }
}