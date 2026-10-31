# Local recurrent kitty cognition checks

Run `dotnet run --project tests/Kitty.Tests -c Release`.

Relay uses 22 sensory/need/context inputs, 24 recurrent tanh memory units, and
two further tanh layers (32 and 24 units). Separate softmax heads choose among
35 behavior scores and five attention modes: cursor, environment, self, rest,
and remembered interests. Its 3,720 original trained parameters occupy 29,760
numeric bytes. The recurrent weights are learned through backpropagation
through time.

This is a small simulation of feline behavior, attention and short-term memory;
it does not have conscious thoughts, understand language, or run an LLM.
It is imitation learning over original synthetic temporal episodes. The app
never downloads a model, records mouse interactions, trains on user behavior,
or sends interaction data anywhere.

`tools/train_kitty_policy.py` reproduces the committed `KittyModel.cs` weights
using NumPy for development only, with fixed seed 20261031. The training set has
2,400 original episodes of 18 time steps; 500 held-out episodes provide 9,000
validation time steps. The teacher includes context traces hidden from the
network's current input, requiring it to learn useful temporal state. Reported
held-out agreement is 97.79% for behavior and 99.13% for attention. These numbers
measure imitation of synthetic examples, not intelligence or real-cat fidelity.

Needs and explicit short-term traces retain play, rest, novelty and contact.
Trust changes with gentle contact versus pestering; habituation reduces the
importance of an unchanging cursor. Fatigue can override continued stimulation.
The network updates on a 250 ms context cadence and reconsiders direct contact,
while action choices and attention commitments last several seconds. Physical
eligibility, finite goal episodes and cooldowns turn action preferences into
coherent activities. The public routine/stage describes the current goal:

- Hunt: swat or chase → stalk → aim with a wiggle → pounce → inspect → wash a paw.
- Settle: circle → knead → loaf → doze → nap → stretch → yawn.
- Recovery: startle → back off → show annoyance → peek → sniff → relax.
- Grooming: general grooming → paw lick → face washing.
- Exploration: inspect → sniff → rub a familiar surface → look around → ear flick.
- Play: energetic play → tail chase or rolling → flop → wash or groom.

Activities check their prerequisites at every stage. Lost prey, fatigue, renewed
stimulation or repeated pestering can interrupt a goal. Recovery seeks space and
then reassesses; a light or deep nap can wake after direct interaction. The
completed/interrupted counts expose finite progress for diagnostics. Recent
action penalties and bounded same-session activity preferences reduce repeated
poses. These scalar preferences are simulated motivation, not online neural
training, and are not recorded or persisted.
Intention summaries describe the actual current simulated goal; they are not
generated reasoning transcripts. All original model, training code and synthetic
examples are covered by the project's Apache 2.0 license.

Behavioral grounding: [AAFP/ISFM environmental-needs guidelines](https://journals.sagepub.com/doi/full/10.1177/1098612x13477537),
[AAHA feline environmental/play guidance](https://www.aaha.org/resources/2021-aaha-aafp-feline-life-stage-guidelines/behavior-and-environmental-needs-kittens/),
and [the primary slow-blink communication experiment](https://www.nature.com/articles/s41598-020-73426-0).
These support broad activity motifs; the sources do not supply training data or
validate this software's model as a scientific simulation.

Checks cover recurrent history dependence and reset, different attention targets,
habituation, waving and pestering, fatigue/recovery, house-cat rest/wake and hunt
sequences, interrupted goals, absent prey, nap waking, text-expression contexts,
invalid numeric input, deterministic
seeded simulations, model size and a broad inference-time budget.
