# Relay's behavior design

Relay is an original software character with simulated needs, short-term memory,
attention and goals. Its recurrent neural policy is trained on original synthetic
episodes. It is a playful interpretation of cat behavior, not a scientific model
of feline consciousness. There is no language service, camera, microphone,
interaction recording or cloud inference.

The behavior vocabulary draws on these sources:

- [AAFP/ISFM Feline Environmental Needs Guidelines](https://journals.sagepub.com/doi/full/10.1177/1098612x13477537): opportunities for predatory play, exploration, scratching, familiar resting places, scent-related investigation and varied stimuli. Relay uses stalking/pouncing sequences, sniffing and rubbing panels, and returning to a comfortable spot.
- [AAHA kitten behavior guidance](https://www.aaha.org/resources/2021-aaha-aafp-feline-life-stage-guidelines/behavior-and-environmental-needs-kittens/): watching, swatting, chasing, pouncing and catching inspired connected play sequences.
- [Humphrey et al., 2020, Scientific Reports](https://www.nature.com/articles/s41598-020-73426-0): experiments on slow-blink interactions informed Relay's relaxed slow-blink animation. The study does not validate this digital model.
- [AAHA on kneading](https://www.aaha.org/resources/is-my-cats-kneading-normal/): alternating paw motions inspired kneading before loafing and resting.

Sequence timing, trust, habituation, curiosity and neural state are software design
choices. They let Relay become less interested in a familiar cursor, attend to
the surroundings or its own paws, remember an interesting spot, and finish an
activity before choosing another. Intent descriptions report those internal
goals; they are not generated claims of conscious thought.

The articulated renderer uses independently damped body segments, head, ears,
limbs and tail links. The original synthesized robot-cat meows, chirps, yawns and
purrs use no recordings. Sound can be muted in Settings and is stopped while the
cat or window is hidden. Neither play nor curiosity activates real app controls.

Training and numerical tests are documented in [the neural-policy tests](../tests/Kitty.Tests/README.md).
