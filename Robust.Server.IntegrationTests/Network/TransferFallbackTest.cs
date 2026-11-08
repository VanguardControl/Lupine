using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Robust.Server.Network.Transfer;
using Robust.Server.ServerStatus;
using Robust.Shared;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages.Transfer;

namespace Robust.UnitTesting.Server.Network;

/// <summary>
/// Wolfgate: a client that never reaches the websocket transfer channel is switched to the game connection with a
/// second transfer init, which a stock client follows; it is remembered and gets the game connection at once next time.
/// </summary>
[TestFixture, NonParallelizable]
[TestOf(typeof(ServerTransferManager))]
public sealed class TransferFallbackTest
{
    private const float FallbackSeconds = 0.2f;

    private List<MsgTransferInit> _inits = new();
    private ProcessMessage<MsgTransferAckInit>? _ackHandler;
    private Mock<IServerNetManager> _net = default!;
    private Mock<IConfigurationManager> _cfg = default!;
    private ServerTransferManager _manager = default!;

    private void Setup(bool http, bool remember)
    {
        // A fresh list each test: a handshake left pending by an earlier test still falls back later.
        var inits = new List<MsgTransferInit>();
        _inits = inits;
        _ackHandler = null;

        _cfg = new Mock<IConfigurationManager>();
        _cfg.Setup(c => c.GetCVar(CVars.TransferHttp)).Returns(http);
        _cfg.Setup(c => c.GetCVar(CVars.TransferHttpEndpoint)).Returns("http://localhost:1212/");
        _cfg.Setup(c => c.GetCVar(CVars.TransferStreamLimit)).Returns(10);
        _cfg.Setup(c => c.GetCVar(CVars.TransferHttpFallbackSeconds)).Returns(FallbackSeconds);
        _cfg.Setup(c => c.GetCVar(CVars.TransferHttpFallbackRemember)).Returns(remember);

        _net = new Mock<IServerNetManager>();
        _net.Setup(n => n.ServerSendMessage(It.IsAny<NetMessage>(), It.IsAny<INetChannel>()))
            .Callback<NetMessage, INetChannel>((msg, _) =>
            {
                if (msg is MsgTransferInit init)
                {
                    lock (inits)
                    {
                        inits.Add(init);
                    }
                }
            });
        _net.Setup(n => n.RegisterNetMessage(It.IsAny<ProcessMessage<MsgTransferAckInit>?>(), It.IsAny<NetMessageAccept>()))
            .Callback<ProcessMessage<MsgTransferAckInit>?, NetMessageAccept>((handler, _) => _ackHandler = handler);

        var log = new Mock<ILogManager>();
        log.Setup(l => l.GetSawmill(It.IsAny<string>())).Returns(new Mock<ISawmill>().Object);

        _manager = new ServerTransferManager(
            _cfg.Object,
            new Mock<IStatusHost>().Object,
            _net.Object,
            log.Object,
            new Mock<ITaskManager>().Object);
        _manager.Initialize();
    }

    private static Mock<INetChannel> NewChannel(NetUserId user)
    {
        var channel = new Mock<INetChannel>();
        channel.SetupGet(c => c.UserId).Returns(user);
        channel.SetupGet(c => c.IsConnected).Returns(true);
        return channel;
    }

    private void Ack(INetChannel channel)
    {
        Assert.That(_ackHandler, Is.Not.Null, "the manager never registered its ack handler");
        _ackHandler!(new MsgTransferAckInit { MsgChannel = channel });
    }

    private List<MsgTransferInit> Inits()
    {
        lock (_inits)
        {
            return _inits.ToList();
        }
    }

    [Test]
    public async Task UnreachedSocketFallsBackAndIsRemembered()
    {
        Setup(http: true, remember: true);
        var user = new NetUserId(Guid.NewGuid());
        var channel = NewChannel(user);

        var handshake = _manager.ServerHandshake(channel.Object);
        Assert.That(Inits(), Has.Count.EqualTo(1));
        Assert.That(Inits()[0].HttpInfo, Is.Not.Null, "the first init should offer the websocket");
        Assert.That(_manager.GetMode(user), Is.EqualTo(TransferChannelMode.WebSocket));

        // Nothing connects the socket; past the timeout a second init without it goes out.
        await Task.Delay(TimeSpan.FromSeconds(FallbackSeconds * 4));
        Assert.That(Inits(), Has.Count.EqualTo(2), "no fallback init after the timeout");
        Assert.That(Inits()[1].HttpInfo, Is.Null, "the fallback init must not offer the websocket");
        Assert.That(_manager.GetMode(user), Is.EqualTo(TransferChannelMode.WebSocketFailed));
        Assert.That(handshake.IsCompleted, Is.False, "the handshake waits for the client's ack of the fallback");

        Ack(channel.Object);
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));

        // The player leaves and joins again: straight to the game connection, no websocket offered.
        _net.Raise(n => n.Disconnect += null, new NetDisconnectedArgs(channel.Object, "Disconnected: quit"));
        var second = _manager.ServerHandshake(channel.Object);
        Assert.That(Inits(), Has.Count.EqualTo(3));
        Assert.That(Inits()[2].HttpInfo, Is.Null, "a remembered player was offered the websocket again");
        Assert.That(_manager.GetMode(user), Is.EqualTo(TransferChannelMode.WebSocketFailed));

        Ack(channel.Object);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task TransferSocketKickIsRemembered()
    {
        Setup(http: true, remember: true);
        var user = new NetUserId(Guid.NewGuid());
        var channel = NewChannel(user);

        _ = _manager.ServerHandshake(channel.Object);
        Assert.That(_manager.GetMode(user), Is.EqualTo(TransferChannelMode.WebSocket));

        // A stock client whose socket errors disconnects itself with this reason.
        _net.Raise(n => n.Disconnect += null, new NetDisconnectedArgs(channel.Object, "Error in transfer socket"));

        var next = _manager.ServerHandshake(channel.Object);
        Assert.That(Inits().Last().HttpInfo, Is.Null, "a player kicked by its socket was offered it again");
        Ack(channel.Object);
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ForgettingOffersTheSocketAgain()
    {
        Setup(http: true, remember: false);
        var user = new NetUserId(Guid.NewGuid());
        var channel = NewChannel(user);

        var handshake = _manager.ServerHandshake(channel.Object);
        await Task.Delay(TimeSpan.FromSeconds(FallbackSeconds * 4));
        Ack(channel.Object);
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));

        _net.Raise(n => n.Disconnect += null, new NetDisconnectedArgs(channel.Object, "Disconnected: quit"));
        _ = _manager.ServerHandshake(channel.Object);
        Assert.That(Inits().Last().HttpInfo, Is.Not.Null, "with remembering off the websocket should be offered again");
    }

    [Test]
    public async Task HttpOffUsesTheGameConnection()
    {
        Setup(http: false, remember: true);
        var user = new NetUserId(Guid.NewGuid());
        var channel = NewChannel(user);

        var handshake = _manager.ServerHandshake(channel.Object);
        Assert.That(Inits(), Has.Count.EqualTo(1));
        Assert.That(Inits()[0].HttpInfo, Is.Null);
        Assert.That(_manager.GetMode(user), Is.EqualTo(TransferChannelMode.GameConnection));

        Ack(channel.Object);
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
