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
    // Monte Carlo battle behind Estimate / WinChance (see WorthIt.cs): the matchup seed, one
    // complete battle (SimulateOneBattle) and the seeded trial loops of single and sequential fights.
    public static partial class WorthIt
    {
        // Deterministic per-matchup seed — built ONLY from the raw numbers that describe the
        // matchup (rosters, commanders, hex bonus), never from GetHashCode() of an object. The
        // SAME matchup always replays the SAME trials; any real change rolls fresh ones. Its own
        // System.Random per call, never UnityEngine.Random (read-only strategic bookkeeping must
        // not consume the game's RNG stream). A side without a commander adds nothing to the
        // hash, so commander-less matchups keep exactly the seeds they always had.
        private static int BuildRosterSeed(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> enemyUnits, float hexDefenseBonus,
            SideCommander attackerCommander = default, SideCommander defenderCommander = default)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(hexDefenseBonus);
                if (attackerCommander.Present)
                    hash = (hash * 31 + 101 + attackerCommander.Initiative) * 31 + attackerCommander.Fate;
                if (defenderCommander.Present)
                    hash = (hash * 31 + 211 + defenderCommander.Initiative) * 31 + defenderCommander.Fate;
                if (attackerCommander.SupportHeroes != null)
                    foreach (DefenderProfile hero in attackerCommander.SupportHeroes) hash = AccumulateProfileHash(hash, hero);
                if (defenderCommander.SupportHeroes != null)
                    foreach (DefenderProfile hero in defenderCommander.SupportHeroes) hash = AccumulateProfileHash(hash, hero);
                foreach (DefenderProfile p in attackerUnits)
                    hash = AccumulateProfileHash(hash, p);
                hash = hash * 31 + 12345; // separates the two rosters — an empty attacker side must
                                           // never hash the same as an empty enemy side
                foreach (DefenderProfile p in enemyUnits)
                    hash = AccumulateProfileHash(hash, p);
                return hash;
            }
        }

        private static int AccumulateProfileHash(int hash, DefenderProfile p)
        {
            unchecked
            {
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(p.Attack);
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(p.Defense);
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(p.HitPoints);
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(p.MaxHitPoints);
                hash = hash * 31 + p.Initiative;
                hash = hash * 31 + (p.IsGroundCombatant ? 1 : 0);
                hash = hash * 31 + (p.IsHero ? 1 : 0);
                hash = hash * 31 + p.FateMax;
                hash = hash * 31 + (p.IsSummoned ? 1 : 0);
                // Empty/singleton collections are already sorted. Avoid allocating LINQ sort
                // iterators for the common case without changing the seed's ordering rule.
                if (p.TypeTags.Count == 1)
                    hash = hash * 31 + (int)p.TypeTags[0];
                else if (p.TypeTags.Count > 1)
                    foreach (UnitTypeTag tag in p.TypeTags.OrderBy(t => (int)t))
                        hash = hash * 31 + (int)tag;
                if (p.Abilities.Count == 1)
                    hash = AccumulateAbilityHash(hash, p.Abilities[0]);
                else if (p.Abilities.Count > 1)
                    foreach (string ability in p.Abilities.OrderBy(a => a, System.StringComparer.Ordinal))
                        hash = AccumulateAbilityHash(hash, ability);
                if (p.Consumption != null) hash = AccumulateAbilityHash(hash, p.Consumption.Key);
                if (p.SingleUseUnit) hash = hash * 31 + 19019;
                if (!p.CanDisruptBuilding) hash = hash * 31 + 19021;
                return hash;
            }
        }

        private static int AccumulateAbilityHash(int hash, string ability)
        {
            if (ability == null) return hash;
            unchecked
            {
                foreach (char ch in ability)
                    hash = hash * 31 + ch;
                return hash * 31 + 7;
            }
        }

        private static int SimulateOneBattle(List<BattleUnit> attackers, List<BattleUnit> defenders,
            System.Random rng, int attackerFate = 0, int defenderFate = 0,
            AbilityMagnitudes? magnitudesOverride = null, SimulationScratch scratch = null)
        {
            AbilityMagnitudes magnitudes = magnitudesOverride ?? AbilityMagnitudes.Default;
            scratch = scratch ?? new SimulationScratch(attackers.Count, defenders.Count);
            scratch.Reset(attackers, defenders);
            var order = scratch.Order;
            var acted = scratch.Acted;
            var suppressed = scratch.Suppressed;
            var livingTargets = scratch.LivingTargets;
            // Battle-constant skill presence: rosters never gain abilities mid-battle, so sides
            // without Bodyguard/Counterattack/Drill skip those checks on the hot path entirely.
            bool attackersGuard = AnyCarries(attackers, UnitAbilities.Bodyguard);
            bool defendersGuard = AnyCarries(defenders, UnitAbilities.Bodyguard);
            bool anyCounter = AnyCarries(attackers, UnitAbilities.Counterattack)
                || AnyCarries(defenders, UnitAbilities.Counterattack);
            bool attackersDrill = AnyCarries(attackers, UnitAbilities.Drill);
            bool defendersDrill = AnyCarries(defenders, UnitAbilities.Drill);
            for (int round = 0; round < MaxSimulatedRounds && AnyCombatantAlive(attackers) && AnyCombatantAlive(defenders); round++)
            {
                for (int i = 0; i < attackers.Count; i++)
                    if (attackers[i].IsHero) ApplyHealing(attackers, i, rng);
                for (int i = 0; i < defenders.Count; i++)
                    if (defenders[i].IsHero) ApplyHealing(defenders, i, rng);
                scratch.BodyguardsUsed.Clear();
                if (attackersDrill) ApplyRosterDrill(attackers, true, scratch.Drilled, magnitudes);
                if (defendersDrill) ApplyRosterDrill(defenders, false, scratch.Drilled, magnitudes);
                order.Clear();
                for (int i = 0; i < attackers.Count; i++) if (!attackers[i].IsHero) order.Add((true, i));
                for (int i = 0; i < defenders.Count; i++) if (!defenders[i].IsHero) order.Add((false, i));

                // Shuffle first (random Initiative tie-break, same reasoning BattleTurnOrder's own
                // class comment gives for why equal Initiative shouldn't always resolve the same
                // way), then a stable sort descending — matches the real turn-order rule exactly.
                for (int i = order.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
                order.Sort(scratch.ByInitiativeDescending);

                acted.Clear();
                suppressed.Clear();
                foreach ((bool isAttacker, int index) turn in order)
                {
                    List<BattleUnit> ownList = turn.isAttacker ? attackers : defenders;
                    List<BattleUnit> enemyList = turn.isAttacker ? defenders : attackers;
                    BattleUnit actor = ownList[turn.index];
                    if (actor.Hp <= 0f)
                    {
                        acted.Add(turn);
                        continue; // killed earlier this same round — skips its turn, same as a real one would
                    }
                    if (suppressed.Contains(turn))
                    {
                        acted.Add(turn);
                        continue; // ShockAttack removed this not-yet-taken action from the round
                    }

                    ApplyHealing(ownList, turn.index, rng);
                    actor = ownList[turn.index];

                    // A hero stands in the back row and the fight ends when the non-hero bodies
                    // are gone, so an attack is only spent on a hero once no body of that side
                    // stands (playtest 2026-10-02: a lone Flamer + hero was rated 0.88 against two
                    // neutral units and lost four of four — random picks wasted half the enemy
                    // attacks on the hero).
                    livingTargets.Clear();
                    for (int i = 0; i < enemyList.Count; i++)
                        if (enemyList[i].Hp > 0f && !enemyList[i].IsHero)
                            livingTargets.Add(i);
                    if (livingTargets.Count == 0)
                        for (int i = 0; i < enemyList.Count; i++)
                            if (enemyList[i].Hp > 0f)
                                livingTargets.Add(i);
                    if (livingTargets.Count == 0)
                        break; // this side just ran out of targets mid-round — battle's over

                    if (ExplosionAttack.TargetLimit(actor.Abilities) > 0)
                    {
                        if (RosterExplosionValue(actor, ownList, enemyList, turn.index) > 0)
                        {
                            ApplyRosterExplosion(actor, attackers, defenders, turn.isAttacker, turn.index, rng, magnitudes,
                                scratch, ref attackerFate, ref defenderFate);
                            ConsumeRosterEquipment(ownList, turn.index);
                        }
                        acted.Add(turn);
                        continue;
                    }

                    int targetIndex = livingTargets[rng.Next(livingTargets.Count)];
                    BattleUnit target = enemyList[targetIndex];

                    BattleSimExchangeOutcome exchange = turn.isAttacker
                        ? ResolveExchange(actor, target, ref attackerFate, ref defenderFate, rng, magnitudes)
                        : ResolveExchange(actor, target, ref defenderFate, ref attackerFate, rng, magnitudes);

                    // UnitAbilities.Bodyguard — a landed hit goes to the hit side's guard, re-run
                    // against its own tags/armor; the guard is the victim from here on.
                    if (exchange.Damage > 0 && (turn.isAttacker ? defendersGuard : attackersGuard))
                    {
                        int guard = FindRosterBodyguard(enemyList, targetIndex, !turn.isAttacker,
                            scratch.BodyguardsUsed);
                        if (guard >= 0)
                        {
                            scratch.BodyguardsUsed.Add((!turn.isAttacker, guard));
                            int intercepted = BattleSimulationKernel.InterceptedDamage(
                                exchange.AttackerSuccesses - exchange.DefenderSuccesses, actor.Abilities,
                                enemyList[guard].TypeTags, enemyList[guard].Abilities, magnitudes);
                            exchange = new BattleSimExchangeOutcome(intercepted, intercepted > 0,
                                exchange.AttackerSuccesses, exchange.DefenderSuccesses);
                            targetIndex = guard;
                        }
                    }

                    bool suppressesTurn = ApplyRosterHit(enemyList, targetIndex, actor, exchange,
                        !turn.isAttacker, rng, magnitudes, scratch, ref attackerFate, ref defenderFate,
                        attackers, defenders);
                    if (suppressesTurn)
                    {
                        var targetTurn = (!turn.isAttacker, targetIndex);
                        if (!acted.Contains(targetTurn))
                            suppressed.Add(targetTurn);
                    }

                    ConsumeRosterEquipment(ownList, turn.index);

                    // UnitAbilities.Counterattack — a surviving victim answers a Range-1 (or
                    // unknown-range) attacker at once: never intercepted, never answered itself.
                    if (anyCounter && ownList[turn.index].Hp > 0f)
                    {
                        BattleUnit victim = enemyList[targetIndex];
                        if (BattleSimulationKernel.TriggersCounterattack(victim.Abilities, victim.IsHero,
                                victim.Hp, actor.Range <= 1,
                                exchange.Damage, actor.Abilities))
                        {
                            BattleUnit answered = ownList[turn.index];
                            BattleSimExchangeOutcome answer = turn.isAttacker
                                ? ResolveExchange(victim, answered, ref defenderFate, ref attackerFate, rng, magnitudes)
                                : ResolveExchange(victim, answered, ref attackerFate, ref defenderFate, rng, magnitudes);
                            ApplyRosterHit(ownList, turn.index, victim, answer, turn.isAttacker, rng, magnitudes,
                                scratch, ref attackerFate, ref defenderFate, attackers, defenders);
                            ConsumeRosterEquipment(enemyList, targetIndex);
                        }
                    }

                    acted.Add(turn);
                }
                RevertRosterDrill(attackers, defenders, scratch.Drilled, magnitudes);
            }

            bool attackersAlive = AnyCombatantAlive(attackers);
            bool defendersAlive = AnyCombatantAlive(defenders);
            if (attackersAlive && !defendersAlive) return 1;
            if (defendersAlive && !attackersAlive) return -1;
            return 0;
        }

        private static BattleEstimate EstimateSequentialFights(IReadOnlyCollection<DefenderProfile> attackerUnits,
            SideCommander attackerCommander, IReadOnlyList<DefendingArmy> defendingArmies,
            float hexDefenseBonus, AbilityMagnitudes? magnitudes)
        {
            var armies = (defendingArmies ?? System.Array.Empty<DefendingArmy>())
                .Select(a => new DefendingArmy(WithBattleSummons(a.Units), a.Commander,
                    a.DefenseBonusOverride, a.ArmyId, a.BuildingDefenseBonus))
                .Where(a => a.Units.Any(p => p.IsGroundCombatant))
                .ToList();
            if (armies.Count <= 1)
                return armies.Count == 0
                    ? new BattleEstimate(1f, 1f, 0f)
                    : Estimate(attackerUnits, armies[0].Units,
                        armies[0].DefenseBonus(hexDefenseBonus, attackerUnits, attackerCommander), attackerCommander,
                        armies[0].Commander, magnitudes);

            attackerUnits = WithBattleSummons(attackerUnits);
            List<BattleUnit> baseline = ToBattleUnits(attackerUnits, 0f, attackerCommander.Initiative, attackerCommander);
            if (!baseline.Any(u => !u.IsHero))
                return new BattleEstimate(0f, 0f, 0f);

            // Strongest defender first, judged against the fresh attacker (stable for equal odds).
            List<DefendingArmy> order = armies
                .Select((a, i) => (a, i, win: Estimate(attackerUnits, a.Units,
                    a.DefenseBonus(hexDefenseBonus, attackerUnits, attackerCommander),
                    attackerCommander, a.Commander, magnitudes).WinChance))
                .OrderBy(x => x.win).ThenBy(x => x.i)
                .Select(x => x.a)
                .ToList();

            int seed = BuildRosterSeed(attackerUnits,
                order.SelectMany(a => a.Units).ToList(), hexDefenseBonus, attackerCommander, default);
            foreach (DefendingArmy a in order)
                if (a.Commander.Present)
                    seed = unchecked((seed * 31 + a.Commander.Initiative) * 31 + a.Commander.Fate);
            AbilityMagnitudes m = magnitudes ?? AbilityMagnitudes.Default;
            // Each defending army converted once; every trial fights a fresh copy (same values the
            // per-trial conversion produced).
            var defenderTemplates = order
                .Select(a => ToBattleUnits(a.Units, a.DefenseBonus(hexDefenseBonus), a.Commander.Initiative, a.Commander))
                .ToList();
            return CachedEstimate(2, seed, m,
                buf =>
                {
                    AppendKey(buf, attackerCommander);
                    AppendKey(buf, baseline);
                    buf.Add(order.Count);
                    for (int i = 0; i < order.Count; i++)
                    {
                        AppendKey(buf, order[i].Commander);
                        AppendKey(buf, defenderTemplates[i]);
                        buf.Add(System.BitConverter.SingleToInt32Bits(order[i].BuildingDefenseBonus));
                    }
                },
                () => SimulateSequential(baseline, order, defenderTemplates, seed, attackerCommander, m));
        }

        private static BattleEstimate SimulateSequential(List<BattleUnit> baseline,
            List<DefendingArmy> order, List<List<BattleUnit>> defenderTemplates, int seed,
            SideCommander attackerCommander, AbilityMagnitudes magnitudes)
        {
            var rng = new System.Random(seed);
            float startHp = baseline.Where(u => !u.IsHero).Sum(u => u.Hp);
            int wins = 0, draws = 0, criticalOnWin = 0;
            float survivingRatioSum = 0f;
            var attackers = new List<BattleUnit>(baseline.Count);
            var defenders = new List<BattleUnit>();
            var entryStats = new List<BattleUnit>(baseline.Count);
            var survivors = new List<BattleUnit>(baseline.Count);
            var survivorStats = new List<BattleUnit>(baseline.Count);
            var scratch = new SimulationScratch(baseline.Count,
                defenderTemplates.Count > 0 ? defenderTemplates.Max(d => d.Count) : 0);
            for (int t = 0; t < MonteCarloTrials; t++)
            {
                attackers.Clear(); attackers.AddRange(baseline);
                entryStats.Clear(); entryStats.AddRange(baseline);
                int result = 1;
                for (int ai = 0; ai < order.Count; ai++)
                {
                    DefendingArmy a = order[ai];
                    int commanderIndex = FirstLivingHeroIndex(attackers);
                    int refreshedAttackerFate = ai == 0 ? attackerCommander.Fate
                        : commanderIndex >= 0 ? attackers[commanderIndex].HeroFate : 0;
                    defenders.Clear(); defenders.AddRange(defenderTemplates[ai]);
                    // Recheck surviving sources between encounters, then freeze this fight's
                    // bonus even if its carrier dies in the first exchange.
                    if (a.BuildingDefenseBonus > 0f && attackers.Any(u => u.Hp > 0f
                        && !u.IsSummoned && u.CanDisruptBuilding && u.HasAbility(UnitAbilities.Disruption)))
                        for (int di = 0; di < defenders.Count; di++)
                        {
                            BattleUnit d = defenders[di];
                            d.Defense -= a.BuildingDefenseBonus;
                            defenders[di] = d;
                        }
                    result = SimulateOneBattle(attackers, defenders, rng,
                        refreshedAttackerFate, a.Commander.Fate, magnitudes, scratch);
                    if (result <= 0)
                        break;
                    // Wounds carry into the next battle; in-battle stat changes (Berserk) do not —
                    // the game reverts them when a battle ends (BattleScreenUI.RevertBerserkStacks).
                    survivors.Clear(); survivorStats.Clear();
                    for (int i = 0; i < attackers.Count; i++)
                    {
                        if (attackers[i].Hp <= 0f || attackers[i].IsSummoned)
                            continue;
                        BattleUnit u = attackers[i];
                        u.Attack = entryStats[i].Attack;
                        u.Defense = entryStats[i].Defense;
                        survivors.Add(u);
                        survivorStats.Add(entryStats[i]);
                    }
                    // Swap buffers, keeping the old lists for the next battle/trial.
                    var oldAttackers = attackers; attackers = survivors; survivors = oldAttackers;
                    var oldStats = entryStats; entryStats = survivorStats; survivorStats = oldStats;
                    if (ai + 1 < order.Count)
                    {
                        int nextCommanderIndex = FirstLivingHeroIndex(attackers);
                        int nextCommanderInitiative = nextCommanderIndex >= 0
                            ? attackers[nextCommanderIndex].Initiative : 0;
                        AppendFreshBattleSummons(attackers, nextCommanderInitiative);
                        entryStats.Clear(); entryStats.AddRange(attackers);
                    }
                }
                if (result > 0)
                {
                    wins++;
                    survivingRatioSum += startHp > 0f ? attackers.Where(u => !u.IsHero).Sum(u => Mathf.Max(0f, u.Hp)) / startHp : 0f;
                    if (attackers.Any(u => !u.IsHero && u.Hp > 0f && u.Hp <= u.MaxHp / 2f))
                        criticalOnWin++;
                }
                else if (result == 0) draws++;
            }
            return new BattleEstimate((wins + draws * 0.5f) / MonteCarloTrials,
                wins > 0 ? survivingRatioSum / wins : 0f,
                wins > 0 ? (float)criticalOnWin / wins : 0f);
        }

        // Shared Monte Carlo readout loop — `baseline` is the attacker's own BattleUnit snapshot
        // (already carrying whatever MaxHp fidelity its caller could offer), copied fresh every
        // trial; `defenderUnits`/`hexDefenseBonus` are rebuilt into BattleUnits per trial the same
        // way every existing caller here already expected.
        private static BattleEstimate EstimateCore(List<BattleUnit> baseline,
            IReadOnlyCollection<DefenderProfile> defenderUnits, float hexDefenseBonus, int seed,
            SideCommander attackerCommander, SideCommander defenderCommander,
            AbilityMagnitudes? magnitudes)
        {
            AbilityMagnitudes m = magnitudes ?? AbilityMagnitudes.Default;
            return CachedEstimate(1, seed, m,
                buf =>
                {
                    AppendKey(buf, attackerCommander);
                    AppendKey(buf, baseline);
                    AppendKey(buf, defenderCommander);
                    AppendKey(buf, defenderUnits, hexDefenseBonus, defenderCommander.Initiative);
                    // The simulated commander healing heroes also receive the hex bonus.
                    buf.Add(System.BitConverter.SingleToInt32Bits(hexDefenseBonus));
                },
                () => SimulateSingle(baseline,
                    ToBattleUnits(defenderUnits, hexDefenseBonus, defenderCommander.Initiative, defenderCommander), seed, attackerCommander,
                    defenderCommander, m));
        }

        private static BattleEstimate EstimateCore(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> defenderUnits, float hexDefenseBonus, int seed,
            SideCommander attackerCommander, SideCommander defenderCommander,
            AbilityMagnitudes? magnitudes)
        {
            AbilityMagnitudes m = magnitudes ?? AbilityMagnitudes.Default;
            return CachedEstimate(1, seed, m,
                buf =>
                {
                    AppendKey(buf, attackerCommander);
                    AppendKey(buf, attackerUnits, 0f, attackerCommander.Initiative);
                    AppendKey(buf, defenderCommander);
                    AppendKey(buf, defenderUnits, hexDefenseBonus, defenderCommander.Initiative);
                    // The simulated commander healing heroes also receive the hex bonus.
                    buf.Add(System.BitConverter.SingleToInt32Bits(hexDefenseBonus));
                },
                () => SimulateSingle(ToBattleUnits(attackerUnits, 0f, attackerCommander.Initiative, attackerCommander),
                    ToBattleUnits(defenderUnits, hexDefenseBonus, defenderCommander.Initiative, defenderCommander), seed,
                    attackerCommander, defenderCommander, m));
        }

        private static BattleEstimate SimulateSingle(List<BattleUnit> baseline,
            List<BattleUnit> defenderTemplate, int seed, SideCommander attackerCommander,
            SideCommander defenderCommander, AbilityMagnitudes magnitudes)
        {
            var rng = new System.Random(seed);
            float startHp = baseline.Sum(u => u.Hp);

            int wins = 0, draws = 0, criticalOnWin = 0;
            float survivingRatioSum = 0f;
            var attackers = new List<BattleUnit>(baseline.Count);
            var defenders = new List<BattleUnit>(defenderTemplate.Count);
            var scratch = new SimulationScratch(baseline.Count, defenderTemplate.Count);
            for (int i = 0; i < MonteCarloTrials; i++)
            {
                attackers.Clear(); attackers.AddRange(baseline);
                defenders.Clear(); defenders.AddRange(defenderTemplate);
                int result = SimulateOneBattle(attackers, defenders, rng,
                    attackerCommander.Fate, defenderCommander.Fate, magnitudes, scratch);
                if (result > 0)
                {
                    wins++;
                    survivingRatioSum += startHp > 0f ? attackers.Sum(u => Mathf.Max(0f, u.Hp)) / startHp : 0f;
                    if (attackers.Any(u => u.Hp > 0f && u.Hp <= u.MaxHp / 2f))
                        criticalOnWin++;
                }
                else if (result == 0) draws++;
            }

            float winChance = (wins + draws * 0.5f) / MonteCarloTrials;
            float survivingRatio = wins > 0 ? survivingRatioSum / wins : 0f;
            float criticalChance = wins > 0 ? (float)criticalOnWin / wins : 0f;
            return new BattleEstimate(winChance, survivingRatio, criticalChance);
        }
    }
}
