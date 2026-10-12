# Heal and Transport

Approved behavior (2026-10-12):

- Heal restores one HP to a random living wounded Bio member of the same battle army, capped at maximum HP. Self-healing and battle summons are eligible.
- A regular unit activates at the start of its own combat turn. Every living hero with Heal activates at the start of each combat round, regardless of commander status. A skipped unit turn does not activate Heal; a retreating side does not activate it.
- Regeneration remains the separate strategic end-of-turn self-heal.
- Transport is persistent, not a turn-start proc. Each carrier supports one slower non-hero, non-aviation Infantry in its own army, up to the carrier's permanent movement maximum (card plus attachments). It never lowers speed or chains temporary bonuses.
- Keep a valid pair through reorder and turn changes. If either member leaves, dies, becomes a prisoner, or ceases to qualify, remove that pair and immediately assign an eligible replacement. Each carrier has at most one recipient; recipients do not stack carriers.
- Selection uses a seeded pseudorandom hash of stable runtime identities, so hypothetical roster reads and the corresponding committed roster agree without changing the game's random stream. It is stable between roster changes.
- Spent movement belongs to the unit. Losing, regaining, or transferring support never refunds it. Effective army movement continues to use the slowest member.

## Implementation boundaries

1. `UnitAbilities`, ability catalog, GameConfig abbreviations and authored faction cards.
2. `BattleEngine` owns live healing; shared `BattleSimulationKernel` owns eligibility selection and HP arithmetic. No gameplay mutation in UI refresh.
3. `BattleAi` shadow HP and `WorthIt` roster simulations include the same ticks. Body-only commander snapshots carry passive healers, including secondary heroes; exact cache keys and combat viability fingerprints include them.
4. `UnitData` keeps native speed separate from a transport pair and tracks spending across maximum changes. `ArmyData.Transport` owns stable assignment and read-only projected rosters. Transfer, deployment, death, imprisonment, attachment, registry and rollback boundaries refresh affected pairs.
5. Existing movement consumers read effective `MoveMax`/`MoveCurrent`; attachment definitions and permanent projections explicitly read `PermanentMoveMax`. Housekeeping projections use native speed/spending and the shared domain rule, so a donor's transport bonus cannot follow a passenger by itself.
6. Strategic effect registry prices eligible Bio healing and actual army mobility gain. Campaign's existing heuristic accounts for support options. Resource spending and reservation ownership stay with their existing owners.
7. `BattleHealEffect` overlays the real healed unit card, with unscaled elapsed time retained by the encounter across grid rebuilds: 0.5 s smooth fade-in, 2 s at alpha 0.8, 0.5 s smooth fade-out. It does not block combat or input.

## Authored cards

- Iron Concord `Dust Medic`: Heal.
- Iron Concord hero `Kessa Vrail`: Heal.
- The Ashen `Scav Carrier`: Transport, retaining RaiseTheRots.
- The Vessels `Relay Carrier`: Transport, retaining r1s5.

## Visual asset

`Assets/Resources/UI/Effects/HealCross.png` is a generated transparent RGBA sprite. Unity imports it as one UI sprite, without mipmaps or compressed alpha. Existing asset GUIDs are preserved.

Built-in imagegen prompt:

> One small, readable, luminous green medical plus cross, centered, straight-on, with four equal rectangular arms. A transparent game UI sprite for a dark post-apocalyptic strategy game with battered steel UI and restrained amber metal accents. Emerald/lime green enamel inside thin gunmetal beveled edges, subtle abrasion and scratches, faint green emissive halo. Bold geometry readable at 48–80 pixels. One cross only; no badge, backing plate, circle, card, scene, text, watermark or painted checkerboard. Orthographic front view, approximately 75% of a square canvas with transparent padding and real alpha around the glow.

## Validation

- New EditMode tests cover hero/unit timing, eligibility, self-heal, passive-healer forecast/equality, fade timing, persistent assignment, distinct carriers, transfer/death/replacement, spent movement, permanent attachments, pure projection, and donor-support removal in Housekeeping.
- Portable verification uses the repository's Unity reference packages and test stubs, via direct Roslyn invocation because the hosted process namespace blocks the .NET CLI/MSBuild process introspection. Compatibility substitutions are confined to scratch copies, not game code.
- Compare all runnable pre-existing tests with the untouched connector snapshot, rather than interpreting Unity-native failures as game failures.
- Both Unity YAML checks apply to the edited assets; YAML parsing, sprite alpha and whitespace checks are also required.
- Final editor checks remain: Unity 6000.5.4f1 full EditMode suite, actual hero/unit Heal in normal and Fast Resolve battles, the cross on rebuilt/moving cards, and human/AI Transport transfer/death/attachment scenarios. No editor build or scene playthrough is claimed by portable checks.

## Review continuation (2026-10-12)

- The original prepared worktree was retained. Review uses current master code at `34e4df4c0c9821860a00ac9e6da6e644b7c0415e`, preserving both the recent faction-name cleanup and removal of Resistance. The task changes do not reintroduce that stat.
- Complete roster creation for neutral armies and event guards now refreshes Transport; releasing a prisoner refreshes the receiving garrison. These changes use the existing domain refresh owner.
- Human fog snapshots freeze the movement they actually observed. They do not apply Transport a second time to members whose copied speed already includes the aura.
- Six extra portable tests cover two healing heroes, retreat suppression, no Transport chains, pair retention, reassignment after a native speed upgrade, and frozen fog-memory movement.
- Validation: 24/24 new portable tests and 1534/1534 pre-existing portable cases pass against the untouched current-master code baseline. All 1365 previously passing cases were explicitly accounted for. Named NUnit TestCase aliases were run through a separate name filter because the test-list selector omits them.
- Both portable test assemblies compile. The raw Unity-reference runtime check retains the same four baseline diagnostic categories and adds none. Compatibility substitutions remain confined to disposable verification copies.
- Both Unity YAML validators, YAML parsing, RGBA/alpha inspection and `git diff --check` pass. Detailed machine-readable results are in `docs/heal-transport-verification-summary.json`.
- The full Unity 6000.5.4f1 EditMode suite and scene playthroughs remain required: normal/Fast Resolve Heal timing, the three-second cross across card rebuilds, human/AI transfer and death, attachments and movement rollback. Portable checks do not replace these.
- Scope is a gameplay behavior change with supporting domain, forecast and presentation integration. Resource allocation, bank reservations and mission ownership still use their existing owners; no temporary verification stubs enter game code.

## Publication asset handoff (2026-10-12)

- Per the owner's request, `Assets/Resources/UI/Effects/HealCross.png` is delivered separately and excluded from this Git publication. Its existing `.meta` is included to preserve the Unity GUID and sprite import settings.
- Add the supplied PNG at exactly that path without replacing its `.meta`. Until it is added, Heal gameplay remains functional but the cross overlay is intentionally skipped by the missing-sprite guard.
- The portable and sprite validation results above refer to the complete local review copy, including the separately delivered PNG. Unity editor verification is still outstanding.
