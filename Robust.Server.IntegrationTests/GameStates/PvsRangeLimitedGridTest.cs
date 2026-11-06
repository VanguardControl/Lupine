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
/// Wolfgate: a range-limited grid is sent only through visible chunks, leaves the client's view like any other
/// entity and comes back when a viewer returns. See PvsOverrideSystem.SetRangeLimited.
/// </summary>
public sealed class PvsRangeLimitedGridTest : RobustIntegrationTest
{
#if DEBUG
    [Test]
    public async Task RangeLimitedGridLeavesAndReentersView()
    {
        await using var pair = await StartConnectedPair();
        var (client, server) = pair;

        var sEntMan = server.ResolveDependency<IEntityManager>();
        var confMan = server.ResolveDependency<IConfigurationManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var xforms = sEntMan.System<SharedTransformSystem>();
        var mapSys = server.System<SharedMapSystem>();
        var pvsOverride = server.System<PvsOverrideSystem>();
        var cEntMan = client.ResolveDependency<IEntityManager>();
        var stateMan = client.ResolveDependency<Robust.Client.GameStates.IClientGameStateManager>();

        server.Post(() => confMan.SetCVar(CVars.NetPVS, true));

        var fullStates = 0;
        stateMan.GameStateApplied += args =>
        {
            if (args.AppliedState.FromSequence == GameTick.Zero)
                fullStates++;
        };

        await RunTicksSync(server, client, 10);

        EntityUid map = default;
        EntityUid grid = default;
        NetEntity gridNet = default;
        NetEntity player = default;
        EntityCoordinates onGrid = default;
        EntityCoordinates farAway = default;
        await server.WaitPost(() =>
        {
            map = mapSys.CreateMap();
            var gridEnt = mapSys.CreateGridEntity(map);
            grid = gridEnt.Owner;
            gridNet = sEntMan.GetNetEntity(grid);
            mapSys.SetTile(grid, gridEnt.Comp, Vector2i.Zero, new Tile(1));
            pvsOverride.SetRangeLimited(grid, true);

            onGrid = new EntityCoordinates(grid, new Vector2(0.5f, 0.5f));
            farAway = new EntityCoordinates(map, new Vector2(200, 200));

            var playerUid = sEntMan.SpawnEntity(null, farAway);
            player = sEntMan.GetNetEntity(playerUid);

            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, playerUid);
            sPlayerMan.JoinGame(session);
        });

        await RunTicksSync(server, client, 10);
        var initialFullStates = fullStates;

        Assert.That(pvsOverride.IsRangeLimited(grid), Is.True);
        Assert.That(cEntMan.TryGetEntity(gridNet, out _), Is.False, "range-limited grid was sent to a far-away client");

        // Step onto the grid: it arrives through the visible chunk.
        await server.WaitPost(() => xforms.SetCoordinates(sEntMan.GetEntity(player), onGrid));
        await RunTicksSync(server, client, 10);

        MetaDataComponent? meta = default!;
        await client.WaitPost(() =>
        {
            Assert.That(cEntMan.TryGetEntityData(gridNet, out _, out meta), "client did not receive the grid");
            Assert.That(meta!.Flags & MetaDataFlags.Detached, Is.EqualTo(MetaDataFlags.None));
        });

        // Walk away: the grid leaves view and the client is told to delete it rather than hold it detached.
        await server.WaitPost(() => xforms.SetCoordinates(sEntMan.GetEntity(player), farAway));
        await RunTicksSync(server, client, 10);
        Assert.That(cEntMan.TryGetEntity(gridNet, out _), Is.False, "client kept the grid after it left view");
        Assert.That(sEntMan.TryGetEntity(gridNet, out _), Is.True, "server deleted the grid itself");

        // Come back: the grid arrives again as a new entity.
        await server.WaitPost(() => xforms.SetCoordinates(sEntMan.GetEntity(player), onGrid));
        await RunTicksSync(server, client, 10);
        await client.WaitPost(() =>
        {
            Assert.That(cEntMan.TryGetEntityData(gridNet, out _, out meta));
            Assert.That(meta!.Flags & MetaDataFlags.Detached, Is.EqualTo(MetaDataFlags.None));
        });

        // Lifting the limit force-sends the grid again.
        await server.WaitPost(() =>
        {
            pvsOverride.SetRangeLimited(grid, false);
            xforms.SetCoordinates(sEntMan.GetEntity(player), farAway);
        });
        await RunTicksSync(server, client, 10);
        await client.WaitPost(() =>
        {
            Assert.That(cEntMan.TryGetEntityData(gridNet, out _, out meta), "force-sent grid missing on client");
            Assert.That(meta!.Flags & MetaDataFlags.Detached, Is.EqualTo(MetaDataFlags.None));
        });

        Assert.That(fullStates, Is.EqualTo(initialFullStates), "client requested a full state");
    }
#endif
}
