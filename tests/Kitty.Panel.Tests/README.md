# Kitty panel and pickup geometry

Run `dotnet run --project tests/Kitty.Panel.Tests -c Release`.

These pure checks use numeric panel rectangles and simulated pointer gestures.
They cover valid and unsafe ledges, feet-on-edge alignment, climb routes, nearby
drop snapping, landing below, scrolling/resizing and stale panel IDs, viewport
and launcher protection, pickup thresholds, grab offsets, cancellation and
bounded release velocity. They do not open a window, capture the real mouse,
move an app control or touch the user's settings.

The WPF owner supplies only visible, clip-aware card borders. Hidden page cards,
buttons, sidebar backgrounds and partially scrolled-off top edges must be
excluded by that owner before calling this geometry helper.
