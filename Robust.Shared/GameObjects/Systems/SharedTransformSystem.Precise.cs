using System;
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
    /// <summary>
    /// World position and rotation of an entity, accumulated in double precision up the parent chain.
    /// </summary>
    public (Vector2d WorldPosition, double WorldRotation) GetWorldPositionRotationPrecise(TransformComponent component)
    {
        double x = component._localPosition.X;
        double y = component._localPosition.Y;
        var angle = component._localRotation.Theta;

        while (component.ParentUid != component.MapUid && component.ParentUid.IsValid())
        {
            component = XformQuery.GetComponent(component.ParentUid);
            var theta = component._localRotation.Theta;
            var (sin, cos) = Math.SinCos(theta);
            var rx = cos * x - sin * y;
            var ry = sin * x + cos * y;
            x = rx + component._localPosition.X;
            y = ry + component._localPosition.Y;
            angle += theta;
        }

        return (new Vector2d(x, y), angle);
    }

    /// <summary>
    /// Converts a world position held as a double origin plus a small float offset into a local position under a
    /// parent, with the subtraction done in doubles so a far-away parent costs no precision.
    /// </summary>
    public static Vector2 WorldToLocalPrecise(
        Vector2d worldOrigin,
        Vector2 worldOffset,
        Vector2d parentPosition,
        double parentRotation)
    {
        var dx = worldOrigin.X + worldOffset.X - parentPosition.X;
        var dy = worldOrigin.Y + worldOffset.Y - parentPosition.Y;
        var (sin, cos) = Math.SinCos(-parentRotation);
        return new Vector2((float) (cos * dx - sin * dy), (float) (sin * dx + cos * dy));
    }
}
