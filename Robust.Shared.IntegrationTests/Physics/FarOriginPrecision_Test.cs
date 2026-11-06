using System;
using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

/// <summary>
/// Wolfgate: a body on a rotated grid 100 km from the map origin must move in a straight line along the grid.
/// Float world coordinates only hold about a centimetre there, so a solver working in world space jitters and
/// drifts off-axis; the solver now works relative to a double-precision origin.
/// </summary>
[TestFixture, TestOf(typeof(SharedPhysicsSystem))]
internal sealed class FarOriginPrecision_Test
{
    [Test]
    public void BodyOnFarRotatedGridMovesStraight()
    {
        var sim = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entManager = sim.Resolve<IEntityManager>();
        var fixtures = entManager.System<FixtureSystem>();
        var physics = entManager.System<SharedPhysicsSystem>();
        var transforms = entManager.System<SharedTransformSystem>();
        var maps = entManager.System<SharedMapSystem>();

        var map = sim.CreateMap();
        var grid = maps.CreateGridEntity(map.MapId);
        for (var x = 0; x < 12; x++)
        {
            maps.SetTile(grid, new Vector2i(x, 0), new Tile(1));
        }

        var gridRotation = Angle.FromDegrees(30);
        transforms.SetLocalPositionRotation(grid.Owner, new Vector2(100_000f, 0f), gridRotation);

        var start = new Vector2(0.5f, 0.5f);
        var body = entManager.SpawnEntity(null, new EntityCoordinates(grid.Owner, start));
        var physicsComp = entManager.AddComponent<PhysicsComponent>(body);
        physics.SetBodyType(body, BodyType.Dynamic, body: physicsComp);
        physics.SetLinearDamping(body, physicsComp, 0f);
        physics.SetAngularDamping(body, physicsComp, 0f);
        var shape = new PolygonShape();
        shape.SetAsBox(0.25f, 0.25f);
        fixtures.CreateFixture(body, "fix1", new Fixture(shape, 0, 0, false), body: physicsComp);
        physics.WakeBody(body, body: physicsComp);

        // 2 m/s along the grid's local X axis, expressed in world space like any mover does.
        const float speed = 2f;
        physics.SetLinearVelocity(body, gridRotation.RotateVec(new Vector2(speed, 0f)), body: physicsComp);

        const float dt = 1f / 30f;
        const int steps = 60;
        var xform = entManager.GetComponent<TransformComponent>(body);
        var maxOffAxis = 0f;
        var maxStepError = 0f;
        var lastX = start.X;

        for (var i = 0; i < steps; i++)
        {
            physics.Update(dt);
            Assert.That(xform.ParentUid, Is.EqualTo(grid.Owner), "body left the grid");

            var local = xform.LocalPosition;
            maxOffAxis = MathF.Max(maxOffAxis, MathF.Abs(local.Y - start.Y));
            maxStepError = MathF.Max(maxStepError, MathF.Abs(local.X - lastX - speed * dt));
            lastX = local.X;
        }

        var expectedX = start.X + speed * dt * steps;
        Assert.Multiple(() =>
        {
            Assert.That(xform.LocalPosition.X, Is.EqualTo(expectedX).Within(1e-3f), "end position drifted");
            Assert.That(maxOffAxis, Is.LessThan(1e-3f), "body drifted off the grid axis");
            Assert.That(maxStepError, Is.LessThan(1e-3f), "steps were not uniform");
        });
    }
}
