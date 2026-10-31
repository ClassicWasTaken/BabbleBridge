// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using BabbleFrameBridge.App;

var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
var brain = new KittyBrain();
KittyAction Best(KittyObservation observation)
{
    var probabilities = brain.Predict(observation);
    return (KittyAction)Array.IndexOf(probabilities, probabilities.Max());
}
Check(KittyModel.InputWeights.Length+KittyModel.HiddenBias.Length+KittyModel.OutputWeights.Length+KittyModel.OutputBias.Length
    == KittyBrain.ParameterCount, "The documented tiny policy size changed.");
Check(Best(new(.85,.08,0,0,0,0,0,0,0,.55,0,.25)) == KittyAction.Watch, "A calm new kitty should watch.");
Check(Best(new(.85,.08,0,.95,1,1,.1,0,0,.55,.5,.25)) == KittyAction.Zoomies, "Strong face-near waving should favor zoomies.");
Check(Best(new(.8,.1,.95,.7,1,0,1,0,0,.4,.5,.25)) == KittyAction.Grumpy, "Repeated irritation should favor a grumpy reaction.");
Check(Best(new(.25,.85,0,0,0,0,0,1,0,.55,.5,.1)) == KittyAction.Sleep, "A sleepy quiet kitty should rest.");
Check(Best(new(.8,.2,0,0,0,0,0,1,0,.75,.5,.1)) == KittyAction.Groom, "A safe quiet kitty should groom.");
Check(Best(new(.85,.08,0,.1,0,0,0,.2,1,.55,.5,.7)) == KittyAction.SmackPanel, "A nearby panel should be a plausible play target.");

var adversarial = new[] {
    new KittyObservation(double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1,99,0,0,0,0,0,0,0),
    new KittyObservation(1,1,1,1,1,1,1,1,1,1,1,1), default };
foreach (var observation in adversarial)
{
    var values = brain.Predict(observation);
    Check(values.All(value => double.IsFinite(value) && value >= 0 && value <= 1), "Policy probabilities must stay finite and bounded.");
    Check(Math.Abs(values.Sum()-1) < 1e-12, "Policy probabilities must sum to one.");
}

var waveMind = new KittyMind(42);
var waveActions = new HashSet<KittyAction>();
for (var i=0; i<160; i++) { waveMind.Tick(.125,new(1,1,0)); waveActions.Add(waveMind.Action); }
Check(waveActions.Contains(KittyAction.Zoomies), "Waving close to the kitty never stimulated zoomies.");
Check(waveMind.Energy < .8, "Energetic play did not produce fatigue.");
for (var i=0; i<480; i++) waveMind.Tick(.125,default);
Check(waveMind.Stimulation < .01 && waveMind.Action != KittyAction.Zoomies, "The kitty stayed in zoomies after stimulation stopped.");
Check(waveMind.DecisionCount < 100, "Neural decisions are being made at rendering-frame frequency.");

var annoyed = new KittyMind(7);
var grumpy = false;
for (var i=0; i<14; i++) { annoyed.Pester(); annoyed.Tick(.1,new(1,0,0)); grumpy |= annoyed.Action==KittyAction.Grumpy; }
Check(annoyed.Irritation > .7, "Rapid pestering did not build annoyance.");
for (var i=0; i<60; i++) { annoyed.Tick(.125,new(1,0,0)); grumpy |= annoyed.Action==KittyAction.Grumpy; }
Check(grumpy, "A repeatedly pestered kitty did not show a grumpy reaction.");
for (var i=0; i<360; i++) annoyed.Tick(.125,default);
Check(annoyed.Irritation < .01, "Annoyance never recovered after leaving the kitty alone.");

var resting = new KittyMind(21);
var restActions = new HashSet<KittyAction>();
for (var i=0; i<2400; i++) { resting.Tick(.125,default); restActions.Add(resting.Action); }
Check(restActions.Contains(KittyAction.Groom) && restActions.Contains(KittyAction.Sleep), "A quiet kitty did not groom and sleep.");
Check(!restActions.Contains(KittyAction.Chase) && !restActions.Contains(KittyAction.Swat) && !restActions.Contains(KittyAction.SmackPanel),
    "The kitty targeted a nonexistent cursor or panel.");
var first = new KittyMind(100); var second = new KittyMind(100);
for (var i=0; i<1000; i++)
{
    var input = new KittyStimulus(i%90 < 20 ? 1 : 0,i%75 < 10 ? 1 : 0,i%40<20 ? 1 : 0);
    if(i%111==0) { first.Pester(); second.Pester(); }
    first.Tick(.125,input); second.Tick(.125,input);
    Check(first.Action==second.Action && first.ActionAge==second.ActionAge && first.Energy==second.Energy, "A seeded simulation was not reproducible.");
}
var age = first.ActionAge;
first.Tick(double.NaN,new(double.NaN,double.PositiveInfinity,-5));
Check(first.ActionAge==age, "An invalid elapsed time changed the simulation.");
first.Tick(3600,new(double.NaN,double.PositiveInfinity,-5));
Check(first.ActionAge <= .5 || first.ActionAge-age <= .5, "Hidden elapsed time was replayed as a huge jump.");

Span<double> output = stackalloc double[10];
var sample = new KittyObservation(.8,.1,.2,.3,.5,.4,.1,.2,1,.5,.3,.4);
for (var i=0; i<1000; i++) brain.Predict(sample,output);
var timer = Stopwatch.StartNew();
for (var i=0; i<100000; i++) brain.Predict(sample,output);
timer.Stop();
Check(timer.Elapsed < TimeSpan.FromSeconds(8), "Tiny neural inference exceeded its lightweight performance budget.");
Console.WriteLine($"PASS {passed} kitty checks; 378 parameters / 3,024 numeric bytes; 100,000 inferences in {timer.Elapsed.TotalMilliseconds:F1} ms ({timer.Elapsed.TotalMicroseconds/100000:F2} µs each).");
