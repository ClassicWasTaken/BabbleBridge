// Copyright (c) 2026 Face Tracking Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace BabbleFrameBridge.App;

/// <summary>
/// Original procedural robot-kitty artwork. All joints and expressions are drawn
/// independently for this app; no mascot artwork, animation frames or model assets
/// from another project are included. The caller owns the animation clock.
/// </summary>
public sealed class HologramKittyVisual : FrameworkElement
{
    private static readonly Brush Shell = Brush("#DE143646"), ShellLight = Brush("#D5224A5B"),
        ShellShade = Brush("#ED102631"), Visor = Brush("#F0081A2A"), Cyan = Brush("#FF6AE3F9"),
        Pale = Brush("#FFF0FFFF"), Blue = Brush("#FF9FC6FF"), Lavender = Brush("#FFB8C4FF"),
        Glass = Brush("#253FF0FF"), Dark = Brush("#FF122C3B"), Shadow = Brush("#42152136"),
        Halo = Brush("#146AE3F9"), Spark = Brush("#A09CEFFF"), Cheek = Brush("#4487C9FF");
    private static readonly Pen Edge = Pen(Cyan, 1.55), SoftEdge = Pen(Brush("#636AE3F9"), .9),
        Glow = Pen(Brush("#156AE3F9"), 6), Fine = Pen(Brush("#8E6AE3F9"), .7),
        WhiteLine = Pen(Pale, 1.5), PurpleLine = Pen(Lavender, 1.8),
        TailGlow = Pen(Brush("#186AE3F9"), 10), TailRim = Pen(Brush("#AA6AE3F9"), 6),
        TailCore = Pen(ShellLight, 3.5), ShadowLine = Pen(Shadow, 2.8);
    private static readonly Geometry Head = Geometry("M39,29 C37,28 33,32 32,37 L29,67 C28,74 33,80 40,81 L101,81 C108,81 113,76 112,69 L109,39 C109,33 105,29 100,29 Z");
    private static readonly Geometry HeadSide = Geometry("M101,29 L113,36 117,68 111,77 108,41Z");
    private static readonly Geometry LeftEar = Geometry("M39,31 L39,11 Q40,7 43,10 L60,28Z");
    private static readonly Geometry RightEar = Geometry("M83,28 L100,8 Q103,5 104,10 L107,34Z");
    private static readonly Geometry LeftEarInset = Geometry("M44,26 L44,16 53,26Z");
    private static readonly Geometry RightEarInset = Geometry("M92,26 L100,16 102,28Z");
    private static readonly Geometry VisorShape = Geometry("M42,37 L98,37 Q102,37 102,42 L105,67 Q105,73 99,73 L42,73 Q36,73 37,67 L38,43 Q38,37 42,37Z");
    private static readonly Geometry Chest = Geometry("M60,80 C48,84 45,97 47,111 Q48,122 62,125 L87,124 Q101,120 100,109 L97,94 Q94,80 82,79Z");
    private static readonly Geometry ChestShine = Geometry("M61,85 C53,91 52,104 55,113 Q64,122 72,115 L78,87Z");
    private static readonly Geometry Core = Geometry("M70,90 L78,94 78,102 70,106 63,102 63,94Z");
    private static readonly Geometry Mouth = Geometry("M66,63 Q69,67 72,63 Q75,67 79,63");
    private static readonly Geometry Smile = Geometry("M66,63 Q71,70 78,63");
    private KittyAction _action;
    private double _phase, _time, _lookX, _lookY, _facing = 1, _excitement;
    private readonly KittyPoseBlend _poses = new();
    private readonly double[] _channels = new double[12];
    private readonly Point[] _tailPoints = new Point[9];
    private double _frameDelta, _gaitPhase;
    private bool _poseReady, _resetPose, _channelsReady;
    private readonly KittySpringRig _rig = new();
    private readonly ContainerVisual _scene = new();
    private readonly DrawingVisual[] _parts = Enumerable.Range(0, 23).Select(_ => new DrawingVisual()).ToArray();
    private readonly MatrixTransform _sceneTransform = new();
    private bool _usesRigMotion;
    private Matrix _headHitTransform = Matrix.Identity;
    private readonly Matrix[] _bodyHitTransforms = new Matrix[3];
    private readonly Rect[] _bodyHitRects = new Rect[3];
    private readonly Point[] _pawHitPoints = new Point[4];
    private Point[] _tailHitPoints = [];

    public int BodyPartCount => _parts.Length;
    public KittyRigSnapshot RigSnapshot => _rig.Snapshot;
    public double PoseTransitionEnergy => _poses.TransitionEnergy;
    public double MouthOpenness => Weight(KittyAction.Yawn) * Math.Sin(Math.PI * Unit((_phase - .12) / .72))
        + Weight(KittyAction.Meow) * Math.Sin(Math.PI * Unit((_phase - .12) / .55)) * .76
        + Weight(KittyAction.Chirp) * Math.Max(0, Math.Sin(_phase * Math.PI * 3)) * .5;

    public HologramKittyVisual()
    {
        Width = Height = 144;
        Focusable = false;
        SnapsToDevicePixels = false;
        _scene.Transform = _sceneTransform;
        foreach (var part in _parts) _scene.Children.Add(part);
        AddVisualChild(_scene);
        AutomationProperties.SetName(this, "Relay, holographic robot kitty");
    }

    public void UpdateRig(double deltaSeconds, double velocityX, double velocityY, double facing = 1)
    {
        _usesRigMotion = true;
        _frameDelta = double.IsFinite(deltaSeconds) ? Math.Clamp(deltaSeconds, 0, .1) : 0;
        _rig.Step(deltaSeconds, velocityX, velocityY, facing);
    }

    public void ResetRig() { _rig.Reset(_facing); _poses.Reset((int)_action); _resetPose = true; _frameDelta = 0; _channelsReady = false; InvalidateVisual(); }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _scene : throw new ArgumentOutOfRangeException(nameof(index));

    /// <param name="phase">Normalized progress through the current action, from zero to one.</param>
    public void RenderFrame(KittyAction action, double phase, double time, double lookX = 0,
        double lookY = 0, double facing = 1, double excitement = 0)
    {
        if ((int)action < 0 || (int)action >= _poses.Count) action = KittyAction.Watch;
        _action = action; _phase = Unit(phase); _time = double.IsFinite(time) ? Math.Max(0, time) : 0;
        if (!_poseReady || _resetPose || !_usesRigMotion) { _poses.Reset((int)action); _poseReady = true; _resetPose = false; }
        else _poses.Step(_frameDelta, (int)action);
        var smoothing = _usesRigMotion ? 1 - Math.Exp(-_frameDelta * 13) : 1;
        _lookX += (SignedUnit(lookX) - _lookX) * smoothing; _lookY += (SignedUnit(lookY) - _lookY) * smoothing;
        _facing = facing < 0 ? -1 : 1;
        _excitement = Unit(excitement);
        var frequency = 2 + Weight(KittyAction.Walk) * 7 + (Weight(KittyAction.Chase) + Weight(KittyAction.Circle)
            + Weight(KittyAction.Backoff)) * 13 + Weight(KittyAction.Zoomies) * 20 + Weight(KittyAction.TailChase) * 17;
        _gaitPhase = _usesRigMotion ? _gaitPhase + _frameDelta * frequency : _time * frequency;
        InvalidateVisual();
    }

    private double Weight(KittyAction action) => _poses[(int)action];
    private double Smooth(int channel, double target)
    {
        if (!_channelsReady || !_usesRigMotion || _frameDelta <= 0) return _channels[channel] = target;
        return _channels[channel] += (target - _channels[channel]) * (1 - Math.Exp(-_frameDelta * 17));
    }
    private static double Mix(double from, double to, double amount) => from + (to - from) * Math.Clamp(amount, 0, 1);

    /// <summary>A separate original cat-face drawing for the companion's launcher.</summary>
    public static DrawingImage CreateHeadIcon()
    {
        var group = new DrawingGroup();
        using (var drawing = group.Open())
        {
            drawing.PushTransform(new ScaleTransform(.29, .29));
            drawing.PushTransform(new TranslateTransform(-25, -4));
            DrawHead(drawing, 0, 0, 0, false, false, false, 0, 0, true);
            drawing.Pop(); drawing.Pop();
        }
        var image = new DrawingImage(group); image.Freeze(); return image;
    }

    public bool ContainsBody(Point localPoint)
    {
        var scale = Math.Min(ActualWidth / 144, ActualHeight / 144);
        if (scale <= 0 || !double.IsFinite(localPoint.X) || !double.IsFinite(localPoint.Y)) return false;
        var x = (localPoint.X - (ActualWidth - 144 * scale) * .5) / scale;
        var y = (localPoint.Y - (ActualHeight - 144 * scale) * .5) / scale;
        var turn = _usesRigMotion ? _rig.Snapshot.FacingScale : _facing;
        if (Math.Abs(turn) < .08) { if (Math.Abs(x - 72) > 7) return false; x = 72; }
        else x = 72 + (x - 72) / turn;
        var point = new Point(x, y);
        var head = Inverse(_headHitTransform, point);
        if (new Rect(28, 24, 88, 61).Contains(head)) return true;
        // Small margins make the thin holographic ears and paws pleasant to pet.
        if (LeftEar.FillContains(head) || RightEar.FillContains(head)) return true;
        for (var i = 0; i < _bodyHitRects.Length; i++)
            if (!_bodyHitRects[i].IsEmpty && _bodyHitRects[i].Contains(Inverse(_bodyHitTransforms[i], point))) return true;
        foreach (var paw in _pawHitPoints)
            if (double.IsFinite(paw.X) && new Rect(paw.X - 9, paw.Y - 6, 18, 12).Contains(point)) return true;
        for (var i = 1; i < _tailHitPoints.Length; i++)
        {
            var a = _tailHitPoints[i - 1]; var segment = _tailHitPoints[i] - a;
            var t = segment.LengthSquared > .001 ? Math.Clamp(Vector.Multiply(point - a, segment) / segment.LengthSquared, 0, 1) : 0;
            if ((point - (a + segment * t)).Length <= 5.5) return true;
        }
        return false;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        ContainsBody(hitTestParameters.HitPoint) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
    private static Point Inverse(Matrix matrix, Point point)
    {
        if (!matrix.HasInverse) return new Point(double.NaN, double.NaN);
        matrix.Invert(); return matrix.Transform(point);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var scale = Math.Min(ActualWidth / 144, ActualHeight / 144);
        if (scale <= 0) return;
        var rig = _rig.Snapshot;
        var turn = _usesRigMotion ? rig.FacingScale : _facing;
        _sceneTransform.Matrix = new Matrix(scale * turn, 0, 0, scale,
            (ActualWidth - 144 * scale) * .5 + 72 * scale * (1 - turn), (ActualHeight - 144 * scale) * .5);
        var run = Weight(KittyAction.Walk) + Weight(KittyAction.Chase) + Weight(KittyAction.Zoomies)
            + Weight(KittyAction.Circle) + Weight(KittyAction.Backoff) + Weight(KittyAction.TailChase);
        var sleep = Weight(KittyAction.Sleep);
        var doze = Weight(KittyAction.Doze);
        var loafWeight = Weight(KittyAction.Loaf) + doze * .75;
        var rest = Unit(sleep + loafWeight + Weight(KittyAction.Flop) * .8);
        var wash = Weight(KittyAction.Groom) + Weight(KittyAction.PawLick) + Weight(KittyAction.FaceWash);
        var anger = Weight(KittyAction.Grumpy);
        var stretch = Weight(KittyAction.Stretch);
        var stalk = Weight(KittyAction.Stalk) + Weight(KittyAction.ButtWiggle);
        var pounce = Weight(KittyAction.Pounce);
        var knead = Weight(KittyAction.Knead);
        var roll = Weight(KittyAction.Roll);
        var rub = Weight(KittyAction.Rub);
        var startle = Weight(KittyAction.Startle);
        var peek = Weight(KittyAction.Peek);
        var wiggle = Weight(KittyAction.ButtWiggle);
        var tailChase = Weight(KittyAction.TailChase);
        var flop = Weight(KittyAction.Flop);
        var blinkWeight = Weight(KittyAction.SlowBlink);
        var gait = Math.Sin(_gaitPhase);
        var breath = Math.Sin(_time * Mix(2.5, 1.8, sleep));
        var burst = Math.Sin(Math.PI * _phase);
        var bounce = Smooth(0, Mix(breath * .65, -Math.Abs(gait) * (2.2 + Weight(KittyAction.Zoomies) * 2.3), run)
            - pounce * Math.Sin(Math.PI * Unit((_phase - .23) / .66)) * 11 - startle * burst * 2.5);
        var lean = Smooth(1, run * (3 + _excitement * 2) - anger * 3 - stretch * 7 + rub * Math.Sin(_time * 3) * 5
            + roll * burst * 18 - Weight(KittyAction.Backoff) * 6 - flop * burst * 18
            + tailChase * Math.Sin(_time * 6) * 10);
        var leftEnd = new Point(60 - gait * run * 13, 127 - Math.Max(0, gait) * run * 9);
        var rightEnd = new Point(86 + gait * run * 13, 127 - Math.Max(0, -gait) * run * 9);
        leftEnd = new Point(Mix(leftEnd.X, 43 - burst * 7, stretch), Mix(leftEnd.Y, 130, stretch));
        rightEnd = new Point(Mix(rightEnd.X, 92 + burst * 13, stretch), Mix(rightEnd.Y, 130, stretch));
        leftEnd.Y -= Math.Max(0, Math.Sin(_time * 5)) * knead * 8;
        rightEnd.Y -= Math.Max(0, -Math.Sin(_time * 5)) * knead * 8;
        leftEnd.X -= (stalk + pounce) * 7; rightEnd.X += (stalk + pounce) * 8;
        leftEnd.Y -= pounce * burst * 7;
        rightEnd = new Point(Mix(rightEnd.X, 72 + Math.Sin(_time * 7) * 3, wash),
            Mix(rightEnd.Y, 71 + Math.Cos(_time * 7) * 2, wash));
        var swat = Weight(KittyAction.Swat) + Weight(KittyAction.SmackPanel);
        var extension = Weight(KittyAction.SmackPanel) * Math.Sin(Math.PI * Math.Min(1, _phase * 1.25))
            + Weight(KittyAction.Swat) * burst;
        rightEnd = new Point(Mix(rightEnd.X, 85 + extension * 34, swat),
            Mix(rightEnd.Y, 126 - extension * 62 + _lookY * extension * 14, swat));
        var scratch = Weight(KittyAction.Scratch);
        leftEnd = new Point(Mix(leftEnd.X, 109, scratch), Mix(leftEnd.Y, 94 + Math.Sin(_time * 12) * 12, scratch));
        rightEnd = new Point(Mix(rightEnd.X, 116, scratch), Mix(rightEnd.Y, 94 - Math.Sin(_time * 12) * 12, scratch));
        leftEnd = new Point(Smooth(2, Mix(leftEnd.X, 48, startle)), Smooth(3, Mix(leftEnd.Y, 108 - burst * 8, startle)));
        rightEnd = new Point(Smooth(4, Mix(rightEnd.X, 98, startle)), Smooth(5, Mix(rightEnd.Y, 108 - burst * 8, startle)));
        var headY = Smooth(6, sleep * (34 + breath * .5) + loafWeight * 22 + wash * 9 + stretch * 14 * burst
            + stalk * 12 + pounce * 8 + roll * 12 + peek * 10 + flop * 23
            + (1 - Unit(rest + wash + stretch + stalk + pounce + roll + peek + startle + flop)) * (run * gait * .7 + _lookY * 1.4));
        var headAngle = Smooth(7, sleep * -13 + wash * (-14 + Math.Sin(_time * 6) * 2) - anger * 7
            + rub * Math.Sin(_time * 3) * 15 + roll * burst * 25 - Weight(KittyAction.Yawn) * 7 * burst
            - peek * 12 - flop * burst * 17 + wiggle * Math.Sin(_time * 13) * 2
            + (1 - Unit(sleep + wash + anger + rub + roll + peek + flop)) * (_lookX * 5 - run * 4 + Math.Sin(_time * .65) * 1.5));
        var headX = Smooth(8, (Weight(KittyAction.Sniff) + Weight(KittyAction.Inspect)) * (3 + Math.Sin(_time * 6) * 1.5)
            + peek * 5 + rub * Math.Sin(_time * 3) * 2);
        var faceBlink = Unit((1 - sleep) * (1 - doze * .7) * (1 - wash * .72)
            * (1 - blinkWeight * Math.Pow(burst, .35)) * (1 - Weight(KittyAction.Yawn) * .88));
        var tail = TailPoints(run, sleep, anger, tailChase, wiggle);
        _tailHitPoints = tail;
        Array.Fill(_pawHitPoints, new Point(double.NaN, double.NaN));
        for (var i = 0; i < _parts.Length; i++)
        {
            using var part = _parts[i].RenderOpen();
            if (i == 0) { DrawProjector(part); continue; }
            if (i is >= 1 and <= 8)
            {
                var link = i - 1;
                var a = tail[link]; var b = tail[link + 1];
                part.DrawLine(TailGlow, a, b); part.DrawLine(TailRim, a, b); part.DrawLine(TailCore, a, b);
                part.DrawEllipse(ShellLight, SoftEdge, b, link == 7 ? 2.1 : 2.6, link == 7 ? 2.1 : 2.6);
                if (link == 7) part.DrawEllipse(Pale, null, b, 1.2, 1.2);
                continue;
            }
            var joint = i switch { 11 => rig.Hip, 12 or 14 => rig.Belly, 13 => rig.Shoulder,
                >= 17 and <= 20 or 22 => rig.Head, 9 or 10 => _rig.LimbOffset(i == 10, false),
                _ => _rig.LimbOffset(i == 16, true) };
            part.PushTransform(new TranslateTransform(joint.X + (i is >= 17 and <= 20 or 22 ? headX : 0), bounce + joint.Y));
            part.PushTransform(new RotateTransform(lean + joint.Angle, 72, 118));
            var hitMatrix = Matrix.Identity;
            hitMatrix.RotateAt(lean + joint.Angle, 72, 118);
            hitMatrix.Translate(joint.X + (i is >= 17 and <= 20 or 22 ? headX : 0), bounce + joint.Y);
            switch (i)
            {
                case 9:
                    part.PushOpacity(1 - rest); DrawLimb(part, new Point(58, 104), new Point(48 + gait * run * 8, 120), new Point(46 - gait * run * 11, 127), .65); part.Pop(); _pawHitPoints[0] = hitMatrix.Transform(new Point(46 - gait * run * 11, 127));
                    break;
                case 10:
                    part.PushOpacity(1 - rest); DrawLimb(part, new Point(88, 105), new Point(98 - gait * run * 8, 120), new Point(101 + gait * run * 11, 127), .65); part.Pop(); _pawHitPoints[1] = hitMatrix.Transform(new Point(101 + gait * run * 11, 127));
                    break;
                case 11: case 12: case 13:
                    var band = i - 11;
                    var cx = Mix(74 - band, 90 - band * 15, sleep) + wiggle * Math.Sin(_time * 15 - band * .9) * (3 - band);
                    var cy = Mix(117.0 - band * 13, 117.0 - band * 2, rest) + stalk * band * 5 + pounce * band * (1 - burst) * 4;
                    part.PushTransform(new ScaleTransform(rig.StretchX, rig.StretchY, 73, 117));
                    var width = Mix(band == 2 ? 37 : band == 1 ? 43 : 45, 32, sleep);
                    var height = Mix(band == 0 ? 21 : 23, 21, rest);
                    _bodyHitRects[band] = new Rect(cx - width / 2, cy - height / 2, width, height);
                    var bodyHit = Matrix.Identity; bodyHit.ScaleAt(rig.StretchX, rig.StretchY, 73, 117); bodyHit.Append(hitMatrix);
                    _bodyHitTransforms[band] = bodyHit;
                    part.DrawRoundedRectangle(ShellShade, Glow, new Rect(cx - width / 2, cy - height / 2, width, height), 10, 10);
                    part.DrawRoundedRectangle(band == 1 ? ShellLight : Shell, Edge, new Rect(cx - width / 2, cy - height / 2, width, height), 10, 10);
                    part.DrawEllipse(Glass, null, new Point(cx - 5, cy - 1), width * .31, height * .34);
                    part.DrawLine(Fine, new Point(cx - width * .32, cy + height * .24), new Point(cx + width * .29, cy + height * .24));
                    part.Pop();
                    break;
                case 14:
                    part.PushOpacity(1 - rest);
                    {
                        part.DrawGeometry(Visor, SoftEdge, Core);
                        part.DrawEllipse(Cyan, null, new Point(70, 98), 2.8, 2.8);
                        part.DrawEllipse(Pale, null, new Point(69.2, 97.2), 1, 1);
                    }
                    part.Pop();
                    break;
                case 15:
                    _pawHitPoints[2] = hitMatrix.Transform(new Point(Mix(leftEnd.X, 51, rest), Mix(leftEnd.Y, 126, rest)));
                    part.PushOpacity(rest); DrawPaw(part, 51, 126, 0, .9); part.Pop();
                    part.PushOpacity(1 - rest); DrawLimb(part, new Point(58, 88), new Point(56, 109), leftEnd, 1); part.Pop();
                    break;
                case 16:
                    _pawHitPoints[3] = hitMatrix.Transform(new Point(Mix(rightEnd.X, 96, rest), Mix(rightEnd.Y, 126, rest)));
                    part.PushOpacity(rest); DrawPaw(part, 96, 126, 0, .9); part.Pop();
                    part.PushOpacity(1 - rest); DrawLimb(part, new Point(85, 89), new Point(90 + (rightEnd.X - 86) * .4, (90 + rightEnd.Y) * .5), rightEnd, 1); part.Pop();
                    part.PushOpacity(Unit(swat + scratch) * Unit((burst - .3) * 3));
                    {
                        part.DrawLine(Fine, new Point(rightEnd.X + 8, rightEnd.Y - 9), new Point(rightEnd.X + 15, rightEnd.Y - 14));
                        part.DrawLine(Fine, new Point(rightEnd.X + 11, rightEnd.Y - 2), new Point(rightEnd.X + 20, rightEnd.Y - 3));
                    }
                    part.Pop();
                    break;
                case 17: case 18: case 19: case 20:
                    if (i == 17) { var headHit = Matrix.Identity; headHit.RotateAt(headAngle, 72, 59); headHit.Translate(0, headY); headHit.Append(hitMatrix); _headHitTransform = headHit; }
                    var layer = i == 17 ? HeadLayer.Shell : i == 18 ? HeadLayer.LeftEar : i == 19 ? HeadLayer.RightEar : HeadLayer.Face;
                    var flick = Weight(KittyAction.EarFlick) * Math.Sin(_phase * Math.PI * 4) * Math.Pow(Math.Sin(Math.PI * _phase), 2) * 20;
                    var lazyFlick = Math.Pow(Math.Max(0, Math.Sin(_time * .61)), 18) * Math.Sin(_time * 19) * 3;
                    var ear = i == 18 ? rig.LeftEarAngle - anger * 11 - startle * 16 + lazyFlick
                        : i == 19 ? rig.RightEarAngle + anger * 11 + startle * 16 + flick - lazyFlick * .6 : 0;
                    DrawHead(part, headY, headAngle, _time, false, false, false, _lookX * _facing, _lookY, false,
                        layer, MouthOpenness, faceBlink, ear, anger, startle);
                    break;
                case 21:
                    part.PushOpacity(Unit(sleep + doze * .7)); DrawSleep(part); part.Pop();
                    part.PushOpacity(anger); DrawGrumpy(part); part.Pop();
                    part.PushOpacity(Weight(KittyAction.Zoomies)); DrawSpeed(part); part.Pop();
                    part.PushOpacity(Unit(Weight(KittyAction.Sniff) + Weight(KittyAction.Inspect)));
                    var sniff = Math.Sin(_time * 7);
                    part.DrawEllipse(null, Fine, new Point(111 + sniff, 63), 2 + sniff * .5, 2);
                    part.Pop();
                    break;
                case 22:
                    part.PushOpacity(wash);
                    var washingFace = Weight(KittyAction.FaceWash);
                    var washY = Mix(80 + Math.Cos(_time * 7) * 2, 57 + Math.Sin(_time * 5) * 10, washingFace);
                    DrawPaw(part, 74 + Math.Sin(_time * 7) * 3, washY, -24, .8);
                    part.PushOpacity(Unit(Math.Sin(_time * 9) * 3));
                    part.DrawEllipse(Lavender, null, new Point(73, 76), 1.7, 2.8);
                    part.Pop(); part.Pop();
                    break;
            }
            part.Pop(); part.Pop();
        }
        _channelsReady = true;
    }

    private Point[] TailPoints(double running, double sleeping, double grumpy, double chasing, double wiggle)
    {
        var points = _tailPoints;
        points[0] = new Point(96, 111);
        var swing = Math.Sin(_time * (2 + grumpy * 6 + running * 7 + chasing * 7 + wiggle * 12)) * (3 + grumpy * 5 + running * 2 + chasing * 4);
        for (var i = 1; i <= 8; i++)
        {
            var t = i / 8.0;
            Point p;
            if (t <= .625)
                p = Cubic(new Point(96, 111), new Point(126, 121), new Point(132, 99 + swing), new Point(123, 86 + swing), t / .625);
            else
                p = Cubic(new Point(123, 86 + swing), new Point(112, 72 + swing), new Point(128, 68 + swing), new Point(132, 78 + swing), (t - .625) / .375);
            var curled = Cubic(new Point(96, 111), new Point(131, 132), new Point(132, 90), new Point(109, 97), t);
            p = new Point(Mix(p.X, curled.X, sleeping), Mix(p.Y, curled.Y, sleeping));
            var caught = Cubic(new Point(96, 111), new Point(132, 113), new Point(125, 60), new Point(105, 83), t);
            p = new Point(Mix(p.X, caught.X, chasing), Mix(p.Y, caught.Y, chasing));
            var offset = _rig.TailOffset(i - 1);
            points[i] = new Point(p.X + offset.X * .65, p.Y + offset.Y * .65);
        }
        return points;
    }
    private static Point Cubic(Point a, Point b, Point c, Point d, double t)
    {
        var u = 1 - t;
        return new Point(u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
            u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y);
    }

    private void DrawProjector(DrawingContext drawing)
    {
        var breath = .5 + .5 * Math.Sin(_time * 2);
        drawing.DrawEllipse(Shadow, null, new Point(73, 136), 45, 5);
        drawing.DrawEllipse(Halo, null, new Point(73, 134), 39 + breath * 2, 5.5);
        drawing.DrawEllipse(null, SoftEdge, new Point(73, 134), 33, 4.5);
        drawing.DrawEllipse(null, Fine, new Point(73, 134), 22, 2.5);
        drawing.DrawLine(Edge, new Point(35, 134), new Point(43, 134));
        drawing.DrawLine(Edge, new Point(104, 134), new Point(112, 134));
        for (var i = 0; i < 3; i++)
        {
            var y = 126 - ((_time * 9 + i * 18) % 53);
            var x = 28 + i * 43 + Math.Sin(_time * .9 + i) * 3;
            drawing.DrawRectangle(Spark, null, new Rect(x, y, 1.4, 2.5));
        }
    }

    private enum HeadLayer { All, Shell, LeftEar, RightEar, Face }
    private static void DrawHead(DrawingContext drawing, double offsetY, double angle, double time,
        bool sleeping, bool grooming, bool grumpy, double lookX, double lookY, bool icon,
        HeadLayer layer = HeadLayer.All, double mouthOpen = 0, double blinkOverride = -1, double earAngle = 0, double anger = 0, double alert = 0)
    {
        drawing.PushTransform(new TranslateTransform(0, offsetY));
        drawing.PushTransform(new RotateTransform(angle, 72, 59));
        if (layer is HeadLayer.All or HeadLayer.LeftEar)
        {
            drawing.PushTransform(new RotateTransform(earAngle, 44, 29));
            drawing.DrawGeometry(null, Glow, LeftEar);
            drawing.DrawGeometry(ShellShade, Edge, LeftEar);
            drawing.DrawGeometry(Glass, SoftEdge, LeftEarInset);
            drawing.DrawLine(Fine, new Point(44, 19), new Point(48, 24));
            drawing.Pop();
        }
        if (layer is HeadLayer.All or HeadLayer.RightEar)
        {
            drawing.PushTransform(new RotateTransform(earAngle, 102, 29));
            drawing.DrawGeometry(null, Glow, RightEar);
            drawing.DrawGeometry(ShellShade, Edge, RightEar);
            drawing.DrawGeometry(Glass, SoftEdge, RightEarInset);
            drawing.DrawLine(Fine, new Point(98, 20), new Point(96, 24));
            drawing.Pop();
        }
        if (layer is HeadLayer.All or HeadLayer.Shell)
        {
            drawing.DrawGeometry(ShellShade, SoftEdge, HeadSide);
            drawing.DrawGeometry(null, Glow, Head);
            drawing.DrawGeometry(ShellLight, Edge, Head);
            drawing.DrawLine(WhiteLine, new Point(42, 31), new Point(60, 31));
            drawing.DrawLine(Fine, new Point(91, 33), new Point(100, 33));
            drawing.DrawLine(Fine, new Point(31, 58), new Point(25, 56));
            drawing.DrawLine(Fine, new Point(31, 62), new Point(23, 63));
            drawing.DrawLine(Fine, new Point(110, 58), new Point(118, 56));
            drawing.DrawLine(Fine, new Point(111, 62), new Point(120, 63));
            drawing.DrawEllipse(Cyan, null, new Point(109, 43), 1, 1);
        }
        if (layer is HeadLayer.All or HeadLayer.Face)
        {
            drawing.DrawGeometry(Visor, SoftEdge, VisorShape);
            drawing.PushClip(VisorShape);
            for (var y = 43; y < 73; y += 6) drawing.DrawLine(PenScan, new Point(34, y), new Point(106, y));
            var scan = 39 + (time * 9 % 34);
            if (!icon) drawing.DrawLine(Scan, new Point(37, scan), new Point(103, scan));
            drawing.DrawGeometry(VisorShine, null, VisorReflection);
            var blinkCycle = time % 5.7;
            var blink = !icon && blinkCycle > 5.50 ? Math.Max(.07, Math.Abs(blinkCycle - 5.60) * 10) : 1;
            if (sleeping) blink = 0;
            else if (blinkOverride >= 0) blink *= blinkOverride;
            else if (grooming) blink = .24;
            anger = Math.Max(anger, grumpy ? 1 : 0);
            var eyeHeight = 8.4 * blink * (1 + alert * .16);
            DrawEye(drawing, 54 + lookX * 2.3, 54 + lookY * 1.5, eyeHeight, anger, sleeping, false);
            DrawEye(drawing, 87 + lookX * 2.3, 54 + lookY * 1.5, eyeHeight * (1 - .035 * Math.Sin(time * .8)), anger, sleeping, true);
            drawing.DrawEllipse(Cheek, null, new Point(45, 65), 4, 1.6);
            drawing.DrawEllipse(Cheek, null, new Point(96, 65), 4, 1.6);
            if (!sleeping)
            {
                drawing.DrawGeometry(grumpy ? Lavender : Cyan, null, Nose);
                if (mouthOpen > .04)
                {
                    drawing.DrawEllipse(Dark, Fine, new Point(72, 66), 2.4 + mouthOpen * 4.8, 1 + mouthOpen * 6.5);
                    if (mouthOpen > .5) drawing.DrawEllipse(Lavender, null, new Point(72, 69.5), 2.4, 1.2);
                }
                else
                {
                    drawing.PushOpacity(1 - anger); drawing.DrawGeometry(null, Fine, grooming ? Smile : Mouth); drawing.Pop();
                    drawing.PushOpacity(anger);
                    drawing.DrawLine(PurpleLine, new Point(67, 68), new Point(72, 66));
                    drawing.DrawLine(PurpleLine, new Point(72, 66), new Point(77, 68));
                    drawing.Pop();
                }
            }
            drawing.Pop();
        }
        drawing.Pop(); drawing.Pop();
    }

    private static readonly Pen PenScan = Pen(Brush("#0B2CF0FF"), .5), Scan = Pen(Brush("#206AE3F9"), 1.4);
    private static readonly Brush VisorShine = Brush("#103FF0FF");
    private static readonly Geometry VisorReflection = Geometry("M39,39 L62,39 41,72 36,68Z");
    private static readonly Geometry Nose = Geometry("M69,60 L75,60 72,63Z");
    private static void DrawEye(DrawingContext drawing, double x, double y, double height, double anger, bool asleep, bool right)
    {
        if (asleep || height < 1)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            { context.BeginFigure(new Point(x - 7, y), false, false); context.QuadraticBezierTo(new Point(x, y + 5), new Point(x + 7, y), true, false); }
            geometry.Freeze(); drawing.DrawGeometry(null, Edge, geometry); return;
        }
        drawing.PushOpacity(1 - anger);
        drawing.DrawEllipse(Halo, null, new Point(x, y), 10.8, height + 2.5);
        drawing.DrawEllipse(Cyan, null, new Point(x, y), 7.8, height);
        drawing.DrawEllipse(Blue, null, new Point(x + 1.1, y + 1.2), 5.5, height * .76);
        drawing.DrawEllipse(Visor, null, new Point(x + 1.4, y + .8), 3.7, height * .66);
        drawing.DrawEllipse(Pale, null, new Point(x - 2.1, y - height * .35), 2.2, Math.Min(2.6, height * .3));
        drawing.DrawEllipse(Pale, null, new Point(x + 3.2, y + height * .43), .9, Math.Min(1.1, height * .15));
        drawing.Pop();
        drawing.PushOpacity(anger);
        drawing.PushTransform(new RotateTransform(right ? 14 : -14, x, y));
        drawing.DrawRoundedRectangle(Lavender, null, new Rect(x - 7.3, y - 2.1, 14.6, 4.2), 1.8, 1.8);
        drawing.Pop(); drawing.Pop();
    }

    private static void DrawLimb(DrawingContext drawing, Point shoulder, Point elbow, Point paw, double opacity)
    {
        drawing.PushOpacity(opacity);
        drawing.DrawLine(PenLimbGlow, shoulder, elbow);
        drawing.DrawLine(PenLimbEdge, shoulder, elbow);
        drawing.DrawLine(PenLimbFill, shoulder, elbow);
        drawing.DrawLine(PenLimbEdge, elbow, paw);
        drawing.DrawLine(PenLimbFill, elbow, paw);
        drawing.DrawEllipse(Visor, SoftEdge, elbow, 3.2, 3.2);
        drawing.DrawEllipse(Cyan, null, elbow, 1.1, 1.1);
        DrawPaw(drawing, paw.X, paw.Y, (paw.X - elbow.X) * .2, 1);
        drawing.Pop();
    }
    private static readonly Pen PenLimbGlow = Pen(Brush("#146AE3F9"), 9), PenLimbEdge = Pen(Brush("#B46AE3F9"), 5.4), PenLimbFill = Pen(ShellShade, 3.2);
    private static void DrawPaw(DrawingContext drawing, double x, double y, double angle, double scale)
    {
        drawing.PushTransform(new RotateTransform(angle, x, y));
        drawing.DrawRoundedRectangle(ShellLight, Edge, new Rect(x - 7 * scale, y - 4 * scale, 14 * scale, 7 * scale), 3 * scale, 3 * scale);
        for (var i = -1; i <= 1; i++) drawing.DrawLine(Fine, new Point(x + i * 3, y), new Point(x + i * 3, y + 2));
        drawing.Pop();
    }

    private void DrawSleep(DrawingContext drawing)
    {
        var rise = _time % 2.8 / 2.8;
        drawing.PushOpacity(1 - rise);
        drawing.PushTransform(new TranslateTransform(0, -rise * 9));
        DrawZ(drawing, 111, 48, 6);
        DrawZ(drawing, 121, 35, 8);
        drawing.Pop(); drawing.Pop();
    }
    private static void DrawZ(DrawingContext drawing, double x, double y, double size)
    {
        drawing.DrawLine(PurpleLine, new Point(x, y), new Point(x + size, y));
        drawing.DrawLine(PurpleLine, new Point(x + size, y), new Point(x, y + size));
        drawing.DrawLine(PurpleLine, new Point(x, y + size), new Point(x + size, y + size));
    }
    private void DrawGrumpy(DrawingContext drawing)
    {
        var x = 117 + Math.Sin(_time * 8) * .8;
        drawing.DrawLine(PurpleLine, new Point(x, 29), new Point(x + 3, 31));
        drawing.DrawLine(PurpleLine, new Point(x + 3, 31), new Point(x + 4, 27));
        drawing.DrawLine(PurpleLine, new Point(x + 6, 32), new Point(x + 9, 33));
        drawing.DrawLine(PurpleLine, new Point(x + 6, 34), new Point(x + 6, 38));
    }
    private void DrawSpeed(DrawingContext drawing)
    {
        var pulse = .5 + .5 * Math.Sin(_time * 18);
        drawing.PushOpacity(.35 + pulse * .45);
        for (var i = 0; i < 3; i++)
        {
            var y = 72 + i * 15;
            const int x = 5; // The retained scene mirrors the trailing lines with the cat.
            drawing.DrawLine(SoftEdge, new Point(x, y), new Point(x + 12 + i * 2, y));
        }
        drawing.Pop();
    }

    private static bool EllipseContains(Rect rectangle, double x, double y)
    {
        var a = (x - rectangle.X - rectangle.Width * .5) / (rectangle.Width * .5);
        var b = (y - rectangle.Y - rectangle.Height * .5) / (rectangle.Height * .5);
        return a * a + b * b <= 1;
    }
    private static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private static double SignedUnit(double value) => double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
    private static SolidColorBrush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static Pen Pen(Brush brush, double width) { var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze(); return pen; }
    private static Geometry Geometry(string data) { var geometry = System.Windows.Media.Geometry.Parse(data); geometry.Freeze(); return geometry; }
}
