// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

namespace BabbleFrameBridge.App;

public enum KittyAction
{
    Watch, Walk, Chase, Swat, SmackPanel, Zoomies, Groom, Sleep, Grumpy, Stretch,
    Sniff, Knead, Loaf, SlowBlink, LookAround, Stalk, Pounce, Rub, Scratch, Inspect,
    Yawn, Chirp, Meow, Roll, Circle,
    Backoff, Peek, ButtWiggle, PawLick, FaceWash, EarFlick, TailChase, Flop, Doze, Startle
}
public enum KittyAttention { Cursor, Environment, Self, Rest, Memory }
public enum KittyRoutine { None, Hunt, Settle, Recovery, Grooming, Explore, Play }

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
/// dense tanh layers (32 and 24), and separate 35-behavior / 5-attention heads.
/// All 3,720 weights were trained on synthetic episodes, including recurrence.
/// This simulates cat attention and behavior; it is not conscious thought or
/// a language model. No cloud, download, recordings or inference runtime.
/// Reproduce with tools/train_kitty_policy.py (NumPy is development-only).
/// </summary>
public sealed class KittyBrain
{
    public const int ActionCount = 35, AttentionCount = 5, InputCount = 22, RecurrentUnits = 24, ParameterCount = 3720;
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
        if (probabilities.Length < ActionCount) throw new ArgumentException("The policy needs 35 output slots.", nameof(probabilities));
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
    private readonly double[] _preferences = new double[KittyBrain.ActionCount];
    private readonly double[] _routineCooldowns = new double[7];
    private readonly Queue<KittyAction> _recent = new();
    private KittyAction[] _episode = [];
    private int _stage;
    private double _episodeAge;
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
    public double Frustration { get; private set; }
    public double Willingness => KittyBrain.Unit(Energy*.55+Trust*.3+(1-Irritation)*.25-Frustration*.25-Sleepiness*.2);
    public KittyRoutine Routine { get; private set; }
    public int RoutineStage => Routine==KittyRoutine.None ? 0 : _stage+1;
    public int RoutineLength => _episode.Length;
    public int CompletedRoutineCount { get; private set; }
    public int InterruptedRoutineCount { get; private set; }
    public string EpisodeDescription => Routine==KittyRoutine.None ? Intention : $"{Routine}: {Intention} ({RoutineStage}/{RoutineLength})";
    public KittyAttention Attention { get; private set; } = KittyAttention.Environment;
    public double AttentionAge { get; private set; }
    public double AttentionDuration { get; private set; } = 4;
    public double GazeInterest => Attention==KittyAttention.Cursor ? (.35+.65*Curiosity)*(1-Habituation*.7) : .55;
    public int DecisionCount { get; private set; }
    public int InferenceCount { get; private set; }
    public double MemoryActivity => _brain.MemoryActivity;
    public string Intention => Action switch {
        KittyAction.Stalk => "Stay low and watch before a pounce.",
        KittyAction.ButtWiggle => "Line up the little leap.",
        KittyAction.Pounce => "Catch the moving thing, then inspect it.",
        KittyAction.Chase or KittyAction.Swat => "Investigate the moving glint.",
        KittyAction.SmackPanel or KittyAction.Scratch => "Explore a surface with a paw.",
        KittyAction.Inspect or KittyAction.Sniff => "Check what changed around here.",
        KittyAction.Rub => "This spot feels familiar.",
        KittyAction.Circle or KittyAction.Knead => "Make a cozy place to rest.",
        KittyAction.Sleep or KittyAction.Loaf or KittyAction.Doze => "Stay somewhere comfortable and recharge.",
        KittyAction.Stretch or KittyAction.Yawn => "Wake up slowly and stretch.",
        KittyAction.Groom or KittyAction.PawLick or KittyAction.FaceWash => "Wash up and settle after activity.",
        KittyAction.Grumpy or KittyAction.Backoff => "A little space would feel better.",
        KittyAction.Startle => "Pause and work out what surprised me.",
        KittyAction.Peek => "Look again from a safer distance.",
        KittyAction.Chirp or KittyAction.Meow => "Ask for a little company.",
        KittyAction.SlowBlink => "Relax around familiar company.",
        KittyAction.EarFlick => "Something tiny caught my attention.",
        KittyAction.Zoomies => "Spend some pent-up play energy.",
        KittyAction.TailChase => "That tail is suddenly fascinating.",
        KittyAction.Roll or KittyAction.Flop => "Enjoy a comfortable moment.",
        _ => Habituation>.65 ? "The cursor can wait; notice something else."
            : Attention==KittyAttention.Memory ? "Remember an interesting place."
            : Attention==KittyAttention.Self ? "Take a moment for myself."
            : "See what is happening around me."
    };
    public string Thought => Intention;

    public KittyMind(int? seed=null)
    {
        _random=new Random(seed ?? Random.Shared.Next());
        Curiosity=Range(.52,.72); Trust=Range(.58,.76); SocialNeed=Range(.3,.5);
        for(var i=0;i<_preferences.Length;i++) _preferences[i]=Range(.86,1.16);
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
        if(!gentle) _startle=Math.Max(_startle,_clickPressure*.7);
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
        Stimulation=KittyBrain.Unit(Stimulation*Math.Exp(-dt/6)+wave*dt*.55*(1-Habituation*.45)*(Energy<.22 ? .15 : 1));
        Irritation=KittyBrain.Unit(Irritation-dt*.032);
        Trust=KittyBrain.Unit(Trust+(.68-Trust)*dt*.006);
        Affection=KittyBrain.Unit(Affection+(.55-Affection)*dt*.012);
        SocialNeed=KittyBrain.Unit(SocialNeed+dt*(next.UserPresence>.1 ? .0015 : .003));
        _idleSeconds=wave>.12 || _clickPressure>.08 ? 0 : _idleSeconds+dt;
        var moving=Action is KittyAction.Walk or KittyAction.Chase or KittyAction.Zoomies or KittyAction.Stalk or KittyAction.Pounce or KittyAction.Inspect or KittyAction.Circle or KittyAction.Backoff or KittyAction.TailChase;
        var playful=Action is KittyAction.Chase or KittyAction.Zoomies or KittyAction.Pounce or KittyAction.Swat or KittyAction.TailChase;
        _roamDrive=KittyBrain.Unit(_roamDrive+dt*(moving ? -.05 : .006));
        Curiosity=KittyBrain.Unit(Curiosity+dt*(Action is KittyAction.Sniff or KittyAction.Inspect ? -.015 : .003)+_novelty*dt*.015);
        _startle*=Math.Exp(-dt/8);
        _playMemory=KittyBrain.Unit(_playMemory*Math.Exp(-dt/30)+(playful ? dt*.15 : 0));
        _contactMemory*=Math.Exp(-dt/35);
        _restMemory=KittyBrain.Unit(_restMemory*Math.Exp(-dt/35)+(Action is KittyAction.Sleep or KittyAction.Loaf or KittyAction.Doze ? dt*.1 : 0));
        _completion*=Math.Exp(-dt/3);
        Frustration=KittyBrain.Unit(Frustration+dt*(Routine==KittyRoutine.Hunt && wave>.15 ? .014 : -.014));
        for(var i=0;i<_cooldowns.Length;i++) _cooldowns[i]=Math.Max(0,_cooldowns[i]-dt);
        for(var i=0;i<_routineCooldowns.Length;i++) _routineCooldowns[i]=Math.Max(0,_routineCooldowns[i]-dt);
        if(Action==KittyAction.Sleep)
        {
            Energy=KittyBrain.Unit(Energy+dt*.055); Sleepiness=KittyBrain.Unit(Sleepiness-dt*.06);
        }
        else
        {
            var cost=Action switch {
                KittyAction.Zoomies=>.09,KittyAction.Chase=>.026,KittyAction.Pounce=>.035,KittyAction.TailChase=>.024,
                KittyAction.Walk or KittyAction.Inspect or KittyAction.Circle or KittyAction.Backoff=>.01,
                KittyAction.Stalk=>.006,KittyAction.Swat or KittyAction.SmackPanel=>.014,
                KittyAction.Groom or KittyAction.Loaf or KittyAction.Doze=>-.004,_=>.0008 };
            Energy=KittyBrain.Unit(Energy-dt*cost);
            Sleepiness=KittyBrain.Unit(Sleepiness+dt*(.0035+(moving ? .006 : 0)+(1-Energy)*.002));
        }
        ActionAge+=dt; AttentionAge+=dt; _policyClock+=dt; _episodeAge+=dt;
        if(_policyClock>=.25 || !_modelReady)
        {
            _brain.Predict(Observation,_policy); _policyClock=0; _modelReady=true; InferenceCount++;
        }
        var urgent=Routine!=KittyRoutine.Recovery && (Irritation>.72 || _startle>.6) && ActionAge>.4;
        var wake=_reconsider && Action is KittyAction.Sleep or KittyAction.Doze && ActionAge>.4;
        var renewedPlay=wave>.45 && Stimulation>.45 && Energy>.35 && Action is KittyAction.Watch or KittyAction.Groom or KittyAction.Loaf;
        if(ActionAge>=ActionDuration || urgent || wake || renewedPlay && ActionAge>.7 || _reconsider && Routine==KittyRoutine.None && ActionAge>.45)
        {
            _reconsider=false; ChooseAction(urgent || wake || renewedPlay);
        }
        if(AttentionAge>=AttentionDuration) ChooseAttention();
    }

    private void ChooseAction(bool interrupted)
    {
        if(interrupted && Routine!=KittyRoutine.None) EndRoutine(false);
        if((Irritation>.72 || _startle>.6) && Routine!=KittyRoutine.Recovery)
        {
            BeginRoutine(KittyRoutine.Recovery,[KittyAction.Startle,KittyAction.Backoff,KittyAction.Grumpy,KittyAction.Peek,KittyAction.Sniff,KittyAction.SlowBlink]);
            SelectAction(_episode[0]); return;
        }
        if(Routine!=KittyRoutine.None)
        {
            var beforeCatch=Action is KittyAction.Chase or KittyAction.Stalk or KittyAction.ButtWiggle;
            var lostHunt=Routine==KittyRoutine.Hunt && (beforeCatch && _stimulus.CursorNear<.1 || Energy<.2);
            var restless=Routine==KittyRoutine.Settle && Stimulation>.55 && Energy>.3;
            if(lostHunt || restless || _episodeAge>90) EndRoutine(false);
            else
            {
                while(++_stage<_episode.Length)
                    if(Eligible(_episode[_stage])) { SelectAction(_episode[_stage]); return; }
                EndRoutine(true);
            }
        }
        Span<double> choices=stackalloc double[KittyBrain.ActionCount];
        var total=0d;
        for(var i=0;i<choices.Length;i++)
        {
            var action=(KittyAction)i;
            var repeats=_recent.Count(a=>a==action);
            var score=Math.Pow(_policy[i],1.25)*_preferences[i]*Math.Pow(.38,repeats);
            if(action==KittyAction.Zoomies) score*=1-Frustration*.6;
            choices[i]=Eligible(action) ? score : 0; total+=choices[i];
        }
        var selected=KittyAction.Watch;
        var remaining=_random.NextDouble()*total;
        for(var i=0;i<choices.Length;i++) { remaining-=choices[i]; if(remaining<=0 && choices[i]>0) { selected=(KittyAction)i; break; } }
        StartEpisodeFor(selected);
        SelectAction(selected);
    }

    private bool Eligible(KittyAction action) => _cooldowns[(int)action]<=0 && action switch {
        KittyAction.Swat=>_stimulus.CursorNear>.25 && Energy>.15,
        KittyAction.Chase=>_stimulus.CursorNear>.12 && _stimulus.CursorMotion>.08 && Energy>.2,
        KittyAction.Stalk or KittyAction.Pounce or KittyAction.ButtWiggle=>_stimulus.CursorNear>.12 && Energy>.2,
        KittyAction.SmackPanel or KittyAction.Scratch=>_stimulus.PanelNear>.25,
        KittyAction.Zoomies=>Energy>.35 && Irritation<.65,
        KittyAction.Sleep=>_clickPressure<.08 && (Stimulation<.35 || Energy<.18) && (Sleepiness>.3 || Energy<.4),
        KittyAction.Knead or KittyAction.Loaf or KittyAction.Circle=>(Stimulation<.5 || Energy<.22) && Irritation<.45,
        KittyAction.Doze=>_clickPressure<.08 && (Stimulation<.5 || Energy<.22) && Irritation<.45,
        KittyAction.Rub=>Trust>.5 && Irritation<.4 && (_stimulus.CursorNear>.1 || _stimulus.PanelNear>.2),
        KittyAction.Roll or KittyAction.Flop=>Trust>.5 && Irritation<.25 && Stimulation<.45,
        KittyAction.Yawn=>Sleepiness>.15 || _restMemory>.2,
        KittyAction.Chirp=>_playMemory>.1 || _novelty>.25,
        KittyAction.Meow=>SocialNeed>.25 && Irritation<.6,
        KittyAction.Startle=>_startle>.35,
        KittyAction.Backoff=>Irritation>.35 || _startle>.2,
        KittyAction.Peek=>_startle>.1 || Curiosity>.45,
        KittyAction.TailChase=>Energy>.4 && Stimulation<.7 && Irritation<.3,
        _=>true };

    private void StartEpisodeFor(KittyAction selected)
    {
        var (kind,path)=selected switch {
            KittyAction.Swat when _stimulus.CursorMotion>.15 && Energy>.4 => (KittyRoutine.Hunt,new[] { KittyAction.Swat,KittyAction.Stalk,KittyAction.ButtWiggle,KittyAction.Pounce,KittyAction.Inspect,KittyAction.PawLick }),
            KittyAction.Chase => (KittyRoutine.Hunt,new[] { KittyAction.Chase,KittyAction.Stalk,KittyAction.ButtWiggle,KittyAction.Pounce,KittyAction.Inspect,KittyAction.PawLick }),
            KittyAction.Stalk => (KittyRoutine.Hunt,new[] { KittyAction.Stalk,KittyAction.ButtWiggle,KittyAction.Pounce,KittyAction.Inspect,KittyAction.PawLick }),
            KittyAction.Pounce => (KittyRoutine.Hunt,new[] { KittyAction.Pounce,KittyAction.Inspect,KittyAction.PawLick }),
            KittyAction.Circle => (KittyRoutine.Settle,new[] { KittyAction.Circle,KittyAction.Knead,KittyAction.Loaf,KittyAction.Doze,KittyAction.Sleep,KittyAction.Stretch,KittyAction.Yawn }),
            KittyAction.Knead => (KittyRoutine.Settle,new[] { KittyAction.Knead,KittyAction.Loaf,KittyAction.Doze,KittyAction.Sleep,KittyAction.Stretch,KittyAction.Yawn }),
            KittyAction.Loaf => (KittyRoutine.Settle,new[] { KittyAction.Loaf,KittyAction.Doze,KittyAction.Sleep,KittyAction.Stretch,KittyAction.Yawn }),
            KittyAction.Sleep => (KittyRoutine.Settle,new[] { KittyAction.Sleep,KittyAction.Stretch,KittyAction.Yawn,KittyAction.SlowBlink }),
            KittyAction.Groom => (KittyRoutine.Grooming,new[] { KittyAction.Groom,KittyAction.PawLick,KittyAction.FaceWash }),
            KittyAction.PawLick => (KittyRoutine.Grooming,new[] { KittyAction.PawLick,KittyAction.FaceWash,KittyAction.Groom }),
            KittyAction.Inspect => (KittyRoutine.Explore,new[] { KittyAction.Inspect,KittyAction.Sniff,KittyAction.Rub,KittyAction.LookAround,KittyAction.EarFlick }),
            KittyAction.TailChase => (KittyRoutine.Play,new[] { KittyAction.TailChase,KittyAction.Roll,KittyAction.Flop,KittyAction.PawLick }),
            KittyAction.Zoomies => (KittyRoutine.Play,new[] { KittyAction.Zoomies,KittyAction.TailChase,KittyAction.Flop,KittyAction.Groom }),
            _ => (KittyRoutine.None,Array.Empty<KittyAction>()) };
        if(kind!=KittyRoutine.None && _routineCooldowns[(int)kind]<=0) BeginRoutine(kind,path);
    }
    private void BeginRoutine(KittyRoutine routine,KittyAction[] path)
    {
        Routine=routine; _episode=path; _stage=0; _episodeAge=0;
    }
    private void EndRoutine(bool completed)
    {
        if(Routine==KittyRoutine.None) return;
        _routineCooldowns[(int)Routine]=completed ? Range(10,22) : Range(5,10);
        if(completed) { CompletedRoutineCount++; _completion=1; Frustration=Math.Max(0,Frustration-.15); }
        else InterruptedRoutineCount++;
        Routine=KittyRoutine.None; _episode=[]; _stage=0; _episodeAge=0;
    }

    private void SelectAction(KittyAction selected)
    {
        if(Action==KittyAction.Pounce) { Frustration=Math.Max(0,Frustration-.35); _completion=1; }
        if(ActionAge>=ActionDuration) _preferences[(int)Action]=Math.Clamp(_preferences[(int)Action]+(Irritation<.4 ? .004 : -.004),.75,1.3);
        Action=selected; ActionAge=0; DecisionCount++;
        _recent.Enqueue(selected); while(_recent.Count>7) _recent.Dequeue();
        ActionDuration=selected switch {
            KittyAction.Walk or KittyAction.Inspect=>Range(2.5,5),KittyAction.Chase=>Range(1.4,3),
            KittyAction.Swat=>Range(.75,1.2),KittyAction.SmackPanel=>Range(1,1.6),
            KittyAction.Zoomies=>Range(3,5),KittyAction.Groom=>Range(4,7),
            KittyAction.Sleep=>Range(10,18),KittyAction.Grumpy=>Range(2.5,5),
            KittyAction.Stretch=>Range(2,3),KittyAction.Stalk=>Range(2,4),KittyAction.ButtWiggle=>Range(.7,1.2),
            KittyAction.Pounce=>Range(.9,1.5),KittyAction.Sniff=>Range(2,4),
            KittyAction.Circle=>Range(2.5,4),KittyAction.Knead=>Range(4,7),
            KittyAction.Loaf or KittyAction.Doze=>Range(6,12),KittyAction.LookAround=>Range(3,6),
            KittyAction.SlowBlink=>Range(2,3.5),KittyAction.Yawn=>Range(2,3.5),
            KittyAction.Chirp or KittyAction.Meow=>Range(1,2),KittyAction.Rub=>Range(2,4),
            KittyAction.Scratch=>Range(2,3.5),KittyAction.Roll or KittyAction.Flop=>Range(3,4.5),
            KittyAction.PawLick or KittyAction.FaceWash=>Range(2.5,4),KittyAction.Backoff=>Range(1.5,2.5),
            KittyAction.Peek=>Range(2,3.5),KittyAction.Startle=>Range(.55,.85),KittyAction.EarFlick=>Range(.6,1.3),
            KittyAction.TailChase=>Range(2.5,4.5),_=>Range(2,5) };
        var recovery=selected switch {
            KittyAction.Zoomies=>Range(12,20),KittyAction.Groom=>12,KittyAction.Swat=>1.2,
            KittyAction.SmackPanel=>3,KittyAction.Sleep=>6,KittyAction.Grumpy=>2.5,
            KittyAction.Stretch=>7,KittyAction.Scratch=>15,KittyAction.Roll or KittyAction.Flop=>20,
            KittyAction.Meow=>12,KittyAction.Chirp=>7,KittyAction.SlowBlink=>12,KittyAction.TailChase=>18,
            KittyAction.Yawn=>12,KittyAction.Circle=>12,KittyAction.Knead=>15,
            KittyAction.Startle=>8,KittyAction.Backoff=>6,KittyAction.EarFlick=>8,_=>0 };
        if(recovery>0) _cooldowns[(int)selected]=ActionDuration+recovery;
        ChooseAttention();
    }

    private void ChooseAttention()
    {
        KittyAttention? focus=Action switch {
            KittyAction.Sleep or KittyAction.Loaf or KittyAction.Circle or KittyAction.Knead or KittyAction.Doze=>KittyAttention.Rest,
            KittyAction.Groom or KittyAction.Stretch or KittyAction.Yawn or KittyAction.Roll or KittyAction.PawLick or KittyAction.FaceWash or KittyAction.TailChase or KittyAction.Flop=>KittyAttention.Self,
            KittyAction.Stalk or KittyAction.Pounce or KittyAction.Chase or KittyAction.Swat or KittyAction.ButtWiggle when _stimulus.CursorNear>.1=>KittyAttention.Cursor,
            KittyAction.Inspect or KittyAction.Sniff or KittyAction.LookAround or KittyAction.Peek or KittyAction.Startle or KittyAction.EarFlick=>KittyAttention.Environment,
            KittyAction.Backoff=>KittyAttention.Memory,_=>null };
        if(focus is null)
        {
            Span<double> scores=stackalloc double[KittyBrain.AttentionCount];
            var probabilities=_brain.AttentionProbabilities; var total=0d;
            for(var i=0;i<scores.Length;i++)
            {
                var value=Math.Pow(probabilities[i],1.2);
                if(i==(int)KittyAttention.Cursor) { if(_stimulus.CursorNear<.05 && _stimulus.UserPresence<.1) value=0; else value*=1-Habituation*.8; }
                scores[i]=value; total+=value;
            }
            var remaining=_random.NextDouble()*total; focus=KittyAttention.Environment;
            for(var i=0;i<scores.Length;i++) { remaining-=scores[i]; if(remaining<=0 && scores[i]>0) { focus=(KittyAttention)i; break; } }
        }
        Attention=focus.Value; AttentionAge=0; AttentionDuration=Range(2.5,7.5);
    }
    private double Range(double min,double max)=>min+_random.NextDouble()*(max-min);
}
