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
    public double MouthOpenness => _action switch
    {
        KittyAction.Yawn => Math.Sin(Math.PI * Unit((_phase - .12) / .72)),
        KittyAction.Meow => Math.Sin(Math.PI * Unit((_phase - .12) / .55)) * .76,
        KittyAction.Chirp => Math.Max(0, Math.Sin(_phase * Math.PI * 3)) * .5,
        _ => 0
    };

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
        _rig.Step(deltaSeconds, velocityX, velocityY, facing);
    }

    public void ResetRig() { _rig.Reset(_facing); InvalidateVisual(); }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _scene : throw new ArgumentOutOfRangeException(nameof(index));

    /// <param name="phase">Normalized progress through the current action, from zero to one.</param>
    public void RenderFrame(KittyAction action, double phase, double time, double lookX = 0,
        double lookY = 0, double facing = 1, double excitement = 0)
    {
        _action = action; _phase = Unit(phase); _time = double.IsFinite(time) ? Math.Max(0, time) : 0;
        _lookX = SignedUnit(lookX); _lookY = SignedUnit(lookY); _facing = facing < 0 ? -1 : 1;
        _excitement = Unit(excitement);
        InvalidateVisual();
    }

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
        var running = _action is KittyAction.Walk or KittyAction.Chase or KittyAction.Zoomies or KittyAction.Circle;
        var sleeping = _action == KittyAction.Sleep;
        var loaf = _action == KittyAction.Loaf;
        var grooming = _action == KittyAction.Groom;
        var grumpy = _action == KittyAction.Grumpy;
        var stretching = _action == KittyAction.Stretch;
        var stalking = _action == KittyAction.Stalk;
        var pouncing = _action == KittyAction.Pounce;
        var kneading = _action == KittyAction.Knead;
        var rolling = _action == KittyAction.Roll;
        var rubbing = _action == KittyAction.Rub;
        var gait = Math.Sin(_time * (_action == KittyAction.Zoomies ? 22 : _action == KittyAction.Walk ? 9 : 15));
        var breath = Math.Sin(_time * (sleeping ? 1.8 : 2.5));
        var burst = Math.Sin(Math.PI * _phase);
        var pounceJump = pouncing ? Math.Sin(Math.PI * Unit((_phase - .23) / .66)) * -11 : 0;
        var bounce = running ? -Math.Abs(gait) * (_action == KittyAction.Zoomies ? 4.5 : 2.2) : breath * .65;
        bounce += pounceJump;
        var lean = running ? 3 + _excitement * 2 : grumpy ? -3 : stretching ? -7 : rubbing ? Math.Sin(_time * 3) * 5 : rolling ? burst * 18 : 0;
        var leftEnd = new Point(60 - gait * (running ? 13 : 0), 127 - Math.Max(0, gait) * (running ? 9 : 0));
        var rightEnd = new Point(86 + gait * (running ? 13 : 0), 127 - Math.Max(0, -gait) * (running ? 9 : 0));
        if (stretching) { leftEnd = new Point(43 - burst * 7, 130); rightEnd = new Point(92 + burst * 13, 130); }
        if (kneading) { leftEnd.Y -= Math.Max(0, Math.Sin(_time * 5)) * 8; rightEnd.Y -= Math.Max(0, -Math.Sin(_time * 5)) * 8; }
        if (stalking || pouncing) { leftEnd.X -= 7; rightEnd.X += 8; leftEnd.Y -= pouncing ? burst * 7 : 0; }
        if (grooming) rightEnd = new Point(72 + Math.Sin(_time * 7) * 3, 71 + Math.Cos(_time * 7) * 2);
        if (_action is KittyAction.Swat or KittyAction.SmackPanel)
        {
            var extension = _action == KittyAction.SmackPanel ? Math.Sin(Math.PI * Math.Min(1, _phase * 1.25)) : burst;
            rightEnd = new Point(85 + extension * 34, 126 - extension * 62 + _lookY * extension * 14);
        }
        if (_action == KittyAction.Scratch)
        {
            leftEnd = new Point(109, 94 + Math.Sin(_time * 12) * 12);
            rightEnd = new Point(116, 94 - Math.Sin(_time * 12) * 12);
        }
        var headY = sleeping ? 34 + breath * .5 : loaf ? 22 : grooming ? 9 : stretching ? 14 * burst : stalking ? 12 : pouncing ? 8 : rolling ? 12 : running ? gait * .7 : _lookY * 1.4;
        var headAngle = sleeping ? -13 : grooming ? -14 + Math.Sin(_time * 6) * 2 : grumpy ? -7 : rubbing ? Math.Sin(_time * 3) * 15 : rolling ? burst * 25 : _action == KittyAction.Yawn ? -7 * burst : _lookX * 5 + (running ? -4 : Math.Sin(_time * .65) * 1.5);
        var tail = TailPoints(running, sleeping, grumpy);
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
            part.PushTransform(new TranslateTransform(joint.X, bounce + joint.Y));
            part.PushTransform(new RotateTransform(lean + joint.Angle, 72, 118));
            var hitMatrix = Matrix.Identity;
            hitMatrix.RotateAt(lean + joint.Angle, 72, 118);
            hitMatrix.Translate(joint.X, bounce + joint.Y);
            switch (i)
            {
                case 9:
                    if (!sleeping && !loaf) { DrawLimb(part, new Point(58, 104), new Point(48 + gait * (running ? 8 : 0), 120), new Point(46 - gait * (running ? 11 : 0), 127), .65); _pawHitPoints[0] = hitMatrix.Transform(new Point(46 - gait * (running ? 11 : 0), 127)); }
                    break;
                case 10:
                    if (!sleeping && !loaf) { DrawLimb(part, new Point(88, 105), new Point(98 - gait * (running ? 8 : 0), 120), new Point(101 + gait * (running ? 11 : 0), 127), .65); _pawHitPoints[1] = hitMatrix.Transform(new Point(101 + gait * (running ? 11 : 0), 127)); }
                    break;
                case 11: case 12: case 13:
                    var band = i - 11;
                    var cx = sleeping ? 90 - band * 15 : 74 - band;
                    var cy = sleeping || loaf ? 117.0 - band * 2 : 117.0 - band * 13;
                    if (stalking) cy += band * 5;
                    if (pouncing) cy += band * (1 - burst) * 4;
                    part.PushTransform(new ScaleTransform(rig.StretchX, rig.StretchY, 73, 117));
                    var width = sleeping ? 32 : band == 2 ? 39 : 46;
                    var height = sleeping || loaf ? 21 : band == 0 ? 19 : 22;
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
                    if (!sleeping && !loaf)
                    {
                        part.DrawGeometry(Visor, SoftEdge, Core);
                        part.DrawEllipse(Cyan, null, new Point(70, 98), 2.8, 2.8);
                        part.DrawEllipse(Pale, null, new Point(69.2, 97.2), 1, 1);
                    }
                    break;
                case 15:
                    _pawHitPoints[2] = hitMatrix.Transform(sleeping || loaf ? new Point(51, 126) : leftEnd);
                    if (sleeping || loaf) DrawPaw(part, 51, 126, 0, .9);
                    else DrawLimb(part, new Point(58, 88), new Point(56, 109), leftEnd, 1);
                    break;
                case 16:
                    _pawHitPoints[3] = hitMatrix.Transform(sleeping || loaf ? new Point(96, 126) : rightEnd);
                    if (sleeping || loaf) DrawPaw(part, 96, 126, 0, .9);
                    else DrawLimb(part, new Point(85, 89), new Point(90 + (rightEnd.X - 86) * .4, (90 + rightEnd.Y) * .5), rightEnd, 1);
                    if (_action is KittyAction.Swat or KittyAction.SmackPanel or KittyAction.Scratch && burst > .5)
                    {
                        part.DrawLine(Fine, new Point(rightEnd.X + 8, rightEnd.Y - 9), new Point(rightEnd.X + 15, rightEnd.Y - 14));
                        part.DrawLine(Fine, new Point(rightEnd.X + 11, rightEnd.Y - 2), new Point(rightEnd.X + 20, rightEnd.Y - 3));
                    }
                    break;
                case 17: case 18: case 19: case 20:
                    if (i == 17) { var headHit = Matrix.Identity; headHit.RotateAt(headAngle, 72, 59); headHit.Translate(0, headY); headHit.Append(hitMatrix); _headHitTransform = headHit; }
                    var layer = i == 17 ? HeadLayer.Shell : i == 18 ? HeadLayer.LeftEar : i == 19 ? HeadLayer.RightEar : HeadLayer.Face;
                    var ear = i == 18 ? rig.LeftEarAngle : i == 19 ? rig.RightEarAngle : 0;
                    DrawHead(part, headY, headAngle, _time, sleeping, grooming, grumpy, _lookX * _facing, _lookY, false,
                        layer, MouthOpenness, _action == KittyAction.SlowBlink ? 1 - Math.Pow(burst, .35) : _action == KittyAction.Yawn ? .12 : loaf ? .55 : -1, ear);
                    break;
                case 21:
                    if (sleeping) DrawSleep(part);
                    if (grumpy) DrawGrumpy(part);
                    if (_action == KittyAction.Zoomies) DrawSpeed(part);
                    if (_action is KittyAction.Sniff or KittyAction.Inspect)
                    {
                        var sniff = Math.Sin(_time * 7);
                        part.DrawEllipse(null, Fine, new Point(111 + sniff, 63), 2 + sniff * .5, 2);
                    }
                    break;
                case 22:
                    if (grooming)
                    {
                        DrawPaw(part, 74 + Math.Sin(_time * 7) * 3, 80 + Math.Cos(_time * 7) * 2, -24, .8);
                        if (Math.Sin(_time * 9) > .15) part.DrawEllipse(Lavender, null, new Point(73, 76), 1.7, 2.8);
                    }
                    break;
            }
            part.Pop(); part.Pop();
        }
    }

    private Point[] TailPoints(bool running, bool sleeping, bool grumpy)
    {
        var points = new Point[9];
        points[0] = new Point(96, 111);
        var swing = Math.Sin(_time * (grumpy ? 8 : running ? 9 : 2)) * (grumpy ? 8 : running ? 5 : 3);
        for (var i = 1; i <= 8; i++)
        {
            var t = i / 8.0;
            Point p;
            if (sleeping)
                p = Cubic(new Point(96, 111), new Point(131, 132), new Point(132, 90), new Point(109, 97), t);
            else if (t <= .625)
                p = Cubic(new Point(96, 111), new Point(126, 121), new Point(132, 99 + swing), new Point(123, 86 + swing), t / .625);
            else
                p = Cubic(new Point(123, 86 + swing), new Point(112, 72 + swing), new Point(128, 68 + swing), new Point(132, 78 + swing), (t - .625) / .375);
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
        HeadLayer layer = HeadLayer.All, double mouthOpen = 0, double blinkOverride = -1, double earAngle = 0)
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
            for (var y = 41; y < 73; y += 4) drawing.DrawLine(PenScan, new Point(34, y), new Point(106, y));
            var scan = 39 + (time * 9 % 34);
            if (!icon) drawing.DrawLine(Scan, new Point(37, scan), new Point(103, scan));
            var blinkCycle = time % 5.7;
            var blink = !icon && blinkCycle > 5.50 ? Math.Max(.07, Math.Abs(blinkCycle - 5.60) * 10) : 1;
            if (sleeping) blink = 0;
            else if (blinkOverride >= 0) blink = blinkOverride;
            else if (grooming) blink = .24;
            var eyeHeight = grumpy ? 4.2 : 8.4 * blink;
            DrawEye(drawing, 54 + lookX * 2.3, 54 + lookY * 1.5, eyeHeight, grumpy, sleeping, false);
            DrawEye(drawing, 87 + lookX * 2.3, 54 + lookY * 1.5, eyeHeight, grumpy, sleeping, true);
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
                else if (grumpy)
                {
                    drawing.DrawLine(PurpleLine, new Point(67, 68), new Point(72, 66));
                    drawing.DrawLine(PurpleLine, new Point(72, 66), new Point(77, 68));
                }
                else drawing.DrawGeometry(null, Fine, grooming ? Smile : Mouth);
            }
            drawing.Pop();
        }
        drawing.Pop(); drawing.Pop();
    }

    private static readonly Pen PenScan = Pen(Brush("#102CF0FF"), .5), Scan = Pen(Brush("#296AE3F9"), 1.8);
    private static readonly Geometry Nose = Geometry("M69,60 L75,60 72,63Z");
    private static void DrawEye(DrawingContext drawing, double x, double y, double height, bool grumpy, bool asleep, bool right)
    {
        if (asleep || height < 1)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            { context.BeginFigure(new Point(x - 7, y), false, false); context.QuadraticBezierTo(new Point(x, y + 5), new Point(x + 7, y), true, false); }
            geometry.Freeze(); drawing.DrawGeometry(null, Edge, geometry); return;
        }
        if (grumpy)
        {
            drawing.PushTransform(new RotateTransform(right ? 14 : -14, x, y));
            drawing.DrawRoundedRectangle(Lavender, null, new Rect(x - 7.3, y - height * .5, 14.6, height), 1.8, 1.8);
            drawing.DrawRectangle(Visor, null, new Rect(x - 9, y - height - 2, 18, height));
            drawing.Pop(); return;
        }
        drawing.DrawEllipse(Halo, null, new Point(x, y), 10.8, height + 2.5);
        drawing.DrawEllipse(Cyan, null, new Point(x, y), 7.8, height);
        drawing.DrawEllipse(Blue, null, new Point(x + 1.1, y + 1.2), 5.5, height * .76);
        drawing.DrawEllipse(Visor, null, new Point(x + 1.4, y + .8), 3.7, height * .66);
        drawing.DrawEllipse(Pale, null, new Point(x - 2.1, y - height * .35), 2.2, Math.Min(2.6, height * .3));
        drawing.DrawEllipse(Pale, null, new Point(x + 3.2, y + height * .43), .9, Math.Min(1.1, height * .15));
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
