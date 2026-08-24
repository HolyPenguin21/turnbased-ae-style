using System.Collections.Generic;
using Game.Cards;
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
            out float score, out int damage, out AiThoughtCategory reason)
        {
            // Round half UP (0.5 -> 1), not Mathf.RoundToInt's banker's rounding — with an odd
            // Attack/even Defense pairing this base expected-damage step lands on exactly x.5, and
            // banker's rounding was quietly flooring a genuine 1-damage hit down to 0.
            int rawExpected = Mathf.FloorToInt(actor.Attack * 0.5f - candidate.Defense * 0.5f + 0.5f);
            if (rawExpected > 0)
                damage = ChallengeResult.ApplyAbilityModifiers(rawExpected, actor, candidate, magnitudes);
            else
                damage = 0; // modifiers can't turn a non-positive base into a hit — every step in
                            // ApplyAbilityModifiers is itself gated on damage > 0, so there's
                            // nothing to compute.

            if (damage <= 0)
            {
                // Can't penetrate this one's Defense — only ever picked if literally nothing
                // better is in range at all (see each caller).
                score = -1000f + candidate.Attack;
                reason = AiThoughtCategory.UselessTargetSkip;
                return false;
            }

            if (candidateHp > 0f && damage >= candidateHp)
            {
                // Among multiple finishable targets, prefer the cheapest kill.
                score = 10000f - candidateHp;
                reason = AiThoughtCategory.FinishingBlow;
            }
            else
            {
                // damage dominates (x10) — how much actually lands is the real measure of
                // efficiency; candidate.Attack only nudges the choice (x0.5) when two targets are
                // roughly equally easy to hurt.
                score = 100f + damage * 10f + candidate.Attack * 0.5f;
                reason = AiThoughtCategory.PriorityTarget;
            }

            // ShockAttack: a small nudge toward knocking a still-to-act enemy out of the round,
            // rather than one that's already spent its turn and has nothing left to lose from it.
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
            out UnitData bestTarget, out float bestDamage)
        {
            bestTarget = null;
            bestDamage = 0f;
            float bestScore = float.NegativeInfinity;
            foreach (UnitData candidate in grid.AllUnits())
            {
                if (candidate.Owner == actor.Owner || !hp.TryGetValue(candidate, out float candidateHp) || candidateHp <= 0f)
                    continue;
                if (!grid.TryFindPosition(candidate, out int candRow, out int candCol)
                    || !BattleGrid.IsInRange(actorRow, actorCol, candRow, candCol, actor.Range))
                    continue;

                bool notYetActed = order != null && order.IndexOf(candidate) > currentIndex;
                if (!TryScoreTarget(actor, candidate, candidateHp, magnitudes, notYetActed,
                    out float score, out int damage, out _))
                    continue;

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
            AbilityMagnitudes magnitudes, List<UnitData> turnOrder, int turnIndex, out BattleAi.AiAction action)
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
                TryScoreTarget(actor, candidate, candidate.HitPointsCurrent, magnitudes, notYetActed,
                    out float score, out int damage, out AiThoughtCategory reason);

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
