# Battle skill flipbook implementation plan

**Goal:** Connect the approved fire/shrapnel sprite sequences to resolved combat hits and deliver the complete code to master, without committing PNGs.

**Architecture:** BattleEngine exposes only facts about triggered Berserk/Shock. A BattleScreenUI partial retains existing cards during presentation, draws primary/collateral damage labels, and gates counterattacks and subsequent Explosion challenges. A uGUI graphic samples the approved packed atlases with a premultiplied adjacent-frame blend shader. No gameplay RNG, balance changes or new rendering package.

**Spec:** Owner's conversation: Berserk reuses Drill; shield on actual Bodyguard; Shock impulse on skill carrier; Scorcher rolls low and engulfs longer; Splash has only the original blast. Latest approved external atlases: fire 19 frames/1.38 s, Splash 15/1.08 s, incoming collateral damage at .48 s. PNGs are already saved by owner.

**Constraints:** Unity 6000.5.4f1; follow existing catalog loader; preserve local image GUIDs. Auto-configure/register externally imported PNGs in the editor. No PNG writes to Git. Commit/merge authorized; native Unity is unavailable here.

## Tasks

- [x] 1. Snapshot base; add failing timeline/skill-fact tests. Implement exact per-frame timing, fade/blend, triggered facts and damage formatting. Run portable compile/tests against fixed base.
- [x] 2. Implement texture-backed UI mesh + shader, overlays and attack sequencing. Cover lethal retention, input block, teardown and Explosion continuation with native tests. Preserve existing result popups and fast/nonhuman flow.
- [x] 3. Register images and shader through the existing catalog; ensure imported atlases use Clamp, no mipmaps, uncompressed RGBA, and correct dimensions. Add narrowly scoped editor import setup so existing image GUIDs are retained. Verify metadata, test differential and review complete diff. Integration to master is authorized and follows verification.

## Review focus

- Bodyguard's actual victim cell is the origin; lethal cards stay visible after authoritative removal.
- Shock is shown for actual queued-turn/counter suppression, not for a miss or dead victim.
- Two Splash neighbours share one initial burst; zero-damage collateral still shows BLOCK.
- Horizontal fire mirrors without rotating tongues; vertical fire uses the upright crown on source/target, never a rotated horizontal wave.
- Canvas scale/layout changes preserve endpoints. Missing textures/targets and cancellation never lock input or invoke stale callbacks.
- Approved v2 PNGs are validated against 4x5/4x4 grids; auto setup keeps pre-existing .meta GUIDs, and the build retains shader/textures via direct catalog references.

## Progress

- Fresh isolated clone on feature/battle-skill-flipbooks from e47aca20ca3842641416920cdce340b03cac307f. The earlier local commits were not published; this branch carries the full final feature rather than only the visual follow-up.
- Base assets copied before changes. Full stub compile has the known 12 declaration-level Editor/TMP errors. Runtime-specific adapters stay outside the repo. Native Unity validation remains outstanding.
- Final portable C# compile: 625 runtime/test/importer source files; success using direct Roslyn after the host's MSBuild ProcessName lookup stopped working. No verification adapters were added to game code.
- Fixed-base test comparison: 309 total / 238 passed / 71 failed → 325 total / 254 passed / the identical 71 failed. All 16 added portable cases passed; no regressions. The failures require native Unity/reference APIs unavailable in this environment.
- Independent pre-merge review completed. Fixed shared Splash/Scorcher source-pulse ownership and added a native mixed-skill regression. Lifecycle tests enter Play Mode so teardown/RefreshGrid may use normal Object.Destroy. No remaining confirmed Critical/Important findings; native execution remains pending.
- Native Unity build, shader rendering and lifecycle tests were not run. Installation paths and the editor verification checklist are in docs/battle-skill-effects.md. No scenes, prefabs, catalog YAML or PNGs were manually edited.
