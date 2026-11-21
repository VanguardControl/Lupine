using System;
using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Robust.Shared.EntitySerialization;

// Wolfgate: the entity-by-entity parts of a load as enumerators, so a caller can spread one load over several ticks.
// See MapLoaderSystem.Staged.cs. Each yield is a point where the load may be left until a later tick.
//
// Entities that exist across a tick before they are started are flagged paused the moment they are allocated, which
// keeps them out of every EntityQueryEnumerator. The flag is set without the pause event and cleared again before the
// stock pause handling runs, so the end state is the same as a one-call load.
public sealed partial class EntityDeserializer
{
    private readonly List<EntityUid> _stagedHidden = new();

    /// <summary>
    /// Marks a reference to an entity outside the save, written with SerializationOptions.KeepExternalReferences.
    /// It carries a token made new each time the server starts, so a file read by another process, where the number
    /// would name some other entity, reads as invalid.
    /// </summary>
    internal static readonly string ExternalPrefix = $"live:{Guid.NewGuid():N}:";

    /// <summary>
    /// Reads a reference written with SerializationOptions.KeepExternalReferences. True for any such reference, with
    /// <see cref="EntityUid.Invalid"/> if it is from another run or the entity is gone.
    /// </summary>
    private bool TryReadExternal(string value, out EntityUid uid)
    {
        uid = EntityUid.Invalid;
        if (!value.StartsWith("live:", StringComparison.Ordinal))
            return false;

        if (value.StartsWith(ExternalPrefix, StringComparison.Ordinal)
            && int.TryParse(value.AsSpan(ExternalPrefix.Length), out var id)
            && !EntMan.Deleted(new EntityUid(id)))
        {
            uid = new EntityUid(id);
        }

        return true;
    }

    /// <summary>
    /// An existing grid that takes the place of the grid in a grid file: nothing is allocated or loaded for the
    /// file's grid entity, and everything on it is loaded onto this one, where it stands. Set before
    /// <see cref="CreateEntities"/>. The grid's own saved components, tiles included, are ignored.
    /// </summary>
    public EntityUid? AdoptGrid;

    private bool IsAdopted(int yamlId)
    {
        return AdoptGrid != null && GridYamlIds.Count == 1 && GridYamlIds[0] == yamlId;
    }

    /// <summary>
    /// <see cref="CreateEntities"/>, yielding after each entity is allocated and after each has its data loaded.
    /// </summary>
    public IEnumerable<bool> CreateEntitiesStaged()
    {
        var time = Timing.CurTime;
        foreach (var (protoId, ents) in Prototypes)
        {
            var proto = protoId == string.Empty
                ? null
                : _proto.Index<EntityPrototype>(protoId);

            foreach (var ent in ents)
            {
                var entity = AllocateEntity(proto, ent);
                if (!entity.IsValid())
                    continue;

                _metaQuery.Comp(entity).PauseTime = time;
                _stagedHidden.Add(entity);
                yield return false;
            }
        }

        if (Options.AssignMapIds)
            AllocateMapIds();

        foreach (var (entity, data) in Entities)
        {
            LoadEntityData(entity, data);
            yield return false;
        }

        CurrentReadingEntity = null;

        GetRootEntities();
        RemoveEmptyChunks();
        StoreGridTileMap();

        if (Options.AssignMapIds)
            AssignMapIds();

        CheckCategory();
    }

    /// <summary>
    /// <see cref="StartEntities"/>, yielding after each entity is initialised and started.
    /// </summary>
    public IEnumerable<bool> StartEntitiesStaged()
    {
        AdoptGrids();
        ValidateMapIds();
        BuildEntityHierarchy();

        foreach (var uid in SortedEntities)
        {
            var meta = _metaQuery.GetComponent(uid);
            ResetNetTicks(uid, meta);
            RebaseNetTicks(meta);
            EntMan.InitializeEntity(uid, meta);
            EntMan.StartEntity(uid);

            // Networking treats the entity as new on the tick it starts, not the tick it was allocated. A component
            // dirtied while initialising has already stamped this tick without the entity being reported, and a
            // second dirty on the same tick is ignored, so the stamp is cleared first.
            meta.EntityLastModifiedTick = GameTick.Zero;
            EntMan.DirtyEntity(uid, meta);
            yield return false;
        }

        foreach (var uid in _stagedHidden)
        {
            if (!_metaQuery.TryComp(uid, out var meta))
                continue;

            // Dirtied: a state built since the entity started said it was paused.
            meta.PauseTime = null;
            EntMan.Dirty(uid, meta);
        }

        _stagedHidden.Clear();

        SetMapInitLifestage();
        SetPaused();
        GetRootNodes();
        PauseMaps();
        InitializeMaps();
        ProcessDeletions();
    }

    /// <summary>
    /// Moves the net ticks an entity got when it was allocated to the current tick. A state built from the previous
    /// tick only carries components modified after it, and this entity has not been in any state yet.
    /// </summary>
    private void RebaseNetTicks(MetaDataComponent metadata)
    {
        var tick = Timing.CurTick;
        foreach (var component in metadata.NetComponents.Values)
        {
            if (component.LastModifiedTick != GameTick.Zero)
            {
                component.LastModifiedTick = tick;

                // A component that sends only its changed fields would otherwise send just what initialising
                // touches, to a client that has never had the whole of it.
                if (component is IComponentDelta delta)
                    delta.LastUnclassifiedDirty = tick;
            }

            if (component.CreationTick != GameTick.Zero)
                component.CreationTick = tick;
        }
    }
}
