// ScreenshotShared/Messaging/PipeMessage.cs
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotShared.Messaging
{
    public sealed class PipeMessage
    {
        public string? Command { get; set; }
        public string? Event { get; set; }

        public string? Value { get; set; }
        public string? Path { get; set; }

        // NEW: explicit flags included in SettingsSync (Client can ignore for now)
        public bool? StartWithWindows { get; set; }
        public bool? AutoStartCapture { get; set; }

        public string Version { get; set; } = "1.0";
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public static string Serialize(PipeMessage msg) => JsonSerializer.Serialize(msg, _json);
        public static PipeMessage? Deserialize(string json)
        {
            try { return JsonSerializer.Deserialize<PipeMessage>(json, _json); }
            catch { return null; }
        }

        // Helpers
        public static PipeMessage Cmd(string command, string? value = null, string? path = null) =>
            new PipeMessage { Command = command, Value = value, Path = path };

        public static PipeMessage SettingsSync(string baseFolder, int interval, int quality, bool startWithWindows, bool autoStartCapture)
            => new PipeMessage
            {
                Event = "SettingsSync",
                Path = baseFolder,
                Value = $"{interval};{quality}",
                StartWithWindows = startWithWindows,
                AutoStartCapture = autoStartCapture
            };
    }
}
