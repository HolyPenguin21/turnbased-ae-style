using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // Pure, side-effect-free readout of what a NOT-YET-FLOWN AirStrike sortie would probably do to
    // a known target roster (2026-08-26, AirStrike/Raid coordination spec, project owner's own
    // report — item 4). Sits next to WorthIt (the AI's own other pure combat-estimate home) rather
    // than inside AviationCombatPresenter itself, which is a MonoBehaviour UI adapter that actually
    // RUNS the real strike (see its own RunAirStrike) and must never be driven from a planning
    // pass. Reuses the exact same dice mechanic RunAirStrike's own BattleAttackPopupUI.Begin call
    // resolves through — WorthIt.RollSuccesses (50/50 per die, same as ChallengeResolver.RollDice)
    // and AbilityMagnitudes.Default.CeramicArmorReduction applied last, same order ResolveDamage
    // itself applies it — never a second, AI-only damage formula.
    //
    // Deliberately mirrors RunAirStrike's own SHAPE, not WorthIt.SimulateOneBattle's: sequential
    // per-aircraft attacks, never a round-robin turn order (an air strike has no return fire — see
    // RunAirStrike's own comment, "no retaliation in this pass"), each one hitting a single
    // uniformly-random still-alive defender, same as CollectStrikeTargets/pool[Random.Range(...)]
    // picks for real. Attacker-side abilities (Hyperkinetic/Pyrokinetic/CriticalDamage) are left
    // out on purpose, same reasoning WorthIt.CanDamage's own comment already gives for every other
    // AI pre-contact estimate in this codebase: skipping them can only make this MORE cautious than
    // a real strike, never falsely confident.
    public static class AviationCombatEstimator
    {
        // Own trial count, not a shared constant with WorthIt.MonteCarloTrials — this estimator's
        // own per-call cost (a handful of aircraft against a handful of known defenders) is
        // unrelated to WorthIt's, it just happens to land on the same number.
        private const int Trials = 100;

        public readonly struct AirStrikeEstimate
        {
            public readonly float ExpectedDefenseAfter;
            public readonly float ExpectedAttackAfter;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> ExpectedDefendersAfter;
            public readonly float ExpectedDamage;
            // Three added 2026-08-26 (air-strike scoring rework, project owner's own spec section
            // 2 — "уничтожение юнитов") — read off the SAME Trials completed strikes ExpectedDamage
            // already averages over, never a second simulation pass. KillAnyProbability: fraction of
            // trials where at least one defender died. ExpectedKillCount: mean defenders killed per
            // trial. WipeProbability: fraction of trials where EVERY defender died (a stronger,
            // per-trial reading than "ExpectedDefendersAfter ended up empty", which only says the
            // AVERAGE remaining roster is empty and can hide a coin-flip wipe behind a merely-heavy
            // average casualty count).
            public readonly float KillAnyProbability;
            public readonly float ExpectedKillCount;
            public readonly float WipeProbability;

            public AirStrikeEstimate(float expectedDefenseAfter, float expectedAttackAfter,
                IReadOnlyList<WorthIt.DefenderProfile> expectedDefendersAfter, float expectedDamage,
                float killAnyProbability = 0f, float expectedKillCount = 0f, float wipeProbability = 0f)
            {
                ExpectedDefenseAfter = expectedDefenseAfter;
                ExpectedAttackAfter = expectedAttackAfter;
                ExpectedDefendersAfter = expectedDefendersAfter;
                ExpectedDamage = expectedDamage;
                KillAnyProbability = killAnyProbability;
                ExpectedKillCount = expectedKillCount;
                WipeProbability = wipeProbability;
            }
        }

        // `knownDefense`/`knownAttack`/`knownDefenders` — the exact same three numbers
        // AirStrikeTask.StrikeTarget already carries (see that struct's own comment), never read
        // from AiMapMemory directly here: this is a pure function over caller-supplied data, no
        // registry/memory lookups of its own, so it can never leak an unknown enemy characteristic
        // the caller didn't already have honest access to.
        //
        // Null/empty `knownDefenders` (no remembered per-unit composition, only an aggregate
        // Defense/Attack sum) reports the strike as a no-op — the aggregate numbers pass through
        // unchanged. There's no per-unit roster here to simulate a random-target strike against,
        // same "nothing to simulate with" limitation WorthIt's own aggregate-sum fallback already
        // lives with everywhere else in this codebase.
        //
        // Monte Carlo, not a closed form (same reasoning WorthIt's own top comment gives) —
        // MonteCarloTrials complete sequential strikes, averaged per-defender remaining HP. A
        // defender whose average remaining HP lands at/below zero is dropped from
        // ExpectedDefendersAfter entirely (expected dead); every survivor keeps its own real
        // Attack/Defense/CeramicArmor/TypeTags/Initiative, just with HitPoints replaced by its own
        // average REMAINING hp across the trials it lived — the caller's own next WinChance call
        // then fights a Monte Carlo battle against an already-wounded defender, same way a real
        // ground raid arriving after a real air strike would.
        public static AirStrikeEstimate EstimateAirStrike(IReadOnlyList<UnitData> aircraft, float knownDefense, float knownAttack,
            IReadOnlyList<WorthIt.DefenderProfile> knownDefenders)
        {
            if (aircraft == null || aircraft.Count == 0 || knownDefenders == null || knownDefenders.Count == 0)
                return new AirStrikeEstimate(knownDefense, knownAttack, knownDefenders ?? System.Array.Empty<WorthIt.DefenderProfile>(), 0f);

            var rng = new System.Random(BuildSeed(aircraft, knownDefenders));
            int n = knownDefenders.Count;
            var hpSum = new float[n];
            float totalDamageSum = 0f;
            int killAnyTrials = 0, wipeTrials = 0;
            float killCountSum = 0f;

            for (int trial = 0; trial < Trials; trial++)
            {
                var hp = new float[n];
                for (int i = 0; i < n; i++)
                    hp[i] = Mathf.Max(1f, knownDefenders[i].HitPoints);
                float startHp = hp.Sum();

                var alive = new List<int>(n);
                for (int i = 0; i < n; i++)
                    alive.Add(i);

                foreach (UnitData plane in aircraft)
                {
                    if (alive.Count == 0)
                        break; // nothing left standing this trial either — matches RunAirStrike's own early-out
                    int idx = alive[rng.Next(alive.Count)];
                    int atk = WorthIt.RollSuccesses(plane.Attack, rng);
                    int def = WorthIt.RollSuccesses(knownDefenders[idx].Defense, rng);
                    int damage = Mathf.Max(0, atk - def);
                    if (knownDefenders[idx].HasCeramicArmor)
                        damage = Mathf.Max(0, damage - AbilityMagnitudes.Default.CeramicArmorReduction);
                    hp[idx] -= damage;
                    if (hp[idx] <= 0f)
                    {
                        hp[idx] = 0f;
                        alive.Remove(idx);
                    }
                }

                for (int i = 0; i < n; i++)
                    hpSum[i] += hp[i];
                totalDamageSum += startHp - hp.Sum();

                int killedThisTrial = n - alive.Count;
                killCountSum += killedThisTrial;
                if (killedThisTrial >= 1)
                    killAnyTrials++;
                if (killedThisTrial == n)
                    wipeTrials++;
            }

            var expectedDefenders = new List<WorthIt.DefenderProfile>();
            float expectedDefense = 0f, expectedAttack = 0f;
            for (int i = 0; i < n; i++)
            {
                float meanHp = hpSum[i] / Trials;
                if (meanHp <= 0.01f)
                    continue; // expected dead on average — dropped from the post-strike roster entirely
                WorthIt.DefenderProfile original = knownDefenders[i];
                expectedDefenders.Add(new WorthIt.DefenderProfile(original.Defense, original.HasCeramicArmor, original.TypeTags,
                    original.Attack, meanHp, original.Initiative));
                expectedDefense += original.Defense;
                expectedAttack += original.Attack;
            }

            return new AirStrikeEstimate(expectedDefense, expectedAttack, expectedDefenders, totalDamageSum / Trials,
                (float)killAnyTrials / Trials, killCountSum / Trials, (float)wipeTrials / Trials);
        }

        // Deterministic per-matchup seed, same reasoning as WorthIt.BuildSeed's own comment — built
        // only from the raw numeric stats describing the matchup (never GetHashCode() of a string/
        // object), so the same aircraft roster against the same known defenders always plays out
        // the same Trials strikes.
        private static int BuildSeed(IReadOnlyList<UnitData> aircraft, IReadOnlyList<WorthIt.DefenderProfile> defenders)
        {
            unchecked
            {
                int hash = 17;
                foreach (UnitData plane in aircraft)
                    hash = hash * 31 + System.BitConverter.SingleToInt32Bits(plane.Attack);
                hash = hash * 31 + 7919; // separates the aircraft roster from the defender roster below
                foreach (WorthIt.DefenderProfile defender in defenders)
                {
                    hash = hash * 31 + System.BitConverter.SingleToInt32Bits(defender.Defense);
                    hash = hash * 31 + System.BitConverter.SingleToInt32Bits(defender.HitPoints);
                }
                return hash;
            }
        }
    }
}
