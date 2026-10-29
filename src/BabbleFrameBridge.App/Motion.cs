// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;

namespace BabbleFrameBridge.App;

/// <summary>Short entrance motion for the visible page, without completion callbacks or delayed state changes.</summary>
public static class Motion
{
    private static readonly ConditionalWeakTable<FrameworkElement, TranslateTransform> Translations = new();
    public static bool Enabled { get; set; } = true;
    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverChanged));
    public static bool GetHover(DependencyObject element) => (bool)element.GetValue(HoverProperty);
    public static void SetHover(DependencyObject element, bool value) => element.SetValue(HoverProperty, value);

    private static void OnHoverChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        element.MouseEnter -= OnHover;
        element.MouseLeave -= OnHover;
        if (args.NewValue is true) { element.MouseEnter += OnHover; element.MouseLeave += OnHover; }
    }

    private static void OnHover(object sender, MouseEventArgs args)
    {
        var element = (FrameworkElement)sender;
        var translation = Translations.GetValue(element, CreateTranslation);
        var from = translation.Y;
        var to = element.IsMouseOver ? -1.5 : 0;
        translation.BeginAnimation(TranslateTransform.YProperty, null);
        translation.Y = Enabled && SystemParameters.ClientAreaAnimation ? to : 0;
        if (!Enabled || !SystemParameters.ClientAreaAnimation) return;
        translation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Call after making the page visible. Passing false also cancels any in-flight animation
    /// and restores the normal visible position, so reduced motion never leaves a faded page.
    /// </summary>
    public static void Transition(FrameworkElement element, bool animate)
    {
        var translation = Translations.GetValue(element, CreateTranslation);

        // Set the final base values before animation; FillBehavior.Stop reveals these when it ends.
        element.BeginAnimation(UIElement.OpacityProperty, null);
        translation.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 1;
        translation.Y = 0;
        if (!animate) return;

        var duration = TimeSpan.FromMilliseconds(220);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
        translation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private static TranslateTransform CreateTranslation(FrameworkElement element)
    {
        var translation = new TranslateTransform();
        var existing = element.RenderTransform;
        if (existing is null || existing.Value.IsIdentity)
        {
            element.RenderTransform = translation;
        }
        else
        {
            // Keep an existing scale/rotation intact if a future page supplies one.
            var group = new TransformGroup();
            group.Children.Add(existing);
            group.Children.Add(translation);
            element.RenderTransform = group;
        }
        return translation;
    }
}
