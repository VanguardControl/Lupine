using System;
using System.Collections.Generic;
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
// Nothing changes on the client: a deletion of a detached entity is an ordinary deletion to it.
//
// A deleted entity's children are moved to nullspace by the client without being marked as out of view, so they are
// never restored when they come back: the server sends only what changed since the client last acked them. An entity
// that left view first is already marked and is safe. A range-limited root is the exception, as it is deleted the
// tick after it leaves view, while the client is still working through its children's departures 75 a tick
// (net.pvs_exit_budget). Its descendants are evicted with it (EvictRootDescendants).
internal sealed partial class PvsSystem
{
    private readonly Stack<TransformComponent> _evictStack = new();

    /// <summary>
    /// Evicts everything a client holds under each range-limited root the leave job evicted from it, so the client
    /// deletes those entities with the root and gets them as new on return. Runs on the main thread once the leave job
    /// has finished and before states are built; the eviction rides in the state about to be built.
    /// </summary>
    private void EvictRootDescendants()
    {
        var curTick = _gameTiming.CurTick;
        foreach (var session in _sessions)
        {
            if (session.EvictedRoots.Count == 0)
                continue;

            var evicted = 0;
            foreach (var root in session.EvictedRoots)
            {
                // A root deleted on the server takes its descendants with it through the global deletion list.
                if (TryGetEntity(root, out var uid) && _xformQuery.TryGetComponent(uid, out var xform))
                    evicted += EvictDescendants(session, xform, curTick);
            }

            session.EvictedRoots.Clear();

            if (evicted > 0)
                Log.Debug($"Evicted {evicted} entities under range-limited roots from {session.Session}");
        }
    }

    private int EvictDescendants(PvsSession session, TransformComponent root, GameTick tick)
    {
        var evicted = 0;
        _evictStack.Clear();
        _evictStack.Push(root);

        while (_evictStack.TryPop(out var xform))
        {
            var children = xform.ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                if (!_xformQuery.TryGetComponent(child, out var childXform)
                    || !_metaQuery.TryGetComponent(child, out var meta))
                {
                    continue;
                }

                // Deleting takes its descendants along, through the global deletion list.
                if (meta.EntityLifeStage >= EntityLifeStage.Terminating)
                    continue;

                _evictStack.Push(childXform);

                if (meta.PvsData == PvsIndex.Invalid)
                    continue;

                // Never sent to this client, or already evicted from it.
                ref var data = ref session.DataMemory.GetRef(meta.PvsData.Index);
                if (data.LastSeen == GameTick.Zero)
                    continue;

                session.Evicted.Add((meta.NetEntity, tick));
                data = new PvsData { Evicted = tick };
                evicted++;
            }
        }

        return evicted;
    }

    private void OnEvictSecondsChanged(float value) => _evictAgeTicks = SecondsToTicks(value);

    private void OnEvictIntervalChanged(float value) => _evictIntervalTicks = Math.Max(1, SecondsToTicks(value));

    private void OnEvictBudgetChanged(int value) => _evictBudget = Math.Max(0, value);

    private void OnEvictScanChanged(int value) => _evictScanSlots = Math.Max(1, value);

    /// <summary>Slots one sweep scans per session; see <see cref="CVars.NetPvsEvictScan"/>.</summary>
    private int _evictScanSlots = 16384;

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

        // A slice of the table per sweep, carried on from where the last one stopped: a full scan of 600k slots for
        // every session was 24 ms/s on the state-sending threads.
        var scan = Math.Min(size, _evictScanSlots);
        for (var n = 0; n < scan && budget > 0; n++)
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
