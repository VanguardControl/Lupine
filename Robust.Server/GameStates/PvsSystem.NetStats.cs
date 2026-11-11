using System;
using System.IO;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Network;

namespace Robust.Server.GameStates;

// Wolfgate: breaks game states down for NetTrafficStats.
internal sealed partial class PvsSystem
{
    [Dependency] private NetTrafficStats _trafficStats = default!;

    [ThreadStatic] private static MemoryStream? _statsStream;

    /// <summary>Counts a session's serialized game state, and on sampled ticks what is in it.</summary>
    private void RecordTrafficStats(GameState state, MemoryStream? serialized)
    {
        if (!_trafficStats.Enabled)
            return;

        if (serialized != null)
            _trafficStats.RawStates.Add(serialized.Length);
        _trafficStats.EntityStates.Add(state.EntityStates.Value?.Count ?? 0);
        _trafficStats.Deletions.Add(state.EntityDeletions.Value?.Count ?? 0);
        if (!_trafficStats.SampleThisTick || state.EntityStates.Value is not { } entities)
            return;

        var stream = _statsStream ??= new MemoryStream();
        var factory = EntityManager.ComponentFactory;
        foreach (var entity in entities)
        {
            long total = 0;
            if (entity.ComponentChanges.Value is { } changes)
            {
                foreach (var change in changes)
                {
                    if (change.State == null)
                        continue;

                    stream.SetLength(0);
                    _serializer.Serialize(stream, change.State);
                    total += stream.Length;
                    var name = factory.TryGetRegistration(change.NetID, out var reg) ? reg.Name : $"#{change.NetID}";
                    _trafficStats.RecordComponent(name, stream.Length);
                }
            }

            var proto = TryGetEntity(entity.NetEntity, out var uid) && MetaData(uid.Value).EntityPrototype is { } p
                ? p.ID
                : "(no prototype)";
            _trafficStats.RecordPrototype(proto, total);
        }
    }
}
