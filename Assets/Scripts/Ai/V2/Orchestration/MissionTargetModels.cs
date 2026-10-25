using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

using Game.Cards;

namespace Game.Ai.V2
{
    // Stage model types for the V2 pipeline — design record: AiStrategyV2Pipeline.cs file header.

    // Concrete mission kinds. Each maps to a V2 Task builder in TaskExecutor. Was a bare string
    // until build-order step 4 — typed now, before anything downstream depends on the spelling.
    // ATK §22 — Attack is a full peer mission kind, not a Phase-B tempo action. It shares the
    // Aggression desire axis with Raid and ActiveDefence (there is deliberately no DesireAxis.Attack)
    // and the one ground-combat kernel; only the semantics of its target are its own.
    public enum MissionKind { Scout, Raid, ActiveDefence, Economy, Development, Attack }

    public enum EconomyTaskKind
    {
        BuildExtraction,
        FoundBase,
        MobileCollection,
        ReturnCollector,
        ReturnBuilder,
    }

    public struct EconomyMissionTarget : IMissionTarget
    {
        MissionKind IMissionTarget.Kind => MissionKind.Economy;
        public MissionIntentKey IntentKey() => MissionIntentKey.ForEconomy(Kind, ObjectiveNumber, TargetHex);
        public StableMissionKey StableKey() => StableMissionKey.ForEconomy(Kind, ObjectiveNumber, TargetHex);
        private int ObjectiveNumber =>
            MissionIntentKey.EconomyObjectiveId(Kind, BuilderArmyId, CollectorArmyId, ResourceType);

        public EconomyTaskKind Kind;
        public HexCoord TargetHex;
        public ResourceType? ResourceType;
        public string ObjectiveId;
        public int? BuilderArmyId;
        public int? CollectorArmyId;
        public int? CollectorSourceArmyId;
        public int ExpectedMarginalYield;
        public HexCoord? SafeReturnHex;
        public CardData BuildCard;
        public ResourceCost BuildResourceCost;
        public float BuildApCost;
        public float BuildValue;
        public float MinimumFollowupAp;
        public IReadOnlyList<EconomyBuilderRouteSnapshot> BuilderRoutes;
    }

    // The *existing* hero, not a card-in-hand or an anonymous "operator" demand. This target
    // is persisted as a DevelopmentIntent and revalidated at every execution boundary.
    public struct DevelopmentMissionTarget : IMissionTarget
    {
        MissionKind IMissionTarget.Kind => MissionKind.Development;
        public MissionIntentKey IntentKey() => MissionIntentKey.ForDevelopment(Mode, FacilityHex);
        public StableMissionKey StableKey() => new StableMissionKey(MissionKind.Development, (int)Mode, 0, FacilityHex.Q, FacilityHex.R);

        public HexCoord FacilityHex;
        public ResearchProductionMode Mode;
        public Game.Units.UnitData Hero;
        public string HeroKey;
        public int? SourceArmyId;
        public float IntrinsicValue;
    }

    // A Scout mission's focus. Explore -> a MapKnowledge.Frontier hex; Refresh -> a previously
    // observed hex whose frozen IntelAge is stale; AirSweep -> an AVIATION-ONLY observation pass whose FocusHex is the
    // strategic sweep anchor (enemy army concentration, else enemy citadel): the wing flies toward
    // it as deep as its refuel endurance allows and returns — never a ground job, never "met" by
    // simply seeing the anchor. The numeric identities remain Explore=0, Refresh=2, AirSweep=3.
    // Hunt (2026-10-22) — a solo Recce strikes a visible enemy scouting force it still reaches this
    // turn (ReconReactionPolicy.CanHuntAt); a one-shot local action like CaptureStructure.
    public enum ScoutTargetKind { Explore = 0, Refresh = 2, AirSweep = 3, CaptureStructure = 4, Hunt = 5, DisruptStructure = 6 }

    // How hidden the mover must be by the time it reaches the risky leg. None -> any scout.
    // Required -> the mover must be hidden OR able to enter stealth first (a visible scout is not a
    // valid executor at all — parity with V1's hard exclusion). Preferred is reserved for a future
    // softer tier; step 4 never emits it.
    public enum StealthRequirement { None, Preferred, Required }

    public struct ScoutMissionTarget : IMissionTarget
    {
        MissionKind IMissionTarget.Kind => MissionKind.Scout;
        public MissionIntentKey IntentKey() => MissionIntentKey.ForScout(Kind, FocusHex);
        public StableMissionKey StableKey() => Kind == ScoutTargetKind.AirSweep
            ? new StableMissionKey(MissionKind.Scout, (int)Kind, 0, 0, 0)
            : new StableMissionKey(MissionKind.Scout, (int)Kind, 0, FocusHex.Q, FocusHex.R);

        public HexCoord FocusHex;
        public ScoutTargetKind Kind;

        public StealthRequirement Stealth;
        public float DetectionRisk;            // [0..1] — 0 unless the enemy can actually detect stealth here

        public bool NeedsStealth => ReconScoutKinds.NeedsStealth(Stealth, DetectionRisk);
    }
}
