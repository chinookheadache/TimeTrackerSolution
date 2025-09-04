// ScreenshotTracker/Core/TrackerService.cs
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScreenshotShared.Logging;
using ScreenshotShared.Messaging;
using ScreenshotShared.Settings;
using ScreenshotShared.Utilities;

namespace ScreenshotTracker.Core
{
    public sealed class TrackerService : IAsyncDisposable
    {
        private readonly PipeServer _server;
        private readonly ScreenshotCollector _collector;
        private readonly AppSettings _settings;
        private readonly ConcurrentDictionary<int, byte> _clients = new();

        private const string AppRunName = "TimeTrackerSolution";

        public TrackerService(string pipeName = "ScreenshotPipe")
        {
            _settings = AppSettings.Load();
            EnsureFolderExists(_settings.BaseFolder);

            // Apply autorun state best-effort at startup
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                AutoRun.Set(AppRunName, exe, _settings.StartWithWindows);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Applying StartWithWindows on startup");
            }

            _collector = new ScreenshotCollector(
                () => _settings.BaseFolder,
                () => _settings.IntervalSeconds,
                () => _settings.JpegQuality
            );

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
                    Logger.LogError(ex, "Broadcast ScreenshotSaved failed");
                }
            };

            _server = new PipeServer(pipeName);
            _server.ClientConnected += OnClientConnected;
            _server.ClientDisconnected += OnClientDisconnected;
            _server.MessageReceived += OnMessageReceived;
        }

        public void Start()
        {
            try
            {
                _server.Start();
                Logger.LogInfo("PipeServer started.");

                // Honor auto-start capture flag
                if (_settings.AutoStartCapture && !_collector.IsRunning)
                {
                    _collector.Start();
                    Logger.LogInfo("AutoStartCapture → started.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "PipeServer.Start failed");
                throw;
            }
        }

        public async Task RequestShutdownAsync()
        {
            try
            {
                Logger.LogInfo("RequestShutdownAsync: broadcasting TrackerExiting…");
                await BroadcastAsync(new PipeMessage { Event = "TrackerExiting" });
                await Task.Delay(200);

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
                SendSettingsSync(clientId);
                SendCaptureState(clientId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Sync to client {clientId} failed");
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
                if (string.IsNullOrWhiteSpace(msg?.Command))
                    return;

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
                            Logger.LogInfo($"Interval → {seconds}s");
                            BroadcastSettingsSync();
                        }
                        break;

                    case "SetQuality":
                        if (int.TryParse(msg.Value, out var quality) && quality is >= 1 and <= 100)
                        {
                            _settings.JpegQuality = quality;
                            _settings.Save();
                            Logger.LogInfo($"JPEG quality → {quality}%");
                            BroadcastSettingsSync();
                        }
                        break;

                    case "SetFolder":
                        if (!string.IsNullOrWhiteSpace(msg.Path))
                        {
                            _settings.BaseFolder = msg.Path!;
                            EnsureFolderExists(_settings.BaseFolder);
                            _settings.Save();
                            Logger.LogInfo($"Base folder → '{_settings.BaseFolder}'");
                            BroadcastSettingsSync();
                        }
                        break;

                    // These are for when we later add client UI; tray also calls the public methods below.
                    case "SetStartWithWindows":
                        {
                            var enable = string.Equals(msg.Value, "true", StringComparison.OrdinalIgnoreCase);
                            SetStartWithWindows(enable);
                        }
                        break;

                    case "SetAutoStartCapture":
                        {
                            var enable = string.Equals(msg.Value, "true", StringComparison.OrdinalIgnoreCase);
                            await SetAutoStartCaptureAsync(enable);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "OnMessageReceived failed");
            }
        }

        // -------------------- Helpers --------------------

        private static void EnsureFolderExists(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            try { Directory.CreateDirectory(folder!); }
            catch (Exception ex) { Logger.LogError(ex, $"Create base folder '{folder}' failed"); }
        }

        private void SendSettingsSync(int clientId)
        {
            try
            {
                var msg = PipeMessage.SettingsSync(
                    _settings.BaseFolder,
                    _settings.IntervalSeconds,
                    _settings.JpegQuality,
                    _settings.StartWithWindows,
                    _settings.AutoStartCapture);

                _ = _server.SendAsync(clientId, msg);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"SendSettingsSync → client {clientId} failed");
            }
        }

        private void BroadcastSettingsSync()
        {
            try
            {
                var msg = PipeMessage.SettingsSync(
                    _settings.BaseFolder,
                    _settings.IntervalSeconds,
                    _settings.JpegQuality,
                    _settings.StartWithWindows,
                    _settings.AutoStartCapture);

                Broadcast(msg);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "BroadcastSettingsSync failed");
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
                Logger.LogError(ex, $"SendCaptureState → client {clientId} failed");
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
                Logger.LogError(ex, "SendCaptureStateAll failed");
            }
        }

        private void Broadcast(PipeMessage msg)
        {
            foreach (var id in _clients.Keys.ToArray())
            {
                try { _ = _server.SendAsync(id, msg); }
                catch (Exception ex) { Logger.LogError(ex, $"Broadcast → client {id} failed"); }
            }
        }

        private async Task BroadcastAsync(PipeMessage msg)
        {
            foreach (var id in _clients.Keys.ToArray())
            {
                try { await _server.SendAsync(id, msg); }
                catch (Exception ex) { Logger.LogError(ex, $"BroadcastAsync → client {id} failed"); }
            }
        }

        // --------- Public API used by tray ----------

        public bool GetStartWithWindows() => _settings.StartWithWindows;
        public bool GetAutoStartCapture() => _settings.AutoStartCapture;

        public void SetStartWithWindows(bool enabled)
        {
            _settings.StartWithWindows = enabled;
            _settings.Save();
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                AutoRun.Set(AppRunName, exe, enabled);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "SetStartWithWindows: apply autorun failed");
            }
            BroadcastSettingsSync();
        }

        public async Task SetAutoStartCaptureAsync(bool enabled)
        {
            _settings.AutoStartCapture = enabled;
            _settings.Save();

            if (enabled && !_collector.IsRunning)
            {
                _collector.Start();
                await BroadcastAsync(new PipeMessage { Event = "CaptureStarted" });
                SendCaptureStateAll();
            }

            BroadcastSettingsSync();
        }
    }
}
