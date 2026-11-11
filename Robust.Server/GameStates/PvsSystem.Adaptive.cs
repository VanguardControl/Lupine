using System;
using System.Collections.Generic;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Threading;
using Robust.Shared.Utility;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Robust.Server.GameStates;

// Wolfgate: a client on a lossy link gets game states less often until the link recovers. Each state is a delta from
// the client's last ack, so a skipped tick's changes ride in the next state; a stock client already steps over a
// missing tick to the next state it has.
internal sealed partial class PvsSystem
{
    /// <summary>Seconds a link must stay clean before its interval drops one step.</summary>
    private const int AdaptiveRecoverSeconds = 5;

    private sealed class AdaptiveLink
    {
        public int Interval = 1;
        public int CleanSeconds;
        public long Sent;
        public long Resent;
        public int Phase;
    }

    private readonly Dictionary<ICommonSession, AdaptiveLink> _adaptive = new();
    private readonly List<PvsSession> _skipped = new();
    private readonly List<ICommonSession> _sendNow = new();
    private int _adaptiveMax;
    private float _adaptiveResendRatio;
    private int _adaptiveAckTicks;
    private GameTick _adaptiveNextCheck;

    private void InitAdaptive()
    {
        Subs.CVar(_configManager, CVars.NetAdaptiveStateMaxInterval, (int v) => _adaptiveMax = v, true);
        Subs.CVar(_configManager, CVars.NetAdaptiveStateResendRatio, (float v) => _adaptiveResendRatio = v, true);
        Subs.CVar(_configManager, CVars.NetAdaptiveStateAckTicks, (int v) => _adaptiveAckTicks = v, true);
    }

    /// <summary>The players to send a state to this tick; the rest wait for their next turn.</summary>
    private ICommonSession[] FilterAdaptive(ICommonSession[] players)
    {
        _skipped.Clear();
        if (_adaptiveMax <= 1)
        {
            _adaptive.Clear();
            return players;
        }

        var tick = _gameTiming.CurTick;
        if (tick >= _adaptiveNextCheck)
        {
            _adaptiveNextCheck = tick + (uint) Math.Max(1, (int) _gameTiming.TickRate);
            CheckLinks(players);
        }

        _sendNow.Clear();
        foreach (var session in players)
        {
            if (!_adaptive.TryGetValue(session, out var link) || link.Interval <= 1
                || (tick.Value + (uint) link.Phase) % (uint) link.Interval == 0)
            {
                _sendNow.Add(session);
                continue;
            }

            var data = GetOrNewPvsSession(session);
            // A client waiting on a full state is never kept waiting, nor is one that has never acked.
            if (data.RequestedFull || data.LastReceivedAck == GameTick.Zero)
            {
                _sendNow.Add(session);
                continue;
            }

            _skipped.Add(data);
        }

        return _skipped.Count == 0 ? players : _sendNow.ToArray();
    }

    /// <summary>Re-rates each player's link from its resends over the last second and how far its acks trail.</summary>
    private void CheckLinks(ICommonSession[] players)
    {
        var net = _netMan as NetManager;
        var tick = _gameTiming.CurTick;
        var tickMs = 1000f / Math.Max(1, (int) _gameTiming.TickRate);
        foreach (var session in players)
        {
            if (!_adaptive.TryGetValue(session, out var link))
            {
                _adaptive[session] = link = new AdaptiveLink { Phase = _adaptive.Count };
                if (net != null && net.TryGetChannelResends(session.Channel, out var s, out var r))
                    (link.Sent, link.Resent) = (s, r);
                continue;
            }

            var lossy = false;
            if (net != null && net.TryGetChannelResends(session.Channel, out var sent, out var resent))
            {
                var dSent = sent - link.Sent;
                var dResent = resent - link.Resent;
                (link.Sent, link.Resent) = (sent, resent);
                lossy = dSent >= 30 && dResent > dSent * _adaptiveResendRatio;
            }

            var data = GetOrNewPvsSession(session);
            if (!data.RequestedFull && data.LastReceivedAck != GameTick.Zero)
            {
                var trail = (long) tick.Value - data.LastReceivedAck.Value;
                var expected = session.Channel.Ping / tickMs + link.Interval + 2;
                lossy |= trail - expected > _adaptiveAckTicks;
            }

            // A client that has never acked a state is never slowed: while skipped it would hold every deletion
            // since the round began.
            if (data.RequestedFull || data.LastReceivedAck == GameTick.Zero)
                lossy = false;

            if (lossy)
            {
                link.CleanSeconds = 0;
                link.Interval = Math.Min(_adaptiveMax, link.Interval + 1);
            }
            else if (link.Interval > 1 && ++link.CleanSeconds >= AdaptiveRecoverSeconds)
            {
                link.CleanSeconds = 0;
                link.Interval--;
            }
        }

        // Drop links of players who left.
        if (_adaptive.Count > players.Length)
        {
            var current = new HashSet<ICommonSession>(players);
            foreach (var session in new List<ICommonSession>(_adaptive.Keys))
            {
                if (!current.Contains(session))
                    _adaptive.Remove(session);
            }
        }
    }

    /// <summary>Skipped players still hold deletion history back to their last ack.</summary>
    private void HoldSkippedAcks()
    {
        foreach (var data in _skipped)
        {
            InterlockedHelper.Min(ref _oldestAck, data.LastReceivedAck.Value);
        }
    }

    /// <summary>A player's current state interval, for tests and diagnostics.</summary>
    internal int GetStateInterval(ICommonSession session)
    {
        return _adaptive.TryGetValue(session, out var link) ? link.Interval : 1;
    }

    /// <summary>Sets a player's state interval directly, for tests.</summary>
    internal void SetStateInterval(ICommonSession session, int interval)
    {
        if (!_adaptive.TryGetValue(session, out var link))
            _adaptive[session] = link = new AdaptiveLink();
        link.Interval = interval;
        link.Phase = 0;
    }
}
