namespace Robust.Shared.Network.Transfer;

internal abstract partial class BaseTransferManager
{
    /// <summary>
    /// Wolfgate: a channel's transfer socket died. The server remembers the player and gives them the game connection
    /// for transfers on later joins.
    /// </summary>
    internal virtual void TransferSocketLost(INetChannel channel)
    {
    }
}
