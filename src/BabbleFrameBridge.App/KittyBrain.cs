// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

namespace BabbleFrameBridge.App;

public enum KittyAction
{
    Watch, Walk, Chase, Swat, SmackPanel, Zoomies, Groom, Sleep, Grumpy, Stretch,
    Sniff, Knead, Loaf, SlowBlink, LookAround, Stalk, Pounce, Rub, Scratch, Inspect,
    Yawn, Chirp, Meow, Roll, Circle
}
public enum KittyAttention { Cursor, Environment, Self, Rest, Memory }

/// <summary>Normalized app-local senses. Nothing is recorded or sent elsewhere.</summary>
public readonly record struct KittyStimulus(double CursorNear = 0, double CursorMotion = 0,
    double PanelNear = 0, double Novelty = 0, double UserPresence = 0);

public readonly record struct KittyObservation(double Energy, double Sleepiness, double Irritation, double Stimulation,
    double CursorNear, double CursorMotion, double ClickPressure, double Idle, double PanelNear,
    double Affection, double ActionAge, double RoamDrive, double Curiosity = .6, double Trust = .65,
    double Habituation = 0, double Novelty = 0, double SocialNeed = .4, double Startle = 0,
    double PlayMemory = 0, double RestMemory = 0, double ContactMemory = 0, double Completion = 0);

/// <summary>
/// Original trained temporal policy: 22 inputs, 24 recurrent memory units, two
/// dense tanh layers (32 and 24), and separate 25-behavior / 5-attention heads.
/// All 3,470 weights were trained on synthetic episodes, including recurrence.
/// This simulates cat attention and behavior; it is not conscious thought or
/// a language model. No cloud, download, recordings or inference runtime.
/// Reproduce with tools/train_kitty_policy.py (NumPy is development-only).
/// </summary>
public sealed class KittyBrain
{
    public const int ActionCount = 25, AttentionCount = 5, InputCount = 22, RecurrentUnits = 24, ParameterCount = 3470;
    private readonly double[] _memory = new double[RecurrentUnits];
    private readonly double[] _attention = new double[AttentionCount];
    internal ReadOnlySpan<double> RecurrentState => _memory;
    public ReadOnlySpan<double> AttentionProbabilities => _attention;
    public double MemoryActivity => Math.Sqrt(_memory.Sum(value => value*value)/RecurrentUnits);
    public void ResetMemory() { Array.Clear(_memory); Array.Clear(_attention); }

    public double[] Predict(KittyObservation observation)
    {
        var result = new double[ActionCount];
        Predict(observation, result);
        return result;
    }

    public void Predict(KittyObservation o, Span<double> probabilities)
    {
        if (probabilities.Length < ActionCount) throw new ArgumentException("The policy needs 25 output slots.", nameof(probabilities));
        Span<double> input = stackalloc double[] {
            o.Energy,o.Sleepiness,o.Irritation,o.Stimulation,o.CursorNear,o.CursorMotion,o.ClickPressure,
            o.Idle,o.PanelNear,o.Affection,o.ActionAge,o.RoamDrive,o.Curiosity,o.Trust,o.Habituation,
            o.Novelty,o.SocialNeed,o.Startle,o.PlayMemory,o.RestMemory,o.ContactMemory,o.Completion };
        for (var i=0;i<input.Length;i++) input[i]=Unit(input[i])*2-1;
        Span<double> next = stackalloc double[RecurrentUnits];
        for (var j=0;j<RecurrentUnits;j++)
        {
            var value=KittyModel.RecurrentBias[j];
            for (var i=0;i<InputCount;i++) value+=input[i]*KittyModel.InputWeights[i*RecurrentUnits+j];
            for (var i=0;i<RecurrentUnits;i++) value+=_memory[i]*KittyModel.RecurrentWeights[i*RecurrentUnits+j];
            next[j]=Math.Tanh(value);
        }
        next.CopyTo(_memory);
        Span<double> dense = stackalloc double[32];
        Layer(next,dense,KittyModel.DenseWeights,KittyModel.DenseBias);
        Span<double> deep = stackalloc double[24];
        Layer(dense,deep,KittyModel.DeepWeights,KittyModel.DeepBias);
        Softmax(deep,probabilities[..ActionCount],KittyModel.OutputWeights,KittyModel.OutputBias);
        Softmax(deep,_attention,KittyModel.AttentionWeights,KittyModel.AttentionBias);
    }

    private static void Layer(ReadOnlySpan<double> input, Span<double> output, double[] weights, double[] bias)
    {
        for (var j=0;j<output.Length;j++)
        {
            var value=bias[j];
            for (var i=0;i<input.Length;i++) value+=input[i]*weights[i*output.Length+j];
            output[j]=Math.Tanh(value);
        }
    }
    private static void Softmax(ReadOnlySpan<double> input, Span<double> output, double[] weights, double[] bias)
    {
        var largest=double.NegativeInfinity;
        for (var j=0;j<output.Length;j++)
        {
            var value=bias[j];
            for (var i=0;i<input.Length;i++) value+=input[i]*weights[i*output.Length+j];
            output[j]=value; largest=Math.Max(largest,value);
        }
        var sum=0d;
        for(var j=0;j<output.Length;j++) sum+=output[j]=Math.Exp(output[j]-largest);
        for(var j=0;j<output.Length;j++) output[j]/=sum;
    }
    internal static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value,0,1) : 0;
}

/// <summary>
/// Needs, short-term episodic traces, preferences, learned attention and finite
/// action sequences surround the recurrent policy. Eligibility and cooldowns
/// are physical/UX constraints, not claims of animal cognition or sentience.
/// </summary>
public sealed class KittyMind
{
    private readonly KittyBrain _brain = new();
    private readonly Random _random;
    private readonly double[] _cooldowns = new double[KittyBrain.ActionCount];
    private readonly double[] _policy = new double[KittyBrain.ActionCount];
    private double _clickPressure,_idleSeconds,_roamDrive=.25,_novelty,_startle,_playMemory,_restMemory,_contactMemory;
    private double _policyClock,_completion;
    private bool _reconsider,_modelReady;
    private KittyStimulus _stimulus;
    public KittyAction Action { get; private set; } = KittyAction.Watch;
    public double ActionAge { get; private set; }
    public double ActionDuration { get; private set; } = 3;
    public double Energy { get; private set; } = .85;
    public double Sleepiness { get; private set; } = .08;
    public double Irritation { get; private set; }
    public double Stimulation { get; private set; }
    public double Affection { get; private set; } = .55;
    public double Curiosity { get; private set; } = .6;
    public double Trust { get; private set; } = .65;
    public double Habituation { get; private set; }
    public double SocialNeed { get; private set; } = .4;
    public KittyAttention Attention { get; private set; } = KittyAttention.Environment;
    public double AttentionAge { get; private set; }
    public double AttentionDuration { get; private set; } = 4;
    public double GazeInterest => Attention==KittyAttention.Cursor ? (.35+.65*Curiosity)*(1-Habituation*.7) : .55;
    public int DecisionCount { get; private set; }
    public int InferenceCount { get; private set; }
    public double MemoryActivity => _brain.MemoryActivity;
    public string Intention => Action switch {
        KittyAction.Stalk => "Watch, wait, then pounce.",
        KittyAction.Pounce => "Catch the moving thing, then settle.",
        KittyAction.Chase or KittyAction.Swat => "A moving thing is worth investigating.",
        KittyAction.SmackPanel or KittyAction.Scratch => "Explore a surface with a paw.",
        KittyAction.Inspect or KittyAction.Sniff => "Investigate something new.",
        KittyAction.Rub => "Mark a familiar, comfortable spot.",
        KittyAction.Circle or KittyAction.Knead => "Make a cozy place to rest.",
        KittyAction.Sleep or KittyAction.Loaf => "This feels like a safe place to recharge.",
        KittyAction.Stretch or KittyAction.Yawn => "Wake up slowly and stretch.",
        KittyAction.Groom => "A little grooming after activity.",
        KittyAction.Grumpy => "Too much attention; make some space.",
        KittyAction.Chirp or KittyAction.Meow => "Ask for a little company.",
        KittyAction.SlowBlink => "Relax around familiar company.",
        KittyAction.Zoomies => "Spend some pent-up play energy.",
        KittyAction.Roll => "Enjoy a comfortable moment.",
        _ => Habituation>.65 ? "The cursor can wait; look at something else."
            : Attention==KittyAttention.Memory ? "Return to a familiar interesting place."
            : Attention==KittyAttention.Self ? "Take a moment for myself."
            : "See what is happening around me."
    };
    public string Thought => Intention;

    public KittyMind(int? seed=null)
    {
        _random=new Random(seed ?? Random.Shared.Next());
        Curiosity=Range(.52,.72); Trust=Range(.58,.76); SocialNeed=Range(.3,.5);
    }
    public KittyObservation Observation => new(Energy,Sleepiness,Irritation,Stimulation,_stimulus.CursorNear,
        _stimulus.CursorMotion,_clickPressure,Math.Min(_idleSeconds/45,1),_stimulus.PanelNear,Affection,
        Math.Min(ActionAge/Math.Max(.1,ActionDuration),1),_roamDrive,Curiosity,Trust,Habituation,_novelty,
        SocialNeed,_startle,_playMemory,_restMemory,_contactMemory,_completion);

    public void Pester()
    {
        _clickPressure=KittyBrain.Unit(_clickPressure+.19);
        var gentle=_clickPressure<.4;
        Stimulation=KittyBrain.Unit(Stimulation+.11+.07*_clickPressure);
        Irritation=KittyBrain.Unit(Irritation+(.02+.13*_clickPressure)*(gentle ? .3 : 1));
        Trust=KittyBrain.Unit(Trust+(gentle ? .009 : -.018));
        Affection=KittyBrain.Unit(Affection+(gentle ? .025 : -.02));
        SocialNeed=KittyBrain.Unit(SocialNeed-.12);
        _contactMemory=1; _idleSeconds=0; _reconsider=true; _modelReady=false;
        if(!gentle) _startle=Math.Max(_startle,.18);
    }

    public void Tick(double deltaSeconds, KittyStimulus stimulus)
    {
        if(!double.IsFinite(deltaSeconds) || deltaSeconds<0) return;
        var dt=Math.Min(deltaSeconds,.5);
        var next=new KittyStimulus(KittyBrain.Unit(stimulus.CursorNear),KittyBrain.Unit(stimulus.CursorMotion),
            KittyBrain.Unit(stimulus.PanelNear),KittyBrain.Unit(stimulus.Novelty),KittyBrain.Unit(stimulus.UserPresence));
        var change=Math.Abs(next.CursorNear-_stimulus.CursorNear)+Math.Abs(next.CursorMotion-_stimulus.CursorMotion);
        var panelChange=Math.Max(0,next.PanelNear-_stimulus.PanelNear);
        _novelty=Math.Max(_novelty*Math.Exp(-dt/6),Math.Max(next.Novelty,Math.Clamp(change*.5+panelChange*.3,0,1)));
        var repetitive=next.CursorNear>.1 && change<.14;
        Habituation=KittyBrain.Unit(Habituation+dt*(repetitive ? .014 : -.025)-_novelty*dt*.025);
        _stimulus=next;
        var wave=next.CursorNear*next.CursorMotion;
        _clickPressure*=Math.Exp(-dt/1.8);
        var playGain=Energy<.22 ? .15 : 1;
        Stimulation=KittyBrain.Unit(Stimulation*Math.Exp(-dt/6)+wave*dt*.55*(1-Habituation*.45)*playGain);
        Irritation=KittyBrain.Unit(Irritation-dt*.032);
        Trust=KittyBrain.Unit(Trust+(.68-Trust)*dt*.006);
        Affection=KittyBrain.Unit(Affection+(.55-Affection)*dt*.012);
        SocialNeed=KittyBrain.Unit(SocialNeed+dt*(next.UserPresence>.1 ? .0015 : .003));
        _idleSeconds=wave>.12 || _clickPressure>.08 ? 0 : _idleSeconds+dt;
        var moving=Action is KittyAction.Walk or KittyAction.Chase or KittyAction.Zoomies or KittyAction.Stalk or KittyAction.Pounce or KittyAction.Inspect or KittyAction.Circle;
        var playful=Action is KittyAction.Chase or KittyAction.Zoomies or KittyAction.Pounce or KittyAction.Swat;
        _roamDrive=KittyBrain.Unit(_roamDrive+dt*(moving ? -.05 : .006));
        Curiosity=KittyBrain.Unit(Curiosity+dt*(Action is KittyAction.Sniff or KittyAction.Inspect ? -.015 : .003)+_novelty*dt*.015);
        _startle*=Math.Exp(-dt/8);
        _playMemory=KittyBrain.Unit(_playMemory*Math.Exp(-dt/30)+(playful ? dt*.15 : 0));
        _contactMemory*=Math.Exp(-dt/35);
        _restMemory=KittyBrain.Unit(_restMemory*Math.Exp(-dt/35)+(Action is KittyAction.Sleep or KittyAction.Loaf ? dt*.1 : 0));
        _completion*=Math.Exp(-dt/3);
        for(var i=0;i<_cooldowns.Length;i++) _cooldowns[i]=Math.Max(0,_cooldowns[i]-dt);
        if(Action==KittyAction.Sleep)
        {
            Energy=KittyBrain.Unit(Energy+dt*.055); Sleepiness=KittyBrain.Unit(Sleepiness-dt*.06);
        }
        else
        {
            var cost=Action switch {
                KittyAction.Zoomies=>.09,KittyAction.Chase=>.026,KittyAction.Pounce=>.035,
                KittyAction.Walk or KittyAction.Inspect or KittyAction.Circle=>.01,
                KittyAction.Stalk=>.006,KittyAction.Swat or KittyAction.SmackPanel=>.014,
                KittyAction.Groom or KittyAction.Loaf=>-.004,_=>.0008 };
            Energy=KittyBrain.Unit(Energy-dt*cost);
            Sleepiness=KittyBrain.Unit(Sleepiness+dt*(.0035+(moving ? .006 : 0)+(1-Energy)*.002));
        }
        ActionAge+=dt; AttentionAge+=dt; _policyClock+=dt;
        if(_policyClock>=.25 || !_modelReady)
        {
            _brain.Predict(Observation,_policy); _policyClock=0; _modelReady=true; InferenceCount++;
        }
        if(wave>.45 && Stimulation>.45 && Action is KittyAction.Watch or KittyAction.Groom or KittyAction.Sleep or KittyAction.Loaf)
            _reconsider=true;
        if(ActionAge>=ActionDuration || _reconsider && ActionAge>=.45)
        {
            _reconsider=false; ChooseAction();
        }
        if(AttentionAge>=AttentionDuration) ChooseAttention();
    }

    private void ChooseAction()
    {
        Span<double> choices=stackalloc double[KittyBrain.ActionCount];
        for(var i=0;i<choices.Length;i++) choices[i]=Math.Pow(_policy[i],1.35);
        // Episode continuity priors reflect finite physical activities. The
        // recurrent learned policy still scores alternatives, needs and goals.
        if(ActionAge>=ActionDuration)
        {
            if(Action==KittyAction.Stalk && _stimulus.CursorNear>.12) choices[(int)KittyAction.Pounce]+=.45;
            if(Action==KittyAction.Pounce) { choices[(int)KittyAction.Groom]+=.12; choices[(int)KittyAction.Chirp]+=.1; _completion=1; }
            if(Action==KittyAction.Inspect) choices[(int)KittyAction.Sniff]+=.3;
            if(Action==KittyAction.Sniff && Trust>.5) choices[(int)KittyAction.Rub]+=.2;
            if(Action==KittyAction.Circle) choices[(int)KittyAction.Knead]+=.45;
            if(Action==KittyAction.Knead) { choices[(int)KittyAction.Loaf]+=.3; choices[(int)KittyAction.Sleep]+=.25; }
            if(Action==KittyAction.Sleep && Stimulation<.35 && Irritation<.3) choices[(int)KittyAction.Stretch]+=.8;
            if(Action==KittyAction.Stretch && _restMemory>.2) choices[(int)KittyAction.Yawn]+=.6;
        }
        var total=0d;
        for(var i=0;i<choices.Length;i++)
        {
            var action=(KittyAction)i;
            var eligible=_cooldowns[i]<=0 && action switch {
                KittyAction.Swat=>_stimulus.CursorNear>.25 && Energy>.15,
                KittyAction.Chase=>_stimulus.CursorNear>.12 && _stimulus.CursorMotion>.08 && Energy>.2,
                KittyAction.Stalk or KittyAction.Pounce=>_stimulus.CursorNear>.12 && Energy>.2,
                KittyAction.SmackPanel or KittyAction.Scratch=>_stimulus.PanelNear>.25,
                KittyAction.Zoomies=>Energy>.35 && Irritation<.65,
                KittyAction.Sleep=>_clickPressure<.08 && (Stimulation<.35 || Energy<.18) && (Sleepiness>.3 || Energy<.4),
                KittyAction.Knead or KittyAction.Loaf or KittyAction.Circle=>(Stimulation<.5 || Energy<.22) && Irritation<.45,
                KittyAction.Rub=>Trust>.5 && Irritation<.4 && (_stimulus.CursorNear>.1 || _stimulus.PanelNear>.2),
                KittyAction.Roll=>Trust>.5 && Irritation<.25 && Stimulation<.45,
                KittyAction.Yawn=>Sleepiness>.15 || _restMemory>.2,
                KittyAction.Chirp=>_playMemory>.1 || _novelty>.25,
                KittyAction.Meow=>SocialNeed>.25 && Irritation<.6,
                _=>true };
            choices[i]=eligible ? choices[i] : 0; total+=choices[i];
        }
        var selected=KittyAction.Watch;
        var remaining=_random.NextDouble()*total;
        for(var i=0;i<choices.Length;i++) { remaining-=choices[i]; if(remaining<=0 && choices[i]>0) { selected=(KittyAction)i; break; } }
        Action=selected; ActionAge=0; DecisionCount++;
        ActionDuration=selected switch {
            KittyAction.Walk or KittyAction.Inspect=>Range(2.5,5),KittyAction.Chase=>Range(1.4,3),
            KittyAction.Swat=>Range(.75,1.2),KittyAction.SmackPanel=>Range(1,1.6),
            KittyAction.Zoomies=>Range(3,5),KittyAction.Groom=>Range(4,7),
            KittyAction.Sleep=>Range(10,18),KittyAction.Grumpy=>Range(2.5,5),
            KittyAction.Stretch=>Range(2,3),KittyAction.Stalk=>Range(2,4),
            KittyAction.Pounce=>Range(.9,1.5),KittyAction.Sniff=>Range(2,4),
            KittyAction.Circle=>Range(2.5,4),KittyAction.Knead=>Range(4,7),
            KittyAction.Loaf=>Range(6,12),KittyAction.LookAround=>Range(3,6),
            KittyAction.SlowBlink=>Range(2,3.5),KittyAction.Yawn=>Range(2,3.5),
            KittyAction.Chirp or KittyAction.Meow=>Range(1,2),KittyAction.Rub=>Range(2,4),
            KittyAction.Scratch=>Range(2,3.5),KittyAction.Roll=>Range(3,4.5),_=>Range(2,5) };
        var recovery=selected switch {
            KittyAction.Zoomies=>Range(12,20),KittyAction.Groom=>12,KittyAction.Swat=>1.2,
            KittyAction.SmackPanel=>3,KittyAction.Sleep=>6,KittyAction.Grumpy=>2.5,
            KittyAction.Stretch=>7,KittyAction.Scratch=>15,KittyAction.Roll=>20,
            KittyAction.Meow=>12,KittyAction.Chirp=>7,KittyAction.SlowBlink=>12,
            KittyAction.Yawn=>12,KittyAction.Circle=>12,KittyAction.Knead=>15,_=>0 };
        if(recovery>0) _cooldowns[(int)selected]=ActionDuration+recovery;
        ChooseAttention();
    }

    private void ChooseAttention()
    {
        KittyAttention? focus=Action switch {
            KittyAction.Sleep or KittyAction.Loaf or KittyAction.Circle or KittyAction.Knead=>KittyAttention.Rest,
            KittyAction.Groom or KittyAction.Stretch or KittyAction.Yawn or KittyAction.Roll=>KittyAttention.Self,
            KittyAction.Stalk or KittyAction.Pounce or KittyAction.Chase or KittyAction.Swat when _stimulus.CursorNear>.1=>KittyAttention.Cursor,
            KittyAction.Inspect or KittyAction.Sniff or KittyAction.LookAround=>KittyAttention.Environment,
            _=>null };
        if(focus is null)
        {
            Span<double> scores=stackalloc double[KittyBrain.AttentionCount];
            var probabilities=_brain.AttentionProbabilities;
            var total=0d;
            for(var i=0;i<scores.Length;i++)
            {
                var value=Math.Pow(probabilities[i],1.2);
                if(i==(int)KittyAttention.Cursor)
                {
                    if(_stimulus.CursorNear<.05 && _stimulus.UserPresence<.1) value=0;
                    else value*=1-Habituation*.8;
                }
                scores[i]=value; total+=value;
            }
            var remaining=_random.NextDouble()*total;
            focus=KittyAttention.Environment;
            for(var i=0;i<scores.Length;i++) { remaining-=scores[i]; if(remaining<=0 && scores[i]>0) { focus=(KittyAttention)i; break; } }
        }
        Attention=focus.Value; AttentionAge=0; AttentionDuration=Range(2.5,7.5);
    }

    private double Range(double min,double max)=>min+_random.NextDouble()*(max-min);
}
