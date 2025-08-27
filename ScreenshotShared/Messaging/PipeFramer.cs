// ScreenshotShared/Messaging/PipeFramer.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenshotShared.Messaging
{
    public static class PipeFramer
    {
        public static async Task WriteAsync(Stream stream, string json, CancellationToken ct)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(json);
            var len = BitConverter.GetBytes(payload.Length);
            await stream.WriteAsync(len.AsMemory(0, 4), ct);
            await stream.WriteAsync(payload.AsMemory(0, payload.Length), ct);
            await stream.FlushAsync(ct);
        }

        public static async Task<string?> ReadAsync(Stream stream, CancellationToken ct)
        {
            var lenBuf = new byte[4];
            int read = await ReadExactAsync(stream, lenBuf, 0, 4, ct);
            if (read == 0) return null;
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0) return null;

            var buf = new byte[len];
            await ReadExactAsync(stream, buf, 0, len, ct);
            return System.Text.Encoding.UTF8.GetString(buf);
        }

        private static async Task<int> ReadExactAsync(Stream s, byte[] b, int off, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await s.ReadAsync(b.AsMemory(off + total, count - total), ct);
                if (n == 0) return total;
                total += n;
            }
            return total;
        }
    }
}