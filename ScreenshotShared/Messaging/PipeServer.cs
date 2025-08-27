// ScreenshotShared/Messaging/PipeServer.cs
using System;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenshotShared.Messaging
{
    public sealed class PipeServer : IAsyncDisposable
    {
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptLoop;
        private int _nextId = 0;
        private readonly ConcurrentDictionary<int, ClientState> _clients = new();

        private sealed class ClientState : IAsyncDisposable
        {
            public int Id { get; init; }
            public NamedPipeServerStream Pipe { get; init; } = default!;
            public Task? RecvLoop { get; set; }
            public CancellationTokenSource Cts { get; init; } = new();

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (!Cts.IsCancellationRequested) Cts.Cancel();
                    if (RecvLoop != null) await Task.WhenAny(RecvLoop, Task.Delay(250));
                }
                catch { }
                finally
                {
                    Pipe.Dispose();
                    Cts.Dispose();
                }
            }
        }

        public event Action<int>? ClientConnected;
        public event Action<int>? ClientDisconnected;
        public event Action<int, PipeMessage>? MessageReceived;
        public event Action<Exception>? Faulted;

        public PipeServer(string pipeName) => _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));

        public void Start() => _acceptLoop ??= Task.Run(AcceptLoopAsync);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                    var id = Interlocked.Increment(ref _nextId);
                    var state = new ClientState { Id = id, Pipe = server };
                    if (!_clients.TryAdd(id, state))
                    {
                        await server.DisposeAsync();
                        continue;
                    }

                    ClientConnected?.Invoke(id);
                    state.RecvLoop = Task.Run(() => ReceiveLoopAsync(state));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Faulted?.Invoke(ex); }
        }

        private async Task ReceiveLoopAsync(ClientState state)
        {
            try
            {
                while (!state.Cts.IsCancellationRequested && state.Pipe.IsConnected)
                {
                    var json = await PipeFramer.ReadAsync(state.Pipe, state.Cts.Token).ConfigureAwait(false);
                    if (json == null) break;
                    var msg = PipeMessage.Deserialize(json);
                    if (msg != null) MessageReceived?.Invoke(state.Id, msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Faulted?.Invoke(ex); }
            finally
            {
                await RemoveClientAsync(state.Id);
            }
        }

        private async Task RemoveClientAsync(int id)
        {
            if (_clients.TryRemove(id, out var st))
            {
                await st.DisposeAsync();
                ClientDisconnected?.Invoke(id);
            }
        }

        public async Task BroadcastAsync(PipeMessage message, CancellationToken ct = default)
        {
            var json = PipeMessage.Serialize(message);
            foreach (var kv in _clients)
            {
                var st = kv.Value;
                if (!st.Pipe.IsConnected) continue;
                try { await PipeFramer.WriteAsync(st.Pipe, json, ct).ConfigureAwait(false); }
                catch { }
            }
        }

        public async Task SendAsync(int clientId, PipeMessage message, CancellationToken ct = default)
        {
            if (_clients.TryGetValue(clientId, out var st) && st.Pipe.IsConnected)
                await PipeFramer.WriteAsync(st.Pipe, PipeMessage.Serialize(message), ct).ConfigureAwait(false);
            else
                throw new InvalidOperationException($"Client {clientId} not connected.");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _cts.Cancel();
                if (_acceptLoop != null) await Task.WhenAny(_acceptLoop, Task.Delay(250));
            }
            catch { }
            finally
            {
                _acceptLoop = null;
                foreach (var kv in _clients) await kv.Value.DisposeAsync();
                _clients.Clear();
                _cts.Dispose();
            }
        }
    }
}