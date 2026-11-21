using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Server.GameStates;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Robust.UnitTesting.Server.GameStates;

/// <summary>
/// Wolfgate: with net.pvs_force_delta a force-sent grid is only added to a client's state when it changed, and the
/// client keeps holding it in between. See PvsSystem.ForceDelta.cs.
/// </summary>
public sealed class PvsForceDeltaTest : RobustIntegrationTest
{
#if DEBUG
    [Test]
    public async Task IdleGridsAreHeldAndChangesStillArrive()
    {
        await using var pair = await StartConnectedPair();
        var (client, server) = pair;

        var sEntMan = server.ResolveDependency<IEntityManager>();
        var confMan = server.ResolveDependency<IConfigurationManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var mapSys = server.System<SharedMapSystem>();
        var metaSys = server.System<MetaDataSystem>();
        var pvs = server.System<PvsSystem>();
        var pvsOverride = server.System<PvsOverrideSystem>();
        var cEntMan = client.ResolveDependency<IEntityManager>();
        var stateMan = client.ResolveDependency<Robust.Client.GameStates.IClientGameStateManager>();

        // Evict after 10 ticks out of view, so a held grid that the sweep did not skip would be deleted.
        var tickRate = server.Timing.TickRate;
        server.Post(() =>
        {
            confMan.SetCVar(CVars.NetPVS, true);
            confMan.SetCVar(CVars.NetPvsForceDelta, true);
            confMan.SetCVar(CVars.NetPvsEvictSeconds, 10f / tickRate);
            confMan.SetCVar(CVars.NetPvsEvictInterval, 1f / tickRate);
        });

        var fullStates = 0;
        stateMan.GameStateApplied += args =>
        {
            if (args.AppliedState.FromSequence == GameTick.Zero)
                fullStates++;
        };

        await RunTicksSync(server, client, 10);

        EntityUid map = default;
        EntityUid idle = default;
        EntityUid changing = default;
        EntityUid limited = default;
        EntityUid doomed = default;
        ICommonSession session = default!;
        await server.WaitPost(() =>
        {
            map = mapSys.CreateMap();
            idle = MakeGrid(new Vector2(0, 0));
            changing = MakeGrid(new Vector2(20, 0));
            limited = MakeGrid(new Vector2(40, 0));
            doomed = MakeGrid(new Vector2(60, 0));

            // Far from every grid, so nothing arrives through a visible chunk.
            var playerUid = sEntMan.SpawnEntity(null, new EntityCoordinates(map, new Vector2(500, 500)));
            session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, playerUid);
            sPlayerMan.JoinGame(session);
        });

        EntityUid MakeGrid(Vector2 at)
        {
            var grid = mapSys.CreateGridEntity(map);
            mapSys.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
            sEntMan.System<SharedTransformSystem>().SetWorldPosition(grid.Owner, at);
            return grid.Owner;
        }

        var nIdle = sEntMan.GetNetEntity(idle);
        var nChanging = sEntMan.GetNetEntity(changing);
        var nLimited = sEntMan.GetNetEntity(limited);
        var nDoomed = sEntMan.GetNetEntity(doomed);

        await RunTicksSync(server, client, 40);
        var initialFullStates = fullStates;

        await AssertHeld(nIdle);
        await AssertHeld(nChanging);
        await AssertHeld(nLimited);
        await AssertHeld(nDoomed);

        // Quiet grids are no longer in the state the server builds each tick.
        await server.WaitPost(() =>
        {
            var data = pvs.PlayerData[session];
            Assert.That(data.PreviouslySent.TryGetValue(server.Timing.CurTick - 1, out var sent), "no state last tick");
            var index = sEntMan.GetComponent<MetaDataComponent>(idle).PvsData;
            Assert.That(sent!.Contains(index), Is.False, "an unchanged force-sent grid was added to the state");
        });

        // A change on a held grid still reaches the client.
        await server.WaitPost(() => metaSys.SetEntityName(changing, "renamed"));
        await RunTicksSync(server, client, 10);
        await client.WaitPost(() =>
        {
            var uid = cEntMan.GetEntity(nChanging);
            Assert.That(cEntMan.GetComponent<MetaDataComponent>(uid).EntityName, Is.EqualTo("renamed"));
        });
        await AssertHeld(nChanging);

        // A grid made later arrives.
        EntityUid late = default;
        await server.WaitPost(() => late = MakeGrid(new Vector2(80, 0)));
        var nLate = sEntMan.GetNetEntity(late);
        await RunTicksSync(server, client, 10);
        await AssertHeld(nLate);

        // A grid taken out of the force-send list leaves the client; a deleted one is deleted.
        await server.WaitPost(() =>
        {
            pvsOverride.SetRangeLimited(limited, true);
            sEntMan.DeleteEntity(doomed);
        });
        await RunTicksSync(server, client, 10);
        Assert.That(cEntMan.TryGetEntity(nLimited, out _), Is.False, "client kept a grid that left the force-send list");
        Assert.That(cEntMan.TryGetEntity(nDoomed, out _), Is.False, "client kept a deleted grid");

        // And comes back when it rejoins the list.
        await server.WaitPost(() => pvsOverride.SetRangeLimited(limited, false));
        await RunTicksSync(server, client, 10);
        await AssertHeld(nLimited);

        // Long past the eviction age, the untouched grids are still there.
        await RunTicksSync(server, client, 40);
        await AssertHeld(nIdle);
        await AssertHeld(nChanging);
        await AssertHeld(nLate);

        // Turning it off goes back to sending the whole list.
        await server.WaitPost(() => confMan.SetCVar(CVars.NetPvsForceDelta, false));
        await RunTicksSync(server, client, 30);
        await AssertHeld(nIdle);
        await server.WaitPost(() =>
        {
            var data = pvs.PlayerData[session];
            Assert.That(data.PreviouslySent.TryGetValue(server.Timing.CurTick - 1, out var sent));
            Assert.That(sent!.Contains(sEntMan.GetComponent<MetaDataComponent>(idle).PvsData), Is.True);
        });

        Assert.That(fullStates, Is.EqualTo(initialFullStates), "client requested a full state");

        async Task AssertHeld(NetEntity net)
        {
            await client.WaitPost(() =>
            {
                Assert.That(cEntMan.TryGetEntityData(net, out _, out var meta), $"client does not hold {net}");
                Assert.That(meta!.Flags & MetaDataFlags.Detached, Is.EqualTo(MetaDataFlags.None), $"{net} is detached");
            });
        }
    }
#endif
}
