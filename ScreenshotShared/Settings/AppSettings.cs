// ScreenshotShared/Settings/AppSettings.cs
using System;
using System.IO;
using System.Text.Json;

namespace ScreenshotShared.Settings
{
    public sealed class AppSettings
    {
        public string BaseFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TimeTrackerSolution", "Screenshots");

        public int IntervalSeconds { get; set; } = 30;
        public int JpegQuality { get; set; } = 80;

        public static string SettingsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TimeTrackerSolution");
        public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new();
                }
            }
            catch { }
            return new AppSettings();
        }
        private static readonly JsonSerializerOptions s_optsIndented = new() { WriteIndented = true };
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}