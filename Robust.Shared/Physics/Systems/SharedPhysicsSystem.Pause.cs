using Robust.Shared.GameObjects;
using Robust.Shared.Physics.Components;

namespace Robust.Shared.Physics.Systems;

// Wolfgate: a paused entity's body used to stay in AwakeBodies, walked by ClearForces, friction and CollideContacts
// every tick for the whole pause. Pausing now puts an awake body to sleep and unpausing wakes it again.
public abstract partial class SharedPhysicsSystem
{
    /// <summary>See <see cref="CVars.PhysicsSleepPausedBodies"/>.</summary>
    private bool _sleepPausedBodies;

    private void OnPhysicsPaused(EntityUid uid, PhysicsComponent body, ref EntityPausedEvent args)
    {
        if (!_sleepPausedBodies || !body.Awake || body.BodyType == BodyType.Static)
            return;

        body.WakeOnUnpause = true;
        SetAwake((uid, body), false);
    }

    private void OnPhysicsUnpaused(EntityUid uid, PhysicsComponent body, ref EntityUnpausedEvent args)
    {
        if (!body.WakeOnUnpause)
            return;

        body.WakeOnUnpause = false;
        if (body.BodyType != BodyType.Static)
            SetAwake((uid, body), true);
    }
}
