using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Every read of AiMapMemory. A null or unknown player reads as "knows nothing".
    public static partial class AiMapMemory
    {
        public static IEnumerable<KnownAirSighting> AllKnownAirSightings(PlayerSetupData player) =>
            TryStore(AirSightings, player, out var sightings)
                ? sightings.Values.OrderBy(s => s.ArmyId)
                : Enumerable.Empty<KnownAirSighting>();

        // Every known resource hex and its complete last-observed effective yield — only hexes this
        // player has actually seen the bonus on.
        public static IEnumerable<KnownResourceHex> AllKnownResourceHexes(PlayerSetupData actor) =>
            TryStore(KnownResourceHexes, actor, out var set)
                ? set.Values
                : Enumerable.Empty<KnownResourceHex>();

        // Every known neutral army, whole map, no radius.
        public static IEnumerable<KnownEnemySighting> AllKnownNeutralSightings(PlayerSetupData actor)
        {
            if (!TryStore(EnemySightings, actor, out var sightings))
                yield break;
            foreach (KnownEnemySighting sighting in sightings.Values)
                if (IsNeutralSightingOwner(sighting.Owner))
                    yield return sighting;
        }

        // Every known enemy-player army, whole map, no radius.
        public static IEnumerable<KnownEnemySighting> AllKnownEnemySightings(PlayerSetupData actor)
        {
            if (!TryStore(EnemySightings, actor, out var sightings))
                yield break;
            foreach (KnownEnemySighting sighting in sightings.Values)
                if (!IsNeutralSightingOwner(sighting.Owner))
                    yield return sighting;
        }

        // The remembered sighting of one physical army (an enemy player's or a neutral) by its
        // stable id, or null. Army id 0 is valid.
        public static KnownEnemySighting? KnownSightingOf(PlayerSetupData actor, int armyId) =>
            TryStore(EnemySightings, actor, out var sightings) && sightings.TryGetValue(armyId, out var s)
                ? s : (KnownEnemySighting?)null;

        // The same, only while it is an enemy PLAYER's army — never a neutral, never the actor's own.
        public static KnownEnemySighting? KnownHostileSightingOf(PlayerSetupData actor, int armyId)
        {
            KnownEnemySighting? s = KnownSightingOf(actor, armyId);
            return s.HasValue && !IsNeutralSightingOwner(s.Value.Owner) && s.Value.Owner != actor ? s : null;
        }

        // Every known army (enemy or neutral) within `radius` of any of `ownHexes`.
        public static IEnumerable<KnownEnemySighting> KnownEnemySightingsNear(PlayerSetupData actor,
            IReadOnlyList<HexCoord> ownHexes, int radius)
        {
            if (!TryStore(EnemySightings, actor, out var sightings))
                yield break;
            foreach (KnownEnemySighting sighting in sightings.Values)
                if (ownHexes.Any(own => HexGridMath.Distance(own, sighting.Hex) <= radius))
                    yield return sighting;
        }

        // A known army (enemy or neutral) last seen on `hex`, or null. A hex may hold several
        // armies; this returns one of them, so read it as "is any army known there".
        public static KnownEnemySighting? KnownEnemySightingAt(PlayerSetupData actor, HexCoord hex)
        {
            if (!TryStore(EnemySightings, actor, out var sightings))
                return null;
            foreach (KnownEnemySighting sighting in sightings.Values)
                if (sighting.Hex.Equals(hex))
                    return sighting;
            return null;
        }

        public static KnownEventState KnownEventStateAt(PlayerSetupData actor, HexCoord hex) =>
            TryStore(KnownEventCompleted, actor, out var states) && states.TryGetValue(hex, out bool completed)
                ? (completed ? KnownEventState.Completed : KnownEventState.Active)
                : KnownEventState.Unknown;

        // Every hex whose event this player knows to be active (guarded or not) / confirmed done.
        public static IEnumerable<HexCoord> KnownEventHexes(PlayerSetupData actor, bool completed) =>
            TryStore(KnownEventCompleted, actor, out var states)
                ? states.Where(kv => kv.Value == completed).Select(kv => kv.Key)
                : Enumerable.Empty<HexCoord>();

        // The guard of a known active event on `hex`, or null (never met one, or since consumed).
        public static GuardStrength? KnownEventGuardStrengthAt(PlayerSetupData actor, HexCoord hex) =>
            TryStore(KnownEventGuards, actor, out var guards) && guards.TryGetValue(hex, out GuardStrength strength)
                ? strength
                : (GuardStrength?)null;

        // Every hex this observer remembers as a Hex Event with a real guard. The guard is not a
        // sighting (it spawns on arrival), so KnownGroundArrival cannot see it; Combat routing
        // reads this list so a march toward one target does not walk into another guard's hex.
        public static IEnumerable<HexCoord> KnownEventGuardHexes(PlayerSetupData actor) =>
            TryStore(KnownEventGuards, actor, out var guards)
                ? guards.Keys
                : Enumerable.Empty<HexCoord>();

        // The last building observed on `hex`, or null — never observed there, or the hex was last
        // observed without one.
        public static KnownBuilding? KnownBuildingAt(PlayerSetupData actor, HexCoord hex) =>
            TryStore(KnownBuildings, actor, out var buildings) && buildings.TryGetValue(hex, out KnownBuilding building)
                ? building
                : (KnownBuilding?)null;

        // Every building this player has ever observed, as last seen.
        public static IEnumerable<KnownBuilding> AllKnownBuildings(PlayerSetupData actor) =>
            TryStore(KnownBuildings, actor, out var buildings)
                ? buildings.Values
                : Enumerable.Empty<KnownBuilding>();

        // FIX-05 (2026-09-22) — the ONE fog-honest answer to "what structural/terrain defence
        // would a defender standing on `hex` actually get", for every AI caller.
        // Game.Combat.WorthIt.HexDefenseBonus stays exactly as it is — it is correct whenever it
        // is called with an honest context (a real fight, a currently-visible hex, the player's
        // own hex) — but it reads BuildingRegistry LIVE, so calling it about a fogged hex let a
        // base built, upgraded, captured or razed out of sight change an AI estimate with no
        // observation behind it. This wraps the same rule in the knowledge boundary:
        //   * terrain is immutable public map data and is always included;
        //   * a CURRENTLY VISIBLE hex reads the live building, because looking at it is the
        //     observation - and that is also what keeps a just-captured/just-razed base correct
        //     without waiting for a vision recompute;
        //   * a fogged hex contributes the last OBSERVED Defense of the building remembered
        //     there, and nothing at all where no building was ever seen.
        // Two players' answers are independent because KnownBuildings is per-player.
        //
        // Combat-owner-aware form: terrain belongs to the hex and applies to every defender,
        // while a Base's structural Defense applies only to an army owned by that Base's owner.
        // A null defendingOwner intentionally means terrain only (for example an event guard).
        public static float KnownHexDefenseBonusFor(PlayerSetupData actor, HexCoord hex,
            PlayerSetupData defendingOwner) =>
            KnownHexDefenseBonusFor(actor, _map, hex, defendingOwner);

        public static float KnownHexDefenseBonusFor(PlayerSetupData actor, HexMap map, HexCoord hex,
            PlayerSetupData defendingOwner) =>
            HexDefenseBonus(actor, map, hex, defendingOwner, anyOwner: false);

        // The site's own defence whoever holds it: a Base's structural Defense counts for any owner.
        public static float KnownHexDefenseBonus(PlayerSetupData actor, HexMap map, HexCoord hex) =>
            HexDefenseBonus(actor, map, hex, null, anyOwner: true);

        public static float KnownBuildingDefenseBonusFor(PlayerSetupData actor, HexCoord hex,
            PlayerSetupData defendingOwner) => KnownBuildingDefenseBonusFor(actor, _map, hex, defendingOwner);

        public static float KnownBuildingDefenseBonusFor(PlayerSetupData actor, HexMap map, HexCoord hex,
            PlayerSetupData defendingOwner) => BuildingDefenseBonus(actor, hex, defendingOwner, false);

        private static float HexDefenseBonus(PlayerSetupData actor, HexMap map, HexCoord hex,
            PlayerSetupData defendingOwner, bool anyOwner)
        {
            float terrainBonus = 0f;
            if (map != null && map.TryGetTerrainAt(hex, out var terrain) && terrain != null)
                terrainBonus = terrain.defenseModifier;
            return terrainBonus + BuildingDefenseBonus(actor, hex, defendingOwner, anyOwner);
        }

        private static float BuildingDefenseBonus(PlayerSetupData actor, HexCoord hex,
            PlayerSetupData defendingOwner, bool anyOwner)
        {
            if (actor == null) return 0f;
            PlayerSetupData owner;
            float defense;
            if (VisionSystem.IsVisible(actor, hex))
            {
                BuildingData live = BuildingRegistry.FindAt(hex);
                if (live == null || !live.IsBase) return 0f;
                owner = live.Owner;
                defense = live.Defense;
            }
            else
            {
                KnownBuilding? remembered = KnownBuildingAt(actor, hex);
                if (!remembered.HasValue || !remembered.Value.IsBase) return 0f;
                owner = remembered.Value.Owner;
                defense = remembered.Value.Defense;
            }
            if (!anyOwner && (defendingOwner == null || owner != defendingOwner)) return 0f;
            // Own carriers are known even in stealth. Never inspect hidden foreign members:
            // visible/remembered foreign contacts remain observation-owned knowledge.
            if (defense > 0f && (owner != actor && ArmyRegistry.AllAt(hex)
                .Any(a => a.Owner == actor && DisruptionRules.HasCarrier(a))
                || AllKnownEnemySightings(actor).Concat(AllKnownNeutralSightings(actor))
                    .Any(s => s.Hex.Equals(hex) && !s.IsAir && s.Owner != owner
                        && (DisruptionRules.HasCarrier(s.Defenders)
                            || DisruptionRules.HasCarrier(s.Commander.SupportHeroes))))) return 0f;
            return defense;
        }

        public static GroundArrival KnownGroundArrival(PlayerSetupData actor, HexCoord hex, bool moverFullyHidden)
            => new GroundArrival(KnownEnemySightingAt(actor, hex).HasValue,
                KnownUndefendedForeignStructureAt(actor, hex), moverFullyHidden);

        public static GroundArrival KnownGroundArrival(PlayerSetupData actor, ArmyData mover, HexCoord hex)
            => KnownGroundArrival(actor, hex, StealthSystem.IsArmyFullyHidden(mover));

        // A hidden army that drops stealth while standing still is a resident reveal
        // (StealthSystem.ExitStealth -> ResidentRevealed): the same arrival rule, applied to its
        // OWN hex. A known army sharing that hex fights it there, instead of the action the reveal
        // was meant for (playtest 2026-10-22, Ysolde T17/T19: two Hooded scouts revealed beside
        // Sand Fin at (-5,1) to sabotage (-6,1) and were wiped on (-5,1)).
        public static bool KnownResidentRevealContact(PlayerSetupData actor, ArmyData mover)
            => mover != null && StealthSystem.IsArmyFullyHidden(mover)
               && KnownGroundArrival(actor, mover.Hex, moverFullyHidden: false).KnownArmy;

        // FIX-07 (2026-09-22) — is the building on `hex` KNOWN to be foreign and KNOWN to be
        // standing there with nobody to defend it? The knowledge-side mirror of the authoritative
        // gameplay rule (BuildingRegistry.CaptureOrDestroyIfUndefended: a foreign building with no
        // engageable resident of its own owner changes hands / is destroyed the moment someone
        // else arrives). It exists so an army ALREADY walking onto such a hex as part of an
        // accepted plan is not blocked from simply occupying it — never so anything goes looking
        // for structures.
        //   * hex VISIBLE now — the live building and the live residents ARE the observation;
        //   * hex FOGGED — a remembered building is required and NO army may be remembered there.
        //     Unknown defence is never "undefended": no remembered building, a remembered
        //     enemy/neutral sighting on the hex, or a known guarded Hex Event on it all answer
        //     false, so nobody is ever waved into a blind walk-in.
        public static bool KnownUndefendedForeignStructureAt(PlayerSetupData actor, HexCoord hex)
        {
            if (actor == null)
                return false;
            // A known active guarded event on the hex is a fight waiting to happen, not an empty
            // structure — and its guard is not an army this memory records as a sighting.
            if (KnownEventGuardStrengthAt(actor, hex).HasValue)
                return false;

            if (VisionSystem.IsVisible(actor, hex))
            {
                BuildingData live = BuildingRegistry.FindAt(hex);
                if (live == null || live.Owner == null || live.Owner == actor)
                    return false;
                foreach (ArmyData resident in ArmyRegistry.AllAt(hex))
                    if (resident != null && resident.Owner == live.Owner
                        && BattleInitiator.IsEngageable(resident, actor))
                        return false;
                return true;
            }

            KnownBuilding? remembered = KnownBuildingAt(actor, hex);
            if (!remembered.HasValue || remembered.Value.Owner == null
                || remembered.Value.Owner == actor)
                return false;
            return !KnownEnemySightingAt(actor, hex).HasValue;
        }
    }
}
