// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using BabbleFrameBridge.App;
using System.Reflection;
using System.Text.RegularExpressions;

var passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    passed++;
}
void Tick(KittyCaption caption, double seconds)
{
    for (var elapsed = 0d; elapsed < seconds; elapsed += .05) caption.Advance(.05);
}
void CheckFrame(KittyCaption caption)
{
    Check(double.IsFinite(caption.Opacity) && caption.Opacity is >= 0 and <= 1,
        "Caption opacity became invalid.");
    Check(double.IsFinite(caption.Scale) && caption.Scale > 0 && caption.Scale < 2,
        "Caption scale became invalid or extreme.");
    Check(double.IsFinite(caption.OffsetY) && Math.Abs(caption.OffsetY) < 40,
        "Caption offset became invalid or extreme.");
}

var empty = new KittyCaption(15);
Check(!empty.Visible && string.IsNullOrEmpty(empty.Text), "A new companion showed an unsolicited caption.");
Check(!empty.Show("  \t\r\n  ") && !empty.Visible, "An empty caption created a speech bubble.");
CheckFrame(empty);

foreach (var kind in Enum.GetValues<KittyCaptionKind>())
{
    var caption = new KittyCaption(23);
    Check(caption.ShowFor(kind), "A supported text vocalization could not be shown: " + kind);
    Check(caption.Visible && !string.IsNullOrWhiteSpace(caption.Text) && caption.Text.Length <= 36,
        "A supported vocalization produced empty or unbounded text: " + kind);
    Check(!caption.Text.Any(char.IsControl), "A vocalization included a control character.");
    Tick(caption, .3);
    Check(caption.Visible && caption.Opacity > 0, "The visible caption never appeared.");
    for (var i = 0; i < 120; i++) { caption.Advance(.05); CheckFrame(caption); }
    Check(!caption.Visible, "A caption never expired: " + kind);
}

var one = new KittyCaption(7);
Check(one.Show("mew~", 2), "An ordinary text vocalization was rejected.");
var original = one.Text;
Check(!one.Show("mew~", 2) && one.Text == original, "A duplicate caption stacked or restarted immediately.");
Check(!one.Show("prrp!", 2) && one.Text == original, "The caption cooldown allowed immediate chatter.");
Check(one.Show("hff.", 2, urgent: true) && one.Text == "hff.", "An urgent reaction could not replace the one visible bubble.");
Tick(one, .3);
Check(one.Opacity > 0, "A replacement caption stayed invisible.");
one.Clear();
Check(!one.Visible && string.IsNullOrEmpty(one.Text), "Hiding the companion retained visible caption content.");
CheckFrame(one);

var sanitized = new KittyCaption(3);
sanitized.Show("mew\n\t" + new string('p', 300) + "\0");
Check(sanitized.Visible && sanitized.Text.Length <= 36 && !sanitized.Text.Any(char.IsControl),
    "Caption sanitization allowed unbounded text or control characters.");
var saved = sanitized.Text;
foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
{
    sanitized.Advance(invalid);
    Check(sanitized.Text == saved && sanitized.Visible, "An invalid elapsed time corrupted caption state.");
    CheckFrame(sanitized);
}
foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -4d })
{
    var caption = new KittyCaption(4);
    caption.Show("zzz", invalid);
    Tick(caption, 30);
    Check(!caption.Visible, "A nonfinite or negative duration left an immortal caption.");
    CheckFrame(caption);
}

var first = new KittyCaption(100);
var second = new KittyCaption(100);
var voices = new HashSet<string>();
for (var i = 0; i < 20; i++)
{
    first.ShowFor(KittyCaptionKind.Meow, urgent: true);
    second.ShowFor(KittyCaptionKind.Meow, urgent: true);
    Check(first.Text == second.Text, "A seeded caption sequence was not reproducible.");
    voices.Add(first.Text);
    Tick(first, 3); Tick(second, 3);
}
Check(voices.Count > 1, "Text vocalizations have no variation.");

// This whitelist is the companion's product vocabulary: captions stay cat noises,
// with no English thoughts, diagnostic text or filler punctuation.
var catNoises = new HashSet<string>(StringComparer.Ordinal)
{
    "mew~", "meow?", "mrrrow~", "prrp!", "mrrip~", "brrp!",
    "mraaa…", "mraaaw~", "haaam~", "prrr…", "purrr~", "brrrr…",
    "mrr?!", "mraow!", "rrp!", "rrr…", "hff.", "mrrph.",
    "mrr?", "mew?", "prrp?", "prrr~", "rrrp~", "mrr~",
    "mrrp!", "mrow!", "mlem~", "prrp~", "hrrr…", "mrr…", "mrrp"
};
foreach (var kind in Enum.GetValues<KittyCaptionKind>())
{
    var variants = new HashSet<string>(StringComparer.Ordinal);
    for (var seed = 0; seed < 32; seed++)
    {
        var caption = new KittyCaption(seed);
        for (var sample = 0; sample < 8; sample++)
        {
            Check(caption.ShowFor(kind, urgent: true), "A generated vocal variant was rejected: " + kind);
            Check(catNoises.Contains(caption.Text), $"A {kind} caption stopped being a cat noise: {caption.Text}");
            variants.Add(caption.Text); caption.Clear();
        }
    }
    Check(variants.Count >= 3, "The full generated vocal vocabulary was not exercised: " + kind);
}
foreach (var sourceName in new[] { "KittyOverlay.Source", "KittyWorld.Source" })
{
    using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(sourceName)
        ?? throw new InvalidOperationException("Missing embedded caption call-site source: " + sourceName);
    using var reader = new StreamReader(source);
    foreach (Match call in Regex.Matches(reader.ReadToEnd(), @"ShowBubble\((?<args>[^;]*?)\);"))
        foreach (Match literal in Regex.Matches(call.Groups["args"].Value, "\"(?<text>[^\"]+)\""))
            Check(catNoises.Contains(literal.Groups["text"].Value),
                "A direct kitty reaction is not a cat noise: " + literal.Groups["text"].Value);
}

Console.WriteLine($"PASS {passed} silent caption checks: vocal text, coalescing, finite fades, expiry, sanitization and deterministic variety.");
