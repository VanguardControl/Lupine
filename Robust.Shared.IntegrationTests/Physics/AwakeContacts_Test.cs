using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Contacts;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Reflection;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

/// <summary>
/// Lupine: the contact scan sets aside contacts whose bodies are both asleep (or asleep against static) and takes them
/// back when either body wakes, is refiltered, stops colliding or changes map. Each scenario runs with
/// physics.awake_contacts on and off (the upstream scan) and must produce the same positions, sleep states and
/// collide events on the same steps.
/// </summary>
[TestFixture, TestOf(typeof(SharedPhysicsSystem))]
internal sealed partial class AwakeContacts_Test
{
    private const float Dt = 1f / 30f;

    /// <summary>
    /// A resting stack falls asleep and its contacts leave the scan while the world keeps them.
    /// </summary>
    [Test]
    public void RestingStackParksItsContacts()
    {
        var world = new StackWorld(awakeContacts: true);
        world.RunUntilAsleep();

        var physics = world.Physics;
        var total = physics.ContactCount;
        var contacts = world.AllContacts();

        Assert.Multiple(() =>
        {
            Assert.That(total, Is.GreaterThanOrEqualTo(StackWorld.BoxCount), "stack has fewer contacts than boxes");
            Assert.That(contacts, Has.Count.EqualTo(total), "contact count disagrees with the bodies' contacts");
            Assert.That(contacts.All(c => c.IsTouching), "a resting contact stopped touching");
            Assert.That(contacts.All(c => c.Set == ContactSet.Asleep), "a contact of the sleeping stack is still scanned");
            Assert.That(physics.AwakeContactCount, Is.Zero);
        });

        world.Step();

        Assert.Multiple(() =>
        {
            Assert.That(physics.LastScannedContactCount, Is.Zero, "the scan visited a sleeping stack");
            Assert.That(physics.ContactCount, Is.EqualTo(total), "parking changed the contact count");
            Assert.That(world.Boxes.All(b => !b.Comp.Awake), "the stack woke up");
        });
    }

    /// <summary>
    /// An impulse on the top box puts its contacts back into the scan, the stack wakes and settles again, and the
    /// whole run matches the upstream scan step for step.
    /// </summary>
    [Test]
    public void ImpulseWakesStackLikeUpstream()
    {
        var traces = new Dictionary<bool, string>();
        var scanned = new Dictionary<bool, long>();

        foreach (var awakeContacts in new[] { true, false })
        {
            var world = new StackWorld(awakeContacts);
            world.RunUntilAsleep();
            var restScanned = world.ScannedTotal;

            var top = world.Boxes[^1];
            world.Physics.ApplyLinearImpulse(top, new Vector2(0.5f, 3f), body: top.Comp);

            if (awakeContacts)
            {
                var topContacts = world.ContactsOf(top);
                var others = world.AllContacts().Except(topContacts).ToList();

                Assert.Multiple(() =>
                {
                    Assert.That(topContacts, Is.Not.Empty);
                    Assert.That(topContacts.All(c => c.Set == ContactSet.Waking), "the woken box's contacts were not queued");
                    Assert.That(others.All(c => c.Set == ContactSet.Asleep), "contacts of untouched boxes were woken");
                });

                world.Step();

                Assert.That(world.Physics.LastScannedContactCount, Is.GreaterThanOrEqualTo(topContacts.Count),
                    "the scan did not visit the woken box's contacts");
            }

            world.RunUntilAsleep();

            for (var i = 0; i < world.Boxes.Count; i++)
            {
                var pos = world.Transforms.GetWorldPosition(world.Boxes[i]);
                Assert.That(pos.Y, Is.EqualTo(i + 0.5f).Within(0.2f), $"box {i} is out of the stack");
            }

            if (awakeContacts)
                Assert.That(world.Physics.AwakeContactCount, Is.Zero, "the resettled stack's contacts stayed in the scan");

            traces[awakeContacts] = world.Trace.ToString();
            scanned[awakeContacts] = world.ScannedTotal - restScanned;
        }

        Assert.That(traces[true], Is.EqualTo(traces[false]), "awake contact set changed the simulation");
        Assert.That(scanned[true], Is.LessThan(scanned[false]), "the awake contact set scanned no fewer contacts");
        TestContext.Out.WriteLine($"Contacts scanned after the impulse: {scanned[true]} with the awake set, {scanned[false]} upstream.");
    }

    /// <summary>
    /// A moving body hits a sleeping one whose contact with it was parked: the sensor and hard contacts start
    /// touching, the sleeping body wakes, all on the same steps as upstream.
    /// </summary>
    [Test]
    public void SleepingBodyHitByMovingBodyLikeUpstream()
    {
        var traces = new Dictionary<bool, string>();

        foreach (var awakeContacts in new[] { true, false })
        {
            var world = new ContactWorld(awakeContacts, gravity: false);

            // Diagonal neighbours: their boxes overlap, so the pair exists, but the shapes don't touch.
            var target = world.SpawnCircle(new Vector2(0f, 0f), 0.5f, sensorRadius: 0.7f);
            var mover = world.SpawnCircle(new Vector2(0.95f, 0.95f), 0.5f);
            world.Physics.SetAwake(target, false);
            world.Physics.SetAwake(mover, false);
            world.Step();

            var pair = world.ContactsOf(mover);
            Assert.That(pair, Is.Not.Empty, "no contact between the neighbours");
            Assert.That(pair.All(c => !c.IsTouching), "the neighbours touch before the mover moves");

            if (awakeContacts)
                Assert.That(pair.All(c => c.Set == ContactSet.Asleep), "the sleeping pair's contacts were not parked");

            world.Physics.SetLinearVelocity(mover, new Vector2(-1.5f, -1.5f), body: mover.Comp);

            if (awakeContacts)
                Assert.That(pair.All(c => c.Set == ContactSet.Waking), "waking the mover did not queue its contacts");

            for (var i = 0; i < 30; i++)
            {
                world.Step();
            }

            var log = world.Trace.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(log, Does.Contain("start e0 e1 sensor fix1"), "the sensor never started touching");
                Assert.That(log, Does.Contain("start e0 e1 fix1 fix1"), "the hard contact never started touching");
                Assert.That(target.Comp.Awake, Is.True, "the hit body did not wake");
            });

            traces[awakeContacts] = log;
        }

        Assert.That(traces[true], Is.EqualTo(traces[false]), "awake contact set changed the simulation");
    }

    /// <summary>
    /// Refiltering a parked contact so it no longer collides destroys it on the next step, as upstream.
    /// </summary>
    [Test]
    public void RefilterOfParkedContactLikeUpstream()
    {
        var traces = new Dictionary<bool, string>();

        foreach (var awakeContacts in new[] { true, false })
        {
            var world = new StackWorld(awakeContacts);
            world.RunUntilAsleep();

            var top = world.Boxes[^1];
            var contacts = world.ContactsOf(top);

            if (awakeContacts)
                Assert.That(contacts.All(c => c.Set == ContactSet.Asleep));

            var fixture = world.Fixtures.GetFixtureOrNull(top, "fix1")!;
            world.Physics.SetCollisionLayer(top, "fix1", fixture, 2);
            world.Physics.SetCollisionMask(top, "fix1", fixture, 2);
            world.Trace.AppendLine("refiltered");

            for (var i = 0; i < 5; i++)
            {
                world.Step();
            }

            Assert.That(world.Trace.ToString(), Does.Contain("end e3 e4 fix1 fix1"),
                "the refiltered contact never ended");

            traces[awakeContacts] = world.Trace.ToString();
        }

        Assert.That(traces[true], Is.EqualTo(traces[false]), "awake contact set changed the simulation");
    }

    /// <summary>
    /// A sleeping box of the stack leaves for nullspace or another map, or stops colliding: its parked contacts end
    /// on the same step as upstream.
    /// </summary>
    [TestCase("nullspace")]
    [TestCase("map")]
    [TestCase("cancollide")]
    public void SleepingBodyLeavesLikeUpstream(string how)
    {
        var traces = new Dictionary<bool, string>();

        foreach (var awakeContacts in new[] { true, false })
        {
            var world = new StackWorld(awakeContacts);
            world.RunUntilAsleep();

            var middle = world.Boxes[1];

            if (awakeContacts)
                Assert.That(world.ContactsOf(middle).All(c => c.Set == ContactSet.Asleep));

            switch (how)
            {
                case "nullspace":
                    world.Transforms.DetachEntity(middle, world.EntMan.GetComponent<TransformComponent>(middle));
                    break;
                case "map":
                    var other = world.Sim.CreateMap().Uid;
                    world.Transforms.SetCoordinates(middle, new EntityCoordinates(other, new Vector2(0f, 1.5f)));
                    break;
                case "cancollide":
                    world.Physics.SetCanCollide(middle, false, body: middle.Comp);
                    break;
            }

            world.Trace.AppendLine(how);

            for (var i = 0; i < 30; i++)
            {
                // Upstream keeps a contact across maps while both bodies sleep; the scan ends it once one wakes.
                if (i == 10 && how == "map")
                    world.Physics.WakeBody(middle, body: middle.Comp);

                world.Step();
            }

            Assert.That(world.Trace.ToString(), Does.Contain("end e1 e2 fix1 fix1"), "the box's contacts never ended");
            traces[awakeContacts] = world.Trace.ToString();
        }

        Assert.That(traces[true], Is.EqualTo(traces[false]), "awake contact set changed the simulation");
    }

    /// <summary>
    /// Parked contacts woken in the reverse of their creation order, between contacts that never slept, raise their
    /// events in creation order when all start touching on the same step, as the upstream scan visits them.
    /// </summary>
    [Test]
    public void WokenContactsKeepUpstreamOrder()
    {
        const int pairs = 6;
        var traces = new Dictionary<bool, string>();

        foreach (var awakeContacts in new[] { true, false })
        {
            var world = new ContactWorld(awakeContacts, gravity: false);
            var movers = new List<Entity<PhysicsComponent>>();

            for (var i = 0; i < pairs; i++)
            {
                var target = world.SpawnCircle(new Vector2(10f * i, 0f), 0.5f);
                var mover = world.SpawnCircle(new Vector2(10f * i + 0.95f, 0.95f), 0.5f);
                movers.Add(mover);

                // Odd pairs stay awake, so their contacts stay in the awake list the woken ones merge into.
                if (i % 2 == 1)
                {
                    world.Physics.SetSleepingAllowed(target, target.Comp, false);
                    world.Physics.SetSleepingAllowed(mover, mover.Comp, false);
                    continue;
                }

                world.Physics.SetAwake(target, false);
                world.Physics.SetAwake(mover, false);
            }

            world.Step();

            for (var i = pairs - 1; i >= 0; i--)
            {
                world.Physics.SetLinearVelocity(movers[i], new Vector2(-1.5f, -1.5f), body: movers[i].Comp);
            }

            for (var i = 0; i < 10; i++)
            {
                world.Step();
            }

            var log = world.Trace.ToString();
            var startSteps = log.Split("step ")
                .Where(block => block.Contains("start"))
                .ToList();

            Assert.That(startSteps, Has.Count.EqualTo(1), "the pairs did not start touching on the same step");
            Assert.That(startSteps[0].Split("start").Length - 1, Is.EqualTo(pairs));

            traces[awakeContacts] = log;
        }

        Assert.That(traces[true], Is.EqualTo(traces[false]), "awake contact set changed the event order");
    }

    /// <summary>
    /// Turning the cvar off puts every parked contact back into the scan.
    /// </summary>
    [Test]
    public void CvarOffWakesParkedContacts()
    {
        var world = new StackWorld(awakeContacts: true);
        world.RunUntilAsleep();
        Assert.That(world.AllContacts().All(c => c.Set == ContactSet.Asleep));

        world.Sim.Resolve<Robust.Shared.Configuration.IConfigurationManager>()
            .SetCVar(Robust.Shared.CVars.PhysicsAwakeContacts, false);
        world.Step();

        Assert.Multiple(() =>
        {
            Assert.That(world.AllContacts().All(c => c.Set == ContactSet.Awake));
            Assert.That(world.Physics.LastScannedContactCount, Is.EqualTo(world.Physics.ContactCount));
        });
    }

    /// <summary>
    /// A ground plus a column of boxes under gravity.
    /// </summary>
    private sealed class StackWorld : ContactWorld
    {
        public const int BoxCount = 4;

        public StackWorld(bool awakeContacts) : base(awakeContacts, gravity: true)
        {
            SpawnBox(new Vector2(0f, -0.5f), new Vector2(10f, 0.5f), BodyType.Static);

            for (var i = 0; i < BoxCount; i++)
            {
                Boxes.Add(SpawnBox(new Vector2(0f, 0.55f + 1.05f * i), new Vector2(0.5f, 0.5f), BodyType.Dynamic));
            }
        }
    }

    private class ContactWorld
    {
        public readonly ISimulation Sim;
        public readonly IEntityManager EntMan;
        public readonly SharedPhysicsSystem Physics;
        public readonly FixtureSystem Fixtures;
        public readonly SharedTransformSystem Transforms;
        public readonly List<Entity<PhysicsComponent>> Bodies = new();
        public readonly List<Entity<PhysicsComponent>> Boxes = new();
        public readonly StringBuilder Trace = new();
        public long ScannedTotal;

        private readonly EntityUid _map;
        private int _step;

        public ContactWorld(bool awakeContacts, bool gravity)
        {
            Sim = RobustServerSimulation.NewSimulation()
                .RegisterComponents(f => f.RegisterClass<AwakeContactsLogComponent>())
                .RegisterEntitySystems(f => f.LoadExtraSystemType<AwakeContactsLogSystem>())
                .ChangeCVar(c => c.SetCVar(Robust.Shared.CVars.PhysicsAwakeContacts, awakeContacts))
                .InitializeInstance();

            EntMan = Sim.Resolve<IEntityManager>();
            Physics = EntMan.System<SharedPhysicsSystem>();
            Fixtures = EntMan.System<FixtureSystem>();
            Transforms = EntMan.System<SharedTransformSystem>();
            EntMan.System<AwakeContactsLogSystem>().Log = Trace;
            EntMan.System<AwakeContactsLogSystem>().Bodies = Bodies;
            _map = Sim.CreateMap().Uid;

            if (gravity)
                Physics.SetGravity(new Vector2(0f, -9.8f));
        }

        public Entity<PhysicsComponent> SpawnBox(Vector2 position, Vector2 halfExtents, BodyType type)
        {
            var shape = new PolygonShape();
            shape.SetAsBox(halfExtents.X, halfExtents.Y);
            return Spawn(position, type, ("fix1", new Fixture(shape, 1, 1, true)));
        }

        public Entity<PhysicsComponent> SpawnCircle(Vector2 position, float radius, float? sensorRadius = null)
        {
            var fixtures = new List<(string, Fixture)> { ("fix1", new Fixture(new PhysShapeCircle(radius), 1, 1, true)) };

            if (sensorRadius != null)
                fixtures.Add(("sensor", new Fixture(new PhysShapeCircle(sensorRadius.Value), 1, 1, false)));

            return Spawn(position, BodyType.Dynamic, fixtures.ToArray());
        }

        private Entity<PhysicsComponent> Spawn(Vector2 position, BodyType type, params (string Id, Fixture Fixture)[] fixtures)
        {
            var uid = EntMan.SpawnEntity(null, new EntityCoordinates(_map, position));
            EntMan.AddComponent<AwakeContactsLogComponent>(uid);
            var body = EntMan.AddComponent<PhysicsComponent>(uid);
            Physics.SetBodyType(uid, type, body: body);

            foreach (var (id, fixture) in fixtures)
            {
                Fixtures.CreateFixture(uid, id, fixture, body: body);
            }

            Physics.SetCanCollide(uid, true, body: body);
            Physics.WakeBody(uid, body: body);
            Bodies.Add((uid, body));
            return (uid, body);
        }

        public void Step()
        {
            Trace.AppendLine($"step {_step++}");
            Physics.Update(Dt);
            ScannedTotal += Physics.LastScannedContactCount;

            for (var i = 0; i < Bodies.Count; i++)
            {
                var (uid, body) = Bodies[i];
                var (pos, rot) = Transforms.GetWorldPositionRotation(uid);
                Trace.AppendLine($"e{i} {Bits(pos.X)} {Bits(pos.Y)} {Bits((float) rot.Theta)} {(body.Awake ? "awake" : "asleep")}");
            }
        }

        public void RunUntilAsleep()
        {
            for (var i = 0; i < 600; i++)
            {
                Step();

                if (Bodies.All(b => !b.Comp.Awake))
                {
                    // One more step so the scan parks what fell asleep in the last one.
                    Step();
                    return;
                }
            }

            Assert.Fail("bodies never fell asleep");
        }

        public List<Contact> ContactsOf(Entity<PhysicsComponent> body)
        {
            return body.Comp.Contacts.ToList();
        }

        public List<Contact> AllContacts()
        {
            return Bodies.SelectMany(b => b.Comp.Contacts).Distinct().ToList();
        }

        private static string Bits(float value)
        {
            return BitConverter.SingleToInt32Bits(value).ToString("x8");
        }
    }

    [Reflect(false)]
    private sealed partial class AwakeContactsLogComponent : Component
    {
    }

    /// <summary>
    /// Writes collide events into the running trace, naming bodies by spawn order.
    /// </summary>
    [Reflect(false)]
    private sealed class AwakeContactsLogSystem : EntitySystem
    {
        public StringBuilder Log = new();
        public List<Entity<PhysicsComponent>> Bodies = new();

        public override void Initialize()
        {
            SubscribeLocalEvent<AwakeContactsLogComponent, StartCollideEvent>(OnStart);
            SubscribeLocalEvent<AwakeContactsLogComponent, EndCollideEvent>(OnEnd);
        }

        private void OnStart(EntityUid uid, AwakeContactsLogComponent comp, ref StartCollideEvent args)
        {
            Write("start", args.OurEntity, args.OtherEntity, args.OurFixtureId, args.OtherFixtureId);
        }

        private void OnEnd(EntityUid uid, AwakeContactsLogComponent comp, ref EndCollideEvent args)
        {
            Write("end", args.OurEntity, args.OtherEntity, args.OurFixtureId, args.OtherFixtureId);
        }

        private void Write(string kind, EntityUid ours, EntityUid other, string ourFixture, string otherFixture)
        {
            var a = Bodies.FindIndex(b => b.Owner == ours);
            var b = Bodies.FindIndex(b => b.Owner == other);

            // Each pair is logged once, lower index first, so the line doesn't depend on which side raised it first.
            if (a > b)
                return;

            Log.AppendLine($"{kind} e{a} e{b} {ourFixture} {otherFixture}");
        }
    }
}
