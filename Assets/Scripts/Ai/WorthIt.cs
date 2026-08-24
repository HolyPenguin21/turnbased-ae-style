using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // The one shared "is this fight worth it" estimate every AI planner used to duplicate on its
    // own — AiAggressionPlanner's own IsEnemyWeaker and AiEventPlanner.ShouldExplore were both the
    // exact same flat Attack-sum-vs-Defense-sum comparison, just copy-pasted twice.
    //
    // DefenseAt/HexDefenseBonus fold in the same terrain + Base-building defense bonus a REAL
    // fight actually grants the defender (see BattleScreenUI.Combat.cs's own BeginAttack — the
    // exact same two lookups, just read directly off HexCoord/HexMap instead of a live
    // BattleGrid, since nothing here needs a battle to exist yet). Only ever added to the
    // DEFENDER's side, same as the real fight — never the attacker's.
    //
    // Score/IsWorthIt (added once AiMapMemory started remembering a sighted enemy's own Attack
    // sum too, not just Defense — see KnownEnemySighting.AttackSum/KnownEventGuardAttackAt) are
    // this class's real "is it worth it" answer: they read BOTH directions of the exchange
    // instead of just "can we out-muscle their defense" — our own net edge (AttackSum vs their
    // Defense) minus theirs (their Attack vs our own Defense) — a unit that wins by grinding
    // through a tough defense while getting chewed up doing it no longer scores the same as one
    // that wins cleanly. Still not BattleAi.SimulateRounds' full round-by-round grid playout
    // (AI_ARCHITECTURE.html section 07's "Combat Worth-It Score") — every caller here only ever
    // knows the OTHER side through fog-of-war memory (an aggregate Attack/Defense sum, no
    // composition, no abilities, no per-unit HP/position to place on a grid), so there's nothing
    // to simulate with; this is the richest read the available data actually supports.
    //
    // 2026-08-22 update (project owner's own call): Score/WinChance no longer read a flat
    // deterministic expected-value formula — they now run MonteCarloTrials simulated dice
    // exchanges (SimulateExchangeMargin, same 50/50-per-die mechanic ChallengeResolver.RollDice
    // actually rolls with) and average/tally the outcome. Same two aggregate sums in, same
    // two-sided "our edge minus their edge" question — just answered via actual coin flips instead
    // of always assuming every dice pool lands its mean, so the result now carries the real
    // mechanic's variance instead of hiding it behind one smooth number. Still bounded to the same
    // aggregate-sum data described above — this was never an attempt at simulating a real
    // multi-round fight (that needs BattleAi.SimulateRounds' own full grid/HP data, which the map
    // doesn't have and this file still deliberately doesn't touch).
    // stakes/fateEdge/urgency(goal) (the formula's other two terms) stay out on purpose — there's
    // no target-value system anywhere in the codebase yet to price "stakes" against, and no
    // goal/urgency object flows into any of these callers today (Оборона/Атака, section 02.4/
    // 02.5, are still scoring-only per that same doc — see its own roadmap Phase 2).
    //
    // CanDamageAll/DefenderProfile (added per the project owner's own report: Score alone can
    // read "worth it" on a lopsided army-vs-army matchup — e.g. two 5/4 units vs one 8/6 — that
    // real per-unit dice actually favor the other side, since Attack/Defense sums pool as if
    // every hit were rolled against one shared total instead of each unit fighting its own
    // target) — a coverage check that every known enemy unit has at least one real counter in
    // our own roster, using the same expected-damage read the real in-battle target picker uses,
    // just without the full round-by-round grid playout (per the project owner's own call —
    // "adequate, not as detailed as in battle").
    public static class WorthIt
    {
        // Attack-sum of `army`'s own non-hero members — the same side of the comparison every
        // caller here always uses for the ATTACKING army (heroes never counted, matching every
        // other flat Attack/Defense sum already in this codebase).
        public static float AttackSum(ArmyData army) => army?.Members.Where(m => !m.IsHero).Sum(m => m.Attack) ?? 0f;

        // Own non-hero Defense sum, no hex bonus — used both as DefenseAt's own first term and,
        // on its own, as the ATTACKER's side of Score's return-fire read (an attacking army isn't
        // standing on a defensible hex it gets credit for, it's marching onto the defender's).
        public static float DefenseSum(ArmyData army) => army?.Members.Where(m => !m.IsHero).Sum(m => m.Defense) ?? 0f;

        // `defender`'s own non-hero Defense sum PLUS whatever `hex` itself would grant a real
        // defender standing there (terrain + Base-building bonus — see HexDefenseBonus). This is
        // what a REAL fight on this hex would actually roll against, not just the army's own raw
        // stats.
        public static float DefenseAt(ArmyData defender, HexCoord hex, HexMap map) => DefenseSum(defender) + HexDefenseBonus(hex, map);

        // Number of simulated exchanges Score/WinChance each run per call — capped at 100 per the
        // project owner's own explicit call (2026-08-22: "ограничиваемся сотней вызовов"). Every
        // map-level caller here runs this once or twice per candidate per AI turn, never per
        // frame, so 100 trials (each just a few hundred coin flips — see RollSuccesses) costs
        // nothing worth measuring; it's a named constant purely so nobody quietly cranks it up on
        // a hot path later without a reason to.
        private const int MonteCarloTrials = 100;

        // Deterministic per-matchup seed (2026-08-24 fix, project owner's own report) — built ONLY
        // from the raw numeric stats that describe the matchup itself (Attack/Defense/HP/
        // Initiative/hex bonus), never from GetHashCode() of a string or object (not guaranteed
        // stable call-to-call, see .NET's own documented caveat). The SAME matchup — same army
        // composition, same HP, same enemy, same hex — therefore always seeds the SAME
        // System.Random and always plays out the SAME MonteCarloTrials trials, so a caller that
        // asks WinChance the same question twice in a row (e.g. this class's own log-vs-verdict
        // double read) gets the same answer both times. A real change to any input (a unit takes
        // damage, the roster changes, a different enemy, a different hex) changes the seed and
        // rolls a fresh set of trials. Deliberately its OWN System.Random per call, never
        // UnityEngine.Random — this evaluation is read-only strategic bookkeeping, not a real game
        // event, and must never consume (or be affected by) the same global RNG stream the actual
        // game systems roll real, game-affecting outcomes from.
        private static int BuildSeed(params float[] values)
        {
            unchecked
            {
                int hash = 17;
                foreach (float v in values)
                    hash = hash * 31 + System.BitConverter.SingleToInt32Bits(v);
                return hash;
            }
        }

        private static int BuildRosterSeed(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> enemyUnits, float hexDefenseBonus)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + System.BitConverter.SingleToInt32Bits(hexDefenseBonus);
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
                hash = hash * 31 + p.Initiative;
                hash = hash * 31 + (p.HasCeramicArmor ? 1 : 0);
                return hash;
            }
        }

        // One die-pool's worth of successes, same 50/50-per-die mechanic the real battle actually
        // rolls with (ChallengeResolver.RollDice — half the faces are a miss, same odds). `diceCount`
        // is always an aggregate Attack/Defense sum, already integer-valued in practice —
        // RoundToInt only guards against float drift from summing. `rng` — this evaluation's own
        // local System.Random (see BuildSeed's own comment), never UnityEngine.Random.
        private static int RollSuccesses(float diceCount, System.Random rng)
        {
            int count = Mathf.Max(0, Mathf.RoundToInt(diceCount));
            int successes = 0;
            for (int i = 0; i < count; i++)
                if (rng.NextDouble() < 0.5)
                    successes++;
            return successes;
        }

        // Net damage margin for ONE simulated exchange (positive = we came out ahead this trial) —
        // both directions rolled for real: our Attack vs their Defense, AND their Attack vs our
        // Defense, same two terms this class always compared, just each one an actual roll now
        // instead of its flat expected value (N*0.5). Still a single simultaneous exchange, not a
        // multi-round HP-depletion fight — the map only ever knows the OTHER side as an aggregate
        // sum (no composition, no HP, no positions — see this class's own top comment), so there's
        // no per-unit HP here to actually deplete round over round the way a real battle would.
        private static float SimulateExchangeMargin(float ourAttack, float ourDefense, float enemyAttack, float enemyDefense,
            System.Random rng)
        {
            int ourDamage = Mathf.Max(0, RollSuccesses(ourAttack, rng) - RollSuccesses(enemyDefense, rng));
            int enemyDamage = Mathf.Max(0, RollSuccesses(enemyAttack, rng) - RollSuccesses(ourDefense, rng));
            return ourDamage - enemyDamage;
        }

        // Graded two-sided net-advantage margin — MonteCarloTrials simulated exchanges (above),
        // averaged. Positive = worth it on average, magnitude = how lopsided; `enemyDefense`/
        // `enemyAttack` are always the OTHER side's remembered aggregate sums (KnownEnemySighting.
        // DefenseSum/AttackSum, or a Hex Event guard's card-stat sums — `enemyDefense` should
        // already include HexDefenseBonus where the caller has a hex to add it from, same as
        // before).
        public static float Score(ArmyData attacker, float enemyDefense, float enemyAttack)
        {
            float ourAttack = AttackSum(attacker);
            float ourDefense = DefenseSum(attacker);
            var rng = new System.Random(BuildSeed(ourAttack, ourDefense, enemyAttack, enemyDefense));
            float total = 0f;
            for (int i = 0; i < MonteCarloTrials; i++)
                total += SimulateExchangeMargin(ourAttack, ourDefense, enemyAttack, enemyDefense, rng);
            return total / MonteCarloTrials;
        }

        public static bool IsWorthIt(ArmyData attacker, float enemyDefense, float enemyAttack) => Score(attacker, enemyDefense, enemyAttack) > 0f;

        // The single win-chance formula every category's army-vs-threat comparison must route
        // through from now on (2026-08-22, project owner's own explicit call: "все сравнения армий
        // на карте должны происходить только через worth it для всех задач и методов") — Оборона's
        // Active posture sizes its own composition against this directly (AiConfig.
        // defenceActiveWinChance, a 60/40 target), and RaidWeakerArmyTask.IsReady routes its own
        // two-sided edge check here too instead of keeping a second copy of the same math.
        //
        // Monte Carlo (2026-08-22, project owner's own call): MonteCarloTrials simulated exchanges
        // (SimulateExchangeMargin above), the fraction we came out strictly ahead in; a tie
        // (margin == 0 — a real outcome once both sides' rolled damage happens to land equal, not
        // just an edge case) splits 50/50 into that fraction instead of favoring either side. This
        // replaces the old closed-form ourPower/(ourPower+enemyPower) ratio, which was a smooth
        // deterministic curve with no notion of variance at all — two equal armies still land
        // close to 0.5 here (exactly 0.5 in expectation, same symmetry as before), just as a Monte
        // Carlo estimate around that point rather than a bit-exact one. `ourAttack`/`ourDefense`
        // let a caller substitute a discounted attack read (e.g. RaidWeakerArmyTask's own
        // wounded-unit discount) without needing a live ArmyData; `enemyDefense` should already
        // include any hex bonus, same convention Score above already uses.
        public static float WinChance(float ourAttack, float ourDefense, float enemyAttack, float enemyDefense)
        {
            var rng = new System.Random(BuildSeed(ourAttack, ourDefense, enemyAttack, enemyDefense));
            int wins = 0, ties = 0;
            for (int i = 0; i < MonteCarloTrials; i++)
            {
                float margin = SimulateExchangeMargin(ourAttack, ourDefense, enemyAttack, enemyDefense, rng);
                if (margin > 0f) wins++;
                else if (margin == 0f) ties++;
            }
            return (wins + ties * 0.5f) / MonteCarloTrials;
        }

        public static float WinChance(ArmyData attacker, float enemyDefense, float enemyAttack) =>
            WinChance(AttackSum(attacker), DefenseSum(attacker), enemyAttack, enemyDefense);

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
            public bool HasCeramicArmor;
            public int Initiative;
            public float Hp;
        }

        // Longest a single simulated battle plays before being scored a draw — map-sized rosters
        // (a handful of units a side) resolve in a handful of rounds almost always; this is
        // generous headroom for a Monte Carlo trial to terminate, not a tuned balance number.
        private const int MaxSimulatedRounds = 50;

        private static List<BattleUnit> ToBattleUnits(IReadOnlyCollection<DefenderProfile> profiles, float extraDefense = 0f)
        {
            var list = new List<BattleUnit>();
            if (profiles == null)
                return list;
            foreach (DefenderProfile p in profiles)
                list.Add(new BattleUnit
                {
                    Attack = p.Attack,
                    Defense = p.Defense + extraDefense,
                    HasCeramicArmor = p.HasCeramicArmor,
                    Initiative = p.Initiative,
                    Hp = Mathf.Max(1f, p.HitPoints),
                });
            return list;
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
        // same "draw" reading SimulateExchangeMargin's own tie already used).
        private static int SimulateOneBattle(List<BattleUnit> attackers, List<BattleUnit> defenders, System.Random rng)
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

                foreach ((bool isAttacker, int index) turn in order)
                {
                    List<BattleUnit> ownList = turn.isAttacker ? attackers : defenders;
                    List<BattleUnit> enemyList = turn.isAttacker ? defenders : attackers;
                    BattleUnit actor = ownList[turn.index];
                    if (actor.Hp <= 0f)
                        continue; // killed earlier this same round — skips its turn, same as a real one would

                    var livingTargets = new List<int>();
                    for (int i = 0; i < enemyList.Count; i++)
                        if (enemyList[i].Hp > 0f)
                            livingTargets.Add(i);
                    if (livingTargets.Count == 0)
                        break; // this side just ran out of targets mid-round — battle's over

                    int targetIndex = livingTargets[rng.Next(livingTargets.Count)];
                    BattleUnit target = enemyList[targetIndex];

                    int damage = Mathf.Max(0, RollSuccesses(actor.Attack, rng) - RollSuccesses(target.Defense, rng));
                    if (target.HasCeramicArmor)
                        damage = Mathf.Max(0, damage - AbilityMagnitudes.Default.CeramicArmorReduction);
                    target.Hp -= damage;
                    enemyList[targetIndex] = target;
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
        // Defense once here (same as CanDamage's own `extraDefense`), not pre-baked by the caller
        // the way the aggregate-sum overload above expects. Empty/null `enemyUnits` is a trivial
        // win (nothing known to fight — matches CanDamageAll's own "vacuously coverable" reading);
        // empty/null `attackerUnits` against a real enemy roster is a trivial loss.
        public static float WinChance(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> enemyUnits, float hexDefenseBonus = 0f)
        {
            if (enemyUnits == null || enemyUnits.Count == 0)
                return 1f;
            if (attackerUnits == null || attackerUnits.Count == 0)
                return 0f;

            var rng = new System.Random(BuildRosterSeed(attackerUnits, enemyUnits, hexDefenseBonus));
            int wins = 0, draws = 0;
            for (int i = 0; i < MonteCarloTrials; i++)
            {
                int result = SimulateOneBattle(ToBattleUnits(attackerUnits), ToBattleUnits(enemyUnits, hexDefenseBonus), rng);
                if (result > 0) wins++;
                else if (result == 0) draws++;
            }
            return (wins + draws * 0.5f) / MonteCarloTrials;
        }

        // Converts a real UnitData into the same per-combatant snapshot DefenderProfile carries for
        // a remembered/cheat-read enemy — our own army is never behind fog of war, so this always
        // uses the unit's REAL current HP (never HitPointsMax the way a remembered enemy has to).
        private static DefenderProfile FromLiveUnit(UnitData unit) =>
            new DefenderProfile(unit.Defense, unit.HasAbility(UnitAbilities.CeramicArmor), unit.TypeTags.ToList(),
                unit.Attack, unit.HitPointsCurrent, unit.Initiative);

        // `attacker`'s own live non-hero roster as the same DefenderProfile snapshot shape —
        // ArmyData convenience overload of the full-roster WinChance above.
        public static float WinChance(ArmyData attacker, IReadOnlyCollection<DefenderProfile> enemyUnits, float hexDefenseBonus = 0f) =>
            WinChance(attacker?.Members.Where(m => !m.IsHero).Select(FromLiveUnit).ToList(), enemyUnits, hexDefenseBonus);

        // Threshold-gated version — also requires CanDamageAll (below), same as every other real
        // readiness check here: raw power alone can overstate a fight where nothing in `attacker`
        // can actually scratch the toughest defender (see CanDamageAll's own comment). `hexBonus`
        // — the defender's own terrain/Base-building bonus, applied per-unit the same way CanDamage
        // already does; NOT the same value as `enemyDefense`'s own hex bonus fold-in (that one's
        // already baked into the sum by the time it gets here).
        //
        // Routes through the full-roster WinChance whenever `defenders` actually carries a real
        // per-unit snapshot (2026-08-22 — see this section's own header comment); falls back to
        // the aggregate-sum WinChance only for the (now rare) case of a target with no remembered
        // composition at all, same as before.
        public static bool MeetsWinChance(ArmyData attacker, float enemyDefense, float enemyAttack,
            IReadOnlyCollection<DefenderProfile> defenders, float threshold, float hexBonus = 0f)
        {
            float chance = defenders != null && defenders.Count > 0
                ? WinChance(attacker, defenders, hexBonus)
                : WinChance(attacker, enemyDefense, enemyAttack);
            return chance >= threshold && CanDamageAll(attacker, defenders, hexBonus);
        }

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
        // instead of one flat aggregate exchange. HitPoints is
        // deliberately never the remembered enemy's CURRENT HP — nothing here has any honest way
        // to know that (fog of war never reveals it, only the last-seen composition does) — it's
        // always the unit's MAX HP, i.e. "assume it's healed up since we last saw it", the same
        // assumption a real player reasoning from memory would have to make too. A live/cheat
        // source (AiDefencePlanner.CheatEstimateRaiderThreat) is free to pass the real CURRENT HP
        // instead, since that path already reads the true ArmyData directly. This struct is now
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
            public readonly int Initiative;

            public DefenderProfile(float defense, bool hasCeramicArmor, IReadOnlyList<UnitTypeTag> typeTags = null,
                float attack = 0f, float hitPoints = 0f, int initiative = 0)
            {
                Defense = defense;
                HasCeramicArmor = hasCeramicArmor;
                TypeTags = typeTags ?? System.Array.Empty<UnitTypeTag>();
                Attack = attack;
                HitPoints = hitPoints;
                Initiative = initiative;
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

        // Coverage gate on top of Score's overall power read: every known enemy unit needs at
        // least ONE counter somewhere in `attacker`'s own roster. A lopsided net edge doesn't mean
        // much if one specific enemy unit is a brick wall none of our units can actually dent — it
        // just tanks forever while the rest of the fight plays out around it, dragging the
        // exchange out far longer than Score's own single-number read suggests. Null/empty
        // `defenders` (no guard, or a data source that has no per-unit read at all) is vacuously
        // coverable — nothing to fail to cover.
        public static bool CanDamageAll(ArmyData attacker, IReadOnlyCollection<DefenderProfile> defenders, float extraDefense = 0f) =>
            CanDamageAll(attacker?.Members.Where(m => !m.IsHero), defenders, extraDefense);

        // Same coverage gate, against a raw unit set instead of a real ArmyData — the shared
        // building block the ArmyData overload above delegates to.
        public static bool CanDamageAll(IEnumerable<UnitData> attackerUnits, IReadOnlyCollection<DefenderProfile> defenders, float extraDefense = 0f)
        {
            if (defenders == null || defenders.Count == 0)
                return true;
            List<UnitData> ourUnits = attackerUnits?.ToList();
            if (ourUnits == null || ourUnits.Count == 0)
                return false;
            foreach (DefenderProfile defender in defenders)
                if (!ourUnits.Any(u => CanDamage(u.Attack, defender, extraDefense)))
                    return false;
            return true;
        }

        // Score/IsWorthIt's own two-sided net-edge margin, PLUS the coverage gate above — both
        // need to pass. `defenders` is optional (pass null/empty where no per-unit read is
        // available at all) so existing callers keep working unchanged. `hexDefenseBonus` — see
        // CanDamage's own comment; `enemyDefense` above already has it folded into the aggregate
        // for the edge read, this is the SAME number handed separately so the per-unit coverage
        // check applies it too. Defaults to 0f (source-compatible with a caller that has no hex).
        //
        // Routes through the full-roster WinChance whenever `defenders` carries a real per-unit
        // snapshot (see MeetsWinChance's own comment — same reasoning, same 2026-08-22 change);
        // falls back to the aggregate-sum Score only when no composition is remembered at all.
        public static bool IsWorthIt(ArmyData attacker, float enemyDefense, float enemyAttack,
            IReadOnlyCollection<DefenderProfile> defenders, float hexDefenseBonus = 0f)
        {
            bool worthIt = defenders != null && defenders.Count > 0
                ? WinChance(attacker, defenders, hexDefenseBonus) > 0.5f
                : IsWorthIt(attacker, enemyDefense, enemyAttack);
            return worthIt && CanDamageAll(attacker, defenders, hexDefenseBonus);
        }

        // The hex's own contribution alone, no army — terrain.defenseModifier (see
        // TerrainTypeEntry's own comment: added to the defender's dice pool only, in every real
        // fight) plus a Base-tagged building's own Defense stat if one sits here (see
        // UnitAbilities.Base — only Base buildings carry this, per BattingScreenUI.Combat.cs's
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
            if (building != null && building.HasAbility(UnitAbilities.Base))
                bonus += building.Defense;

            return bonus;
        }
    }
}
