using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Units;

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

        // AI-RECON-02 — air OBSERVATION capacity, from the shared ReconAirCapacityPolicy (the same
        // slot cap + launch-subset + AP/Energy gate ReconAirExecutor launches against):
        //   AirborneReconWings         — own wings already flying a durable ReconAssignment; each is
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
    public sealed class EconomyStanding
    {
        // One entry per ResourceType, in ResourceBundle.All order.
        public IReadOnlyList<EconomyResourceStanding> PerType;

        // Aggregate resource appetite of the cards the AI can still play (hand + deck) —
        // variant (a): the absolute floor is derived from what the deck actually costs, not a
        // fixed threshold.
        public ResourceBundle DeckResourceNeed;

        // Sustainable per-turn income target by resource. Unlike DeckResourceNeed this is NOT
        // "pay the remaining deck within N turns": it is the larger of the field-median income
        // and this deck's average per-card resource cadence. Stockpile runway affects security,
        // not the target itself.
        public ResourceBundle IncomeTarget;

        public float RelativePressure;    // [-1..1]  <0 behind the field, >0 ahead
        public float BottleneckPressure;  // [0..1]   how bad the single worst resource is
        public float AbsFloor;            // [0..1]   income vs DeckResourceNeed/horizon, smoothstepped
        public float EconomicSecurity;    // [0..1]   blend(AbsFloor, RelativePressure, BottleneckPressure)
    }

    public struct EconomyResourceStanding
    {
        public ResourceType Type;
        public float OwnIncome;
        public float FieldMedianIncome;
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
