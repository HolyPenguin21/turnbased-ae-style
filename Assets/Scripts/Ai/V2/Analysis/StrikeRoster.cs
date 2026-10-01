using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Units;

namespace Game.Ai.V2
{
    // One position of the strike-force target roster: the card (by its identity key) that the
    // peak stack (SelfSnapshot.TotalMilitaryPotential) is made of, where it is now, and its power.
    public readonly struct StrikeRosterSlot
    {
        public readonly string Key;
        public readonly bool IsHero;
        public readonly float Power;
        public readonly ForceSource Source;

        public StrikeRosterSlot(string key, bool isHero, float power, ForceSource source)
        {
            Key = key;
            IsHero = isHero;
            Power = power;
            Source = source;
        }
    }

    // One pool candidate: its roster slot and the PowerUnit the peak greedy reads.
    public readonly struct StrikeRosterCandidate
    {
        public readonly StrikeRosterSlot Slot;
        public readonly AiPower.PowerUnit Unit;

        public StrikeRosterCandidate(StrikeRosterSlot slot, AiPower.PowerUnit unit)
        {
            Slot = slot;
            Unit = unit;
        }
    }

    // 2026-10-01 (user decision) — the strike force is gathered toward a concrete roster: the
    // composition of the strongest army the whole deck can form (AiPower.NestedPotentialsOf, the
    // same greedy as the > 80% bar). A preparation freezes it (AttackIntent.TargetRoster) and
    //  · waits only for a source of a MISSING position (or an equivalent: a card at least as
    //    strong as the weakest missing position) — never for a body the roster does not need;
    //  · lets Housekeeping release a host body that is not in the roster when a real source of a
    //    missing position needs its slot, and take same-hex roster bodies in
    //    (ReorgViability.PreparationRosterWaste).
    // Positions are a multiset by card key: two Medium Tanks are two positions.
    public static class StrikeRoster
    {
        // 2026-10-01 (user decision, variant B) — a preparation's roster is composed under the
        // commander its host actually has (or none): the same greedy as the peak
        // (AiPower.ComposeStackOf), its bodies from the whole pool, capped by that commander's
        // CommandRating (`capacity` counts the commander, as ArmyData.Capacity does). A stronger
        // commander standing elsewhere is fetched by its own preparation step; once it leads the
        // host the roster is re-frozen under it.
        public static List<StrikeRosterSlot> ComposeUnder(IReadOnlyList<StrikeRosterCandidate> pool,
            StrikeRosterCandidate? commander, int capacity, out float power)
        {
            var bodies = (pool ?? System.Array.Empty<StrikeRosterCandidate>())
                .Where(c => !c.Unit.IsHero).ToList();
            List<StrikeRosterCandidate> pick = AiPower.ComposeStackOf(bodies, c => c.Unit,
                System.Math.Max(1, capacity), commander.HasValue, commander.GetValueOrDefault());
            power = AiPower.EffectiveArmyPower(pick.Select(c => c.Unit).ToList());
            return pick.Select(c => c.Slot).ToList();
        }

        // The pool candidate standing for a live hero (by its card key), or a fresh one built
        // from the unit itself when the pool has none.
        public static StrikeRosterCandidate CommanderCandidate(UnitData hero) =>
            new StrikeRosterCandidate(new StrikeRosterSlot(UnitKey(hero), true, 0f, ForceSource.Map),
                AiPower.ToPowerUnit(hero));

        // 2026-10-01 (user decision) — a Recce body the peak stack itself picked (an RC Vehicle with
        // a Plasma Cannon outfights most cards) is a combat body for every FieldCombatPower demand,
        // not only the preparation host's: scouts that are not among the peak's bodies stay Recon.
        public static bool IsPeakBody(WorldSnapshot snap, CardDefinition d)
        {
            IReadOnlyList<StrikeRosterSlot> roster = snap?.Self?.StrikeRoster;
            if (roster == null || d == null || d.cardType != CardType.Unit)
                return false;
            string key = CardKey(d);
            for (int i = 0; i < roster.Count; i++)
                if (!roster[i].IsHero && roster[i].Key == key)
                    return true;
            return false;
        }

        public static string CardKey(CardDefinition d) =>
            d == null ? null : string.IsNullOrEmpty(d.authoredKey) ? d.displayName : d.authoredKey;

        // A live unit is its originating card; a unit without one matches only itself.
        public static string UnitKey(UnitData u) =>
            u == null ? null : CardKey(u.OriginatingCard) ?? $"unit#{u.RuntimeId}";

        // Target positions the members do not fill yet (multiset difference, by key).
        public static List<StrikeRosterSlot> Missing(IReadOnlyList<StrikeRosterSlot> target,
            IEnumerable<UnitData> members)
        {
            var have = new Dictionary<string, int>();
            foreach (UnitData u in members ?? Enumerable.Empty<UnitData>())
            {
                string k = UnitKey(u);
                if (k == null) continue;
                have.TryGetValue(k, out int n);
                have[k] = n + 1;
            }
            var missing = new List<StrikeRosterSlot>();
            foreach (StrikeRosterSlot slot in target ?? System.Array.Empty<StrikeRosterSlot>())
            {
                if (have.TryGetValue(slot.Key, out int n) && n > 0)
                    have[slot.Key] = n - 1;
                else
                    missing.Add(slot);
            }
            return missing;
        }

        // Host bodies the target roster does not contain (multiset), weakest first. Heroes are
        // the ATK-F03 excess-hero rule's business, never listed here.
        public static List<UnitData> NonTargetBodies(IReadOnlyList<StrikeRosterSlot> target,
            IEnumerable<UnitData> members)
        {
            var need = new Dictionary<string, int>();
            foreach (StrikeRosterSlot slot in target ?? System.Array.Empty<StrikeRosterSlot>())
            {
                need.TryGetValue(slot.Key, out int n);
                need[slot.Key] = n + 1;
            }
            var surplus = new List<UnitData>();
            foreach (UnitData u in (members ?? Enumerable.Empty<UnitData>())
                         .Where(u => u != null && !u.IsHero && !u.IsPrisoner)
                         .OrderByDescending(u => AiPower.UnitPower(u)).ThenBy(u => u.RuntimeId))
            {
                string k = UnitKey(u);
                if (k != null && need.TryGetValue(k, out int n) && n > 0)
                    need[k] = n - 1;
                else
                    surplus.Add(u);
            }
            surplus.Reverse();
            return surplus;
        }

        // Is this card a source worth waiting for: it fills a missing position, or it is an
        // equivalent (at least as strong as the weakest missing body position). With no target
        // roster (none frozen) every strengthening card stays a witness, as before.
        public static bool FillsMissing(IReadOnlyList<StrikeRosterSlot> missing, CardDefinition d,
            float cardPower)
        {
            if (missing == null || d == null)
                return true;
            string key = CardKey(d);
            bool hero = d.cardType == CardType.Hero;
            if (missing.Any(m => m.Key == key && m.IsHero == hero))
                return true;
            List<StrikeRosterSlot> bodies = missing.Where(m => !m.IsHero).ToList();
            return !hero && bodies.Count > 0 && cardPower + 0.001f >= bodies.Min(m => m.Power);
        }
    }
}
