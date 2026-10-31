# Silent kitty captions

Run `dotnet run --project tests/Kitty.Caption.Tests -c Release`.

Relay's vocalizations are text bubbles. This pure test project links only the
caption controller; it contains no audio output, WPF runtime or native imports.
Checks cover each supported text reaction, a single coalesced bubble, bounded
appearance/fade values, expiry, clearing on hide, text sanitization, invalid
numeric inputs and reproducible variation. Viewport placement and click-through
are exercised by the application Appearance tests.
