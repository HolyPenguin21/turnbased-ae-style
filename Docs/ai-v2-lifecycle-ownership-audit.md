# AI Strategy V2 lifecycle ownership audit

Source: GitHub connector master `706b1bbddee9abe4c4f052caea353bc3a790a332`, all local C# source hashes verified against GitHub tree before code edits.

## Pre-change state holders

| Holder | Scope | Mutation owner(s) before migration | Reset/lifetime owner |
|---|---|---|---|
| MissionIntentState | mixed: durable intents/domain + Recon turn | Continuity.Put/Remove; domain methods | MissionIntentRegistry.Clear (match); Recon lazy turn |
| ActorCommitments | derived mutable view per pass | FromIntents + PhaseA/tempo/air Claim | new view on snapshot/intents reconciliation |
| StrategicResourceReservationLedger | turn | InfrastructureFulfillment/Provisioning/Reaction/Continuity | Pipeline BeginTurn / expiry / turn end |
| StrategicInterruptRegistry | turn pending facts | observation/execution boundaries | typed Consume / Clear / lazy turn |
| V2StateVersion | process monotonic freshness | many committed mutation boundaries | never reset; equality/order only |
| CapabilityPoolExhaustionRegistry | turn, reaction rounds carry | allocation/provisioning diagnostics | Pipeline BeginTurn; BeginRound preserves |
| ResourceStarvationRegistry | persistent EWMA; pass evidence; forecasts grace 1 turn | diagnostics Record/Verify + forecast producers | Decay once per turn; BeginVerifiedPass; match Clear |
| GroundCombatAdmissionRegistry | proposal scoped derived | ground-combat planners Record* | ConditionalWeakTable proposal lifetime |
| PreparationDeliveryMemory | current and previous turn grace | capability delivery Remember | stamp check; match Clear |
| ReconIntelSnapshotRegistry | cache (player, turn, knowledge revision) | WorldAnalysis Record | key fallback/replacement; match Clear |
| ScoutTrailRegistry | persistent bounded actor trail | AiTurnController actual movement | bounded queue; match ClearAll |
| AirSortieRegistry | persistent flight/endurance | air mission executors/continuity | landing/recovery/actor removal; match Clear |
| AviationObligationStallRegistry | turn | obligation execution stall | lazy turn; match Clear |
| DevelopmentInvestmentGate | persistent consecutive-turn streak | DemandLayer Observe first snapshot | match Clear; skipped turn breaks streak |
| TurnResourceBook | derived live bank; no storage | no writer: canonical physical stock + ledger | each read |
| PhaseAApBudget | pass | PhaseA capability followup | Create per pass |
| StrategicTempoBudget | turn | tempo + PhaseA generation | For lazy stamp; match ClearAll |
| OperationContinuationWindow | turn | Pipeline settle/mobilization | stamp equality; match Clear |
| MissionOutcomeLedger | pass facts + semantic classification | provision/execution recording; Finalize Classify | new ledger per allocation cycle |
| ForceBaselineRegistry | persistent player baseline | Pipeline RecordStart first scan | match Clear |
| AiReconMemory | persistent knowledge | observation | match reset |
| AiReconIntelMemory | persistent intelligence | observation | match reset |
| ApTurnPressure | persistent feedback/telemetry | turn end Record | match reset |
| StrategicCapabilityLeaseRegistry | turn materialized capability protection | StrategicManager Mark | Housekeeping clear; session fallback on abort |
| ReconPatrolState / ReconAirSortieState | persistent actor role/flight state | recon executors/continuity | domain reconciliation / match reset |
| ProvisioningSession / AllocationSession | pass assignments/envelopes | corresponding provisioning/allocator | new instance per pass |
| ProjectedPhysicalState / MaterializationConsumptionState | pass simulation/consumption | materialization planning | new plan/pass |
| WorldSnapshot | derived decision frame | WorldAnalysis Scan/RefreshStrategicKnowledge | replace after dirty facts |

## Confirmed hotspots and safe boundaries

* MissionIntentState contains three lifetimes: mission dictionary, generated Development claims and Economy failure/suppression dictionaries persist; Recon trim/used sets are lazily cleared by independent turn stamps. All method bodies must retain their current semantics during separation.
* Pipeline explicitly begins telemetry, capability exhaustion, resource reservations and later expires/asserts resources. Other registries lazily reset. Session must not reset starvation EWMA, sortie endurance, scout trail, investment streaks or PreparationDeliveryMemory's intentional previous-turn grace.
* ActorCommitments is already the normalized Housekeeping contract. FromIntents also owns mission-specific actor validity. Extract policy mechanically; do not replace role validity with generic claimed. Its temporary Claim writers must remain because Phase A and air binding need same-pass protection.
* Economy reservation owner uses `Economy:{StableMissionKey}`, and its key encoding matches MissionIntentKey.ForEconomy (kind, resource/recovery actor, site). It does NOT include build-card identity. MissionIntentKey is suitable for Economy ownership; Raid/Attack StableMissionKey identifies a leg while MissionIntentKey identifies the durable target. Reaction is a pass owner rather than a mission. Typed migration must retain this distinction and exact bank authority until proved.
* Outcome Completed is often LEG completion, not durable-operation termination: Raid assault/campaign, return subleg, Attack support sideleg and Recon waypoint. Generic terminal cleanup on Completed would change gameplay. Existing Continuity domain paths remain authoritative operation transitions.
* NoExecutableStep is deferred for the rest of the current turn by CapabilityPoolExhaustionRegistry, while Economy applies separate outbound/ReturnBuilder policy. NeedsReplan/StateChanged are execution freshness facts, not interchangeable with operation termination.
* V2StateVersion has only equality/order readers; absolute value is not serialized. Commit boundaries already avoid rollback/no-op bumps, but producers independently publish invalidation observations. Merging both without tracing each transaction would risk duplicate bumps or changed dirty flags.

## Cache contract before changes

| Cache/frame | Source/key | Writers / invalidation | Readers / boundary |
|---|---|---|---|
| WorldSnapshot | authoritative own world + honest map memory; player/turn/knowledge/pathing | WorldAnalysis Scan/Refresh; Pipeline refresh after facts | Strategy, Demand, allocator, provisioning; frame replacement |
| AiMapMemory | observed hex/contact facts; KnowledgeVersion | observation/visibility; revision only for actual knowledge change | WorldAnalysis/recon; match reset |
| Recon intel snapshot | last observed map; player + turn + KnowledgeVersion | WorldAnalysis Record / lazy fallback | recon objective/capacity; frozen prior frames keep their own copy |
| route/path | map/pathing, actor movement/eligibility | existing route owners; pathing/knowledge keys | executors/provisioning; existing isolation tests |
| combat estimate | live/known roster, defender context | existing combat cache owner and lifecycle invalidation | WorthIt/admission; existing tests |
| capability/force | own map units + hand + remaining deck | WorldAnalysis refresh | Demand/Attack readiness; no new cache |
| V2StateVersion | committed V2 mutations, process order | existing transaction endpoints | executor preflight/planning stamps; no reset |

## Migration safety

Initial wrapper migration may retain old adapters, but never parallel copies. Unity 6000 project acceptance requires full EditMode suite and E2E gameplay; managed differential checks cannot prove native gameplay parity.

## Audited call sites

Read/write entry points for each owner; declarations and comments are omitted. This inventory records all external references in the current baseline, rather than claiming each call mutates state.

### MissionIntentRegistry

- `Assets/Editor/AiActiveDefenceTests.cs:281,284,294,304,307,319,327,330,349`
- `Assets/Editor/AiAggressionOwnershipRegressionTests.cs:32,40`
- `Assets/Editor/AiAggressionRaidTests.cs:599`
- `Assets/Editor/AiAttackBaseRefitTests.cs:26,36,142,251,255,282`
- `Assets/Editor/AiAttackIntermediateBaseTests.cs:25,26,117,130,136,148,152,202`
- `Assets/Editor/AiAttackLaneTests.cs:48,55,341,950,978`
- `Assets/Editor/AiAttackObjectiveTests.cs:585,590,611`
- `Assets/Editor/AiAviationMissionRegressionTests.cs:32,41`
- `Assets/Editor/AiAviationSortieCycleTests.cs:1130,1150,1164,1165,1169`
- `Assets/Editor/AiDevelopmentEquipmentMatchupTests.cs:584,602,613,621,650,657`
- `Assets/Editor/AiEconomyBuildSiteOwnershipTests.cs:42,45,55,58`
- `Assets/Editor/AiEconomyContinuityAuditTests.cs:24,83,115,313,332,345,412,578`
- `Assets/Editor/AiEconomyDecisionTests.cs:458,488,519,542,574,594,634,650,670,681,694,741,770,839,900,916,937,941,961,983,993,1002,1006,1025,1048,1088,1132,1154,1183,1798,1817,1896,1936,1980,2390,2413,2437,3115,3901`
- `Assets/Editor/AiEconomyDevelopmentAuditTests.cs:40,63,91,108`
- `Assets/Editor/AiEconomyOwnershipTests.cs:68,114,222,226,236,238,247,383,389`
- `Assets/Editor/AiFieldPowerCommittedRecipientTests.cs:35,41,62`
- `Assets/Editor/AiGarrisonHeroTests.cs:207,212,227`
- `Assets/Editor/AiRaidIntentStateTests.cs:149,191,214,236,252,287,347`
- `Assets/Editor/AiReconAttackObservationTests.cs:24,93`
- `Assets/Editor/AiReconAuditBugTests.cs:20,143,253,271,296,310`
- `Assets/Editor/AiReconContinuityPayloadTests.cs:17,28,42,58,64`
- `Assets/Editor/AiReconDetectorRiskTests.cs:20`
- `Assets/Editor/AiReconIncumbentCostTests.cs:14`
- `Assets/Editor/AiReconTrimEligibilityTests.cs:36,46,94,116,139,156`
- `Assets/Editor/AiStrategicSpendabilityApTests.cs:23,49,135,182`
- `Assets/Editor/AiTaskScoreBaseSwitchRegressionTests.cs:56,84,106,108,127,142`
- `Assets/Scripts/Ai/V2/Analysis/ReconCapacitySnapshot.cs:106`
- `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Development.cs:214`
- `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Economy.cs:165,533`
- `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Self.cs:463`
- `Assets/Scripts/Ai/V2/Continuity/AttackIntentLabel.cs:16`
- `Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.Raid.cs:464,486,530,568`
- `Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.cs:91,148,217,270,407,1093,1117,1125`
- `Assets/Scripts/Ai/V2/Evaluation/Cards/NonCombatCardPlayer.cs:495`
- `Assets/Scripts/Ai/V2/Evaluation/Cards/StrategicCardEvaluator.cs:1443`
- `Assets/Scripts/Ai/V2/Execution/AttackExecutor.cs:41`
- `Assets/Scripts/Ai/V2/Execution/AttackTacticalOpportunity.cs:327`
- `Assets/Scripts/Ai/V2/Execution/ReconAirExecutor.cs:207`
- `Assets/Scripts/Ai/V2/Execution/ReconGroundExecutor.cs:389`
- `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorgAnalyzer.cs:252`
- `Assets/Scripts/Ai/V2/Housekeeping/HousekeepingManager.cs:66,84,97`
- `Assets/Scripts/Ai/V2/Initiative/InitiativeCoordinatorV2.cs:43`
- `Assets/Scripts/Ai/V2/Materialization/MaterializationDeliveryPolicy.cs:92,411`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.Attack.Gather.cs:25`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.Attack.Preparation.cs:105,393`
- `Assets/Scripts/Ai/V2/Missions/AttackBaseRefitPolicy.cs:34,50,221`
- `Assets/Scripts/Ai/V2/Missions/Raid/RaidRecoveryPlanner.cs:564`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.AggressionAdmission.cs:54`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.DevelopmentAdmission.cs:231`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:1123,1299,1321`
- `Assets/Scripts/Ai/V2/Provisioning/AttackProvisioner.cs:101,376`
- `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.Development.cs:64`
- `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.Economy.cs:59`
- `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs:297`
- `Assets/Scripts/Ai/V2/Reaction/ReactionRoundExecutor.cs:286`
- `Assets/Scripts/Ai/V2/Reaction/StrategicReactionPass.cs:178,196`
- `Assets/Scripts/Ai/V2/Recon/ReconAssignmentPlanner.cs:191,510,536,626,684`
- `Assets/Scripts/Ai/V2/Recon/ScoutMoverSelector.cs:155`
- `Assets/Scripts/Ai/V2/State/StrategicSpendability.cs:147`
- `Assets/Scripts/Ai/V2/Strategy/Demand/AggressionDemandEvaluator.Attack.cs:190`
- `Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Economy.cs:130,1335`
- `Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs:81,475`
- `Assets/Scripts/Ai/V2/Strategy/Objectives/AttackForcePool.cs:44`
- `Assets/Scripts/Ai/V2/Strategy/Objectives/AttackObjectiveEvaluator.cs:558`
- `Assets/Scripts/Ai/V2/Strategy/PhaseA/CapabilityDeliveryEvaluator.cs:182,345`
- `Assets/Scripts/Ai/V2/Strategy/StrategicMaintenancePolicy.cs:208`
- `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseA.cs:427,582`
- `Assets/Scripts/Setup/CitadelSetupController.cs:164`

### ActorCommitments

- `Assets/Editor/AiAggressionOwnershipRegressionTests.cs:164`
- `Assets/Editor/AiAggressionRaidTests.cs:470`
- `Assets/Editor/AiAttackBaseRefitTests.cs:192`
- `Assets/Editor/AiAttackLaneTests.cs:111,273,321,1192`
- `Assets/Editor/AiEconomyDecisionTests.cs:465,479,536,587,744,762,855,892,1125,1176,2375,2468`
- `Assets/Editor/AiRaidIntentStateTests.cs:145,179`
- `Assets/Editor/AiReconAuditBugTests.cs:103`
- `Assets/Editor/AiReconContinuityPayloadTests.cs:94`
- `Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.Attack.cs:77,91,108,133,418,566,657`
- `Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.Raid.cs:87,101,115`
- `Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.cs:465,737`
- `Assets/Scripts/Ai/V2/Evaluation/Cards/NonCombatCardPlayer.cs:497`
- `Assets/Scripts/Ai/V2/Execution/ReconAirExecutor.cs:206`
- `Assets/Scripts/Ai/V2/Housekeeping/HousekeepingManager.cs:65,83,96`
- `Assets/Scripts/Ai/V2/Materialization/MaterializationDeliveryPolicy.cs:94`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.cs:33`
- `Assets/Scripts/Ai/V2/Missions/EconomyMissionPlanner.cs:42`
- `Assets/Scripts/Ai/V2/Missions/ReconMissionPlanner.cs:144`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:178,237,270,513,557,644,1122,1222,1261,1298,1320`
- `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.Development.cs:72`
- `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.Economy.cs:62`
- `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs:296`
- `Assets/Scripts/Ai/V2/Reaction/ReactionRoundExecutor.cs:105,135,285`
- `Assets/Scripts/Ai/V2/Reaction/StrategicReactionPass.cs:177`
- `Assets/Scripts/Ai/V2/Recon/ReconAssignmentPlanner.cs:190,509`
- `Assets/Scripts/Ai/V2/Strategy/AviationRebasePlanner.cs:92`
- `Assets/Scripts/Ai/V2/Strategy/Objectives/DevelopmentOpportunityEvaluator.cs:130`
- `Assets/Scripts/Ai/V2/Strategy/PhaseA/CapabilityDeliveryEvaluator.cs:184`

### StrategicResourceReservationLedger

- `Assets/Editor/AiAttackBaseRefitTests.cs:28,38,226,231`
- `Assets/Editor/AiAttackIntermediateBaseTests.cs:221,224,231,232,238`
- `Assets/Editor/AiDevelopmentDecisionPathTests.cs:118,137`
- `Assets/Editor/AiDevelopmentReadmissionTests.cs:21,31,35`
- `Assets/Editor/AiEconomyContinuityAuditTests.cs:26,387,393,480,603`
- `Assets/Editor/AiEconomyDecisionTests.cs:51,2026,2027,2039,2041,2792,2811,2813,2815,2824,2838,2842,3685,3691,3694,3696,3924,3927,3929,3931,3940`
- `Assets/Editor/AiEconomyDevelopmentAuditTests.cs:147,157,159,162,164,173,182,185,194,206,209,212,215,224,233,236,239,248,256,258,260`
- `Assets/Editor/AiEconomyOwnershipTests.cs:69,423,424`
- `Assets/Editor/AiEconomyReservationLifecycleTests.cs:40,49,52,54,56,75,87,93,96,99,102,104,113,128,130,132,135,137,139,147,156,168,170,172,174,183,190,199,207,215,217,220`
- `Assets/Editor/AiReservationInvariantsTests.cs:114,115,119,122,124,133,134,138,142`
- `Assets/Editor/AiSecondaryBaseStrategicNodeTests.cs:558,559,573,575,588,611`
- `Assets/Editor/AiStrategicSpendabilityApTests.cs:20,207,208,251,330,332,343,346,348,350,352,361,362,389,420,421,478,479,508,509,543,544,553,580,601,602,614`
- `Assets/Editor/AiTurnResourceBookTests.cs:43,46,53,74,79,81,84,101,103,105,107,112,114,118,121,138,139,140,141,142,143,144,145,146,147,148,149,150,152,303,304`
- `Assets/Editor/ArmyMovementLifecyclePlayModeTests.cs:84,438,463,465`
- `Assets/Editor/ArmyRemovalRegistryTests.cs:32,45,168,170,176,177,178`
- `Assets/Editor/AttachmentLifecycleTests.cs:33,49,57,152,170`
- `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Development.cs:223`
- `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Economy.cs:60,61`
- `Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.cs:240,242,636,2006`
- `Assets/Scripts/Ai/V2/Execution/TaskExecutor.cs:852`
- `Assets/Scripts/Ai/V2/Housekeeping/HousekeepingManager.cs:77`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:110,372,1396,1398`
- `Assets/Scripts/Ai/V2/Reaction/ReactionRoundExecutor.cs:44,48,79`
- `Assets/Scripts/Ai/V2/Reaction/StrategicReactionPass.cs:230`
- `Assets/Scripts/Ai/V2/State/ReservationInvariants.cs:149,166,225`
- `Assets/Scripts/Ai/V2/State/StrategicSpendability.cs:269`
- `Assets/Scripts/Ai/V2/State/TurnResourceBook.cs:130`
- `Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs:219,423,426,430,442,447,456,472,540,552,559,568,575,581,597,599,602,607,621,625`
- `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseB.cs:40,42,52,57,100,291`
- `Assets/Scripts/Setup/CitadelSetupController.cs:173`

### StrategicInterruptRegistry

- `Assets/Editor/AttachmentSlotTests.cs:67,70`
- `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Observation.cs:38,49,60,68,81,87,93,102,107,111,116,123,131,139`
- `Assets/Scripts/Ai/V2/Execution/AttackExecutor.cs:295,625`
- `Assets/Scripts/Ai/V2/Execution/CardDrawExecutor.cs:62`
- `Assets/Scripts/Ai/V2/Execution/RaidExecutor.cs:417`
- `Assets/Scripts/Ai/V2/Execution/ReconAirExecutor.cs:797,803`
- `Assets/Scripts/Ai/V2/Execution/ReconGroundExecutor.cs:452,458`
- `Assets/Scripts/Ai/V2/Execution/TaskExecutor.cs:169`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:416,453`
- `Assets/Scripts/Ai/V2/Reaction/ReactionOpportunityProbe.cs:28,66`
- `Assets/Scripts/Ai/V2/Reaction/ReactionRoundExecutor.cs:27,30,33,54,276,278,281,325,335`
- `Assets/Scripts/Ai/V2/Reaction/StrategicReactionPass.cs:76,151,158,162,163,173`
- `Assets/Scripts/Ai/V2/Strategy/PhaseB/TempoActionExecutor.cs:104`
- `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseB.cs:102,203,215`
- `Assets/Scripts/Setup/CitadelSetupController.cs:172`

### V2StateVersion

- `Assets/Editor/AttachmentLifecycleTests.cs:158,165`
- `Assets/Scripts/Ai/V2/Evaluation/Cards/NonCombatCardPlayer.cs:679,875,876`
- `Assets/Scripts/Ai/V2/Execution/AttackExecutor.cs:624`
- `Assets/Scripts/Ai/V2/Execution/BuildingPlayExecutor.cs:108,123,254,277`
- `Assets/Scripts/Ai/V2/Execution/CardPlayExecutor.cs:276,277`
- `Assets/Scripts/Ai/V2/Execution/GroundCombatLegStep.cs:179`
- `Assets/Scripts/Ai/V2/Execution/GroundCombatReinforcementTransaction.cs:33,56`
- `Assets/Scripts/Ai/V2/Execution/RaidExecutor.cs:416`
- `Assets/Scripts/Ai/V2/Execution/ReconAirExecutor.cs:80,102,143,154,182,225,265,280,300,339,439,597,633,670,701`
- `Assets/Scripts/Ai/V2/Execution/ReconGroundExecutor.cs:240,416`
- `Assets/Scripts/Ai/V2/Execution/TaskExecutor.cs:161,176,229,444,474,879,880,887`
- `Assets/Scripts/Ai/V2/Materialization/MaterializationExecutor.cs:146,147`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:264,732,1284`
- `Assets/Scripts/Ai/V2/Provisioning/ProvisioningResult.cs:93,108`
- `Assets/Scripts/Ai/V2/Strategy/AviationRebasePlanner.cs:444`
- `Assets/Scripts/Ai/V2/Strategy/Demand/DevelopmentUpgradeFulfillment.cs:99,113`
- `Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs:202,927`
- `Assets/Scripts/Ai/V2/Strategy/HandReplenishPolicy.cs:70`
- `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseB.cs:263,268,269,312`

### CapabilityPoolExhaustionRegistry

- `Assets/Editor/AiAttackLaneTests.cs:781,782`
- `Assets/Editor/AiEconomyContinuityAuditTests.cs:369,371,373`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:108,858,888,892,895,901,904,924,951,953,956,957`
- `Assets/Scripts/Ai/V2/Reaction/ReactionRoundExecutor.cs:65,189,210,212,215,216`
- `Assets/Scripts/Setup/CitadelSetupController.cs:165`

### ResourceStarvationRegistry

- `Assets/Editor/AiEconomyDecisionTests.cs:42,45,47,49,53,55,56,57,58,59,60,62,65,76,79,81,83,85,88,90,92,94,96`
- `Assets/Editor/AiV2ArchitecturalRegressionTests.cs:20,105,106,108,109,110,112,114,117,119`
- `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Economy.cs:47,67`
- `Assets/Scripts/Ai/V2/Diagnostics/MaterializationDiagnostics.cs:26,154`
- `Assets/Scripts/Ai/V2/Evaluation/Cards/StrategicCardEvaluator.cs:1715`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.cs:20,41`
- `Assets/Scripts/Ai/V2/Missions/ReconMissionPlanner.cs:58,162`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.DevelopmentAdmission.cs:69`
- `Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.cs:30`
- `Assets/Scripts/Ai/V2/Strategy/Objectives/DevelopmentOpportunityEvaluator.cs:120,167`
- `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseA.cs:660`
- `Assets/Scripts/Setup/CitadelSetupController.cs:161`

### GroundCombatAdmissionRegistry

- `Assets/Editor/AiAttackLaneTests.cs:712`
- `Assets/Editor/AiRaidActorCommitmentTests.cs:181,182`
- `Assets/Editor/AiV2ArchitecturalRegressionTests.cs:131,132,141,142,153,154,165,166,190,191`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.ActiveDefence.cs:188,190`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.Attack.cs:226,227,248,603,608`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.Raid.cs:238,252,564,567`
- `Assets/Scripts/Ai/V2/Missions/MissionAdmissionPolicy.cs:111`
- `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs:131`

### PreparationDeliveryMemory

- `Assets/Editor/AiAttackPreparationWitnessTests.cs:109,111,112,118`
- `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorgAnalyzer.cs:268`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.AggressionAdmission.cs:103`
- `Assets/Scripts/Ai/V2/Strategy/Demand/AggressionDemandEvaluator.Attack.cs:314`
- `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseA.cs:646`
- `Assets/Scripts/Setup/CitadelSetupController.cs:158`

### ReconIntelSnapshotRegistry

- `Assets/Editor/AiAviationMissionRegressionTests.cs:31,40,91`
- `Assets/Editor/AiReconAttackObservationTests.cs:25,95`
- `Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.cs:987,992`
- `Assets/Scripts/Ai/V2/Diagnostics/ReconAcceptanceAudit.cs:185`
- `Assets/Scripts/Ai/V2/Missions/GroundCombat/GroundCombatAirSupport.cs:90`
- `Assets/Scripts/Ai/V2/Recon/AirReconRouteCandidate.cs:676`
- `Assets/Scripts/Ai/V2/Recon/ReconAirStepPlanner.cs:301`
- `Assets/Scripts/Ai/V2/Recon/ReconConcurrencyPolicy.cs:58,109`
- `Assets/Scripts/Ai/V2/Recon/ReconGroundStepPlanner.cs:209,315`
- `Assets/Scripts/Ai/V2/State/AiReconMemory.cs:48,68`
- `Assets/Scripts/Ai/V2/Strategy/Desire/DesireEvaluators.cs:345,346,402`
- `Assets/Scripts/Ai/V2/Strategy/Objectives/ReconObjectiveEvaluator.cs:120,203,204,236,238,244,250,296,314,317,469,471,483,485`
- `Assets/Scripts/Ai/V2/Strategy/Objectives/ScoutObjectiveEvaluator.cs:83,84`

### ScoutTrailRegistry

- `Assets/Scripts/Ai/AiTurnController.cs:510`
- `Assets/Scripts/Ai/V2/Reaction/ReconReactionPolicy.cs:165`
- `Assets/Scripts/Ai/V2/Recon/ReconGroundStepPlanner.cs:349,351`
- `Assets/Scripts/Ai/V2/State/AiReconMemory.cs:53`
- `Assets/Scripts/Setup/CitadelSetupController.cs:160`

### AirSortieRegistry

- `Assets/Editor/AiAviationMissionRegressionTests.cs:29,38,168,304`
- `Assets/Editor/AiAviationSortieCycleTests.cs:35,46,408,409,432,450,455,1108`
- `Assets/Editor/AiReconAirLifecycleTests.cs:85,133`
- `Assets/Editor/AiReconRuleOwnershipTests.cs:165,210`
- `Assets/Editor/AiSecondaryBaseStrategicNodeTests.cs:504,610,626,643,655,670,707`
- `Assets/Editor/ArmyMovementLifecyclePlayModeTests.cs:48,49,81,82,475,476,486,487`
- `Assets/Editor/ArmyRemovalRegistryTests.cs:31,44,155,156,158,159,160,161`
- `Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.Attack.cs:448,499`
- `Assets/Scripts/Ai/V2/Execution/GroundCombatLegStep.cs:82,92,155,218`
- `Assets/Scripts/Ai/V2/Execution/ReconAirExecutor.cs:458,475,484,546,656,657,664,743,747,760,763,779`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.ActiveDefence.cs:212`
- `Assets/Scripts/Ai/V2/Missions/GroundCombat/GroundCombatAirSupport.Projection.cs:134`
- `Assets/Scripts/Ai/V2/Missions/GroundCombat/GroundCombatAirSupport.cs:203,359,370,386,401,403,407,409,422,464`
- `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.Air.cs:62`
- `Assets/Scripts/Ai/V2/Recon/AiAirSortiePlanner.cs:511,518,544,737,766`
- `Assets/Scripts/Ai/V2/Recon/ReconAirCapacityPolicy.cs:132`
- `Assets/Scripts/Ai/V2/Recon/ReconAirReservation.cs:129,152`
- `Assets/Scripts/Ai/V2/State/AiReconMemory.cs:50`
- `Assets/Scripts/Ai/V2/Strategy/AviationRebasePlanner.cs:63,73,375,389,405,471,480`
- `Assets/Scripts/Setup/CitadelSetupController.cs:154`

### AviationObligationStallRegistry

- `Assets/Editor/AiReconAuditBugTests.cs:322,323,325,326,327,329,330,331`
- `Assets/Scripts/Ai/V2/Execution/ReconAirExecutor.cs:244`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:757,818`
- `Assets/Scripts/Ai/V2/Strategy/AviationRebasePlanner.cs:77`
- `Assets/Scripts/Setup/CitadelSetupController.cs:155`

### DevelopmentInvestmentGate

- `Assets/Editor/AiGenerationSourceIdentityTests.cs:27,52,61`
- `Assets/Scripts/Ai/V2/Materialization/GenerationSource.cs:91`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.DevelopmentAdmission.cs:42`
- `Assets/Scripts/Ai/V2/Strategy/Demand/AggressionDemandEvaluator.Attack.cs:323`
- `Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.cs:33`
- `Assets/Scripts/Ai/V2/Strategy/Demand/GroundCombatDemandPolicy.cs:169`
- `Assets/Scripts/Ai/V2/Strategy/Objectives/DevelopmentOpportunityEvaluator.cs:231,468,613`
- `Assets/Scripts/Setup/CitadelSetupController.cs:162`

### TurnResourceBook

- `Assets/Editor/AiDevelopmentReadmissionTests.cs:28`
- `Assets/Editor/AiEconomyDecisionTests.cs:2037,3935,3936,3937`
- `Assets/Editor/AiRawResourceReadRatchetTests.cs:74`
- `Assets/Editor/AiReservationInvariantsTests.cs:63`
- `Assets/Editor/AiStrategicSpendabilityApTests.cs:241,243,585`
- `Assets/Editor/AiTurnResourceBookTests.cs:26,50,51,72,73,161,167,204,205,207,213,214,222,223,232,233,242,248,261,271,284,290,292,294,312`
- `Assets/Editor/ArmyRemovalRegistryTests.cs:180`
- `Assets/Scripts/Ai/V2/Allocation/ResourceAllocator.cs:566,571,1004,1006`
- `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Development.cs:222`
- `Assets/Scripts/Ai/V2/Housekeeping/HousekeepingManager.cs:49`
- `Assets/Scripts/Ai/V2/Missions/AttackBaseRefitPolicy.cs:209`
- `Assets/Scripts/Ai/V2/State/ReservationInvariants.cs:138,170`
- `Assets/Scripts/Ai/V2/State/StrategicSpendability.cs:264,269`
- `Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs:282`
- `Assets/Scripts/Ai/V2/Strategy/PhaseA/MaterializationPortfolioSolver.cs:106,126,212`

### PhaseAApBudget

- `Assets/Editor/AiAttackBaseRefitTests.cs:170`
- `Assets/Editor/AiRawResourceReadRatchetTests.cs:70`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:195`
- `Assets/Scripts/Ai/V2/Reaction/ReactionRoundExecutor.cs:114`

### StrategicTempoBudget

- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:1414`
- `Assets/Scripts/Ai/V2/Reaction/ReactionMaterializationSolver.cs:58`
- `Assets/Scripts/Ai/V2/Reaction/ReactionOpportunityProbe.cs:110`
- `Assets/Scripts/Ai/V2/Strategy/HandReplenishPolicy.cs:45`
- `Assets/Scripts/Ai/V2/Strategy/PhaseB/TempoActionExecutor.cs:60,123`
- `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseA.cs:245,433,715,766`
- `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseB.cs:77`
- `Assets/Scripts/Setup/CitadelSetupController.cs:176`

### OperationContinuationWindow

- `Assets/Editor/AiAttackBaseRefitTests.cs:27,37,148`
- `Assets/Editor/AiStrategicSpendabilityApTests.cs:22,92,95,141,191`
- `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Self.cs:119`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.Attack.Preparation.cs:61`
- `Assets/Scripts/Ai/V2/Missions/AttackBaseRefitPolicy.cs:181`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:133,1108,1317`
- `Assets/Scripts/Ai/V2/State/StrategicSpendability.cs:144,161`
- `Assets/Scripts/Setup/CitadelSetupController.cs:175`

### StrategicCapabilityLeaseRegistry

- `Assets/Editor/AiCollectorCapabilityDeliveryTests.cs:137`
- `Assets/Editor/AiEconomyDecisionTests.cs:3906`
- `Assets/Editor/AiHousekeepingMissionContractTests.cs:241,250`
- `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorgAnalyzer.cs:394,412`
- `Assets/Scripts/Ai/V2/Housekeeping/HousekeepingManager.cs:116`
- `Assets/Scripts/Ai/V2/Materialization/MaterializationCandidateBuilder.cs:121`
- `Assets/Scripts/Ai/V2/Strategy/PhaseA/CapabilityDeliveryEvaluator.cs:291,305,426`
- `Assets/Scripts/Setup/CitadelSetupController.cs:132,181`

## Unchanged bank invariant distinction

`AiReservationInvariantsTests.DeferredBuildSavingBeyondStock_IsNotACommittedViolation` proves that a deferred H/E/M/T saving claim can intentionally exceed current physical stock. Completion/reaction committed claims are checked against physical stock; spendable free is clamped. The refactor preserves this existing rule and does not impose `all reservations <= stock`, which would change gameplay. AP deferred reservation remains forbidden.
