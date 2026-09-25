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
    // The one shared "is this ground fight worth it" estimate — every army-vs-army comparison on
    // the map goes through here, never a private formula at a call site.
    //
    //  · DefenderProfile — one combatant as the estimator sees it: our own live units
    //    (FromLiveUnit) or an enemy's fog-honest remembered roster (AiMapMemory).
    //  · Estimate / WinChance — MonteCarloTrials complete battles (SimulateOneBattle): each round a
    //    shuffle-then-Initiative turn order, every living actor rolls real dice (50% per die)
    //    against a random living enemy, the canonical ability-modifier chain, and the Fate duel of
    //    every exchange (ResolveExchange, spending decided by FateDuelAi). Each side may have a
    //    commander (SideCommander — ArmyData.Commander, the army's first hero): its Initiative is
    //    added to every combatant of its side and its Fate buys rerolls. No grid positions.
    //  · EstimateSequential — a hex held by several armies: one battle per defending army,
    //    strongest first, wounds carry over, Fate refills.
    //  · HexDefenseBonus — terrain + Base-building defence, added to the DEFENDER's dice only,
    //    exactly as BattleScreenUI.Combat's BeginAttack does in a real fight.
    //  · CanDamageAll — the coverage gate: every known defender must have at least one attacker
    //    able to scratch it (the same expected-damage read BattleTargetSelector uses), because a
    //    win chance alone can overstate a fight nothing in the roster can actually hurt.
    //  · CombatValue — the quick body value every strongest-first roster pick uses.
    public static class WorthIt
    {
        // Attack-sum of `army`'s own non-hero members — the same side of the comparison every
        // caller here always uses for the ATTACKING army (heroes never counted, matching every
        // other flat Attack/Defense sum already in this codebase).
        public static float AttackSum(ArmyData army) => AttackSum(army?.Members);

        // Own non-hero Defense sum, no hex bonus — used both as DefenseAt's own first term and,
        // on its own, as the ATTACKER's side of Score's return-fire read (an attacking army isn't
        // standing on a defensible hex it gets credit for, it's marching onto the defender's).
        public static float DefenseSum(ArmyData army) => DefenseSum(army?.Members);

        // Roster-scoped overloads — same non-hero flat sum against an explicit member set rather
        // than a whole ArmyData. Needed where the caller must rank/measure only the members it is
        // actually allowed to know about: a defender's members that are HIDDEN from the observer
        // must not feed the "which army do I contact" power read (BattleInitiator.FindEnemyAt),
        // or an invisible heavy unit inside a mixed army would still steer the enemy's target
        // pick (project owner's own P1).
        public static float AttackSum(IEnumerable<UnitData> members) => members == null ? 0f : CombatantsOf(members).Sum(m => m.Attack);
        public static float DefenseSum(IEnumerable<UnitData> members) => members == null ? 0f : CombatantsOf(members).Sum(m => m.Defense);

        // The ONE ground-combatant filter every estimator entry point below applies to BOTH sides
        // (UnitData.IsGroundCombatant / DefenderProfile.IsGroundCombatant). Callers pass whole
        // rosters; no caller filters heroes itself, so no caller can disagree with the battle.
        private static List<UnitData> CombatantsOf(IEnumerable<UnitData> units) =>
            units == null ? new List<UnitData>() : units.Where(u => u != null && u.IsGroundCombatant).ToList();

        private static List<DefenderProfile> CombatantsOf(IEnumerable<DefenderProfile> profiles) =>
            profiles == null ? new List<DefenderProfile>() : profiles.Where(p => p.IsGroundCombatant).ToList();

        // `defender`'s own non-hero Defense sum PLUS whatever `hex` itself would grant a real
        // defender standing there (terrain + Base-building bonus — see HexDefenseBonus). This is
        // what a REAL fight on this hex would actually roll against, not just the army's own raw
        // stats.
        public static float DefenseAt(ArmyData defender, HexCoord hex, HexMap map) => DefenseSum(defender) + HexDefenseBonus(hex, map);

        // Number of simulated exchanges Score/WinChance each run per call — capped at 100 per the
        // project owner's own explicit call (2026-08-22: "ограничиваемся сотней вызовов"),
        // lowered to 25 (2026-09-19) once profiling showed CombatOpportunityAnalyzer actually
        // calls this twice per known target, per settled step, many settled steps per AI turn —
        // not "once or twice per turn" as assumed when the 100-trial limit was first set. 25
        // trials still gives a usable win-chance estimate; it's a named constant purely so nobody
        // quietly cranks it back up on a hot path without weighing that cost again.
        private const int MonteCarloTrials = 25;

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
                foreach (UnitTypeTag tag in p.TypeTags.OrderBy(t => (int)t))
                    hash = hash * 31 + (int)tag;
                foreach (string ability in p.Abilities.OrderBy(a => a, System.StringComparer.Ordinal))
                {
                    if (ability == null) continue;
                    foreach (char ch in ability)
                        hash = hash * 31 + ch;
                    hash = hash * 31 + 7;
                }
                return hash;
            }
        }

        // One die-pool's worth of successes, same 50/50-per-die mechanic the real battle actually
        // rolls with (ChallengeResolver.RollDice — half the faces are a miss, same odds). `diceCount`
        // is always an aggregate Attack/Defense sum, already integer-valued in practice —
        // RoundToInt only guards against float drift from summing. `rng` — this evaluation's own
        // local System.Random (see BuildSeed's own comment), never UnityEngine.Random.
        //
        // Public since 2026-08-26 (AirStrike/Raid coordination spec, project owner's own report) —
        // AviationCombatEstimator's own one-sided air-strike simulation needs this exact same die
        // mechanic but plays it out in a different battle SHAPE (sequential single-target aircraft
        // attacks, no return fire) that doesn't fit SimulateOneBattle's round-robin structure, so it
        // reuses this one primitive rather than rolling its own copy of the 50/50 mechanic.
        public static int RollSuccesses(float diceCount, System.Random rng)
        {
            int count = Mathf.Max(0, Mathf.RoundToInt(diceCount));
            int successes = 0;
            for (int i = 0; i < count; i++)
                if (rng.NextDouble() < 0.5)
                    successes++;
            return successes;
        }

        // The same pool as RollSuccesses, die by die, so a Fate duel can reroll individual misses.
        // Draws the RNG exactly like RollSuccesses: with no Fate on either side the simulated
        // battle consumes the identical random stream it always did.
        private static bool[] RollDice(float diceCount, System.Random rng)
        {
            var dice = new bool[Mathf.Max(0, Mathf.RoundToInt(diceCount))];
            for (int i = 0; i < dice.Length; i++)
                dice[i] = rng.NextDouble() < 0.5;
            return dice;
        }

        private static int CountHits(bool[] dice)
        {
            int hits = 0;
            foreach (bool d in dice)
                if (d) hits++;
            return hits;
        }

        // A side's battle commander (ArmyData.Commander — the army's first hero). Heroes never
        // fight, but the commander's Initiative is added to every combatant of its side
        // (BattleTurnOrder) and its Fate buys rerolls in every exchange (BattleAttackPopupUI's
        // duel). Fate is FateMax: it refills at the start of every battle
        // (UnitData.ReplenishFateForNewBattle), so any future fight starts with the full pool.
        public readonly struct SideCommander
        {
            public readonly bool Present;
            public readonly int Initiative;
            public readonly int Fate;

            public SideCommander(int initiative, int fate)
            {
                Present = true;
                Initiative = initiative;
                Fate = Mathf.Max(0, fate);
            }

            public static SideCommander Of(UnitData hero) =>
                hero == null ? default : new SideCommander(hero.Initiative, hero.FateMax);
            // A hero that is still a card (an event guard, a hero about to be played).
            public static SideCommander Of(CardDefinition hero) =>
                hero == null ? default : new SideCommander(hero.initiative, hero.fate);
            public static SideCommander Of(IEnumerable<UnitData> members) =>
                Of(ArmyData.CommanderOf(members));
        }

        // One armed exchange as the real battle resolves it: both pools rolled, then the Fate duel
        // (FateDuelOrder — defender first, sides alternate while anyone spends; each
        // spend rerolls the first miss with an ordinary die and a failed reroll ends that side's
        // turn), every spend decided by the ONE policy FateDuelAi owns. Returns the damage after
        // the canonical ability modifier chain.
        private static int ResolveExchange(BattleUnit actor, BattleUnit target,
            ref int actorFate, ref int targetFate, System.Random rng)
        {
            bool[] attackDice = RollDice(actor.Attack, rng);
            bool[] defenceDice = RollDice(target.Defense, rng);
            if (actorFate > 0 || targetFate > 0)
            {
                int defendingHp = Mathf.Max(1, Mathf.CeilToInt(target.Hp));
                var order = new FateDuelOrder();
                while (order.TryNext(out bool defenderTurn))
                    order.Report(defenderTurn
                        ? DuelTurn(attackDice, defenceDice, defenceDice, true, ref targetFate, actor, target, defendingHp, rng)
                        : DuelTurn(attackDice, defenceDice, attackDice, false, ref actorFate, actor, target, defendingHp, rng));
            }
            int raw = Mathf.Max(0, CountHits(attackDice) - CountHits(defenceDice));
            return ChallengeResult.ApplyAbilityModifiers(raw, actor.Abilities, target.TypeTags,
                target.Abilities, AbilityMagnitudes.Default);
        }

        private static bool DuelTurn(bool[] attackDice, bool[] defenceDice, bool[] ownDice,
            bool isDefender, ref int fate, BattleUnit actor, BattleUnit target, int defendingHp,
            System.Random rng)
        {
            bool spent = false;
            while (fate > 0 && FateDuelAi.ShouldSpendFate(attackDice, defenceDice, fate, isDefender,
                       actor.Abilities, target.TypeTags, target.Abilities, AbilityMagnitudes.Default,
                       defendingUnitHp: defendingHp))
            {
                int miss = System.Array.IndexOf(ownDice, false);
                if (miss < 0)
                    break;
                ownDice[miss] = rng.NextDouble() < 0.5;
                fate--;
                spent = true;
                if (!ownDice[miss])
                    break;
            }
            return spent;
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
        // Initiative tie-break, then sort descending), same dice (RollSuccesses), just with a
        // RANDOM target each attack rather than BattleTargetSelector's scored pick — this class
        // still never has a live grid/position/range to be smart about (see this file's own top
        // comment), only a remembered/cheat-read roster.
        private struct BattleUnit
        {
            public float Attack;
            public float Defense;
            public IReadOnlyList<string> Abilities;
            public IReadOnlyList<UnitTypeTag> TypeTags;
            public int Initiative;
            public float Hp;

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

        // Longest a single simulated battle plays before being scored a draw — map-sized rosters
        // (a handful of units a side) resolve in a handful of rounds almost always; this is
        // generous headroom for a Monte Carlo trial to terminate, not a tuned balance number.
        private const int MaxSimulatedRounds = 50;

        private static List<BattleUnit> ToBattleUnits(IReadOnlyCollection<DefenderProfile> profiles,
            float extraDefense = 0f, int initiativeBonus = 0)
        {
            var list = new List<BattleUnit>();
            if (profiles == null)
                return list;
            foreach (DefenderProfile p in profiles)
            {
                float hp = Mathf.Max(1f, p.HitPoints);
                list.Add(new BattleUnit
                {
                    Attack = p.Attack,
                    Defense = p.Defense + extraDefense,
                    Abilities = p.Abilities,
                    TypeTags = p.TypeTags,
                    Initiative = p.Initiative + initiativeBonus,
                    Hp = hp,
                    MaxHp = Mathf.Max(hp, p.MaxHitPoints),
                });
            }
            return list;
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
            foreach (UnitData m in CombatantsOf(attacker.Members))
                list.Add(new BattleUnit
                {
                    Attack = m.Attack,
                    Defense = m.Defense,
                    Abilities = m.Abilities.ToList(),
                    TypeTags = m.TypeTags.ToList(),
                    Initiative = m.Initiative + initiativeBonus,
                    Hp = Mathf.Max(1f, m.HitPointsCurrent),
                    MaxHp = Mathf.Max(1f, m.HitPointsMax),
                });
            return list;
        }

        // Aggregate-roster mirror of BattleScreenUI.Combat.cs's ResolveSplashSkills for
        // SimulateOneBattle. Half (floored) of the primary damage minus the victim's own
        // CeramicArmor (Option A — the attacker's offensive bonuses are already in `primaryDamage`),
        // to up to two random OTHER living enemies (Splash) and/or one random living Bio enemy
        // (Scorcher). Positions don't exist in this model, so "adjacent" is approximated as
        // "random other body". Only runs for a Splash/Scorcher actor.
        private static void ApplyRosterSplash(List<BattleUnit> enemyList, int primaryTargetIndex,
            BattleUnit actor, int primaryDamage, System.Random rng)
        {
            bool splash = actor.HasAbility(UnitAbilities.Splash);
            bool scorcher = actor.HasAbility(UnitAbilities.Scorcher);
            if ((!splash && !scorcher) || primaryDamage <= 0)
                return;
            int half = primaryDamage / 2; // floor
            if (half <= 0)
                return;

            var others = new List<int>();
            for (int i = 0; i < enemyList.Count; i++)
                if (i != primaryTargetIndex && enemyList[i].Hp > 0f)
                    others.Add(i);
            if (others.Count == 0)
                return;

            if (splash)
            {
                int hits = Mathf.Min(2, others.Count);
                for (int k = 0; k < hits && others.Count > 0; k++)
                {
                    int pick = others[rng.Next(others.Count)];
                    RosterSideHit(enemyList, pick, half);
                    others.Remove(pick);
                }
            }
            if (scorcher)
            {
                var bio = others.FindAll(i => enemyList[i].TypeTags != null
                    && enemyList[i].TypeTags.Contains(UnitTypeTag.Bio));
                if (bio.Count > 0)
                    RosterSideHit(enemyList, bio[rng.Next(bio.Count)], half);
            }
        }

        private static void RosterSideHit(List<BattleUnit> list, int idx, int half)
        {
            BattleUnit u = list[idx];
            int dmg = half;
            if (u.HasAbility(UnitAbilities.CeramicArmor))
                dmg = Mathf.Max(0, dmg - AbilityMagnitudes.Default.CeramicArmorReduction);
            if (dmg <= 0)
                return;
            u.Hp -= dmg;
            list[idx] = u;
        }

        private static bool AnyAlive(List<BattleUnit> units)
        {
            foreach (BattleUnit u in units)
                if (u.Hp > 0f)
                    return true;
            return false;
        }

        // One full battle: rounds of shuffle-then-sort-by-Initiative turn order, every living
        // actor rolling real dice against a random living enemy, until one side has nobody left or
        // MaxSimulatedRounds runs out. Returns +1 (attackers wiped the defenders), -1 (defenders
        // wiped the attackers), or 0 (mutual wipe, or neither side finished the other off in time —
        // a draw, which every readout counts as half a win).
        private static int SimulateOneBattle(List<BattleUnit> attackers, List<BattleUnit> defenders,
            System.Random rng, int attackerFate = 0, int defenderFate = 0)
        {
            for (int round = 0; round < MaxSimulatedRounds && AnyAlive(attackers) && AnyAlive(defenders); round++)
            {
                var order = new List<(bool isAttacker, int index)>(attackers.Count + defenders.Count);
                for (int i = 0; i < attackers.Count; i++) order.Add((true, i));
                for (int i = 0; i < defenders.Count; i++) order.Add((false, i));

                // Shuffle first (random Initiative tie-break, same reasoning BattleTurnOrder's own
                // class comment gives for why equal Initiative shouldn't always resolve the same
                // way), then a stable sort descending — matches the real turn-order rule exactly.
                for (int i = order.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
                order.Sort((a, b) =>
                {
                    int ai = a.isAttacker ? attackers[a.index].Initiative : defenders[a.index].Initiative;
                    int bi = b.isAttacker ? attackers[b.index].Initiative : defenders[b.index].Initiative;
                    return bi.CompareTo(ai);
                });

                var acted = new HashSet<(bool isAttacker, int index)>();
                var suppressed = new HashSet<(bool isAttacker, int index)>();
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

                    var livingTargets = new List<int>();
                    for (int i = 0; i < enemyList.Count; i++)
                        if (enemyList[i].Hp > 0f)
                            livingTargets.Add(i);
                    if (livingTargets.Count == 0)
                        break; // this side just ran out of targets mid-round — battle's over

                    int targetIndex = livingTargets[rng.Next(livingTargets.Count)];
                    BattleUnit target = enemyList[targetIndex];

                    int damage = turn.isAttacker
                        ? ResolveExchange(actor, target, ref attackerFate, ref defenderFate, rng)
                        : ResolveExchange(actor, target, ref defenderFate, ref attackerFate, rng);
                    target.Hp -= damage;

                    if (damage > 0 && actor.HasAbility(UnitAbilities.ShockAttack))
                    {
                        var targetTurn = (!turn.isAttacker, targetIndex);
                        if (!acted.Contains(targetTurn))
                            suppressed.Add(targetTurn);
                    }

                    if (damage > 0 && target.HasAbility(UnitAbilities.Berserk))
                    {
                        target.Attack += AbilityMagnitudes.Default.BerserkAttackGain;
                        target.Defense = Mathf.Max(1f,
                            target.Defense - AbilityMagnitudes.Default.BerserkDefenseLoss);
                    }

                    enemyList[targetIndex] = target;

                    // UnitAbilities.Splash / Scorcher — this roster model has no positions, so
                    // "neighbours" is approximated as random OTHER living enemies. No-op for an
                    // actor with neither ability, so every existing trial is unchanged.
                    if (damage > 0)
                        ApplyRosterSplash(enemyList, targetIndex, actor, damage, rng);

                    acted.Add(turn);
                }
            }

            bool attackersAlive = AnyAlive(attackers);
            bool defendersAlive = AnyAlive(defenders);
            if (attackersAlive && !defendersAlive) return 1;
            if (defendersAlive && !attackersAlive) return -1;
            return 0;
        }

        // Full-roster win fraction — MonteCarloTrials complete battles (SimulateOneBattle above),
        // fresh HP for every trial. `hexDefenseBonus` is folded into every defending unit's own
        // Defense once here (same as CanDamage's own `extraDefense`), never pre-baked by the
        // caller. Empty/null `enemyUnits` is a trivial
        // win (nothing known to fight — matches CanDamageAll's own "vacuously coverable" reading);
        // empty/null `attackerUnits` against a real enemy roster is a trivial loss.
        public static float WinChance(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> enemyUnits, float hexDefenseBonus = 0f,
            SideCommander attackerCommander = default, SideCommander defenderCommander = default) =>
            Estimate(attackerUnits, enemyUnits, hexDefenseBonus, attackerCommander, defenderCommander).WinChance;

        // Converts a real UnitData into the same per-combatant snapshot DefenderProfile carries for
        // a remembered/cheat-read enemy — used both for our own army (never behind fog of war) and
        // for a REAL, ground-truth-visible enemy roster no memory lookup is needed for (e.g. an
        // AirStrike loiter/repeat target the army is physically sitting on top of right now — see
        // AiAggressionPlanner.TryEnterLoiterAtTarget). Either way this always uses the unit's REAL
        // current HP (never HitPointsMax the way a remembered/fogged enemy has to). Public since
        // 2026-08-26 (air-strike scoring rework) for that second use — same single conversion, no
        // second copy of it anywhere else.
        public static DefenderProfile FromLiveUnit(UnitData unit) =>
            new DefenderProfile(unit.Defense, unit.HasAbility(UnitAbilities.CeramicArmor), unit.TypeTags.ToList(),
                unit.Attack, unit.HitPointsCurrent, unit.Initiative, unit.Abilities.ToList(),
                unit.HitPointsMax, unit.IsGroundCombatant);

        // Richer Monte Carlo readout added 2026-08-24 (project owner's own P1 plan, "WorthIt не
        // оценивает цену победы") alongside the bare win/lose verdict WinChance always returned —
        // a 95% WinChance can still mean the survivors limp home critically wounded, which the old
        // float couldn't say anything about. WinChance(ArmyData, ...) below is now a thin wrapper
        // over Estimate() so every existing pass/fail caller is unaffected.
        public readonly struct BattleEstimate
        {
            public readonly float WinChance;

            // Mean fraction of the attacking army's starting HP (sum across all non-hero members)
            // still standing at the end, averaged only over the trials the attacker actually won —
            // a losing trial says nothing about "surviving" a win that didn't happen.
            public readonly float ExpectedSurvivingHpRatioOnWin;

            // Fraction of WON trials where at least one surviving non-hero member ends the battle
            // at or below half its OWN real MaxHp — the exact predicate
            // RaidWeakerArmyTask.IsCriticallyWounded already applies to a real post-battle army, so
            // this number answers "how often does even a WIN immediately trigger that same
            // return-to-base-to-repair verdict".
            public readonly float CriticalAfterBattleChance;

            public BattleEstimate(float winChance, float expectedSurvivingHpRatioOnWin, float criticalAfterBattleChance)
            {
                WinChance = winChance;
                ExpectedSurvivingHpRatioOnWin = expectedSurvivingHpRatioOnWin;
                CriticalAfterBattleChance = criticalAfterBattleChance;
            }
        }

        // Same full-roster Monte Carlo WinChance(ArmyData, ...) always ran, just also tracking
        // what happens to OUR OWN side across the trials it wins (see BattleEstimate's own
        // comment). Observability only for now (2026-08-24 plan's own explicit scope) — nothing
        // gates on this yet, callers just log it. Seeded identically to the pre-existing
        // WinChance(ArmyData, ...) call (same FromLiveUnit-derived profile list feeds
        // BuildRosterSeed) so this change doesn't shift which battles a given call used to roll.
        // The attacker's commander is read off the live army itself (ArmyData.Commander).
        public static BattleEstimate Estimate(ArmyData attacker, IReadOnlyCollection<DefenderProfile> enemyUnits,
            float hexDefenseBonus = 0f, SideCommander defenderCommander = default)
        {
            enemyUnits = CombatantsOf(enemyUnits);
            if (enemyUnits.Count == 0)
                return new BattleEstimate(1f, 1f, 0f);

            SideCommander attackerCommander = SideCommander.Of(attacker?.Commander);
            List<BattleUnit> baseline = ToAttackerBattleUnits(attacker, attackerCommander.Initiative);
            if (baseline.Count == 0)
                return new BattleEstimate(0f, 0f, 0f);

            // Seeded off the same FromLiveUnit-derived profile the live roster represents — real
            // MaxHp still comes from ToAttackerBattleUnits above (a wounded attacker's true max is
            // not recoverable from DefenderProfile.HitPoints, which only ever carries CURRENT hp).
            var seedProfiles = CombatantsOf(attacker.Members).Select(FromLiveUnit).ToList();
            int seed = BuildRosterSeed(seedProfiles, enemyUnits, hexDefenseBonus,
                attackerCommander, defenderCommander);
            return EstimateCore(baseline, enemyUnits, hexDefenseBonus, seed,
                attackerCommander, defenderCommander);
        }

        // Roster-vs-roster overload (2026-09-14, Housekeeping contact-selection sync) — the same
        // full round-by-round Monte Carlo as the ArmyData overload above, for a virtual/projected
        // attacker that has no live ArmyData (e.g. an enemy DefenderProfile roster read off
        // AiMapMemory, or a Housekeeping virtual defender roster). Both overloads now share the one
        // EstimateCore loop below — no second Monte Carlo copy.
        public static BattleEstimate Estimate(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> defenderUnits, float hexDefenseBonus,
            SideCommander attackerCommander = default, SideCommander defenderCommander = default)
        {
            attackerUnits = CombatantsOf(attackerUnits);
            defenderUnits = CombatantsOf(defenderUnits);
            if (defenderUnits.Count == 0)
                return new BattleEstimate(1f, 1f, 0f);

            List<BattleUnit> baseline = ToBattleUnits(attackerUnits, 0f, attackerCommander.Initiative);
            if (baseline.Count == 0)
                return new BattleEstimate(0f, 0f, 0f);

            int seed = BuildRosterSeed(attackerUnits, defenderUnits, hexDefenseBonus,
                attackerCommander, defenderCommander);
            return EstimateCore(baseline, defenderUnits, hexDefenseBonus, seed,
                attackerCommander, defenderCommander);
        }

        // THE quick "how much fight is in this body" number (Attack + Defense + HitPoints +
        // 0.25 × Initiative) every roster pick uses: strongest-first ordering, swap candidates,
        // the Monte-Carlo pre-filter. One formula, never re-typed at a call site.
        public static float CombatValue(float attack, float defense, float hitPoints, int initiative) =>
            attack + defense + hitPoints + 0.25f * initiative;

        public static float CombatValue(DefenderProfile p) =>
            CombatValue(p.Attack, p.Defense, p.HitPoints, p.Initiative);

        // Every defending body of an opposition, in army order — the flat roster callers use for
        // counts, power and coverage. Built from the per-army list, never kept as a second source.
        public static List<DefenderProfile> UnitsOf(IEnumerable<DefendingArmy> armies)
        {
            var units = new List<DefenderProfile>();
            if (armies != null)
                foreach (DefendingArmy a in armies)
                    if (a.Units != null)
                        units.AddRange(a.Units);
            return units;
        }

        // One defending army of a multi-army hex: its fighting roster and its own commander.
        public readonly struct DefendingArmy
        {
            public readonly IReadOnlyCollection<DefenderProfile> Units;
            public readonly SideCommander Commander;

            public DefendingArmy(IReadOnlyCollection<DefenderProfile> units, SideCommander commander)
            {
                Units = units ?? System.Array.Empty<DefenderProfile>();
                Commander = commander;
            }
        }

        // A hex held by several armies is taken the way the real rules take it: one battle per
        // defending army, strongest defender first (BattleInitiator.FindEnemyAt — the army the
        // attacker is least likely to beat), the surviving attacker carrying its wounds into the
        // next battle (ResolveHexAfterVictory) while both sides' Fate refills for every battle
        // (ReplenishFateForNewBattle). The attack wins only by winning every battle; the hex
        // defence bonus applies to every defending army. A single army is exactly Estimate().
        public static BattleEstimate EstimateSequential(IReadOnlyCollection<DefenderProfile> attackerUnits,
            SideCommander attackerCommander, IReadOnlyList<DefendingArmy> defendingArmies,
            float hexDefenseBonus)
        {
            var armies = (defendingArmies ?? System.Array.Empty<DefendingArmy>())
                .Select(a => new DefendingArmy(CombatantsOf(a.Units), a.Commander))
                .Where(a => a.Units.Count > 0)
                .ToList();
            if (armies.Count <= 1)
                return armies.Count == 0
                    ? new BattleEstimate(1f, 1f, 0f)
                    : Estimate(attackerUnits, armies[0].Units, hexDefenseBonus, attackerCommander,
                        armies[0].Commander);

            attackerUnits = CombatantsOf(attackerUnits);
            List<BattleUnit> baseline = ToBattleUnits(attackerUnits, 0f, attackerCommander.Initiative);
            if (baseline.Count == 0)
                return new BattleEstimate(0f, 0f, 0f);

            // Strongest defender first, judged against the fresh attacker (stable for equal odds).
            List<DefendingArmy> order = armies
                .Select((a, i) => (a, i, win: Estimate(attackerUnits, a.Units, hexDefenseBonus,
                    attackerCommander, a.Commander).WinChance))
                .OrderBy(x => x.win).ThenBy(x => x.i)
                .Select(x => x.a)
                .ToList();

            int seed = BuildRosterSeed(attackerUnits,
                order.SelectMany(a => a.Units).ToList(), hexDefenseBonus, attackerCommander, default);
            foreach (DefendingArmy a in order)
                if (a.Commander.Present)
                    seed = unchecked((seed * 31 + a.Commander.Initiative) * 31 + a.Commander.Fate);
            var rng = new System.Random(seed);
            float startHp = baseline.Sum(u => u.Hp);
            int wins = 0, draws = 0, criticalOnWin = 0;
            float survivingRatioSum = 0f;
            for (int t = 0; t < MonteCarloTrials; t++)
            {
                var attackers = new List<BattleUnit>(baseline);
                var entryStats = new List<BattleUnit>(baseline);
                int result = 1;
                foreach (DefendingArmy a in order)
                {
                    result = SimulateOneBattle(attackers,
                        ToBattleUnits(a.Units, hexDefenseBonus, a.Commander.Initiative), rng,
                        attackerCommander.Fate, a.Commander.Fate);
                    if (result <= 0)
                        break;
                    // Wounds carry into the next battle; in-battle stat changes (Berserk) do not —
                    // the game reverts them when a battle ends (BattleScreenUI.RevertBerserkStacks).
                    var survivors = new List<BattleUnit>(attackers.Count);
                    var survivorStats = new List<BattleUnit>(attackers.Count);
                    for (int i = 0; i < attackers.Count; i++)
                    {
                        if (attackers[i].Hp <= 0f)
                            continue;
                        BattleUnit u = attackers[i];
                        u.Attack = entryStats[i].Attack;
                        u.Defense = entryStats[i].Defense;
                        survivors.Add(u);
                        survivorStats.Add(entryStats[i]);
                    }
                    attackers = survivors;
                    entryStats = survivorStats;
                }
                if (result > 0)
                {
                    wins++;
                    survivingRatioSum += startHp > 0f ? attackers.Sum(u => Mathf.Max(0f, u.Hp)) / startHp : 0f;
                    if (attackers.Any(u => u.Hp > 0f && u.Hp <= u.MaxHp / 2f))
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
            SideCommander attackerCommander, SideCommander defenderCommander)
        {
            var rng = new System.Random(seed);
            float startHp = baseline.Sum(u => u.Hp);

            int wins = 0, draws = 0, criticalOnWin = 0;
            float survivingRatioSum = 0f;
            for (int i = 0; i < MonteCarloTrials; i++)
            {
                var attackers = new List<BattleUnit>(baseline);
                int result = SimulateOneBattle(attackers,
                    ToBattleUnits(defenderUnits, hexDefenseBonus, defenderCommander.Initiative), rng,
                    attackerCommander.Fate, defenderCommander.Fate);
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

        // `attacker`'s own live non-hero roster as the same DefenderProfile snapshot shape —
        // ArmyData convenience overload of the full-roster WinChance above. Thin wrapper over
        // Estimate() (2026-08-24) — every pass/fail caller here keeps working unchanged.
        public static float WinChance(ArmyData attacker, IReadOnlyCollection<DefenderProfile> enemyUnits,
            float hexDefenseBonus = 0f, SideCommander defenderCommander = default) =>
            Estimate(attacker, enemyUnits, hexDefenseBonus, defenderCommander).WinChance;

        // Minimal per-defender read for the coverage check below — just enough to reuse the same
        // expected-damage step BattleTargetSelector.TryScoreTarget already uses for a real attack
        // pick (rawExpected = Attack*0.5 − Defense*0.5), plus the one ability that started this:
        // CeramicArmor's flat reduction. Deliberately NOT a full UnitData/ability set — this is the
        // "adequate, not battle-detailed" version (per the project owner's own call): every other
        // ability (Hyperkinetic/Pyrokinetic/CriticalDamage — all attacker-side bonuses) is left out
        // on purpose. Skipping those can only make CanDamage MORE cautious than a real hit would
        // be, never falsely confident — the safe direction for a rough pre-contact check to be
        // wrong in, unlike skipping the defender's own CeramicArmor would be.
        //
        // TypeTags (added 2026-08-19, project owner's own call) — NOT read by CanDamage/
        // CanDamageAll above, those still only need Defense/CeramicArmor. This is here purely so
        // AiMapMemory can remember a scouted enemy's own Bio/Armored/Mechanical classification
        // alongside its Defense, honestly (only ever set from an actually-observed sighting or
        // guard — see AiMapMemory's own "Видимость с памятью" principle) — AiManagementPlanner's
        // own counter-tech card scoring (Hyperkinetic vs known Armored, Pyrokinetic vs known Bio)
        // reads it via AiMapMemory.KnownEnemyTypeTagCount rather than through this struct
        // directly. Never null — empty when the source had no tags (or wasn't a real UnitData/
        // CardDefinition read at all).
        //
        // Attack/HitPoints/Initiative (added 2026-08-22, project owner's own call: "если мы
        // когда-то видели армию значит мы видели её состав и знаем всё о ней кроме текущего
        // состояния") — the full per-combatant snapshot SimulateOneBattle needs (see WinChance's
        // own DefenderProfile-list overload above) to actually play a real round-by-round fight
        // instead of one flat aggregate exchange. HitPoints is the unit's CURRENT HP as of its own
        // last observation (AiMapMemory.OnVisibilityChanged, 2026-08-26 air-strike-memory fix —
        // was MAX HP, "assume it's healed up since we last saw it"; there is no in-field regen in
        // this game to make that assumption safe, only base-side UnitRepair, so a remembered
        // sighting now freezes damage the same honest way it already freezes composition,
        // corrected only by a later re-observation). A live/cheat source
        // (AiDefencePlanner.CheatEstimateRaiderThreat) already reads the real ArmyData's own
        // current HP the same way. This struct is now
        // used symmetrically for BOTH sides of a fight (see WinChance's own DefenderProfile-list
        // overload below) — "Defender" is a legacy name from when it only ever described the
        // other side; CanDamage/CanDamageAll still only read Defense/HasCeramicArmor off it.
        public readonly struct DefenderProfile
        {
            public readonly float Defense;
            public readonly bool HasCeramicArmor;
            public readonly IReadOnlyList<UnitTypeTag> TypeTags;
            public readonly float Attack;
            public readonly float HitPoints;
            // True maximum HP when the source is live. Older/remembered profile producers that
            // only know current HP may omit it; the constructor then conservatively falls back to
            // HitPoints. The full BattleEstimate tie-break needs this distinction for wounded
            // attackers, otherwise CriticalAfterBattleChance diverges from the live ArmyData path.
            public readonly float MaxHitPoints;
            public readonly int Initiative;
            public readonly IReadOnlyList<string> Abilities;
            // UnitData.IsGroundCombatant carried into the profile (FromLiveUnit), so a profile
            // roster keeps the one "fights in a ground battle" fact after conversion. Producers that
            // only ever describe fighting bodies (remembered enemies, cards, projections) leave the
            // default true. Every WorthIt estimator filters on it — see CombatantsOf.
            public readonly bool IsGroundCombatant;

            public DefenderProfile(float defense, bool hasCeramicArmor, IReadOnlyList<UnitTypeTag> typeTags = null,
                float attack = 0f, float hitPoints = 0f, int initiative = 0,
                IReadOnlyList<string> abilities = null, float maxHitPoints = 0f,
                bool isGroundCombatant = true)
            {
                IsGroundCombatant = isGroundCombatant;
                Defense = defense;
                HasCeramicArmor = hasCeramicArmor;
                TypeTags = typeTags ?? System.Array.Empty<UnitTypeTag>();
                Attack = attack;
                HitPoints = hitPoints;
                MaxHitPoints = maxHitPoints > 0f ? maxHitPoints : hitPoints;
                Initiative = initiative;
                Abilities = abilities ?? (hasCeramicArmor
                    ? (IReadOnlyList<string>)new[] { UnitAbilities.CeramicArmor }
                    : System.Array.Empty<string>());
            }
        }

        // Same rawExpected half-stat step as BattleTargetSelector.TryScoreTarget, CeramicArmor's
        // flat reduction applied on top if the defender carries it (AbilityMagnitudes.Default —
        // this runs long before any BattleAttackPopupUI/live battle exists to read a tuned value
        // from). No FloorToInt/ApplyAbilityModifiers integer rounding here on purpose — this only
        // ever asks "positive or not", so the exact rounding rule doesn't change the answer.
        //
        // `extraDefense` — the hex's own terrain/Base-building bonus (HexDefenseBonus), added
        // 2026-08-20 (project owner's own report). DefenderProfile.Defense is always the
        // defender's own raw stat as memorized, never including where the fight would actually
        // happen — every OTHER read in this file that compares against a real hex (DefenseAt,
        // Score's own enemyDefense parameter) already folds this in; this coverage check used to
        // be the one exception, which could read a defender as damageable off its raw stat alone
        // while the hex bonus on top would actually make it un-killable. Defaults to 0f so a
        // caller with no hex to check against (a pure stat comparison) is unaffected.
        //
        // Public since 2026-08-23 (project owner's own report) — AiManagementPlanner's own
        // UnitCompositionFitBonus needs this exact per-unit check to ask a hypothetical "would
        // THIS candidate card cover a defender none of a StillAssembling raid's current roster
        // can already damage", the same single-source-of-truth rule every other coverage read in
        // this codebase already follows (see CanDamageAll's own comment) — no second copy of this
        // formula anywhere else.
        public static bool CanDamage(float attack, DefenderProfile defender, float extraDefense = 0f)
        {
            float expected = attack * 0.5f - (defender.Defense + extraDefense) * 0.5f;
            if (defender.HasCeramicArmor)
                expected -= AbilityMagnitudes.Default.CeramicArmorReduction;
            return expected > 0f;
        }

        // Composition-aware penetration check for immutable rosters. The raw expected hit must
        // cross the same per-unit threshold as BattleTargetSelector before canonical combat skill
        // modifiers are allowed to change its size.
        public static bool CanDamage(DefenderProfile attacker, DefenderProfile defender,
            float extraDefense = 0f)
        {
            int rawExpected = Mathf.FloorToInt(
                attacker.Attack * 0.5f - (defender.Defense + extraDefense) * 0.5f + 0.5f);
            if (rawExpected <= 0)
                return false;
            return ChallengeResult.ApplyAbilityModifiers(rawExpected, attacker.Abilities,
                defender.TypeTags, defender.Abilities, AbilityMagnitudes.Default) > 0;
        }

        public static bool CanDamageAll(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> defenders, float extraDefense = 0f)
        {
            defenders = CombatantsOf(defenders);
            if (defenders.Count == 0)
                return true;
            attackerUnits = CombatantsOf(attackerUnits);
            if (attackerUnits.Count == 0)
                return false;
            foreach (DefenderProfile defender in defenders)
                if (!attackerUnits.Any(attacker => CanDamage(attacker, defender, extraDefense)))
                    return false;
            return true;
        }

        // Coverage gate on top of Score's overall power read: every known enemy unit needs at
        // least ONE counter somewhere in `attacker`'s own roster. A lopsided net edge doesn't mean
        // much if one specific enemy unit is a brick wall none of our units can actually dent — it
        // just tanks forever while the rest of the fight plays out around it, dragging the
        // exchange out far longer than Score's own single-number read suggests. Null/empty
        // `defenders` (no guard, or a data source that has no per-unit read at all) is vacuously
        // coverable — nothing to fail to cover.
        public static bool CanDamageAll(ArmyData attacker, IReadOnlyCollection<DefenderProfile> defenders, float extraDefense = 0f) =>
            CanDamageAll(attacker?.Members, defenders, extraDefense);

        // Same coverage gate, against a raw unit set instead of a real ArmyData — the shared
        // building block the ArmyData overload above delegates to.
        public static bool CanDamageAll(IEnumerable<UnitData> attackerUnits, IReadOnlyCollection<DefenderProfile> defenders, float extraDefense = 0f)
        {
            defenders = CombatantsOf(defenders);
            if (defenders.Count == 0)
                return true;
            List<UnitData> ourUnits = CombatantsOf(attackerUnits);
            if (ourUnits.Count == 0)
                return false;
            foreach (DefenderProfile defender in defenders)
                if (!ourUnits.Any(u => CanDamage(u.Attack, defender, extraDefense)))
                    return false;
            return true;
        }

        // The hex's own contribution alone, no army — terrain.defenseModifier (see
        // TerrainTypeEntry's own comment: added to the defender's dice pool only, in every real
        // fight) plus a Base building's own Defense stat if one sits here (see
        // BuildingData.IsBase — only Base buildings carry this, per BattingScreenUI.Combat.cs's
        // own gate). Needed on its own for a Hex Event's card-stat guard (AiMapMemory.
        // KnownEventGuardDefenseAt) — that guard is never a live ArmyData sitting on the hex until
        // Explore is chosen, so there's no army to hand DefenseAt, only the hex's own bonus to add
        // on top of the guard's own card total.
        public static float HexDefenseBonus(HexCoord hex, HexMap map)
        {
            float bonus = 0f;
            if (map != null && map.TryGetTerrainAt(hex, out TerrainTypeEntry terrain) && terrain != null)
                bonus += terrain.defenseModifier;

            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building != null && building.IsBase)
                bonus += building.Defense;

            return bonus;
        }
    }
}
