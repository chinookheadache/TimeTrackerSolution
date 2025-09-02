using System;
using System.IO;

namespace ScreenshotShared.Logging
{
    public static class Logger
    {
        private static readonly object _lock = new();

        private static string BaseDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TimeTrackerSolution", "logs");

        private static string LogFile(string name) => Path.Combine(BaseDir, $"{name}.log");

        public static void LogError(Exception ex, string message, string logName = "tracker")
        {
            try
            {
                Directory.CreateDirectory(BaseDir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERROR] {message}\n{ex}\n";
                lock (_lock) File.AppendAllText(LogFile(logName), line);
            }
            catch { /* last resort: do nothing */ }
        }

        public static void LogInfo(string message, string logName = "tracker")
        {
            try
            {
                Directory.CreateDirectory(BaseDir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [INFO] {message}\n";
                lock (_lock) File.AppendAllText(LogFile(logName), line);
            }
            catch { }
        }
    }
}
