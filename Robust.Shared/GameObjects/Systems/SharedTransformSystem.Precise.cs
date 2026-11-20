using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.Maths;

namespace Robust.Shared.GameObjects;

// Wolfgate: double-precision world transforms for the physics solver.
//
// World coordinates are floats, so a body 100 km from the map origin can only be placed to the nearest centimetre
// in world space, and that error is rotated by the grid's angle on the way back to local coordinates. Summing the
// parent chain in doubles and converting back the same way keeps the sub-millimetre local position of a body on a
// far grid intact.
public abstract partial class SharedTransformSystem
{
    // A float can't hold where a grid 76 km out really is: positions there are 8 mm apart. An entity parented straight
    // to a map (a root: a grid, or anything loose in space) therefore lost whatever part of each step did not fit, every
    // tick: slow drift never moved it, a turn shook it, and docked grids were rounded apart and pulled back together.
    // The solver now leaves the position it actually reached here, and reads it back next step for as long as the
    // float still matches, so anything else that moves the entity simply takes over.

    /// <summary>
    /// A root's solved position, and the float local position it was rounded to.
    /// </summary>
    private readonly record struct PreciseRoot(Vector2d Position, Vector2 Rounded);

    private readonly Dictionary<EntityUid, PreciseRoot> _preciseRoots = new();

    /// <summary>
    /// Records the double-precision position the solver reached for an entity parented directly to a map. It is
    /// kept only while it rounds to the entity's current local position.
    /// </summary>
    internal void SetPreciseRootPosition(EntityUid uid, TransformComponent xform, Vector2d position)
    {
        var local = xform._localPosition;

        if (xform.ParentUid != xform.MapUid || (float) position.X != local.X || (float) position.Y != local.Y)
        {
            _preciseRoots.Remove(uid);
            return;
        }

        _preciseRoots[uid] = new PreciseRoot(position, local);
    }

    /// <summary>
    /// Forgets the double-precision position of a root.
    /// </summary>
    internal void ClearPreciseRootPosition(EntityUid uid)
    {
        _preciseRoots.Remove(uid);
    }

    /// <summary>
    /// Forgets every double-precision root position.
    /// </summary>
    internal void ClearPreciseRootPositions()
    {
        _preciseRoots.Clear();
    }

    /// <summary>
    /// An entity's local position in doubles: the solver's own figure for a root it last moved, the float otherwise.
    /// </summary>
    private Vector2d GetLocalPositionPrecise(EntityUid uid, TransformComponent component)
    {
        var local = component._localPosition;

        if (_preciseRoots.Count != 0 &&
            component.ParentUid == component.MapUid &&
            _preciseRoots.TryGetValue(uid, out var root) &&
            root.Rounded == local)
        {
            return root.Position;
        }

        return new Vector2d(local.X, local.Y);
    }

    /// <summary>
    /// World position and rotation of an entity, accumulated in double precision up the parent chain.
    /// </summary>
    public (Vector2d WorldPosition, double WorldRotation) GetWorldPositionRotationPrecise(EntityUid uid, TransformComponent component)
    {
        var (x, y) = GetLocalPositionPrecise(uid, component);
        var angle = component._localRotation.Theta;

        while (component.ParentUid != component.MapUid && component.ParentUid.IsValid())
        {
            uid = component.ParentUid;
            component = XformQuery.GetComponent(uid);
            var theta = component._localRotation.Theta;
            var (sin, cos) = Math.SinCos(theta);
            var rx = cos * x - sin * y;
            var ry = sin * x + cos * y;
            var (px, py) = GetLocalPositionPrecise(uid, component);
            x = rx + px;
            y = ry + py;
            angle += theta;
        }

        return (new Vector2d(x, y), angle);
    }

    /// <summary>
    /// Converts a world position held as a double origin plus a small float offset into a local position under a
    /// parent, with the subtraction done in doubles so a far-away parent costs no precision.
    /// </summary>
    public static Vector2d WorldToLocalPrecise(
        Vector2d worldOrigin,
        Vector2 worldOffset,
        Vector2d parentPosition,
        double parentRotation)
    {
        var dx = worldOrigin.X + worldOffset.X - parentPosition.X;
        var dy = worldOrigin.Y + worldOffset.Y - parentPosition.Y;
        var (sin, cos) = Math.SinCos(-parentRotation);
        return new Vector2d(cos * dx - sin * dy, sin * dx + cos * dy);
    }
}
