using Robust.Shared.Network;

namespace Robust.Server.Network.Transfer;

/// <summary>
/// Wolfgate: which channel a connected player's file transfers use. Server-only, so content can show it to the player;
/// the client engine stays stock.
/// </summary>
public interface IServerTransferStatus
{
    /// <summary>The player's transfer channel; <see cref="TransferChannelMode.GameConnection"/> when not connected.</summary>
    TransferChannelMode GetMode(NetUserId user);
}

/// <summary>Wolfgate: how a player's file transfers travel.</summary>
public enum TransferChannelMode : byte
{
    /// <summary>Over the game connection, because the websocket channel is off on this server.</summary>
    GameConnection,

    /// <summary>Over the websocket channel.</summary>
    WebSocket,

    /// <summary>Over the game connection, because the player's websocket failed to connect now or earlier.</summary>
    WebSocketFailed,
}
