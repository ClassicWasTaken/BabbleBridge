// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using BabbleFrameBridge.App;

var checks=0;
void Check(bool pass,string message) { if(!pass) throw new Exception(message); checks++; }
var arrays=new[] { KittyModel.InputWeights,KittyModel.RecurrentWeights,KittyModel.RecurrentBias,
    KittyModel.DenseWeights,KittyModel.DenseBias,KittyModel.DeepWeights,KittyModel.DeepBias,
    KittyModel.OutputWeights,KittyModel.OutputBias,KittyModel.AttentionWeights,KittyModel.AttentionBias };
Check(arrays.Sum(a=>a.Length)==KittyBrain.ParameterCount,"Model parameter inventory changed.");
Check(KittyBrain.ParameterCount==3720 && KittyBrain.ParameterCount*8<100000,"The recurrent policy exceeded its lightweight budget.");
Check(arrays.All(a=>a.All(double.IsFinite)),"Model weights contain invalid values.");
Check(Enum.GetValues<KittyAction>().Length==35 && (int)KittyAction.Stretch==9 && (int)KittyAction.Circle==24 && (int)KittyAction.Startle==34,
    "Existing actions were renumbered or the new cat repertoire is incomplete.");
Check(KittyModel.RecurrentWeights.Any(w=>Math.Abs(w)>.1),"The recurrent pathway is not trained.");

var calm=new KittyObservation(.85,.08,0,0,0,0,0,0,0,.55,0,.25);
KittyAction Best(KittyObservation o) {
    var brain=new KittyBrain(); var p=brain.Predict(o);
    return (KittyAction)Array.IndexOf(p,p.Max());
}
Check(Best(calm) is KittyAction.Watch or KittyAction.LookAround or KittyAction.Loaf or KittyAction.SlowBlink or KittyAction.Roll,"A quiet kitty has an implausible initial priority.");
Check(Best(calm with { Stimulation=.95,CursorNear=1,CursorMotion=1 })==KittyAction.Zoomies,"Strong play stimulation did not favor zoomies.");
Check(Best(calm with { Irritation=.95,ClickPressure=1,Stimulation=.7 })==KittyAction.Grumpy,"Pestering did not favor a grumpy response.");
Check(Best(calm with { Energy=.25,Sleepiness=.85,Idle=1 })==KittyAction.Sleep,"A quiet exhausted kitty did not favor sleep.");

var a=new KittyBrain(); var b=new KittyBrain();
for(var i=0;i<12;i++) {
    a.Predict(calm with { CursorNear=1,CursorMotion=1,Stimulation=.4 });
    b.Predict(calm);
}
var afterPlay=a.Predict(calm); var afterQuiet=b.Predict(calm);
Check(!a.RecurrentState.SequenceEqual(b.RecurrentState),"Different recent histories produced identical recurrent memory.");
Check(afterPlay.Zip(afterQuiet,(x,y)=>Math.Abs(x-y)).Sum()>.001,"Past experience did not influence the same present observation.");
a.ResetMemory(); b.ResetMemory();
Check(a.Predict(calm).SequenceEqual(b.Predict(calm)),"Resetting temporal memory was not deterministic.");

foreach(var o in new[] { calm,default,new KittyObservation(double.NaN,double.PositiveInfinity,-99,100,0,0,0,0,0,0,0,0) }) {
    var p=a.Predict(o);
    Check(p.All(v=>double.IsFinite(v)&&v>=0&&v<=1)&&Math.Abs(p.Sum()-1)<1e-12,"Behavior output is not a bounded distribution.");
    var gaze=a.AttentionProbabilities.ToArray();
    Check(gaze.All(v=>double.IsFinite(v)&&v>=0&&v<=1)&&Math.Abs(gaze.Sum()-1)<1e-12,"Attention output is not a bounded distribution.");
}

var attentionMind=new KittyMind(11);
var cursorTicks=0; var otherTicks=0; var focusModes=new HashSet<KittyAttention>();
for(var i=0;i<1200;i++) {
    attentionMind.Tick(.125,new(.8,0,0,0,1)); focusModes.Add(attentionMind.Attention);
    if(attentionMind.Attention==KittyAttention.Cursor) cursorTicks++; else otherTicks++;
}
Check(otherTicks>cursorTicks,"A stationary cursor monopolizes attention.");
Check(attentionMind.Habituation>.7,"Repeated unchanging cursor presence did not habituate.");
Check(focusModes.Contains(KittyAttention.Environment)&&focusModes.Count>=3,"The kitty did not have multiple autonomous attention targets.");

var wave=new KittyMind(42); var waveActions=new HashSet<KittyAction>();
for(var i=0;i<400;i++) { wave.Tick(.125,new(1,1,0,0,1)); waveActions.Add(wave.Action); }
Check(waveActions.Contains(KittyAction.Zoomies),"Near-face waving did not produce zoomies.");
Check(wave.Energy<.8,"Vigorous activity did not create fatigue.");
for(var i=0;i<800;i++) wave.Tick(.125,default);
Check(wave.Stimulation<.02 && wave.Action!=KittyAction.Zoomies,"The kitty could not settle after stimulation stopped.");
Check(wave.InferenceCount>wave.DecisionCount&&wave.InferenceCount<650,"The recurrent policy is not running at a bounded independent thinking cadence.");

var annoyed=new KittyMind(7); var grumpy=false;
for(var i=0;i<14;i++) { annoyed.Pester(); annoyed.Tick(.1,new(1,0,0,0,1)); grumpy|=annoyed.Action==KittyAction.Grumpy; }
Check(annoyed.Irritation>.7&&annoyed.Trust<.6,"Repeated pestering did not affect irritation and trust.");
for(var i=0;i<80;i++) { annoyed.Tick(.125,new(1,0,0)); grumpy|=annoyed.Action==KittyAction.Grumpy; }
Check(grumpy,"The pestered kitty never displayed annoyance.");
for(var i=0;i<480;i++) annoyed.Tick(.125,default);
Check(annoyed.Irritation<.01,"Irritation did not recover after giving the kitty space.");

var home=new KittyMind(21); var homeActions=new HashSet<KittyAction>(); var transitions=new HashSet<(KittyAction,KittyAction)>();
var last=home.Action;
for(var i=0;i<8000;i++) {
    home.Tick(.125,default); homeActions.Add(home.Action);
    if(home.Action!=last) { transitions.Add((last,home.Action)); last=home.Action; }
}
Check(homeActions.Contains(KittyAction.Groom)&&homeActions.Contains(KittyAction.Sleep),"A quiet house kitty failed to groom and nap.");
Check(homeActions.Contains(KittyAction.Knead)&&homeActions.Contains(KittyAction.Loaf),"The quiet kitty never prepared and used a resting pose.");
Check(homeActions.Contains(KittyAction.Yawn)&&homeActions.Contains(KittyAction.Stretch),"The kitty never yawned or stretched after resting.");
Check(homeActions.Contains(KittyAction.SlowBlink)&&homeActions.Contains(KittyAction.LookAround),"Relaxed blinking and independent observation are absent.");
Check(!homeActions.Any(x=>x is KittyAction.Chase or KittyAction.Swat or KittyAction.Stalk or KittyAction.Pounce or KittyAction.SmackPanel or KittyAction.Scratch),
    "The kitty targeted a nonexistent cursor or surface.");
Check(transitions.Contains((KittyAction.Sleep,KittyAction.Stretch)),"The nap-to-wake stretch sequence was not observed.");
Check(transitions.Contains((KittyAction.Circle,KittyAction.Knead))||transitions.Contains((KittyAction.Knead,KittyAction.Loaf)),
    "The settle-to-rest sequence was not observed.");

var hunting=new KittyMind(12); var hunted=new HashSet<KittyAction>();
var huntedTransitions=new HashSet<(KittyAction,KittyAction)>(); last=hunting.Action;
for(var i=0;i<3200;i++) {
    var phase=i%200;
    hunting.Tick(.125,phase<70 ? new(.85,.55,1,phase<4?1:0,1) : new(.4,0,1,0,1));
    hunted.Add(hunting.Action);
    if(hunting.Action!=last) { huntedTransitions.Add((last,hunting.Action)); last=hunting.Action; }
}
Check(hunted.Contains(KittyAction.Stalk)&&hunted.Contains(KittyAction.Pounce),"Predatory play did not include stalking and pouncing: "+string.Join(", ",hunted));
Check(huntedTransitions.Contains((KittyAction.Stalk,KittyAction.ButtWiggle))
    && huntedTransitions.Contains((KittyAction.ButtWiggle,KittyAction.Pounce)),"Stalk, aim and pounce episode continuity is absent.");
Check(hunted.Contains(KittyAction.Sniff)||hunted.Contains(KittyAction.Inspect),"Novel surfaces never triggered investigation.");
var vocalActions=new HashSet<KittyAction>();
for(var seed=0;seed<12;seed++) {
    var social=new KittyMind(seed);
    for(var i=0;i<2400;i++) social.Tick(.125,default);
    social.Pester();
    for(var i=0;i<240;i++) { social.Tick(.125,new(.6,.2,0,0,1)); vocalActions.Add(social.Action); }
}
Check(vocalActions.Contains(KittyAction.Chirp)||vocalActions.Contains(KittyAction.Meow),"Social/play contexts never selected a vocal behavior.");

Check(home.CompletedRoutineCount>0,"Autonomous home routines never finish.");
Check(homeActions.Contains(KittyAction.PawLick)&&homeActions.Contains(KittyAction.FaceWash),
    "Grooming never unfolds into distinct paw and face washing stages.");
Check(homeActions.Contains(KittyAction.Doze)&&homeActions.Contains(KittyAction.EarFlick),
    "Quiet home behavior lacks light dozing and small attention reactions.");

KittyMind FindHunt(int seed)
{
    var mind=new KittyMind(seed);
    for(var i=0;i<6000;i++)
    {
        var phase=i%200;
        mind.Tick(.125,phase<70 ? new(.85,.55,1,phase<4?1:0,1) : new(.4,0,1,0,1));
        if(mind.Routine==KittyRoutine.Hunt&&mind.Action==KittyAction.Stalk&&mind.ActionAge<.2) return mind;
    }
    throw new Exception("The neural behavior selector never started a hunt episode.");
}
var interruptedHunt=FindHunt(23);
Check(interruptedHunt.Action==KittyAction.Stalk&&interruptedHunt.RoutineLength>=5,
    "A hunt episode does not start with a committed multi-stage stalking goal.");
var interruptions=interruptedHunt.InterruptedRoutineCount;
for(var i=0;i<14;i++) { interruptedHunt.Pester(); interruptedHunt.Tick(.1,new(1,0,1,0,1)); }
for(var i=0;i<30&&interruptedHunt.Routine!=KittyRoutine.Recovery;i++) interruptedHunt.Tick(.125,new(1,0,1,0,1));
Check(interruptedHunt.Routine==KittyRoutine.Recovery&&interruptedHunt.InterruptedRoutineCount>interruptions,
    "Repeated pestering did not interrupt the current goal and start a space-seeking recovery episode.");
var recoveryActions=new HashSet<KittyAction>();
for(var i=0;i<100;i++) { recoveryActions.Add(interruptedHunt.Action); interruptedHunt.Tick(.125,default); }
Check(recoveryActions.Contains(KittyAction.Backoff)&&recoveryActions.Contains(KittyAction.Peek),
    "A startled kitty did not back away and cautiously look again.");

var missingPrey=FindHunt(12); var lostCount=missingPrey.InterruptedRoutineCount;
for(var i=0;i<60;i++) missingPrey.Tick(.125,default);
Check(missingPrey.InterruptedRoutineCount>lostCount&&missingPrey.Routine!=KittyRoutine.Hunt,
    "A hunt stayed committed to a cursor that disappeared.");
Check(missingPrey.Action!=KittyAction.Pounce&&missingPrey.Action!=KittyAction.ButtWiggle,
    "The kitty still aims or pounces at absent prey.");

var napper=new KittyMind(21);
for(var i=0;i<8000 && !(napper.Action is KittyAction.Sleep or KittyAction.Doze && napper.ActionAge>.5);i++) napper.Tick(.125,default);
Check(napper.Action is KittyAction.Sleep or KittyAction.Doze,"The kitty never entered a wakeable nap.");
napper.Pester(); napper.Tick(.125,new(.8,0,0,0,1));
Check(napper.Action is not (KittyAction.Sleep or KittyAction.Doze),"A gentle interaction did not wake a light or deep nap.");
Check(napper.Willingness is >=0 and <=1&&napper.Frustration is >=0 and <=1,
    "Motivation or frustration is not a bounded simulation state.");
Check(!string.IsNullOrWhiteSpace(napper.EpisodeDescription),"The current episode has no readable goal description.");

var one=new KittyMind(100); var two=new KittyMind(100);
for(var i=0;i<1200;i++) {
    var senses=new KittyStimulus(i%90<20?1:0,i%75<10?1:0,i%40<20?1:0,i%130<2?1:0,1);
    if(i%111==0) { one.Pester(); two.Pester(); }
    one.Tick(.125,senses); two.Tick(.125,senses);
    Check(one.Action==two.Action&&one.ActionAge==two.ActionAge&&one.Attention==two.Attention&&one.Energy==two.Energy
        &&one.Routine==two.Routine&&one.RoutineStage==two.RoutineStage&&one.Frustration==two.Frustration,
        "A seeded cognitive simulation was not reproducible.");
    Check(double.IsFinite(one.MemoryActivity)&&one.MemoryActivity>=0&&one.MemoryActivity<=1,"Recurrent memory became unbounded.");
}
var age=one.ActionAge;
one.Tick(double.NaN,new(double.NaN,double.PositiveInfinity,-5));
Check(one.ActionAge==age,"Invalid elapsed time changed cognition.");
one.Tick(3600,new(double.NaN,double.PositiveInfinity,-5));
Check(one.ActionAge<=.5||one.ActionAge-age<=.5,"Hidden time was replayed as a huge jump.");
Check(!string.IsNullOrWhiteSpace(one.Intention)&&one.Thought==one.Intention,"Cognitive intention summary is absent.");

Span<double> output=stackalloc double[KittyBrain.ActionCount];
var perf=new KittyBrain(); for(var i=0;i<1000;i++) perf.Predict(calm,output);
var timer=Stopwatch.StartNew();
for(var i=0;i<100000;i++) perf.Predict(calm,output);
timer.Stop();
Check(timer.Elapsed<TimeSpan.FromSeconds(8),"Recurrent inference exceeded the lightweight budget.");
Console.WriteLine($"PASS {checks} cognitive kitty checks; {KittyBrain.ParameterCount} trained parameters / {KittyBrain.ParameterCount*8:N0} numeric bytes; 100k recurrent inferences {timer.Elapsed.TotalMilliseconds:F1}ms ({timer.Elapsed.TotalMicroseconds/100000:F2}us each).");
