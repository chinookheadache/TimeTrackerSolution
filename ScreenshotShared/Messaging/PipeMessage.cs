// ScreenshotShared/Messaging/PipeMessage.cs
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotShared.Messaging
{
    public sealed class PipeMessage
    {
        [JsonPropertyName("Command")] public string? Command { get; init; }
        [JsonPropertyName("Event")]   public string? Event { get; init; }
        [JsonPropertyName("Value")]   public string? Value { get; init; }
        [JsonPropertyName("Path")]    public string? Path { get; init; }

        [JsonPropertyName("Version")]       public string Version { get; init; } = "1.0";
        [JsonPropertyName("CorrelationId")] public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
        [JsonPropertyName("TimestampUtc")]  public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public static string Serialize(PipeMessage msg) => JsonSerializer.Serialize(msg, JsonOptions);
        public static PipeMessage? Deserialize(string json) => JsonSerializer.Deserialize<PipeMessage>(json, JsonOptions);

        public static PipeMessage Cmd(string name, string? value = null, string? path = null, string? correlationId = null) =>
            new() { Command = name, Value = value, Path = path, CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"), TimestampUtc = DateTime.UtcNow };

        public static PipeMessage Ev(string name, string? value = null, string? path = null, string? correlationId = null) =>
            new() { Event = name, Value = value, Path = path, CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"), TimestampUtc = DateTime.UtcNow };
    }
}