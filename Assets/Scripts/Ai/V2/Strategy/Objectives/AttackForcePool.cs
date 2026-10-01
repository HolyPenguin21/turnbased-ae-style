using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // 2026-10-01 (user decision) — the force an Attack can actually assemble, and so the ONE
    // measure of the Attack bar (> 80% of its peak), the strike roster and the pool a
    // preparation composes from. The same peak greedy as TotalMilitaryPotential
    // (AiPower.NestedPotentialsOf), over the AVAILABLE pool only:
    //   · in: every field army — the Attack operation's own, free ones and those another
    //     operation holds (Raid, ActiveDefence, Economy, Development: they come back and a
    //     preparation draws from them) —, the garrison bodies a garrison may spare (its defence
    //     floor stays), a hero a garrison may let go, every held card (with its attached
    //     equipment) and the remaining deck;
    //   · out: explicit scouts (lone scouts and armies a Scout mission holds), the garrison's
    //     mandatory defence, garrison heroes (Support tag) and facility operators — they never
    //     lead a strike force.
    // The army set is the one FieldStrikePotential reads, so the mobilization gate compares the
    // field strike force and this peak over the same armies (2026-10-01, user decision: a busy
    // Raid army counted on one side only opened the gate for one pass, Sable T14).
    // TotalMilitaryPotential stays the whole-deck ceiling (reserve, readiness, Panel_Data).
    // Built once per snapshot (ConditionalWeakTable), from the live own armies of that moment.
    internal sealed class AttackForcePool
    {
        public float Peak;
        public List<StrikeRosterSlot> Roster = new List<StrikeRosterSlot>();
        public List<StrikeRosterCandidate> Pool = new List<StrikeRosterCandidate>();
        public Dictionary<string, int> KeyCounts = new Dictionary<string, int>();


        // Explicit scouts on a mission: the mover of every live Scout intent. Out of both Attack
        // force measures (this pool and WorldAnalysis.FieldStrikePotential).
        internal static HashSet<int> ScoutMissionArmyIds(PlayerSetupData player)
        {
            var scouts = new HashSet<int>();
            if (player == null)
                return scouts;
            foreach (MissionIntent i in MissionIntentRegistry.GetOrCreate(player).All)
                if (i != null && i.Status == IntentStatus.Active && i.Kind == MissionKind.Scout
                    && i.PreferredMoverArmyId.HasValue)
                    scouts.Add(i.PreferredMoverArmyId.Value);
            return scouts;
        }

        // Built once per snapshot, inside WorldAnalysis.BuildForceMeasures (self.Hand/Deck/
        // AvailablePower are already set), and stored on SelfSnapshot (AttackPeak, StrikeRoster,
        // StrikePool, StrikePoolKeyCounts): no second cache to invalidate.
        internal static AttackForcePool Build(PlayerSetupData player, SelfSnapshot self)
        {
            HashSet<int> scouts = ScoutMissionArmyIds(player);
            HexCoord citadel = AiTurnController.GarrisonHexFor(player);
            if (player == null || self == null)
                return new AttackForcePool();
            var map = new List<StrikeRosterCandidate>();
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player).Where(x => x != null).OrderBy(x => x.Id))
            {
                if (a.IsPrison || a.IsAirfield || AviationRules.IsAirArmy(a) || AiArmyRoles.IsSoloRecce(a)
                    || scouts.Contains(a.Id))
                    continue;
                HashSet<int> spare = null;
                List<UnitData> garrisonBodies = null;
                if (a.IsGarrison)
                {
                    garrisonBodies = a.Members.Where(u => u != null && u.IsGroundCombatant).ToList();
                    spare = AiArmyRoles.SpareableBodies(garrisonBodies,
                        set => AiPower.EffectiveArmyPower(set.ToList()),
                        AiArmyRoles.GarrisonDefenceFloor(self.AvailablePower, a.Hex.Equals(citadel)));
                }
                foreach (UnitData u in a.Members)
                {
                    if (u == null || u.IsPrisoner || u.IsAviation || u.IsSummoned)
                        continue;
                    if (u.IsHero)
                    {
                        if (AiArmyRoles.IsGarrisonHero(u) || AiArmyRoles.IsFacilityOperator(player, a.Hex, u)
                            || (a.IsGarrison && a.Members.Count <= 1))
                            continue;
                    }
                    else if (spare != null && !spare.Contains(garrisonBodies.IndexOf(u)))
                        continue;
                    map.Add(new StrikeRosterCandidate(new StrikeRosterSlot(StrikeRoster.UnitKey(u),
                        u.IsHero, AiPower.UnitPower(u), ForceSource.Map), AiPower.ToPowerUnit(u)));
                }
            }

            var bodies = new List<StrikeRosterCandidate>();
            var heroes = new List<StrikeRosterCandidate>();
            void Add(CardDefinition d, AiPower.PowerUnit pu, ForceSource source)
            {
                if (d == null || d.isAviation || (d.cardType != CardType.Unit && d.cardType != CardType.Hero))
                    return;
                if (d.cardType == CardType.Hero && d.unitTypeTags != null
                    && d.unitTypeTags.Contains(UnitTypeTag.Support))
                    return;
                var c = new StrikeRosterCandidate(new StrikeRosterSlot(StrikeRoster.CardKey(d),
                    pu.IsHero, pu.BasePower, source), pu);
                (d.cardType == CardType.Hero ? heroes : bodies).Add(c);
            }
            foreach (CardData c in self.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
                if (c?.Definition != null) Add(c.Definition, AiPower.ToPowerUnit(c), ForceSource.Hand);
            foreach (CardDefinition d in self.Deck ?? (IReadOnlyList<CardDefinition>)System.Array.Empty<CardDefinition>())
                if (d != null) Add(d, AiPower.ToPowerUnit(d), ForceSource.Deck);

            AiPower.ForcePotentials ceilings = AiPower.NestedPotentialsOf(map, bodies, heroes,
                x => x.Unit, out List<StrikeRosterCandidate> peak);
            var pool = new AttackForcePool
            {
                Peak = ceilings.Total,
                Roster = peak.Select(x => x.Slot).ToList(),
                Pool = map.Concat(bodies).Concat(heroes).ToList(),
            };
            foreach (StrikeRosterCandidate c in pool.Pool)
                if (c.Slot.Key != null)
                {
                    pool.KeyCounts.TryGetValue(c.Slot.Key, out int n);
                    pool.KeyCounts[c.Slot.Key] = n + 1;
                }
            return pool;
        }
    }
}
