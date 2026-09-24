using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    public sealed class ProvisionedMission
    {
        public MissionProposal Mission;
        public StableMissionKey Key;
        public MissionKind Kind;
        public ScoutTargetKind ScoutKind;
        public int MoverArmyId;
        public HexCoord FocusHex;
        public HexCoord ExecutionHex;
        public int? TrackedArmyId;
        public int BaselineObservedTurn;
        // Single source of truth for this provisioned task's Raid target. RaidTargetArmyId below is
        // a read-only projection for existing non-Raid/logging readers — never a second settable copy.
        public RaidTargetRef RaidTarget;
        public int RaidTargetArmyId => RaidTarget.Kind == RaidTargetKind.NeutralArmy ? RaidTarget.ArmyId : 0;
        public HexCoord RaidLastKnownHex;
        public bool RaidTargetIsNeutral;
        // AGG-RAID §9 — which leg of the Raid this provisioned task is, and the concrete actors /
        // destination it was provisioned for. Execution reads these and does NOT re-decide any of
        // them (no target re-pick, no base re-pick, no strategic re-scoring).
        public RaidMissionPhase RaidPhase = RaidMissionPhase.Assault;
        public int? RaidPrimaryArmyId;
        public int? RaidSupportArmyId;
        public int? RaidAirSupportArmyId;
        public HexCoord? RaidAirSupportLandingHex;
        public HexCoord RaidDestinationHex;
        // Reinforcement only: the support army is already standing on the primary's hex, so this
        // step is the ATOMIC roster handoff (transfer / swap) and must perform no movement.
        public bool RaidHandoffReady;
        public RaidRefitAction RaidRefitAction;
        public ActiveDefenceMissionTarget ActiveDefenceTarget;
        // ATK §22 — the whole Attack leg in ONE frozen object (phase, actors, destination, site
        // facts). Deliberately not a spray of Attack* fields beside it: the mission target struct
        // already is the lane's transport, so Execution reads the same object Provisioning wrote.
        public AttackMissionTarget AttackTarget;
        // Reinforcement only: the support army is already standing on the primary's hex, so this
        // step is the ATOMIC roster handoff and must perform no movement.
        public bool AttackHandoffReady;
        public EconomyMissionTarget EconomyTarget;
        public DevelopmentMissionTarget DevelopmentTarget;
        public string ReservationOwner;
        public MissionIntentKey? EconomyLoanSource;
        public ResourceVector ClaimedPhysical;
        // AP still needed for EXECUTION to carry this mission out (e.g. Economy travel/build cost).
        public float ClaimedAp;
        // Economy garrison-extraction never mutates during Provisioning: Provisioning only
        // estimates and defers, Execution spends for real inside its own beforeStep/afterStep
        // window (see MaterializeEconomyGarrisonBuilder in TaskExecutor.cs).
        //
        // >= 0 marks this ProvisionedMission as NOT YET a real mover: MoverArmyId is a synthetic
        // negative id (SyntheticGarrisonExtractionActorId), and this field names the garrison
        // TaskExecutor.RunEconomyStep must extract a hero from — the same pattern as AirLaunch
        // (ScoutExecutorKind.AirLaunch's synthetic ActorKey + AirfieldHex/LaunchSubset below
        // materialize inside ReconAirExecutor). -1 (default) means MoverArmyId is already a real,
        // resolvable army.
        public int EconomyExtractionGarrisonArmyId = -1;
        // The EXACT resolved plan (tier/hero/container/AP) Provisioning chose, pinned so Execution
        // materializes precisely that plan instead of re-running ResolveGarrisonExtractionCandidate
        // with weaker inputs (commitments:null, session:null), which could legally pick a different
        // — or already-claimed — actor. Default (Tier == None) whenever
        // EconomyExtractionGarrisonArmyId is -1 (nothing deferred).
        internal ProvisioningManager.GarrisonExtractionCandidate EconomyExtractionPlan;
        // The FULL preparation decision (composition, donor, real AP, resource stage cost)
        // PlanEconomyCompletion computed against a read-only preview of the not-yet-real container.
        // Execution applies this pinned plan directly — see
        // TaskExecutor.MaterializeEconomyGarrisonBuilder — never re-deriving it. Default (Feasible
        // == false) whenever EconomyExtractionGarrisonArmyId is -1.
        internal ProvisioningManager.EconomyCompletionPlan EconomyExtractionPreparation;
        // True whenever EconomyExtractionPreparation has been pinned by Provisioning but not yet
        // APPLIED — for a garrison-extraction candidate (always, alongside
        // EconomyExtractionGarrisonArmyId >= 0) AND for a direct-army candidate whose hero is
        // already real but whose composition change and/or donor-loan suspend is left for Execution
        // to apply. False (with EconomyExtractionGarrisonArmyId == -1) means the hero's roster is
        // already exactly right — nothing to defer, straight to movement.
        internal bool EconomyPreparationPending;
        // RECON-AIR-01 — the REAL Energy this mission's bound actor needs to activate (0 for Ground,
        // which never spends Energy to activate). Folded into ClaimedPhysical.Energy so it flows
        // through the SAME generic ResourceAllocator accounting AP already uses (RegisterProvisionSuccess).
        public float ClaimedEnergy;
        public bool StealthApReserved;
        // State version after provisioning completed. In the current batch adapter several
        // provisioned missions may coexist; the future selected-only loop executes immediately
        // and can require this version to still be current before issuing a gameplay command.
        public int PlannedAtStateVersion = -1;
        // AI-RECON-02 — this Scout mission's requirement is a stealthy one (StealthRequirement.
        // Required OR a non-zero DetectionRisk). Flows Requirement -> Mission -> Intent so the
        // durable ScoutIntent knows an active lane is a stealth lane a generic scout can't cover.
        public bool RequiresStealth;
        // Round 4 — which executor this Scout mission is bound to. Ground (default) is executed by
        // ReconGroundExecutor through TaskExecutor, exactly as before. AirExisting/AirLaunch are
        // executed by ReconAirExecutor (the orchestrator routes provisioned Scout missions to the
        // right executor by this tag BEFORE calling TaskExecutor.Execute — see AiStrategyV2Pipeline).
        public ScoutExecutorKind ExecutorKind = ScoutExecutorKind.Ground;
        public HexCoord AirfieldHex;                       // AirLaunch only
        public System.Collections.Generic.List<Game.Units.UnitData> LaunchSubset; // AirLaunch only
    }
}
