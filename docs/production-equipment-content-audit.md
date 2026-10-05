# Production Equipment — analysis, implementation and verification
## A. Scope and B. Source revision
Implemented an expansion of the existing physical Equipment content from 7 offered cards to 40: 29 new definitions and registration of all 11 legacy definitions. This is a content/behaviour change with a local shared UI formatter change; the Attachment, ResearchProduction, Resource Bank, evaluator and combat architecture are unchanged.
Source: `HolyPenguin21/turnbased-ae-style`, `master` commit `d88e11c0ba85a793579b2f6543a824316a79ccc5`. Before publication, master advanced to `02d59d61d10c24e3a2ed37b185a46b9bceebd6cf` (UI prefabs and Pin_Red texture). The four modified existing files were freshly read at that revision and matched the original source SHA. Delivery uses that newer parent/tree, preserving those unrelated changes. Project Unity: `6000.5.4f1`. Read the current AGENTS.md, card/attachment/production implementations, all four faction catalogs, existing attachment/Mutator/transaction/cache tests, and both verification-tool READMEs before editing. Local source-only snapshots support diff inspection; they are not the upstream commit.
## C. Existing Production and D. Existing physical Equipment outside Production
The pre-change authored definitions are listed below. All used CardType.Equipment, the default physical slot, Unit hosts, AP 1, activation AP 1, and no clearAbilityFamilies. Legacy `.bio` keys actually restrict Infantry (not Bio); these stable keys were deliberately retained. Numeric ids are catalog indices, not persistent identity. Costs are H / E / M / T.
| ID | Before name | Key | Tags | Effects | Added abilities | H / E / M / T | Fate | Offered before |
|---:|---|---|---|---|---|---|---:|---|
| 12 | it Flamer | `neutral.equipment.bio.flamer` | Infantry | Attack +1, Range = 1 | Pyrokinetic, Scorcher | 2 / 1 / 0 / 0 | 3 | yes |
| 13 | it Plasma Gun | `neutral.equipment.bio.plasma-gun` | Infantry | Attack = 8, Range = 2 | - | 0 / 1 / 1 / 2 | 6 | no |
| 14 | it AT Launcher | `neutral.equipment.bio.at-launcher` | Infantry | Attack +1, Range = 2 | Hyperkinetic | 0 / 1 / 0 / 4 | 4 | yes |
| 15 | it Claws | `neutral.equipment.bio.claws` | Infantry | Attack +1, Range = 1 | Hyperkinetic | 0 / 2 / 0 / 2 | 3 | yes |
| 16 | it Heavy MG | `neutral.equipment.bio.heavy-mg` | Infantry | Attack = 5, Range = 2 | - | 0 / 1 / 1 / 1 | 3 | yes |
| 17 | it Armor Plate | `neutral.equipment.mechanical.armor-plate` | Mechanical | Defense +1 | - | 0 / 1 / 0 / 1 | 4 | yes |
| 18 | it Artillery Cannon | `neutral.equipment.armored.artillery-cannon` | Vehicle | Range +1 | - | 0 / 1 / 1 / 4 | 4 | yes |
| 19 | it AT VH Launcher | `(empty)` | Vehicle | Attack +1, Range = 2 | Hyperkinetic | 0 / 1 / 1 / 4 | 4 | no |
| 20 | it Double Barrel | `neutral.equipment.armored.double-barrel` | Vehicle | - | CriticalDamage | 0 / 2 / 1 / 4 | 6 | yes |
| 21 | it Nuclear Engine | `neutral.equipment.mechanical.nuclear-engine` | Vehicle, Aircraft | Move +1 | - | 0 / 3 / 0 / 4 | 5 | no |
| 22 | it Plasma Cannon | `neutral.equipment.armored.plasma-cannon` | Vehicle | Attack = 9, Range = 2 | - | 0 / 4 / 1 / 4 | 5 | no |

Before Production: Flamer; AT Launcher; Claws; Heavy MG; Armor Plate; Artillery Cannon; Double Barrel. Outside Production: Plasma Gun; AT VH Launcher; Nuclear Engine; Plasma Cannon. Research already contains exactly 20 Mutators.
Legacy removal lists: Flamer removed ShockAttack/Hyperkinetic/Pyrokinetic/Scorcher/AA; Plasma Gun, AT Launcher, Heavy MG, Artillery Cannon and AT VH Launcher removed ShockAttack/Hyperkinetic/Pyrokinetic/AA; Claws removed Hyperkinetic/Pyrokinetic; Plasma Cannon also removed CriticalDamage. None reliably removed the complete seven weapon effects. The slots are permanent: normal runtime cannot replace an occupied physical slot. Removal is needed for host-native weapon abilities (and projected candidates), rather than to invent an unequip/replacement workflow.
## Host audit and authoring choices
EquipmentSystem.FitsHostCore uses ANY matching hostTypeTags, with hostKinds enforced separately. Combining Mechanical and Vehicle would allow Aircraft through Mechanical. New personal gear therefore requires Infantry, while new heavy ground modules require Vehicle OR Mecha. The current rosters have Bio+Infantry in Iron Concord/The Ashen and Mechanical+Infantry in The Vessels; vehicles and mecha are separate chassis tags. Aircraft have Mechanical+Aircraft, without Vehicle or Mecha. Armored is not required by every ground vehicle, so it was not added as an alternative that might widen chassis eligibility. Generic armor/chassis/servo/ceramic modules use Mechanical and intentionally remain available to mechanical aircraft. Nuclear Engine retains its legacy Vehicle OR Aircraft restriction. All physical definitions retain Unit-only host kinds; no automatic Hero expansion was introduced. Mutator remains independently Bio-gated.
Actual roster combinations (classification axes retain ANY semantics):

| Catalog | Tags and number of definitions |
|---|---|
| IronConcord | Bio, Hero (5); Bio, Hero, Support (3); Bio, Infantry (6); Mechanical, Vehicle (4); Mechanical (1); Bio, Infantry, Support (1); Mechanical, Armored (1); Mechanical, Vehicle, Armored (2); Support (2); Mechanical, Aircraft (2) |
| TheAshen | Bio, Hero (5); Bio, Hero, Support (3); Bio, Infantry (6); Mechanical (2); Bio, Infantry, Support (1); Mechanical, Vehicle (3); Mechanical, Armored, Mecha (1); Mechanical, Armored, Vehicle (3); Mechanical, Mecha (1); Mechanical, Aircraft (2) |
| Neutral | Bio (5); Mechanical, Armored (1); Mechanical (1); Bio, Infantry (1) |
| TheVessels | Mechanical, Hero (5); Mechanical, Hero, Support (3); Mechanical, Infantry (4); Mechanical, Vehicle (2); Mechanical, Support (5); Mechanical, Armored, Mecha (4); Mechanical, Armored, Vehicle (1); Mechanical, Aircraft (3) |

Cost tiers were chosen after inspecting the existing cost table: new basic upgrades use 1 Materials plus up to 1 Energy/Tech and Fate 3; specialized modules typically use Materials/Energy/Tech and Fate 4; heavy conversions use more Materials/Tech and Fate 4–6. Tradeoffs can lower the challenge tier. All new cards use Human 0. Legacy costs/Fate are unchanged. Servo Actuators/Nuclear Engine share Move +1 but preserve different host footprints and existing Nuclear Engine economy; Infantry/vehicle variants likewise share effects with different physical recipients, as required by the specified pool.
## E. Added Equipment
The 29 additions are listed below. Every row has AP 1, attachment slot Equipment, host kind Unit, no Resistance changes and no clearAbilityFamilies. Full weapon conversions remove ShockAttack, Hyperkinetic, Pyrokinetic, Scorcher, Splash, CriticalDamage and AA before adding their own abilities. Scaling upgrades and defensive/add-on modules do not clear existing weapons. Numeric signs/equals in this audit table explain gameplay semantics; UI badges retain plain numeric values.
| ID | Name | authoredKey | Host tags | Host kind | Stat changes | Abilities added | AP | H / E / M / T | Fate | Art stem |
|---:|---|---|---|---|---|---|---:|---|---:|---|
| 44 | Ballistic Shield | `neutral.equipment.infantry.ballistic-shield` | Infantry | Unit | Defense +1 | - | 1 | 0 / 0 / 1 / 0 | 3 | `Item_BallisticShield` |
| 45 | Ceramic Vest | `neutral.equipment.infantry.ceramic-vest` | Infantry | Unit | - | CeramicArmor | 1 | 0 / 0 / 1 / 1 | 4 | `Item_CeramicVest` |
| 46 | Assault Rifle Kit | `neutral.equipment.infantry.assault-rifle-kit` | Infantry | Unit | Attack +1 | - | 1 | 0 / 1 / 1 / 0 | 3 | `Item_AssaultRifleKit` |
| 47 | Marksman Rifle | `neutral.equipment.infantry.marksman-rifle` | Infantry | Unit | Range +1 | - | 1 | 0 / 0 / 1 / 1 | 3 | `Item_MarksmanRifle` |
| 48 | Shotgun | `neutral.equipment.infantry.shotgun` | Infantry | Unit | Attack = 6, Range = 1 | - | 1 | 0 / 1 / 1 / 0 | 3 | `Item_Shotgun` |
| 49 | Grenade Launcher | `neutral.equipment.infantry.grenade-launcher` | Infantry | Unit | Range = 2 | Splash | 1 | 0 / 1 / 1 / 1 | 4 | `Item_GrenadeLauncher` |
| 50 | Shock Rifle | `neutral.equipment.infantry.shock-rifle` | Infantry | Unit | Range = 2 | ShockAttack | 1 | 0 / 1 / 1 / 2 | 4 | `Item_ShockRifle` |
| 51 | Incendiary Rifle | `neutral.equipment.infantry.incendiary-rifle` | Infantry | Unit | Range = 2 | Pyrokinetic | 1 | 0 / 2 / 1 / 1 | 4 | `Item_IncendiaryRifle` |
| 52 | Rail Rifle | `neutral.equipment.infantry.rail-rifle` | Infantry | Unit | Range = 2 | Hyperkinetic | 1 | 0 / 1 / 1 / 2 | 4 | `Item_RailRifle` |
| 53 | Twin SMG | `neutral.equipment.infantry.twin-smg` | Infantry | Unit | Attack +1, Range = 1 | CriticalDamage | 1 | 0 / 2 / 1 / 3 | 6 | `Item_TwinSMG` |
| 54 | Portable Mortar | `neutral.equipment.infantry.portable-mortar` | Infantry | Unit | Range +1, Move -1 | Splash | 1 | 0 / 1 / 2 / 1 | 4 | `Item_PortableMortar` |
| 55 | Recoil Cannon | `neutral.equipment.infantry.recoil-cannon` | Infantry | Unit | Attack = 7, Range = 2, Move -1 | - | 1 | 0 / 1 / 2 / 2 | 4 | `Item_RecoilCannon` |
| 56 | AA Launcher | `neutral.equipment.infantry.aa-launcher` | Infantry | Unit | - | AA | 1 | 0 / 1 / 1 / 2 | 4 | `Item_AALauncher` |
| 57 | Reinforced Chassis | `neutral.equipment.mechanical.reinforced-chassis` | Mechanical | Unit | HP +2, Move -1 | - | 1 | 0 / 0 / 2 / 0 | 3 | `Item_Vh_ReinforcedChassis` |
| 58 | Servo Actuators | `neutral.equipment.mechanical.servo-actuators` | Mechanical | Unit | Move +1 | - | 1 | 0 / 1 / 1 / 1 | 3 | `Item_Vh_ServoActuators` |
| 59 | Ceramic Plating | `neutral.equipment.mechanical.ceramic-plating` | Mechanical | Unit | - | CeramicArmor | 1 | 0 / 0 / 2 / 1 | 4 | `Item_Vh_CeramicPlating` |
| 60 | Reactive Armor | `neutral.equipment.mechanical.reactive-armor` | Mechanical | Unit | Defense +1, Move -1 | - | 1 | 0 / 0 / 2 / 0 | 3 | `Item_Vh_ReactiveArmor` |
| 61 | Turbocharger | `neutral.equipment.vehicle.turbocharger` | Vehicle, Mecha | Unit | Move +1, Defense -1 | - | 1 | 0 / 1 / 1 / 0 | 3 | `Item_Vh_Turbocharger` |
| 62 | Autocannon | `neutral.equipment.vehicle.autocannon` | Vehicle, Mecha | Unit | Attack +1, Range = 2 | - | 1 | 0 / 1 / 2 / 1 | 4 | `Item_Vh_Autocannon` |
| 63 | HE Cannon | `neutral.equipment.vehicle.he-cannon` | Vehicle, Mecha | Unit | Range = 2 | Splash | 1 | 0 / 1 / 2 / 1 | 4 | `Item_Vh_HECannon` |
| 64 | Flame Projector | `neutral.equipment.vehicle.flame-projector` | Vehicle, Mecha | Unit | Range = 1 | Pyrokinetic, Scorcher | 1 | 0 / 2 / 2 / 1 | 4 | `Item_Vh_FlameProjector` |
| 65 | Rail Cannon | `neutral.equipment.vehicle.rail-cannon` | Vehicle, Mecha | Unit | Attack = 8, Range = 2 | Hyperkinetic | 1 | 0 / 2 / 2 / 3 | 6 | `Item_Vh_RailCannon` |
| 66 | Shock Projector | `neutral.equipment.vehicle.shock-projector` | Vehicle, Mecha | Unit | - | ShockAttack | 1 | 0 / 2 / 1 / 2 | 4 | `Item_Vh_ShockProjector` |
| 67 | AA Mount | `neutral.equipment.vehicle.aa-mount` | Vehicle, Mecha | Unit | - | AA | 1 | 0 / 1 / 2 / 2 | 4 | `Item_Vh_AAMount` |
| 68 | Dozer Blade | `neutral.equipment.vehicle.dozer-blade` | Vehicle, Mecha | Unit | Range = 1, Attack +1, Defense +1 | - | 1 | 0 / 0 / 2 / 1 | 4 | `Item_Vh_DozerBlade` |
| 69 | Siege Ram | `neutral.equipment.vehicle.siege-ram` | Vehicle, Mecha | Unit | Range = 1, Move -1 | CriticalDamage | 1 | 0 / 1 / 2 / 2 | 5 | `Item_Vh_SiegeRam` |
| 70 | Spiked Ram | `neutral.equipment.vehicle.spiked-ram` | Vehicle, Mecha | Unit | Range = 1, Attack +1 | ShockAttack | 1 | 0 / 1 / 2 / 1 | 4 | `Item_Vh_SpikedRam` |
| 71 | Mortar Rack | `neutral.equipment.vehicle.mortar-rack` | Vehicle, Mecha | Unit | Range +1, Attack -1 | Splash | 1 | 0 / 1 / 2 / 1 | 4 | `Item_Vh_MortarRack` |
| 72 | Assault Conversion Kit | `neutral.equipment.vehicle.assault-conversion-kit` | Vehicle, Mecha | Unit | Range = 1, Defense +1 | ShockAttack | 1 | 0 / 1 / 2 / 2 | 4 | `Item_Vh_AssaultConversionKit` |

## F. Production before / after
Before (7): Flamer; AT Launcher; Claws; Heavy MG; Armor Plate; Artillery Cannon; Double Barrel.

After (40), in offered order:

1. Flamer
2. AT Launcher
3. Claws
4. Heavy MG
5. Plasma Gun
6. Ballistic Shield
7. Ceramic Vest
8. Assault Rifle Kit
9. Marksman Rifle
10. Shotgun
11. Grenade Launcher
12. Shock Rifle
13. Incendiary Rifle
14. Rail Rifle
15. Twin SMG
16. Portable Mortar
17. Recoil Cannon
18. AA Launcher
19. Armor Plate
20. Reinforced Chassis
21. Servo Actuators
22. Ceramic Plating
23. Reactive Armor
24. Nuclear Engine
25. Turbocharger
26. Artillery Cannon
27. AT VH Launcher
28. Double Barrel
29. Plasma Cannon
30. Autocannon
31. HE Cannon
32. Flame Projector
33. Rail Cannon
34. Shock Projector
35. AA Mount
36. Dozer Blade
37. Siege Ram
38. Spiked Ram
39. Mortar Rack
40. Assault Conversion Kit

No CardType-based Production routing was introduced. ResearchProductionCatalog.productionCards is the sole availability list. Research entries and catalog source references are byte-for-byte unchanged. Existing ids 0–43 retain their positions; new definitions append at ids 44–72 before the catalog’s remaining fields.
## G. Compatibility formatter
EquipmentCardText is the one shared owner. Equipment and Mutator both use tags followed by host kinds, comma-separated, deduplicated, without labels or empty lines. Bio is still displayed for Mutator even if omitted from its authored tags because FitsHostCore enforces it independently.

| Before | After |
|---|---|
| Equipment: `Infantry` (kind omitted) | `Infantry, Unit` |
| Mutator: `Bio required` then `Unit` on another line | `Bio, Unit` |
| Mutator: `Bio required` then `Hero` on another line | `Bio, Hero` |
| Multi-tag example | `Armored, Vehicle, Unit` |
| Only host kind | `Hero` |
| No tags and no host kinds | no compatibility line |
The task’s Required/Host Type labels were illustrative: the current formatter actually used Bio required for Mutator and omitted Equipment host kinds. Empty/null host kinds still follow the existing gameplay rejection rule; the formatter does not change eligibility.
## H. Existing content fixes and art mapping
AT VH Launcher now has `neutral.equipment.armored.at-vh-launcher`. Removed the legacy `it ` presentation prefix from all 11 physical cards. Repository-wide lookup audit found no gameplay lookup depending on those prefixed Equipment names: production resolution uses authoredKey and rejects duplicate keys. Existing authored keys, ids, host restrictions, stat payloads, added abilities, costs and Fate are preserved. Existing conversion removal lists are completed through the existing removeAbilities payload. All physical cards explicitly serialize attachmentSlot 0.
Both art references use existing Sprite assets: `Assets/Textures/Units/0_Neutrals/GameCards/<stem>.png` and `Assets/Textures/Units/0_Neutrals/DetailView/<stem>_Full.png`. Both importer families were verified as textureType 8 / spriteMode 1. No placeholder/new bitmap asset is required.

| Equipment | GameCards filename | DetailView filename | Status |
|---|---|---|---|
| Flamer | `Item_Flamer.png` | `Item_Flamer_Full.png` | both linked |
| AT Launcher | `Item_AT.png` | `Item_AT_Full.png` | both linked |
| Claws | `Item_Claws.png` | `Item_Claws_Full.png` | both linked |
| Heavy MG | `Item_MG.png` | `Item_MG_Full.png` | both linked |
| Plasma Gun | `Item_PlasmaGun.png` | `Item_PlasmaGun_Full.png` | both linked |
| Ballistic Shield | `Item_BallisticShield.png` | `Item_BallisticShield_Full.png` | both linked |
| Ceramic Vest | `Item_CeramicVest.png` | `Item_CeramicVest_Full.png` | both linked |
| Assault Rifle Kit | `Item_AssaultRifleKit.png` | `Item_AssaultRifleKit_Full.png` | both linked |
| Marksman Rifle | `Item_MarksmanRifle.png` | `Item_MarksmanRifle_Full.png` | both linked |
| Shotgun | `Item_Shotgun.png` | `Item_Shotgun_Full.png` | both linked |
| Grenade Launcher | `Item_GrenadeLauncher.png` | `Item_GrenadeLauncher_Full.png` | both linked |
| Shock Rifle | `Item_ShockRifle.png` | `Item_ShockRifle_Full.png` | both linked |
| Incendiary Rifle | `Item_IncendiaryRifle.png` | `Item_IncendiaryRifle_Full.png` | both linked |
| Rail Rifle | `Item_RailRifle.png` | `Item_RailRifle_Full.png` | both linked |
| Twin SMG | `Item_TwinSMG.png` | `Item_TwinSMG_Full.png` | both linked |
| Portable Mortar | `Item_PortableMortar.png` | `Item_PortableMortar_Full.png` | both linked |
| Recoil Cannon | `Item_RecoilCannon.png` | `Item_RecoilCannon_Full.png` | both linked |
| AA Launcher | `Item_AALauncher.png` | `Item_AALauncher_Full.png` | both linked |
| Armor Plate | `Item_Vh_Armor.png` | `Item_Vh_Armor_Full.png` | both linked |
| Reinforced Chassis | `Item_Vh_ReinforcedChassis.png` | `Item_Vh_ReinforcedChassis_Full.png` | both linked |
| Servo Actuators | `Item_Vh_ServoActuators.png` | `Item_Vh_ServoActuators_Full.png` | both linked |
| Ceramic Plating | `Item_Vh_CeramicPlating.png` | `Item_Vh_CeramicPlating_Full.png` | both linked |
| Reactive Armor | `Item_Vh_ReactiveArmor.png` | `Item_Vh_ReactiveArmor_Full.png` | both linked |
| Nuclear Engine | `Item_Vh_Motor.png` | `Item_Vh_Motor_Full.png` | both linked |
| Turbocharger | `Item_Vh_Turbocharger.png` | `Item_Vh_Turbocharger_Full.png` | both linked |
| Artillery Cannon | `Item_Vh_ArtileryCannon.png` | `Item_Vh_ArtileryCannon_Full.png` | both linked |
| AT VH Launcher | `Item_Vh_AT.png` | `Item_Vh_AT_Full.png` | both linked |
| Double Barrel | `Item_Vh_DualCannon.png` | `Item_Vh_DualCannon_Full.png` | both linked |
| Plasma Cannon | `Item_Vh_PlasmaCannon.png` | `Item_Vh_PlasmaCannon_Full.png` | both linked |
| Autocannon | `Item_Vh_Autocannon.png` | `Item_Vh_Autocannon_Full.png` | both linked |
| HE Cannon | `Item_Vh_HECannon.png` | `Item_Vh_HECannon_Full.png` | both linked |
| Flame Projector | `Item_Vh_FlameProjector.png` | `Item_Vh_FlameProjector_Full.png` | both linked |
| Rail Cannon | `Item_Vh_RailCannon.png` | `Item_Vh_RailCannon_Full.png` | both linked |
| Shock Projector | `Item_Vh_ShockProjector.png` | `Item_Vh_ShockProjector_Full.png` | both linked |
| AA Mount | `Item_Vh_AAMount.png` | `Item_Vh_AAMount_Full.png` | both linked |
| Dozer Blade | `Item_Vh_DozerBlade.png` | `Item_Vh_DozerBlade_Full.png` | both linked |
| Siege Ram | `Item_Vh_SiegeRam.png` | `Item_Vh_SiegeRam_Full.png` | both linked |
| Spiked Ram | `Item_Vh_SpikedRam.png` | `Item_Vh_SpikedRam_Full.png` | both linked |
| Mortar Rack | `Item_Vh_MortarRack.png` | `Item_Vh_MortarRack_Full.png` | both linked |
| Assault Conversion Kit | `Item_Vh_AssaultConversionKit.png` | `Item_Vh_AssaultConversionKit_Full.png` | both linked |

## I. AI verification
Traced GenerationSource → offered authored keys → reserved affordability → MaterializationPlanFactory → MaterializationExecutor → ResearchProductionSystem.TryStartAttempt/MintCard → EquipmentSystem. The existing evaluator computes signed deltas from Predict/Project/PredictAttachment. Heavy MG projects Attack 3→5 as a gain and Attack 7→5 as a loss, without an “always useful” rule. The existing scorer includes stat and added/lost ability deltas, tactical Move/Range effects and role deltas. WorthIt matchup fit remains the existing gate; AA ability maps to the existing anti-air role. No coefficients, evaluator logic or materialization logic changed. Added tests exercise the real evaluator and actual authored definitions, but execution is pending.
## J. Resource Bank
No bank mutation/API was introduced. GenerationSource.FitsReservedAffordability protects reservation claims; MaterializationExecutor repeats that check immediately before the shared attempt transaction. TryStartAttempt validates canonical root/catalog/actor/site/budget before one payment. A failed challenge intentionally consumes its stake; pre-start rejection does not. MintCard sets ResearchProductionCreated, so EffectivePlayResourceCost is null and only activation AP is paid on attachment. MaterializationPlanFactory counts the creation stake once and the minted card activation once; consumption Push/Pop handles plan claims. Existing cancellation/refund policy is unchanged. Source audit found no new double-spend/reservation path; actual bank/transaction tests were not executable here. New per-card tests check minted costs and reservation consumption, alongside existing AttachmentSlotTests and ResearchProductionAttemptTransactionTests.
## K. Cache / projection
EquipmentSystem.Project produces fresh dictionaries/ability lists for hand/UI/AI prediction without mutating definitions. Permanent live installation writes the existing UnitData attachment/stat/ability state; canonical slot projection rebuilds Equipment then Mutator while retaining runtime deltas and spent resources. Live attachment still publishes visibility/content changes through RefreshLiveHostObservation. WorldAnalysis.Self reads current profiles and WorldAnalysis.Observation compares combat stats/abilities/capabilities for reevaluation. WorthIt estimate keys include attack, defense, HP, initiative, ability and tag lists; movement/range are tactical values, not hidden new fields. Added authored-content cache tests verify two misses before/after an upgrade and a hit on repeated post-upgrade reads when executed. No separate cache was added.
## UI call paths
CardUI/CardFace, ArmyUnitCardUI attachment previews, ResearchProductionModalUI.Description and EquipmentArtToggle use the shared formatter and existing stat badges. Attached descriptions retain effect-only formatting. Army viewer, battle and spawned units read the existing projected/live host stats and abilities. No prefab/scene changes were needed. No rendered Unity UI inspection was possible in this environment; editor validation must check Production pages, hand cards, attachment mode, Army cards/viewer, Battle cards, hover/toggle, badges and compatibility text.
## L. Checks and limits
Passed here:

- Parsed all four authored catalogs and validated unique cross-catalog keys, contiguous ids and exact registration of 40 physical cards.
- Confirmed 29 additions, at least one actual Unit host for each definition, Infantry support for Mechanical infantry, and no Aircraft matches for ground conversions.
- Validated 80 correct GameCards/DetailView Sprite GUID links and importer modes.
- Compared all Mutator/non-Equipment definitions, remaining catalog fields and Research entries against the pre-change snapshot; unchanged.
- Verified preserved legacy costs/Fate/stat effects/host constraints and no Resistance changes.
- Ran verify_full.js and verify_types.js on both changed assets: pass.
- Ran git diff --check: pass.
Added automated C# coverage (not run here): ProductionEquipmentContentTests covers all 40 authored cards’ projection/apply and minted-card cost/reservation paths, representative stat conversions and all requested ability categories, signed Heavy MG evaluator delta, weapon ability removal, actual faction host matching, AA radius/entry-reaction/one-shot state, both slot installation orders, exact cache miss/hit behavior and Unity catalog/Sprite resolution. AttachmentCompatibilityTextTests covers compact Unit/Hero/Mutator/multiple-tag/duplicate/empty constraints. Existing MutatorContentTests now shares AttachmentContentTestData and expects the new compact format.
Unity 6000.5.4f1 and dotnet/Mono are absent. Tools/ai-verify/setup.sh failed due package-manager privilege restrictions and missing dotnet-sdk-8.0 availability. Therefore no compile baseline, differential C# suite or EditMode suite was claimed to pass. Run the project’s full EditMode suite in Unity, then the affected gameplay/UI scenarios, before merging.
Twin SMG retains the requested Attack +1, Range =1 and CriticalDamage. Source inspection confirms canonical CriticalDamage defaults to ×2; its Fate 6 tier follows Double Barrel, and its cost is E2/M1/T3. A contract test exercises the canonical damage modifier. Existing battle simulation/evaluator execution is unavailable here, so this is provisional authored content, not a demonstrated balanced outcome. No effect was silently weakened. Validate across weak/strong Infantry and melee/ranged opponents in the existing simulation before final balance acceptance.
## M. Deferred implementation
No additional gameplay system was introduced or deferred by this patch. Remaining acceptance work is the unavailable editor/test execution described above. The feature branch is reviewable; master is not merged.
