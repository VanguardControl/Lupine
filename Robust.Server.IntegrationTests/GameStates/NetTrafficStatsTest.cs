using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Player;

namespace Robust.UnitTesting.Server.GameStates;

/// <summary>Wolfgate: the traffic breakdown counts game states by message and, on sampled ticks, by component.</summary>
public sealed class NetTrafficStatsTest : RobustIntegrationTest
{
    [Test]
    public async Task CountsGameStatesAndTheirComponents()
    {
        await using var pair = await StartConnectedPair();
        var (client, server) = pair;
        var sEntMan = server.ResolveDependency<IEntityManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var stats = server.ResolveDependency<NetTrafficStats>();
        await RunTicksSync(server, client, 10);

        await server.WaitPost(() =>
        {
            server.ResolveDependency<IConfigurationManager>().SetCVar(CVars.NetPVS, true);
            var map = server.System<SharedMapSystem>().CreateMap();
            var player = sEntMan.SpawnEntity(null, new EntityCoordinates(map, default));
            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, player);
            sPlayerMan.JoinGame(session);
            stats.Start(1);
        });

        await RunTicksSync(server, client, 10);
        await server.WaitPost(() =>
        {
            stats.RecordSend(new MsgState(), 100);
            stats.RecordSend(new MsgEntity { SystemMessage = new TestStatsEvent() }, 40);
            stats.Stop();
            Assert.That(stats.Messages["MsgState"].Bytes, Is.EqualTo(100));
            Assert.That(stats.Messages["ev:TestStatsEvent"].Count, Is.EqualTo(1), "Network events are counted by type.");
            // Integration pairs skip the real socket, so packets are recorded by hand here.
            Assert.That(stats.RawStates.Bytes, Is.GreaterThan(0), "Game states are counted before compression.");
            Assert.That(stats.Components.ContainsKey("Transform"), "Components in game states are broken down by name.");
            Assert.That(stats.Report(10, 1), Does.Contain("MsgState"));

            var before = stats.Messages["MsgState"].Count;
            stats.BeginTick();
            Assert.That(stats.SampleThisTick, Is.False, "Nothing is sampled once stopped.");
            Assert.That(stats.Messages["MsgState"].Count, Is.EqualTo(before));
        });
    }

    private sealed class TestStatsEvent : EntityEventArgs;
}
