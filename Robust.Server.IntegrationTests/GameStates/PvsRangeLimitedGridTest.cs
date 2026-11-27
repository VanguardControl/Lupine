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

    /// <summary>
    /// The grid's own entities come back with it. The client works through departures a few a tick and deletes the
    /// grid the tick after it leaves view; a child it hadn't reached was moved to nullspace unmarked and never
    /// re-attached, so a returning player saw the floor and none of the walls or objects.
    /// </summary>
    [Test]
    public async Task RangeLimitedGridChildrenComeBackAttached()
    {
        await using var pair = await StartConnectedPair();
        var (client, server) = pair;

        var sEntMan = server.ResolveDependency<IEntityManager>();
        var confMan = server.ResolveDependency<IConfigurationManager>();
        var cConfMan = client.ResolveDependency<IConfigurationManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var xforms = sEntMan.System<SharedTransformSystem>();
        var mapSys = server.System<SharedMapSystem>();
        var pvsOverride = server.System<PvsOverrideSystem>();
        var cEntMan = client.ResolveDependency<IEntityManager>();

        server.Post(() => confMan.SetCVar(CVars.NetPVS, true));

        // One departure a tick, so the grid's deletion reaches the client before its children's departures do.
        client.Post(() => cConfMan.SetCVar(CVars.NetPVSEntityExitBudget, 1));

        await RunTicksSync(server, client, 10);

        EntityUid grid = default;
        NetEntity gridNet = default, looseNet = default, anchoredNet = default, nestedNet = default, player = default;
        EntityCoordinates onGrid = default;
        EntityCoordinates farAway = default;
        await server.WaitPost(() =>
        {
            var map = mapSys.CreateMap();
            var gridEnt = mapSys.CreateGridEntity(map);
            grid = gridEnt.Owner;
            gridNet = sEntMan.GetNetEntity(grid);
            for (var x = 0; x < 3; x++)
            {
                mapSys.SetTile(grid, gridEnt.Comp, new Vector2i(x, 0), new Tile(1));
            }

            pvsOverride.SetRangeLimited(grid, true);

            onGrid = new EntityCoordinates(grid, new Vector2(0.5f, 0.5f));
            farAway = new EntityCoordinates(map, new Vector2(200, 200));

            var loose = sEntMan.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(1.5f, 0.5f)));
            looseNet = sEntMan.GetNetEntity(loose);
            nestedNet = sEntMan.GetNetEntity(sEntMan.SpawnEntity(null, new EntityCoordinates(loose, Vector2.Zero)));

            var anchored = sEntMan.SpawnEntity(null, new EntityCoordinates(grid, new Vector2(2.5f, 0.5f)));
            xforms.AnchorEntity(anchored);
            anchoredNet = sEntMan.GetNetEntity(anchored);

            var playerUid = sEntMan.SpawnEntity(null, onGrid);
            player = sEntMan.GetNetEntity(playerUid);

            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, playerUid);
            sPlayerMan.JoinGame(session);
        });

        await RunTicksSync(server, client, 10);
        foreach (var (net, what) in new[] { (looseNet, "loose entity"), (nestedNet, "nested entity"), (anchoredNet, "anchored entity") })
        {
            Assert.That(cEntMan.TryGetEntity(net, out _), $"client never received the {what}");
        }

        // Walk away: the grid and everything the client holds on it are deleted there.
        await server.WaitPost(() => xforms.SetCoordinates(sEntMan.GetEntity(player), farAway));
        await RunTicksSync(server, client, 20);
        Assert.That(cEntMan.TryGetEntity(gridNet, out _), Is.False, "client kept the grid after it left view");
        foreach (var (net, what) in new[] { (looseNet, "loose entity"), (nestedNet, "nested entity"), (anchoredNet, "anchored entity") })
        {
            Assert.That(cEntMan.TryGetEntity(net, out _), Is.False, $"client kept the {what} after its grid was deleted");
        }

        // Come back: every child arrives attached where the server has it.
        await server.WaitPost(() => xforms.SetCoordinates(sEntMan.GetEntity(player), onGrid));
        await RunTicksSync(server, client, 20);
        await client.WaitPost(() =>
        {
            Assert.That(cEntMan.TryGetEntity(gridNet, out var cGrid), "client did not get the grid back");
            Assert.That(cEntMan.TryGetEntity(looseNet, out var cLoose), "client did not get the loose entity back");

            var expected = new[]
            {
                (looseNet, cGrid, "loose entity"),
                (anchoredNet, cGrid, "anchored entity"),
                (nestedNet, cLoose, "nested entity"),
            };

            foreach (var (net, parent, what) in expected)
            {
                Assert.That(cEntMan.TryGetEntityData(net, out var uid, out var meta), $"client did not get the {what} back");
                var xform = cEntMan.GetComponent<TransformComponent>(uid!.Value);
                Assert.That(xform.ParentUid, Is.EqualTo(parent!.Value), $"the {what} is not attached where the server has it");
                Assert.That(meta!.Flags & MetaDataFlags.Detached, Is.EqualTo(MetaDataFlags.None), $"the {what} is still marked out of view");
            }

            Assert.That(cEntMan.GetComponent<TransformComponent>(cEntMan.GetEntity(anchoredNet)).Anchored,
                "the anchored entity is not anchored");
        });

        client.Post(() => cConfMan.SetCVar(CVars.NetPVSEntityExitBudget, CVars.NetPVSEntityExitBudget.DefaultValue));
    }
#endif
}
