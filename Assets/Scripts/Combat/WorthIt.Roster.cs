using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // The simulated combatant (BattleUnit), roster conversion and the per-exchange ability
    // mechanics SimulateOneBattle applies: splash, bodyguard, drill, healing, explosion, hero death.
    public static partial class WorthIt
    {
        // One armed exchange as the real battle resolves it: both pools rolled, then the Fate duel
        // (FateDuelOrder — defender first, sides alternate while anyone spends; each
        // spend rerolls the first miss with an ordinary die; only attackers stop after a failed
        // reroll), every spend decided by the ONE policy FateDuelAi owns. Returns the damage after
        // the canonical ability modifier chain.
        private static BattleSimExchangeOutcome ResolveExchange(BattleUnit actor, BattleUnit target,
            ref int actorFate, ref int targetFate, System.Random rng, AbilityMagnitudes magnitudes)
        {
            return BattleSimulationKernel.ResolveExchange(
                Mathf.RoundToInt(actor.Attack), Mathf.RoundToInt(target.Defense),
                actor.Abilities, target.TypeTags, target.Abilities,
                ref actorFate, ref targetFate,
                Mathf.Max(1, Mathf.CeilToInt(target.Hp)),
                magnitudes, rng);
        }

        // ---- Full round-by-round Monte Carlo (2026-08-22, project owner's own call) ----
        //
        // Everything above this point still only ever plays out ONE simultaneous exchange per
        // trial, because that used to be all the map genuinely knew about the other side — an
        // aggregate Attack/Defense sum, no composition, no HP. That's no longer true: once an
        // enemy army has actually been SEEN, AiMapMemory now remembers its full per-unit roster
        // (Attack/Defense/HitPoints/Initiative, not just Defense — see DefenderProfile's own
        // comment), and a "cheat" read (AiDefencePlanner.CheatEstimateRaiderThreat) already has the
        // real live roster directly. Whenever a real per-unit roster is available on BOTH sides,
        // WinChance below plays MonteCarloTrials complete battles to actual HP-zero instead of one
        // flat exchange — same round structure BattleTurnOrder uses (shuffle for a random
        // Initiative tie-break, then sort descending), same tokens, just with a
        // RANDOM target each attack rather than BattleTargetSelector's scored pick — this class
        // still never has a live grid/position/range to be smart about (see this file's own top
        // comment), only a remembered/cheat-read roster.
        private struct BattleUnit
        {
            public bool SingleUseUnit;
            public bool CanDisruptBuilding;
            public EquipmentConsumption Consumption;
            public float Attack;
            public float Defense;
            public IReadOnlyList<string> Abilities;
            public IReadOnlyList<UnitTypeTag> TypeTags;
            public int Initiative;
            public float Hp;
            public bool IsHero;
            public int HeroFate;
            public bool IsSummoned;
            // Attack range (-1: unknown). Positions do not exist here, so a Range-1 (or unknown)
            // attacker is the one that stands within reach of a Counterattack answer.
            public int Range;

            public bool HasAbility(string ability) => Abilities != null && Abilities.Contains(ability);

            // True max HP the unit entered this simulated battle with — separate from Hp (which
            // Estimate() below mutates round by round) because CriticalAfterBattleChance needs to
            // compare a SURVIVOR's final Hp against its own real max, not the (possibly already
            // wounded) starting Hp of this one simulated fight. For the DefenderProfile-only
            // conversion below (ToBattleUnits), nothing here knows a real MaxHp distinct from the
            // remembered/assumed starting HitPoints, so it defaults to the same value as Hp — see
            // ToAttackerBattleUnits for the one path (our own live roster) that fills in the real one.
            public float MaxHp;
        }

        private static void ConsumeRosterEquipment(List<BattleUnit> side, int index)
        {
            BattleUnit unit = side[index];
            if (unit.SingleUseUnit) { unit.Hp = 0f; side[index] = unit; return; }
            EquipmentConsumption change = unit.Consumption;
            if (change == null) return;
            unit.Attack += change.Delta(EquipmentStat.Attack);
            unit.Defense += change.Delta(EquipmentStat.Defense);
            unit.Initiative += change.Delta(EquipmentStat.Initiative);
            unit.Range += change.Delta(EquipmentStat.Range);
            unit.HeroFate = Mathf.Max(0, unit.HeroFate + change.Delta(EquipmentStat.Fate));
            unit.Hp = change.RemainingHitPoints(unit.Hp, unit.MaxHp, out unit.MaxHp);
            unit.Abilities = change.ApplyAbilities(unit.Abilities);
            unit.Consumption = null;
            side[index] = unit;
        }

        // Longest a single simulated battle plays before being scored a draw — map-sized rosters
        // (a handful of units a side) resolve in a handful of rounds almost always; this is
        // generous headroom for a Monte Carlo trial to terminate, not a tuned balance number.
        private const int MaxSimulatedRounds = 50;

        private static List<BattleUnit> ToBattleUnits(IReadOnlyCollection<DefenderProfile> profiles,
            float extraDefense = 0f, int initiativeBonus = 0, SideCommander commander = default)
        {
            var list = new List<BattleUnit>();
            if (profiles == null)
                return list;
            foreach (DefenderProfile p in profiles)
            {
                list.Add(ToBattleUnit(p, extraDefense, initiativeBonus));
            }
            int represented = list.Count(u => u.IsHero && IsSupportHero(u.Abilities));
            for (int i = represented; i < (commander.SupportHeroes?.Count ?? 0); i++)
                list.Add(ToBattleUnit(commander.SupportHeroes[i], extraDefense, 0));
            return list;
        }

        // Both key construction and miss computation use this exact conversion. A cache hit
        // needs its values, not a temporary list containing them.
        private static BattleUnit ToBattleUnit(DefenderProfile p, float extraDefense, int initiativeBonus)
        {
            float hp = Mathf.Max(1f, p.HitPoints);
            return new BattleUnit
            {
                Attack = p.Attack,
                Defense = p.Defense + extraDefense,
                Abilities = p.Abilities,
                TypeTags = p.TypeTags,
                Initiative = p.IsHero ? p.Initiative : p.Initiative + initiativeBonus,
                Hp = hp,
                MaxHp = Mathf.Max(hp, p.MaxHitPoints),
                IsHero = p.IsHero,
                HeroFate = p.FateMax,
                IsSummoned = p.IsSummoned,
                Range = p.Range,
                Consumption = p.Consumption,
                SingleUseUnit = p.SingleUseUnit,
                CanDisruptBuilding = p.CanDisruptBuilding && p.HitPoints > 0f,
            };
        }

        // Attacker BattleUnits built straight off the real ArmyData roster rather than through
        // DefenderProfile — FromLiveUnit's own comment already explains DefenderProfile.HitPoints
        // means CURRENT hp for our own side, so it can't also carry the unit's true MaxHp that
        // Estimate()'s CriticalAfterBattleChance needs. Non-hero only, same convention every other
        // attacker-side read in this file uses.
        private static List<BattleUnit> ToAttackerBattleUnits(ArmyData attacker, int initiativeBonus)
        {
            var list = new List<BattleUnit>();
            if (attacker == null)
                return list;
            foreach (UnitData m in TacticalTargetsOf(attacker.Members))
                list.Add(new BattleUnit
                {
                    Attack = m.Attack,
                    Defense = m.IsHero ? Mathf.Max(0, m.FateMax) : m.Defense,
                    Abilities = m.Abilities.ToList(),
                    TypeTags = m.TypeTags.ToList(),
                    Initiative = m.IsHero ? m.Initiative : m.Initiative + initiativeBonus,
                    Hp = Mathf.Max(1f, m.HitPointsCurrent),
                    MaxHp = Mathf.Max(1f, m.HitPointsMax),
                    IsHero = m.IsHero,
                    HeroFate = Mathf.Max(0, m.FateMax),
                    IsSummoned = m.IsSummoned,
                    Range = m.Range,
                    Consumption = EquipmentSystem.Consumption(m),
                    SingleUseUnit = ExplosionAttack.IsSingleUseUnit(m),
                    CanDisruptBuilding = !m.IsAviation && !m.IsPrisoner,
                });
            return list;
        }

        private static void AppendFreshBattleSummons(List<BattleUnit> side, int initiativeBonus)
        {
            if (side == null || side.Any(u => u.IsSummoned))
                return;
            UnitAbilityCatalog catalog = UnitAbilityCatalog.Active;
            CardDefinition template = catalog != null ? catalog.ResolveRaiseTheRotsCard() : null;
            if (template == null)
                return;

            int summoners = side.Count(u => u.Hp > 0f && !u.IsSummoned
                && u.Abilities != null && u.Abilities.Contains(UnitAbilities.RaiseTheRots));
            int count = Mathf.Min(Mathf.Max(0, BattleGrid.Columns * 2 - side.Count),
                summoners * Mathf.Max(0, catalog.raiseTheRotsUnitsPerSummoner));
            if (count <= 0)
                return;

            DefenderProfile profile = ProfileFromCard(template);
            for (int i = 0; i < count; i++)
                side.AddRange(ToBattleUnits(new[] { profile }, 0f, initiativeBonus));
        }

        // Aggregate-roster mirror of BattleScreenUI.Combat.cs's ResolveSplashSkills for
        // SimulateOneBattle. Half (floored) of the primary damage minus the victim's own
        // CeramicArmor (Option A — the attacker's offensive bonuses are already in `primaryDamage`),
        // to up to two random OTHER living enemies (Splash) and/or one random living Bio enemy
        // (Scorcher). Positions don't exist in this model, so "adjacent" is approximated as
        // "random other body". Only runs for a Splash/Scorcher actor.
        private static void ApplyRosterSplash(List<BattleUnit> enemyList, int primaryTargetIndex,
            BattleUnit actor, int primaryDamage, System.Random rng, AbilityMagnitudes magnitudes,
            List<int> others)
        {
            bool splash = actor.HasAbility(UnitAbilities.Splash);
            bool scorcher = actor.HasAbility(UnitAbilities.Scorcher);
            if ((!splash && !scorcher) || primaryDamage <= 0)
                return;
            int half = primaryDamage / 2; // floor
            if (half <= 0)
                return;

            others.Clear();
            for (int i = 0; i < enemyList.Count; i++)
                if (i != primaryTargetIndex && enemyList[i].Hp > 0f)
                    others.Add(i);
            if (others.Count == 0)
                return;

            List<BattleSimSecondaryTarget> selectedTargets =
                BattleSimulationKernel.SelectSecondaryTargets(
                    others.Count,
                    i =>
                    {
                        int unitIndex = others[i];
                        return enemyList[unitIndex].TypeTags != null
                            && enemyList[unitIndex].TypeTags.Contains(UnitTypeTag.Bio);
                    },
                    splash, scorcher, rng);
            foreach (BattleSimSecondaryTarget selected in selectedTargets)
                RosterSideHit(enemyList, others[selected.Index], primaryDamage, magnitudes);
        }

        private static void RosterSideHit(List<BattleUnit> list, int idx, int primaryDamage,
            AbilityMagnitudes magnitudes)
        {
            BattleUnit u = list[idx];
            int damage = BattleSimulationKernel.SecondaryDamage(
                primaryDamage, u.Abilities, magnitudes);
            if (damage <= 0)
                return;
            u.Hp -= damage;

            int attack = Mathf.RoundToInt(u.Attack);
            int defense = Mathf.RoundToInt(u.Defense);
            BattleSimulationKernel.ApplyBerserkIfHit(
                true, u.Abilities, ref attack, ref defense, magnitudes);
            u.Attack = attack;
            u.Defense = defense;
            list[idx] = u;
        }

        private static bool AnyCombatantAlive(List<BattleUnit> units)
        {
            foreach (BattleUnit u in units)
                if (!u.IsHero && u.Hp > 0f)
                    return true;
            return false;
        }

        private static int FirstLivingHeroIndex(List<BattleUnit> units)
        {
            for (int i = 0; i < units.Count; i++)
                if (units[i].IsHero && units[i].Hp > 0f)
                    return i;
            return -1;
        }

        private static void TransferCommanderAfterHeroDeath(List<BattleUnit> side, int deadHeroIndex,
            ref int sideFate)
        {
            if (deadHeroIndex < 0 || deadHeroIndex >= side.Count || !side[deadHeroIndex].IsHero)
                return;

            // Only the first living hero is Commander. If another hero was targeted first, command
            // does not change. If Commander died, remove its Initiative bonus from every actor and
            // immediately transfer command/Fate to the next living hero.
            int firstAlive = FirstLivingHeroIndex(side);
            int oldBonus = side[deadHeroIndex].Initiative;
            if (firstAlive >= 0 && firstAlive < deadHeroIndex)
                return;

            int newBonus = firstAlive >= 0 ? side[firstAlive].Initiative : 0;
            sideFate = firstAlive >= 0 ? side[firstAlive].HeroFate : 0;
            int delta = newBonus - oldBonus;
            for (int i = 0; i < side.Count; i++)
            {
                BattleUnit u = side[i];
                if (!u.IsHero)
                {
                    u.Initiative += delta;
                    side[i] = u;
                }
            }
        }

        // One full battle: rounds of shuffle-then-sort-by-Initiative turn order, every living
        // actor rolling real tokens against a random living enemy, until one side has nobody left or
        // MaxSimulatedRounds runs out. Returns +1 (attackers wiped the defenders), -1 (defenders
        // wiped the attackers), or 0 (mutual wipe, or neither side finished the other off in time —
        // a draw, which every readout counts as half a win).
        // Estimate-local workspace: never shared across estimates or exposed through their cache.
        // Every battle/round clears it; it changes allocations, not simulated inputs or RNG order.
        private sealed class SimulationScratch
        {
            internal readonly List<(bool isAttacker, int index)> Order;
            internal readonly HashSet<(bool isAttacker, int index)> Acted = new HashSet<(bool, int)>();
            internal readonly HashSet<(bool isAttacker, int index)> Suppressed = new HashSet<(bool, int)>();
            // Per round: Bodyguard reactions spent, Drill bonuses handed out.
            internal readonly HashSet<(bool isAttacker, int index)> BodyguardsUsed = new HashSet<(bool, int)>();
            internal readonly List<(bool isAttacker, int index)> Drilled = new List<(bool, int)>();
            internal readonly List<int> LivingTargets;
            internal readonly List<int> SecondaryTargets;
            internal readonly System.Comparison<(bool isAttacker, int index)> ByInitiativeDescending;
            private List<BattleUnit> _attackers, _defenders;

            internal SimulationScratch(int attackers, int defenders)
            {
                Order = new List<(bool, int)>(attackers + defenders);
                LivingTargets = new List<int>(System.Math.Max(attackers, defenders));
                SecondaryTargets = new List<int>(System.Math.Max(attackers, defenders));
                ByInitiativeDescending = (a, b) =>
                {
                    int ai = a.isAttacker ? _attackers[a.index].Initiative : _defenders[a.index].Initiative;
                    int bi = b.isAttacker ? _attackers[b.index].Initiative : _defenders[b.index].Initiative;
                    return bi.CompareTo(ai);
                };
            }

            internal void Reset(List<BattleUnit> attackers, List<BattleUnit> defenders)
            {
                _attackers = attackers; _defenders = defenders;
                Order.Clear(); Acted.Clear(); Suppressed.Clear();
                BodyguardsUsed.Clear(); Drilled.Clear();
                LivingTargets.Clear(); SecondaryTargets.Clear();
            }
        }

        private static void ApplyHealing(List<BattleUnit> side, int carrierIndex, System.Random rng)
        {
            BattleUnit carrier = side[carrierIndex];
            if (carrier.Hp <= 0f || !carrier.HasAbility(UnitAbilities.Heal)) return;
            int index = BattleSimulationKernel.SelectHealingTarget(side.Count, i =>
                side[i].Hp > 0f && side[i].Hp < side[i].MaxHp
                && side[i].TypeTags != null && side[i].TypeTags.Contains(UnitTypeTag.Bio), rng);
            if (index < 0) return;
            BattleUnit target = side[index];
            target.Hp = BattleSimulationKernel.HealedHitPoints(target.Hp, target.MaxHp);
            target.Consumption = target.Consumption?.AfterHpRefill();
            side[index] = target;
        }

        // UnitAbilities.Drill at round start — the live rule (BattleSimulationKernel.
        // SelectDrillTargets) on this roster; RevertRosterDrill takes the bonus back off.
        private static void ApplyRosterDrill(List<BattleUnit> side, bool isAttacker,
            List<(bool isAttacker, int index)> drilled, AbilityMagnitudes magnitudes)
        {
            int carriers = 0;
            for (int i = 0; i < side.Count; i++)
                if (side[i].Hp > 0f && side[i].HasAbility(UnitAbilities.Drill))
                    carriers++;
            if (carriers == 0 || magnitudes.DrillAttackBonus == 0)
                return;
            foreach (int index in BattleSimulationKernel.SelectDrillTargets(side.Count, carriers,
                         i => !side[i].IsHero && side[i].Hp > 0f, i => Mathf.RoundToInt(side[i].Attack)))
            {
                BattleUnit u = side[index];
                u.Attack += magnitudes.DrillAttackBonus;
                side[index] = u;
                drilled.Add((isAttacker, index));
            }
        }

        private static void RevertRosterDrill(List<BattleUnit> attackers, List<BattleUnit> defenders,
            List<(bool isAttacker, int index)> drilled, AbilityMagnitudes magnitudes)
        {
            foreach ((bool isAttacker, int index) in drilled)
            {
                List<BattleUnit> side = isAttacker ? attackers : defenders;
                BattleUnit u = side[index];
                u.Attack -= magnitudes.DrillAttackBonus;
                side[index] = u;
            }
            drilled.Clear();
        }

        // UnitAbilities.Bodyguard on a roster: positions do not exist, so — the same
        // approximation Splash makes for its neighbours — any living unspent guard of the hit side
        // counts as adjacent. The most HP wins, the lowest index on a tie (BattleRules).
        private static int FindRosterBodyguard(List<BattleUnit> side, int targetIndex, bool sideIsAttacker,
            HashSet<(bool isAttacker, int index)> used)
        {
            int best = -1;
            float bestHp = 0f;
            for (int i = 0; i < side.Count; i++)
            {
                BattleUnit u = side[i];
                if (i == targetIndex || u.IsHero || u.Hp <= bestHp || !u.HasAbility(UnitAbilities.Bodyguard)
                    || used.Contains((sideIsAttacker, i)))
                    continue;
                best = i;
                bestHp = u.Hp;
            }
            return best;
        }

        // One resolved exchange landing on list[index] (the hit side is attackers when
        // hitSideIsAttacker): HP, the victim's Berserk, Commander hand-over, then Splash/Scorcher
        // around it — this roster model has no positions, so "neighbours" are random OTHER living
        // units (a no-op for an actor with neither skill). Returns whether the hit suppresses the
        // victim's pending turn (ShockAttack).
        private static bool ApplyRosterHit(List<BattleUnit> list, int index, BattleUnit actor,
            BattleSimExchangeOutcome exchange, bool hitSideIsAttacker, System.Random rng,
            AbilityMagnitudes magnitudes, SimulationScratch scratch, ref int attackerFate, ref int defenderFate,
            List<BattleUnit> attackers, List<BattleUnit> defenders)
        {
            BattleUnit target = list[index];
            float targetHp = target.Hp;
            int transientAttack = Mathf.RoundToInt(target.Attack);
            int transientDefense = Mathf.RoundToInt(target.Defense);
            BattleSimulationKernel.ApplyPrimaryOutcome(
                exchange, actor.Abilities, target.Abilities,
                ref targetHp, ref transientAttack, ref transientDefense,
                magnitudes, out bool suppressesTurn);
            target.Hp = targetHp;
            target.Attack = transientAttack;
            target.Defense = transientDefense;
            list[index] = target;

            if (target.IsHero && target.Hp <= 0f)
            {
                if (hitSideIsAttacker)
                    TransferCommanderAfterHeroDeath(attackers, index, ref attackerFate);
                else
                    TransferCommanderAfterHeroDeath(defenders, index, ref defenderFate);
            }

            if (exchange.Damage > 0)
            {
                int commanderBefore = FirstLivingHeroIndex(list);
                ApplyRosterSplash(list, index, actor, exchange.Damage, rng, magnitudes,
                    scratch.SecondaryTargets);
                // A Commander killed by a side-hit hands over command like a primary kill.
                if (commanderBefore >= 0 && list[commanderBefore].Hp <= 0f)
                {
                    if (hitSideIsAttacker)
                        TransferCommanderAfterHeroDeath(attackers, commanderBefore, ref attackerFate);
                    else
                        TransferCommanderAfterHeroDeath(defenders, commanderBefore, ref defenderFate);
                }
            }
            return suppressesTurn;
        }

        private static float RosterExplosionValue(BattleUnit actor, List<BattleUnit> own,
            List<BattleUnit> enemy, int actorIndex)
        {
            int count = 0;
            float value = 0;
            void Add(BattleUnit target, bool friendly, bool self)
            {
                if (target.Hp <= 0 || self && actor.SingleUseUnit) return;
                count++;
                value += (friendly ? -1 : 1) * ExplosionAttack.ExpectedDamageValue(actor.Abilities,
                    Mathf.RoundToInt(target.Defense), target.TypeTags, target.Abilities, target.Hp, target.MaxHp,
                    CombatValue(target.Attack, target.Defense, target.MaxHp, target.Initiative));
            }
            for (int i = 0; i < own.Count; i++) Add(own[i], true, i == actorIndex);
            foreach (var target in enemy) Add(target, false, false);
            value *= Mathf.Min(ExplosionAttack.TargetLimit(actor.Abilities), count) / (float)Mathf.Max(1, count);
            return value - (actor.SingleUseUnit ? CombatValue(actor.Attack, actor.Defense, actor.Hp, actor.Initiative) : 0);
        }

        private static bool AnyCarries(List<BattleUnit> side, string ability)
        {
            for (int i = 0; i < side.Count; i++)
                if (side[i].HasAbility(ability) || side[i].Consumption?.After.Abilities.Contains(ability) == true)
                    return true;
            return false;
        }

        private static void ApplyRosterExplosion(BattleUnit actor, List<BattleUnit> attackers,
            List<BattleUnit> defenders, bool actorIsAttacker, int actorIndex, System.Random rng, AbilityMagnitudes magnitudes,
            SimulationScratch scratch, ref int attackerFate, ref int defenderFate)
        {
            var pool = new List<(bool attacking, int index)>();
            for (int i = 0; i < attackers.Count; i++)
                if (attackers[i].Hp > 0 && !(actor.SingleUseUnit && actorIsAttacker && i == actorIndex)) pool.Add((true, i));
            for (int i = 0; i < defenders.Count; i++)
                if (defenders[i].Hp > 0 && !(actor.SingleUseUnit && !actorIsAttacker && i == actorIndex)) pool.Add((false, i));
            int count = System.Math.Min(ExplosionAttack.TargetLimit(actor.Abilities), pool.Count);
            // Area hits neither intercept nor create Splash/Counterattack chains.
            actor.Attack = ExplosionAttack.AttackTokens;
            for (int i = 0; i < count; i++)
            {
                int j = rng.Next(i, pool.Count); (pool[i], pool[j]) = (pool[j], pool[i]);
                var victim = pool[i];
                var side = victim.attacking ? attackers : defenders;
                BattleUnit target = side[victim.index];
                int offensiveFate = actorIsAttacker == victim.attacking ? 0
                    : actorIsAttacker ? attackerFate : defenderFate;
                int targetFate = victim.attacking ? attackerFate : defenderFate;
                var exchange = ResolveExchange(actor, target, ref offensiveFate, ref targetFate, rng, magnitudes);
                if (actorIsAttacker != victim.attacking)
                { if (actorIsAttacker) attackerFate = offensiveFate; else defenderFate = offensiveFate; }
                if (victim.attacking) attackerFate = targetFate; else defenderFate = targetFate;
                float hp = target.Hp;
                int attack = Mathf.RoundToInt(target.Attack), defense = Mathf.RoundToInt(target.Defense);
                BattleSimulationKernel.ApplyPrimaryOutcome(exchange, actor.Abilities, target.Abilities,
                    ref hp, ref attack, ref defense, magnitudes, out bool suppressed);
                target.Hp = hp; target.Attack = attack; target.Defense = defense;
                side[victim.index] = target;
                if (target.IsHero && hp <= 0)
                {
                    if (victim.attacking) TransferCommanderAfterHeroDeath(side, victim.index, ref attackerFate);
                    else TransferCommanderAfterHeroDeath(side, victim.index, ref defenderFate);
                }
                if (suppressed && !scratch.Acted.Contains(victim)) scratch.Suppressed.Add(victim);
            }
        }
    }
}
