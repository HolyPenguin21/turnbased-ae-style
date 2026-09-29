using System.Collections.Generic;
using Game.Cards;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // Shared target-desirability scoring, split out of BattleAi.cs — used by BOTH the live
    // per-turn pick (BattleAi.ChooseAction) and the round-simulation pick (BattleAi.RunOneRound),
    // so a projected fight can never favor a target the real AI wouldn't actually go for, or vice
    // versa.
    public static class BattleTargetSelector
    {
        // Small enough next to the x10000/x10 finishing-blow/damage-efficiency tiers to only ever
        // nudge a choice between otherwise-close candidates toward "this hit also knocks the
        // target out of the round" (UnitAbilities.ShockAttack) — never overrides a genuinely
        // better kill/damage pick.
        private const float ShockAttackTargetBonus = 5f;

        // Same tier as ShockAttackTargetBonus — only a tie-breaker, never overrides a kill/damage
        // pick. Per net-enemy orthogonal neighbour of the candidate for a Splash actor (the
        // half-damage spread lands on them, or on the actor's own side if they're friendly), and
        // per enemy Bio neighbour for a Scorcher actor. Zero for an actor with neither.
        private const float SplashNeighbourBonus = 3f;

        private static float SplashSpreadBonus(BattleGrid grid, UnitData actor, int targetRow, int targetCol)
        {
            bool splash = actor.HasAbility(UnitAbilities.Splash);
            bool scorcher = actor.HasAbility(UnitAbilities.Scorcher);
            if (!splash && !scorcher)
                return 0f;

            int splashNet = 0, scorcherBio = 0;
            int[] dRow = { -1, 1, 0, 0 };
            int[] dCol = { 0, 0, -1, 1 };
            for (int i = 0; i < 4; i++)
            {
                UnitData n = grid.Get(targetRow + dRow[i], targetCol + dCol[i]);
                if (n == null || n == actor)
                    continue;
                bool enemy = n.Owner != actor.Owner;
                splashNet += enemy ? 1 : -1;
                if (enemy && n.TypeTags.Contains(UnitTypeTag.Bio))
                    scorcherBio++;
            }

            float bonus = 0f;
            if (splash)
                bonus += splashNet * SplashNeighbourBonus;
            if (scorcher)
                bonus += scorcherBio * SplashNeighbourBonus;
            return bonus;
        }

        // Target priority, highest to lowest: finishing blow (cheapest kill first) > damage
        // efficiency (how much of our attack actually gets through the target's own Defense, with
        // the target's own Attack only as a minor tiebreak between similarly-easy targets) >
        // unreachable-for-damage (rawExpected <= 0, or ability modifiers — CeramicArmor in
        // particular — knock the modified damage back down to 0).
        //
        // `candidateHp` is a parameter rather than read off UnitData directly because the two
        // callers track HP differently — TryFindBestReachableTarget works off SimulateRounds' own
        // shadow `hp` dictionary (mid-playout, may already be lower than the real UnitData), while
        // TryChooseAttackTarget reads the live `candidate.HitPointsCurrent`.
        //
        // candidateNotYetActedThisRound: whether `candidate` still has an action coming this round
        // — a landed ShockAttack hit only actually costs it something if it does. Callers compute
        // this from whatever "turn order + current index" list they're working off (see
        // TryFindBestReachableTarget/TryChooseAttackTarget below for the two different sources).
        //
        // Returns false for a target this attack can't meaningfully hurt — `score`/`reason` are
        // still filled in on a false return so TryChooseAttackTarget can still compare "useless"
        // candidates against each other for its own last-resort fallback (a live Duel Challenge
        // rolls real dice, so an ~0-expected target can still land a hit via variance or Fate);
        // TryFindBestReachableTarget's deterministic expected-value model has no such variance to
        // hope for, so it skips a false return entirely and lets the actor advance instead — see
        // each caller's own handling.
        public static bool TryScoreTarget(UnitData actor, UnitData candidate, float candidateHp,
            AbilityMagnitudes magnitudes, bool candidateNotYetActedThisRound,
            out float score, out float expectedDamage, out AiThoughtCategory reason,
            int defenderBonusDice = 0, int? actorAttackOverride = null, int? candidateDefenseOverride = null,
            int? candidateAttackOverride = null)
        {
            BattleCombatOdds.ExchangeOdds odds = BattleCombatOdds.Evaluate(
                actor, candidate, defenderBonusDice, magnitudes, candidateHp,
                actorAttackOverride, candidateDefenseOverride);
            expectedDamage = odds.ExpectedDamage;

            if (expectedDamage <= 0.0001f)
            {
                int candidateAttack = candidateAttackOverride ?? candidate.Attack;
                score = -1000f + candidateAttack + candidate.Initiative;
                reason = AiThoughtCategory.UselessTargetSkip;
                return false;
            }

            // Target utility is driven by actual removal probability first, then by useful damage
            // and the amount of enemy output being denied. This replaces the old binary
            // "expected damage >= HP" shortcut, which could not distinguish a likely kill from a
            // one-in-many lucky roll and undervalued high-impact units about to act.
            int threatAttack = candidateAttackOverride ?? candidate.Attack;
            score = odds.KillProbability * 10000f
                + Mathf.Min(expectedDamage, Mathf.Max(0f, candidateHp)) * 100f
                + threatAttack * 2f
                + candidate.Initiative * 1.5f;

            if (candidateNotYetActedThisRound)
                score += threatAttack * 1.5f + candidate.Initiative * 2f;

            reason = odds.KillProbability >= 0.5f
                ? AiThoughtCategory.FinishingBlow
                : AiThoughtCategory.PriorityTarget;

            if (actor.HasAbility(UnitAbilities.ShockAttack) && candidateNotYetActedThisRound)
                score += ShockAttackTargetBonus;

            return true;
        }

        // order/currentIndex: the SAME simulated turn-order list/position RunOneRound is already
        // iterating (see BattleAi.RunOneRound) — "not yet acted" means order.IndexOf(candidate) is
        // still ahead of the round's current position, exactly the check
        // BattleScreenUI.Combat.cs's SkipRemainingTurnThisRound already makes on the live grid.
        //
        // A target this attack can't meaningfully hurt gets left out here (unlike
        // TryChooseAttackTarget's own last-resort fallback) — a deterministic expected-value
        // playout has no dice variance to hope for, so "attacking" for a modeled 0 damage is
        // strictly worse than spending the round advancing instead.
        public static bool TryFindBestReachableTarget(BattleGrid grid, Dictionary<UnitData, float> hp, UnitData actor,
            int actorRow, int actorCol, AbilityMagnitudes magnitudes, List<UnitData> order, int currentIndex,
            out UnitData bestTarget, out float bestDamage, ArmyData battleDefender = null,
            int battleDefenderDefenseBonus = 0, Dictionary<UnitData, int> simulatedAttack = null,
            Dictionary<UnitData, int> simulatedDefense = null)
        {
            bestTarget = null;
            bestDamage = 0f;
            float bestScore = float.NegativeInfinity;
            foreach (UnitData candidate in grid.AllUnits())
            {
                if (candidate.Owner == actor.Owner
                    || !hp.TryGetValue(candidate, out float candidateHp) || candidateHp <= 0f)
                    continue;
                if (!grid.TryFindPosition(candidate, out int candRow, out int candCol)
                    || !BattleGrid.IsInRange(actorRow, actorCol, candRow, candCol, actor.Range))
                    continue;

                bool notYetActed = order != null && order.IndexOf(candidate) > currentIndex;
                int defenderBonus = BattleProtectionRules.GetTotalDefenseBonus(
                    grid, candidate, battleDefender, battleDefenderDefenseBonus);
                int? actorAttackOverride = simulatedAttack != null && simulatedAttack.TryGetValue(actor, out int simActorAttack)
                    ? simActorAttack : (int?)null;
                int? candidateDefenseOverride = simulatedDefense != null && simulatedDefense.TryGetValue(candidate, out int simCandidateDefense)
                    ? simCandidateDefense : (int?)null;
                int? candidateAttackOverride = simulatedAttack != null && simulatedAttack.TryGetValue(candidate, out int simCandidateAttack)
                    ? simCandidateAttack : (int?)null;
                if (!TryScoreTarget(actor, candidate, candidateHp, magnitudes, notYetActed,
                    out float score, out float damage, out _, defenderBonus,
                    actorAttackOverride, candidateDefenseOverride, candidateAttackOverride))
                    continue;

                score += SplashSpreadBonus(grid, actor, candRow, candCol);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDamage = damage;
                    bestTarget = candidate;
                }
            }
            return bestTarget != null;
        }

        // turnOrder/turnIndex: BattleScreenUI's own live _turnOrder/_turnIndex fields (see
        // BattleScreenUI.Combat.cs's SkipRemainingTurnThisRound, which already does the identical
        // IndexOf(...) > _turnIndex check for the same reason) — passed through from
        // BattleAi.ChooseAction, which has no turn-order concept of its own.
        public static bool TryChooseAttackTarget(BattleGrid grid, UnitData actor, int actorRow, int actorCol,
            AbilityMagnitudes magnitudes, List<UnitData> turnOrder, int turnIndex, out BattleAi.AiAction action,
            ArmyData battleDefender = null, int battleDefenderDefenseBonus = 0)
        {
            UnitData bestTarget = null;
            int bestRow = -1, bestCol = -1;
            float bestScore = float.NegativeInfinity;
            AiThoughtCategory bestReason = AiThoughtCategory.PriorityTarget;

            foreach (UnitData candidate in grid.AllUnits())
            {
                if (candidate.Owner == actor.Owner)
                {
                    BattleDebugLog.Write($"[TargetDiag] skip {candidate.Name}: same owner as actor {actor.Name}");
                    continue;
                }
                if (!grid.TryFindPosition(candidate, out int candRow, out int candCol))
                {
                    BattleDebugLog.Write($"[TargetDiag] skip {candidate.Name}: not found on grid");
                    continue;
                }
                if (!BattleGrid.IsInRange(actorRow, actorCol, candRow, candCol, actor.Range))
                {
                    BattleDebugLog.Write($"[TargetDiag] skip {candidate.Name}: out of range " +
                        $"(actor=({actorRow},{actorCol}) range={actor.Range}, target=({candRow},{candCol}))");
                    continue;
                }

                bool notYetActed = turnOrder != null && turnOrder.IndexOf(candidate) > turnIndex;
                int defenderBonus = battleDefender != null && battleDefender.Members.Contains(candidate)
                    ? battleDefenderDefenseBonus : 0;
                defenderBonus += BattleProtectionRules.GetGuardedDefenseBonus(grid, candidate, battleDefender);
                TryScoreTarget(actor, candidate, candidate.HitPointsCurrent, magnitudes, notYetActed,
                    out float score, out float damage, out AiThoughtCategory reason, defenderBonus);
                score += SplashSpreadBonus(grid, actor, candRow, candCol);

                BattleDebugLog.Write($"[TargetDiag] candidate {candidate.Name}: hp={candidate.HitPointsCurrent} " +
                    $"defense={candidate.Defense} ceramicArmor={candidate.HasAbility(UnitAbilities.CeramicArmor)} " +
                    $"actorAttack={actor.Attack} damage={damage} score={score} reason={reason} notYetActed={notYetActed}");

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = candidate;
                    bestRow = candRow;
                    bestCol = candCol;
                    bestReason = reason;
                }
            }

            if (bestTarget == null)
            {
                action = default;
                return false;
            }

            BattleDebugLog.Write($"[TargetDiag] actor {actor.Name} (attack={actor.Attack}) chose {bestTarget.Name} score={bestScore}");
            action = new BattleAi.AiAction { Kind = BattleAi.AiActionKind.Attack, Target = bestTarget, Row = bestRow, Col = bestCol, Reason = bestReason };
            return true;
        }
    }
}
