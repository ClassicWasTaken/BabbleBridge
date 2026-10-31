// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

namespace BabbleFrameBridge.App;

public enum KittyCaptionKind { Meow, Chirp, Yawn, Purr, Surprise, Grumpy, Curious, Cozy, Play, Dream, Groom }

/// <summary>A single silent vocalization with a bounded lifetime, gentle entrance and fade.</summary>
public sealed class KittyCaption
{
    private readonly Random _random;
    private double _age, _duration, _cooldown;
    public string Text { get; private set; } = "";
    public bool Visible => Text.Length > 0 && _age < _duration;
    public double Opacity => !Visible ? 0 : Math.Clamp(Math.Min(_age / .10, (_duration - _age) / .28), 0, 1);
    public double Scale => .92 + .08 * Ease(Math.Clamp(_age / .18, 0, 1));
    public double OffsetY => 4 * (1 - Ease(Math.Clamp(_age / .18, 0, 1)));

    public KittyCaption(int? seed = null) => _random = new Random(seed ?? Random.Shared.Next());

    public bool Show(string text, double duration = 1.8, bool urgent = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = new string(text.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (clean.Length == 0) return false;
        if (clean.Length > 36) clean = clean[..35] + "…";
        if (!urgent && _cooldown > 0) return false;
        if (Visible && clean == Text && _age < .4) return false;
        Text = clean;
        _age = 0;
        _duration = double.IsFinite(duration) ? Math.Clamp(duration, .6, 4) : 1.8;
        _cooldown = .45;
        return true;
    }

    public bool ShowFor(KittyCaptionKind kind, bool urgent = false)
    {
        string[] choices = kind switch {
            KittyCaptionKind.Meow => ["mew~", "meow?", "mrrrow~"],
            KittyCaptionKind.Chirp => ["prrp!", "mrrip~", "brrp!"],
            KittyCaptionKind.Yawn => ["mraaa…", "mraaaw~", "haaam~"],
            KittyCaptionKind.Purr => ["prrr…", "purrr~", "brrrr…"],
            KittyCaptionKind.Surprise => ["mrr?!", "mraow!", "rrp!"],
            KittyCaptionKind.Grumpy => ["rrr…", "hff.", "mrrph."],
            KittyCaptionKind.Curious => ["mrr?", "mew?", "prrp?"],
            KittyCaptionKind.Cozy => ["prrr~", "rrrp~", "mrr~"],
            KittyCaptionKind.Play => ["mrrp!", "mrow!", "prrp!"],
            KittyCaptionKind.Groom => ["mlem~", "mrr~", "prrp~"],
            _ => ["hrrr…", "mrr…", "prrr…"] };
        var index = _random.Next(choices.Length);
        if (choices[index] == Text) index = (index + 1) % choices.Length;
        return Show(choices[index], kind is KittyCaptionKind.Purr or KittyCaptionKind.Dream ? 2.4 : 1.8, urgent);
    }

    public void Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) return;
        var dt = Math.Min(elapsedSeconds, 10);
        _age += dt;
        _cooldown = Math.Max(0, _cooldown - dt);
        if (_age >= _duration) Text = "";
    }

    public void Clear() { Text = ""; _age = _duration = _cooldown = 0; }
    private static double Ease(double value) => 1 - Math.Pow(1 - value, 3);
}
