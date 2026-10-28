using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai
{
    // The read-only records AiMapMemory stores and hands out. Every record is written only from a
    // hex the observer can genuinely see (AiMapMemory.Observation.cs) and then stays exactly as
    // observed. Each SameContent is THE change rule of its record: memory updates and snapshot
    // observation both read it, so keep it in step with the record's field list.
    public static partial class AiMapMemory
    {
        // Aviation has a different target pool from ground contact. This store is observed
        // only on visible hexes; it never changes the existing ground/route sightings.
        public readonly struct KnownAirSighting
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal KnownAirSighting(bool __matchSave, global::Game.HexGrid.HexCoord __Hex, global::Game.Players.PlayerSetupData __Owner, int __SeenTurn, bool __IsGroundArmy, global::Game.Combat.AviationCombatEstimator.DefendingAirArmy __Roster)
        {
            Hex = __Hex;
            Owner = __Owner;
            SeenTurn = __SeenTurn;
            IsGroundArmy = __IsGroundArmy;
            Roster = __Roster;
        }

            public readonly HexCoord Hex;
            public readonly PlayerSetupData Owner;
            public readonly int SeenTurn;
            public readonly bool IsGroundArmy;
            public readonly AviationCombatEstimator.DefendingAirArmy Roster;
            public int ArmyId => Roster.ArmyId;
            public KnownAirSighting(HexCoord hex, PlayerSetupData owner, int seenTurn,
                bool isGroundArmy, AviationCombatEstimator.DefendingAirArmy roster)
            {
                Hex = hex; Owner = owner; SeenTurn = seenTurn;
                IsGroundArmy = isGroundArmy; Roster = roster;
            }
        }

        internal static bool SameAirSighting(KnownAirSighting a, KnownAirSighting b) =>
            a.ArmyId == b.ArmyId && a.Hex.Equals(b.Hex) && a.Owner == b.Owner
            && a.SeenTurn == b.SeenTurn && a.IsGroundArmy == b.IsGroundArmy
            && a.Roster.CommanderIndex == b.Roster.CommanderIndex
            && WorthIt.DefenderProfile.SameContent(a.Roster.Units, b.Roster.Units)
            && a.Roster.CurrentFates.SequenceEqual(b.Roster.CurrentFates);

        // The last sighting of one physical enemy/neutral army, stored under its stable ArmyData.Id
        // so a moved army's record is updated in place instead of leaving an orphan at its old hex.
        public readonly struct KnownEnemySighting
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal KnownEnemySighting(bool __matchSave, int __ArmyId, global::Game.HexGrid.HexCoord __Hex, global::Game.Players.PlayerSetupData __Owner, string __Name, int __MemberCount, float __DefenseSum, float __AttackSum, global::System.Collections.Generic.IReadOnlyList<global::Game.Combat.WorthIt.DefenderProfile> __Defenders, bool __HasAntiAir, int __RecceRadius, int __RecceSpotStrength, int __SeenTurn, bool __IsGarrison, bool __IsAir, int __MaxMovement, global::Game.Combat.WorthIt.SideCommander __Commander)
        {
            ArmyId = __ArmyId;
            Hex = __Hex;
            Owner = __Owner;
            Name = __Name;
            MemberCount = __MemberCount;
            DefenseSum = __DefenseSum;
            AttackSum = __AttackSum;
            Defenders = __Defenders;
            HasAntiAir = __HasAntiAir;
            RecceRadius = __RecceRadius;
            RecceSpotStrength = __RecceSpotStrength;
            SeenTurn = __SeenTurn;
            IsGarrison = __IsGarrison;
            IsAir = __IsAir;
            MaxMovement = __MaxMovement;
            Commander = __Commander;
        }

            // The sighted army's stable ArmyData.Id. Lets a consumer track one physical army
            // across moves (AiReconMemory keys its longer observation history by it).
            public readonly int ArmyId;
            // Last observed position.
            public readonly HexCoord Hex;
            public readonly PlayerSetupData Owner;
            public readonly string Name;
            // Visible members only; a hidden member's existence is unknown.
            public readonly int MemberCount;
            // Ground-combatant sums (heroes excluded).
            public readonly float DefenseSum;
            public readonly float AttackSum;
            // Full per-unit snapshot of every visible member, current HP included ("если мы
            // когда-то видели армию значит мы видели её состав"). Damage, repairs and regeneration
            // become known only on re-observation. A lone hero is kept: it is a Capture/Kill
            // target, not an army with an unknown empty roster.
            public readonly IReadOnlyList<WorthIt.DefenderProfile> Defenders;
            // Any visible member carries an AntiAirRules-recognized AA ability. An observed
            // tactical capability; strategic aviation route selection does not read it.
            public readonly bool HasAntiAir;
            // Best Recce radius / spot strength among the visible members (стелс §2) — lets
            // CanDetectStealthAt answer only where this army could ACTUALLY roll a stealth
            // challenge. 0/0 for an army with no Recce at all.
            public readonly int RecceRadius;
            public readonly int RecceSpotStrength;
            // The global turn this sighting was last (re)observed; drives expiry and lets a
            // consumer age a last-known position. 0 for a sighting recorded before the first
            // OnTurnStarted (turn numbering starts at 1, so that reads as "very old").
            public readonly int SeenTurn;
            // Building-bound garrison: part of its structure's defence, never expires by elapsed
            // turns, corrected only by re-observing its hex. Consumers that model a roaming threat
            // skip it; consumers that price a site's defender package keep it.
            public readonly bool IsGarrison;
            // Every visible member is aviation (garrisons, airfields and prisons never are).
            public readonly bool IsAir;
            // Slowest visible member's movement — a conservative fastest-possible budget.
            public readonly int MaxMovement;
            // The observed commander; absent when the army has no hero or its commander is hidden.
            public readonly WorthIt.SideCommander Commander;

            public KnownEnemySighting(HexCoord hex, PlayerSetupData owner, string name, int memberCount, float defenseSum, float attackSum,
                IReadOnlyList<WorthIt.DefenderProfile> defenders, bool hasAntiAir = false, int recceRadius = 0, int recceSpotStrength = 0,
                int seenTurn = 0, int armyId = 0, bool isGarrison = false,
                WorthIt.SideCommander commander = default, bool isAir = false, int maxMovement = 1)
            {
                IsAir = isAir;
                MaxMovement = System.Math.Max(1, maxMovement);
                IsGarrison = isGarrison;
                Commander = commander;
                ArmyId = armyId;
                Hex = hex;
                Owner = owner;
                Name = name;
                MemberCount = memberCount;
                DefenseSum = defenseSum;
                AttackSum = attackSum;
                Defenders = defenders == null ? null : System.Array.AsReadOnly(defenders.ToArray());
                HasAntiAir = hasAntiAir;
                RecceRadius = recceRadius;
                RecceSpotStrength = recceSpotStrength;
                SeenTurn = seenTurn;
            }

            // Could this remembered army actually roll a stealth-detection challenge against a
            // hidden unit standing on `hex`? Mirrors StealthSystem.SourcePool: co-located →
            // always (pool ≥ 1); exactly adjacent → only with radius>0 AND spot>0 (an r1s0
            // source reveals the hex but never detects stealth); 2+ hexes → never.
            public bool CanDetectStealthAt(HexCoord hex)
            {
                int dist = HexGridMath.Distance(Hex, hex);
                if (dist == 0)
                    return true;
                if (dist == 1)
                    return RecceRadius > 0 && RecceSpotStrength > 0;
                return false;
            }

            // Every field a decision reads, the roster by content.
            public bool SameContent(KnownEnemySighting o) =>
                ArmyId == o.ArmyId && Hex.Equals(o.Hex) && Owner == o.Owner && Name == o.Name
                && MemberCount == o.MemberCount && DefenseSum == o.DefenseSum && AttackSum == o.AttackSum
                && HasAntiAir == o.HasAntiAir && SeenTurn == o.SeenTurn
                && RecceRadius == o.RecceRadius && RecceSpotStrength == o.RecceSpotStrength
                && IsGarrison == o.IsGarrison && IsAir == o.IsAir && MaxMovement == o.MaxMovement
                && Commander.Equals(o.Commander)
                && WorthIt.DefenderProfile.SameContent(Defenders, o.Defenders);
        }

        // A known Hex Event's guard as card stats: aggregate Attack/Defense, one DefenderProfile
        // per physical copy (WorthIt's full-roster estimate and CanDamageAll's coverage), the
        // guard army's name and commander. The guard is never a live army until Explore is chosen.
        public readonly struct GuardStrength
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal GuardStrength(bool __matchSave, float __Defense, float __Attack, global::System.Collections.Generic.IReadOnlyList<global::Game.Combat.WorthIt.DefenderProfile> __Defenders, string __Name, global::Game.Combat.WorthIt.SideCommander __Commander, int __RewardTier)
        {
            Defense = __Defense;
            Attack = __Attack;
            Defenders = __Defenders;
            Name = __Name;
            Commander = __Commander;
            RewardTier = __RewardTier;
        }

            public readonly float Defense;
            public readonly float Attack;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> Defenders;
            public readonly string Name;
            // The guard's commander: its first hero card, if the guard has one.
            public readonly WorthIt.SideCommander Commander;
            // The authored guard tier (HexEventGuardEstimate.RewardTier, light 0 / medium 1 /
            // heavy 2), which carries the event's reward tier; -1 unknown.
            public readonly int RewardTier;

            public GuardStrength(float defense, float attack, IReadOnlyList<WorthIt.DefenderProfile> defenders, string name = null,
                WorthIt.SideCommander commander = default, int rewardTier = -1)
            {
                Commander = commander;
                RewardTier = rewardTier;
                Defense = defense;
                Attack = attack;
                Defenders = defenders == null ? null : System.Array.AsReadOnly(defenders.ToArray());
                Name = name;
            }

            // RewardTier is authored per event and cannot change under the same guard.
            public bool SameContent(GuardStrength o) =>
                Defense == o.Defense && Attack == o.Attack && Name == o.Name
                && Commander.Equals(o.Commander) && WorthIt.DefenderProfile.SameContent(Defenders, o.Defenders);
        }

        // Full effective resource line last observed while the hex was genuinely visible. The
        // dominant type is retained only for display/prioritisation callers; Economy V2 consumes
        // Yield directly and must never reconstruct hidden quantities from the live map.
        public readonly struct KnownResourceHex
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal KnownResourceHex(bool __matchSave, global::Game.HexGrid.HexCoord __Hex, global::Game.Economy.ResourceType __DominantType, global::Game.Economy.ResourceYields __Yield)
        {
            Hex = __Hex;
            DominantType = __DominantType;
            Yield = __Yield;
        }

            public readonly HexCoord Hex;
            public readonly ResourceType DominantType;
            public readonly ResourceYields Yield;

            public KnownResourceHex(HexCoord hex, ResourceType dominantType, ResourceYields yield)
            {
                Hex = hex;
                DominantType = dominantType;
                // Own the observation instead of retaining a terrain/bonus object's mutable
                // ResourceYields reference.
                Yield = new ResourceYields
                {
                    human = yield?.human ?? 0,
                    energy = yield?.energy ?? 0,
                    materials = yield?.materials ?? 0,
                    tech = yield?.tech ?? 0,
                };
            }

            public bool SameContent(KnownResourceHex o) =>
                Hex.Equals(o.Hex) && DominantType == o.DominantType
                && (Yield?.human ?? 0) == (o.Yield?.human ?? 0)
                && (Yield?.energy ?? 0) == (o.Yield?.energy ?? 0)
                && (Yield?.materials ?? 0) == (o.Yield?.materials ?? 0)
                && (Yield?.tech ?? 0) == (o.Yield?.tech ?? 0);
        }

        public enum KnownEventState { Unknown, Active, Completed }

        // Last observed building on a hex, owner included — an enemy building's true current owner
        // is never known further than this player's own last look at that hex.
        public readonly struct KnownBuilding
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal KnownBuilding(bool __matchSave, global::System.Collections.Generic.IReadOnlyList<global::Game.Combat.WorthIt.DefenderProfile> __ExplosionStructures, global::Game.HexGrid.HexCoord __Hex, global::Game.Players.PlayerSetupData __Owner, bool __IsStartingCitadel, bool __IsBase, global::System.Collections.Generic.IReadOnlyCollection<string> __FacilityAbilities, global::System.Collections.Generic.IReadOnlyList<int> __CollectedAmounts, int __FreeFacilitySlots, float __Defense, int __SeenTurn)
        {
            ExplosionStructures = __ExplosionStructures;
            Hex = __Hex;
            Owner = __Owner;
            IsStartingCitadel = __IsStartingCitadel;
            IsBase = __IsBase;
            FacilityAbilities = __FacilityAbilities;
            CollectedAmounts = __CollectedAmounts;
            FreeFacilitySlots = __FreeFacilitySlots;
            Defense = __Defense;
            SeenTurn = __SeenTurn;
        }

            // Public structure stats for explosion pricing; own facility slots included.
            public readonly IReadOnlyList<WorthIt.DefenderProfile> ExplosionStructures;
            public readonly HexCoord Hex;
            public readonly PlayerSetupData Owner;
            public readonly bool IsStartingCitadel;
            public readonly bool IsBase;
            // T08 — OWN buildings only: union of every placed Facility's Abilities (e.g.
            // UnitAbilities.CollectHuman). A foreign base's facility cards are internal slot
            // contents no AI task may know; null for a building another player owned when observed.
            public readonly IReadOnlyCollection<string> FacilityAbilities;
            // Last observed physical collection of the hex's resources by this structure, indexed
            // by ResourceType — an observed EFFECT the UI shows for any owner, never the cards
            // behind it.
            public readonly IReadOnlyList<int> CollectedAmounts;
            // T08 — OWN buildings only: unlocked empty slots (0 for a foreign one). The
            // authoritative answer to whether another Facility could be placed now.
            public readonly int FreeFacilitySlots;
            // FIX-05 — last observed Defense stat. Stored for every building; only a Base
            // contributes it to a fight (see KnownHexDefenseBonusFor).
            public readonly float Defense;
            // ATK §66 — turn this record was last written. A structure's LOCATION never expires;
            // this lets TaskScore.Staleness price intel age. 0 means "never stamped" (a synthesised
            // record in a test), which callers must read as "age unknown", never as turn 0.
            public readonly int SeenTurn;

            public KnownBuilding(HexCoord hex, PlayerSetupData owner, bool isStartingCitadel,
                IReadOnlyCollection<string> facilityAbilities,
                IReadOnlyList<int> collectedAmounts = null, int freeFacilitySlots = int.MaxValue,
                bool isBase = false, float defense = 0f, int seenTurn = 0,
                IReadOnlyList<WorthIt.DefenderProfile> explosionStructures = null)
            {
                ExplosionStructures = System.Array.AsReadOnly((explosionStructures
                    ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToArray());
                Defense = defense;
                SeenTurn = seenTurn;
                Hex = hex;
                Owner = owner;
                IsStartingCitadel = isStartingCitadel;
                IsBase = isBase;
                FacilityAbilities = facilityAbilities;
                CollectedAmounts = collectedAmounts;
                FreeFacilitySlots = freeFacilitySlots;
            }

            public bool HasFacilityWithAbility(string ability) =>
                FacilityAbilities != null && FacilityAbilities.Contains(ability);

            public int CollectedAmount(ResourceType type)
            {
                int index = (int)type;
                return CollectedAmounts != null && index >= 0 && index < CollectedAmounts.Count
                    ? System.Math.Max(0, CollectedAmounts[index])
                    : 0;
            }

            public bool SameContent(KnownBuilding o) =>
                Hex.Equals(o.Hex) && Owner == o.Owner
                && IsStartingCitadel == o.IsStartingCitadel && IsBase == o.IsBase
                && Defense == o.Defense && SeenTurn == o.SeenTurn
                && FreeFacilitySlots == o.FreeFacilitySlots
                && WorthIt.DefenderProfile.SameContent(ExplosionStructures, o.ExplosionStructures)
                && new HashSet<string>(FacilityAbilities ?? System.Array.Empty<string>())
                    .SetEquals(o.FacilityAbilities ?? System.Array.Empty<string>())
                && (CollectedAmounts ?? System.Array.Empty<int>())
                    .SequenceEqual(o.CollectedAmounts ?? System.Array.Empty<int>());
        }

        // THE one AI rule for "what does this ground mover's arrival on `hex` set off, as far as
        // this player knows" — the knowledge-side mirror of the gameplay arrival rules:
        //   * a known enemy/neutral army on the hex fights a mover that is visible
        //     (BattleInitiator.CanInitiateContact on entry, GameTurnController's contested sweep
        //     for a visible hero), and a FULLY hidden mover walks past it with nothing happening;
        //   * a known undefended foreign structure is captured/destroyed by a visible mover
        //     (BuildingRegistry.CaptureOrDestroyIfUndefended), and a fully hidden mover walks past.
        // "Fully hidden" is the mover's OWN stealth state (StealthSystem.IsArmyFullyHidden — the
        // same definition the gameplay rules use) — never whether an enemy has detected it: the
        // owner of a hidden army does not know that (stealth design).
        // A Hex Event is deliberately not part of this: every AI move resolves its own event choice
        // by authority (AiTurnController.MoveArmyRoutine → IssueMoveOrder allowAiEventExplore), so
        // a Transit move always skips and keeps its stealth / movement, exactly like a human Skip.
        // Every AI "may this mover step onto hex X" check must derive from this — the execution
        // gate, Recon step/vantage safety, the route blockers and the snapshot's scout block set.
        public readonly struct GroundArrival
        {
            public readonly bool KnownArmy;
            public readonly bool UndefendedStructure;
            public readonly bool MoverFullyHidden;

            public GroundArrival(bool knownArmy, bool undefendedStructure, bool moverFullyHidden)
            {
                KnownArmy = knownArmy;
                UndefendedStructure = undefendedStructure;
                MoverFullyHidden = moverFullyHidden;
            }

            public bool Contact => KnownArmy && !MoverFullyHidden;
            public bool Takeover => UndefendedStructure && !MoverFullyHidden;
            // Arrival sets off a fight or a takeover — something only a Combat/Capture authority
            // may deliberately seek.
            public bool HasOutcome => Contact || Takeover;
        }
    }
}
