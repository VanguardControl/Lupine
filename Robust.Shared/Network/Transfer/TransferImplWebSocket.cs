using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Robust.Shared.Log;
using Robust.Shared.Utility;

namespace Robust.Shared.Network.Transfer;

#pragma warning disable RA0004 // Task.Result

internal abstract class TransferImplWebSocket : BaseTransferImpl
{
    internal const string KeyHeaderName = "RT-Key";
    internal const string UserIdHeaderName = "RT-UserId";

    internal const int RandomKeyBytes = 32;

    private readonly byte[] _headerBuffer = ArrayPool<byte>.Shared.Rent(MaxHeaderSize);

    private readonly CancellationTokenSource _readCancel = new();

    public WebSocket? WebSocket;

    // Set once the socket has died. The game connection carries on without large transfers.
    private volatile bool _socketLost;

    protected TransferImplWebSocket(ISawmill sawmill, BaseTransferManager parent, INetChannel channel)
        : base(sawmill, parent, channel)
    {
    }

    protected override bool BoundedChannel => true;

    public override Stream StartTransfer(TransferStartInfo startInfo)
    {
        if (WebSocket == null)
            throw new InvalidOperationException("Player not connected yet");

        if (_socketLost)
            throw new InvalidOperationException("Player's transfer socket was lost");

        var id = Interlocked.Increment(ref OutgoingIdCounter);

        return new SendStream(this, id, startInfo.MessageKey);
    }

    protected async void ReadThread()
    {
        DebugTools.Assert(WebSocket != null);

        try
        {
            var cancel = _readCancel.Token;
            while (!cancel.IsCancellationRequested)
            {
                var receiveResult = await WebSocket
                    .ReceiveAsync(_headerBuffer.AsMemory(), cancel)
                    .ConfigureAwait(false);

                BaseTransferManager.ReceivedDataMetrics.Inc(receiveResult.Count);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    HandleSocketLost("closed by the other side");
                    CompleteReceivingChannels(new IOException("Transfer socket was lost"));
                    return;
                }

                if (!receiveResult.EndOfMessage)
                    throw new ProtocolViolationException("Header did not fit in one receive");

                if (receiveResult.MessageType != WebSocketMessageType.Binary)
                    throw new ProtocolViolationException("Data must be binary!");

                // Parse received data.
                var receivedData = _headerBuffer.AsMemory(0, receiveResult.Count);
                HandleHeaderReceived(receivedData, out var flags, out var transferId, out var channel);

                if ((flags & TransferFlags.HasData) != 0)
                    await ReceiveTransferData(WebSocket, channel, cancel).ConfigureAwait(false);

                HandlePostData(flags, transferId, channel);
            }
        }
        catch (Exception) when (_readCancel.IsCancellationRequested)
        {
            // We're being disposed, so the read failing is expected.
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            HandleSocketLost(e.Message);
            CompleteReceivingChannels(new IOException("Transfer socket was lost"));
        }
        catch (Exception e)
        {
            try
            {
                Sawmill.Error($"Error reading transfer socket: {e}");
            }
            catch (ObjectDisposedException)
            {
                // Logging has shut down. This runs in an async void, where a throw would kill the process.
            }

            Channel.Disconnect("Error in transfer socket");
        }
    }

    private void HandleSocketLost(string reason)
    {
        _socketLost = true;

        try
        {
            Sawmill.Warning($"Lost transfer socket for {Channel}: {reason}");
            Parent.TransferSocketLost(Channel);
        }
        catch (ObjectDisposedException)
        {
            // Logging has shut down.
        }
    }

    private sealed class SendStream : ChunkedSendStream
    {
        public SendStream(TransferImplWebSocket parent, long id, string key) : base(parent, id, key)
        {
        }

        protected override async ValueTask SendChunkAsync(ArraySegment<byte> buffer, CancellationToken cancel)
        {
            var parent = (TransferImplWebSocket)Parent;
            if (parent._socketLost)
                throw new IOException("Transfer socket was lost");

            var ws = parent.WebSocket!;

            BaseTransferManager.SentDataMetrics.Inc(buffer.Count);

            try
            {
                await ws.SendAsync(
                        buffer,
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancel)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
            {
                parent.HandleSocketLost(e.Message);
                throw;
            }
        }
    }

    private static async ValueTask ReceiveTransferData(
        WebSocket ws,
        ChannelWriter<ArraySegment<byte>> channel,
        CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
            var result = await ws.ReceiveAsync(buf.AsMemory(), cancel).ConfigureAwait(false);

            BaseTransferManager.ReceivedDataMetrics.Inc(result.Count);

            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("Closed by the other side during a transfer");

            if (result.MessageType != WebSocketMessageType.Binary)
                throw new ProtocolViolationException("Data must be binary!");

            await channel.WriteAsync(new ArraySegment<byte>(buf, 0, result.Count), cancel).ConfigureAwait(false);

            if (result.EndOfMessage)
                break;
        }
    }

    public override void Dispose()
    {
        base.Dispose();

        // Cancel first, so the read loop knows the socket closing under it is expected.
        _readCancel.Cancel();
        WebSocket?.Dispose();

        ArrayPool<byte>.Shared.Return(_headerBuffer);
    }
}
