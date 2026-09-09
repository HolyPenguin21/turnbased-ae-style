using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  WORLD SNAPSHOT  (Strategy V2 build-order step 2)
    // ===========================================================================================
    //  The single shared world scan. WorldAnalysis.Scan builds one of these once at the top of
    //  Pipeline.RunTurn; every stage after it (StrategyLayer, MissionLayer, ResourceAllocator,
    //  ...) reads ONLY this object and never touches raw game state again. Treat every field as
    //  IMMUTABLE once Scan returns — nothing downstream writes to it.
    //
    //  LAYERS
    //    Self        — this player's own state. No fog of war applies (it's ours).
    //    Known       — HONEST, fog-of-war-respecting. Sourced purely from AiMapMemory +
    //                  VisionSystem. This is what a fair player could know.
    //    TrueWorld   — CHEAT. Direct registry reads, hidden units included, enemy incomes/
    //                  stockpiles included. Downstream code uses this ONLY where cheating is
    //                  sanctioned (the project owner's explicit calls). Kept in its own layer so
    //                  "did this decision cheat" is always answerable by which field it read.
    //    MapKnowledge— fog / frontier / how much of the board is understood.
    //    Economy     — EconomyStanding: continuous, RELATIVE-to-the-field economic health.
    //    Threat      — ThreatModel: Enemy x Asset pressure, one model serving Defence/Recon/
    //                  part of Aggression. The cheat/honest boundary here is a TYPE invariant
    //                  (see EnemyContactSnapshot), not a comment convention.
    // ===========================================================================================
    public sealed class WorldSnapshot
    {
        public int TurnNumber;

        public SelfSnapshot Self;
        public KnownSnapshot Known;
        public TrueWorldSnapshot TrueWorld;
        public MapKnowledgeSnapshot MapKnowledge;
        public EconomyStanding Economy;
        public ThreatModel Threat;
        // Development capability — the ONE detect of "can I do Research/Production, and on what".
        // Read by BOTH the radar DevelopmentEvaluator (summary -> desire) and the
        // DevelopmentOpportunityEvaluator (per-offering EV). Refreshed with Self on every scan.
        public DevelopmentReadiness Development;
    }

    // =======================================================================================
    //  DEVELOPMENT READINESS  (Research / Production capability, snapshot-pure)
    // =======================================================================================
    //  A facility the player owns whose building carries the mode's Facility ability, plus the
    //  qualifying Hero on that hex if any. `Contested` (an enemy on the hex) is recorded but is
    //  NOT a scoring/desire gate — it only blocks EXECUTION for the turn (Phase A precondition).
    public struct DevelopmentFacility
    {
        public HexCoord Hex;
        public ResearchProductionMode Mode;
        public bool HasHero;
        public bool Contested;          // enemy on the hex — execution-blocked this turn only
        public int HeroFate;
        public int HeroCommandRating;
    }

    // One catalog card + exact operator a facility could attempt this turn. Affordability and
    // facility/operator structure are filtered; contested status is retained for execution-time
    // revalidation. SuccessChance is a soft ranking input, never a hard threshold.
    public struct DevelopmentOffering
    {
        public HexCoord FacilityHex;
        public ResearchProductionMode Mode;
        public CardDefinition Card;
        public float SuccessChance;
        public bool ProducesEquipment;
        public ResourceBundle StakeCost;   // card.resourceCost — spent whether the Challenge wins or loses
        public GenerationStep Generation;  // exact facility/mode/operator/card candidate
    }

    public sealed class DevelopmentReadiness
    {
        public IReadOnlyList<DevelopmentFacility> Facilities = System.Array.Empty<DevelopmentFacility>();
        public IReadOnlyList<DevelopmentOffering> Offerings = System.Array.Empty<DevelopmentOffering>();

        public bool AnyFacilityWithHero;   // a facility exists AND carries a qualifying hero (execution-ready)
        public float BestSuccessChance;    // max p over Offerings (0 if none)
        public float SurplusFraction;      // [0..1] resource headroom above the reservation floors
        public int UpgradeTargetCount;     // rough count of own units / hand Unit cards worth improving

        // --- staging signals (radar is no longer gated on facility+hero; DemandLayer stages them) --
        public bool AnyOperatorlessFacility; // a built facility with no qualifying hero and no enemy on the hex
        public bool ResearcherCardInHand;    // hand holds a Hero card granting the Researcher role ability
        public bool AssemblerCardInHand;     // hand holds a Hero card granting the Assembler role ability
        // "there is a plausible path to doing Development": a staffed facility, any built facility (a
        // hero can still arrive), or a Research/Production Facility card in hand to build one. Damps
        // the LATENT desire term to zero in the pure opening when none of that is true.
        public bool DevPathViable;
    }

    // --- Four stockpiled resources as one value. Index order matches ResourceType.
    public struct ResourceBundle
    {
        public float Human, Energy, Materials, Tech;

        public float Sum => Human + Energy + Materials + Tech;

        public float Get(ResourceType t)
        {
            switch (t)
            {
                case ResourceType.Human: return Human;
                case ResourceType.Energy: return Energy;
                case ResourceType.Materials: return Materials;
                default: return Tech;
            }
        }

        public void Add(ResourceType t, float v)
        {
            switch (t)
            {
                case ResourceType.Human: Human += v; break;
                case ResourceType.Energy: Energy += v; break;
                case ResourceType.Materials: Materials += v; break;
                default: Tech += v; break;
            }
        }

        public static readonly ResourceType[] All =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };
    }

    // --- A power-scored snapshot of one army (own or enemy). "Raw" Attack/DefenseSum are kept
    //     alongside the new EffectiveArmyPower purely so a caller that wants parity with a V1
    //     read still has it. IsHiddenFromUs is only ever true inside TrueWorld (a fog-honest
    //     Known sighting can't see a hidden army at all).
    public sealed class ArmySnapshot
    {
        public int ArmyId;
        public PlayerSetupData Owner;
        public HexCoord Hex;
        public bool IsGarrison;
        public bool IsPrison;
        public bool IsAir;
        public bool IsAirfield;
        public int MemberCount;
        public bool HasHero;
        // Best CommandRating among this army's hero members (0 = no hero). Sets a real forming
        // army's slot cap (ArmyData.ComputeCapacity) — the CombatOpportunityAnalyzer reads it to
        // size an assemblable raid roster the way EvaluateAssemblablePlan does. Own armies only in
        // practice (a fog/cheat-read enemy army never exposes its hero's rating).
        public int HeroCommandRating;
        // HasAntiAir is DUAL-USE: for an own army it means "fields an AntiAir counter unit"; for an
        // enemy contact it means "fields AA guns" (aviation-routing danger). Kept as its own field
        // because the aviation path (AirReconRouteCandidate) reads it independently.
        public bool HasAntiAir;
        // AI-MGR-01 P1.7 / review-r4 P1 ARCH — the strategic ROLES this army covers for standing-
        // force readiness, derived DYNAMICALLY from its members' abilities + stats via
        // StrategicEffectRegistry (not a card type / class flag). Own armies only in practice; an
        // enemy/cheat-read army leaves it None. Consumed by BaselineForceReadiness's coverage
        // vector. A new coverage role flows in with zero edits here.
        public RoleCoverage StrategicCoverage;
        // final closure §3.3 (army -> candidate aura direction) — the subset of this army's members'
        // registry-resolved effects that are ALLY AURAS (StrategicEffectContext.EligibleAllies), so
        // the effect-evaluation context can price the marginal buff a standing aura gives an INCOMING
        // candidate. Own armies only; empty until an aura row is added to StrategicEffectRegistry.
        internal IReadOnlyList<StrategicEffect> AllyAuraEffects = System.Array.Empty<StrategicEffect>();
        // final closure §3.3 P2 — ALL member profiles INCLUDING heroes, for the aura
        // candidate -> army direction (an aura buffing "Armored" must see an Armored HERO ally too;
        // `Members` above is deliberately non-hero for WorthIt combat estimates).
        public IReadOnlyList<WorthIt.DefenderProfile> MembersWithHeroes = System.Array.Empty<WorthIt.DefenderProfile>();
        public bool IsHiddenFromUs;

        public float AttackSum;             // WorthIt-style raw sum, non-hero
        public float DefenseSum;
        public float EffectiveArmyPower;    // AiPower — composition-adjusted ranking scalar
        public float CompositionQuality;    // [0..1] the multiplier's driver, kept for the "why" log
        public int MaxMovement;             // per-turn move budget, for rough ETA

        // ---- BATTLE-SLOT capacity (review-r4 P1 ARCH) — frozen from ArmyData so strategic effect
        //      scoring reads occupancy from the SAME snapshot as everything else, never live state.
        public int Capacity;                // ArmyData.Capacity — nominal battle-member cap
        public int OccupiedBattleSlots;     // members currently occupying slots (heroes included)
        public int FreeBattleSlots => System.Math.Max(0, Capacity - OccupiedBattleSlots);

        // ---- OPERATIONAL state (2026-08-29, build-order step 4) ----------------------------
        // Frozen here so the Recon mission planner / ScoutCostModel can size a mover's cost for
        // THIS allocation cycle without a fresh live read downstream. Only meaningful for
        // Self.Armies — an enemy/cheat-read army's activation/movement state is NOT knowable and
        // must never feed a strategic decision (it is populated from ArmyData all the same, but
        // treat it as noise for a non-own army).
        public int ActivationApCost;
        public int ActivationEnergyCost;   // game rule: non-zero ONLY for a real air army
        public bool HasActivatedThisTurn;
        public int CurrentMovement;        // MP left THIS turn (MaxMovement minus what's spent)
        public bool IsSoloRecce;           // AiArmyRoles.IsSoloRecce — the cheap dedicated scout shape
        // Frozen from the canonical AiArmyRoles.IsHeroLed predicate. Economy consumers add only
        // target/intent/route context; they never re-derive the structural actor shape.
        public bool IsMobileEconomyBuilder;
        // ARCH-02 §29/§59 — frozen at scan time from the live ArmyData so downstream layers
        // (RaidActorEligibility, CombatOpportunityAnalyzer, CapabilityInventory) read one snapshot
        // fact instead of re-deriving it from live ArmyRegistry state. Own armies only: a raid
        // mover is always our own. Structural = not prison/air/airfield/garrison/soloRecce/
        // solo-hero-awaiting-escort and has at least one member.
        public bool IsStructuralRaidActor;

        // Stealth capability (own armies only — a fog/cheat-read enemy army's is unknown). Lets a
        // stealth-Required Scout mission tell which movers can actually satisfy it: a mover is
        // capable if it is already hidden, or can still slip into stealth (CanEnterStealth) before
        // its first move.
        public bool IsHidden;
        public bool CanEnterStealth;       // StealthSystem.CanEnterStealth — !hidden AND stealthLevel > 0
        public int StealthLevel;           // best AbilityParams.GetStealthLevel among members (0 = none)

        // Vision reach for THIS army, EXACTLY VisionSystem's own formula
        // (GameConfig.armyVisionRadius + AbilityParams.GetBestRecceRadius). Own armies only in
        // practice — lets a Surveil vantage be chosen without a live VisionSystem read. Seeing a
        // hex from this range is NOT visiting it (only standing on a hex marks it visited).
        public int EffectiveVisionRadius;
        // Own-army Collect capability frozen with the rest of the actor. Analysis uses it only
        // to avoid pricing a Facility that would merely displace this army's existing collection.
        public ResourceBundle CollectionCapacity;

        // Per-combatant profiles for WorthIt's full-roster Monte Carlo / coverage checks.
        public IReadOnlyList<WorthIt.DefenderProfile> Members;
    }

    public sealed class BuildingSnapshot
    {
        public HexCoord Hex;
        public PlayerSetupData Owner;
        public bool IsStartingCitadel;
        public float Defense;
        public IReadOnlyCollection<string> FacilityAbilities;

        public bool HasFacilityAbility(string a) => FacilityAbilities != null && FacilityAbilities.Contains(a);
    }

    // =======================================================================================
    //  AP ACTION ECONOMY  (AI-MGR — Dynamic Strategic Effect Utility)
    // =======================================================================================
    //  Snapshot-pure facts the StrategicEffectRegistry needs to price a PlayerGlobal recurring-
    //  resource effect (ApBonus today) by its DYNAMIC marginal value instead of a flat bonus.
    //  Built once in WorldAnalysis.BuildSelf from live state; never re-read downstream.
    //  Deliberately NOT sourced from Initiative's InitiativeAnalyticsHistory ring buffer — that is
    //  walled off from WorldAnalysis by design; this is a fresh structural read of THIS turn.
    //  These are STRUCTURAL FACTS only — an UPPER BOUND on what could cost AP this turn. WorldAnalysis
    //  does NOT know which of the estimated demand components are legal/useful this turn (an
    //  unaffordable card, an "actionable" army with nothing worth doing, Development with no runnable
    //  opportunity, an air slot that yields no route). The authoritative owner-witnessed AP workload
    //  is assembled at evaluation time inside StrategicManager Phase A/B from the real candidate set
    //  (MaterializationPortfolioSolver.EstimateLegalApWorkload + committed movers + witnessed air);
    //  these facts are only the deliberately-discounted fallback (apStructuralDemandConfidence) used
    //  when no witnessed set exists (sims / bare snapshots) — see
    //  StrategicEffectRegistry.StructuralFallbackApDemand.
    public sealed class ApActionEconomySnapshot
    {
        public int BaseActionPoints;            // AP available this turn (post initiative roll + already-granted ApBonus)
        public int RecurringApSources;          // in-play own carriers of UnitAbilities.ApBonus (army members + bases + facilities)
        public int RecurringApPerTurn;          // RecurringApSources * UnitAbilities.ApBonusActionPointsPerSource
        public int UnactivatedActionableArmies; // own non-garrison/prison/air armies with members that have not acted yet
        public int ApCostingHandActions;        // hand cards whose play has a real AP cost

        // Raw structural demand components — an UPPER BOUND on what could cost AP this turn, never
        // proof any of it is useful. Only consumed by StructuralFallbackApDemand when no owner-
        // witnessed workload was assembled for the call.
        public float EstimatedArmyApDemand;        // Σ activation AP over own unactivated field armies
        public float EstimatedCardApDemand;        // Σ EffectivePlayApCost over AP-costing hand cards
        public float EstimatedDevelopmentApDemand; // apDevActionApProxy if a dev facility + operator are both present
        public float EstimatedAirApDemand;         // apAirSortieApProxy per structurally-available recon-air sortie/wing
    }

    // =======================================================================================
    //  SELF
    // =======================================================================================
    public sealed class SelfSnapshot
    {
        public HexCoord Citadel;
        public IReadOnlyList<HexCoord> BaseHexes;
        public IReadOnlyList<ArmySnapshot> Armies;

        public float FieldPower;
        public float GarrisonPower;
        public float TotalPower;

        // Best single stack the player could assemble RIGHT NOW from on-map units + hand + deck,
        // capped at the best available hero's CommandRating. Dynamic — loses a strong unit in a
        // battle and this drops. Comparison / "how strong am I" only; gates nothing.
        public float BestStackPotential;

        // Near-static ceiling: every military unit already on the map plus every unit card still
        // in hand or deck, composition-adjusted. "If we can't get stronger than this even in
        // theory, there is nothing left to wait for before striking the enemy citadel."
        public float TotalMilitaryPotential;

        public ResourceBundle Stockpile;
        public ResourceBundle PerTurnIncome;
        public int ActionPoints;

        public IReadOnlyList<CardData> Hand;
        public IReadOnlyList<CardDefinition> Deck;   // still-drawable pool (multiset, order unknown)
        public int HandCapacity;
        public bool HasFreeHandSlot;

        public bool HasDevFacility;
        public bool HasDevOperator;

        // AI-MGR — Dynamic Strategic Effect Utility. Snapshot-pure AP action-economy facts + the
        // count of non-hero bodies the AI could realistically field under a hero's Command (own
        // on-map non-hero units + hand Unit cards). Consumed by StrategicEffectRegistry (recurring
        // effect value) and StrategicCardEvaluator.HeroLeadershipFit (Command marginal capacity).
        public ApActionEconomySnapshot ApEconomy;
        public int DeployableCombatBodies;

        // AI-RECON-02 — air OBSERVATION capacity, from the shared ReconAirCapacityPolicy (the same
        // slot cap + launch-subset + AP/Energy gate ReconAirExecutor launches against):
        //   AirborneReconWings         — own wings already flying a durable ReconPatrolState; each is
        //                                an active observation lane the executor will continue.
        //   SpareAirObservationSorties — ADDITIONAL recon sorties launchable this turn, bounded by
        //                                MaxAirReconActorsPerTurn minus in-flight air slots AND by
        //                                one greedy pass over the shared post-reservation AP/Energy
        //                                budget (ready standalone wings, then storage launch
        //                                subsets — each accepted sortie consumes its own AP/Energy).
        // DemandLayer counts these so it does not build a redundant ground Scout for an observation
        // lane a helicopter already covers.
        public int AirborneReconWings;
        public int SpareAirObservationSorties;
    }

    // =======================================================================================
    //  KNOWN  (honest, fog-of-war)
    // =======================================================================================
    public sealed class KnownSnapshot
    {
        public IReadOnlyList<AiMapMemory.KnownEnemySighting> EnemySightings;
        public IReadOnlyList<AiMapMemory.KnownEnemySighting> NeutralSightings;
        public IReadOnlyList<AiMapMemory.KnownBuilding> Buildings;
        public IReadOnlyList<HexCoord> EventGuardHexes;
        public IReadOnlyList<KeyValuePair<HexCoord, ResourceType>> ResourceHexes;

        // Aggregates ported verbatim from AiStrategyDirector.Evaluate's own "shared readings".
        public float EnemyKnownStrength;
        public int NearestEnemyToBase;
        public float EnemyStrengthNearBases;
    }

    // =======================================================================================
    //  TRUE WORLD  (cheat — use only where sanctioned)
    // =======================================================================================
    public sealed class TrueWorldSnapshot
    {
        public IReadOnlyList<ArmySnapshot> EnemyArmies;    // non-own, non-neutral; hidden included; WITH Hex
        public IReadOnlyList<ArmySnapshot> NeutralArmies;
        public IReadOnlyList<BuildingSnapshot> AllBuildings;
        public IReadOnlyList<OpponentSnapshot> Opponents;
    }

    public sealed class OpponentSnapshot
    {
        public PlayerSetupData Player;
        public ResourceBundle PerTurnIncome;
        public ResourceBundle Stockpile;
        public int ArmyCount;
        public float ArmyPower;
    }

    // =======================================================================================
    //  MAP KNOWLEDGE
    // =======================================================================================
    public sealed class MapKnowledgeSnapshot
    {
        public int TotalHexes;
        public int VisitedHexes;
        public int VisibleHexes;
        public float UnknownFrac;          // 1 - visited/total — every dark hex, reachable or not

        // Real frontier (build-order step 4). A frontier hex is unvisited, on-map, "safe" (not in
        // a scout-danger zone, not within AiConfigV2.frontierEnemyAvoidRadius of a known
        // non-neutral sighting, no known neutral standing on it) and adjacent to REACHABLE visited
        // ground — visited+on-map+safe hexes flood-connected to at least one own base. Each entry
        // carries the two facts the Recon planner would otherwise re-scan the map for.
        public IReadOnlyList<FrontierHexSnapshot> Frontier;

        // Fraction of the WHOLE map (of TotalHexes) that is unvisited, on-map, safe AND sits in a
        // dark region flood-connected to the frontier — i.e. how much map is still there to be
        // discovered by walking. Replaces V1's flat reconUnreachableFloor: this is 0 exactly when
        // Frontier is empty, and a single mountain pass with 40% of the map behind it still reads
        // ~0.40 (a frontier-hex COUNT could not). Drives ReconExploration directly.
        public float ExplorableUnknownFrac;

        // Every on-map hex (== map.AllCoords). SurveilVantageSelector enumerates observation
        // candidates from this instead of re-reading the map.
        public IReadOnlyList<HexCoord> AllHexes;

        // The subset of AllHexes a ground scout must never be routed ONTO regardless of stealth:
        // an active scout-danger cooldown (off-map is implicit). Enemy PROXIMITY is deliberately
        // NOT here (it only annotates). Spec §19 — a known neutral physically on a hex is NO LONGER
        // folded in here: that block is actor-state-aware (a fully-hidden scout can pass) and lives
        // in NeutralOccupiedHexes below. Use IsBlockedForScout to combine the two correctly.
        public ISet<HexCoord> ScoutHardBlockedHexes;

        // Hexes with a known neutral force physically standing on them. A VISIBLE scout must not be
        // routed through these (it would be forced into an engagement); a fully-hidden scout can
        // pass per the authoritative Stealth/BattleInitiator rules. Kept separate from
        // ScoutHardBlockedHexes so the block can be applied conditionally.
        public ISet<HexCoord> NeutralOccupiedHexes;

        // Spec §19 — the single actor-state-aware "may this scout be routed onto/through `h`" test.
        // A stealth-capable mover ignores neutral occupancy; every mover still respects the true
        // hard blocks.
        public bool IsBlockedForScout(HexCoord h, bool stealthCapable) =>
            (ScoutHardBlockedHexes != null && ScoutHardBlockedHexes.Contains(h))
            || (!stealthCapable && NeutralOccupiedHexes != null && NeutralOccupiedHexes.Contains(h));

        // Every hex this player has ever stood on (VisionSystem.IsVisited). A byproduct of the
        // frontier scan, exposed so the step-7 continuity layer can tell whether a durable Explore
        // intent's focus hex is still unvisited without a live VisionSystem read.
        public ISet<HexCoord> VisitedHexSet;
    }

    // One frontier hex plus what the Recon planner needs to value it, computed once in the scan.
    // A frontier hex is NEVER dropped for enemy proximity — that is an annotation here, not a
    // filter (only HardBlocked reasons — a neutral on the hex, an active scout-danger zone, off
    // the map — keep a hex out of the frontier).
    public struct FrontierHexSnapshot
    {
        public HexCoord Hex;
        public int FreshNeighbors;            // on-map, unvisited neighbours this hex would open
        public int DistanceFromNearestBase;  // min hex distance to any Self.BaseHex (Citadel included)

        // A known non-neutral force sits within AiConfigV2.frontierEnemyExposureRadius. A visible
        // scout should not be routed here (V1 hard-excludes that); a stealth scout still can.
        public bool EnemyExposure;
        // At least one of those forces could actually roll a stealth-detection challenge on this
        // hex (KnownEnemySighting.CanDetectStealthAt) — even a hidden scout runs a real risk.
        public bool StealthDetectionRisk;
    }

    // =======================================================================================
    //  ECONOMY STANDING  (replaces V1's binary EcoMature + standalone IncomeBehindBonus)
    // =======================================================================================
    // Frozen structural route witness for one possible Economy builder. Intent ownership and
    // loan policy are applied later by Demand; Provisioning revalidates the selected route live.
    public struct EconomyBuilderRouteSnapshot
    {
        public int ArmyId;
        public int TravelCost;
        public bool IsOnTarget;
    }

    public struct EconomyExtractionOpportunity
    {
        public HexCoord Hex;
        public ResourceType ResourceType;
        public int EffectiveYield;
        public int CurrentBuildingCollection;
        public int MarginalIncomeGain;
        public float BaseNetworkSynergy;
        public float NearbyResourceClusterValue;
        public IReadOnlyList<EconomyBuilderRouteSnapshot> BuilderRoutes;
    }

    public struct EconomyBaseOpportunity
    {
        public HexCoord Hex;
        public float CapacityValue;
        public float NearbyResourceClusterValue;
        public float LogisticsValue;
        public bool ConvertsOwnedExtractionSite;
        public IReadOnlyList<EconomyBuilderRouteSnapshot> BuilderRoutes;
    }

    public sealed class EconomyStanding
    {
        // Frozen, fog-honest opportunity facts. Strategy scores these records; it never
        // reconstructs site legality or resource physics independently.
        public IReadOnlyList<EconomyExtractionOpportunity> ExtractionOpportunities =
            System.Array.Empty<EconomyExtractionOpportunity>();
        public IReadOnlyList<EconomyBaseOpportunity> BaseOpportunities =
            System.Array.Empty<EconomyBaseOpportunity>();

        // One entry per ResourceType, in ResourceBundle.All order.
        public IReadOnlyList<EconomyResourceStanding> PerType;

        // Aggregate resource appetite of the cards the AI can still play (hand + deck) —
        // variant (a): the absolute floor is derived from what the deck actually costs, not a
        // fixed threshold.
        public ResourceBundle DeckResourceNeed;
        public ResourceBundle HandResourceNeed;
        public ResourceBundle RemainingDeckResourceNeed;
        public ResourceBundle ReservedOperationalNeed;
        public ResourceBundle SpendableStockpile;

        // Sustainable per-turn income target by resource. Unlike DeckResourceNeed this is NOT
        // "pay the remaining deck within N turns": it is the larger of the field-median income
        // and this deck's average per-card resource cadence. Stockpile runway affects security,
        // not the target itself.
        public ResourceBundle IncomeTarget;

        public float RelativePressure;    // [-1..1]  <0 behind the field, >0 ahead
        public float BottleneckPressure;  // [0..1]   how bad the single worst resource is
        public float AbsFloor;            // [0..1]   income vs DeckResourceNeed/horizon, smoothstepped
        public float EconomicSecurity;    // [0..1]   blend(AbsFloor, RelativePressure, BottleneckPressure)
        public ResourceType MostDeficientResource;
        public float MaxDeficitScore;
        public float MeanDeficitScore;
        public bool HasActionableOpportunity;

        public bool IsExtractionActionable(HexCoord hex, ResourceType type) =>
            ExtractionOpportunities != null && ExtractionOpportunities.Any(x =>
                x.Hex.Equals(hex) && x.ResourceType == type
                && x.MarginalIncomeGain > AiConfigV2.allocatorSliceEpsilon);

        public float MarginalExtractionGainAt(HexCoord hex, ResourceType type) =>
            ExtractionOpportunities == null ? 0f : ExtractionOpportunities
                .Where(x => x.Hex.Equals(hex) && x.ResourceType == type)
                .Select(x => (float)x.MarginalIncomeGain).DefaultIfEmpty(0f).Max();

        // Single owner of the project's "income below target" predicate. Demand emission and the
        // post-step resource-site trigger both call this, so discovery cannot use a second,
        // drifting definition of a deficient resource.
        public bool IsIncomeDeficient(SelfSnapshot self, ResourceType type)
        {
            if (self == null)
                return true;
            float target = System.Math.Max(0f, IncomeTarget.Get(type));
            if (target <= AiConfigV2.allocatorSliceEpsilon)
                return false;
            return self.PerTurnIncome.Get(type) + AiConfigV2.allocatorSliceEpsilon < target;
        }

        // Pure per-resource model. WorldAnalysis owns input collection; keeping the formula here
        // makes the frozen snapshot directly testable and prevents demand/desire from re-scoring it.
        public static EconomyResourceStanding CalculateResource(ResourceType type, float ownIncome,
            float opponentMedianIncome, float handNeed, float remainingDeckNeed,
            float reservedOperationalNeed, float spendableStockpile, float starvationPressure)
        {
            float cardCadence = Mathf.Max(
                remainingDeckNeed / Mathf.Max(1f, AiConfigV2.economyDeckNeedHorizonTurns),
                handNeed / Mathf.Max(1f, AiConfigV2.economyHandPaydownHorizonTurns),
                reservedOperationalNeed / Mathf.Max(1f, AiConfigV2.economyOperationalPaydownHorizonTurns));
            float target = Mathf.Max(opponentMedianIncome, cardCadence);
            float incomeGap = Mathf.Clamp01((target - ownIncome) / Mathf.Max(target, 0.0001f));
            float relativeGap = Mathf.Clamp01((opponentMedianIncome - ownIncome)
                / Mathf.Max(opponentMedianIncome, 1f));
            float wanted = handNeed
                + remainingDeckNeed * AiConfigV2.economyDeckNeedDiscount
                + reservedOperationalNeed;
            float runway = Mathf.Clamp01((spendableStockpile
                    + ownIncome * AiConfigV2.economyRunwayHorizonTurns)
                / Mathf.Max(wanted, 1f));
            float operational = Mathf.Clamp01(reservedOperationalNeed
                / Mathf.Max(1f, spendableStockpile + ownIncome));
            float starvation = Mathf.Clamp01(starvationPressure);
            float deficit = Mathf.Clamp01(
                AiConfigV2.economyIncomeGapWeight * incomeGap
                + AiConfigV2.economyRelativeGapWeight * relativeGap
                + AiConfigV2.economyRunwayGapWeight * (1f - runway)
                + AiConfigV2.economyOperationalPressureWeight * operational
                + AiConfigV2.economyStarvationWeight * starvation);
            return new EconomyResourceStanding
            {
                Type = type,
                OwnIncome = ownIncome,
                OpponentMedianIncome = opponentMedianIncome,
                FieldMedianIncome = opponentMedianIncome,
                HandResourceNeed = handNeed,
                RemainingDeckResourceNeed = remainingDeckNeed,
                ReservedOperationalNeed = reservedOperationalNeed,
                SpendableStockpile = spendableStockpile,
                IncomeTarget = target,
                IncomeGap = incomeGap,
                RelativeIncomeGap = relativeGap,
                RunwayCoverage = runway,
                OperationalPressure = operational,
                StarvationPressure = starvation,
                DeficitScore = deficit,
                Ratio = ownIncome / Mathf.Max(1f, opponentMedianIncome),
            };
        }
    }

    public struct EconomyResourceStanding
    {
        public ResourceType Type;
        public float OwnIncome;
        public float OpponentMedianIncome;
        public float FieldMedianIncome;
        public float HandResourceNeed;
        public float RemainingDeckResourceNeed;
        public float ReservedOperationalNeed;
        public float SpendableStockpile;
        public float IncomeTarget;
        public float IncomeGap;
        public float RelativeIncomeGap;
        public float RunwayCoverage;
        public float OperationalPressure;
        public float StarvationPressure;
        public float DeficitScore;
        public float Ratio;               // OwnIncome / max(1, FieldMedianIncome)
    }

    // =======================================================================================
    //  THREAT MODEL
    // =======================================================================================
    public enum ContactKnowledge { Exact, LastKnown, Region, Unknown }
    public enum ContactSource { Honest, Cheat }
    public enum AssetKind { Citadel, Base, Facility, Army, ResourceSite }

    // A single enemy force the AI is aware of, at whatever fidelity it earned. The cheat/honest
    // boundary is enforced HERE, structurally: a Cheat-sourced contact can only ever be
    // Region/Unknown and can never carry Position (see the constructor's clamp in WorldAnalysis).
    // That is spec-18 ("a hidden army raising an alert must not become a targetable hex") as an
    // architectural constraint rather than a comment on each call site.
    public sealed class EnemyContactSnapshot
    {
        public ArmySnapshot Army;
        public ContactKnowledge Knowledge;
        public ContactSource Source;

        public HexCoord? Position;         // non-null ONLY for Exact / LastKnown
        public HexCoord? RegionCenter;     // non-null ONLY for Region
        public int RegionRadius;

        public float Confidence;           // [0..1]

        // Global turn this contact's position was last honestly observed. For an Exact contact
        // that is the current turn (age 0); for LastKnown it is the sighting's SeenTurn. Only
        // meaningful for a Honest contact that carries a Position — a Cheat contact has neither a
        // position nor an observation history, so it is never a surveillance target.
        public int LastObservedTurn;

        public int AgeTurns(int currentTurn) => System.Math.Max(0, currentTurn - LastObservedTurn);
    }

    public sealed class StrategicAssetSnapshot
    {
        public HexCoord Hex;
        public AssetKind Kind;
        public float Value;                // importance to US, set by the snapshot
        public float Defense;              // quick scalar: HexDefenseBonus + Σ Defenders' Defense
        public float HexDefenseBonus;      // the structural / terrain part alone (fed to WorthIt as its hexDefenseBonus)
        public IReadOnlyList<WorthIt.DefenderProfile> Defenders; // garrison / the army's own roster
    }

    // One Enemy x Asset pressure pairing above the listing cutoff.
    public sealed class AssetThreatSnapshot
    {
        public StrategicAssetSnapshot Asset;
        public EnemyContactSnapshot Contact;

        public bool CanDamage;             // can the contact's force actually hurt this asset
        public int? EnemyEta;              // turns for the contact to reach the asset; null if Knowledge >= Region
        public int? ResponseEta;           // turns for our nearest adequate force to intervene
        public float AttackWinChance;      // WorthIt full-roster MC — contact as attacker
        public float PotentialDamage;      // expected value lost if it lands (0..1 fraction of Asset.Value)
        public float Confidence;
        public float Severity;             // continuous — see AiConfigV2.severity* weights
    }

    public sealed class ThreatModel
    {
        public IReadOnlyList<EnemyContactSnapshot> Contacts;
        public IReadOnlyList<StrategicAssetSnapshot> Assets;
        public IReadOnlyList<AssetThreatSnapshot> Threats;

        // Honest, POSITIONED contacts indexed by the tracked army's id — the freshest one when the
        // same army is both live-sighted and remembered. The step-7 Surveil continuity path reads
        // this instead of querying AiReconMemory, keeping "downstream reads the snapshot" intact.
        public IReadOnlyDictionary<int, EnemyContactSnapshot> ReconContactByArmyId;
        // AI-behaviour label ONLY — no game "siege" state exists. "A force I can't beat is at the
        // gates": an enemy within AiConfigV2.siegeRadius (3) of a Citadel/Base (or <=1 turn out)
        // whose attack would probably win, OR'd with V1 AiDefencePlanner.IsUnderSiege for parity.
        public bool UnderSiege;
    }
}
