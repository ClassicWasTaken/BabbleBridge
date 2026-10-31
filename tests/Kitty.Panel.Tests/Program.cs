// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using BabbleFrameBridge.App;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < .001, message);
var panels = new[] { new KittySurface("hero", 248, 108, 900, 220), new KittySurface("output", 248, 350, 900, 220),
    new KittySurface("module", 248, 590, 900, 140) };

Check(KittyPanelWorld.TryFindNearestPerch(panels, 1200, 860, 300, 220, 30, out var perch), "A nearby visible panel top was not recognized.");
Check(perch.SurfaceId == "output", "A perch used the wrong panel.");
Near(perch.Y + 132, 350, "Cat feet are not on the actual top edge.");
Near(perch.X + 72, 372, "A centered drop changed horizontal position.");
Check(!KittyPanelWorld.TryFindNearestPerch(panels, 1200, 860, 300, 0, 500, out var high) || high.SurfaceId != "hero",
    "A header panel without enough headroom was accepted.");
Check(!KittyPanelWorld.TryFindNearestPerch(panels, 1200, 860, 300, 250, 5, out _), "A far-away drop snapped to a panel.");
Check(KittyPanelWorld.TryFindNearestPerch(panels, 1200, 860, 210, 218, 64, out var corner), "A nearby corner drop should fit.");
Check(corner.CenterX >= 276 && corner.X >= 6, "A corner perch overhung its safe top or viewport.");

var bad = new[] { new KittySurface("", 248, 350, 500, 100), new KittySurface("nan", double.NaN, 350, 500, 100),
    new KittySurface("small", 248, 350, 90, 100), new KittySurface("offscreen", 248, -20, 900, 400),
    new KittySurface("below", 248, 900, 900, 100), new KittySurface("tiny", 248, 350, 500, 4),
    new KittySurface("infinite", 248, 350, double.PositiveInfinity, 100), new KittySurface("hidden", -500, 350, 500, 100) };
Check(!KittyPanelWorld.TryFindNearestPerch(bad, 1200, 860, 300, 218, 1000, out _), "An invalid, too-small or invisible surface was accepted.");
Check(!KittyPanelWorld.TryFindNearestPerch(panels, double.NaN, 860, 300, 218, 64, out _), "Invalid viewport dimensions reached perch geometry.");
Check(!KittyPanelWorld.TryFindNearestPerch(panels, 1200, 860, double.NaN, 218, 64, out _), "Invalid cat coordinates reached perch geometry.");
Check(!KittyPanelWorld.TryFindNearestPerch(panels, 1200, 860, 300, 218, double.NaN, out _), "Invalid snap distance was accepted.");
Check(!KittyPanelWorld.TryFindNearestPerch(null, 1200, 860, 300, 218, 64, out _), "A missing layout found a ledge.");

Check(KittyPanelWorld.TryChooseClimbTarget(panels, 1200, 860, 300, 590, 7, out var climb), "An eligible higher panel did not offer a climb.");
Check(climb.EdgeY < 590 + 132 && climb.SurfaceId != "hero", "A climb selected a lower or unsafe top.");
var route = KittyPanelWorld.CreateClimbRoute(climb, 1200, 860, 300, 590);
Check(route.Approach.Y >= route.Grip.Y && route.Grip.Y >= route.Top.Y, "The climb route did not ascend before settling on top.");
Near(route.Top.X, climb.X, "Climb final position moved away from its perch.");
Near(route.Top.Y, climb.Y, "Climb final feet are off the ledge.");
Check(KittyPanelWorld.TryChooseClimbTarget(panels, 1200, 860, 300, 590, 7, out var repeat) && repeat == climb,
    "Climb preference seeds are not deterministic.");
Check(!KittyPanelWorld.TryChooseClimbTarget(panels, 1200, 860, 300, 6, 7, out _), "A cat already above all eligible tops chose an upward climb.");

Check(KittyPanelWorld.TryFindLandingBelow(panels, 1200, 860, 300, 190, out var landing) && landing.SurfaceId == "output",
    "The nearest ledge below the cat was skipped.");
Check(!KittyPanelWorld.TryFindLandingBelow(panels, 1200, 860, 0, 190, out _), "Landing snapped sideways onto a distant card.");
Check(!KittyPanelWorld.TryFindLandingBelow(panels, 1200, 860, 300, 650, out _), "A panel above the cat was treated as a floor below.");

var moved = panels.Select(surface => surface.Id == "output" ? surface with { Left = 270, Top = 390, Width = 700 } : surface).ToArray();
Check(KittyPanelWorld.TryRefreshPerch(perch, moved, 1200, 860, out var updated), "A valid moving surface lost its perched cat.");
Near(updated.EdgeY, 390, "A perched cat did not follow a scrolling panel top.");
Near(updated.CenterX - updated.SurfaceLeft, perch.CenterX - perch.SurfaceLeft, "A resized panel changed the cat's near-edge offset.");
Check(!KittyPanelWorld.TryRefreshPerch(perch, panels.Where(value => value.Id != "output"), 1200, 860, out _), "A removed or navigated-away surface remained perched.");
Check(!KittyPanelWorld.TryRefreshPerch(perch, moved.Select(value => value.Id == "output" ? value with { Top = 80 } : value), 1200, 860, out _),
    "A scrolled-off top edge left the cat perched out of view.");

var protectedDrop = KittyPanelWorld.ClampPosition(20, 710, 1200, 860);
Check(protectedDrop.X >= 216 || protectedDrop.Y + 144 <= 650, "A carried cat blocked the lower-left launcher.");
var shallowCollision = KittyPanelWorld.ClampPosition(210, 690, 1200, 860);
Check(shallowCollision.X == 216 && shallowCollision.Y == 690, "A shallow launcher collision made a large vertical drag jump.");
var edgeDrop = KittyPanelWorld.ClampPosition(10000, -10000, 1200, 860);
Check(edgeDrop.X >= 6 && edgeDrop.X + 144 <= 1194 && edgeDrop.Y >= 6, "Drag clamping let the cat leave the viewport.");
foreach (var width in new[] { 0d, 80, 143, 360, 1200, double.NaN, double.PositiveInfinity })
foreach (var height in new[] { 0d, 80, 150, 360, 860, double.NaN, double.PositiveInfinity })
{
    var clamped = KittyPanelWorld.ClampPosition(double.NaN, double.PositiveInfinity, width, height);
    Check(double.IsFinite(clamped.X) && double.IsFinite(clamped.Y) && clamped.X >= 0 && clamped.Y >= 0,
        "An invalid or tiny viewport contaminated coordinates.");
}

var gesture = new KittyCarryGesture();
Check(gesture.Begin(340, 300, 300, 260), "A valid pickup did not begin.");
Check(!gesture.Begin(340, 300, 300, 260), "Another pointer replaced an active pickup.");
var still = gesture.Move(342, 301, .016, 1200, 860);
Check(still == new KittyPoint(300, 260) && !gesture.IsDragging, "A gentle click became a drag.");
var click = gesture.End();
Check(!click.WasDragging && !gesture.IsPressed, "A plain click was not returned as a click.");
Check(gesture.Begin(340, 300, 300, 260), "A second pickup did not begin.");
var dragged = gesture.Move(420, 350, .016, 1200, 860);
Near(dragged.X, 380, "Pickup did not preserve the horizontal grab offset.");
Near(dragged.Y, 310, "Pickup did not preserve the vertical grab offset.");
Check(gesture.IsDragging && gesture.IsPressed, "Crossing the drag threshold did not pick up the cat.");
var release = gesture.End();
Check(release.WasDragging && release.Position == dragged && !gesture.IsPressed && !gesture.IsDragging,
    "Releasing a dragged cat lost its position or capture state.");
Check(Math.Sqrt(release.Velocity.X * release.Velocity.X + release.Velocity.Y * release.Velocity.Y) <= 800.001,
    "Fast input produced an unbounded release velocity.");
Check(!gesture.Begin(double.NaN, 20, 10, 10), "An invalid pointer began a drag.");
Check(gesture.Begin(0, 0, 0, 0), "Extreme-input fixture failed to begin.");
gesture.Move(1e300, 1e300, .001, 1e301, 1e301);
Check(double.IsFinite(gesture.Velocity.X) && double.IsFinite(gesture.Velocity.Y), "Extreme finite inputs contaminated release velocity.");
gesture.Cancel();
Check(gesture.Begin(350, 300, 300, 260), "Cancel fixture failed to begin.");
var beforeInvalid = gesture.Position;
Check(gesture.Move(double.PositiveInfinity, 500, .01, 1200, 860) == beforeInvalid, "Invalid input teleported the cat.");
gesture.Move(500, 500, .016, 1200, 860);
var beforeCancel = gesture.Position;
gesture.Cancel();
Check(!gesture.IsPressed && !gesture.IsDragging && gesture.Velocity == default && gesture.Position == beforeCancel,
    "Canceling leaked gesture/capture state or teleported the cat.");

var random = new Random(42);
for (var i = 0; i < 2000; i++)
{
    var x = random.NextDouble() * 2500 - 600; var y = random.NextDouble() * 1800 - 300;
    var clamped = KittyPanelWorld.ClampPosition(x, y, 1200, 860);
    Check(clamped.X >= 6 && clamped.X + 144 <= 1194 && clamped.Y >= 6 && clamped.Y + 144 <= 854,
        "A randomized drag escaped safe viewport bounds.");
    Check(clamped.X >= 216 || clamped.Y + 144 <= 650, "A randomized drag blocked the protected launcher.");
}
Console.WriteLine($"PASS {checks} panel and pickup checks: real edge geometry, climb routes, layout changes, protected bounds and drag release.");
