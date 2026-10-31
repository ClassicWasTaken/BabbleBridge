// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace BabbleFrameBridge.App;

public sealed partial class HologramKittyOverlay
{
    private enum WorldMode { Free, Approach, Grip, Pull, Perched, Landing }
    private readonly KittyCarryGesture _carry = new();
    private WorldMode _worldMode;
    private KittyPerchTarget? _perch, _pressedPerch;
    private KittyClimbRoute _route;
    private Point _segmentStart, _segmentEnd;
    private double _worldAge, _worldDuration, _surfaceAge, _nextClimbAt, _perchDwell, _carryTimestamp;
    private bool _releasingCapture;

    /// <summary>Current, clipped panel tops in overlay coordinates. No controls are modified.</summary>
    public Func<IReadOnlyList<KittySurface>>? GetSurfaces { get; set; }
    public bool IsDragging => _carry.IsDragging;
    public bool IsPerched => _worldMode == WorldMode.Perched;
    public bool IsClimbing => _worldMode is WorldMode.Approach or WorldMode.Grip or WorldMode.Pull;
    public string? CurrentPerchSurfaceId => _perch?.SurfaceId;
    public Point FeetPosition => new(_position.X + KittyPanelWorld.FeetX, _position.Y - _lift + KittyPanelWorld.FeetY);

    public bool BeginPickup(Point point)
    {
        Dispatcher.VerifyAccess();
        if (_disposed || !_active || !CanDisplayCaption() || !HitCat(point)) return false;
        var displayed = new Point(_position.X, _position.Y - _lift);
        if (!_carry.Begin(point.X, point.Y, displayed.X, displayed.Y)) return false;
        _pressedPerch = IsPerched ? _perch : null;
        _perch = null; _worldMode = WorldMode.Free; _worldAge = 0;
        _position = displayed; _lift = 0; _velocity = default;
        _decisionAccumulator = 0;
        _carryTimestamp = Timestamp();
        PlaceCat();
        return true;
    }

    public void MovePickup(Point point)
    {
        Dispatcher.VerifyAccess();
        if (_disposed || !_carry.IsPressed) return;
        var now = Timestamp();
        var next = _carry.Move(point.X, point.Y, now - _carryTimestamp, ActualWidth, ActualHeight);
        _carryTimestamp = now;
        _position = new Point(next.X, next.Y);
        _velocity = new Vector(_carry.Velocity.X, _carry.Velocity.Y);
        _pointer = point; _lift = 0;
        PlaceCat(); RenderWorldFrame(.5); InvalidateVisual();
    }

    public void EndPickup(Point point)
    {
        Dispatcher.VerifyAccess();
        if (!_carry.IsPressed) return;
        MovePickup(point);
        var release = _carry.End();
        ReleasePickupCapture();
        _velocity = default; _lift = 0;
        if (!release.WasDragging)
        {
            RestorePressedPerch();
            Pester(point);
        }
        else
        {
            _pressedPerch = null;
            if (KittyPanelWorld.TryFindNearestPerch(ReadSurfaces(), ActualWidth, ActualHeight,
                _position.X, _position.Y, 72, out var target))
            {
                if (SystemParameters.ClientAreaAnimation)
                {
                    _perch = target;
                    StartSegment(WorldMode.Landing, PointFor(target.Position), .25);
                }
                else SetPerch(target);
            }
            else if (SystemParameters.ClientAreaAnimation) StartSegment(WorldMode.Landing, ClampWorldPosition(_position), .35);
            else { _worldMode = WorldMode.Free; _target = _position; }
            ShowCaption(KittyCaptionKind.Surprise);
            _nextClimbAt = _seconds + 15;
        }
        PlaceCat(); RenderWorldFrame(.5); InvalidateVisual();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CancelPickup()
    {
        Dispatcher.VerifyAccess();
        if (!_carry.IsPressed) { ReleasePickupCapture(); return; }
        _carry.Cancel(); _velocity = default; _lift = 0;
        RestorePressedPerch();
        _position = ClampWorldPosition(_position); _target = _position;
        ReleasePickupCapture(); PlaceCat(); RenderWorldFrame(.5); InvalidateVisual();
    }

    private void RestorePressedPerch()
    {
        if (_pressedPerch is { } previous && KittyPanelWorld.TryRefreshPerch(previous, ReadSurfaces(),
            ActualWidth, ActualHeight, out var updated)) SetPerch(updated);
        _pressedPerch = null;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (!_carry.IsPressed) return;
        MovePickup(args.GetPosition(this)); args.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.ChangedButton != MouseButton.Left || !_carry.IsPressed) return;
        EndPickup(args.GetPosition(this)); args.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        base.OnLostMouseCapture(args);
        if (!_releasingCapture) CancelPickup();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        base.OnPreviewKeyDown(args); PickupKeyDown(this, args);
    }

    private void PickupKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || !_carry.IsPressed) return;
        CancelPickup(); args.Handled = true;
    }

    private void OwnerDeactivated(object? sender, EventArgs args) => CancelPickup();
    private void ReleasePickupCapture()
    {
        if (!IsMouseCaptured || _releasingCapture) return;
        _releasingCapture = true;
        try { ReleaseMouseCapture(); }
        finally { _releasingCapture = false; }
    }

    private IReadOnlyList<KittySurface> ReadSurfaces()
    {
        try { return GetSurfaces?.Invoke() ?? Array.Empty<KittySurface>(); }
        catch (InvalidOperationException) { return Array.Empty<KittySurface>(); }
    }

    /// <summary>Reconcile a ledge with layout even when Windows animations are disabled.</summary>
    public void RefreshSurfaces()
    {
        Dispatcher.VerifyAccess();
        if (_disposed || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_perch is { } previous)
        {
            if (KittyPanelWorld.TryRefreshPerch(previous, ReadSurfaces(), ActualWidth, ActualHeight, out var updated))
            {
                _perch = updated;
                if (IsPerched) { _position = PointFor(updated.Position); _lift = 0; }
                else if (_worldMode == WorldMode.Landing) _segmentEnd = PointFor(updated.Position);
                else if (IsClimbing && updated != previous)
                {
                    _route = KittyPanelWorld.CreateClimbRoute(updated, ActualWidth, ActualHeight, _position.X, _position.Y);
                    StartSegment(WorldMode.Approach, PointFor(_route.Approach), .8);
                }
            }
            else
            {
                _perch = null; _worldMode = WorldMode.Free; _velocity = default;
                _position = ClampWorldPosition(_position); _target = _position;
                _nextClimbAt = _seconds + 5;
            }
        }
        else if (!_carry.IsPressed) _position = ClampWorldPosition(_position);
        PlaceCat(); RenderWorldFrame(.5); InvalidateVisual();
    }

    /// <summary>Ask the companion to use a real reachable panel, through its ordinary climb controller.</summary>
    public bool TryClimbTo(string surfaceId)
    {
        Dispatcher.VerifyAccess();
        if (_disposed || !_active || _carry.IsPressed) return false;
        var surfaces = ReadSurfaces().Where(surface => surface.Id == surfaceId).ToArray();
        if (!KittyPanelWorld.TryChooseClimbTarget(surfaces, ActualWidth, ActualHeight,
            _position.X, _position.Y, _random.Next(), out var target)) return false;
        _perch = target; _lift = 0; _velocity = default;
        _route = KittyPanelWorld.CreateClimbRoute(target, ActualWidth, ActualHeight, _position.X, _position.Y);
        if (!SystemParameters.ClientAreaAnimation) SetPerch(target);
        else StartSegment(WorldMode.Approach, PointFor(_route.Approach),
            Math.Clamp((_position - PointFor(_route.Approach)).Length / 145, .4, 2));
        _nextClimbAt = _seconds + 20;
        RenderWorldFrame(.5);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool UpdateWorld(double dt)
    {
        _surfaceAge += dt;
        if (_surfaceAge >= .15) { _surfaceAge = 0; RefreshSurfaces(); }
        if (_carry.IsPressed) { _lift = 0; PlaceCat(); return true; }
        _worldAge += dt;
        if (_worldMode == WorldMode.Free)
        {
            if (_seconds >= _nextClimbAt && _mind.Stimulation < .5
                && _mind.Action is KittyAction.Watch or KittyAction.Walk or KittyAction.Inspect or KittyAction.LookAround or KittyAction.Sniff)
            {
                _nextClimbAt = _seconds + 5;
                if (KittyPanelWorld.TryChooseClimbTarget(ReadSurfaces(), ActualWidth, ActualHeight,
                    _position.X, _position.Y, _random.Next(), out var target)) TryClimbTo(target.SurfaceId);
            }
            if (_worldMode == WorldMode.Free) return false;
        }
        if (_worldMode == WorldMode.Perched)
        {
            _velocity = default; _lift = 0;
            if (_perch is { } perched) _position = PointFor(perched.Position);
            var startled = _worldAge > .8 && _mind.Stimulation > .75
                && _mind.Action is KittyAction.Zoomies or KittyAction.Chase or KittyAction.Backoff or KittyAction.Startle;
            if (startled || _worldAge > _perchDwell && _mind.Action is not (KittyAction.Sleep or KittyAction.Doze or KittyAction.Loaf))
            {
                _perch = null;
                StartSegment(WorldMode.Landing, ClampWorldPosition(_position + new Vector(_facing * 30, 100)), .55);
            }
            PlaceCat(); return true;
        }
        var t = Math.Clamp(_worldAge / Math.Max(.1, _worldDuration), 0, 1);
        var ease = t * t * (3 - 2 * t);
        var prior = _position;
        _position = _segmentStart + (_segmentEnd - _segmentStart) * ease;
        _velocity = dt > 0 ? (_position - prior) / dt : default;
        if (Math.Abs(_velocity.X) > 3) _facing = Math.Sign(_velocity.X);
        _lift = 0; PlaceCat();
        if (t < 1) return true;
        switch (_worldMode)
        {
            case WorldMode.Approach: StartSegment(WorldMode.Grip, PointFor(_route.Grip), .7); break;
            case WorldMode.Grip: StartSegment(WorldMode.Pull, PointFor(_route.Top), .85); break;
            case WorldMode.Pull when _perch is { } target: SetPerch(target); break;
            case WorldMode.Landing when _perch is { } landing: SetPerch(landing); break;
            default: _worldMode = WorldMode.Free; _target = _position; _velocity = default; break;
        }
        return true;
    }

    private void SetPerch(KittyPerchTarget target)
    {
        _perch = target; _worldMode = WorldMode.Perched; _worldAge = 0;
        _perchDwell = 12 + _random.NextDouble() * 14;
        _position = PointFor(target.Position); _target = _position; _velocity = default; _lift = 0;
        PlaceCat();
    }

    private void StartSegment(WorldMode mode, Point destination, double duration)
    {
        _worldMode = mode; _worldAge = 0; _worldDuration = duration;
        _segmentStart = _position; _segmentEnd = destination;
    }

    private void CompleteWorldWithoutMotion()
    {
        if (_carry.IsPressed) return;
        if (_perch is { } previous && KittyPanelWorld.TryRefreshPerch(previous, ReadSurfaces(),
            ActualWidth, ActualHeight, out var target)) SetPerch(target);
        else if (_worldMode != WorldMode.Free)
        {
            _position = ClampWorldPosition(_worldMode == WorldMode.Landing ? _segmentEnd : _position);
            _worldMode = WorldMode.Free; _perch = null; _target = _position;
        }
    }

    private void RenderWorldFrame(double phase, bool advance = false)
    {
        // Pointer and layout events may arrive several times between display
        // ticks. Only the composition frame advances the articulated clocks.
        if (!advance) _cat.UpdateRig(0, _velocity.X, _velocity.Y, _facing);
        if (!_rendering) _cat.ResetRig();
        var pose = _carry.IsDragging ? KittyInteractionPose.Carried : _worldMode switch
        {
            WorldMode.Grip or WorldMode.Pull => KittyInteractionPose.Climbing,
            WorldMode.Perched => KittyInteractionPose.Perched,
            WorldMode.Landing => KittyInteractionPose.Landing,
            _ => KittyInteractionPose.None
        };
        var action = _carry.IsPressed ? KittyAction.Watch : _worldMode switch
        {
            WorldMode.Approach => KittyAction.Walk,
            WorldMode.Grip or WorldMode.Pull or WorldMode.Landing => KittyAction.Watch,
            WorldMode.Perched when _mind.Action is not (KittyAction.Groom or KittyAction.PawLick or KittyAction.FaceWash
                or KittyAction.Loaf or KittyAction.Sleep or KittyAction.Doze or KittyAction.SlowBlink
                or KittyAction.EarFlick or KittyAction.Chirp or KittyAction.Meow or KittyAction.Yawn) => KittyAction.Watch,
            _ => _mind.Action
        };
        _cat.SetInteractionPose(pose, Math.Clamp(_worldAge / Math.Max(.1, _worldDuration), 0, 1), _velocity.X, _velocity.Y);
        _cat.RenderFrame(action, phase, _seconds, _gaze.X, _gaze.Y, _facing, _mind.Stimulation);
    }

    private Point ClampWorldPosition(Point position) => PointFor(KittyPanelWorld.ClampPosition(position.X, position.Y, ActualWidth, ActualHeight));
    private static Point PointFor(KittyPoint point) => new(point.X, point.Y);
    private static double Timestamp() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
