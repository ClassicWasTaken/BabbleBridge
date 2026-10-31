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

    public HologramKittyVisual()
    {
        Width = Height = 144;
        Focusable = false;
        SnapsToDevicePixels = false;
        AutomationProperties.SetName(this, "Relay, holographic robot kitty");
    }

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

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var scale = Math.Min(ActualWidth / 144, ActualHeight / 144);
        if (scale <= 0) return null;
        var point = hitTestParameters.HitPoint;
        var x = (point.X - (ActualWidth - 144 * scale) * .5) / scale;
        var y = (point.Y - (ActualHeight - 144 * scale) * .5) / scale;
        var sleeping = _action == KittyAction.Sleep;
        var head = sleeping ? new Rect(30, 60, 89, 55) : new Rect(30, 22, 87, 61);
        var body = sleeping ? new Rect(42, 86, 70, 44) : new Rect(45, 76, 58, 57);
        return EllipseContains(head, x, y) || EllipseContains(body, x, y)
            ? new PointHitTestResult(this, point) : null;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var scale = Math.Min(ActualWidth / 144, ActualHeight / 144);
        if (scale <= 0) return;
        drawing.PushTransform(new TranslateTransform((ActualWidth - 144 * scale) * .5, (ActualHeight - 144 * scale) * .5));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        DrawProjector(drawing);
        if (_facing < 0) drawing.PushTransform(new ScaleTransform(-1, 1, 72, 0));
        var running = _action is KittyAction.Walk or KittyAction.Chase or KittyAction.Zoomies;
        var sleeping = _action == KittyAction.Sleep;
        var grooming = _action == KittyAction.Groom;
        var grumpy = _action == KittyAction.Grumpy;
        var stretching = _action == KittyAction.Stretch;
        var gait = Math.Sin(_time * (_action == KittyAction.Zoomies ? 22 : _action == KittyAction.Walk ? 9 : 15));
        var breath = Math.Sin(_time * (sleeping ? 1.8 : 2.5));
        var bounce = running ? -Math.Abs(gait) * (_action == KittyAction.Zoomies ? 5.3 : 2.6) : breath * .75;
        var lean = running ? 5 + _excitement * 3 : grumpy ? -3 : stretching ? -9 : 0;
        var burst = Math.Sin(Math.PI * _phase);
        drawing.PushTransform(new TranslateTransform(0, bounce));
        drawing.PushTransform(new RotateTransform(lean, 72, 118));
        DrawTail(drawing, running, sleeping, grumpy);
        if (sleeping)
        {
            drawing.DrawEllipse(Shell, Edge, new Point(76, 112), 34, 17);
            drawing.DrawEllipse(Glass, SoftEdge, new Point(75, 109), 24, 11);
            DrawPaw(drawing, 47, 125, 0, .9);
            DrawPaw(drawing, 96, 125, 0, .9);
        }
        else
        {
            // Far limbs precede the translucent chassis and nearer limbs.
            DrawLimb(drawing, new Point(58, 104), new Point(48 + gait * (running ? 8 : 0), 120), new Point(46 - gait * (running ? 11 : 0), 127), .75);
            DrawLimb(drawing, new Point(88, 105), new Point(98 - gait * (running ? 8 : 0), 120), new Point(101 + gait * (running ? 11 : 0), 127), .75);
            drawing.DrawGeometry(null, Glow, Chest);
            drawing.DrawGeometry(Shell, Edge, Chest);
            drawing.DrawGeometry(Glass, null, ChestShine);
            drawing.DrawGeometry(Visor, SoftEdge, Core);
            drawing.DrawEllipse(Cyan, null, new Point(70, 98), 2.8, 2.8);
            drawing.DrawEllipse(Pale, null, new Point(69.2, 97.2), 1, 1);
            drawing.DrawLine(Fine, new Point(82, 88), new Point(90, 93));
            drawing.DrawLine(Fine, new Point(82, 92), new Point(91, 97));
            var leftEnd = new Point(60 - gait * (running ? 13 : 0), 127 - Math.Max(0, gait) * (running ? 9 : 0));
            var rightEnd = new Point(86 + gait * (running ? 13 : 0), 127 - Math.Max(0, -gait) * (running ? 9 : 0));
            if (stretching) { leftEnd = new Point(43 - burst * 7, 130); rightEnd = new Point(92 + burst * 13, 130); }
            DrawLimb(drawing, new Point(58, 88), new Point(56, 109), leftEnd, 1);
            if (grooming) rightEnd = new Point(72 + Math.Sin(_time * 7) * 3, 71 + Math.Cos(_time * 7) * 2);
            if (_action is KittyAction.Swat or KittyAction.SmackPanel)
            {
                var extension = _action == KittyAction.SmackPanel ? Math.Sin(Math.PI * Math.Min(1, _phase * 1.25)) : burst;
                rightEnd = new Point(85 + extension * 34, 126 - extension * 62 + _lookY * extension * 14);
            }
            DrawLimb(drawing, new Point(85, 89), new Point(90 + (rightEnd.X - 86) * .4, (90 + rightEnd.Y) * .5), rightEnd, 1);
            if (_action is KittyAction.Swat or KittyAction.SmackPanel && burst > .5)
            {
                drawing.DrawLine(Fine, new Point(rightEnd.X + 8, rightEnd.Y - 9), new Point(rightEnd.X + 15, rightEnd.Y - 14));
                drawing.DrawLine(Fine, new Point(rightEnd.X + 11, rightEnd.Y - 2), new Point(rightEnd.X + 20, rightEnd.Y - 3));
            }
        }
        var headY = sleeping ? 34 + breath * .5 : grooming ? 9 : stretching ? 14 * burst : running ? gait * .7 : _lookY * 1.4;
        var headAngle = sleeping ? -13 : grooming ? -14 + Math.Sin(_time * 6) * 2 : grumpy ? -7 : _lookX * 5 + (running ? -6 : Math.Sin(_time * .65) * 1.5);
        DrawHead(drawing, headY, headAngle, _time, sleeping, grooming, grumpy, _lookX * _facing, _lookY, false);
        if (grooming)
        {
            // The washing paw sits in front of the visor, so the lick reads as
            // grooming rather than a limb disappearing behind the monitor.
            var wash = Math.Sin(_time * 7);
            DrawPaw(drawing, 74 + wash * 3, 80 + Math.Cos(_time * 7) * 2, -24, .8);
            if (Math.Sin(_time * 9) > .15)
                drawing.DrawEllipse(Lavender, null, new Point(73, 76), 1.7, 2.8);
        }
        drawing.Pop(); drawing.Pop();
        if (_facing < 0) drawing.Pop();
        if (sleeping) DrawSleep(drawing);
        if (grumpy) DrawGrumpy(drawing);
        if (_action == KittyAction.Zoomies) DrawSpeed(drawing);
        drawing.Pop(); drawing.Pop();
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

    private void DrawTail(DrawingContext drawing, bool running, bool sleeping, bool grumpy)
    {
        var swing = Math.Sin(_time * (grumpy ? 8 : running ? 9 : 2)) * (grumpy ? 11 : running ? 7 : 4);
        var tail = new StreamGeometry();
        using (var context = tail.Open())
        {
            context.BeginFigure(new Point(96, 111), false, false);
            if (sleeping)
                context.BezierTo(new Point(127, 128), new Point(131, 95), new Point(110, 96), true, false);
            else
            {
                context.BezierTo(new Point(126, 121), new Point(132, 99 + swing), new Point(123, 86 + swing), true, false);
                context.BezierTo(new Point(112, 72 + swing), new Point(128, 68 + swing), new Point(132, 78 + swing), true, false);
            }
        }
        tail.Freeze();
        drawing.DrawGeometry(null, TailGlow, tail);
        drawing.DrawGeometry(null, TailRim, tail);
        drawing.DrawGeometry(null, TailCore, tail);
        if (!sleeping) drawing.DrawEllipse(Pale, null, new Point(132, 78 + swing), 1.5, 1.5);
    }

    private static void DrawHead(DrawingContext drawing, double offsetY, double angle, double time,
        bool sleeping, bool grooming, bool grumpy, double lookX, double lookY, bool icon)
    {
        drawing.PushTransform(new TranslateTransform(0, offsetY));
        drawing.PushTransform(new RotateTransform(angle, 72, 59));
        drawing.DrawGeometry(null, Glow, LeftEar);
        drawing.DrawGeometry(null, Glow, RightEar);
        drawing.DrawGeometry(ShellShade, Edge, LeftEar);
        drawing.DrawGeometry(ShellShade, Edge, RightEar);
        drawing.DrawGeometry(Glass, SoftEdge, LeftEarInset);
        drawing.DrawGeometry(Glass, SoftEdge, RightEarInset);
        drawing.DrawLine(Fine, new Point(44, 19), new Point(48, 24));
        drawing.DrawLine(Fine, new Point(98, 20), new Point(96, 24));
        drawing.DrawGeometry(ShellShade, SoftEdge, HeadSide);
        drawing.DrawGeometry(null, Glow, Head);
        drawing.DrawGeometry(ShellLight, Edge, Head);
        drawing.DrawLine(WhiteLine, new Point(42, 31), new Point(60, 31));
        drawing.DrawLine(Fine, new Point(91, 33), new Point(100, 33));
        drawing.DrawGeometry(Visor, SoftEdge, VisorShape);
        drawing.PushClip(VisorShape);
        for (var y = 41; y < 73; y += 4) drawing.DrawLine(PenScan, new Point(34, y), new Point(106, y));
        var scan = 39 + (time * 9 % 34);
        if (!icon) drawing.DrawLine(Scan, new Point(37, scan), new Point(103, scan));
        var blinkCycle = time % 5.7;
        var blink = !icon && blinkCycle > 5.50 ? Math.Max(.07, Math.Abs(blinkCycle - 5.60) * 10) : 1;
        if (sleeping) blink = 0;
        if (grooming) blink = .24;
        var eyeHeight = grumpy ? 4.2 : 8.4 * blink;
        DrawEye(drawing, 54 + lookX * 2.3, 54 + lookY * 1.5, eyeHeight, grumpy, sleeping, false);
        DrawEye(drawing, 87 + lookX * 2.3, 54 + lookY * 1.5, eyeHeight, grumpy, sleeping, true);
        drawing.DrawEllipse(Cheek, null, new Point(45, 65), 4, 1.6);
        drawing.DrawEllipse(Cheek, null, new Point(96, 65), 4, 1.6);
        if (!sleeping)
        {
            drawing.DrawGeometry(grumpy ? Lavender : Cyan, null, Nose);
            if (grumpy)
            {
                drawing.DrawLine(PurpleLine, new Point(67, 68), new Point(72, 66));
                drawing.DrawLine(PurpleLine, new Point(72, 66), new Point(77, 68));
            }
            else drawing.DrawGeometry(null, Fine, grooming ? Smile : Mouth);
        }
        drawing.Pop();
        // Three short telemetry whiskers and a status diode make the chassis
        // recognizably feline without copying a mascot face or logo.
        drawing.DrawLine(Fine, new Point(31, 58), new Point(25, 56));
        drawing.DrawLine(Fine, new Point(31, 62), new Point(23, 63));
        drawing.DrawLine(Fine, new Point(110, 58), new Point(118, 56));
        drawing.DrawLine(Fine, new Point(111, 62), new Point(120, 63));
        drawing.DrawEllipse(Cyan, null, new Point(109, 43), 1, 1);
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
            var x = _facing < 0 ? 123 : 5;
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
