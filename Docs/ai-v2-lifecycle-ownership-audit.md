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

## Continuation ownership audit (after 595cf945)

Master was re-read through the connector and remained 706b1bbd; feature HEAD was 595cf945. The table above remains a baseline callsite/scope audit. Current additions:

| Holder | Scope | Source / writer | Readers / reset |
|---|---|---|---|
| MissionLeaseBook session actor table | turn; reconciled at existing decision boundaries | MissionActorPolicy feeds original valid roles; same pass claims remain temporary | ActorCommitments; retirement drops only matching operation, rekey transfers, session close clears |
| MissionLease read handle | turn-bound view, no storage | book For(existing MissionIntentKey) | domain/tests; closed book rejects old handles |
| ReservationOwner | immutable identity projection | canonical MissionIntentKey factory or existing reaction pass token | ledger/bank diagnostics; no new operation id |
| ledger Identity | turn resource row value | production mutation API is MissionLeaseBook; storage is original ledger | TurnResourceBook same token projection; exact original expiry/bank rules |
| WorldDelta synchronous transaction scope | synchronous canonical transaction only | nested committed facts are staged; authoritative outer commit/rollback decides | one process revision policy; Dispose/rollback drops pending facts, no coroutine yield |

The generic lease book contains no Attack/Raid/etc. role validation or lifecycle switch. All production ledger mutation callsites have moved behind it; match-start ClearAll remains a storage reset adapter. Detached FromIntents views are derived pass projections, not session lifecycle owners. Scope/claim tests cover player/turn isolation, same-turn release, old handle closure, another operation surviving release, legacy sub-leg completion and in-place Recon rekey.

Separate bank pass: unchanged physical-stock/free/authority math, costs and caps; typed writes preserve the exact Economy tokens. Owner-scoped completion→deferred and deferred→completion, repeat reservation, no deferred AP, abort, independent second owner and stale-turn write are tested. Deferred saving above physical stock remains deliberately unchanged.

Separate cache pass: no cache/key/dirtiness rule added. FoundBase previously allowed child CardPlay stamping before canonical commit and unconditionally stamped a rolled-back failure; synchronous staging fixes that transaction boundary. Its dirty facts still come from the original observation pass. Existing Raid/Attack handoff flags are unchanged when combining commit/publication. TaskExecutor/air aggregate-vs-inner stamping and native next-read acceptance remain open; do not claim global exactly-once or full E2E parity.

### Typed result continuation

MissionStepResult owns a per-step typed payload dictionary, not a world cache or ownership registry. Normalization writes its facts; domain continuity reads them. Legacy MissionTurnOutcome properties contain no second storage. Missing read access is non-mutating; nullable identity stays null and actor id 0 remains legal. Extra execution evidence (Recon durable continuation, Economy delivery/holding, Development delivery and Attack air strike) is retained explicitly.

Domain execution classification uses immutable code bindings to the existing Continuity partials. The same common interruption and provisioning semantics remain. Economy's live goal predicate moved verbatim to EconomyLifecycleState; ledger now owns neither that rule nor a domain field schema. Generic MissionStepResult<T> transport and operational ingress are tested. Pipeline now calls session.Settle(MissionStepResult); internal legacy domain paths read a noncopying MissionTurnOutcome view sharing one fact record/payload dictionary. Full mission extension without transition-policy adaptation is not claimed. The 5,442-combination golden matrix and all baseline-passing managed tests remain unchanged.

### Child revision receipt audit

TaskExecutor previously always re-stamped any productive result, including already stamped ReconAir child actions. The existing `ExecutionResult.StateVersionAfter >= 0` receipt now prevents that duplicate aggregate stamp. Raid/Attack terminal handoff records the receipt returned by the same existing masked Apply. Actual baseline/current reflection proof: child+aggregate revision delta 2 → 1. No new revision counter or result boolean was introduced.

Ground capture and air-support paths may continue moving after a child commit. They must not claim a terminal receipt for that intermediate mutation; their producer boundaries remain an explicit transaction migration item. Synchronous transaction scopes must not cross coroutine yields.

The six live objective checks previously embedded in the normalization coordinator now bind to existing domain Continuity partials; the exact actor/phase/target predicates and fog-of-war restrictions are retained.

### Common ingress checkpoint

`MissionStepResult` now owns the existing common facts, not just disposition/cost/actor. Its private record is shared only by explicit compatibility views. `MissionTurnOutcome` has no common storage; outcome flags and domain properties remain projections. Pipeline consumes the common ledger result and session settlement API. The sixteen original common fact defaults remain unchanged. Fifteen new tests prove shared reference facts, six generic result transitions, another operation surviving cleanup, six-domain step parity and stale-session refusal. No new persistent state or cache was added.

### Domain transition checkpoint on updated master

Master `13210b16fc1ed6a42facc17ec3cc55d06aa83afb` was fetched through the connector. No AI path overlaps the upstream UI/scene/badge changes; feature-only merge `a69c7392` preserves the full master tree and assets. A separate frozen baseline compile/test run retains 984/525 across 1,509 managed cases; the original 706b1bbd baseline was not replaced.

ReconcileOutcome now uses the common disposition and ordered callback groups: progress facts → side leg → completion → terminal recovery/retirement → no-progress → existing advancement/fresh creation. Composition ordering preserves legacy payload precedence, including fixtures lacking MissionKind. Raid/Attack/Recon completion and Economy return/recovery remain domain decisions; completed leg does not retire an active campaign. Intent retirement still releases through the sole MissionLeaseBook owner.

Fourteen moved domain/helper methods are byte-identical. The 5,760-transition fingerprint was captured from the pre-extraction assembly before compiling the modified policies and remains identical. Separate all-domain lease tests retain other operation rows. No eligibility, spending, dirty-mask, cache or revision policy was copied or changed. AdvanceIntent/ResolveActive role coordination and remaining committed-action transaction boundaries remain explicit migration items.

### Domain advancement checkpoint

AdvanceIntent now contains shared accounting/suspension/stall/reap ordering and delegates mover identity and typed domain fact writes to ordered policies. Raid reads the frozen leg/primary/support facts, Attack calls GroundCombatLegs.IsAttackSupportLeg, Economy/Development preserve their existing pins, and Recon uses its existing exclusivity owner. Payload application keeps Scout → Attack → Raid → Economy ordering. Economy owns repeated capability-failure suppression. Two former capability-failure age exceptions share one domain composition predicate; existing eligibility owners are called, not copied.

The original transition fingerprint is unchanged. An additional 5,760-case pre-extraction fingerprint (`FF74620661454EAA6C80DAF01394D39EB1E5ED37F9B3C9FA875F92FAA5DFBECF`) covers NoMoverExists/MoverContended, pool exhaustion, stalled/aged operations, moverless scouts and collectors. Explicit frozen-role tests cover Raid handoff/air support and Attack support without primary reassignment; pinning tests cover Economy/Development retry. No bank, revision, cache, threshold or cost code changed.


## Continuation: resolution, Housekeeping and pass ownership

Master rechecked through the connector: `13210b16fc1ed6a42facc17ec3cc55d06aa83afb`; feature checkpoint before these changes: `5369c7afd18afc117105bad3bf71d553ea816e10`. No overlapping upstream AI changes.

| Concern | Authoritative owner / current evidence |
|---|---|
| Active intent resolution | existing domain Continuity partials; common ResolveActive preserves preparer → resolver → rekey/remove → sort → finalizer order; six branch bodies match mechanically |
| Result payload capture | same domain partials; common normalization owns common facts/status; original 5,442-case fingerprint unchanged |
| Tentative provisioning actors | pass lease book under AiTurnSession; ISet adapter has no second claim storage; pass close and abandoned-turn close reject stale writes/read access |
| Support-leg release | session projects only that operation through MissionActorPolicy; another operation and anonymous pass claim remain; resource hold stays if domain retains operation |
| Housekeeping mutation permission | normalized ArmyMutationContract; Attack domain supplies live roster/deployment/release-body readers; Analyzer and Executor no longer interpret Attack/Recon intent registries |
| Revision | WorldDeltaLifecycle only counter; ground/capture/strike/return/rebase actions update existing receipt; TaskExecutor/phase/Pipeline avoid outer duplicate stamps |
| Generated Development output | one persistent DevelopmentLifecycleState store; existing uniqueness/age/reconciliation rules intentionally survive a completed delivery leg |

Separate bottom-up bank review: physical stock, TurnResourceBook, StrategicSpendability, allocator budgets, canonical materialization/provisioning/execution spending and completion↔deferred replacement conditions are unchanged. A production call-site search finds direct reservation mutations only inside MissionLeaseBook. Side-leg ownership tests retain another operation's rows; no operation identity/token encoding or reservation amount changed. Deferred saving protection may exceed physical stock by the original design; it is not silently rebalanced.

Separate cache review: no cache, key or new dirty mask was added. AiMapMemory and pathing revision remain their existing source of truth; route/combat/Recon intel and derived capability readers retain the existing keys/refresh order. Changed action boundaries use per-action receipts; observations publish the same reason-scoped facts without another bump. The managed raw compile still reports exactly the same three UI/audio reference-stub errors; diagnostic copies alone adapt those calls. Native next-read/cache acceptance remains outstanding.

Current managed gate: 1,643 cases, 1,118 passed / 525 failed; all 134 new cases pass; no original passing case disappeared/regressed; first error line of every baseline failure unchanged. Neither a failed baseline nor an omitted native coroutine is counted as passing. No PR workflow run was available on the preceding feature checkpoint; no full Unity/E2E result is claimed.


### Further master sync and canonical Housekeeping revision

Master advanced to `27856c202b118ed4ff46de00d499c0f1a6184c4a` during final checks. Its only changes are UI prefab/scene/texture assets, with no C# source changes. Feature merge `e89ef147` overlays all 14 real Git tree entries (including two deletions) without reconstructing binary files; every upstream entry and every changed AI blob was verified. Master is unchanged by this work.

The canonical Housekeeping executor had no revision request after successful roster mutation. A focused test runs two real commander reorders and a third rejected no-op, preserving membership and original Applied=2/Failed=1 behavior. Before the change its revision delta is 0 (test failure); after the change it is 2. The four successful canonical branches now request WorldDeltaLifecycle.CommitMutation immediately after success. This adds no gameplay predicate, resource spend, actor ownership writer or dirty mask. Native transfer/swap/fold and subsequent combat-cache reads remain acceptance items.


## Final source delivery audit

Master rechecked through GitHub connector: `dee26a1d5d32ba1b1d4a95e09134a839aef8d16c`. Feature-only merge `bc3dab7a` preserves seven upstream entries, including binary prefab/scene changes; no overlapping AI path. Master is not merged by this task.

Final managed gate: 1,665 cases, 1,140 passed / 525 unchanged baseline failures; all 156 added cases pass. Comparing both original 706b1bbd and frozen pre-delivery feature assemblies finds no lost passing case, no new failure and no changed first baseline error line. The result, transition and aging fingerprints remain unchanged. Native UnityTest/TestCaseSource and gameplay E2E are delegated to the owner, not counted as passed.

Common retirement owns removal once; Economy retirement preparation owns only domain loan repayment. A hypothetical new kind requires no generic cleanup branch. Its initially colliding diagnostic owner token is replaced by a canonical serialization of all six MissionIntentKey fields; existing mission bank tokens remain unchanged.

Execution and common results share historical immutable WorldDelta receipts and revision receipt. Original observation conditions/order/dirty masks mechanically match the pre-delivery implementation. Result ingestion never replays publication. Stale turn publication is rejected before registry/revision writes. No new cache or mirrored dirty state exists.

Aviation formation commits its trace only after the guarded rollback path. Rebase records that receipt before querying actor survival. Three managed cases verify committed/missing/uncommitted receipt behavior without a registry actor; anti-air loss and actual formation rollback still require the native acceptance scenarios.

Production reservations are still mutated through MissionLeaseBook; no bank rule or cost changed. Original deferred saving protection can exceed physical stock and remains explicitly distinguished from committed spendable/coverage checks. Source delivery is not blocked on native acceptance; the exact remaining checks are in [ai-v2-lifecycle-unity-acceptance.md](ai-v2-lifecycle-unity-acceptance.md).

Delivery sync: master advanced to `55237f4896e457c5922ca2c95506cd312db39c0e` during publication. Feature-only merge `584dffbb` preserves all five changed entries (four UI/map/editor C# files and one prefab), with no AI overlap or deletion. Managed parity was rechecked after this sync.
