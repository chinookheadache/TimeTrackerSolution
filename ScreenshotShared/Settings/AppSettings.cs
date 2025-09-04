// ScreenshotShared/Settings/AppSettings.cs
using System;
using System.IO;
using System.Text.Json;
using ScreenshotShared.Logging;

namespace ScreenshotShared.Settings
{
    public sealed class AppSettings
    {
        public string BaseFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Screenshots");

        public int IntervalSeconds { get; set; } = 30;
        public int JpegQuality { get; set; } = 80;

        // NEW flags (Step 7)
        public bool StartWithWindows { get; set; } = false;
        public bool AutoStartCapture { get; set; } = false;

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true
        };

        private static string SettingsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TimeTrackerSolution");

        private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json, _json);
                    if (s is not null) return s;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "AppSettings.Load failed");
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var tmp = SettingsPath + ".tmp";
                var json = JsonSerializer.Serialize(this, _json);
                File.WriteAllText(tmp, json);
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                File.Move(tmp, SettingsPath);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "AppSettings.Save failed");
            }
        }
    }
}
