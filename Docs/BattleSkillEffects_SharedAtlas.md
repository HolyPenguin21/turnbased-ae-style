# Rustmarch — shared battle-skill VFX atlas

The single binary file **Assets/Textures/UI/Effects/Effects.png** is supplied by the
owner and committed together with **Effects.png.meta**; keep the meta's GUID stable
so the catalog reference resolves.

- RGBA image: **1536 × 1024 px**.
- Eight columns (192 px each), five rows from the **top** in this order:
  1. Heal
  2. Drill
  3. Berserk
  4. Bodyguard
  5. Shock Attack
- Row boundaries measured from the top: **0, 205, 410, 614, 819, 1024**.
  Source height is not divisible by five: row 3 is 204 px high, others 205.
- Playback is 8 frames / 0.88 s on `Time.unscaledTime`, with no combat delay.
  Frames are rendered at a stationary card pivot by `BattleSkillEffect`.
- `GameAssetCatalog` resolves key `UI/Effects/Effects` to the Texture2D.
  After placing the PNG, Unity imports using the committed `.meta` file.
- `Assets/Textures/UI/Effects/Flipbooks/`, `BattleDamageFlipbook`,
  `BattleSkillFlipbook`, and their dedicated shader are removed.
- **Splash and Scorcher combat mechanics are unchanged**: secondary damage,
  associated Berserk reactions, results, and hit-order display remain.
  Their obsolete visual flipbook implementation is deleted.
- The four legacy still-image textures (`HealCross`, `DrillFist`, `BodyguardShield`,
  `ShockImpulse`) are deleted together with their catalog entries.

QA: in the Unity Editor, verify each skill plays the correct row, none of the
eight frames bleeds into an adjacent row, and no input/turn advancement waits
for the animation. `BattleSkillEffectAtlasTests` validates the frame geometry.
