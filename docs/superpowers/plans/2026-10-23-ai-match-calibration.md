# AI Match Behavior Calibration Implementation Plan

> Execute inline with superpowers:executing-plans. The user approved implementation of points 1–8 after discussing the design and point 6; no further design approval is pending.

**Goal:** Remove two demonstrated decision defects and calibrate existing AI decisions without new production classes, mission kinds, scoring systems or architectural layers.

**Architecture:** Keep the existing ownership tree in Assets/Scripts/Ai/V2/ARCHITECTURE.md. Change the canonical policy or fact producer and verify every downstream admission/execution reader; keep domain actions authoritative.

**Tech Stack:** Unity 6000.5.4f1, C#, NUnit EditMode tests. Unity/manual match runs belong to the user and do not block delivery.

**Spec:** Approved conversation and external analysis at /workspace/scratch/72613a674194/AI_Behavior_Calibration_3-8_2026-10-23.md. Points 1–2 are the Flee cycle and structurally useless Research preparation described in the preceding match report.

## Global Constraints

- Inspect current master, canonical owners, full call chains and neighboring consumers before each modification.
- Use existing production classes/methods. No new vertical layer or generic refactoring.
- Recon supplies knowledge; Economy enables opportunities; military need justifies Production.
- Use only honestly known enemy facts. Whole own deck is valid preparation knowledge.
- Manual tests and Unity runs are the user's responsibility; unavailable tooling is reported honestly.
- Do not touch unrelated catalog, scene, prefab, package or visual changes.
- Implementation does not authorize publishing to master; deliver a reviewable local branch/patch.

## Review Focus

- An escape endpoint must not hide a dangerous actual first step; holding must stop the executor without consuming movement.
- Applicability must include remaining-deck hosts, obey offered-card filters, and not require equipment hosts for deployable outputs.
- Garrison clocks must agree at the initial scan and live final checks; age must not depend on memory container.
- Closed mobilization permits only an already executable observed opportunity; no early gather or demand, and no weaker repeated admission.
- Defensive terrain must be counted once; arrival deadlines must use remaining movement and turn order.

## Task 1: Stop unsafe repeated Flee

Files: Reaction/ReconReactionPolicy.cs; existing AiReconGroundReactionRegressionTests.cs and AiReconEscapeMemoryTests.cs.
Consumes honest sightings and SafeStepPathing; ordinary movement retains ReconPatrolState.LastEscape. Strictly increasing emergency clearance needs no extra escape veto. Produces the same ReconReactionDecision.
- [x] Add regression for two opposing threats, including one outside the current flee radius, and unchanged reversal.
- [x] Score the executable first step against the same complete known strong-threat set; accept strict safety improvement, preserve changed-situation escape and safe detours.
- [x] Check executor hold behavior and ordinary scout step memory interaction.

## Task 2: Reject structurally unusable Development preparation

Files: Strategy/Objectives/DevelopmentOpportunityEvaluator.cs; existing Development preparation tests.
Consumes OfferedCards, canonical EquipmentSystem.FitsHost, own deployed/hand/deck hosts. Produces the same structural preparation verdict.
- [x] Cover no compatible host, host only in remaining deck, and deployable output.
- [x] Extend StructuralPreparationBlock used by preparation facts and selected site; retain intrinsic scoring and READY execution rules.
- [x] Verify operator claims and Phase-B exclusion use the same conclusion.

## Task 3: Calibrate Base value

Files: Foundation/AiConfigV2.TaskScore.cs.
Consumes existing Airfield and EconomicExpansionValue normalized facts. Produces the same TaskScore.
- [x] Set Airfield cap 8→4 and economic expansion cap 6→3.
- [x] Recheck logged score sensitivity and existing income/base admission behavior; do not add constant-mirroring tests.

## Task 4: Prioritize existing Hunt near own income

Files: Strategy/Objectives/ReconObjectiveEvaluator.cs; existing Recon objective tests.
Consumes existing executable ReconHuntOpportunities and Known buildings' collection facts.
- [x] Cover own productive extraction within economySiteThreatRadius, foreign/nonproductive/far structure.
- [x] In HuntAt raise relevance 0.7→1 only near known own productive extraction, retaining cost and combat restrictions.

## Task 5: Make garrison observation age consistent

Files: AiArmyRoles.cs and existing clock/context owners if required; existing AiGarrisonDefenceFloorTests.cs.
Consumes SeenTurn and actual current turn; produces the same scalar floor and spareable roster.
- [x] Cover equally old canonical/historical contacts; trace first-scan timing and move Observe before BuildSelf. Full Scan execution remains a Unity check.
- [x] Make both sources use one age convention and one current clock; preserve fresh observations and all defence constants.
- [x] Check Self, AttackForcePool, Demand, Economy donors, sorties, GarrisonRelief and Housekeeping final checks.

## Task 6: Unblock existing attack by opportunity

Files: Missions/AggressionMissionPlanner.Attack.cs, Missions/AttackSecondaryPolicy.cs; existing attack tests.
Consumes existing IsOpportunity and HasAssaultObservation. Produces ordinary Assault proposals.
- [x] Cover closed-mobilization ready decisive attack and rejection of stale/weak/unready plans.
- [x] Let direct assembly be assessed while closed; require the shared departure policy to enforce fresh decisive opportunity before start.
- [x] Keep fresh gather, preparation and unbound capability demand gated by mobilization; retain home safety and secondary surplus rules.

## Task 7: Align defensive bonus and arrival estimates

Files: Analysis/WorldAnalysis.Threat.cs, Strategy/Objectives/ActiveDefenceObjectiveEvaluator.cs, Missions/AttackMarchPolicy.cs; existing ActiveDefence/Attack policy tests.
Consumes canonical domain hex bonus and existing ArrivalEta. Produces the same Threat and March verdicts.
- [x] Cover positive/negative terrain, no double bonus, remaining MP and equal-deadline cases.
- [x] Freeze full own structure defence in StrategicAssetSnapshot; read it in HoldChanceAtAsset.
- [x] Replace geometric specific-mover deadlines with existing route arrival logic while respecting current-turn arrival and the already-at-home case.

## Task 8: Prioritize needed reconnaissance

Files: Strategy/Objectives/ReconObjectiveEvaluator.cs, Foundation/AiConfigV2.Recon.cs; existing AiReconAttackObservationTests.cs.
Consumes existing ObservationNeeds. Produces the same Explore TaskScore.
- [x] Cover unseen operational observation need and non-additive ruins relevance.
- [x] Give the needed Explore the same full StrategicRelevance as needed Refresh; use max with ruins.
- [x] Set reconRuinsRelevance 1→0.5, preserving InfoGain, staleness, concurrency and tactical movement policy.

## Verification and delivery

- Compare source ownership/call chains and all changed paths, preserve .meta and line endings.
- Attempt baseline and differential ai-verify when the required runtime is available. Do not accept a tool's empty error file as a successful build if the compiler never ran.
- Add meaningful existing-suite regressions even if they require the user's Unity runtime; report what actually ran.
- Perform an independent whole-change review, then inspect its findings against adjacent mechanics.
- Deliver the branch changes and a portable patch; no merge or push unless separately authorized.
