using System.Collections.Generic;
using System.Runtime.InteropServices;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Robust.Server.GameStates;

// Wolfgate: force-sent entities (every grid and map) are only walked for a session when they changed.
//
// Stock PVS re-adds the whole force-send list to every session's send set every tick, changed or not, and the ack and
// leave jobs then walk those lists again: grids x sessions x tick rate, growing with round age. With
// net.pvs_force_delta a session whose last acked state is recent only gets the force-sent entities that were modified
// or joined the list since that state. The rest stay held on the client: the leave job and the eviction sweep skip
// anything in the force-send list, so an entity that is not re-sent is never detached or evicted.
//
// A session takes the short walk only while it provably holds the full list: its acked state must be inside the
// window the change list covers, and no force-sent entity may have been held back from a state at or after it.
// Anything else (a join, a full-state request, a lagging client) takes the stock full walk.
internal sealed partial class PvsSystem
{
    private bool _forceDelta;

    /// <summary>PVS indices of the cached force-send list while <see cref="_forceDelta"/> is on, else empty.</summary>
    private readonly HashSet<PvsIndex> _forceHeld = new();

    /// <summary>Last tick's force-send set, to find what joined and what dropped out.</summary>
    private readonly HashSet<EntityUid> _forcePrevious = new();

    /// <summary>Tick at which entities joined the force-send list, kept for <see cref="DirtyBufferSize"/> ticks.</summary>
    private readonly Dictionary<EntityUid, GameTick> _forceJoined = new();

    /// <summary>Force-sent entities modified or joined within the last <see cref="DirtyBufferSize"/> ticks.</summary>
    private readonly List<(PvsChunk.ChunkEntity Ent, GameTick Changed)> _forceRecent = new();

    /// <summary>Live entities that dropped out of the force-send list this tick.</summary>
    private readonly List<Entity<MetaDataComponent>> _forceDropped = new();

    private void OnForceDeltaChanged(bool value) => _forceDelta = value;

    /// <summary>
    /// Rebuilds the change list for this tick. Runs on the main thread after the force-send cache is rebuilt and
    /// before any session is serialised.
    /// </summary>
    private void UpdateForceDelta()
    {
        var cur = _gameTiming.CurTick;
        var windowStart = cur.Value > DirtyBufferSize ? new GameTick(cur.Value - DirtyBufferSize) : GameTick.Zero;

        _forceRecent.Clear();
        _forceDropped.Clear();
        _forceHeld.Clear();

        foreach (var uid in _forcePrevious)
        {
            if (_forceOverrideSet.Contains(uid))
                continue;

            _forceJoined.Remove(uid);
            if (_metaQuery.TryGetComponent(uid, out var meta)
                && meta.EntityLifeStage < EntityLifeStage.Terminating
                && meta.PvsData != PvsIndex.Invalid)
            {
                _forceDropped.Add((uid, meta));
            }
        }

        foreach (ref var ent in CollectionsMarshal.AsSpan(_cachedForceOverride))
        {
            var changed = _metadataMemory.GetRef(ent.Ptr.Index).LastModifiedTick;

            if (!_forcePrevious.Contains(ent.Uid))
                _forceJoined[ent.Uid] = cur;

            if (_forceJoined.Count > 0 && _forceJoined.TryGetValue(ent.Uid, out var joined))
            {
                if (joined <= windowStart)
                    _forceJoined.Remove(ent.Uid);
                else if (joined > changed)
                    changed = joined;
            }

            if (changed > windowStart)
                _forceRecent.Add((ent, changed));

            if (_forceDelta)
                _forceHeld.Add(ent.Ptr);
        }

        _forcePrevious.Clear();
        _forcePrevious.UnionWith(_forceOverrideSet);
    }

    /// <summary>
    /// Adds only the force-sent entities that changed since the session's acked state. False if the session has to
    /// take the full walk.
    /// </summary>
    private bool TryAddForcedDelta(PvsSession session, GameTick fromTick)
    {
        var cur = _gameTiming.CurTick;
        if (!_forceDelta
            || session.RequestedFull
            || fromTick == GameTick.Zero
            || fromTick.Value + DirtyBufferSize < cur.Value
            || fromTick <= session.ForceIncomplete)
        {
            return false;
        }

        var entered = session.Budget.EnterCount;
        var created = session.Budget.NewCount;

        foreach (ref var entry in CollectionsMarshal.AsSpan(_forceRecent))
        {
            if (entry.Changed <= fromTick)
                continue;

            ref var meta = ref _metadataMemory.GetRef(entry.Ent.Ptr.Index);
            meta.Validate(entry.Ent.Meta);
            AddEntity(session, ref entry.Ent, ref meta, fromTick);
            NoteForcedResult(session, entry.Ent.Ptr, cur);
        }

        // A held entity that is re-sent after a quiet spell looks like one entering view. Only count the new ones
        // against the budget, as the full walk does.
        session.Budget.EnterCount = entered + (session.Budget.NewCount - created);
        return true;
    }

    /// <summary>
    /// A force-sent entity that did not make it into this state (held back after an eviction) leaves the session
    /// without the full list until it acks a later state.
    /// </summary>
    private void NoteForcedResult(PvsSession session, PvsIndex index, GameTick cur)
    {
        if (session.DataMemory.GetRef(index.Index).LastSeen != cur)
            session.ForceIncomplete = cur;
    }

    /// <summary>
    /// Sends an entity that dropped out of the force-send list once more, so the leave job sees it next tick and
    /// detaches or evicts it like any other entity that left view.
    /// </summary>
    private void AddForceDropped(PvsSession session, GameTick fromTick)
    {
        if (!_forceDelta || _forceDropped.Count == 0)
            return;

        var cur = _gameTiming.CurTick;
        foreach (var ent in _forceDropped)
        {
            ref var data = ref session.DataMemory.GetRef(ent.Comp.PvsData.Index);
            if (data.LastSeen == GameTick.Zero || data.LastSeen == cur || data.Evicted != GameTick.Zero)
                continue;

            AddEntity(session, ent, fromTick);
        }
    }
}
