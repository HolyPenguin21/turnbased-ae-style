using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Honest per-player memory of hex CONTENT — the piece VisionSystem itself explicitly does
    // NOT keep (see its own class comment: "Content has no memory either way and re-hides the
    // instant vision leaves"). Subscribes to VisionSystem.VisibilityChanged and, on every
    // recompute, snapshots whatever's on `player`'s own currently-visible hexes into two
    // permanent-until-corrected stores: which hexes are known to carry a resource bonus (and,
    // as of the Разведка Задача 2 pass, which ResourceType — reading the type off an already-
    // VISIBLE hex isn't the cheat AiEconomyPlanner.DominantResourceType's own caller guards
    // against elsewhere, since a real player would see the bonus icon the moment fog lifts too),
    // and where an enemy/neutral army was last actually seen. Per the project owner's own
    // "Видимость с памятью" principle — stale info is never auto-expired, only overwritten by a
    // fresh observation of that SAME hex (see OnVisibilityChanged's own `sightings.Remove`
    // branch).
    //
    // Deliberately narrow in scope — only the slices AiGoalScorer/AiScoutPlanner/AiTurnController
    // actually need honesty for right now (resource hexes + type, enemy armies). Other players'
    // own resource stockpiles stay the project's own documented cheat exception (see
    // AiGoalScorer's own IncomeBehindBonus) and never route through here.
    public static class AiMapMemory
    {
        private class EnemySighting
        {
            public PlayerSetupData Owner;
            public int MemberCount;
            public float DefenseSum;
        }

        public readonly struct KnownEnemySighting
        {
            public readonly HexCoord Hex;
            public readonly PlayerSetupData Owner;
            public readonly int MemberCount;
            public readonly float DefenseSum;

            public KnownEnemySighting(HexCoord hex, PlayerSetupData owner, int memberCount, float defenseSum)
            {
                Hex = hex;
                Owner = owner;
                MemberCount = memberCount;
                DefenseSum = defenseSum;
            }
        }

        // HexCoord -> the dominant ResourceType last observed there (see AiEconomyPlanner.
        // DominantResourceType) — a hex only ever enters this dictionary once its bonus has
        // actually been seen, same honesty rule as everything else here.
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, ResourceType>> KnownResourceHexes =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, ResourceType>>();
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, EnemySighting>> EnemySightings =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, EnemySighting>>();

        private static bool _subscribed;

        // Idempotent — safe to call every new-game setup without risking a doubled subscription
        // (see CitadelSetupController, which calls this alongside VisionSystem.Clear/Configure).
        public static void EnsureSubscribed()
        {
            if (_subscribed)
                return;
            VisionSystem.VisibilityChanged += OnVisibilityChanged;
            _subscribed = true;
        }

        public static void Clear()
        {
            KnownResourceHexes.Clear();
            EnemySightings.Clear();
        }

        private static void OnVisibilityChanged(PlayerSetupData player)
        {
            if (player == null)
                return;

            if (!KnownResourceHexes.TryGetValue(player, out Dictionary<HexCoord, ResourceType> resources))
            {
                resources = new Dictionary<HexCoord, ResourceType>();
                KnownResourceHexes[player] = resources;
            }
            if (!EnemySightings.TryGetValue(player, out Dictionary<HexCoord, EnemySighting> sightings))
            {
                sightings = new Dictionary<HexCoord, EnemySighting>();
                EnemySightings[player] = sightings;
            }

            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(player))
            {
                ResourceType? dominant = AiEconomyPlanner.DominantResourceType(hex);
                if (dominant.HasValue)
                    resources[hex] = dominant.Value;

                ArmyData enemy = ArmyRegistry.AllAt(hex).FirstOrDefault(a => a.Owner != player && BattleInitiator.IsEngageable(a));
                if (enemy != null)
                {
                    sightings[hex] = new EnemySighting
                    {
                        Owner = enemy.Owner,
                        MemberCount = enemy.Members.Count,
                        DefenseSum = enemy.Members.Where(m => !m.IsHero).Sum(m => m.Defense),
                    };
                }
                else
                {
                    // Freshly observed and empty now — corrects any stale sighting rather than
                    // leaving it to linger (see the class's own "исправляет только новое
                    // наблюдение" comment).
                    sightings.Remove(hex);
                }
            }
        }

        // A hex's resource bonus counts as "known" the moment it's ever been merely VISIBLE, not
        // necessarily visited — matches how AiScoutPlanner's own isUndiscoveredResource bonus
        // already treats discovery (fogged vs visible, not visited vs unvisited).
        public static bool IsResourceHexKnown(PlayerSetupData actor, HexCoord hex)
        {
            return KnownResourceHexes.TryGetValue(actor, out Dictionary<HexCoord, ResourceType> set) && set.ContainsKey(hex);
        }

        // Разведка Задача 2's own completion/target read: every known hex whose dominant type
        // matches `type` — callers still need to check BuildingRegistry.FindAt themselves for
        // "already claimed", same live-world-state split AiGoalScorer.ScoreExpandEconomy already
        // draws (memory says WHAT was seen, never whether it's still free).
        public static IEnumerable<HexCoord> KnownResourceHexesOfType(PlayerSetupData actor, ResourceType type)
        {
            if (!KnownResourceHexes.TryGetValue(actor, out Dictionary<HexCoord, ResourceType> set))
                yield break;
            foreach (KeyValuePair<HexCoord, ResourceType> kv in set)
                if (kv.Value == type)
                    yield return kv.Key;
        }

        public static bool HasKnownEnemyWithin(PlayerSetupData actor, HexCoord center, int radius)
        {
            return EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings)
                && sightings.Keys.Any(hex => HexGridMath.Distance(center, hex) <= radius);
        }

        // Same read as HasKnownEnemyWithin, narrowed to sightings whose owner is neutral —
        // Экономика · Задача 1's own "don't build near a neutral garrison" check
        // (AiConfig.neutralBuildAvoidRadius), which cares about neutrals specifically rather than
        // any known hostile army the way HasKnownEnemyWithin itself does.
        public static bool HasKnownNeutralWithin(PlayerSetupData actor, HexCoord center, int radius)
        {
            return EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings)
                && sightings.Any(kv => kv.Value.Owner != null && kv.Value.Owner.IsNeutral
                    && HexGridMath.Distance(center, kv.Key) <= radius);
        }

        public static IEnumerable<KnownEnemySighting> KnownEnemySightingsNear(PlayerSetupData actor,
            IReadOnlyList<HexCoord> ownHexes, int radius)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings))
                yield break;

            foreach (KeyValuePair<HexCoord, EnemySighting> kv in sightings)
                if (ownHexes.Any(own => HexGridMath.Distance(own, kv.Key) <= radius))
                    yield return new KnownEnemySighting(kv.Key, kv.Value.Owner, kv.Value.MemberCount, kv.Value.DefenseSum);
        }

        // One specific hex's own last-known sighting, if any — Разведка Задача 1's own "может
        // напасть на армию послабее" check (see AiScoutPlanner.FindVisitTargetHex) needs exactly
        // this hex, not a radius scan.
        public static KnownEnemySighting? KnownEnemySightingAt(PlayerSetupData actor, HexCoord hex)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings)
                || !sightings.TryGetValue(hex, out EnemySighting sighting))
                return null;
            return new KnownEnemySighting(hex, sighting.Owner, sighting.MemberCount, sighting.DefenseSum);
        }

        public static float KnownGarrisonDefenseAt(PlayerSetupData actor, HexCoord hex)
        {
            return EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings)
                && sightings.TryGetValue(hex, out EnemySighting sighting)
                ? sighting.DefenseSum
                : 0f;
        }
    }
}
