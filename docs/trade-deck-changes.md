# Trading Post — AI deck budget update

Base: c9316568d3d36273ad320f08de80365172ddb6e6. Trading Post now costs **1 deck point** (previously 5), with one copy in every one of the 24 authored AI decks. Main decks are exactly 100 points; equipment and mutator blueprints retain their separate budget rules.

All pre-existing cards, heroes, leaders, scouts, facilities and blueprints are retained. Each deck gains 4 points of cheap ground units costing 1 or 2 points per card. Existing unit types are preferred; Watcher Drone is added where existing cheap ground types cannot fill the gap within copy limits. No collectors or aircraft are added.

| AI deck | Added units | Points | H / E / M / T |
| --- | --- | ---: | --- |
| Aldric Voss | Watcher Drone ×4 | 100 | 27 / 34 / 32 / 35 |
| Kessa Vrail | Dust Medic ×1, Scout ×1 | 100 | 32 / 23 / 27 / 23 |
| Vector Vance | Light Infantry ×2, Scout ×1 | 100 | 36 / 24 / 22 / 21 |
| Dorian Kesh | Light Infantry ×2, Scout ×1 | 100 | 29 / 31 / 34 / 25 |
| Bolt Jax | Light Infantry ×2, Scout ×1 | 100 | 29 / 33 / 40 / 26 |
| Nadia Thorne | Grapeshot ×1, Scout ×1 | 100 | 26 / 34 / 33 / 23 |
| Lira Sable | Watcher Drone ×4 | 100 | 29 / 26 / 31 / 26 |
| Tessa Rourke | Light Infantry ×2, Medium infantry ×1 | 100 | 26 / 38 / 30 / 27 |
| Miller Hayes | Iron Husk ×2 | 100 | 28 / 28 / 33 / 17 |
| Hank Mercer | Iron Husk ×2 | 100 | 36 / 28 / 22 / 18 |
| Rusty Miller | Ash Runner ×1, Bone Picker ×3 | 100 | 28 / 30 / 36 / 22 |
| Nora Blackwell | Rattletrap ×2 | 100 | 29 / 25 / 29 / 17 |
| Elena Hayes | Ash Runner ×2, Iron Husk ×1 | 100 | 27 / 30 / 32 / 18 |
| Vera Sterling | Ash Runner ×2, Iron Husk ×1 | 100 | 27 / 30 / 32 / 18 |
| Nova Sterling | Ash Runner ×1, Bone Picker ×3 | 100 | 36 / 24 / 23 / 19 |
| Iri Vane | Ash Runner ×2, Iron Husk ×1 | 100 | 27 / 32 / 33 / 25 |
| Mara the Archivist | Vessel Rifleman ×2, Spindle Gunner ×1 | 100 | 5 / 45 / 35 / 40 |
| Edda the Conductor | Vessel Rifleman ×2, Spindle Gunner ×1 | 100 | 5 / 41 / 43 / 39 |
| Thren the Scholar | Vessel Rifleman ×2, Spindle Gunner ×1 | 100 | 5 / 35 / 43 / 32 |
| Vale the Machinist | Vessel Rifleman ×2, Silt Pilgrim ×1 | 100 | 5 / 45 / 46 / 35 |
| Orr the Keeper | Vessel Rifleman ×2, Whisper Scout ×1 | 100 | 5 / 36 / 46 / 29 |
| Ilex the Surveyor | Vessel Rifleman ×2, Spindle Gunner ×1 | 100 | 5 / 38 / 39 / 34 |
| Jol the Warden | Vessel Rifleman ×2, Whisper Scout ×1 | 100 | 5 / 43 / 46 / 29 |
| Nyx the Cartographer | Vessel Rifleman ×2, Whisper Scout ×1 | 100 | 5 / 46 / 40 / 34 |

The three human starter deck lists are unchanged and retain one Trading Post each. Their totals become 85 (The Vessels), 87 (The Ashen), and 81 (Iron Concord) through the shared price change.

Resource demand remains within 2:1 for Concord/Ashen and within 2:1 across Energy/Materials/Tech for Vessels. The Vessels retain their intentionally small Human requirement.

## Construction score

Trade and ApBonus both register PlayerGlobal effects in StrategicEffectRegistry. Contributions feeds their value into GlobalRoleFit, and StrategicCardEvaluator.ScoreNonCombat includes it in the same breakdown and DecisionScore used by NonCombatCardPlayer and the Phase B arbiter. Trading Post and Intel Center (ApBonus) also share the same construction cost: 2 AP and H1/E1/T1.

TradeFunding.AccessValue contributes tradeAccessValue = 8 when Trade is absent and the snapshot has a surplus donor plus an unmet deck/operational resource need; otherwise it contributes zero. ApBonus uses dynamic recurring-AP valuation. They are comparable on the shared score scale, not assigned identical values or guaranteed priority. No separate mission or duplicate building scorer is introduced.

## Validation

Both Unity YAML validators pass for the two changed assets. A data-level audit resolves all 24 decks against the card catalogs and checks exact point totals, one Trading Post, unique keys, faction/copy limits, leader retention, all original entries and blueprint lists, cheap additions and resource totals. Native Unity import and game runs were not performed for this data-only update.
