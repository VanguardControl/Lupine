using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.UnitTesting.Shared.GameState;

namespace Robust.UnitTesting.Shared.EntitySerialization;

/// <summary>
/// Wolfgate: a grid loaded over many ticks ends up the same as one loaded in a single call, stays out of queries and
/// game states while it is half built, and can be abandoned. See MapLoaderSystem.Staged.cs.
/// </summary>
internal sealed class StagedGridLoadTest : RobustIntegrationTest
{
    private const int Children = 40;

    private const string Prototypes = @"
- type: testTileDef
  id: space

- type: testTileDef
  id: a
";

    [Test]
    public async Task GridLoadsOverManyTicks()
    {
        await using var pair = await StartConnectedPair(
            new ServerIntegrationOptions { Pool = false, ExtraPrototypes = Prototypes },
            new ClientIntegrationOptions { Pool = false, ExtraPrototypes = Prototypes });
        var (client, server) = pair;

        var sEntMan = server.ResolveDependency<IEntityManager>();
        var cEntMan = client.ResolveDependency<IEntityManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var mapSys = server.System<SharedMapSystem>();
        var metaSys = server.System<MetaDataSystem>();
        var xforms = server.System<SharedTransformSystem>();
        var loader = server.System<MapLoaderSystem>();

        SerializationTestHelper.LoadTileDefs(server.ProtoMan, server.ResolveDependency<ITileDefinitionManager>(), "space");
        SerializationTestHelper.LoadTileDefs(client.ProtoMan, client.ResolveDependency<ITileDefinitionManager>(), "space");
        var tile = new Tile(server.ProtoMan.Index<TileDef>("a").TileId);

        // Without PVS every state is built from the per-tick added and dirty sets, as replays are: the path where an
        // entity allocated ticks before it starts could be sent half built, or with parts missing.
        server.Post(() => server.ResolveDependency<IConfigurationManager>().SetCVar(CVars.NetPVS, false));

        await RunTicksSync(server, client, 10);

        // A grid with named children, one of them inside another.
        EntityUid map = default;
        string yaml = default!;
        await server.WaitPost(() =>
        {
            map = mapSys.CreateMap();
            var grid = mapSys.CreateGridEntity(map);
            mapSys.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, tile);
            mapSys.SetTile(grid.Owner, grid.Comp, new Vector2i(1, 0), tile);

            EntityUid last = default;
            for (var i = 0; i < Children; i++)
            {
                var parent = i == Children - 1 ? last : grid.Owner;
                last = sEntMan.SpawnEntity(null, new EntityCoordinates(parent, new Vector2(0.5f, 0.5f)));
                metaSys.SetEntityName(last, $"child {i}");

                // Networked data nothing touches while the entity starts: it only reaches the client if the load
                // presents the entity as new on the tick it starts.
                sEntMan.AddComponent<AutoNetworkingTestComponent>(last).IsNetworked = 100 + i;
            }

            var player = sEntMan.SpawnEntity(null, new EntityCoordinates(map, new Vector2(50, 50)));
            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, player);
            sPlayerMan.JoinGame(session);

            using var writer = new StringWriter();
            Assert.That(loader.TrySaveGrid(grid.Owner, writer));
            yaml = writer.ToString();
            sEntMan.DeleteEntity(grid.Owner);
        });

        await RunTicksSync(server, client, 5);

        Assert.That(MapLoaderSystem.TryParse(new StringReader(yaml), out var data));

        // What a single call makes of the file.
        var expected = new List<string>();
        await server.WaitPost(() =>
        {
            Assert.That(loader.TryLoadGrid(new StringReader(yaml), "single", out var oneMap, out var one));
            expected = Names(one!.Value.Owner);
            sEntMan.DeleteEntity(oneMap!.Value.Owner);
        });
        Assert.That(expected, Has.Count.EqualTo(Children));

        await RunTicksSync(server, client, 5);
        var visibleBefore = 0;
        var existingBefore = 0;
        await server.WaitPost(() =>
        {
            visibleBefore = Visible();
            existingBefore = sEntMan.EntityCount;
        });

        // Abandoning a load part-way leaves nothing behind.
        StagedGridLoad abandoned = default!;
        await server.WaitPost(() => abandoned = loader.BeginLoadGrid(data!, "abandoned"));
        for (var i = 0; i < Children + 10; i++)
        {
            await server.WaitPost(() => Assert.That(loader.StepLoad(abandoned, TimeSpan.Zero), Is.False));
            await RunTicksSync(server, client, 1);
        }

        await server.WaitPost(() =>
        {
            Assert.That(sEntMan.EntityCount, Is.GreaterThan(existingBefore), "the load had not made anything yet");
            loader.AbortLoad(abandoned);
            Assert.That(abandoned.State, Is.EqualTo(StagedLoadState.Failed));
            Assert.That(sEntMan.EntityCount, Is.EqualTo(existingBefore), "an abandoned load left entities behind");
            Assert.That(loader.IsStaging(abandoned.Map), Is.False);
        });

        var fullStates = 0;
        client.ResolveDependency<Robust.Client.GameStates.IClientGameStateManager>().GameStateApplied += args =>
        {
            if (args.AppliedState.FromSequence == GameTick.Zero)
                fullStates++;
        };

        // One step per tick with no budget: every entity is allocated, loaded and started on a tick of its own.
        // A load consumes its data node, so parse the file again.
        Assert.That(MapLoaderSystem.TryParse(new StringReader(yaml), out var fresh));
        StagedGridLoad load = default!;
        await server.WaitPost(() => load = loader.BeginLoadGrid(fresh!, "staged"));
        var done = false;
        var steps = 0;
        while (!done)
        {
            await server.WaitPost(() =>
            {
                done = loader.StepLoad(load, TimeSpan.Zero);
                if (!done)
                {
                    Assert.That(loader.IsStaging(load.Map), Is.True);
                    Assert.That(Visible(), Is.EqualTo(visibleBefore), "a half-built entity showed up in a query");
                }
            });
            await RunTicksSync(server, client, 1);
            Assert.That(++steps, Is.LessThan(Children * 10), "the load never finished");
        }

        Assert.That(steps, Is.GreaterThan(Children * 3), "the load was not spread over ticks");
        Assert.That(load.State, Is.EqualTo(StagedLoadState.Done));
        Assert.That(load.Grid, Is.Not.Null);
        var loaded = load.Grid!.Value.Owner;

        await server.WaitPost(() =>
        {
            Assert.That(loader.IsStaging(load.Map), Is.False);
            Assert.That(mapSys.IsPaused(load.Map), Is.True);
            Assert.That(sEntMan.GetComponent<MetaDataComponent>(loaded).EntityPaused, Is.True);
            Assert.That(xforms.GetMap(loaded), Is.EqualTo(load.Map));
            Assert.That(Names(loaded), Is.EqualTo(expected));

            // Hand it to the live map, as the caller would.
            xforms.SetCoordinates(loaded, new EntityCoordinates(map, new Vector2(40, 50)));
            sEntMan.DeleteEntity(load.Map);
        });

        await RunTicksSync(server, client, 10);

        await server.WaitPost(() =>
        {
            Assert.That(sEntMan.GetComponent<MetaDataComponent>(loaded).EntityPaused, Is.False);
            Assert.That(Names(loaded), Is.EqualTo(expected));
        });

        // The client got every entity, with its data, although most were allocated ticks before they started.
        var nets = new List<(NetEntity Net, string Name, int Value)>();
        await server.WaitPost(() =>
        {
            foreach (var uid in Descendants(loaded))
            {
                var meta = sEntMan.GetComponent<MetaDataComponent>(uid);
                Assert.That(meta.EntityPaused, Is.False, $"{meta.EntityName} is still paused");
                nets.Add((meta.NetEntity, meta.EntityName, sEntMan.GetComponent<AutoNetworkingTestComponent>(uid).IsNetworked));
            }
        });

        await client.WaitPost(() =>
        {
            Assert.That(cEntMan.TryGetEntity(sEntMan.GetNetEntity(loaded), out _), "the client has no grid");
            foreach (var (net, name, value) in nets)
            {
                Assert.That(cEntMan.TryGetEntityData(net, out var uid, out var meta), $"the client is missing {name}");
                Assert.That(meta!.EntityName, Is.EqualTo(name));
                Assert.That(value, Is.GreaterThanOrEqualTo(100));
                Assert.That(cEntMan.TryGetComponent<AutoNetworkingTestComponent>(uid, out var comp), $"{name} lost its data");
                Assert.That(comp!.IsNetworked, Is.EqualTo(value));
            }
        });

        // A state that left out part of a new entity would have made the client ask for everything again.
        Assert.That(fullStates, Is.Zero, "the client requested a full state");
        return;

        int Visible()
        {
            var count = 0;
            var query = sEntMan.EntityQueryEnumerator<TransformComponent>();
            while (query.MoveNext(out _))
            {
                count++;
            }

            return count;
        }

        IEnumerable<EntityUid> Descendants(EntityUid root)
        {
            var children = sEntMan.GetComponent<TransformComponent>(root).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                yield return child;
                foreach (var deeper in Descendants(child))
                {
                    yield return deeper;
                }
            }
        }

        List<string> Names(EntityUid root)
        {
            return Descendants(root)
                .Select(uid => sEntMan.GetComponent<MetaDataComponent>(uid).EntityName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
    }
    /// <summary>
    /// A grid whose contents were saved and deleted gets them back on the same grid entity, a few a tick.
    /// </summary>
    [Test]
    public async Task ContentsLoadOntoTheGridThatStayed()
    {
        await using var pair = await StartConnectedPair(
            new ServerIntegrationOptions { Pool = false, ExtraPrototypes = Prototypes },
            new ClientIntegrationOptions { Pool = false, ExtraPrototypes = Prototypes });
        var (client, server) = pair;

        var sEntMan = server.ResolveDependency<IEntityManager>();
        var cEntMan = client.ResolveDependency<IEntityManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var mapSys = server.System<SharedMapSystem>();
        var metaSys = server.System<MetaDataSystem>();
        var loader = server.System<MapLoaderSystem>();

        SerializationTestHelper.LoadTileDefs(server.ProtoMan, server.ResolveDependency<ITileDefinitionManager>(), "space");
        SerializationTestHelper.LoadTileDefs(client.ProtoMan, client.ResolveDependency<ITileDefinitionManager>(), "space");
        var tile = new Tile(server.ProtoMan.Index<TileDef>("a").TileId);
        server.Post(() => server.ResolveDependency<IConfigurationManager>().SetCVar(CVars.NetPVS, false));
        await RunTicksSync(server, client, 10);

        EntityUid map = default;
        EntityUid grid = default;
        string yaml = default!;
        var expected = new List<(string Name, int Value)>();
        await server.WaitPost(() =>
        {
            map = mapSys.CreateMap();
            var made = mapSys.CreateGridEntity(map);
            grid = made.Owner;
            mapSys.SetTile(grid, made.Comp, Vector2i.Zero, tile);
            sEntMan.System<SharedTransformSystem>().SetWorldPosition(grid, new Vector2(30, 30));

            for (var i = 0; i < Children; i++)
            {
                var child = sEntMan.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
                metaSys.SetEntityName(child, $"child {i}");
                sEntMan.AddComponent<AutoNetworkingTestComponent>(child).IsNetworked = 100 + i;
                expected.Add(($"child {i}", 100 + i));
            }

            var player = sEntMan.SpawnEntity(null, new EntityCoordinates(map, new Vector2(50, 50)));
            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, player);
            sPlayerMan.JoinGame(session);

            using var writer = new StringWriter();
            Assert.That(loader.TrySaveGrid(grid, writer));
            yaml = writer.ToString();

            // The contents go; the grid stays.
            foreach (var child in sEntMan.GetComponent<TransformComponent>(grid)._children.ToArray())
            {
                sEntMan.DeleteEntity(child);
            }
        });

        await RunTicksSync(server, client, 20);
        var gridsBefore = sEntMan.Count<Robust.Shared.Map.Components.MapGridComponent>();
        var mapsBefore = sEntMan.Count<Robust.Shared.Map.Components.MapComponent>();

        var fullStates = 0;
        client.ResolveDependency<Robust.Client.GameStates.IClientGameStateManager>().GameStateApplied += args =>
        {
            if (args.AppliedState.FromSequence == GameTick.Zero)
                fullStates++;
        };

        Assert.That(MapLoaderSystem.TryParse(new StringReader(yaml), out var data));
        StagedGridLoad load = default!;
        await server.WaitPost(() => load = loader.BeginLoadOntoGrid(data!, "onto", grid));
        var done = false;
        var steps = 0;
        while (!done)
        {
            await server.WaitPost(() => done = loader.StepLoad(load, TimeSpan.Zero));
            await RunTicksSync(server, client, 1);
            Assert.That(++steps, Is.LessThan(Children * 10), "the load never finished");
        }

        Assert.That(steps, Is.GreaterThan(Children * 2), "the load was not spread over ticks");
        Assert.That(load.State, Is.EqualTo(StagedLoadState.Done));
        Assert.That(load.Grid!.Value.Owner, Is.EqualTo(grid), "the contents went onto another grid");
        await RunTicksSync(server, client, 5);

        var nets = new List<(NetEntity Net, string Name, int Value)>();
        await server.WaitPost(() =>
        {
            Assert.That(sEntMan.Count<Robust.Shared.Map.Components.MapGridComponent>(), Is.EqualTo(gridsBefore), "a second grid was made");
            Assert.That(sEntMan.Count<Robust.Shared.Map.Components.MapComponent>(), Is.EqualTo(mapsBefore), "a map was made");
            Assert.That(sEntMan.GetComponent<TransformComponent>(grid).MapUid, Is.EqualTo(map));

            var found = new List<(string, int)>();
            foreach (var child in sEntMan.GetComponent<TransformComponent>(grid)._children)
            {
                var meta = sEntMan.GetComponent<MetaDataComponent>(child);
                Assert.That(meta.EntityPaused, Is.False, $"{meta.EntityName} is still paused");
                Assert.That(meta.EntityLifeStage, Is.EqualTo(EntityLifeStage.MapInitialized));
                var value = sEntMan.GetComponent<AutoNetworkingTestComponent>(child).IsNetworked;
                found.Add((meta.EntityName, value));
                nets.Add((meta.NetEntity, meta.EntityName, value));
            }

            Assert.That(found.OrderBy(x => x.Item2), Is.EqualTo(expected.OrderBy(x => x.Value)));
        });

        await client.WaitPost(() =>
        {
            foreach (var (net, name, value) in nets)
            {
                Assert.That(cEntMan.TryGetEntityData(net, out var uid, out var meta), $"the client is missing {name}");
                Assert.That(meta!.EntityName, Is.EqualTo(name));
                Assert.That(cEntMan.GetComponent<AutoNetworkingTestComponent>(uid!.Value).IsNetworked, Is.EqualTo(value));
                Assert.That(meta.EntityPaused, Is.False, $"the client thinks {name} is paused");
            }
        });

        Assert.That(fullStates, Is.Zero, "the client requested a full state");
    }
    /// <summary>
    /// Part of what stands on a grid is saved with its parent kept, deleted, and put back under the same grid.
    /// </summary>
    [Test]
    public async Task LooseEntitiesComeBackUnderTheirParent()
    {
        await using var pair = await StartConnectedPair(
            new ServerIntegrationOptions { Pool = false, ExtraPrototypes = Prototypes },
            new ClientIntegrationOptions { Pool = false, ExtraPrototypes = Prototypes });
        var (client, server) = pair;

        var sEntMan = server.ResolveDependency<IEntityManager>();
        var cEntMan = client.ResolveDependency<IEntityManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var mapSys = server.System<SharedMapSystem>();
        var metaSys = server.System<MetaDataSystem>();
        var loader = server.System<MapLoaderSystem>();

        SerializationTestHelper.LoadTileDefs(server.ProtoMan, server.ResolveDependency<ITileDefinitionManager>(), "space");
        SerializationTestHelper.LoadTileDefs(client.ProtoMan, client.ResolveDependency<ITileDefinitionManager>(), "space");
        var tile = new Tile(server.ProtoMan.Index<TileDef>("a").TileId);
        server.Post(() => server.ResolveDependency<IConfigurationManager>().SetCVar(CVars.NetPVS, false));
        await RunTicksSync(server, client, 10);

        EntityUid grid = default;
        EntityUid stays = default;
        MappingDataNode data = default!;
        var expected = new List<(string Name, int Value, Vector2 At)>();
        await server.WaitPost(() =>
        {
            var map = mapSys.CreateMap();
            var made = mapSys.CreateGridEntity(map);
            grid = made.Owner;
            mapSys.SetTile(grid, made.Comp, Vector2i.Zero, tile);

            var taken = new HashSet<EntityUid>();
            EntityUid last = default;
            for (var i = 0; i < Children; i++)
            {
                // The last one sits inside another, and points at the one that stays.
                var parent = i == Children - 1 ? last : grid;
                var at = new Vector2(0.1f + i * 0.01f, 0.5f);
                last = sEntMan.SpawnEntity(null, new EntityCoordinates(parent, at));
                metaSys.SetEntityName(last, $"child {i}");
                sEntMan.AddComponent<AutoNetworkingTestComponent>(last).IsNetworked = 100 + i;
                taken.Add(last);
                expected.Add(($"child {i}", 100 + i, at));
            }

            stays = sEntMan.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(0.9f, 0.9f)));
            metaSys.SetEntityName(stays, "stays");
            sEntMan.AddComponent<EntitySaveTestComponent>(last).Entity = stays;

            var player = sEntMan.SpawnEntity(null, new EntityCoordinates(map, new Vector2(50, 50)));
            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, player);
            sPlayerMan.JoinGame(session);

            var serializer = new EntitySerializer(server.ResolveDependency<IEntitySystemManager>().DependencyCollection, SerializationOptions.Default with
            {
                MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                ErrorOnOrphan = false,
                KeepExternalReferences = true,
                KeepExternalParents = true,
            });
            serializer.ReserveYamlIds(taken);
            foreach (var uid in taken)
            {
                serializer.SerializeEntity(uid);
            }

            data = serializer.Write();
            foreach (var uid in taken)
            {
                if (!sEntMan.Deleted(uid))
                    sEntMan.DeleteEntity(uid);
            }
        });

        await RunTicksSync(server, client, 20);
        var fullStates = 0;
        client.ResolveDependency<Robust.Client.GameStates.IClientGameStateManager>().GameStateApplied += args =>
        {
            if (args.AppliedState.FromSequence == GameTick.Zero)
                fullStates++;
        };

        StagedGridLoad load = default!;
        await server.WaitPost(() => load = loader.BeginLoadEntities(data, "loose"));
        var done = false;
        var steps = 0;
        while (!done)
        {
            await server.WaitPost(() => done = loader.StepLoad(load, TimeSpan.Zero));
            await RunTicksSync(server, client, 1);
            Assert.That(++steps, Is.LessThan(Children * 10), "the load never finished");
        }

        Assert.That(load.State, Is.EqualTo(StagedLoadState.Done));
        Assert.That(load.UidMap, Has.Count.EqualTo(Children));
        await RunTicksSync(server, client, 5);

        var nets = new List<(NetEntity Net, string Name, int Value)>();
        await server.WaitPost(() =>
        {
            Assert.That(sEntMan.Deleted(stays), Is.False);
            var found = new List<(string, int, Vector2)>();
            foreach (var uid in load.Result!.Entities)
            {
                var meta = sEntMan.GetComponent<MetaDataComponent>(uid);
                var xform = sEntMan.GetComponent<TransformComponent>(uid);
                Assert.That(meta.EntityPaused, Is.False, $"{meta.EntityName} is still paused");
                Assert.That(xform.GridUid, Is.EqualTo(grid), $"{meta.EntityName} is not on the grid");
                var value = sEntMan.GetComponent<AutoNetworkingTestComponent>(uid).IsNetworked;
                found.Add((meta.EntityName, value, xform.LocalPosition));
                nets.Add((meta.NetEntity, meta.EntityName, value));

                if (meta.EntityName == $"child {Children - 1}")
                {
                    Assert.That(xform.ParentUid, Is.Not.EqualTo(grid), "the nested one came back loose");
                    Assert.That(sEntMan.GetComponent<EntitySaveTestComponent>(uid).Entity, Is.EqualTo(stays), "a reference to what stayed was lost");
                }
                else
                {
                    Assert.That(xform.ParentUid, Is.EqualTo(grid));
                }
            }

            Assert.That(found.OrderBy(x => x.Item2), Is.EqualTo(expected.OrderBy(x => x.Value)));
        });

        await client.WaitPost(() =>
        {
            foreach (var (net, name, value) in nets)
            {
                Assert.That(cEntMan.TryGetEntityData(net, out var uid, out var meta), $"the client is missing {name}");
                Assert.That(cEntMan.GetComponent<AutoNetworkingTestComponent>(uid!.Value).IsNetworked, Is.EqualTo(value));
                Assert.That(meta!.EntityPaused, Is.False);
            }
        });

        Assert.That(fullStates, Is.Zero, "the client requested a full state");
    }
}
