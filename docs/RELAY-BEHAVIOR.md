# Relay's behavior design

Relay is an original software character with simulated needs, short-term memory,
attention and goals. Its small custom recurrent neural policy is trained on original
synthetic episodes. It is not an LLM and does not understand language. It is a playful interpretation of cat behavior, not a scientific model
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

The 35 behaviors are grouped into interruptible episodes: hunting, settling,
recovery, grooming, exploration and play. Frustration, willingness, recent-action
history and learned activity preferences influence which goal is continued,
abandoned or chosen next. These are software design choices, not claims of real
feline consciousness.

The local 1.4.0 preview adds a physical affordance controller alongside that
unchanged 35-behavior model. The controller finds usable top edges on visible
app panels, plans a climb, places Relay's paws on an edge and lets it perch
there. It refreshes the usable surfaces when the page, scroll position or window
size changes. These physical interaction poses are separate from the recurrent
model's learned action labels.

Pressing and dragging Relay's drawn body picks it up. Its legs and tail dangle,
while head and ear motion respond to the drag. Release sets it down, with a
nearby valid ledge catching a drop and a soft landing pose easing it back into
its next activity. The geometry controller handles placement and bounds; the
neural policy continues to provide simulated behavioral context. None of these
interactions activate real controls or move the system cursor.

The articulated renderer uses independently damped body segments, head, ears,
limbs and tail links. Pose weights, paws, head posture, gaze and facial states
blend smoothly when an activity changes or is interrupted. Relay makes no sound:
meows, chirps, yawns and purrs appear only as brief, click-through text bubbles.
The audio runtime and sound preference from earlier previews have been removed.
The kitty starts off at every app launch. Enabling it is a session-only choice;
it pauses in the tray and clears active interactions when hidden.

Separately, the tracking-connection background smoothly slows its particles to
a stop and dims them when the bridge stops. Restarting the bridge brightens the
particles and eases them back into motion. Windows reduced-motion preferences
still apply.

Training and numerical tests are documented in [the neural-policy tests](../tests/Kitty.Tests/README.md).
Physical placement and pickup geometry checks are documented in [the panel tests](../tests/Kitty.Panel.Tests/README.md).
