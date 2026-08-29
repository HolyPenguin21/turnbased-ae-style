using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Units;

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
        public bool HasAntiAir;
        public bool IsHiddenFromUs;

        public float AttackSum;             // WorthIt-style raw sum, non-hero
        public float DefenseSum;
        public float EffectiveArmyPower;    // AiPower — composition-adjusted ranking scalar
        public float CompositionQuality;    // [0..1] the multiplier's driver, kept for the "why" log
        public int MaxMovement;             // per-turn move budget, for rough ETA

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
        public float UnknownFrac;

        // STUB until build-order step 4 (the Recon planner fills this). Empty list for now.
        public IReadOnlyList<HexCoord> Frontier;
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
        // AI-behaviour label ONLY — no game "siege" state exists. "A force I can't beat is at the
        // gates": an enemy within AiConfigV2.siegeRadius (3) of a Citadel/Base (or <=1 turn out)
        // whose attack would probably win, OR'd with V1 AiDefencePlanner.IsUnderSiege for parity.
        public bool UnderSiege;
    }
}
