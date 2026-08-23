using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Honest per-player memory of hex CONTENT — the piece VisionSystem itself explicitly does
    // NOT keep (see its own class comment: "Content has no memory either way and re-hides the
    // instant vision leaves"). Subscribes to VisionSystem.VisibilityChanged and, on every
    // recompute, snapshots whatever's on `player`'s own currently-visible hexes into three
    // permanent-until-corrected stores: which hexes are known to carry a resource bonus (and,
    // as of the Разведка Задача 2 pass, which ResourceType — reading the type off an already-
    // VISIBLE hex isn't the cheat AiEconomyPlanner.DominantResourceType's own caller guards
    // against elsewhere, since a real player would see the bonus icon the moment fog lifts too),
    // where an enemy/neutral army was last actually seen, and (as of the Агрессия redesign) which
    // hexes carry a known active Hex Event with a real guard — see KnownEventGuardDefenseAt. Per
    // the project owner's own "Видимость с памятью" principle — stale info is never auto-expired,
    // only overwritten by a fresh observation of that SAME hex (see OnVisibilityChanged's own
    // `sightings.Remove` branch) — except an event actually being consumed, a real world-state
    // change corrected for every player immediately (OnEventConsumed), not left for each to
    // individually re-discover.
    //
    // Deliberately narrow in scope — only the slices AiGoalScorer/AiScoutPlanner/AiTurnController/
    // RaidWeakerArmyTask actually need honesty for right now (resource hexes + type, enemy
    // armies, event guards). Other players' own resource stockpiles stay the project's own
    // documented cheat exception (see AiGoalScorer's own IncomeBehindBonus) and never route
    // through here.
    public static class AiMapMemory
    {
        private class EnemySighting
        {
            public PlayerSetupData Owner;
            public string Name;
            public int MemberCount;
            public float DefenseSum;
            public float AttackSum;
            public List<WorthIt.DefenderProfile> Defenders;
            // The global turn (see _currentTurn/OnTurnStarted below) this sighting was last
            // actually (re)observed — drives expiry in ExpireStaleSightings. Stamped, not left at
            // its default, even for a sighting recorded before the very first OnTurnStarted call
            // (initial army placement) — that default (0) is exactly right, since turn numbering
            // itself starts at 1 (see GameTurnController.TurnNumber).
            public int SeenTurn;
        }

        public readonly struct KnownEnemySighting
        {
            public readonly HexCoord Hex;
            public readonly PlayerSetupData Owner;
            public readonly string Name;
            public readonly int MemberCount;
            public readonly float DefenseSum;
            public readonly float AttackSum;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> Defenders;

            public KnownEnemySighting(HexCoord hex, PlayerSetupData owner, string name, int memberCount, float defenseSum, float attackSum,
                IReadOnlyList<WorthIt.DefenderProfile> defenders)
            {
                Hex = hex;
                Owner = owner;
                Name = name;
                MemberCount = memberCount;
                DefenseSum = defenseSum;
                AttackSum = attackSum;
                Defenders = defenders;
            }
        }

        // Aggregate Attack/Defense sums plus the per-unit DefenderProfile list WorthIt.CanDamageAll
        // needs for its coverage check (see that method's own comment) — shared shape for both a
        // Hex Event's card-stat guard (KnownEventGuards below) and a physical army sighting
        // (EnemySighting above reuses the same three numbers/list, just not through this struct
        // directly since it's already its own class). `Name` — the sighted army's own ArmyData.Name,
        // or a Hex Event guard's own HexEventRegistry.Entry.GuardArmyName; never a per-unit read (see
        // DefenderProfile's own comment — this file only ever remembers the OTHER side as an
        // aggregate), added 2026-08-22 purely for AiAggressionPlanner's own "not enough force" log
        // line (project owner's own report) to name the target instead of just its numbers.
        public readonly struct GuardStrength
        {
            public readonly float Defense;
            public readonly float Attack;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> Defenders;
            public readonly string Name;

            public GuardStrength(float defense, float attack, IReadOnlyList<WorthIt.DefenderProfile> defenders, string name = null)
            {
                Defense = defense;
                Attack = attack;
                Defenders = defenders;
                Name = name;
            }
        }

        // HexCoord -> the dominant ResourceType last observed there (see AiEconomyPlanner.
        // DominantResourceType) — a hex only ever enters this dictionary once its bonus has
        // actually been seen, same honesty rule as everything else here.
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, ResourceType>> KnownResourceHexes =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, ResourceType>>();
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, EnemySighting>> EnemySightings =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, EnemySighting>>();
        // HexCoord -> guard's own card-stat strength, for every Hex Event this player has ever
        // SEEN while it still had an unconsumed guard — Агрессия's own "known event with guard"
        // half of RaidWeakerArmyTask's target pool (see that class's own FindTarget). Same
        // "видимость с памятью" honesty rule as everything else here: a hex only enters this once
        // actually visible AND HexEventRegistry.HasActiveEvent(hex) AND it carries a real guard
        // (an unguarded event is never a combat target, nothing to remember here for it).
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, GuardStrength>> KnownEventGuards =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, GuardStrength>>();

        private static bool _subscribed;
        // Global game turn (GameTurnController.TurnNumber, same one AiTurnContext.TurnNumber
        // snapshots) as of the most recent OnTurnStarted call — used only to stamp/expire
        // EnemySighting.SeenTurn (see that field's own comment). Not a live reference, just a
        // plain int updated once per AI player's own turn, same shape as AiTurnContext.TurnNumber
        // itself.
        private static int _currentTurn;

        // Idempotent — safe to call every new-game setup without risking a doubled subscription
        // (see CitadelSetupController, which calls this alongside VisionSystem.Clear/Configure).
        public static void EnsureSubscribed()
        {
            if (_subscribed)
                return;
            VisionSystem.VisibilityChanged += OnVisibilityChanged;
            // The event itself being cleared (guard actually beaten, reward claimed) is a real
            // world-state change, not a per-player illusion — every player's own memory of it
            // corrects immediately rather than waiting for each to individually re-observe the
            // hex (see OnEventConsumed's own comment).
            HexEventRegistry.EventConsumed += OnEventConsumed;
            _subscribed = true;
        }

        public static void Clear()
        {
            KnownResourceHexes.Clear();
            EnemySightings.Clear();
            KnownEventGuards.Clear();
            _currentTurn = 0;
        }

        // Called once, right at the top of AiTurnController.RunTurn, before that turn's own
        // Decide loop ever reads memory — stamps _currentTurn for every EnemySighting recorded
        // from this point on (OnVisibilityChanged below) AND expires `actor`'s own enemy-army
        // sightings last (re)observed more than AiConfig.enemySightingMemoryTurns turns ago (see
        // that field's own comment — resource hexes/event guards are NOT touched here, both stay
        // permanent-until-corrected). Scoped to `actor` alone rather than sweeping every player's
        // memory at once — this only ever needs to be accurate for the player whose own Decide
        // loop is about to read it; every other player's memory gets swept the same way at the
        // top of their own next RunTurn instead.
        public static void OnTurnStarted(PlayerSetupData actor, int turnNumber)
        {
            _currentTurn = turnNumber;
            if (actor == null || !EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings))
                return;

            List<HexCoord> stale = null;
            foreach (KeyValuePair<HexCoord, EnemySighting> kv in sightings)
            {
                if (turnNumber - kv.Value.SeenTurn > AiConfig.enemySightingMemoryTurns)
                    (stale ?? (stale = new List<HexCoord>())).Add(kv.Key);
            }
            if (stale == null)
                return;
            foreach (HexCoord hex in stale)
                sightings.Remove(hex);
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
            if (!KnownEventGuards.TryGetValue(player, out Dictionary<HexCoord, GuardStrength> eventGuards))
            {
                eventGuards = new Dictionary<HexCoord, GuardStrength>();
                KnownEventGuards[player] = eventGuards;
            }

            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(player))
            {
                ResourceType? dominant = AiEconomyPlanner.DominantResourceType(hex);
                if (dominant.HasValue)
                    resources[hex] = dominant.Value;

                ArmyData enemy = ArmyRegistry.AllAt(hex).FirstOrDefault(a => a.Owner != player && BattleInitiator.IsEngageable(a));
                if (enemy != null)
                {
                    List<UnitData> nonHero = enemy.Members.Where(m => !m.IsHero).ToList();
                    sightings[hex] = new EnemySighting
                    {
                        Owner = enemy.Owner,
                        Name = enemy.Name,
                        MemberCount = enemy.Members.Count,
                        DefenseSum = nonHero.Sum(m => m.Defense),
                        AttackSum = nonHero.Sum(m => m.Attack),
                        // Full per-unit snapshot (2026-08-22, project owner's own call: "если мы
                        // когда-то видели армию значит мы видели её состав и знаем всё о ней кроме
                        // текущего состояния") — Attack/Initiative alongside Defense/CeramicArmor/
                        // TypeTags now, so WorthIt's own full-roster Monte Carlo (WinChance's
                        // DefenderProfile-list overload) can actually play this army out round by
                        // round instead of only reading its aggregate sums. HitPointsMax, never
                        // HitPointsCurrent — see DefenderProfile's own comment for why: nothing
                        // here can honestly know the CURRENT HP of an army last seen turns ago,
                        // only what it looked like fully healed.
                        Defenders = nonHero.Select(m => new WorthIt.DefenderProfile(m.Defense, m.HasAbility(UnitAbilities.CeramicArmor),
                            m.TypeTags.ToList(), m.Attack, m.HitPointsMax, m.Initiative)).ToList(),
                        SeenTurn = _currentTurn,
                    };
                }
                else
                {
                    // Freshly observed and empty now — corrects any stale sighting rather than
                    // leaving it to linger (see the class's own "исправляет только новое
                    // наблюдение" comment).
                    sightings.Remove(hex);
                }

                HexEventRegistry.Entry eventEntry = HexEventRegistry.HasActiveEvent(hex) ? HexEventRegistry.FindAt(hex) : null;
                if (eventEntry != null && eventEntry.ResolvedGuardMembers.Count > 0)
                {
                    // Same flat card-stat sum WorthIt/AiEventPlanner.ShouldExplore already use —
                    // the guard is never a live ArmyData until Explore is chosen (see
                    // HexEventRegistry.Entry's own comment), so card stats are all there is to
                    // read.
                    var guardMembers = eventEntry.ResolvedGuardMembers
                        .Where(g => g.card != null && g.card.cardType != CardType.Hero).ToList();
                    float defense = guardMembers.Sum(g => g.card.defenseRating * g.count);
                    float attack = guardMembers.Sum(g => g.card.attack * g.count);
                    // One DefenderProfile per PHYSICAL copy (repeated by g.count), not one per
                    // card type — a card's own stats are already fully known deterministically
                    // (it's a static card-guard, not fog-of-war memory of a moving army), but
                    // WorthIt's full-roster Monte Carlo needs one real combatant/HP-pool per copy
                    // to actually play a "3 Grunts" guard out as three separate units, the same way
                    // a real fight against them would. CanDamageAll's own coverage check doesn't
                    // care about the duplication (it only asks "is there a counter for this profile
                    // anywhere", repeats are harmless there).
                    var defenders = guardMembers.SelectMany(g => Enumerable.Repeat(new WorthIt.DefenderProfile(g.card.defenseRating,
                        g.card.grantedAbilities != null && g.card.grantedAbilities.Contains(UnitAbilities.CeramicArmor),
                        g.card.unitTypeTags, g.card.attack, g.card.hitPoints, g.card.initiative), g.count)).ToList();
                    eventGuards[hex] = new GuardStrength(defense, attack, defenders, eventEntry.GuardArmyName);
                }
                else
                {
                    eventGuards.Remove(hex);
                }
            }
        }

        // The event's own guard just got beaten for real (reward claimed) — a genuine world-state
        // change, so every player's own memory of it is corrected immediately rather than left to
        // go stale until each individually re-observes the hex.
        private static void OnEventConsumed(HexCoord hex)
        {
            foreach (Dictionary<HexCoord, GuardStrength> eventGuards in KnownEventGuards.Values)
                eventGuards.Remove(hex);
        }

        // A hex's resource bonus counts as "known" the moment it's ever been merely VISIBLE, not
        // necessarily visited — matches how AiScoutPlanner's own isUndiscoveredResource bonus
        // already treats discovery (fogged vs visible, not visited vs unvisited).
        public static bool IsResourceHexKnown(PlayerSetupData actor, HexCoord hex)
        {
            return KnownResourceHexes.TryGetValue(actor, out Dictionary<HexCoord, ResourceType> set) && set.ContainsKey(hex);
        }

        public static bool HasKnownEnemyWithin(PlayerSetupData actor, HexCoord center, int radius)
        {
            return EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings)
                && sightings.Keys.Any(hex => HexGridMath.Distance(center, hex) <= radius);
        }

        // Same read as HasKnownEnemyWithin, narrowed to sightings whose owner is neutral —
        // Экономика · Задача 1's own "don't build near a neutral garrison" check
        // (AiConfig.neutralBuildTriggerRadius), which cares about neutrals specifically rather than
        // any known hostile army the way HasKnownEnemyWithin itself does.
        public static bool HasKnownNeutralWithin(PlayerSetupData actor, HexCoord center, int radius)
        {
            return EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings)
                && sightings.Any(kv => kv.Value.Owner != null && kv.Value.Owner.IsNeutral
                    && HexGridMath.Distance(center, kv.Key) <= radius);
        }

        // Every known neutral-army hex on the whole map, no radius — RaidWeakerArmyTask's own
        // target pool isn't wavefront/radius-bounded like Разведка's (see that class's own class
        // comment), it just scores every known target by raw distance from the citadel.
        public static IEnumerable<KnownEnemySighting> AllKnownNeutralSightings(PlayerSetupData actor)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings))
                yield break;
            foreach (KeyValuePair<HexCoord, EnemySighting> kv in sightings)
                if (kv.Value.Owner != null && kv.Value.Owner.IsNeutral)
                    yield return new KnownEnemySighting(kv.Key, kv.Value.Owner, kv.Value.Name, kv.Value.MemberCount, kv.Value.DefenseSum,
                        kv.Value.AttackSum, kv.Value.Defenders);
        }

        // Every known non-neutral-army hex on the whole map, no radius — AiDefencePlanner's own
        // Patrol target picker (FindPatrolTarget/RandomKnownEnemyHex), same "знаем направление, не
        // содержимое хекса" cheat the project owner explicitly sanctioned for Patrol's first target
        // of a fresh cycle. Mirrors AllKnownNeutralSightings' own shape, just the opposite owner
        // filter.
        public static IEnumerable<KnownEnemySighting> AllKnownEnemySightings(PlayerSetupData actor)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings))
                yield break;
            foreach (KeyValuePair<HexCoord, EnemySighting> kv in sightings)
                if (kv.Value.Owner != null && !kv.Value.Owner.IsNeutral)
                    yield return new KnownEnemySighting(kv.Key, kv.Value.Owner, kv.Value.Name, kv.Value.MemberCount, kv.Value.DefenseSum,
                        kv.Value.AttackSum, kv.Value.Defenders);
        }

        public static IEnumerable<KnownEnemySighting> KnownEnemySightingsNear(PlayerSetupData actor,
            IReadOnlyList<HexCoord> ownHexes, int radius)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings))
                yield break;

            foreach (KeyValuePair<HexCoord, EnemySighting> kv in sightings)
                if (ownHexes.Any(own => HexGridMath.Distance(own, kv.Key) <= radius))
                    yield return new KnownEnemySighting(kv.Key, kv.Value.Owner, kv.Value.Name, kv.Value.MemberCount, kv.Value.DefenseSum,
                        kv.Value.AttackSum, kv.Value.Defenders);
        }

        // One specific hex's own last-known sighting, if any — RaidWeakerArmyTask's own
        // RequiredStrengthAt/IsStillValidTarget need exactly this hex, not a radius scan.
        public static KnownEnemySighting? KnownEnemySightingAt(PlayerSetupData actor, HexCoord hex)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings)
                || !sightings.TryGetValue(hex, out EnemySighting sighting))
                return null;
            return new KnownEnemySighting(hex, sighting.Owner, sighting.Name, sighting.MemberCount, sighting.DefenseSum,
                sighting.AttackSum, sighting.Defenders);
        }

        public static float KnownGarrisonDefenseAt(PlayerSetupData actor, HexCoord hex)
        {
            return EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings)
                && sightings.TryGetValue(hex, out EnemySighting sighting)
                ? sighting.DefenseSum
                : 0f;
        }

        // Null = no known active guarded event at this hex (never seen one, or it's since been
        // consumed — see OnEventConsumed). RaidWeakerArmyTask's own event-guard half of a target's
        // required strength (see that class's own FindTarget/RequiredStrengthAt — takes the max of
        // this and KnownGarrisonDefenseAt for a hex, not their sum, since a physical neutral army
        // sharing this hex and this event's own card-guard are two separate fights, never fought
        // at once).
        public static GuardStrength? KnownEventGuardStrengthAt(PlayerSetupData actor, HexCoord hex)
        {
            return KnownEventGuards.TryGetValue(actor, out Dictionary<HexCoord, GuardStrength> eventGuards)
                && eventGuards.TryGetValue(hex, out GuardStrength strength)
                ? strength
                : (GuardStrength?)null;
        }

        public static float? KnownEventGuardDefenseAt(PlayerSetupData actor, HexCoord hex) => KnownEventGuardStrengthAt(actor, hex)?.Defense;

        // Same guard, its own card-stat Attack sum instead of Defense — WorthIt.Score's own "how
        // hard would the guard hit back" half (see RaidWeakerArmyTask.RequiredStrengthAt).
        public static float? KnownEventGuardAttackAt(PlayerSetupData actor, HexCoord hex) => KnownEventGuardStrengthAt(actor, hex)?.Attack;

        // Every hex this player has ever seen an active guarded event on — RaidWeakerArmyTask's
        // own candidate-gatherer needs to enumerate these the same way it enumerates
        // EnemySightings via KnownEnemySightingsNear, just without a radius (Агрессия's own
        // target pool isn't wavefront-bounded — see that class's own class comment).
        public static IEnumerable<HexCoord> KnownEventGuardHexes(PlayerSetupData actor)
        {
            return KnownEventGuards.TryGetValue(actor, out Dictionary<HexCoord, GuardStrength> eventGuards)
                ? eventGuards.Keys
                : Enumerable.Empty<HexCoord>();
        }

        // How many individual non-hero members, across every currently-known ARMY sighting for
        // `actor` (physical armies only — EnemySightings, not KnownEventGuards' own card-stat
        // guards, which aren't really "an enemy army" in the sense this counts), carry `tag` —
        // AiManagementPlanner's own counter-tech PlayCard scoring reads this (Hyperkinetic once
        // enough known Armored targets are on record, Pyrokinetic for Bio — see that class's own
        // comment) to prefer a card that would actually counter what's already been scouted.
        // Same "видимость с памятью" honesty as every other read here — only ever counts a
        // sighting this player has actually observed, corrected/overwritten the same way
        // DefenseSum/AttackSum already are, never the true enemy roster.
        public static int KnownEnemyTypeTagCount(PlayerSetupData actor, UnitTypeTag tag)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<HexCoord, EnemySighting> sightings))
                return 0;
            int count = 0;
            foreach (EnemySighting sighting in sightings.Values)
            {
                if (sighting.Defenders == null)
                    continue;
                foreach (WorthIt.DefenderProfile defender in sighting.Defenders)
                    if (defender.TypeTags.Contains(tag))
                        count++;
            }
            return count;
        }
    }
}
