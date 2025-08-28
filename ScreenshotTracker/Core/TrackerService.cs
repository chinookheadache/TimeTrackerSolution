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
            _server.ClientConnected += async id =>
            {
                // As soon as a client connects, push current settings & running state to THAT client
                try { await SendStateAsync(id); } catch { /* optional log */ }
            };
            _server.Start();

            _collector = new ScreenshotCollector(
                () => _settings.BaseFolder,
                () => _settings.IntervalSeconds,
                () => _settings.JpegQuality);

            _collector.ScreenshotSaved += async (_, path) =>
            {
                try { await _server.BroadcastAsync(PipeMessage.Ev("ScreenshotSaved", path: path), _cts.Token); }
                catch { /* optional log */ }
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

                    case "QueryState":
                        // Client explicitly asked for current settings + running state
                        await SendStateAsync(clientId);
                        break;

                    case "Shutdown":
                        await _server.BroadcastAsync(PipeMessage.Ev("TrackerExiting"), _cts.Token);
                        await DisposeAsync();
                        Environment.Exit(0);
                        break;
                }
            }
            catch
            {
                // optional logging
            }
        }

        private Task BroadcastSettingsSync() =>
            _server.BroadcastAsync(new PipeMessage
            {
                Event = "SettingsSync",
                Value = $"{_settings.IntervalSeconds};{_settings.JpegQuality}",
                Path = _settings.BaseFolder
            }, _cts.Token);

        /// <summary>
        /// Send SettingsSync + CaptureState either to a specific client (if id provided) or broadcast to all.
        /// </summary>
        private async Task SendStateAsync(int? clientId = null)
        {
            var settings = new PipeMessage
            {
                Event = "SettingsSync",
                Value = $"{_settings.IntervalSeconds};{_settings.JpegQuality}",
                Path = _settings.BaseFolder
            };
            var state = PipeMessage.Ev("CaptureState", value: _collector.IsRunning ? "Running" : "Stopped");

            if (clientId is int id)
            {
                await _server.SendAsync(id, settings, _cts.Token);
                await _server.SendAsync(id, state, _cts.Token);
            }
            else
            {
                await _server.BroadcastAsync(settings, _cts.Token);
                await _server.BroadcastAsync(state, _cts.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _cts.Cancel();
                _collector.Stop();
                await _server.DisposeAsync();
            }
            catch
            {
                // ignore
            }
        }
    }
}
