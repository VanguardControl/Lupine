using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Robust.Shared.Network;

/// <summary>
/// Wolfgate: counts what the server sends, by message, by network event, and (sampled) by component and entity
/// prototype inside game states. Off until started; every recorder returns at once while off.
/// </summary>
public sealed class NetTrafficStats
{
    /// <summary>One counter: how many and how many bytes.</summary>
    public sealed class Tally
    {
        public long Count;
        public long Bytes;

        public void Add(long bytes)
        {
            Interlocked.Increment(ref Count);
            Interlocked.Add(ref Bytes, bytes);
        }
    }

    private volatile bool _enabled;
    private int _sampleEvery = 10;
    private DateTime _started;
    private DateTime? _stopped;
    private long _ticks;
    private long _sampledTicks;

    /// <summary>Whether counting is on.</summary>
    public bool Enabled => _enabled;

    /// <summary>Whether game states built this tick are broken down by component and prototype.</summary>
    public bool SampleThisTick { get; private set; }

    /// <summary>Packets by message name; network events by <c>ev:</c> and their type.</summary>
    public readonly ConcurrentDictionary<string, Tally> Messages = new();

    /// <summary>Uncompressed component state bytes inside game states, on sampled ticks only.</summary>
    public readonly ConcurrentDictionary<string, Tally> Components = new();

    /// <summary>Entity states inside game states by prototype, on sampled ticks only. Bytes are the sum of their components.</summary>
    public readonly ConcurrentDictionary<string, Tally> Prototypes = new();

    /// <summary>Game states before compression, and the entity states and deletions in them.</summary>
    public readonly Tally RawStates = new();
    public readonly Tally EntityStates = new();
    public readonly Tally Deletions = new();

    /// <summary>Clears every counter and starts counting, breaking down one game state tick in <paramref name="sampleEvery"/>.</summary>
    public void Start(int sampleEvery)
    {
        Messages.Clear();
        Components.Clear();
        Prototypes.Clear();
        Reset(RawStates);
        Reset(EntityStates);
        Reset(Deletions);
        _sampleEvery = Math.Max(1, sampleEvery);
        _ticks = 0;
        _sampledTicks = 0;
        _started = DateTime.UtcNow;
        _stopped = null;
        _enabled = true;
    }

    /// <summary>Stops counting and keeps the counts for a report.</summary>
    public void Stop()
    {
        if (!_enabled)
            return;

        _enabled = false;
        SampleThisTick = false;
        _stopped = DateTime.UtcNow;
    }

    /// <summary>Called once per tick on the main thread before game states are built.</summary>
    public void BeginTick()
    {
        if (!_enabled)
            return;

        _ticks++;
        SampleThisTick = _ticks % _sampleEvery == 0;
        if (SampleThisTick)
            _sampledTicks++;
    }

    /// <summary>Records one packet about to be sent.</summary>
    public void RecordSend(NetMessage message, int bytes)
    {
        if (!_enabled)
            return;

        var key = message is Messages.MsgEntity { SystemMessage: { } ev } ? "ev:" + ev.GetType().Name : message.MsgName;
        Messages.GetOrAdd(key, static _ => new Tally()).Add(bytes);
    }

    public void RecordComponent(string name, long bytes)
    {
        Components.GetOrAdd(name, static _ => new Tally()).Add(bytes);
    }

    public void RecordPrototype(string id, long bytes)
    {
        Prototypes.GetOrAdd(id, static _ => new Tally()).Add(bytes);
    }

    /// <summary>A report of the busiest entries, as rates over the time counted.</summary>
    public string Report(int top, int players)
    {
        var seconds = Math.Max(0.001, ((_stopped ?? DateTime.UtcNow) - _started).TotalSeconds);
        // Sampled tallies cover one tick in N; scale them up to the whole window.
        var scale = _sampledTicks == 0 ? 0 : (double) _ticks / _sampledTicks;
        var sb = new StringBuilder();
        var sent = Messages.Values.Sum(t => t.Bytes);
        sb.AppendLine($"Net stats over {seconds:0.0} s{(_enabled ? " (running)" : "")}, {players} players, " +
                      $"{_sampledTicks}/{_ticks} ticks broken down.");
        sb.AppendLine($"Sent {Kb(sent / seconds)}/s in all, {Kb(sent / seconds / Math.Max(1, players))}/s per player. " +
                      $"Game states {Kb(RawStates.Bytes / seconds)}/s before compression, " +
                      $"{EntityStates.Count / seconds:0} entity states/s, {Deletions.Count / seconds:0} deletions/s.");
        Section("Messages and network events", Messages, 1);
        Section("Components in game states (uncompressed, estimated)", Components, scale);
        Section("Entity states by prototype (uncompressed, estimated)", Prototypes, scale);
        return sb.ToString();

        void Section(string title, ConcurrentDictionary<string, Tally> tallies, double factor)
        {
            sb.AppendLine(title + ":");
            foreach (var (key, tally) in tallies.OrderByDescending(p => p.Value.Bytes).Take(top))
            {
                sb.AppendLine($"  {Kb(tally.Bytes * factor / seconds),12}/s {tally.Count * factor / seconds,10:0.0}/s  {key}");
            }
        }

        static string Kb(double bytes) => $"{bytes / 1024:0.0} KB";
    }

    private static void Reset(Tally tally)
    {
        tally.Count = 0;
        tally.Bytes = 0;
    }
}
