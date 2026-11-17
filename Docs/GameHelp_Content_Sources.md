# English help - source audit

Base: `master`, merge `421f05e`, 2026-11-17. The owner authorized English article filling.
Scope: authored help content, grouped ability navigation, heading rendering and review/verification documents. No gameplay balance changes.

The 85 regular topic IDs remain unchanged. The 46 former ability topic IDs are now heading-block
identities inside one `all-abilities` article, selected with the single Abilities button. Every existing
ability title and description translation key is retained. The 138 original ability text blocks remain
unchanged; grouping adds localized subheadings and a page introduction. There are 86 navigation
articles and 440 text/heading blocks in total. No image is invented or required for the text to work.

| Section / topics | Primary implementation and authored data | Checks applied to the text |
|---|---|---|
| Getting started | `Assets/Scripts/Turns/GameTurnController.cs`; `Assets/Scripts/Turns/StrategicTurnEffects.cs`; `Assets/Scripts/Turns/InitiativeRules.cs`; `Assets/Scripts/Turns/TurnOrderResolver.cs`; `Assets/Scripts/Camera/RtsCameraController.cs`; `Assets/Scripts/Setup/GameSetupController.cs` | Personal turn versus round; income before initiative; 5 free + 5 bought tokens; price 1/2/4/8/16; AP 12/10/8; original-citadel recapture deadline; local seats, not promised online support. |
| Cards and decks | `Assets/Scripts/Cards/DeckRules.cs`; `Assets/Scripts/Cards/MatchLoadout.cs`; `Assets/Scripts/Cards/CardHandCommands.cs`; `Assets/Scripts/Cards/EquipmentSystem.cs`; `Assets/Scripts/Cards/TacticHand.cs`; `Assets/Scripts/UI/Cards/CardHandUI.cs`; `Assets/Scripts/UI/BattleScreen/BattleAttackPopupUI.Tactics.cs`; `Assets/Scenes/Game.unity` | 100 deck points; owned/copy limits; repeatable blueprints versus drawn main cards; starting hand 7, maximum 10, draw 2 AP; staged tactic can be changed before Accept; no general discard button. |
| Economy and infrastructure | `Assets/Scripts/Economy/IncomeProjection.cs`; `Assets/Scripts/Economy/TradeActions.cs`; `Assets/Scripts/Map/PlayerRoot.Trade.cs`; `Assets/Scripts/Map/InfrastructureActions.cs`; `Assets/Scripts/Map/BuildingData.cs`; `Assets/Scripts/Map/BuildingRegistry.cs`; `Assets/Scripts/UI/Popups/BaseViewerModalUI.cs`; `Assets/Config/GameConfig.asset` | Building-first finite collection and unit leftovers; Produce distinct from Collect; Trade rate/idle turn; hero on construction hex; slots 2 to 5; normal capture versus resource-site destruction; structure damage through explosions rather than a promised Siege button. |
| Armies and movement | `Assets/Scripts/Map/ArmyData.cs`; `Assets/Scripts/Map/ArmyData.Transport.cs`; `Assets/Scripts/Map/ArmyActions.cs`; `Assets/Scripts/Map/MovementRules.cs`; `Assets/Scripts/Units/UnitRepair.cs`; `Assets/Scripts/UI/Popups/ArmyViewerModalUI.cs` | Commander replaces capacity, not sum of heroes; capacities 2/4; create 2 AP; activation ledger; ground/air containers; slowest effective member; one passenger per carrier; repair 1 AP and a fixed per-instance resource cost. |
| Reconnaissance and weather | `Assets/Scripts/Map/VisionSystem.cs`; `Assets/Scripts/Map/HumanVisualMemory.cs`; `Assets/Scripts/Map/StealthSystem.cs`; `Assets/Scripts/Core/TacticalIntelligence.cs`; `Assets/Scripts/Map/SandstormRules.cs`; `Assets/Scripts/Map/MatchWeatherConditions.cs`; `Assets/Scripts/Combat/BattleRulesSnapshot.cs`; `Assets/Config/GameConfig.asset` | Vision versus visit/detection; 0/1 basic army/building vision; personal fog snapshots; best spot pool, no addition; stealth tie favors hiding; detection through next observer turn; actual storm sizes 1/3/5/6/5/3 and cooldown; weather frozen per encounter. |
| Battles and challenges | `Assets/Scripts/Combat/BattleEngine.cs`; `Assets/Scripts/Combat/BattleEngine.Commands.cs`; `Assets/Scripts/Combat/BattleRules.cs`; `Assets/Scripts/Combat/BattleGrid.cs`; `Assets/Scripts/Combat/BattleTurnOrder.cs`; `Assets/Scripts/Combat/ChallengeResult.cs`; `Assets/Scripts/Combat/BattleChallengeSession.cs`; `Assets/Scripts/Combat/FateDuelAi.cs`; `Assets/Scripts/Combat/DelayedBattleRegistry.cs`; `Assets/Scripts/Combat/ExplosionAttack.cs`; `Assets/Scripts/Combat/DisruptionRules.cs` | 5x5 grid; heroes anywhere in own back row; move OR attack; tactical movement orthogonal, range includes diagonals; Fate refills after encounter; distinct equality outcomes; retreat moves one neighboring hex and can meet enemies; 10-die explosions and friendly/structure targets. |
| Research and production | `Assets/Scripts/Cards/ResearchProductionSystem.cs`; `Assets/Scripts/Cards/ResearchProductionChallengeSession.cs`; `Assets/Scripts/Cards/MatchLoadout.cs`; `Assets/Scripts/Map/HexSelectionController.cs`; `Assets/Scripts/UI/BattleScreen/BattleAttackPopupUI.ResearchProduction.cs` | Role + owned Facility on same hex; attempt cost paid even on failure; Research reveals acting hero; Production does not; full Fate stat for tokens, local available Fate for spend; equality succeeds; no live hero Fate debit; no double resource charge for the produced card. |
| Aviation and AA | `Assets/Scripts/Aviation/AviationRules.cs`; `Assets/Scripts/Aviation/AviationActions.cs`; `Assets/Scripts/Aviation/AviationActions.Carriers.cs`; `Assets/Scripts/Aviation/AviationTurnLifecycle.cs`; `Assets/Scripts/Aviation/AviationCombatOperation.cs`; `Assets/Scripts/Aviation/AntiAirRules.cs`; `Assets/Scripts/Aviation/AntiAirState.cs` | Formation is not paid take-off; shared carrier storage; launch Energy; movement costs storm exits; once-per-owner-turn strikes; hero target only when sole living army member; AA requires sight and revealed carrier; Skip pair bookkeeping; fuel losses ceil(half max HP), usable movement halved; landing is not full repair. |
| Events | `Assets/Scripts/Map/HexSelectionController.Events.cs`; `Assets/Scripts/Cards/EventCatalog.cs`; `Assets/Cards/EventCatalog.asset` | Ground visit; Explore reveals whole visitor and exhausts movement; guard not confused with other armies; match cards versus Library permanent grant; command post restores free level-1 Base; full hand not promised unlimited reward delivery. |
| Campaign and collection | `Assets/Scripts/Campaign/CampaignRules.cs`; `Assets/Scripts/Campaign/CampaignMatchBridge.cs`; `Assets/Scripts/Progression/CollectionService.cs`; `Assets/Scripts/Progression/RewardService.cs`; `Assets/Scripts/Progression/MatchAccountOutcome.cs` | Neighbor attack; one pending operation; attacker win captures, draw/defense does not; ownership receipt once; immutable match loadout; win up to 5 choices, choose up to 2; defeat up to 1; one account outcome in non-campaign hotseat. |
| Menu, saves and presentation | `Assets/Scripts/Setup/MatchSaveEligibility.cs`; `Assets/Scripts/UI/Panels/GameMenuPanelUI.cs`; `Assets/Scripts/UI/Panels/MatchSaveSlotsUI.cs`; `Assets/Scripts/UI/Panels/GameSettingsPanelUI.cs`; `Assets/Scripts/UI/Tutorial/TutorialHintProgress.cs`; `Assets/Scripts/Turns/GameTurnController.cs`; `Assets/Scripts/UI/ArmyRadio/ArmyRadioChatter.cs` | Confirmed settled human turn; no save during pending actions; overwrite/delete confirmation; load replaces unsaved progress; observer action-boundary hold and separate pause; presentation messages do not alter gameplay. |
| All 46 ability entries | `Assets/Scripts/Cards/UnitAbilities.cs`; `Assets/Scripts/Cards/UnitAbilityCatalog.cs`; `Assets/Cards/UnitAbilityCatalog.asset` plus the relevant effect sites above | Complete tag coverage; authored magnitudes; practical use and restrictions; no claim that every known tag is present in every deck; round-income wording corrected where brief catalog text says only 'each turn'. |

## Editorial corrections

- `hand`: **Hand size, drawing and using cards** replaces the unimplemented general discard implication.
- `bases`: **Founding bases and expansion** replaces the unsupported influence-radius implication. The
  construction rule requires a hero on the chosen hex; it does not check a separate Base radius.
- Ordinary ground conquest, direct explosion structure damage and the absence of a manual Siege
  challenge are described separately. Old comments saying structure damage is impossible are not
  treated as a restriction on the live ExplosionAttack implementation.
- The current human UI has no dedicated stationary repeat-air-strike button. Its underlying rules
  are described without directing the player to a nonexistent control.
- In-game text contains no source paths, type names or claims of features scheduled for later.

## Validation

Run the structural/content checks, review-export comparison and both Unity YAML validators documented
in `Tools/game-help-verify/README.md`. The review document is generated from the catalog, never a second
source of gameplay text. Numeric constants are editorial snapshots of the audited revision and must
be reviewed if the corresponding balance or rule changes later.

This workspace has no Unity editor or C# compiler. Engine heading layout, scrolling the grouped ability page and the
full EditMode suite remain native acceptance checks; no successful Unity run is claimed.
