// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

namespace BabbleFrameBridge.App;

public enum KittyAction { Watch, Walk, Chase, Swat, SmackPanel, Zoomies, Groom, Sleep, Grumpy, Stretch }

/// <summary>Normalized sensory inputs; cursor motion is its recent speed, not an OS cursor action.</summary>
public readonly record struct KittyStimulus(double CursorNear = 0, double CursorMotion = 0, double PanelNear = 0);

/// <summary>All inputs are normalized to 0..1 and sanitized before inference.</summary>
public readonly record struct KittyObservation(double Energy, double Sleepiness, double Irritation, double Stimulation,
    double CursorNear, double CursorMotion, double ClickPressure, double Idle, double PanelNear,
    double Affection, double ActionAge, double RoamDrive);

/// <summary>
/// Original 12→16→10 neural policy, trained on synthetic kitty scenarios. It is
/// imitation learning, not a language model. No cloud, downloads, inference
/// framework, telemetry, user data, or online training is involved. Reproduction:
/// tools/train_kitty_policy.py. The generated weights occupy 3,024 numeric bytes.
/// </summary>
public sealed class KittyBrain
{
    public const int ActionCount = 10;
    public const int ParameterCount = 378;

    public double[] Predict(KittyObservation observation)
    {
        var result = new double[ActionCount];
        Predict(observation, result);
        return result;
    }

    public void Predict(KittyObservation observation, Span<double> probabilities)
    {
        if (probabilities.Length < ActionCount) throw new ArgumentException("The policy needs ten output slots.", nameof(probabilities));
        Span<double> input = stackalloc double[] {
            observation.Energy, observation.Sleepiness, observation.Irritation, observation.Stimulation,
            observation.CursorNear, observation.CursorMotion, observation.ClickPressure, observation.Idle,
            observation.PanelNear, observation.Affection, observation.ActionAge, observation.RoamDrive };
        for (var i = 0; i < input.Length; i++) input[i] = Unit(input[i])*2-1;
        Span<double> hidden = stackalloc double[16];
        for (var j = 0; j < hidden.Length; j++)
        {
            var sum = KittyModel.HiddenBias[j];
            for (var i = 0; i < input.Length; i++) sum += input[i]*KittyModel.InputWeights[i*16+j];
            hidden[j] = Math.Tanh(sum);
        }
        var largest = double.NegativeInfinity;
        for (var k = 0; k < ActionCount; k++)
        {
            var sum = KittyModel.OutputBias[k];
            for (var j = 0; j < hidden.Length; j++) sum += hidden[j]*KittyModel.OutputWeights[j*ActionCount+k];
            probabilities[k] = sum;
            largest = Math.Max(largest, sum);
        }
        var total = 0d;
        for (var k = 0; k < ActionCount; k++) total += probabilities[k] = Math.Exp(probabilities[k]-largest);
        for (var k = 0; k < ActionCount; k++) probabilities[k] /= total;
    }

    internal static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}

/// <summary>
/// Small needs simulation and action timing around the learned policy. Physical
/// eligibility, finite animation durations, and recovery cooldowns prevent the
/// pet from swatting absent targets or getting trapped in perpetual zoomies.
/// Rendering and cursor/panel effects remain entirely outside this class.
/// </summary>
public sealed class KittyMind
{
    private readonly KittyBrain _brain = new();
    private readonly Random _random;
    private readonly double[] _cooldowns = new double[KittyBrain.ActionCount];
    private double _clickPressure, _idleSeconds, _roamDrive = .25;
    private bool _reconsider;
    private KittyStimulus _stimulus;

    public KittyAction Action { get; private set; } = KittyAction.Watch;
    public double ActionAge { get; private set; }
    public double ActionDuration { get; private set; } = 3;
    public double Energy { get; private set; } = .85;
    public double Sleepiness { get; private set; } = .08;
    public double Irritation { get; private set; }
    public double Stimulation { get; private set; }
    public double Affection { get; private set; } = .55;
    public int DecisionCount { get; private set; }

    public KittyMind(int? seed = null) => _random = new Random(seed ?? Random.Shared.Next());

    public KittyObservation Observation => new(Energy, Sleepiness, Irritation, Stimulation,
        _stimulus.CursorNear, _stimulus.CursorMotion, _clickPressure, Math.Min(_idleSeconds/45, 1),
        _stimulus.PanelNear, Affection, Math.Min(ActionAge/Math.Max(.1, ActionDuration), 1), _roamDrive);

    /// <summary>One click on the kitty; isolated gentle clicks are tolerated, repeated pestering is irritating.</summary>
    public void Pester()
    {
        _clickPressure = KittyBrain.Unit(_clickPressure+.19);
        Stimulation = KittyBrain.Unit(Stimulation+.12+.08*_clickPressure);
        Irritation = KittyBrain.Unit(Irritation+(.02+.12*_clickPressure)*(_clickPressure > .4 ? 1 : .3));
        Affection = KittyBrain.Unit(Affection+(_clickPressure < .4 ? .025 : -.02));
        _idleSeconds = 0;
        _reconsider = true;
    }

    /// <summary>Called at the controller's low-frequency sensing cadence, independently of rendering.</summary>
    public void Tick(double deltaSeconds, KittyStimulus stimulus)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0) return;
        var dt = Math.Min(deltaSeconds, .5); // Never replay minutes of hidden time on restore.
        _stimulus = new(KittyBrain.Unit(stimulus.CursorNear), KittyBrain.Unit(stimulus.CursorMotion), KittyBrain.Unit(stimulus.PanelNear));
        var wave = _stimulus.CursorNear*_stimulus.CursorMotion;
        _clickPressure *= Math.Exp(-dt/1.8);
        Stimulation = KittyBrain.Unit(Stimulation*Math.Exp(-dt/6)+wave*dt*.50);
        Irritation = KittyBrain.Unit(Irritation-dt*.032);
        Affection = KittyBrain.Unit(Affection+(.55-Affection)*dt*.012);
        _idleSeconds = wave > .12 || _clickPressure > .08 ? 0 : _idleSeconds+dt;
        var moving = Action is KittyAction.Walk or KittyAction.Chase or KittyAction.Zoomies;
        _roamDrive = KittyBrain.Unit(_roamDrive+dt*(moving ? -.07 : .007));
        for (var i = 0; i < _cooldowns.Length; i++) _cooldowns[i] = Math.Max(0, _cooldowns[i]-dt);

        if (Action == KittyAction.Sleep)
        {
            Energy = KittyBrain.Unit(Energy+dt*.065);
            Sleepiness = KittyBrain.Unit(Sleepiness-dt*.072);
        }
        else
        {
            var cost = Action switch {
                KittyAction.Zoomies => .09, KittyAction.Chase => .026, KittyAction.Walk => .010,
                KittyAction.Swat or KittyAction.SmackPanel => .014, KittyAction.Groom => -.003,
                _ => .0008 };
            Energy = KittyBrain.Unit(Energy-dt*cost);
            Sleepiness = KittyBrain.Unit(Sleepiness+dt*(.0035+(moving ? .006 : 0)+(1-Energy)*.002));
        }
        ActionAge += dt;
        if (wave > .45 && Stimulation > .45 && Action is KittyAction.Watch or KittyAction.Groom or KittyAction.Sleep)
            _reconsider = true;
        if (ActionAge >= ActionDuration || _reconsider && ActionAge >= .45)
        {
            _reconsider = false;
            ChooseAction();
        }
    }

    private void ChooseAction()
    {
        Span<double> choices = stackalloc double[KittyBrain.ActionCount];
        _brain.Predict(Observation, choices);
        DecisionCount++;
        var sum = 0d;
        for (var i = 0; i < choices.Length; i++)
        {
            var action = (KittyAction)i;
            var eligible = _cooldowns[i] <= 0 && (action switch {
                KittyAction.Swat => _stimulus.CursorNear > .25,
                KittyAction.Chase => _stimulus.CursorNear > .12 && _stimulus.CursorMotion > .08,
                KittyAction.SmackPanel => _stimulus.PanelNear > .25,
                KittyAction.Zoomies => Energy > .35,
                KittyAction.Sleep => _clickPressure < .08 && Stimulation < .35 && (Sleepiness > .3 || Energy < .4),
                _ => true });
            choices[i] = eligible ? Math.Pow(choices[i], 1.6) : 0;
            sum += choices[i];
        }
        var selected = KittyAction.Watch;
        var remaining = _random.NextDouble()*sum;
        for (var i = 0; i < choices.Length; i++)
        {
            remaining -= choices[i];
            if (remaining <= 0 && choices[i] > 0) { selected = (KittyAction)i; break; }
        }
        Action = selected;
        ActionAge = 0;
        ActionDuration = selected switch {
            KittyAction.Walk => Range(2, 4), KittyAction.Chase => Range(1.4, 3),
            KittyAction.Swat => Range(.75, 1.2), KittyAction.SmackPanel => Range(1, 1.6),
            KittyAction.Zoomies => Range(3, 5), KittyAction.Groom => Range(3.5, 6),
            KittyAction.Sleep => Range(8, 15), KittyAction.Grumpy => Range(2.5, 5),
            KittyAction.Stretch => Range(1.8, 3.2), _ => Range(2, 5) };
        var recovery = selected switch {
            KittyAction.Zoomies => Range(12, 20), KittyAction.Groom => 12,
            KittyAction.Swat => 1.2, KittyAction.SmackPanel => 3,
            KittyAction.Sleep => 6, KittyAction.Grumpy => 1.5,
            KittyAction.Stretch => 7, _ => 0 };
        if (recovery > 0) _cooldowns[(int)selected] = ActionDuration+recovery;
    }

    private double Range(double min, double max) => min+_random.NextDouble()*(max-min);
}
