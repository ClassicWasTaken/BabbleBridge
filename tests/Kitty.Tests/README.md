# Local kitty policy checks

Run `dotnet run --project tests/Kitty.Tests -c Release`.

The kitty uses a 12-input, 16-hidden-unit tanh neural network with 10 softmax
behavior outputs. Its 378 original parameters occupy 3,024 numeric bytes. It is
imitation learning over original synthetic kitty scenarios, not an LLM, remote
assistant, copied model, or a model trained on user behavior. The app never
downloads a model or sends interaction data anywhere.

`tools/train_kitty_policy.py` reproduces the committed `KittyModel.cs` weights
using NumPy for development only, with fixed seed 20261031. Its original utility
teacher covers quiet observation, cursor play, panel play, vigorous stimulation,
irritation, grooming, fatigue and rest. The held-out top-action agreement is
recorded in the generated file; this measures agreement with synthetic examples,
not intelligence or realism. Runtime needs and cooldowns gate impossible actions
and keep animations finite. All original model, training code and synthetic
examples are covered by this project's Apache 2.0 license.

Checks cover response to waving and repeated pestering, fatigue and recovery,
grooming/sleeping without input, absent targets, invalid numeric inputs,
deterministic seeded simulations, model size and a broad inference-time budget.
