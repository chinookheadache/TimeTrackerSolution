using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ScreenshotShared.Logging;

namespace ScreenshotTracker.Core
{
    /// <summary>
    /// Periodically captures all screens and saves JPEGs under {BaseFolder}\yyyy-MM-dd\HHmmss-fff-N.jpg
    /// Constructor takes delegates so settings can change at runtime:
    ///   getFolder(): base folder string
    ///   getIntervalSeconds(): capture interval in seconds
    ///   getJpegQuality(): 1..100
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ScreenshotCollector
    {
        private readonly Func<string> _getFolder;
        private readonly Func<int> _getIntervalSeconds;
        private readonly Func<int> _getJpegQuality;

        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;

        public event EventHandler<string>? ScreenshotSaved;

        public bool IsRunning => _loopTask is not null && !_loopTask.IsCompleted;

        public ScreenshotCollector(Func<string> getFolder, Func<int> getIntervalSeconds, Func<int> getJpegQuality)
        {
            _getFolder = getFolder ?? throw new ArgumentNullException(nameof(getFolder));
            _getIntervalSeconds = getIntervalSeconds ?? throw new ArgumentNullException(nameof(getIntervalSeconds));
            _getJpegQuality = getJpegQuality ?? throw new ArgumentNullException(nameof(getJpegQuality));
        }

        public void Start()
        {
            if (IsRunning) return;
            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
        }

        public void Stop()
        {
            try { _loopCts?.Cancel(); } catch { }
            try { _loopTask?.Wait(250); } catch { }
            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            Logger.LogInfo("ScreenshotCollector loop started.");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var interval = Math.Max(1, _getIntervalSeconds());
                    try
                    {
                        var folder = _getFolder();
                        if (!string.IsNullOrWhiteSpace(folder))
                        {
                            var savedPaths = CaptureAll(folder, _getJpegQuality());
                            foreach (var path in savedPaths)
                            {
                                try { ScreenshotSaved?.Invoke(this, path); }
                                catch (Exception ex) { Logger.LogError(ex, "ScreenshotSaved handler threw"); }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // normal on shutdown
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error during capture loop iteration");
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Collector loop crashed");
            }
            finally
            {
                Logger.LogInfo("ScreenshotCollector loop stopped.");
            }
        }

        private static IEnumerable<string> CaptureAll(string baseFolder, int jpegQuality)
        {
            var results = new List<string>();

            try
            {
                var dayFolder = Path.Combine(baseFolder, DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dayFolder);

                // Enumerate all screens
                var screens = System.Windows.Forms.Screen.AllScreens;
                var stamp = DateTime.Now.ToString("HHmmss-fff");

                // Set JPEG encoder
                if (jpegQuality < 1) jpegQuality = 1;
                if (jpegQuality > 100) jpegQuality = 100;
                var encoder = GetJpegEncoder();
                var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(Encoder.Quality, jpegQuality);

                for (int i = 0; i < screens.Length; i++)
                {
                    var s = screens[i];
                    using var bmp = new Bitmap(s.Bounds.Width, s.Bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using var g = Graphics.FromImage(bmp);
                    g.CopyFromScreen(s.Bounds.Left, s.Bounds.Top, 0, 0, s.Bounds.Size);

                    var file = Path.Combine(dayFolder, $"{stamp}-{i + 1}.jpg");

                    // Save atomically: write to temp then move
                    var tmp = file + ".tmp";
                    bmp.Save(tmp, encoder, encParams);
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);

                    results.Add(file);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "CaptureAll failed");
            }

            return results;
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var c in codecs)
            {
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            }
            // Fallback (shouldn't happen)
            return codecs.Length > 0 ? codecs[0] : throw new InvalidOperationException("No image encoders found.");
        }
    }
}
