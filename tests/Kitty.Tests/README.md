# Local recurrent kitty cognition checks

Run `dotnet run --project tests/Kitty.Tests -c Release`.

Relay uses 22 sensory/need/context inputs, 24 recurrent tanh memory units, and
two further tanh layers (32 and 24 units). Separate softmax heads choose among
25 behavior scores and five attention modes: cursor, environment, self, rest,
and remembered interests. Its 3,470 original trained parameters occupy 27,760
numeric bytes. The recurrent weights are learned through backpropagation
through time, rather than an untrained random reservoir.

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
held-out agreement is 96.78% for behavior and 98.64% for attention. These numbers
measure imitation of synthetic examples, not intelligence or real-cat fidelity.

Needs and explicit short-term traces retain play, rest, novelty and contact.
Trust changes with gentle contact versus pestering; habituation reduces the
importance of an unchanging cursor. Fatigue can override continued stimulation.
The network updates at up to four context steps per simulated second, while
action choices and attention commitments last several seconds. Cooldowns,
physical eligibility and finite episode priors support sequences such as
stalk → pounce, circle → knead → loaf/nap, and nap → stretch → yawn.
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
sequences, absent targets, vocal contexts, invalid numeric input, deterministic
seeded simulations, model size and a broad inference-time budget.
