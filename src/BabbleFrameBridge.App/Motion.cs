// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BabbleFrameBridge.App;

/// <summary>Render-only transitions that settle to final base values and can be interrupted cleanly.</summary>
public static class Motion
{
    private static readonly ConditionalWeakTable<FrameworkElement, ElementMotion> States = new();
    private static readonly CubicEase EaseOut = CreateEasing();
    private static bool enabled = true;

    public static bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            if (!value)
                foreach (var pair in States)
                    Reset(pair.Key, pair.Value);
        }
    }

    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverChanged));
    public static bool GetHover(DependencyObject element) => (bool)element.GetValue(HoverProperty);
    public static void SetHover(DependencyObject element, bool value) => element.SetValue(HoverProperty, value);

    public static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached(
        "Pressed", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPressedChanged));
    public static bool GetPressed(DependencyObject element) => (bool)element.GetValue(PressedProperty);
    public static void SetPressed(DependencyObject element, bool value) => element.SetValue(PressedProperty, value);

    public static readonly DependencyProperty FeedbackOpacityProperty = DependencyProperty.RegisterAttached(
        "FeedbackOpacity", typeof(double), typeof(Motion), new PropertyMetadata(1.0, OnFeedbackOpacityChanged));
    public static double GetFeedbackOpacity(DependencyObject element) => (double)element.GetValue(FeedbackOpacityProperty);
    public static void SetFeedbackOpacity(DependencyObject element, double value) => element.SetValue(FeedbackOpacityProperty, value);

    private static bool CanAnimate => Enabled && SystemParameters.ClientAreaAnimation;

    private static void OnHoverChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        element.MouseEnter -= OnHover;
        element.MouseLeave -= OnHover;
        element.IsEnabledChanged -= OnEnabledChanged;
        element.Unloaded -= OnUnloaded;
        if (args.NewValue is true)
        {
            element.MouseEnter += OnHover;
            element.MouseLeave += OnHover;
            element.IsEnabledChanged += OnEnabledChanged;
            element.Unloaded += OnUnloaded;
        }
        else if (States.TryGetValue(element, out var state)) ResetInteraction(state);
    }

    private static void OnHover(object sender, MouseEventArgs args) => UpdateInteraction((FrameworkElement)sender);
    private static void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateInteraction((FrameworkElement)sender);
    private static void OnPressedChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is FrameworkElement element) UpdateInteraction(element);
    }
    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (States.TryGetValue(element, out var state)) ResetInteraction(state);
    }

    private static void UpdateInteraction(FrameworkElement element)
    {
        var state = States.GetValue(element, CreateState);
        var pressed = element.IsEnabled && GetPressed(element);
        var hover = element.IsEnabled && GetHover(element) && element.IsMouseOver;
        var animate = CanAnimate && element.IsLoaded;
        Animate(state.Hover, TranslateTransform.YProperty, CanAnimate ? pressed ? .6 : hover ? -1.2 : 0 : 0, animate, 150);
        var scale = pressed && CanAnimate ? .985 : 1;
        Animate(state.Scale, ScaleTransform.ScaleXProperty, scale, animate, 120);
        Animate(state.Scale, ScaleTransform.ScaleYProperty, scale, animate, 120);
    }

    private static void OnFeedbackOpacityChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        var state = States.GetValue(element, CreateState);
        state.FeedbackOpacity = Math.Clamp((double)args.NewValue, 0, 1);
        Animate(element, UIElement.OpacityProperty, state.FeedbackOpacity, CanAnimate && element.IsLoaded, 120);
    }

    /// <summary>Repeated calls continue from the displayed values, avoiding a flash during quick navigation.</summary>
    public static void Transition(FrameworkElement element, bool animate)
    {
        var state = States.GetValue(element, CreateState);
        var interrupted = state.EntranceActive;
        var fromOpacity = interrupted ? element.Opacity : 0;
        var fromY = interrupted ? state.Entrance.Y : 8;
        var generation = ++state.EntranceGeneration;
        state.EntranceActive = false;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        state.Entrance.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 1;
        state.Entrance.Y = 0;
        state.EntranceOpacity = true;
        if (!animate || !CanAnimate) return;

        state.EntranceActive = true;
        AnimateFrom(element, UIElement.OpacityProperty, fromOpacity, 1, 210, (_, _) =>
        {
            if (state.EntranceGeneration == generation) state.EntranceActive = false;
        });
        AnimateFrom(state.Entrance, TranslateTransform.YProperty, fromY, 0, 210);
    }

    /// <summary>A purely visual kitty swat; layout, focus and control actions are unchanged.</summary>
    public static void PetWobble(FrameworkElement element, double strength = 1)
    {
        if (!CanAnimate || !element.IsVisible || !element.IsLoaded) return;
        var state = States.GetValue(element, CreateState);
        state.Pet ??= CreatePetMotion(state.Transforms);
        var power = double.IsFinite(strength) ? Math.Clamp(strength, .15, 1) : .5;
        var angle = (element.ActualWidth > 360 ? 1.25 : element is System.Windows.Controls.Primitives.ButtonBase ? 5.5 : 2.5) * power;
        Spring(state.Pet.Rotation, RotateTransform.AngleProperty, 0, angle);
        Spring(state.Pet.Offset, TranslateTransform.YProperty, 0, 3 * power);
        Spring(state.Pet.Scale, ScaleTransform.ScaleXProperty, 1, -.018 * power);
        Spring(state.Pet.Scale, ScaleTransform.ScaleYProperty, 1, .012 * power);
    }

    public static void ResetPetWobbles()
    {
        foreach (var pair in States)
        {
            if (!pair.Key.Dispatcher.CheckAccess()) { pair.Key.Dispatcher.BeginInvoke(new Action(ResetPetWobbles)); return; }
            ResetPet(pair.Value.Pet);
        }
    }

    private static PetMotion CreatePetMotion(TransformGroup group)
    {
        var pet = new PetMotion();
        group.Children.Add(pet.Scale);
        group.Children.Add(pet.Rotation);
        group.Children.Add(pet.Offset);
        return pet;
    }

    private static void Spring(Animatable target, DependencyProperty property, double rest, double amplitude)
    {
        var current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, rest);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620), FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        foreach (var (milliseconds, displacement) in new[] { (75, 1.0), (170, -.58), (280, .28), (400, -.11), (620, .0) })
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(rest + amplitude * displacement,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), EaseOut));
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetPet(PetMotion? pet)
    {
        if (pet is null) return;
        pet.Rotation.BeginAnimation(RotateTransform.AngleProperty, null); pet.Rotation.Angle = 0;
        pet.Offset.BeginAnimation(TranslateTransform.YProperty, null); pet.Offset.Y = 0;
        pet.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); pet.Scale.ScaleX = 1;
        pet.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); pet.Scale.ScaleY = 1;
    }

    private static void Animate(DependencyObject target, DependencyProperty property, double to, bool animate, int milliseconds)
    {
        var from = (double)target.GetValue(property);
        Begin(target, property, null);
        target.SetValue(property, to);
        if (animate && Math.Abs(from - to) > .0001) AnimateFrom(target, property, from, to, milliseconds);
    }

    private static void AnimateFrom(DependencyObject target, DependencyProperty property, double from, double to, int milliseconds, EventHandler? completed = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop
        };
        if (completed is not null) animation.Completed += completed;
        Begin(target, property, animation);
    }

    private static void Begin(DependencyObject target, DependencyProperty property, DoubleAnimation? animation)
    {
        if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        else if (target is Animatable transform) transform.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Reset(FrameworkElement element, ElementMotion state)
    {
        if (!element.Dispatcher.CheckAccess()) { element.Dispatcher.BeginInvoke(new Action(() => Reset(element, state))); return; }
        ResetInteraction(state);
        ResetPet(state.Pet);
        state.EntranceActive = false;
        state.EntranceGeneration++;
        state.Entrance.BeginAnimation(TranslateTransform.YProperty, null);
        state.Entrance.Y = 0;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (state.EntranceOpacity) element.Opacity = 1;
        else element.Opacity = state.FeedbackOpacity;
    }

    private static void ResetInteraction(ElementMotion state)
    {
        state.Hover.BeginAnimation(TranslateTransform.YProperty, null); state.Hover.Y = 0;
        state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); state.Scale.ScaleX = 1;
        state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); state.Scale.ScaleY = 1;
    }

    private static ElementMotion CreateState(FrameworkElement element)
    {
        var state = new ElementMotion();
        var transforms = new TransformGroup();
        state.Transforms = transforms;
        var existing = element.RenderTransform;
        if (existing is not null && !existing.Value.IsIdentity) transforms.Children.Add(existing);
        transforms.Children.Add(state.Scale);
        transforms.Children.Add(state.Hover);
        transforms.Children.Add(state.Entrance);
        element.RenderTransform = transforms;
        if (element.ReadLocalValue(UIElement.RenderTransformOriginProperty) == DependencyProperty.UnsetValue)
            element.RenderTransformOrigin = new Point(.5, .5);
        return state;
    }

    private static CubicEase CreateEasing()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }

    private sealed class ElementMotion
    {
        public TransformGroup Transforms = null!;
        public PetMotion? Pet;
        public readonly TranslateTransform Entrance = new();
        public readonly TranslateTransform Hover = new();
        public readonly ScaleTransform Scale = new(1, 1);
        public bool EntranceOpacity;
        public bool EntranceActive;
        public int EntranceGeneration;
        public double FeedbackOpacity = 1;
    }

    private sealed class PetMotion
    {
        public readonly RotateTransform Rotation = new();
        public readonly TranslateTransform Offset = new();
        public readonly ScaleTransform Scale = new(1, 1);
    }
}
