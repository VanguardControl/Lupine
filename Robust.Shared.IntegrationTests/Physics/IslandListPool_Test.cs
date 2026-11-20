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
/// Lupine: island lists are kept across steps. A body touching nothing joins the lone island and must hand the
/// lists it was seeded with straight back, or every such body empties the free lists each step.
/// </summary>
[TestFixture, TestOf(typeof(SharedPhysicsSystem))]
internal sealed class IslandListPool_Test
{
    private const float Dt = 1f / 30f;
    private const int LoneBodies = 300;
    private const int Pairs = 40;

    [Test]
    public void SteadySceneMakesNoNewIslandLists()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var ent = sim.Resolve<IEntityManager>();
        var physics = ent.System<SharedPhysicsSystem>();
        var fixtures = ent.System<FixtureSystem>();
        var joints = ent.System<SharedJointSystem>();
        var map = sim.CreateMap().Uid;

        EntityUid Spawn(Vector2 position)
        {
            var uid = ent.SpawnEntity(null, new EntityCoordinates(map, position));
            var body = ent.AddComponent<PhysicsComponent>(uid);
            physics.SetBodyType(uid, BodyType.Dynamic, body: body);
            physics.SetSleepingAllowed(uid, body, false);
            fixtures.CreateFixture(uid, "fix1", new Fixture(new PhysShapeCircle(0.4f), 1, 1, true), body: body);
            physics.SetCanCollide(uid, true, body: body);
            physics.WakeBody(uid, body: body);
            return uid;
        }

        // Lone bodies and jointed pairs, mixed so lone bodies are seeded between islands.
        for (var i = 0; i < LoneBodies; i++)
        {
            Spawn(new Vector2(10f * i, 0f));

            if (i >= Pairs)
                continue;

            var a = Spawn(new Vector2(10f * i, 50f));
            var b = Spawn(new Vector2(10f * i + 2f, 50f));
            joints.GetOrCreateWeldJoint(a, b, "pair");
        }

        for (var i = 0; i < 5; i++)
        {
            physics.Update(Dt);
        }

        var created = physics.IslandListsCreated;

        for (var i = 0; i < 20; i++)
        {
            physics.Update(Dt);
        }

        Assert.That(physics.IslandListsCreated, Is.EqualTo(created), "a steady scene made new island lists");
    }
}
