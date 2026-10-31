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
var poses = new KittyPoseBlend();
Check(poses.Count == 35 && poses[0] == 1, "The refined pose vocabulary must start in a stable watch pose.");
poses.Step(1d / 60, 7);
Check(poses[0] is > .95 and < 1 && poses[7] is > 0 and < .05,
    "Changing from watch to sleep snapped directly to the sleeping geometry.");
var beforeInterrupt = Enumerable.Range(0, poses.Count).Select(index => poses[index]).ToArray();
poses.Step(1d / 144, 6);
Check(Math.Abs(poses[7] - beforeInterrupt[7]) < .04 && poses.TransitionEnergy > 0,
    "An interrupted pose discarded its continuous blend state.");
for (var i = 0; i < 720; i++) poses.Step(1d / 144, 6);
Check(Math.Abs(poses[6] - 1) < .000001 && poses.TransitionEnergy < .000001,
    "Blended action transitions must settle completely without residual jitter.");
static double[] BlendAt(int fps)
{
    var blend = new KittyPoseBlend();
    for (var i = 0; i < fps; i++) blend.Step(1d / fps, 7);
    for (var elapsed = 0d; elapsed < .2; elapsed += 1d / fps)
        blend.Step(Math.Min(1d / fps, .2 - elapsed), 6);
    return Enumerable.Range(0, blend.Count).Select(index => blend[index]).ToArray();
}
var blend30 = BlendAt(30); var blend60 = BlendAt(60); var blend144 = BlendAt(144);
Check(Math.Abs(blend30[6] - blend60[6]) < .000001 && Math.Abs(blend60[6] - blend144[6]) < .000001,
    "Pose morphing changed visibly with the display's refresh rate.");
for (var i = 0; i < 2000; i++)
{
    poses.Step(i % 7 == 0 ? .09 : .004, i % poses.Count);
    var sum = Enumerable.Range(0, poses.Count).Sum(index => poses[index]);
    Check(double.IsFinite(poses.TransitionEnergy) && Math.Abs(sum - 1) < .000001,
        "Rapid action interruptions escaped the normalized, finite pose blend.");
}
var poseBeforeInvalidTime = poses[6]; poses.Step(double.NaN, 6); poses.Step(-1, 6);
Check(poses[6] == poseBeforeInvalidTime, "Invalid time changed the visual pose blend.");
poses.Reset(12);
Check(poses[12] == 1 && poses.TransitionEnergy == 0, "Suspending motion must reset every pose clock.");
Console.WriteLine($"PASS {checks} articulated spring checks: inertial lag, chain independence, bounded motion, frame-rate consistency and reset.");
