// ScreenshotShared/Utilities/AutoRun.cs
using System;
using System.IO;
using Microsoft.Win32;
using ScreenshotShared.Logging;

namespace ScreenshotShared.Utilities
{
    /// <summary>
    /// Per-user autorun via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
    /// No elevation required.
    /// </summary>
    public static class AutoRun
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Get(string appName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                if (key is null) return false;
                var val = key.GetValue(appName) as string;
                return !string.IsNullOrWhiteSpace(val);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"AutoRun.Get failed for '{appName}'", "tracker");
                return false;
            }
        }

        public static void Set(string appName, string exePath, bool enabled)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null) return;

                if (enabled)
                {
                    var quoted = $"\"{exePath}\"";
                    key.SetValue(appName, quoted);
                }
                else
                {
                    key.DeleteValue(appName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"AutoRun.Set failed for '{appName}' (enabled={enabled})", "tracker");
            }
        }
    }
}
