using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LanLink;

/// <summary>
/// Wraps a single TCP connection to a remote node.
/// Provides framed async send (serialised with a semaphore) and a
/// background read loop that fires <see cref="MessageReceived"/>.
/// </summary>
public sealed class PeerConnection : IDisposable
{
    private readonly TcpClient      _client;
    private readonly NetworkStream  _stream;
    private readonly SemaphoreSlim  _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public string? RemoteNodeId   { get; set; }
    public string? RemoteName     { get; set; }
    public string  RemoteEndpoint { get; }
    public bool    IsOutgoing     { get; }

    /// <summary>(connection, header, binaryPayload)</summary>
    public event Action<PeerConnection, WireMessage, byte[]>? MessageReceived;
    public event Action<PeerConnection>? Disconnected;

    public PeerConnection(TcpClient client, bool isOutgoing)
    {
        _client  = client;
        _stream  = client.GetStream();
        IsOutgoing = isOutgoing;
        RemoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    /// <summary>Start the background read loop.</summary>
    public void StartReading() => _ = ReadLoopAsync();

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadFrameAsync(_stream, _cts.Token)
                                            .ConfigureAwait(false);
                if (frame is null) break;   // clean disconnect

                var (msg, payload) = frame.Value;
                try { MessageReceived?.Invoke(this, msg, payload); }
                catch { /* handler fault — don't kill the read loop */ }
            }
        }
        catch { /* socket error or cancellation */ }
        finally
        {
            FireDisconnected();
        }
    }

    /// <summary>Send a framed message (+ optional binary payload).</summary>
    public async Task SendAsync(WireMessage message, byte[]? payload = null)
    {
        if (_disposed != 0) return;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteFrameAsync(_stream, message, payload, _cts.Token)
                            .ConfigureAwait(false);
        }
        catch
        {
            FireDisconnected();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FireDisconnected()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            try { Disconnected?.Invoke(this); } catch { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel();   } catch { }
        try { _client.Close(); } catch { }
    }
}
