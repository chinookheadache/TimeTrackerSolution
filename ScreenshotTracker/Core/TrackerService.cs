using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScreenshotShared.Logging;
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
            try
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
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            Logger.LogInfo($"Screenshot saved: {path}");
                            Broadcast(new PipeMessage { Event = "ScreenshotSaved", Path = path });
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to broadcast ScreenshotSaved");
                    }
                };

                _server = new PipeServer(pipeName);
                _server.ClientConnected += OnClientConnected;         // Action<int>
                _server.ClientDisconnected += OnClientDisconnected;   // Action<int>
                _server.MessageReceived += OnMessageReceived;         // Action<int, PipeMessage>
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "TrackerService ctor failed");
                throw;
            }
        }

        public void Start()
        {
            try
            {
                _server.Start();
                Logger.LogInfo("PipeServer started.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "PipeServer.Start failed");
                throw;
            }
        }

        /// <summary>
        /// Called by App on tray "Exit": tells clients to shutdown, stops capture, then we can dispose.
        /// </summary>
        public async Task RequestShutdownAsync()
        {
            try
            {
                Logger.LogInfo("RequestShutdownAsync: broadcasting TrackerExiting…");
                await BroadcastAsync(new PipeMessage { Event = "TrackerExiting" });

                // Give clients a brief moment to process
                await Task.Delay(200);

                // Stop capture if running
                if (_collector.IsRunning)
                {
                    Logger.LogInfo("Stopping capture on shutdown.");
                    _collector.Stop();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error during RequestShutdownAsync");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Logger.LogInfo("TrackerService.DisposeAsync");
                await _server.DisposeAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error disposing PipeServer");
            }
        }

        // -------------------- Pipe events --------------------

        private void OnClientConnected(int clientId)
        {
            _clients[clientId] = 0;
            Logger.LogInfo($"Client connected: {clientId}");

            try
            {
                // Proactively send current settings and capture state
                SendSettingsSync(clientId);
                SendCaptureState(clientId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error syncing state to client {clientId}");
            }
        }

        private void OnClientDisconnected(int clientId)
        {
            _clients.TryRemove(clientId, out _);
            Logger.LogInfo($"Client disconnected: {clientId}");
        }

        private async void OnMessageReceived(int clientId, PipeMessage msg)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(msg.Command))
                {
                    Logger.LogInfo($"Command from {clientId}: {msg.Command} (Value='{msg.Value}', Path='{msg.Path}')");

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
                                Logger.LogInfo("Capture started.");
                            }
                            break;

                        case "StopCapture":
                            if (_collector.IsRunning)
                            {
                                _collector.Stop();
                                await BroadcastAsync(new PipeMessage { Event = "CaptureStopped" });
                                SendCaptureStateAll();
                                Logger.LogInfo("Capture stopped.");
                            }
                            break;

                        case "SetInterval":
                            if (int.TryParse(msg.Value, out var seconds) && seconds > 0)
                            {
                                _settings.IntervalSeconds = seconds;
                                _settings.Save();
                                Logger.LogInfo($"Interval updated to {seconds}s");
                                BroadcastSettingsSync();
                            }
                            break;

                        case "SetQuality":
                            if (int.TryParse(msg.Value, out var quality) && quality is >= 1 and <= 100)
                            {
                                _settings.JpegQuality = quality;
                                _settings.Save();
                                Logger.LogInfo($"JPEG quality updated to {quality}%");
                                BroadcastSettingsSync();
                            }
                            break;

                        case "SetFolder":
                            if (!string.IsNullOrWhiteSpace(msg.Path))
                            {
                                _settings.BaseFolder = msg.Path!;
                                EnsureFolderExists(_settings.BaseFolder);
                                _settings.Save();
                                Logger.LogInfo($"Base folder changed to '{_settings.BaseFolder}'");
                                BroadcastSettingsSync();
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in OnMessageReceived");
            }
        }

        // -------------------- Helpers: Settings & State sync --------------------

        private static void EnsureFolderExists(string? folder)
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                try
                {
                    Directory.CreateDirectory(folder!);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Failed to create base folder '{folder}'");
                }
            }
        }

        private void SendSettingsSync(int clientId)
        {
            try
            {
                var payload = $"{_settings.IntervalSeconds};{_settings.JpegQuality}";
                var msg = new PipeMessage
                {
                    Event = "SettingsSync",
                    Value = payload,
                    Path = _settings.BaseFolder
                };
                _ = _server.SendAsync(clientId, msg);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to send SettingsSync to client {clientId}");
            }
        }

        private void BroadcastSettingsSync()
        {
            try
            {
                var payload = $"{_settings.IntervalSeconds};{_settings.JpegQuality}";
                Broadcast(new PipeMessage
                {
                    Event = "SettingsSync",
                    Value = payload,
                    Path = _settings.BaseFolder
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to broadcast SettingsSync");
            }
        }

        private void SendCaptureState(int clientId)
        {
            try
            {
                var state = _collector.IsRunning ? "Running" : "Stopped";
                _ = _server.SendAsync(clientId, new PipeMessage { Event = "CaptureState", Value = state });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to send CaptureState to client {clientId}");
            }
        }

        private void SendCaptureStateAll()
        {
            try
            {
                var state = _collector.IsRunning ? "Running" : "Stopped";
                Broadcast(new PipeMessage { Event = "CaptureState", Value = state });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to broadcast CaptureState");
            }
        }

        private void Broadcast(PipeMessage msg)
        {
            foreach (var id in _clients.Keys.ToArray())
            {
                try
                {
                    _ = _server.SendAsync(id, msg);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Broadcast send failed to client {id}");
                }
            }
        }

        private async Task BroadcastAsync(PipeMessage msg)
        {
            foreach (var id in _clients.Keys.ToArray())
            {
                try
                {
                    await _server.SendAsync(id, msg);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"BroadcastAsync send failed to client {id}");
                }
            }
        }
    }
}
