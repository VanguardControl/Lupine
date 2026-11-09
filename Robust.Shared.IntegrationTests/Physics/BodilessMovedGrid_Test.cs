using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;

namespace Robust.UnitTesting.Shared.Physics;

/// <summary>
/// Lupine: a map's own grid moved into another map has no physics body. The broadphase used to throw on it every
/// substep, which stopped physics for every map; now it is skipped and everything else keeps moving.
/// </summary>
[TestFixture, TestOf(typeof(SharedBroadphaseSystem))]
internal sealed class BodilessMovedGrid_Test : RobustIntegrationTest
{
    [Test]
    public async Task MovedMapGridDoesNotStopPhysics()
    {
        // The skip logs an error on purpose; this test is about physics carrying on.
        var server = StartServer(new ServerIntegrationOptions { FailureLogLevel = LogLevel.Fatal });
        await server.WaitIdleAsync();

        var entManager = server.ResolveDependency<IEntityManager>();
        var physSystem = entManager.System<SharedPhysicsSystem>();
        var fixtureSystem = entManager.System<FixtureSystem>();
        var transformSystem = entManager.System<SharedTransformSystem>();
        var mapSystem = entManager.System<SharedMapSystem>();

        await server.WaitAssertion(() =>
        {
            // A planet-like map whose terrain grid is the map entity itself.
            var planet = mapSystem.CreateMap(out _);
            var planetGrid = entManager.EnsureComponent<MapGridComponent>(planet);
            mapSystem.SetTile(planet, planetGrid, Vector2i.Zero, new Tile(1));
            Assert.That(entManager.HasComponent<PhysicsComponent>(planet), Is.False, "precondition: a map has no body");

            mapSystem.CreateMap(out var otherId);
            var mover = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(50f, 50f), otherId));
            var body = entManager.AddComponent<PhysicsComponent>(mover);
            physSystem.SetBodyType(mover, BodyType.Dynamic, body: body);
            var shape = new PolygonShape();
            shape.SetAsBox(0.25f, 0.25f);
            fixtureSystem.CreateFixture(mover, "fix1", new Fixture(shape, 1, 1, true), body: body);
            physSystem.WakeBody(mover, body: body);
            physSystem.SetLinearVelocity(mover, new Vector2(1f, 0f), body: body);

            // What the z-level takeoff left behind: the planet map, moved into another map, queued as a moved grid.
            // Debug builds assert on reparenting a map, so the test queues it directly.
            physSystem.MovedGrids.Add(planet);

            var start = transformSystem.GetWorldPosition(mover);
            Assert.DoesNotThrow(() =>
            {
                for (var i = 0; i < 10; i++)
                {
                    physSystem.Update(0.05f);
                }
            });

            Assert.That(transformSystem.GetWorldPosition(mover).X, Is.GreaterThan(start.X + 0.1f), "physics stopped");
            Assert.That(physSystem.MovedGrids, Does.Not.Contain(planet));
        });
    }
}
