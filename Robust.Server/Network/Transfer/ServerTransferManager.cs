using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Robust.Server.ServerStatus;
using Robust.Shared;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages.Transfer;
using Robust.Shared.Network.Transfer;

namespace Robust.Server.Network.Transfer;

internal sealed class ServerTransferManager : BaseTransferManager, ITransferManager, IServerTransferStatus
{
    internal const string TransferApiUrl = "/rt_transfer_init";

    private readonly IConfigurationManager _cfg;
    private readonly IStatusHost _statusHost;
    private readonly IServerNetManager _netManager;

    private readonly Dictionary<NetUserId, Player> _onlinePlayers = new();

    /// <summary>
    /// Wolfgate: players whose transfer socket failed to connect or dropped. They get the game connection straight away
    /// on later joins, until the server restarts. Written from the socket threads, so locked.
    /// </summary>
    private readonly HashSet<NetUserId> _httpFailed = new();

    internal ServerTransferManager(IConfigurationManager cfg, IStatusHost statusHost, IServerNetManager netManager, ILogManager logManager, ITaskManager taskManager)
        : base(logManager, NetMessageAccept.Server, taskManager)
    {
        _cfg = cfg;
        _statusHost = statusHost;
        _netManager = netManager;
    }

    public void Initialize()
    {
        _netManager.RegisterNetMessage<MsgTransferInit>();
        _netManager.RegisterNetMessage<MsgTransferData>(RxTransferData, NetMessageAccept.Server | NetMessageAccept.Handshake);
        _netManager.RegisterNetMessage<MsgTransferAckInit>(RxTransferAckInit, NetMessageAccept.Server | NetMessageAccept.Handshake);

        _statusHost.AddHandler(HandleRequest);

        _netManager.Disconnect += NetManagerOnDisconnect;
    }

    private void RxTransferData(MsgTransferData message)
    {
        if (!_onlinePlayers.TryGetValue(message.MsgChannel.UserId, out var player)
            || player.Impl is not TransferImplLidgren lidgren)
        {
            message.MsgChannel.Disconnect("Not lidgren");
            return;
        }

        lidgren.ReceiveData(message);
    }

    private void RxTransferAckInit(MsgTransferAckInit message)
    {
        if (!_onlinePlayers.TryGetValue(message.MsgChannel.UserId, out var player)
            || player.Impl is not TransferImplLidgren lidgren)
        {
            message.MsgChannel.Disconnect("Not lidgren");
            return;
        }

        lidgren.ReceiveInitAck();
    }

    public Stream StartTransfer(INetChannel channel, TransferStartInfo startInfo)
    {
        if (!_onlinePlayers.TryGetValue(channel.UserId, out var player))
            throw new InvalidOperationException("Player is not connected yet!");

        return player.Impl.StartTransfer(startInfo);
    }

    private async Task<bool> HandleRequest(IStatusHandlerContext context)
    {
        if (context.Url.AbsolutePath != TransferApiUrl)
            return false;

        if (!context.IsWebSocketRequest)
        {
            Sawmill.Verbose("HTTP request failed: not a websocket request");
            await context.RespondErrorAsync(HttpStatusCode.BadRequest);
            return true;
        }

        if (!context.RequestHeaders.TryGetValue(TransferImplWebSocket.UserIdHeaderName, out var userIdValue)
            || userIdValue.Count != 1)
        {
            Sawmill.Verbose("HTTP request failed: missing RT-UserId");
            await context.RespondErrorAsync(HttpStatusCode.BadRequest);
            return true;
        }

        if (!Guid.TryParse(userIdValue[0], out var userId))
        {
            Sawmill.Verbose($"HTTP request failed: UserID '{userId}' invalid");
            await context.RespondErrorAsync(HttpStatusCode.BadRequest);
            return true;
        }

        if (!_onlinePlayers.TryGetValue(new NetUserId(userId), out var player))
        {
            Sawmill.Warning($"HTTP request failed: UserID '{userId}' not online");
            await context.RespondErrorAsync(HttpStatusCode.BadRequest);
            return true;
        }

        if (player.Impl is not ServerTransferImplWebSocket serverWs)
        {
            Sawmill.Warning($"HTTP request failed: UserID '{userId}' is not using websocket transfer");
            await context.RespondErrorAsync(HttpStatusCode.Unauthorized);
            return true;
        }

        await serverWs.HandleApiRequest(new NetUserId(userId), context);
        return true;
    }

    public async Task ServerHandshake(INetChannel channel)
    {
        if (_onlinePlayers.ContainsKey(channel.UserId))
            throw new InvalidOperationException("We already have data for this user??");

        var transferHttpEnabled = _cfg.GetCVar(CVars.TransferHttp);

        // Wolfgate: a player whose socket failed before gets the game connection straight away.
        var knownBad = transferHttpEnabled && IsHttpFailed(channel.UserId);

        if (!transferHttpEnabled || knownBad)
        {
            var lidgren = NewLidgren(channel);
            _onlinePlayers.Add(channel.UserId, new Player
            {
                Impl = lidgren,
                Mode = knownBad ? TransferChannelMode.WebSocketFailed : TransferChannelMode.GameConnection,
            });

            if (knownBad)
                Sawmill.Info($"{channel}: using the game connection for transfers, as its socket failed earlier");

            await lidgren.ServerInit();
            return;
        }

        var socket = new ServerTransferImplWebSocket(Sawmill, this, _cfg, _netManager, channel);
        socket.MaxChannelCount = _cfg.GetCVar(CVars.TransferStreamLimit);

        var datum = new Player
        {
            Impl = socket,
            Mode = TransferChannelMode.WebSocket,
        };

        _onlinePlayers.Add(channel.UserId, datum);

        var connected = socket.ServerInit();

        // Wolfgate: fall back to the game connection when the client can't reach the socket. A stock client takes a
        // second transfer init and switches to it, so this needs nothing from the client. Before, the join hung.
        var timeout = _cfg.GetCVar(CVars.TransferHttpFallbackSeconds);
        if (timeout <= 0)
        {
            await connected;
            return;
        }

        var finished = await Task.WhenAny(connected, Task.Delay(TimeSpan.FromSeconds(timeout)));
        if (finished == connected || !await socket.TryAbandon())
        {
            // Connected, or connecting under the API lock as the timeout fired.
            await connected;
            return;
        }

        // Disconnected while waiting: the socket was disposed, as the stock code would have done.
        if (!_onlinePlayers.TryGetValue(channel.UserId, out var current) || current != datum)
            throw new TaskCanceledException();

        Sawmill.Warning($"{channel}: transfer socket not reached in {timeout} s, falling back to the game connection");
        MarkHttpFailed(channel.UserId);
        socket.Dispose();

        var fallback = NewLidgren(channel);
        datum.Impl = fallback;
        datum.Mode = TransferChannelMode.WebSocketFailed;
        await fallback.ServerInit();
    }

    private TransferImplLidgren NewLidgren(INetChannel channel)
    {
        return new TransferImplLidgren(Sawmill, channel, this, _netManager)
        {
            MaxChannelCount = _cfg.GetCVar(CVars.TransferStreamLimit),
        };
    }

    /// <inheritdoc />
    public TransferChannelMode GetMode(NetUserId user)
    {
        return _onlinePlayers.TryGetValue(user, out var player) ? player.Mode : TransferChannelMode.GameConnection;
    }

    private bool IsHttpFailed(NetUserId user)
    {
        if (!_cfg.GetCVar(CVars.TransferHttpFallbackRemember))
            return false;

        lock (_httpFailed)
        {
            return _httpFailed.Contains(user);
        }
    }

    private void MarkHttpFailed(NetUserId user)
    {
        lock (_httpFailed)
        {
            _httpFailed.Add(user);
        }
    }

    internal override void TransferSocketLost(INetChannel channel)
    {
        MarkHttpFailed(channel.UserId);
    }

    public event Action ClientHandshakeComplete
    {
        add { }
        remove { }
    }

    private void NetManagerOnDisconnect(object? sender, NetDisconnectedArgs e)
    {
        if (!_onlinePlayers.Remove(e.Channel.UserId, out var player))
            return;

        // Wolfgate: a stock client whose socket errors disconnects itself with this reason; next time it gets the game
        // connection instead of another kick.
        if (player.Mode == TransferChannelMode.WebSocket
            && e.Reason.Contains("transfer socket", StringComparison.OrdinalIgnoreCase))
        {
            MarkHttpFailed(e.Channel.UserId);
        }

        Sawmill.Debug("Cleaning up connection for channel {Player} that disconnected", e.Channel);
        player.Impl.Dispose();
    }

    private sealed class Player
    {
        public required BaseTransferImpl Impl;

        /// <summary>Wolfgate: which channel this player's transfers use; see <see cref="IServerTransferStatus"/>.</summary>
        public TransferChannelMode Mode;
    }
}
