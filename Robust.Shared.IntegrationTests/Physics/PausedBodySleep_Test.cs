using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

/// <summary>
/// Wolfgate: pausing an entity puts its awake body to sleep so it leaves the solver's awake set, and unpausing wakes
/// it again. A body that was already asleep is left alone.
/// </summary>
[TestFixture, TestOf(typeof(SharedPhysicsSystem))]
internal sealed class PausedBodySleep_Test
{
    [Test]
    public void PausedAwakeBodySleepsAndWakesOnUnpause()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entManager = sim.Resolve<IEntityManager>();
        var fixtures = entManager.System<FixtureSystem>();
        var physics = entManager.System<SharedPhysicsSystem>();
        var metaData = entManager.System<MetaDataSystem>();

        var map = sim.CreateMap();

        var awake = SpawnBody(entManager, fixtures, physics, map.Uid, Vector2.Zero);
        var asleep = SpawnBody(entManager, fixtures, physics, map.Uid, new Vector2(5f, 0f));
        physics.SetAwake(asleep, false);

        Assert.That(awake.Comp.Awake, Is.True);
        Assert.That(asleep.Comp.Awake, Is.False);
        Assert.That(physics.AwakeBodies.Count, Is.EqualTo(1));

        metaData.SetEntityPaused(awake, true);
        metaData.SetEntityPaused(asleep, true);

        Assert.Multiple(() =>
        {
            Assert.That(awake.Comp.Awake, Is.False, "paused body stayed awake");
            Assert.That(awake.Comp.WakeOnUnpause, Is.True);
            Assert.That(asleep.Comp.WakeOnUnpause, Is.False, "a body already asleep must not be woken by unpausing");
            Assert.That(physics.AwakeBodies.Count, Is.EqualTo(0), "paused bodies left in the awake set");
        });

        metaData.SetEntityPaused(awake, false);
        metaData.SetEntityPaused(asleep, false);

        Assert.Multiple(() =>
        {
            Assert.That(awake.Comp.Awake, Is.True, "unpaused body did not wake");
            Assert.That(awake.Comp.WakeOnUnpause, Is.False);
            Assert.That(asleep.Comp.Awake, Is.False, "unpausing woke a body that slept before the pause");
            Assert.That(physics.AwakeBodies.Count, Is.EqualTo(1));
        });
    }

    private static Entity<PhysicsComponent> SpawnBody(IEntityManager entManager, FixtureSystem fixtures,
        SharedPhysicsSystem physics, EntityUid map, Vector2 position)
    {
        var uid = entManager.SpawnEntity(null, new EntityCoordinates(map, position));
        var body = entManager.AddComponent<PhysicsComponent>(uid);
        physics.SetBodyType(uid, BodyType.Dynamic, body: body);
        var shape = new PolygonShape();
        shape.SetAsBox(0.25f, 0.25f);
        fixtures.CreateFixture(uid, "fix1", new Fixture(shape, 0, 0, false), body: body);
        physics.SetCanCollide(uid, true, body: body);
        physics.WakeBody(uid, body: body);
        return (uid, body);
    }
}
