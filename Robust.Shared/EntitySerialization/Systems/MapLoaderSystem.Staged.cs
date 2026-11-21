using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Timing;

namespace Robust.Shared.EntitySerialization.Systems;

// Wolfgate: loading a grid over several ticks.
//
// TryLoadGrid parses the file and allocates, loads, initialises and starts every entity inside one call, so a big
// hull stalls the tick it is loaded on. BeginLoadGrid builds the same grid on a paused map of its own, and StepLoad
// advances it for a time budget each tick. When it finishes the caller owns a paused map holding the grid, exactly
// what TryLoadGrid(reader, source, out map, out grid) with PauseMaps gives, and moves the grid where it belongs.
//
// Until then the grid is half built: entities that are not started yet are flagged paused so queries skip them, and
// the grid sits on a map nothing else is on. Content must not touch a grid while IsStaging is true for its map.
// Map-init of a file saved before map-init still runs in one go in the last step; a save taken from a running game
// has none.
//
// Server-only: the client never calls this.
public sealed partial class MapLoaderSystem
{
    private readonly HashSet<EntityUid> _stagingMaps = new();

    /// <summary>Whether a map is the private map of a grid load that has not finished.</summary>
    public bool IsStaging(EntityUid map) => _stagingMaps.Contains(map);

    /// <summary>
    /// Parses a grid or map file into its data node. Touches no game state, so it may run on any thread.
    /// </summary>
    public static bool TryParse(TextReader reader, [NotNullWhen(true)] out MappingDataNode? data)
    {
        data = null;
        using var textReader = reader;
        var documents = DataNodeParser.ParseYamlStream(reader).ToArray();
        if (documents.Length != 1)
            return false;

        data = documents[0].Root as MappingDataNode;
        return data != null;
    }

    /// <summary>
    /// Starts loading a grid onto a new paused map without doing any of the work yet; call <see cref="StepLoad"/>
    /// until it returns true. The load consumes <paramref name="data"/>; parse the file again for another load.
    /// </summary>
    public StagedGridLoad BeginLoadGrid(MappingDataNode data, string source, DeserializationOptions? options = null)
    {
        var opts = options ?? DeserializationOptions.Default;
        var mapUid = _mapSystem.CreateMap(out var mapId, runMapInit: opts.InitializeMaps);
        _mapSystem.SetPaused(mapUid, true);
        _stagingMaps.Add(mapUid);

        var load = new StagedGridLoad(source, mapUid, mapId);
        load.Work = RunStaged(load, data, opts).GetEnumerator();
        return load;
    }

    /// <summary>
    /// Advances a load until it finishes or the budget is spent. True once the load is over, whether it worked
    /// (<see cref="StagedGridLoad.Grid"/> is set) or failed (everything it made is deleted).
    /// </summary>
    public bool StepLoad(StagedGridLoad load, TimeSpan budget)
    {
        if (load.State != StagedLoadState.Running)
            return true;

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        load.Steps++;

        try
        {
            while (load.Work!.MoveNext())
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(start) >= budget)
                {
                    load.Elapsed += System.Diagnostics.Stopwatch.GetElapsedTime(start);
                    return false;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"Caught exception while loading {load.Source} in stages: {e}");
            load.Elapsed += System.Diagnostics.Stopwatch.GetElapsedTime(start);
            Fail(load);
            return true;
        }

        load.Elapsed += System.Diagnostics.Stopwatch.GetElapsedTime(start);
        load.Work.Dispose();
        load.Work = null;
        _stagingMaps.Remove(load.Map);

        if (load.State == StagedLoadState.Running)
            Fail(load);
        else if (load.State == StagedLoadState.Done)
            Log.Debug($"Loaded {load.Source} in {load.Steps} steps, {load.Elapsed.TotalMilliseconds:0.0} ms");

        return true;
    }

    /// <summary>Stops a load that has not finished and deletes everything it made.</summary>
    public void AbortLoad(StagedGridLoad load)
    {
        if (load.State == StagedLoadState.Running)
            Fail(load);
    }

    private void Fail(StagedGridLoad load)
    {
        load.State = StagedLoadState.Failed;
        load.Work?.Dispose();
        load.Work = null;
        load.Grid = null;
        _stagingMaps.Remove(load.Map);

        if (load.Deserializer != null)
            Delete(load.Deserializer.Result);

        load.Deserializer = null;
        Del(load.Map);
    }

    private IEnumerable<bool> RunStaged(StagedGridLoad load, MappingDataNode data, DeserializationOptions options)
    {
        var ev = new BeforeEntityReadEvent();
        RaiseLocalEvent(ev);

        var opts = new MapLoadOptions
        {
            MergeMap = load.MapId,
            DeserializationOptions = options,
            ExpectedCategory = FileCategory.Grid,
        };
        opts.DeserializationOptions.AssignMapIds = true;

        var deserializer = new EntityDeserializer(
            _dependency,
            data,
            opts.DeserializationOptions,
            ev.RenamedPrototypes,
            ev.DeletedPrototypes);
        load.Deserializer = deserializer;

        if (!deserializer.TryProcessData())
        {
            Log.Debug($"Failed to process entity data in {load.Source}");
            yield break;
        }

        if (deserializer.Result.Category != FileCategory.Grid && deserializer.Result.Category != FileCategory.Unknown)
        {
            Log.Error($"{load.Source} does not hold a grid, but {deserializer.Result.Category}");
            yield break;
        }

        yield return false;

        foreach (var _ in deserializer.CreateEntitiesStaged())
        {
            yield return false;
        }

        if (deserializer.Result.Category != FileCategory.Grid)
        {
            Log.Error($"{load.Source} does not hold a grid, but {deserializer.Result.Category}");
            yield break;
        }

        load.Entities = deserializer.Result.Entities.Count;

        var merged = new HashSet<EntityUid>();
        MergeMaps(deserializer, opts, merged);
        yield return false;

        foreach (var _ in deserializer.StartEntitiesStaged())
        {
            yield return false;
        }

        MapInitalizeMerged(merged, load.MapId);

        if (deserializer.Result.Grids.Count != 1)
        {
            Log.Error($"{load.Source} held {deserializer.Result.Grids.Count} grids, expected one");
            yield break;
        }

        load.Result = deserializer.Result;
        load.Grid = deserializer.Result.Grids.Single();
        load.Deserializer = null;
        load.State = StagedLoadState.Done;
    }
}

public enum StagedLoadState : byte
{
    Running,
    Done,
    Failed,
}

/// <summary>A grid load in progress; see <see cref="MapLoaderSystem.BeginLoadGrid"/>.</summary>
public sealed class StagedGridLoad
{
    public readonly string Source;

    /// <summary>The paused map the grid is built on. The caller owns it once the load is done.</summary>
    public readonly EntityUid Map;

    public readonly MapId MapId;

    public StagedLoadState State { get; internal set; }

    /// <summary>The loaded grid, once <see cref="State"/> is <see cref="StagedLoadState.Done"/>.</summary>
    public Entity<MapGridComponent>? Grid { get; internal set; }

    public LoadResult? Result { get; internal set; }

    /// <summary>Entities in the file, known once they are all allocated.</summary>
    public int Entities { get; internal set; }

    /// <summary>Calls to <see cref="MapLoaderSystem.StepLoad"/> so far.</summary>
    public int Steps { get; internal set; }

    /// <summary>Main-thread time spent in those calls.</summary>
    public TimeSpan Elapsed { get; internal set; }

    internal IEnumerator<bool>? Work;
    internal EntityDeserializer? Deserializer;

    internal StagedGridLoad(string source, EntityUid map, MapId mapId)
    {
        Source = source;
        Map = map;
        MapId = mapId;
    }
}
