using System;
using System.Runtime.InteropServices;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Server.GameStates;

// Wolfgate: evicts long-unseen entities from clients and builds per-session deletion lists.
//
// A client never drops an entity that left its view; it parks it in nullspace and keeps it until it reconnects.
// A ghost touring the sector ends up holding the whole sector, and frame time collapses under the GC. The server
// now tells a client to delete entities it has not been sent for a while and sends them as new if they come back.
// Nothing changes on the client: a deletion of a detached entity is an ordinary deletion to it, and the client
// detaches the entity's children before deleting it, so they survive until their own eviction.
internal sealed partial class PvsSystem
{
    private void OnEvictSecondsChanged(float value) => _evictAgeTicks = SecondsToTicks(value);

    private void OnEvictIntervalChanged(float value) => _evictIntervalTicks = Math.Max(1, SecondsToTicks(value));

    private void OnEvictBudgetChanged(int value) => _evictBudget = Math.Max(0, value);

    private uint SecondsToTicks(float seconds)
    {
        return seconds <= 0 ? 0 : (uint) Math.Ceiling(seconds * _gameTiming.TickRate);
    }

    /// <summary>
    /// Evicts entities this client has held out of view for longer than <see cref="CVars.NetPvsEvictSeconds"/>.
    /// Runs on the session's own thread during state construction and only writes the session's own data.
    /// </summary>
    private void SweepEvictions(PvsSession session)
    {
        var curTick = _gameTiming.CurTick;
        if (_evictAgeTicks == 0 || _evictBudget == 0 || curTick.Value <= _evictAgeTicks)
            return;

        if (curTick < session.NextEvictionSweep)
            return;

        session.NextEvictionSweep = new GameTick(curTick.Value + _evictIntervalTicks);

        var cutoff = new GameTick(curTick.Value - _evictAgeTicks);
        var data = session.DataMemory.GetSpan();
        var size = Math.Min(data.Length, _metadataMemory.CurrentSize);
        var cursor = session.EvictionCursor;
        var budget = _evictBudget;
        var evicted = 0;

        for (var n = 0; n < size && budget > 0; n++)
        {
            if (cursor >= size)
                cursor = 0;

            var index = cursor++;
            ref var entry = ref data[index];

            // Never sent, in view, or seen recently. Freed slots are zeroed, so they fall out here too.
            if (entry.LastSeen == GameTick.Zero || entry.LastSeen >= cutoff)
                continue;

            // Deleted but not yet returned to the free list: the global deletion list already covers it.
            ref var meta = ref _metadataMemory.GetRef(index);
            if (meta.LifeStage >= EntityLifeStage.Terminating)
                continue;

            session.Evicted.Add((meta.NetEntity, curTick));
            entry = new PvsData { Evicted = curTick };
            budget--;
            evicted++;
        }

        session.EvictionCursor = cursor;

        if (evicted > 0)
            Log.Debug($"Evicted {evicted} entities from {session.Session}");
    }

    /// <summary>
    /// An evicted entity stays unsent until the client has acked a tick past the eviction. The client applies
    /// deletions after entity states, so a re-send in the same state as the deletion would be deleted too.
    /// </summary>
    private static bool IsHeldBackAfterEviction(ref PvsData data, GameTick fromTick)
    {
        if (data.Evicted == GameTick.Zero)
            return false;

        if (data.Evicted > fromTick)
            return true;

        data.Evicted = GameTick.Zero;
        return false;
    }

    /// <summary>
    /// Fills <see cref="PvsSession.Deletions"/> with the deletions this client still needs: global deletions newer
    /// than its last ack and its own evictions. Evictions the client has acked are dropped.
    /// </summary>
    private void BuildDeletions(PvsSession session)
    {
        var fromTick = session.FromTick;
        var deletions = session.Deletions;
        deletions.Clear();

        var deleted = CollectionsMarshal.AsSpan(_deletedEntities);
        var ticks = CollectionsMarshal.AsSpan(_deletedTick);
        for (var i = 0; i < deleted.Length; i++)
        {
            if (ticks[i] > fromTick)
                deletions.Add(deleted[i]);
        }

        var evicted = session.Evicted;
        for (var i = evicted.Count - 1; i >= 0; i--)
        {
            var (netEntity, tick) = evicted[i];
            if (tick > fromTick)
                deletions.Add(netEntity);
            else
                evicted.RemoveSwap(i);
        }
    }
}
