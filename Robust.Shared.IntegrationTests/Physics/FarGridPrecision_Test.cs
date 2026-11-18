using System;
using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

/// <summary>
/// Wolfgate: a grid 76 km from the map origin, where a float is only good to 8 mm, must move and carry what touches
/// it the way the same grid does at the origin. A grid keeps the position the solver reached in doubles between steps
/// (physics.precise_root_positions) and far contacts are built around their first body (physics.precise_contacts).
/// </summary>
[TestFixture, TestOf(typeof(SharedPhysicsSystem))]
internal sealed class FarGridPrecision_Test
{
    private const float Dt = 1f / 30f;
    private const int Steps = 300;

    // Multiples of 1/64 m, which a float holds exactly out to 131 km.
    private static readonly Vector2 Far = new(76_000f, 28_120f);

    private sealed class World
    {
        public IEntityManager Ent = default!;
        public SharedPhysicsSystem Physics = default!;
        public SharedTransformSystem Xform = default!;
        public SharedMapSystem Maps = default!;
        public MapId Map;

        public Entity<MapGridComponent> Grid(Vector2 position, int width, int height)
        {
            var grid = Maps.CreateGridEntity(Map);
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    Maps.SetTile(grid, new Vector2i(x, y), new Tile(1));
                }
            }

            Xform.SetLocalPosition(grid.Owner, position);
            var body = Ent.GetComponent<PhysicsComponent>(grid);
            var manager = Ent.GetComponent<FixturesComponent>(grid);
            foreach (var (id, fixture) in manager.Fixtures)
            {
                Physics.SetDensity(grid, id, fixture, 40f, false, manager);
            }

            Physics.ResetMassData(grid, manager, body);
            Physics.SetBodyType(grid, BodyType.Dynamic, body: body);
            Physics.SetFixedRotation(grid, false, body: body);
            Physics.SetLinearDamping(grid, body, 0f);
            Physics.SetAngularDamping(grid, body, 0f);
            Physics.SetSleepingAllowed(grid, body, false);
            Physics.WakeBody(grid, body: body);
            return grid;
        }

        public EntityUid Box(EntityCoordinates coords, BodyType type, float half, float density)
        {
            var uid = Ent.SpawnEntity(null, coords);
            var body = Ent.AddComponent<PhysicsComponent>(uid);
            Physics.SetBodyType(uid, type, body: body);
            Physics.SetLinearDamping(uid, body, 0f);
            Physics.SetAngularDamping(uid, body, 0f);
            Physics.SetSleepingAllowed(uid, body, false);
            var shape = new PolygonShape();
            shape.SetAsBox(half, half);
            Ent.System<FixtureSystem>().CreateFixture(uid, "fix1", new Fixture(shape, 1, 1, true, density), body: body);
            Physics.SetCanCollide(uid, true, body: body);
            Physics.WakeBody(uid, body: body);
            return uid;
        }
    }

    private static World NewWorld()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var ent = sim.Resolve<IEntityManager>();
        return new World
        {
            Ent = ent,
            Physics = ent.System<SharedPhysicsSystem>(),
            Xform = ent.System<SharedTransformSystem>(),
            Maps = ent.System<SharedMapSystem>(),
            Map = sim.CreateMap().MapId,
        };
    }

    /// <summary>
    /// 5 cm/s is under a millimetre a physics step, far less than the 8 mm between floats, so every step used to
    /// round back to where it started.
    /// </summary>
    [Test]
    public void SlowFarGridStillMoves()
    {
        var world = NewWorld();
        var grid = world.Grid(Far, 10, 6);
        var xform = world.Ent.GetComponent<TransformComponent>(grid);
        var velocity = new Vector2(0.05f, 0.02f);
        world.Physics.SetLinearVelocity(grid, velocity);

        for (var i = 0; i < Steps; i++)
        {
            world.Physics.Update(Dt);
        }

        var moved = xform.LocalPosition - Far;
        var expected = velocity * Dt * Steps;
        Assert.Multiple(() =>
        {
            Assert.That(moved.X, Is.EqualTo(expected.X).Within(0.008f));
            Assert.That(moved.Y, Is.EqualTo(expected.Y).Within(0.008f));
        });
    }

    /// <summary>
    /// A turning grid's transform swings around its centre of mass, so each step was rounded differently and the
    /// centre wandered off its line: 44 cm in 10 s.
    /// </summary>
    [Test]
    public void TurningFarGridHoldsItsCourse()
    {
        var world = NewWorld();
        var grid = world.Grid(Far, 40, 12);
        var body = world.Ent.GetComponent<PhysicsComponent>(grid);
        var xform = world.Ent.GetComponent<TransformComponent>(grid);
        var velocity = new Vector2(7.3f, 3.1f);
        world.Physics.SetLinearVelocity(grid, velocity, body: body);
        world.Physics.SetAngularVelocity(grid, 0.2f, body: body);

        var start = Centre(xform, body);

        for (var i = 0; i < Steps; i++)
        {
            world.Physics.Update(Dt);
        }

        var end = Centre(xform, body);
        Assert.Multiple(() =>
        {
            Assert.That(end.X - start.X, Is.EqualTo((double) velocity.X * Dt * Steps).Within(0.012));
            Assert.That(end.Y - start.Y, Is.EqualTo((double) velocity.Y * Dt * Steps).Within(0.012));
        });
    }

    /// <summary>
    /// A crate held against a wall of its own ship while the ship flies and turns. The rounding of both world
    /// positions used to land in the contact points, kicking the crate up to 10 cm/s and 8 mm about.
    /// </summary>
    [Test]
    public void CrateAgainstWallOnFarMovingGridStaysPut()
    {
        var world = NewWorld();
        var grid = world.Grid(Far, 12, 4);
        world.Xform.SetLocalRotation(grid.Owner, Angle.FromDegrees(30));
        world.Physics.SetLinearVelocity(grid, new Vector2(7.3f, 3.1f));
        world.Physics.SetAngularVelocity(grid, 0.1f);
        var gridXform = world.Ent.GetComponent<TransformComponent>(grid);

        world.Box(new EntityCoordinates(grid.Owner, new Vector2(6.5f, 1.5f)), BodyType.Static, 0.5f, 0f);
        var crate = world.Box(new EntityCoordinates(grid.Owner, new Vector2(5.3f, 1.5f)), BodyType.Dynamic, 0.35f, 20f);
        var body = world.Ent.GetComponent<PhysicsComponent>(crate);
        var xform = world.Ent.GetComponent<TransformComponent>(crate);

        var min = float.MaxValue;
        var max = float.MinValue;
        var topSpeed = 0f;

        for (var i = 0; i < 240; i++)
        {
            world.Physics.ApplyForce(crate, gridXform.LocalRotation.RotateVec(new Vector2(body.Mass * 6f, 0f)), body: body);
            world.Physics.Update(Dt);

            // Give it two seconds to reach the wall and settle.
            if (i < 60)
                continue;

            min = MathF.Min(min, xform.LocalPosition.X);
            max = MathF.Max(max, xform.LocalPosition.X);
            topSpeed = MathF.Max(topSpeed, body.LinearVelocity.Length());
        }

        Assert.Multiple(() =>
        {
            Assert.That(body.ContactCount, Is.EqualTo(1), "the crate never reached the wall");
            Assert.That(max - min, Is.LessThan(2e-4f), "the crate moved about against the wall");
            Assert.That(topSpeed, Is.LessThan(1e-3f), "the crate was kicked");
        });
    }

    /// <summary>
    /// Two docked grids under thrust, turning until their hulls meet. Rounded apart every tick, the far pair was
    /// shaken by its own joint and hit a tick out of step: 3.7 cm/s and 0.022 rad/s away from the same pair at the
    /// origin.
    /// </summary>
    [Test]
    public void DockedFarGridsMoveLikeTheSamePairAtTheOrigin()
    {
        var near = RunDocked(Vector2.Zero);
        var far = RunDocked(Far);
        var linear = 0f;
        var angular = 0f;

        for (var i = 0; i < Steps; i++)
        {
            linear = MathF.Max(linear, (far.Linear[i] - near.Linear[i]).Length());
            angular = MathF.Max(angular, MathF.Abs(far.Angular[i] - near.Angular[i]));
        }

        Assert.Multiple(() =>
        {
            Assert.That(linear, Is.LessThan(0.006f), "linear velocity strayed from the run at the origin");
            Assert.That(angular, Is.LessThan(0.004f), "angular velocity strayed from the run at the origin");
        });
    }

    /// <summary>
    /// The carried position only stands while the float still matches it, so moving a grid by hand starts it afresh.
    /// </summary>
    [Test]
    public void MovedByHandFarGridStartsFromItsNewPosition()
    {
        var world = NewWorld();
        var grid = world.Grid(Far, 10, 6);
        var xform = world.Ent.GetComponent<TransformComponent>(grid);
        world.Physics.SetLinearVelocity(grid, new Vector2(0.05f, 0.02f));

        for (var i = 0; i < 10; i++)
        {
            world.Physics.Update(Dt);
        }

        // One float along from where it is: closer than the fraction the solver is carrying could ever account for.
        var moved = new Vector2(MathF.BitIncrement(xform.LocalPosition.X), xform.LocalPosition.Y);
        world.Xform.SetLocalPosition(grid.Owner, moved);
        world.Physics.SetLinearVelocity(grid, Vector2.Zero);
        world.Physics.Update(Dt);

        Assert.That(xform.LocalPosition, Is.EqualTo(moved));

        // And it drifts on from there.
        world.Physics.SetLinearVelocity(grid, new Vector2(0.05f, 0f));

        for (var i = 0; i < Steps; i++)
        {
            world.Physics.Update(Dt);
        }

        Assert.That(xform.LocalPosition.X - moved.X, Is.EqualTo(0.05f * Dt * Steps).Within(0.008f));
    }

    private static (double X, double Y) Centre(TransformComponent xform, PhysicsComponent body)
    {
        var (sin, cos) = Math.SinCos(xform.LocalRotation.Theta);
        var centre = body.LocalCenter;
        return (xform.LocalPosition.X + cos * centre.X - sin * centre.Y, xform.LocalPosition.Y + sin * centre.X + cos * centre.Y);
    }

    private static (Vector2[] Linear, float[] Angular) RunDocked(Vector2 position)
    {
        var world = NewWorld();
        var a = world.Grid(position, 20, 10);
        var b = world.Grid(position + new Vector2(21f, 0f), 14, 10);
        var bodyA = world.Ent.GetComponent<PhysicsComponent>(a);
        var bodyB = world.Ent.GetComponent<PhysicsComponent>(b);

        // The docking joint as content sets it up.
        SharedJointSystem.LinearStiffness(0.5f, 2f, bodyA.FixturesMass, bodyB.FixturesMass, out var stiffness, out var damping);
        var joint = world.Ent.System<SharedJointSystem>().GetOrCreateWeldJoint(a, b, "dock");
        joint.LocalAnchorA = new Vector2(20.5f, 5f);
        joint.LocalAnchorB = new Vector2(-0.5f, 5f);
        joint.ReferenceAngle = 0f;
        joint.CollideConnected = true;
        joint.Stiffness = stiffness;
        joint.Damping = damping;

        var linear = new Vector2[Steps];
        var angular = new float[Steps];

        for (var i = 0; i < Steps; i++)
        {
            world.Physics.ApplyForce(a, new Vector2(bodyA.Mass * 1.5f, bodyA.Mass * 0.4f), body: bodyA);
            world.Physics.ApplyTorque(a, bodyA.Inertia * 0.02f, body: bodyA);
            world.Physics.Update(Dt);
            linear[i] = bodyA.LinearVelocity;
            angular[i] = bodyA.AngularVelocity;
        }

        return (linear, angular);
    }
}
