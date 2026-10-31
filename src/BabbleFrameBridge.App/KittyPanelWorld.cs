// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

namespace BabbleFrameBridge.App;

public readonly record struct KittyPoint(double X, double Y);
public readonly record struct KittySurface(string Id, double Left, double Top, double Width, double Height);
public readonly record struct KittyPerchTarget(string SurfaceId, double X, double Y, double CenterX,
    double EdgeY, bool FromLeft, double SurfaceLeft, double SurfaceRight)
{
    public KittyPoint Position => new(X, Y);
}
public readonly record struct KittyClimbRoute(KittyPoint Approach, KittyPoint Grip, KittyPoint Top);
public readonly record struct KittyCarryRelease(bool WasDragging, KittyPoint Position, KittyPoint Velocity);

/// <summary>
/// Pure geometry for the app's visible panel tops. Callers supply only real,
/// visible panel borders; no controls are moved, clicked or modified here.
/// </summary>
public static class KittyPanelWorld
{
    public const double CatSize = 144, FeetX = 72, FeetY = 132;
    public const double MinimumPanelTop = 150, MinimumPanelWidth = 120;
    private const double Padding = 6, ProtectedWidth = 216, ProtectedHeight = 210;

    /// <summary>Keep a carried body visible and leave its lower-left launcher accessible.</summary>
    public static KittyPoint ClampPosition(double x, double y, double viewportWidth, double viewportHeight)
    {
        var width = Size(viewportWidth); var height = Size(viewportHeight);
        var maxX = Math.Max(0, width - CatSize - Padding);
        var maxY = Math.Max(0, height - CatSize - Padding);
        var minX = Math.Min(Padding, maxX); var minY = Math.Min(Padding, maxY);
        x = Math.Clamp(Finite(x), minX, maxX);
        y = Math.Clamp(Finite(y), minY, maxY);
        if (x < ProtectedWidth && y + CatSize > height - ProtectedHeight)
        {
            var aboveLauncher = height - ProtectedHeight - CatSize;
            if (aboveLauncher >= minY && maxX >= ProtectedWidth)
            {
                // Slide along the closest boundary instead of making a large
                // vertical jump when a drag just brushes the launcher region.
                if (ProtectedWidth - x < y - aboveLauncher) x = ProtectedWidth;
                else y = Math.Min(y, aboveLauncher);
            }
            else if (aboveLauncher >= minY) y = Math.Min(y, aboveLauncher);
            else if (maxX >= ProtectedWidth) x = ProtectedWidth;
            // Very small viewports cannot fit both the cat and the protected
            // region. Staying inside the viewport is the final safe fallback.
            else y = minY;
        }
        return new KittyPoint(x, y);
    }

    /// <summary>Find a top edge close to the cat's feet when it is put down.</summary>
    public static bool TryFindNearestPerch(IEnumerable<KittySurface>? surfaces,
        double viewportWidth, double viewportHeight, double catX, double catY,
        double maxDistance, out KittyPerchTarget target)
    {
        target = default;
        if (!ValidViewport(viewportWidth, viewportHeight) || !Coordinates(catX, catY)
            || !double.IsFinite(maxDistance) || maxDistance < 0 || surfaces is null) return false;
        var best = maxDistance > 1e150 ? 1e300 : maxDistance * maxDistance;
        var found = false;
        foreach (var surface in surfaces)
        {
            if (!TryPerch(surface, viewportWidth, viewportHeight, catX + FeetX, out var candidate)) continue;
            var distance = DistanceSquared(catX + FeetX, catY + FeetY, candidate.CenterX, candidate.EdgeY);
            if (distance > best) continue;
            // Stable input order resolves exact ties, avoiding jitter at shared edges.
            if (found && distance >= best) continue;
            best = distance; target = candidate; found = true;
        }
        return found;
    }

    /// <summary>Choose a reachable higher panel, with deterministic small route variety.</summary>
    public static bool TryChooseClimbTarget(IEnumerable<KittySurface>? surfaces,
        double viewportWidth, double viewportHeight, double catX, double catY,
        int preferenceSeed, out KittyPerchTarget target)
    {
        target = default;
        if (!ValidViewport(viewportWidth, viewportHeight) || !Coordinates(catX, catY) || surfaces is null) return false;
        var best = double.PositiveInfinity;
        var found = false; var ordinal = 0;
        foreach (var surface in surfaces)
        {
            var rise = catY + FeetY - surface.Top;
            if (!double.IsFinite(rise) || rise < 14 || rise > 540) { ordinal++; continue; }
            for (var side = 0; side < 2; side++)
            {
                // The front paws grab the side before the body pulls onto the ledge.
                var desiredCenter = side == 0 ? surface.Left + 40 : surface.Left + surface.Width - 40;
                if (!TryPerch(surface, viewportWidth, viewportHeight, desiredCenter, out var candidate)) continue;
                candidate = candidate with { FromLeft = side == 0 };
                var route = CreateClimbRoute(candidate, viewportWidth, viewportHeight, catX, catY);
                var approach = Math.Sqrt(DistanceSquared(catX, catY, route.Approach.X, route.Approach.Y));
                var horizontalPull = Math.Abs(candidate.X - route.Grip.X);
                var variety = UnitHash(preferenceSeed, ordinal * 2 + side) * 32;
                var cost = approach + rise * .18 + horizontalPull * .25 + variety;
                if (cost >= best) continue;
                best = cost; target = candidate; found = true;
            }
            ordinal++;
        }
        return found;
    }

    /// <summary>Find the first eligible ledge underneath a released cat, without a sideways teleport.</summary>
    public static bool TryFindLandingBelow(IEnumerable<KittySurface>? surfaces,
        double viewportWidth, double viewportHeight, double catX, double catY,
        out KittyPerchTarget target)
    {
        target = default;
        if (!ValidViewport(viewportWidth, viewportHeight) || !Coordinates(catX, catY) || surfaces is null) return false;
        var feet = catY + FeetY; var bestDrop = double.PositiveInfinity; var found = false;
        foreach (var surface in surfaces)
        {
            if (!TryPerch(surface, viewportWidth, viewportHeight, catX + FeetX, out var candidate)) continue;
            var drop = candidate.EdgeY - feet;
            if (drop < -.001 || drop >= bestDrop || Math.Abs(candidate.CenterX - (catX + FeetX)) > 28) continue;
            target = candidate; bestDrop = drop; found = true;
        }
        return found;
    }

    /// <summary>Resolve a perched cat against current layout after scrolling, resize or navigation.</summary>
    public static bool TryRefreshPerch(KittyPerchTarget previous, IEnumerable<KittySurface>? surfaces,
        double viewportWidth, double viewportHeight, out KittyPerchTarget target)
    {
        target = default;
        if (string.IsNullOrEmpty(previous.SurfaceId) || surfaces is null) return false;
        foreach (var surface in surfaces)
        {
            if (!string.Equals(surface.Id, previous.SurfaceId, StringComparison.Ordinal)) continue;
            var offset = previous.FromLeft ? previous.CenterX - previous.SurfaceLeft : previous.SurfaceRight - previous.CenterX;
            var desired = previous.FromLeft ? surface.Left + offset : surface.Left + surface.Width - offset;
            if (!TryPerch(surface, viewportWidth, viewportHeight, desired, out target)) return false;
            target = target with { FromLeft = previous.FromLeft };
            return true;
        }
        return false;
    }

    public static KittyClimbRoute CreateClimbRoute(KittyPerchTarget target,
        double viewportWidth, double viewportHeight, double catX, double catY)
    {
        var top = ClampPosition(target.X, target.Y, viewportWidth, viewportHeight);
        var side = target.FromLeft ? target.SurfaceLeft - 119 : target.SurfaceRight - 25;
        var approach = ClampPosition(side, Math.Max(top.Y + 84, Math.Min(Finite(catY), top.Y + 190)), viewportWidth, viewportHeight);
        var grip = ClampPosition(side, top.Y + 27, viewportWidth, viewportHeight);
        return new KittyClimbRoute(approach, grip, top);
    }

    private static bool TryPerch(KittySurface surface, double width, double height,
        double desiredCenter, out KittyPerchTarget target)
    {
        target = default;
        if (!ValidViewport(width, height) || string.IsNullOrWhiteSpace(surface.Id)
            || !Coordinates(surface.Left, surface.Top) || !Coordinates(surface.Width, surface.Height)
            || surface.Width < MinimumPanelWidth || surface.Height < 24
            || surface.Top < MinimumPanelTop || surface.Top > height - 20) return false;
        var right = surface.Left + surface.Width;
        if (!double.IsFinite(right)) return false;
        var leftEdge = Math.Max(Padding, surface.Left);
        var rightEdge = Math.Min(width - Padding, right);
        if (rightEdge - leftEdge < MinimumPanelWidth) return false;
        var minimum = Math.Max(leftEdge + 28, FeetX + Padding);
        var maximum = Math.Min(rightEdge - 28, width - CatSize - Padding + FeetX);
        if (maximum < minimum) return false;
        var center = Math.Clamp(Finite(desiredCenter, (minimum + maximum) * .5), minimum, maximum);
        var x = center - FeetX; var y = surface.Top - FeetY;
        var clamped = ClampPosition(x, y, width, height);
        // Never silently move a perch off its actual edge to satisfy a viewport
        // or launcher constraint; simply reject that unsafe surface instead.
        if (Math.Abs(clamped.X - x) > .001 || Math.Abs(clamped.Y - y) > .001) return false;
        target = new KittyPerchTarget(surface.Id, x, y, center, surface.Top,
            center <= (leftEdge + rightEdge) * .5, leftEdge, rightEdge);
        return true;
    }

    private static bool ValidViewport(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && width >= CatSize + Padding * 2 && height >= MinimumPanelTop + 20;
    private static bool Coordinates(double x, double y) => double.IsFinite(x) && double.IsFinite(y);
    private static double Size(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    private static double Finite(double value, double fallback = 0) => double.IsFinite(value) ? value : fallback;
    private static double DistanceSquared(double x, double y, double otherX, double otherY) =>
        (x - otherX) * (x - otherX) + (y - otherY) * (y - otherY);
    private static double UnitHash(int seed, int index)
    {
        var hash = unchecked((uint)seed * 747796405u + (uint)index * 2891336453u + 277803737u);
        hash ^= hash >> 16; hash *= 2246822519u; hash ^= hash >> 13;
        return hash / (double)uint.MaxValue;
    }
}

/// <summary>Pure click-versus-pickup gesture, keeping the exact point grabbed under the pointer.</summary>
public sealed class KittyCarryGesture
{
    public const double DragThreshold = 4;
    private KittyPoint _press, _grab;
    public bool IsPressed { get; private set; }
    public bool IsDragging { get; private set; }
    public KittyPoint Position { get; private set; }
    public KittyPoint Velocity { get; private set; }

    public bool Begin(double pointerX, double pointerY, double catX, double catY)
    {
        if (IsPressed || !Valid(pointerX, pointerY) || !Valid(catX, catY)) return false;
        _press = new(pointerX, pointerY); _grab = new(pointerX - catX, pointerY - catY);
        Position = new(catX, catY); Velocity = default; IsPressed = true; IsDragging = false;
        return true;
    }

    public KittyPoint Move(double pointerX, double pointerY, double elapsedSeconds, double width, double height)
    {
        if (!IsPressed || !Valid(pointerX, pointerY)) return Position;
        if (!IsDragging && Math.Pow(pointerX - _press.X, 2) + Math.Pow(pointerY - _press.Y, 2) >= DragThreshold * DragThreshold)
            IsDragging = true;
        if (!IsDragging) return Position;
        var next = KittyPanelWorld.ClampPosition(pointerX - _grab.X, pointerY - _grab.Y, width, height);
        var dt = double.IsFinite(elapsedSeconds) && elapsedSeconds > 0 ? Math.Clamp(elapsedSeconds, .001, .1) : 0;
        if (dt > 0)
        {
            var vx = Math.Clamp((next.X - Position.X) / dt, -800, 800);
            var vy = Math.Clamp((next.Y - Position.Y) / dt, -800, 800);
            var speed = Math.Sqrt(vx * vx + vy * vy);
            if (speed > 800) { vx *= 800 / speed; vy *= 800 / speed; }
            var smoothing = 1 - Math.Exp(-dt * 18);
            Velocity = new(Velocity.X + (vx - Velocity.X) * smoothing, Velocity.Y + (vy - Velocity.Y) * smoothing);
        }
        else Velocity = default;
        Position = next;
        return Position;
    }

    public KittyCarryRelease End()
    {
        var release = new KittyCarryRelease(IsDragging, Position, Velocity);
        IsPressed = IsDragging = false; Velocity = default;
        return release;
    }

    public void Cancel() { IsPressed = IsDragging = false; Velocity = default; }
    private static bool Valid(double x, double y) => double.IsFinite(x) && double.IsFinite(y);
}
