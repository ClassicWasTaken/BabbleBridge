// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using BabbleFrameBridge.App;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
var rig = new KittySpringRig();
Check(rig.Snapshot.Head == default && rig.Snapshot.StretchX == 1, "Fresh rig must be at rest.");
rig.Step(1d / 60, 420, 0);
Check(rig.Snapshot.Head.X < 0 && rig.Snapshot.Hip.X < 0, "Moving right must make parts trail to the left.");
for (var i = 0; i < 120; i++) rig.Step(1d / 120, 420, 0);
var moving = rig.Snapshot;
Check(moving.Head.X < moving.Shoulder.X && moving.Shoulder.X < moving.Hip.X,
    "Head, shoulder and hip must have separate inertial lag.");
Check(moving.StretchX > 1.03 && moving.StretchY < 1, "Running must stretch and squash the articulated torso.");
Check(rig.TailOffset(7) != rig.TailOffset(0), "Tail links must move independently.");
rig.Step(1d / 60, -420, 0);
Check(rig.Snapshot.Head.X < 0, "Turning must not instantly snap every part to its opposite position.");
for (var i = 0; i < 720; i++) rig.Step(1d / 120, 0, 0);
Check(Math.Abs(rig.Snapshot.Head.X) < .001 && rig.Snapshot.MotionEnergy < .001, "Stopped springs must settle without perpetual jitter.");
Check(Math.Abs(rig.Snapshot.StretchX - 1) < .0001, "The torso must return to its normal proportions.");

static KittyRigSnapshot Simulate(int fps)
{
    var value = new KittySpringRig();
    for (var i = 0; i < fps * 3; i++)
    {
        var t = (i + .5) / fps;
        value.Step(1d / fps, Math.Sin(t * 2.5) * 400, Math.Cos(t * 1.2) * 120);
    }
    return value.Snapshot;
}
var at30 = Simulate(30); var at60 = Simulate(60); var at144 = Simulate(144);
Check(Math.Abs(at30.Head.X - at144.Head.X) < .6 && Math.Abs(at60.Head.X - at144.Head.X) < .3,
    "Spring movement must remain consistent across 30, 60 and 144 Hz.");
Check(Math.Abs(at30.TailTip.X - at144.TailTip.X) < .8, "The trailing chain must not depend heavily on frame rate.");

rig.Reset();
for (var i = 0; i < 2000; i++)
{
    rig.Step(i % 10 == 0 ? .09 : .004, i % 2 == 0 ? 10000 : -10000, Math.Sin(i) * 10000, i % 7 == 0 ? -1 : 1);
    var snapshot = rig.Snapshot;
    Check(double.IsFinite(snapshot.Head.X) && double.IsFinite(snapshot.TailTip.Y)
        && Math.Abs(snapshot.Head.X) <= 10 && Math.Abs(snapshot.Head.Y) <= 10
        && snapshot.StretchX is >= .88 and <= 1.12, "Extreme frame/input changes escaped safe visual bounds.");
}
rig.Step(.1, double.NaN, double.PositiveInfinity);
Check(double.IsFinite(rig.Snapshot.MotionEnergy), "Invalid cursor/velocity inputs contaminated the rig.");
var saved = rig.Snapshot;
rig.Step(double.NaN, 100, 100); rig.Step(-1, 100, 100);
Check(rig.Snapshot == saved, "Invalid time must not advance or mutate animation.");
rig.Reset(); rig.Step(1d / 60, -300, 0, -1);
Check(rig.Snapshot.FacingScale is > -1 and < 1, "A direction change must turn through a continuous pose instead of mirroring instantly.");
for (var i = 0; i < 120; i++) rig.Step(1d / 120, -300, 0, -1);
Check(rig.Snapshot.FacingScale < -.99, "The facing spring never finished its turn.");
rig.Reset();
Check(rig.Snapshot.Head == default && rig.Snapshot.MotionEnergy == 0 && rig.Snapshot.StretchX == 1,
    "Disabling/reducing motion must fully reset the rig.");
Console.WriteLine($"PASS {checks} articulated spring checks: inertial lag, chain independence, bounded motion, frame-rate consistency and reset.");
