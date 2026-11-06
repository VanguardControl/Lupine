using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Robust.UnitTesting.Server.Network;

/// <summary>
/// Wolfgate: with the parallel-decrypt batch size at one, every tick of client traffic goes through the parallel path
/// and the connection must stay healthy, states included.
/// </summary>
public sealed class ParallelDecryptTest : RobustIntegrationTest
{
    [Test]
    public async Task ParallelDecryptKeepsTheConnectionHealthy()
    {
        await using var pair = await StartConnectedPair();
        var (client, server) = pair;

        var confMan = server.ResolveDependency<IConfigurationManager>();
        var sEntMan = server.ResolveDependency<IEntityManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var cEntMan = client.ResolveDependency<IEntityManager>();

        server.Post(() =>
        {
            confMan.SetCVar(CVars.NetParallelDecrypt, true);
            confMan.SetCVar(CVars.NetParallelDecryptMin, 1);
        });

        await RunTicksSync(server, client, 5);

        NetEntity player = default;
        await server.WaitPost(() =>
        {
            var map = server.System<SharedMapSystem>().CreateMap();
            var playerUid = sEntMan.SpawnEntity(null, new EntityCoordinates(map, default));
            player = sEntMan.GetNetEntity(playerUid);
            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, playerUid);
            sPlayerMan.JoinGame(session);
        });

        // Plenty of ticks of acks, inputs and states through the batch path.
        await RunTicksSync(server, client, 60);

        var session = sPlayerMan.Sessions.First();
        Assert.That(session.Status, Is.EqualTo(SessionStatus.InGame), "client was disconnected");
        Assert.That(cEntMan.TryGetEntity(player, out _), Is.True, "client never received its own entity");
    }
}
