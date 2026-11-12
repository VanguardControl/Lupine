using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Client.GameStates;
using Robust.Server.GameStates;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Robust.UnitTesting.Server.GameStates;

/// <summary>
/// Wolfgate: a client on a slowed state rate gets a state every 2nd tick, still follows what moves, and never falls
/// back to a full state. See PvsSystem.Adaptive.cs.
/// </summary>
public sealed class PvsAdaptiveRateTest : RobustIntegrationTest
{
    [Test]
    public async Task SlowedClientGetsEveryOtherStateAndKeepsUp()
    {
        await using var pair = await StartConnectedPair();
        var (client, server) = pair;
        var sEntMan = server.ResolveDependency<IEntityManager>();
        var cEntMan = client.ResolveDependency<IEntityManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var stateMan = (ClientGameStateManager) client.ResolveDependency<IClientGameStateManager>();
        var pvs = sEntMan.System<PvsSystem>();
        var xforms = sEntMan.System<SharedTransformSystem>();

        await server.WaitPost(() =>
        {
            var cfg = server.ResolveDependency<IConfigurationManager>();
            cfg.SetCVar(CVars.NetPVS, true);
            cfg.SetCVar(CVars.NetAdaptiveStateMaxInterval, 2);
        });
        await RunTicksSync(server, client, 10);

        EntityUid mover = default;
        NetEntity moverNet = default;
        ICommonSession session = default!;
        await server.WaitPost(() =>
        {
            var map = server.System<SharedMapSystem>().CreateMap();
            var player = sEntMan.SpawnEntity(null, new EntityCoordinates(map, default));
            mover = sEntMan.SpawnEntity(null, new EntityCoordinates(map, new Vector2(1, 0)));
            moverNet = sEntMan.GetNetEntity(mover);
            session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, player);
            sPlayerMan.JoinGame(session);
        });
        await RunTicksSync(server, client, 10);

        var applied = 0;
        var fullStates = 0;
        stateMan.GameStateApplied += args =>
        {
            applied++;
            if (args.AppliedState.FromSequence == GameTick.Zero)
                fullStates++;
        };

        await server.WaitPost(() => pvs.SetStateInterval(session, 2));
        for (var i = 0; i < 20; i++)
        {
            await server.WaitPost(() => xforms.SetLocalPosition(mover, new Vector2(1 + i * 0.1f, 0)));
            await RunTicksSync(server, client, 1);
        }

        await RunTicksSync(server, client, 4);
        await client.WaitPost(() =>
        {
            Assert.That(fullStates, Is.Zero, "A slowed client never needs a full state.");
            Assert.That(applied, Is.InRange(8, 16), "About every other tick's state reaches the client.");
            Assert.That(cEntMan.TryGetEntity(moverNet, out var cMover));
            var pos = cEntMan.GetComponent<TransformComponent>(cMover!.Value).LocalPosition;
            Assert.That(pos.X, Is.EqualTo(1 + 19 * 0.1f).Within(0.001f), "The client ends where the server put it.");
        });

        await server.WaitPost(() => Assert.That(pvs.GetStateInterval(session), Is.EqualTo(2)));
    }
}
