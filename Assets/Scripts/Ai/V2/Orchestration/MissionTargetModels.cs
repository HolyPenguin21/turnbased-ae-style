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

    public struct EconomyMissionTarget
    {
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
        public int ProjectedActivationApCost;
        public int ProjectedMaxMovement;
    }

    // The *existing* hero, not a card-in-hand or an anonymous "operator" demand. This target
    // is persisted as a DevelopmentIntent and revalidated at every execution boundary.
    public struct DevelopmentMissionTarget
    {
        public HexCoord FacilityHex;
        public ResearchProductionMode Mode;
        public Game.Units.UnitData Hero;
        public string HeroKey;
        public int? SourceArmyId;
        public float IntrinsicValue;
    }

    // A Scout mission's focus. Explore -> a MapKnowledge.Frontier hex; Refresh -> a previously
    // observed hex whose frozen IntelAge is stale; Surveil -> a stale honest contact's last-known
    // hex (Contact non-null); AirSweep -> an AVIATION-ONLY observation pass whose FocusHex is the
    // strategic sweep anchor (enemy army concentration, else enemy citadel): the wing flies toward
    // it as deep as its refuel endurance allows and returns — never a ground job, never "met" by
    // simply seeing the anchor. The numeric identities remain Explore=0, Surveil=1, Refresh=2,
    // AirSweep=3.
    public enum ScoutTargetKind { Explore, Surveil, Refresh, AirSweep }

    // How hidden the mover must be by the time it reaches the risky leg. None -> any scout.
    // Required -> the mover must be hidden OR able to enter stealth first (a visible scout is not a
    // valid executor at all — parity with V1's hard exclusion). Preferred is reserved for a future
    // softer tier; step 4 never emits it.
    public enum StealthRequirement { None, Preferred, Required }

    public struct ScoutMissionTarget
    {
        public HexCoord FocusHex;
        public ScoutTargetKind Kind;
        public EnemyContactSnapshot Contact;   // non-null ONLY for Surveil

        public StealthRequirement Stealth;
        public float DetectionRisk;            // [0..1] — 0 unless the enemy can actually detect stealth here
    }
}
