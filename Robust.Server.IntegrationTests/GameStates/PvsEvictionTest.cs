using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Client.GameStates;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Robust.UnitTesting.Server.GameStates;

/// <summary>
/// Wolfgate: the server evicts entities a client has held out of view for too long, and sends them as new when
/// they come back. See PvsSystem.Evict.cs.
/// </summary>
public sealed class PvsEvictionTest : RobustIntegrationTest
{
#if DEBUG
    [Test]
    public async Task EvictsLongUnseenEntityAndResendsItInFull()
    {
        await using var pair = await StartConnectedPair();
        var (client, server) = pair;

        var sEntMan = server.ResolveDependency<IEntityManager>();
        var confMan = server.ResolveDependency<IConfigurationManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var xforms = sEntMan.System<SharedTransformSystem>();
        var stateMan = (ClientGameStateManager) client.ResolveDependency<IClientGameStateManager>();
        var cEntMan = client.ResolveDependency<IEntityManager>();

        // Evict after 10 ticks out of view, sweeping every tick.
        var tickRate = server.Timing.TickRate;
        server.Post(() =>
        {
            confMan.SetCVar(CVars.NetPVS, true);
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
        NetEntity player = default;
        NetEntity entity = default;
        EntityCoordinates coords = default;
        await server.WaitPost(() =>
        {
            map = server.System<SharedMapSystem>().CreateMap();
            coords = new(map, default);

            var playerUid = sEntMan.SpawnEntity(null, coords);
            var entUid = sEntMan.SpawnEntity(null, coords);
            entity = sEntMan.GetNetEntity(entUid);
            player = sEntMan.GetNetEntity(playerUid);

            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, playerUid);
            sPlayerMan.JoinGame(session);
        });

        await RunTicksSync(server, client, 10);
        var initialFullStates = fullStates;

        MetaDataComponent? meta = default!;
        await client.WaitPost(() =>
        {
            Assert.That(cEntMan.TryGetEntityData(entity, out _, out meta));
            Assert.That(meta!.Flags & MetaDataFlags.Detached, Is.EqualTo(MetaDataFlags.None));
        });

        // Walk out of range: the client detaches the entity but keeps it.
        var farAway = new EntityCoordinates(map, new Vector2(100, 100));
        await server.WaitPost(() => xforms.SetCoordinates(sEntMan.GetEntity(player), farAway));
        await RunTicksSync(server, client, 5);
        Assert.That(meta!.Flags & MetaDataFlags.Detached, Is.EqualTo(MetaDataFlags.Detached));
        Assert.That(cEntMan.TryGetEntity(entity, out _), Is.True);

        // Stay away past the eviction age: the server tells the client to delete its copy.
        await RunTicksSync(server, client, 30);
        Assert.That(cEntMan.TryGetEntity(entity, out _), Is.False, "client still holds the evicted entity");
        Assert.That(sEntMan.TryGetEntity(entity, out _), Is.True, "server deleted the entity itself");

        // Come back: the entity arrives as a new entity, with no full-state request.
        await server.WaitPost(() => xforms.SetCoordinates(sEntMan.GetEntity(player), coords));
        await RunTicksSync(server, client, 10);

        await client.WaitPost(() =>
        {
            Assert.That(cEntMan.TryGetEntityData(entity, out _, out meta));
            Assert.That(meta!.Flags & MetaDataFlags.Detached, Is.EqualTo(MetaDataFlags.None));
            Assert.That(stateMan.IsQueuedForDetach(entity), Is.False);
        });

        Assert.That(fullStates, Is.EqualTo(initialFullStates), "client requested a full state");
    }
#endif
}
