// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BabbleFrameBridge.App;

/// <summary>
/// Original FaceTracking Bridge particle artwork: points travel around a slowly changing
/// closed Catmull-Rom loop. Standard spline interpolation and damped cursor forces
/// are implemented here independently; no website animation code or parameters are used.
/// </summary>
public sealed class ParticleField : FrameworkElement
{
    public static readonly DependencyProperty AnimationsEnabledProperty =
        DependencyProperty.Register(nameof(AnimationsEnabled), typeof(bool), typeof(ParticleField),
            new FrameworkPropertyMetadata(true, OnAnimationPreferenceChanged));

    private const int Count = 820, Levels = 28, SpritePixels = 48;
    private const double LargestAlpha = .24;
    private static readonly Point[] Anchors =
    [new(-.07,.60), new(.16,.19), new(.43,.37), new(.74,.82),
     new(1.07,.53), new(.83,.12), new(.57,.49), new(.23,.86)];
    private static readonly Color[] Tints =
    [Color.FromRgb(106,227,249), Color.FromRgb(57,189,211), Color.FromRgb(143,180,246)];
    private static readonly BitmapSource[][] Sprites = CreateSprites();
    private readonly Seed[] seeds = CreateSeeds();
    private readonly Point[] path = new Point[Anchors.Length];
    private readonly Vector[] offsets = new Vector[Count], velocities = new Vector[Count];
    private Window? owner;
    private bool loaded, attached, pointerActive, displaced;
    private Point pointer;
    private TimeSpan? lastFrame;
    private double seconds;
    private double pathSeconds = double.NaN, pathWidth, pathHeight;

    public bool AnimationsEnabled
    {
        get => (bool)GetValue(AnimationsEnabledProperty);
        set => SetValue(AnimationsEnabledProperty, value);
    }

    public ParticleField()
    {
        IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateAnimationState();
        SizeChanged += (_, _) => UpdateAnimationState();
    }

    private static void OnAnimationPreferenceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var field = (ParticleField)sender;
        field.UpdateAnimationState(); field.InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (loaded) return;
        loaded = true; owner = Window.GetWindow(this);
        if (owner != null)
        {
            owner.StateChanged += OnWindowStateChanged;
            owner.PreviewMouseMove += OnMouseMove;
            owner.MouseLeave += OnMouseLeave;
        }
        SystemParameters.StaticPropertyChanged += OnSystemPreferenceChanged;
        UpdateAnimationState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        loaded = false; UpdateAnimationState();
        if (owner != null)
        {
            owner.StateChanged -= OnWindowStateChanged;
            owner.PreviewMouseMove -= OnMouseMove;
            owner.MouseLeave -= OnMouseLeave;
        }
        owner = null;
        SystemParameters.StaticPropertyChanged -= OnSystemPreferenceChanged;
    }

    private void OnWindowStateChanged(object? sender, EventArgs args) => UpdateAnimationState();
    private void OnSystemPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateAnimationState();
    }
    private void UpdateAnimationState()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateAnimationState)); return; }
        var active = loaded && IsVisible && ActualWidth > 0 && ActualHeight > 0
            && AnimationsEnabled && SystemParameters.ClientAreaAnimation
            && owner?.WindowState != WindowState.Minimized;
        if (active == attached) return;
        attached = active; lastFrame = null;
        if (active) CompositionTarget.Rendering += OnRendering;
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            pointerActive = false;
            Array.Clear(offsets); Array.Clear(velocities); displaced = false;
            InvalidateVisual();
        }
    }
    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        pointer = args.GetPosition(this);
        pointerActive = attached && pointer.X >= 0 && pointer.Y >= 0
            && pointer.X <= ActualWidth && pointer.Y <= ActualHeight;
    }
    private void OnMouseLeave(object sender, MouseEventArgs args) => pointerActive = false;

    private void OnRendering(object? sender, EventArgs args)
    {
        if (!attached || args is not RenderingEventArgs frame || lastFrame == frame.RenderingTime) return;
        var dt = lastFrame.HasValue ? Math.Clamp((frame.RenderingTime - lastFrame.Value).TotalSeconds, 0, .035) : 0;
        lastFrame = frame.RenderingTime; seconds += dt;
        if (pointerActive || displaced)
        {
            UpdatePath(); displaced = false;
            for (var i = 0; i < Count; i++)
            {
                var position = Position(seeds[i]) + offsets[i];
                var force = -48 * offsets[i] - 13 * velocities[i];
                if (pointerActive)
                {
                    var away = position - pointer; var distance = away.Length;
                    if (distance is > .1 and < 64)
                    {
                        var falloff = 1 - distance / 64;
                        force += away / distance * (1800 * falloff * falloff);
                    }
                }
                velocities[i] += force * dt;
                offsets[i] += velocities[i] * dt;
                if (offsets[i].LengthSquared + velocities[i].LengthSquared < .015)
                    offsets[i] = velocities[i] = default;
                else displaced = true;
            }
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        UpdatePath();
        for (var i = 0; i < Count; i++)
        {
            var seed = seeds[i]; var position = Position(seed) + offsets[i];
            var radius = seed.Radius;
            if (position.X + radius < 0 || position.X - radius > ActualWidth
                || position.Y + radius < 0 || position.Y - radius > ActualHeight) continue;
            var visibility = .38 + .62 * Math.Clamp(position.X / (ActualWidth * .6), 0, 1);
            var opacity = Math.Clamp((int)(seed.Opacity * visibility / LargestAlpha * (Levels - 1)), 0, Levels - 1);
            if (opacity > 0)
                drawing.DrawImage(Sprites[seed.Tint][opacity], new Rect(position.X - radius, position.Y - radius, radius * 2, radius * 2));
        }
    }

    private void UpdatePath()
    {
        // Cursor simulation and drawing share the same path for this display tick.
        if (pathSeconds == seconds && pathWidth == ActualWidth && pathHeight == ActualHeight) return;
        pathSeconds = seconds; pathWidth = ActualWidth; pathHeight = ActualHeight;
        // Smooth deterministic control-point drift changes the loop without wave-layer equations.
        var interval = (int)(seconds / 27); var t = seconds / 27 - interval;
        t = t * t * (3 - 2 * t);
        for (var i = 0; i < path.Length; i++)
        {
            var x = Mix(Noise(i * 2, interval), Noise(i * 2, interval + 1), t) * .055;
            var y = Mix(Noise(i * 2 + 1, interval), Noise(i * 2 + 1, interval + 1), t) * .11;
            path[i] = new Point((Anchors[i].X + x) * ActualWidth, (Anchors[i].Y + y) * ActualHeight);
        }
    }

    private Point Position(Seed seed)
    {
        var u = (seed.Start + seconds * seed.Speed) % 1 * path.Length;
        var n = (int)u; var t = u - n; var t2 = t * t; var t3 = t2 * t;
        var a = path[(n + path.Length - 1) % path.Length]; var b = path[n];
        var c = path[(n + 1) % path.Length]; var d = path[(n + 2) % path.Length];
        var x = .5 * (2*b.X + (-a.X+c.X)*t + (2*a.X-5*b.X+4*c.X-d.X)*t2 + (-a.X+3*b.X-3*c.X+d.X)*t3);
        var y = .5 * (2*b.Y + (-a.Y+c.Y)*t + (2*a.Y-5*b.Y+4*c.Y-d.Y)*t2 + (-a.Y+3*b.Y-3*c.Y+d.Y)*t3);
        var dx = .5 * ((-a.X+c.X) + 2*(2*a.X-5*b.X+4*c.X-d.X)*t + 3*(-a.X+3*b.X-3*c.X+d.X)*t2);
        var dy = .5 * ((-a.Y+c.Y) + 2*(2*a.Y-5*b.Y+4*c.Y-d.Y)*t + 3*(-a.Y+3*b.Y-3*c.Y+d.Y)*t2);
        var length = Math.Max(1, Math.Sqrt(dx*dx + dy*dy));
        var lane = seed.Lane * Math.Clamp(ActualHeight * .14, 18, 42);
        return new Point(x - dy/length*lane, y + dx/length*lane);
    }
    private static double Mix(double a, double b, double t) => a + (b - a) * t;
    private static double Noise(int channel, int interval)
    {
        var value = unchecked((uint)(channel * 374761393 + interval * 668265263 + 17029));
        value = (value ^ (value >> 13)) * 1274126177;
        return (value ^ (value >> 16)) / (double)uint.MaxValue * 2 - 1;
    }
    private static Seed[] CreateSeeds()
    {
        var random = new Random(0x46425247); var result = new Seed[Count];
        for (var i = 0; i < result.Length; i++)
        {
            var lane = (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 1.6;
            result[i] = new Seed(random.NextDouble(), .009 + random.NextDouble() * .008, lane,
                1.15 + Math.Pow(random.NextDouble(), 1.7) * 3.9, .065 + random.NextDouble() * .15,
                random.NextDouble() < .10 ? 2 : random.Next(2));
        }
        return result;
    }
    private static BitmapSource[][] CreateSprites()
    {
        var result = new BitmapSource[Tints.Length][];
        for (var tint = 0; tint < Tints.Length; tint++)
        {
            result[tint] = new BitmapSource[Levels]; var color = Tints[tint];
            for (var opacity = 0; opacity < Levels; opacity++)
            {
                var pixels = new byte[SpritePixels * SpritePixels * 4];
                for (var y = 0; y < SpritePixels; y++)
                for (var x = 0; x < SpritePixels; x++)
                {
                    var px = (x + .5) / SpritePixels - .5; var py = (y + .5) / SpritePixels - .5;
                    var alpha = Math.Pow(Math.Max(0, 1 - 4*(px*px+py*py)), 1.55) * LargestAlpha * opacity / (Levels - 1);
                    var offset = (y * SpritePixels + x) * 4;
                    pixels[offset] = (byte)(color.B * alpha); pixels[offset+1] = (byte)(color.G * alpha);
                    pixels[offset+2] = (byte)(color.R * alpha); pixels[offset+3] = (byte)(255 * alpha);
                }
                var sprite = BitmapSource.Create(SpritePixels, SpritePixels, 96, 96, PixelFormats.Pbgra32, null, pixels, SpritePixels * 4);
                sprite.Freeze(); result[tint][opacity] = sprite;
            }
        }
        return result;
    }
    private readonly record struct Seed(double Start, double Speed, double Lane, double Radius, double Opacity, int Tint);
}
