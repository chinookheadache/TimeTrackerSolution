// ScreenshotShared/Messaging/PipeClient.cs
using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenshotShared.Messaging
{
    public sealed class PipeClient : IAsyncDisposable
    {
        private readonly string _pipeName;
        private NamedPipeClientStream? _pipe;
        private CancellationTokenSource? _cts;
        private Task? _recvLoop;

        public event Action<PipeMessage>? MessageReceived;
        public event Action<Exception>? ReceiveFaulted;
        public bool IsConnected => _pipe?.IsConnected == true;

        public PipeClient(string pipeName) => _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));

        public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            await DisposeAsync();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var t = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(t.Token, _cts.Token);
            await _pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            _pipe.ReadMode = PipeTransmissionMode.Byte;

            _recvLoop = Task.Run(() => ReceiveLoopAsync(_pipe, _cts.Token));
        }

        private async Task ReceiveLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    var json = await PipeFramer.ReadAsync(pipe, ct).ConfigureAwait(false);
                    if (json == null) break;
                    var msg = PipeMessage.Deserialize(json);
                    if (msg != null) MessageReceived?.Invoke(msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ReceiveFaulted?.Invoke(ex); }
        }

        public async Task SendAsync(PipeMessage message, CancellationToken cancellationToken = default)
        {
            if (_pipe is null || !_pipe.IsConnected) throw new InvalidOperationException("PipeClient not connected.");
            await PipeFramer.WriteAsync(_pipe, PipeMessage.Serialize(message), cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _cts?.Cancel();
                if (_recvLoop != null) await Task.WhenAny(_recvLoop, Task.Delay(250));
            }
            catch { }
            finally
            {
                _recvLoop = null;
                _cts?.Dispose(); _cts = null;
                _pipe?.Dispose(); _pipe = null;
            }
        }
    }
}