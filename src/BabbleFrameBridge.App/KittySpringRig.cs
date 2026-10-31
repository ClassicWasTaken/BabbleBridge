// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

namespace BabbleFrameBridge.App;

public readonly record struct KittyJoint(double X, double Y, double Angle = 0);
public readonly record struct KittyRigSnapshot(KittyJoint Hip, KittyJoint Belly, KittyJoint Shoulder,
    KittyJoint Head, double LeftEarAngle, double RightEarAngle, double StretchX,
    double StretchY, KittyJoint TailTip, double MotionEnergy, double FacingScale);

/// <summary>
/// Small articulated spring rig: three torso vertebrae, an independently lagging
/// head, two ears and eight trailing tail links. It consumes movement velocity,
/// never owns a timer, and uses bounded substeps instead of frame-based lerps.
/// </summary>
public sealed class KittySpringRig
{
    public const int TailLinkCount = 8;
    private readonly Axis[] _x = new Axis[14], _y = new Axis[14];
    private Axis _stretch, _earLeft, _earRight;
    private Axis _turn = new() { Position = 1 };
    private double _vx, _vy, _time;

    public KittyRigSnapshot Snapshot => new(Joint(0), Joint(1), Joint(2), Joint(3),
        _earLeft.Position, _earRight.Position, 1 + _stretch.Position,
        1 / Math.Sqrt(1 + _stretch.Position), TailOffset(TailLinkCount - 1),
        _x.Sum(axis => axis.Velocity * axis.Velocity) + _y.Sum(axis => axis.Velocity * axis.Velocity), _turn.Position);

    public KittyJoint TailOffset(int index)
    {
        if (index < 0 || index >= TailLinkCount) throw new ArgumentOutOfRangeException(nameof(index));
        return Joint(index + 4);
    }

    public KittyJoint LimbOffset(bool right, bool front)
    {
        var index = front ? 12 : 13;
        return new KittyJoint(_x[index].Position * (right ? 1 : .8), _y[index].Position * (right ? .7 : 1));
    }

    public void Step(double deltaSeconds, double velocityX, double velocityY, double facing = 1)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;
        var dt = Math.Min(.1, deltaSeconds);
        var steps = Math.Max(1, (int)Math.Ceiling(dt * 240));
        var h = dt / steps;
        var targetX = Finite(velocityX, -700, 700) * (facing < 0 ? -1 : 1);
        var targetY = Finite(velocityY, -700, 700);
        for (var step = 0; step < steps; step++)
        {
            var oldVx = _vx;
            _vx += (targetX - _vx) * (1 - Math.Exp(-h * 13));
            _vy += (targetY - _vy) * (1 - Math.Exp(-h * 13));
            var acceleration = Math.Clamp((_vx - oldVx) / h, -2400, 2400);
            var speed = Math.Min(1, Math.Sqrt(_vx * _vx + _vy * _vy) / 420);
            _time += h;
            Follow(0, -_vx * .006, -_vy * .004, 23, .85, h, 5);
            Follow(1, _x[0].Position * .65 - _vx * .007, _y[0].Position * .65 - _vy * .004, 19, .72, h, 7);
            Follow(2, _x[1].Position * .65 - _vx * .007, _y[1].Position * .65 - _vy * .004, 17, .7, h, 9);
            Follow(3, _x[2].Position * .6 - _vx * .009 - acceleration * .0005,
                _y[2].Position * .6 - _vy * .006, 15, .7, h, 10);
            for (var i = 0; i < TailLinkCount; i++)
            {
                var parentX = i == 0 ? _x[0].Position : _x[i + 3].Position;
                var parentY = i == 0 ? _y[0].Position : _y[i + 3].Position;
                var wave = Math.Sin(_time * 6 - i * .6) * speed;
                Follow(i + 4, parentX * .62 - _vx * (.002 + i * .0003) - acceleration * .0008,
                    parentY * .62 - _vy * .002 + wave * (1 + i * .15), 21 - i * .9, .68, h, 10);
            }
            Follow(12, -_vx * .005, -_vy * .003, 25, .72, h, 5);
            Follow(13, -_vx * .008, -_vy * .005, 20, .7, h, 6);
            _stretch.Step(Math.Clamp(speed * .065 + acceleration * .000012, -.035, .10), 20, .8, h, .12);
            _earLeft.Step(Math.Clamp(-_vx * .011 - acceleration * .0018, -10, 10), 19, .65, h, 12);
            _earRight.Step(Math.Clamp(-_vx * .009 - acceleration * .0022, -10, 10), 16, .65, h, 12);
            _turn.Step(facing < 0 ? -1 : 1, 24, .95, h, 1);
        }
    }

    public void Reset(double facing = 1)
    {
        Array.Clear(_x); Array.Clear(_y);
        _stretch = _earLeft = _earRight = default;
        _turn = new Axis { Position = facing < 0 ? -1 : 1 };
        _vx = _vy = _time = 0;
    }

    private KittyJoint Joint(int index) => new(_x[index].Position, _y[index].Position,
        Math.Clamp(_x[index].Position * .3, -4, 4));
    private void Follow(int index, double x, double y, double frequency, double damping, double dt, double limit)
    {
        _x[index].Step(Math.Clamp(x, -limit, limit), frequency, damping, dt, limit);
        _y[index].Step(Math.Clamp(y, -limit, limit), frequency, damping, dt, limit);
    }
    private static double Finite(double value, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : 0;
    private struct Axis
    {
        public double Position, Velocity;
        public void Step(double target, double frequency, double damping, double dt, double limit)
        {
            Velocity += ((target - Position) * frequency * frequency - 2 * damping * frequency * Velocity) * dt;
            Position += Velocity * dt;
            if (Math.Abs(Position) > limit) { Position = Math.Clamp(Position, -limit, limit); Velocity *= -.15; }
        }
    }
}
