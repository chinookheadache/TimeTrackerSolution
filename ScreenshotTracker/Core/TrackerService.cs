using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScreenshotShared.Messaging;
using ScreenshotShared.Settings;

namespace ScreenshotTracker.Core
{
    public sealed class TrackerService : IAsyncDisposable
    {
        private readonly PipeServer _server;
        private readonly ScreenshotCollector _collector;

        // Single source of truth for settings
        private readonly AppSettings _settings;

        // Track connected client ids so we can broadcast
        private readonly ConcurrentDictionary<int, byte> _clients = new();

        public TrackerService(string pipeName = "ScreenshotPipe")
        {
            // Load persisted settings once at startup
            _settings = AppSettings.Load();
            EnsureFolderExists(_settings.BaseFolder);

            // ScreenshotCollector(Func<string>, Func<int>, Func<int>) — pass delegates POSITIONALLY
            _collector = new ScreenshotCollector(
                () => _settings.BaseFolder,
                () => _settings.IntervalSeconds,
                () => _settings.JpegQuality
            );

            // EventHandler<string> → (sender, path)
            _collector.ScreenshotSaved += (sender, path) =>
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Broadcast(new PipeMessage { Event = "ScreenshotSaved", Path = path });
                }
            };

            _server = new PipeServer(pipeName);
            _server.ClientConnected    += OnClientConnected;      // Action<int>
            _server.ClientDisconnected += OnClientDisconnected;   // Action<int>
            _server.MessageReceived    += OnMessageReceived;      // Action<int, PipeMessage>
        }

        public void Start()
        {
            // Your PipeServer.Start() takes no parameters
            _server.Start();
        }

        /// <summary>
        /// Called by App on tray "Exit": tells clients to shutdown, stops capture, then we can dispose.
        /// </summary>
        public async Task RequestShutdownAsync()
        {
            // Ask clients to exit gracefully
            await BroadcastAsync(new PipeMessage { Event = "TrackerExiting" });

            // Give clients a brief moment to process
            await Task.Delay(200);

            // Stop capture if running
            if (_collector.IsRunning)
                _collector.Stop();

            // (Server Dispose happens in App.OnExit via DisposeAsync)
        }

        public async ValueTask DisposeAsync()
        {
            // Collector has no Dispose() in your API
            await _server.DisposeAsync();
        }

        // -------------------- Pipe events --------------------

        private void OnClientConnected(int clientId)
        {
            _clients[clientId] = 0;

            // Proactively send current settings and capture state
            SendSettingsSync(clientId);
            SendCaptureState(clientId);
        }

        private void OnClientDisconnected(int clientId)
        {
            _clients.TryRemove(clientId, out _);
        }

        private async void OnMessageReceived(int clientId, PipeMessage msg)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(msg.Command))
                {
                    switch (msg.Command)
                    {
                        case "QueryState":
                            SendSettingsSync(clientId);
                            SendCaptureState(clientId);
                            break;

                        case "StartCapture":
                            if (!_collector.IsRunning)
                            {
                                _collector.Start();
                                await BroadcastAsync(new PipeMessage { Event = "CaptureStarted" });
                                SendCaptureStateAll();
                            }
                            break;

                        case "StopCapture":
                            if (_collector.IsRunning)
                            {
                                _collector.Stop();
                                await BroadcastAsync(new PipeMessage { Event = "CaptureStopped" });
                                SendCaptureStateAll();
                            }
                            break;

                        case "SetInterval":
                            if (int.TryParse(msg.Value, out var seconds) && seconds > 0)
                            {
                                _settings.IntervalSeconds = seconds;
                                _settings.Save();
                                // Collector reads new value via delegate
                                BroadcastSettingsSync();
                            }
                            break;

                        case "SetQuality":
                            if (int.TryParse(msg.Value, out var quality) && quality is >= 1 and <= 100)
                            {
                                _settings.JpegQuality = quality;
                                _settings.Save();
                                // Collector reads new value via delegate
                                BroadcastSettingsSync();
                            }
                            break;

                        case "SetFolder":
                            if (!string.IsNullOrWhiteSpace(msg.Path))
                            {
                                _settings.BaseFolder = msg.Path!;
                                EnsureFolderExists(_settings.BaseFolder);
                                _settings.Save();
                                // Collector reads new folder via delegate
                                BroadcastSettingsSync();
                            }
                            break;
                    }
                }
            }
            catch
            {
                // TODO: add file logging later
            }
        }

        // -------------------- Helpers: Settings & State sync --------------------

        private static void EnsureFolderExists(string? folder)
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                try { Directory.CreateDirectory(folder!); } catch { /* ignore for now */ }
            }
        }

        private void SendSettingsSync(int clientId)
        {
            var payload = $"{_settings.IntervalSeconds};{_settings.JpegQuality}";
            var msg = new PipeMessage
            {
                Event = "SettingsSync",
                Value = payload,
                Path  = _settings.BaseFolder
            };
            _ = _server.SendAsync(clientId, msg);
        }

        private void BroadcastSettingsSync()
        {
            var payload = $"{_settings.IntervalSeconds};{_settings.JpegQuality}";
            Broadcast(new PipeMessage
            {
                Event = "SettingsSync",
                Value = payload,
                Path  = _settings.BaseFolder
            });
        }

        private void SendCaptureState(int clientId)
        {
            var state = _collector.IsRunning ? "Running" : "Stopped";
            _ = _server.SendAsync(clientId, new PipeMessage { Event = "CaptureState", Value = state });
        }

        private void SendCaptureStateAll()
        {
            var state = _collector.IsRunning ? "Running" : "Stopped";
            Broadcast(new PipeMessage { Event = "CaptureState", Value = state });
        }

        private void Broadcast(PipeMessage msg)
        {
            foreach (var id in _clients.Keys.ToArray())
            {
                _ = _server.SendAsync(id, msg);
            }
        }

        private async Task BroadcastAsync(PipeMessage msg)
        {
            foreach (var id in _clients.Keys.ToArray())
            {
                await _server.SendAsync(id, msg);
            }
        }
    }
}
