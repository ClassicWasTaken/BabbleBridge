// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BabbleFrameBridge.App;

/// <summary>
/// A local neural-policy companion layered over the app. Only the cat is hit
/// testable; its visual antics never click a real control or move the OS cursor.
/// </summary>
public sealed class HologramKittyOverlay : FrameworkElement, IDisposable
{
    private const double CatSize = 144;
    private const double DecisionInterval = .125;
    private static readonly Brush BubbleFill = FrozenBrush(Color.FromArgb(238, 15, 32, 43));
    private static readonly Brush BubbleInk = FrozenBrush(Color.FromRgb(111, 233, 255));
    private static readonly Pen BubbleOutline = FrozenPen(Color.FromArgb(150, 76, 199, 222), 1);
    private static readonly Pen RipplePen = FrozenPen(Color.FromArgb(190, 110, 232, 255), 1.5);
    private readonly HologramKittyVisual _cat = new() { IsHitTestVisible = false };
    private readonly TranslateTransform _placement = new();
    private readonly KittyMind _mind = new();
    private readonly Random _random = new();
    private readonly KittyCaption _caption = new();
    private readonly DispatcherTimer _captionTimer;
    private double _captionTick;
    private Window? _owner;
    private bool _loaded, _active, _rendering, _inputAttached, _disposed, _positioned, _impactSent;
    private TimeSpan? _lastFrame;
    private double _seconds, _decisionAccumulator, _waypointAge, _cursorMotion, _wavePulse;
    private double _lastPointerTimestamp, _facing = 1, _rippleRemaining;
    private Point _position, _target, _pointer, _lastPointer, _ripplePoint;
    private Vector _velocity, _previousPointerDelta;
    private bool _pointerActive;
    private KittyAction _lastAction = KittyAction.Watch;
    private double _observedActionAge;
    private KittyAttention _lastAttention = KittyAttention.Environment;
    private Point _gazeTarget, _memoryPoint, _restingSpot, _interestPoint, _orbitCenter;
    private Vector _gaze;
    private double _gazeDwell, _novelty, _interestAge = 100, _lift;
    private bool _captionSent;
    private FormattedText? _bubble;

    /// <summary>Overlay-local coordinates; the root can render-wobble an eligible panel at this point.</summary>
    public Action<Point, double>? ImpactRequested { get; set; }
    public Func<Point, bool>? CanSmackAt { get; set; }
    public event EventHandler? StateChanged;
    public event EventHandler? AnimationSuspended;
    public bool IsActive => _active;
    public bool IsAnimating => _rendering;
    public KittyAction BrainAction => _mind.Action;
    public Rect CatBounds => new(_position.X + 29, _position.Y - _lift + 20, 88, 115);
    public KittyAttention Attention => _mind.Attention;
    public Point GazeTarget => _gazeTarget;
    public string Status => !_active ? "Relay is tucked away." : _mind.Intention;
    public string CaptionText => _caption.Text;
    public bool CaptionVisible => _active && _caption.Visible;
    public Rect CaptionBounds { get; private set; } = Rect.Empty;

    public HologramKittyOverlay()
    {
        Focusable = false;
        Visibility = Visibility.Collapsed;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        _captionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        _captionTimer.Tick += OnCaptionTick;
        _cat.RenderTransform = _placement;
        AddVisualChild(_cat);
        AddLogicalChild(_cat);
        Loaded += (_, _) => { _loaded = true; UpdateActivity(); };
        Unloaded += (_, _) => { _loaded = false; UpdateActivity(); };
        IsVisibleChanged += (_, _) => UpdateActivity();
        SizeChanged += (_, _) =>
        {
            // Collapsing reports a temporary zero size; keep the last location
            // until the next real arrange instead of teleporting to the origin.
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                PositionInitially();
                _position = ClampPosition(_position);
                _target = ClampPosition(_target);
                PlaceCat();
            }
            _velocity = default;
            _lift = 0;
            _cat.ResetRig();
            _rippleRemaining = 0;
            AnimationSuspended?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            // A collapsed overlay first becomes visible with zero layout size.
            // Its first arrange must re-evaluate the rendering subscription.
            UpdateActivity();
        };
    }

    public void Attach(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Dispatcher.VerifyAccess();
        if (_disposed) throw new ObjectDisposedException(nameof(HologramKittyOverlay));
        if (ReferenceEquals(_owner, owner)) return;
        DetachOwner();
        _owner = owner;
        owner.IsVisibleChanged += OwnerVisibilityChanged;
        owner.StateChanged += OwnerStateChanged;
        owner.Closed += OwnerClosed;
        SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
        UpdateActivity();
    }

    public void SetActive(bool active)
    {
        Dispatcher.VerifyAccess();
        if (_disposed || _active == active) return;
        _active = active;
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active)
        {
            PositionInitially();
            PlaceCat();
            _cat.RenderFrame(_mind.Action, 0, _seconds, facing: _facing, excitement: _mind.Stimulation);
        }
        UpdateActivity();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateActivity();
    private void OwnerStateChanged(object? sender, EventArgs args) => UpdateActivity();
    private void OwnerClosed(object? sender, EventArgs args) => Dispose();
    private void SystemPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (Dispatcher.CheckAccess()) UpdateActivity();
        else Dispatcher.BeginInvoke(new Action(UpdateActivity));
    }

    private void UpdateActivity()
    {
        if (_disposed) return;
        var visible = _active && _loaded && IsVisible && ActualWidth > 0 && ActualHeight > 0
            && _owner is { IsVisible: true } && _owner.WindowState != WindowState.Minimized;
        var animate = visible && SystemParameters.ClientAreaAnimation;
        if (_rendering != animate)
        {
            _rendering = animate;
            _lastFrame = null;
            _decisionAccumulator = 0;
            if (animate) CompositionTarget.Rendering += OnRendering;
            else
            {
                CompositionTarget.Rendering -= OnRendering;
                _velocity = default;
                _lift = 0;
                _cat.ResetRig();
                PlaceCat();
                _pointerActive = false;
                _cursorMotion = _wavePulse = 0;
                _rippleRemaining = 0;
                ClearCaption();
                _cat.RenderFrame(KittyAction.Watch, .5, _seconds, facing: _facing);
                AnimationSuspended?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        }
        if (!visible) ClearCaption();
        else if (animate) _captionTimer.Stop();
        if (_inputAttached == animate || _owner is null) return;
        _inputAttached = animate;
        if (animate)
        {
            _owner.PreviewMouseMove += OnPointerMove;
            _owner.PreviewMouseDown += OnWindowClick;
            _owner.MouseLeave += OnPointerLeave;
        }
        else
        {
            _owner.PreviewMouseMove -= OnPointerMove;
            _owner.PreviewMouseDown -= OnWindowClick;
            _owner.MouseLeave -= OnPointerLeave;
        }
    }

    private void OnPointerMove(object sender, MouseEventArgs args)
    {
        var point = args.GetPosition(this);
        _pointerActive = point.X >= 0 && point.Y >= 0 && point.X <= ActualWidth && point.Y <= ActualHeight;
        if (!_pointerActive) return;
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var dt = now - _lastPointerTimestamp;
        var delta = point - _lastPointer;
        if (dt is > .0001 and < .25)
        {
            var speed = delta.Length / dt;
            _cursorMotion = Math.Max(_cursorMotion, Math.Clamp(speed / 1400, 0, 1));
            if (delta.Length > 4 && _previousPointerDelta.Length > 4
                && Vector.Multiply(delta, _previousPointerDelta) < 0
                && (point - FacePoint()).Length < 230)
                _wavePulse = Math.Min(1, _wavePulse + .35);
        }
        _lastPointerTimestamp = now;
        _lastPointer = _pointer = point;
        _memoryPoint = point;
        _previousPointerDelta = delta;
    }

    private void OnPointerLeave(object sender, MouseEventArgs args)
    {
        _pointerActive = false;
        _previousPointerDelta = default;
        _lastPointerTimestamp = 0;
    }

    private void OnWindowClick(object sender, MouseButtonEventArgs args)
    {
        var point = args.GetPosition(this);
        if (HitCat(point)) return;
        _interestPoint = _memoryPoint = point;
        _interestAge = 0;
        _novelty = 1;
    }

    protected override void OnMouseDown(MouseButtonEventArgs args)
    {
        base.OnMouseDown(args);
        var point = args.GetPosition(this);
        if (!_active || args.ChangedButton != MouseButton.Left || !HitCat(point)) return;
        _pointer = _lastPointer = point;
        _pointerActive = true;
        _previousPointerDelta = default;
        _lastPointerTimestamp = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        _mind.Pester();
        // Direct interaction can wake the mind immediately; its next small
        // neural inference chooses the response rather than a click sequence.
        if (_rendering)
        {
            _mind.Tick(.01, Stimulus());
            ObserveActionChange();
        }
        else _cat.RenderFrame(KittyAction.Watch, .5, _seconds, facing: _facing);
        ShowBubble(_mind.Irritation > .65 ? "hff." : _mind.Stimulation > .6 ? "!!" : "mrrp", 1.25);
        StateChanged?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private KittyStimulus Stimulus()
    {
        var distance = _pointerActive ? (_pointer - FacePoint()).Length : double.PositiveInfinity;
        var near = Math.Clamp(1 - distance / 380, 0, 1);
        var motion = Math.Max(_cursorMotion, _wavePulse) * Math.Clamp(1 - distance / 270, 0, 1);
        var paw = PawPoint();
        var panelNear = paw.X >= 225 && paw.Y >= 100 && paw.Y < ActualHeight - 35
            && HasSmackTarget(paw);
        return new KittyStimulus(near, motion, panelNear ? 1 : 0, _novelty, _pointerActive ? 1 : 0);
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        if (!_rendering || args is not RenderingEventArgs frame || frame.RenderingTime == _lastFrame) return;
        var dt = _lastFrame.HasValue ? Math.Clamp((frame.RenderingTime - _lastFrame.Value).TotalSeconds, 0, .05) : 0;
        _lastFrame = frame.RenderingTime;
        if (dt <= 0) return;
        _seconds += dt;
        _waypointAge += dt;
        _interestAge += dt;
        _novelty *= Math.Exp(-dt / 7);
        _decisionAccumulator += dt;
        _cursorMotion *= Math.Exp(-dt * 3.5);
        _wavePulse *= Math.Exp(-dt * 1.5);
        if (_decisionAccumulator >= DecisionInterval)
        {
            _mind.Tick(Math.Min(.5, _decisionAccumulator), Stimulus());
            _decisionAccumulator = 0;
            ObserveActionChange();
        }
        UpdateMovement(dt);
        var phase = Math.Clamp((_mind.ActionAge + _decisionAccumulator) / Math.Max(.1, _mind.ActionDuration), 0, 1);
        UpdateGaze(dt);
        if (!_impactSent && phase >= .4)
        {
            if (_mind.Action is KittyAction.SmackPanel or KittyAction.Scratch)
            {
                var point = PawPoint(phase);
                if (HasSmackTarget(point)) ImpactRequested?.Invoke(point, _mind.Action == KittyAction.Scratch ? .35 : .75 + _mind.Stimulation * .25);
                Pulse(point);
                _impactSent = true;
            }
            else if (_mind.Action == KittyAction.Swat && _pointerActive && (_pointer - FacePoint()).Length < 100)
            {
                Pulse(_pointer);
                _impactSent = true;
            }
        }
        if (!_captionSent && phase >= .22)
        {
            var kind = _mind.Action switch { KittyAction.Meow => KittyCaptionKind.Meow,
                KittyAction.Chirp => KittyCaptionKind.Chirp, KittyAction.Yawn => KittyCaptionKind.Yawn,
                KittyAction.Knead when _mind.Trust > .5 => KittyCaptionKind.Purr,
                KittyAction.Sleep or KittyAction.Doze => KittyCaptionKind.Dream,
                KittyAction.Sniff or KittyAction.Peek => KittyCaptionKind.Curious,
                KittyAction.PawLick or KittyAction.FaceWash => KittyCaptionKind.Groom,
                KittyAction.Startle => KittyCaptionKind.Surprise, _ => (KittyCaptionKind?)null };
            if (kind is { } vocalization) ShowCaption(vocalization);
            _captionSent = true;
        }
        _cat.UpdateRig(dt, _velocity.X, _velocity.Y, _facing);
        _cat.RenderFrame(_mind.Action, phase, _seconds, _gaze.X, _gaze.Y, _facing, _mind.Stimulation);
        var hadOverlayEffects = _caption.Visible || _rippleRemaining > 0;
        _caption.Advance(dt);
        _rippleRemaining = Math.Max(0, _rippleRemaining - dt);
        if (!_caption.Visible) { _bubble = null; CaptionBounds = Rect.Empty; }
        // Include the final zero frame so the retained drawing cannot leave a
        // faint cursor ring behind after its one-shot effect has finished.
        if (hadOverlayEffects || _caption.Visible || _rippleRemaining > 0) InvalidateVisual();
    }

    private void ObserveActionChange()
    {
        var restarted = _mind.ActionAge + .000001 < _observedActionAge;
        _observedActionAge = _mind.ActionAge;
        if (_mind.Action == _lastAction && !restarted) return;
        _lastAction = _mind.Action;
        _impactSent = false;
        _captionSent = false;
        _waypointAge = 0;
        _orbitCenter = _position;
        _target = _mind.Action switch
        {
            KittyAction.Walk or KittyAction.Zoomies or KittyAction.LookAround => WanderPoint(),
            KittyAction.Inspect or KittyAction.Sniff or KittyAction.Rub when _interestAge < 15 => ClampPosition(new Point(_interestPoint.X - 90, _interestPoint.Y - 85)),
            KittyAction.Stalk or KittyAction.Pounce or KittyAction.Chase when _pointerActive => ClampPosition(new Point(_pointer.X - 72, _pointer.Y - 62)),
            KittyAction.Swat when _pointerActive => ClampPosition(new Point(_pointer.X - 72 - _facing * 62, _pointer.Y - 59)),
            KittyAction.Grumpy when _pointerActive => ClampPosition(_position + AwayFromPointer() * 50),
            KittyAction.Backoff when _pointerActive => ClampPosition(_position + AwayFromPointer() * 95),
            KittyAction.Startle when _pointerActive => ClampPosition(_position + AwayFromPointer() * 38),
            KittyAction.Peek when _pointerActive => ClampPosition(_position - AwayFromPointer() * 18),
            KittyAction.Circle => ClampPosition(_restingSpot),
            _ => _position
        };
        if (_mind.Action == KittyAction.Zoomies) ShowBubble("!!", 1);
        else if (_mind.Action == KittyAction.Grumpy) ShowBubble("hff.", 1.6);
        else if (_mind.Action is KittyAction.Swat or KittyAction.SmackPanel) ShowBubble("bap!", .65);
        if (_mind.Action is KittyAction.Loaf or KittyAction.Sleep) _restingSpot = _position;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateMovement(double dt)
    {
        var action = _mind.Action;
        if (action is KittyAction.Chase or KittyAction.Stalk && _pointerActive)
            _target = ClampPosition(new Point(_pointer.X - 72 - _facing * 30, _pointer.Y - 62));
        else if (action == KittyAction.Circle)
        {
            var angle = _mind.ActionAge / Math.Max(1, _mind.ActionDuration) * Math.PI * 2;
            _target = ClampPosition(_restingSpot + new Vector(Math.Cos(angle) * 22, Math.Sin(angle) * 11));
        }
        else if (action == KittyAction.TailChase)
        {
            var angle = _mind.ActionAge * 4;
            _target = ClampPosition(_orbitCenter + new Vector(Math.Cos(angle) * 43, Math.Sin(angle) * 23));
        }
        else if (action == KittyAction.Swat)
            _target = _pointerActive && (_pointer - FacePoint()).Length > 92
                ? ClampPosition(new Point(_pointer.X - 72 - _facing * 62, _pointer.Y - 59)) : _position;
        else if (action == KittyAction.Zoomies && ((_position - _target).Length < 45 || _waypointAge > .9))
        {
            _target = WanderPoint();
            _waypointAge = 0;
        }
        var moving = action is KittyAction.Walk or KittyAction.Chase or KittyAction.Zoomies or KittyAction.Grumpy or KittyAction.Swat
            or KittyAction.Stalk or KittyAction.Pounce or KittyAction.Inspect or KittyAction.Sniff or KittyAction.Rub or KittyAction.Circle;
        moving |= action is KittyAction.Backoff or KittyAction.Peek or KittyAction.Startle or KittyAction.TailChase;
        var desired = moving ? _target - _position : default;
        var speed = action switch { KittyAction.Zoomies => 420, KittyAction.Pounce => 310, KittyAction.Stalk => 42,
            KittyAction.Chase => 190, KittyAction.Swat => 140, KittyAction.Grumpy => 65, KittyAction.Circle => 60,
            KittyAction.TailChase => 150, KittyAction.Backoff => 105, KittyAction.Peek => 25, _ => 78 };
        var phase = Math.Clamp((_mind.ActionAge + _decisionAccumulator) / Math.Max(.1, _mind.ActionDuration), 0, 1);
        if (action == KittyAction.Pounce && phase < .18) desired = default;
        var desiredVelocity = desired.Length > 1 ? desired / desired.Length * Math.Min(speed, desired.Length * 4) : default;
        var velocityChange = (desiredVelocity - _velocity) * (1 - Math.Exp(-dt * (action == KittyAction.Zoomies ? 9 : 6)));
        var changeLimit = (action == KittyAction.Zoomies ? 1500 : action is KittyAction.Pounce or KittyAction.Startle ? 1800 : 750) * dt;
        if (velocityChange.Length > changeLimit) velocityChange *= changeLimit / velocityChange.Length;
        _velocity += velocityChange;
        _position = ClampPosition(_position + _velocity * dt);
        if (action is KittyAction.Backoff or KittyAction.Startle && _pointerActive)
            _facing = _pointer.X >= _position.X + 72 ? 1 : -1;
        else if (Math.Abs(_velocity.X) > 6) _facing = Math.Sign(_velocity.X);
        else if (_mind.Attention == KittyAttention.Cursor && _pointerActive && action is KittyAction.Watch or KittyAction.Swat)
            _facing = _pointer.X >= _position.X + 72 ? 1 : -1;
        _lift = action switch { KittyAction.Pounce => Math.Sin(Math.PI * Math.Clamp((phase - .18) / .65, 0, 1)) * 28,
            KittyAction.Startle => Math.Sin(Math.PI * phase) * 10, _ => 0 };
        PlaceCat();
    }

    private void UpdateGaze(double dt)
    {
        _gazeDwell -= dt;
        var attention = _mind.Attention;
        if (_lastAttention != attention || _gazeDwell <= 0)
        {
            _lastAttention = attention;
            _gazeDwell = 1.4 + _random.NextDouble() * 2.8;
            _gazeTarget = attention switch {
                KittyAttention.Self => FacePoint() + new Vector(_facing * 18, 70),
                KittyAttention.Rest => FacePoint() + new Vector(_facing * 35, 48),
                KittyAttention.Memory => _memoryPoint,
                _ => _interestAge < 9 ? _interestPoint : new Point(40 + _random.NextDouble() * Math.Max(1, ActualWidth - 80),
                    90 + _random.NextDouble() * Math.Max(1, ActualHeight - 150)) };
        }
        if (attention == KittyAttention.Cursor && _pointerActive) _gazeTarget = _pointer;
        if (attention is KittyAttention.Self or KittyAttention.Rest)
            _gazeTarget = FacePoint() + new Vector(_facing * 28, attention == KittyAttention.Rest ? 45 : 65);
        var direction = _gazeTarget - FacePoint();
        var wanted = new Vector(Math.Clamp(direction.X / 180, -1, 1), Math.Clamp(direction.Y / 160, -1, 1));
        _gaze += (wanted - _gaze) * (1 - Math.Exp(-dt * 5.5));
    }

    private Vector AwayFromPointer()
    {
        var away = FacePoint() - _pointer;
        return away.Length > 1 ? away / away.Length : new Vector(_facing, 0);
    }

    private Point WanderPoint() => ClampPosition(new Point(20 + _random.NextDouble() * Math.Max(0, ActualWidth - CatSize - 40),
        110 + _random.NextDouble() * Math.Max(0, ActualHeight - CatSize - 150)));

    private void PositionInitially()
    {
        if (_positioned || ActualWidth < CatSize || ActualHeight < CatSize) return;
        _positioned = true;
        _position = ClampPosition(new Point(245, ActualHeight - 265));
        _target = _position;
        _restingSpot = _position;
        _gazeTarget = _memoryPoint = new Point(Math.Max(40, ActualWidth * .62), Math.Max(90, ActualHeight * .35));
    }

    private Point ClampPosition(Point point)
    {
        var minY = ActualHeight > 280 ? 80 : 0;
        var x = Math.Clamp(point.X, 0, Math.Max(0, ActualWidth - CatSize));
        var y = Math.Clamp(point.Y, minY, Math.Max(minY, ActualHeight - CatSize - 18));
        // Leave the bottom-left activation button and setup/credits links accessible.
        if (x < 216 && y + CatSize > ActualHeight - 210)
            y = Math.Max(minY, ActualHeight - 210 - CatSize);
        return new Point(x, y);
    }

    private Point FacePoint() => new(_position.X + 72, _position.Y - _lift + 59);
    private bool HasSmackTarget(Point point)
    {
        try { return CanSmackAt?.Invoke(point) ?? true; }
        catch (InvalidOperationException) { return false; } // A page may unload during navigation.
    }
    private Point PawPoint(double phase = .4)
    {
        // Match the visual's forward arm at the panel-swat contact pose. The
        // same gaze offset, reflection and breathing translation keep the UI
        // impact at the holographic paw rather than below its feet.
        var extension = Math.Sin(Math.PI * Math.Min(1, phase * 1.25));
        var lookY = _gaze.Y;
        var localX = 85 + extension * 34;
        var localY = 126 - extension * 62 + lookY * extension * 14 + Math.Sin(_seconds * 2.5) * .75;
        return new Point(_position.X + (_facing < 0 ? 144 - localX : localX), _position.Y - _lift + localY);
    }
    private void PlaceCat() { _placement.X = _position.X; _placement.Y = _position.Y - _lift; }
    private void Pulse(Point point) { _ripplePoint = point; _rippleRemaining = .4; InvalidateVisual(); }

    private void ShowBubble(string text, double duration)
    {
        if (!CanDisplayCaption() || !_caption.Show(text, duration)) return;
        RefreshCaptionText();
    }

    private void ShowCaption(KittyCaptionKind kind)
    {
        if (!CanDisplayCaption() || !_caption.ShowFor(kind, kind == KittyCaptionKind.Surprise)) return;
        RefreshCaptionText();
    }

    private bool CanDisplayCaption() => _active && _loaded && IsVisible && _owner is { IsVisible: true }
        && _owner.WindowState != WindowState.Minimized;

    private void RefreshCaptionText()
    {
        _bubble = new FormattedText(_caption.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 11, BubbleInk, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (!_rendering) { _captionTick = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; _captionTimer.Start(); }
        InvalidateVisual();
    }

    private void ClearCaption()
    {
        _captionTimer.Stop(); _caption.Clear(); _bubble = null; CaptionBounds = Rect.Empty;
        InvalidateVisual();
    }

    private void OnCaptionTick(object? sender, EventArgs args)
    {
        if (!CanDisplayCaption()) { ClearCaption(); return; }
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        _caption.Advance(Math.Max(0, now - _captionTick)); _captionTick = now;
        if (!_caption.Visible) { ClearCaption(); return; }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (!_active) return;
        if (_caption.Visible && _bubble is { } text)
        {
            var width = Math.Min(text.Width + 22, Math.Max(12, ActualWidth - 12));
            var x = Math.Clamp(_position.X + 72 - width / 2, 6, Math.Max(6, ActualWidth - width - 6));
            var preferredY = _position.Y - _lift - 23;
            if (preferredY < 6) preferredY = _position.Y - _lift + CatSize + 2;
            var motion = SystemParameters.ClientAreaAnimation;
            var y = Math.Clamp(preferredY + (motion ? _caption.OffsetY : 0), 6, Math.Max(6, ActualHeight - 34));
            CaptionBounds = new Rect(x, y, width, 27);
            drawing.PushOpacity(motion ? _caption.Opacity : 1);
            var scale = motion ? _caption.Scale : 1;
            drawing.PushTransform(new ScaleTransform(scale, scale, x + width / 2, y + 27));
            drawing.DrawRoundedRectangle(BubbleFill, BubbleOutline, CaptionBounds, 12, 12);
            drawing.DrawText(text, new Point(x + 11, y + 6));
            drawing.Pop(); drawing.Pop();
        }
        if (_rippleRemaining > 0)
        {
            var phase = 1 - _rippleRemaining / .4;
            drawing.PushOpacity((1 - phase) * .85);
            drawing.DrawEllipse(null, RipplePen, _ripplePoint, 5 + phase * 27, 5 + phase * 27);
            drawing.Pop();
        }
    }

    private bool HitCat(Point point)
    {
        var local = point - new Point(_position.X, _position.Y - _lift);
        return _cat.ContainsBody(new Point(local.X, local.Y));
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
        _active && HitCat(parameters.HitPoint) ? new PointHitTestResult(this, parameters.HitPoint) : null;
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _cat : throw new ArgumentOutOfRangeException(nameof(index));
    protected override Size MeasureOverride(Size availableSize) { _cat.Measure(new Size(CatSize, CatSize)); return default; }
    protected override Size ArrangeOverride(Size finalSize) { _cat.Arrange(new Rect(0, 0, CatSize, CatSize)); return finalSize; }

    private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen FrozenPen(Color color, double width) { var pen = new Pen(FrozenBrush(color), width); pen.Freeze(); return pen; }

    private void DetachOwner()
    {
        if (_owner is null) return;
        _owner.IsVisibleChanged -= OwnerVisibilityChanged;
        _owner.StateChanged -= OwnerStateChanged;
        _owner.Closed -= OwnerClosed;
        _owner.PreviewMouseMove -= OnPointerMove;
        _owner.PreviewMouseDown -= OnWindowClick;
        _owner.MouseLeave -= OnPointerLeave;
        SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged;
        _owner = null;
        _inputAttached = false;
    }

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _active = _rendering = false;
        _captionTimer.Stop(); _captionTimer.Tick -= OnCaptionTick;
        _cat.ResetRig();
        CompositionTarget.Rendering -= OnRendering;
        DetachOwner();
        _bubble = null;
        _caption.Clear(); CaptionBounds = Rect.Empty; _rippleRemaining = 0;
        AnimationSuspended?.Invoke(this, EventArgs.Empty);
        Visibility = Visibility.Collapsed;
    }
}
