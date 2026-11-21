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
}
