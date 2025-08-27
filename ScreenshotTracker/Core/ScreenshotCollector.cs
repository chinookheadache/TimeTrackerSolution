// ScreenshotTracker/Core/ScreenshotCollector.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenshotTracker.Core
{
    public sealed class ScreenshotCollector
    {
        private readonly Func<string> _getFolder;
        private readonly Func<int> _getIntervalSeconds;
        private readonly Func<int> _getJpegQuality;

        private CancellationTokenSource? _cts;
        public event EventHandler<string>? ScreenshotSaved;

        public bool IsRunning => _cts is { IsCancellationRequested: false };

        public ScreenshotCollector(Func<string> getFolder, Func<int> getIntervalSeconds, Func<int> getJpegQuality)
        {
            _getFolder = getFolder;
            _getIntervalSeconds = getIntervalSeconds;
            _getJpegQuality = getJpegQuality;
        }

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { CaptureAll(ct); } catch { }
                var delay = Math.Max(1, _getIntervalSeconds());
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { }
            }
        }

        private void CaptureAll(CancellationToken ct)
        {
            foreach (var screen in Screen.AllScreens)
            {
                ct.ThrowIfCancellationRequested();
                var bounds = screen.Bounds;
                using var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);

                var day = DateTime.Now.ToString("yyyy-MM-dd");
                var folder = Path.Combine(_getFolder(), day);
                Directory.CreateDirectory(folder);

                var file = Path.Combine(folder, $"{DateTime.Now:HH-mm-ss-fff}_{(screen.Primary ? "P" : "S")}.jpg");
                SaveJpeg(bmp, file, _getJpegQuality());
                ScreenshotSaved?.Invoke(this, file);
            }
        }

        private static void SaveJpeg(Bitmap bmp, string file, int quality)
        {
            var enc = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bmp.Save(file, enc, ep);
        }
    }
}