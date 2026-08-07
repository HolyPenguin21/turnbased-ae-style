using System.Collections.Generic;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // Pure decision logic for the AI-controlled side of a Tactical Battle Module fight — same
    // stateless-static style as BattleTurnOrder/BattleInitiator, operating only on
    // BattleGrid/ArmyData/UnitData. BattleScreenUI owns all the MonoBehaviour/coroutine/UI
    // plumbing and calls into this for the actual decisions; execution of whatever this returns
    // (PerformMove/BeginAttack/OnPassClicked) is 100% shared with the human player's own path —
    // this class never touches the grid or army data directly, only reads it and reports back
    // what it would do.
    //
    // Every "why" a decision was made maps to an AiThoughtCategory so the caller can drive
    // AiЕhoughts_Text without re-deriving the reasoning — see BattleAiThoughtsUI.
    public static class BattleAi
    {
        // ---- Arrangement (see the user's own spec: range-aware, not the generic sequential
        // fill BattleGrid.FromArmies uses by default) ----

        // Replaces whatever FromArmies's generic default already placed for this army: Range==1
        // members go to the front row (left to right), Range>=2 to the back row, hero to the
        // standard reserved slot (BattleGrid.HeroColumn). Computed from this army's own roster —
        // it never looks at the opposing side's PLACEMENT (same restriction the human's own
        // Arrangement phase has, and unknowable anyway — both sides arrange independently at the
        // same time). It DOES read the opposing side's own current STATS via `enemyArmy` (see
        // the user's own spec — this is public information either side could already look up by
        // hand via HexSelectionController.TryHandleEnemyArmyMarkerClick's read-only army viewer,
        // so it isn't an unfair advantage), specifically to judge whether the back row is
        // actually a safe haven this round — see backRowExposed below. No literal stat
        // thresholds are hardcoded anywhere here; every number comes from the grid's own fixed
        // row geometry (see BattleGrid's own comment on row distances) compared against whatever
        // Range values the enemy roster actually has right now, so this keeps working correctly
        // regardless of future balance changes.
        public static void ArrangeArmy(BattleGrid grid, ArmyData army, int frontRow, int backRow, ArmyData enemyArmy = null)
        {
            if (grid == null || army == null)
                return;

            foreach (UnitData member in army.Members)
                if (grid.TryFindPosition(member, out int row, out int col))
                    grid.Set(row, col, null);

            UnitData hero = null;
            var melee = new List<UnitData>();
            var ranged = new List<UnitData>();
            foreach (UnitData member in army.Members)
            {
                if (member.IsHero) { hero = member; continue; }
                if (member.Range <= 1) melee.Add(member);
                else ranged.Add(member);
            }

            // The enemy's own strongest non-hero Range, read live — worst case, we don't know
            // which of ITS two rows that unit ends up in, so distance is measured from whichever
            // of the enemy's rows is CLOSER to our back row (the more dangerous assumption for
            // us). If even that can't reach, the back row is a genuine refuge this round and the
            // plain Range-based split above is already the right call. If it CAN reach, nothing
            // here is truly safe (the front row is always at least as reachable as the back), so
            // the back row's own occupants are instead chosen by who's actually worth putting in
            // the comparatively-less-exposed slot: highest Defense+HP first, not army-list order.
            int enemyMaxRange = 0;
            if (enemyArmy != null)
                foreach (UnitData enemyMember in enemyArmy.Members)
                    if (!enemyMember.IsHero && enemyMember.Range > enemyMaxRange)
                        enemyMaxRange = enemyMember.Range;

            bool weAreAttackerSide = frontRow == BattleGrid.AttackerFrontRow;
            int enemyFrontRow = weAreAttackerSide ? BattleGrid.DefenderFrontRow : BattleGrid.AttackerFrontRow;
            int enemyBackRow = weAreAttackerSide ? BattleGrid.DefenderBackRow : BattleGrid.AttackerBackRow;
            int closestEnemyReachToOurBack = Mathf.Min(Mathf.Abs(enemyFrontRow - backRow), Mathf.Abs(enemyBackRow - backRow));
            bool backRowExposed = enemyMaxRange >= closestEnemyReachToOurBack;
            if (backRowExposed)
                ranged.Sort((a, b) => (b.Defense + b.HitPointsMax).CompareTo(a.Defense + a.HitPointsMax));

            if (hero != null)
                grid.Set(backRow, BattleGrid.HeroColumn, hero);

            int frontCol = 0;
            int backCol = BattleGrid.HeroColumn + 1;
            var overflow = new List<UnitData>();

            foreach (UnitData member in melee)
            {
                if (frontCol < BattleGrid.Columns) grid.Set(frontRow, frontCol++, member);
                else overflow.Add(member);
            }
            foreach (UnitData member in ranged)
            {
                if (backCol < BattleGrid.Columns) grid.Set(backRow, backCol++, member);
                else overflow.Add(member);
            }
            foreach (UnitData member in overflow)
            {
                if (frontCol < BattleGrid.Columns) grid.Set(frontRow, frontCol++, member);
                else if (backCol < BattleGrid.Columns) grid.Set(backRow, backCol++, member);
                // Beyond that there's nowhere left — not reachable given ArmyData.Capacity's cap.
            }
        }

        // ---- Round-start retreat/fight assessment ----

        public struct RetreatAssessment
        {
            public bool ShouldRetreat;
            public bool IsCitadelDefense;
        }

        // 1-round and 3-round expected-damage projection (aggregate, not a unit-by-unit
        // simulation — see the plan's own note on this simplification), used ONLY for this
        // fight/retreat call, never for in-round target coordination (that emerges on its own
        // from ChooseAction's fresh-each-turn finishing-blow priority). defendingOwnCitadel
        // short-circuits straight to "never retreat" regardless of the numbers.
        public static RetreatAssessment AssessRetreat(ArmyData aiArmy, ArmyData enemyArmy, bool defendingOwnCitadel)
        {
            if (defendingOwnCitadel)
                return new RetreatAssessment { ShouldRetreat = false, IsCitadelDefense = true };

            float ownHp = TotalHp(aiArmy);
            float enemyHp = TotalHp(enemyArmy);
            float ownPerRound = ExpectedDamagePerRound(aiArmy, enemyArmy);
            float enemyPerRound = ExpectedDamagePerRound(enemyArmy, aiArmy);

            float ownRemaining = ownHp;
            float enemyRemaining = enemyHp;
            for (int round = 0; round < 3; round++)
            {
                ownRemaining -= enemyPerRound;
                enemyRemaining -= ownPerRound;
                if (ownRemaining <= 0f || enemyRemaining <= 0f)
                    break;
            }

            float ownLossFraction = ownHp > 0f ? Mathf.Clamp01((ownHp - Mathf.Max(0f, ownRemaining)) / ownHp) : 1f;
            float enemyLossFraction = enemyHp > 0f ? Mathf.Clamp01((enemyHp - Mathf.Max(0f, enemyRemaining)) / enemyHp) : 1f;

            // No clear projected advantage (we'd lose proportionally more than we deal out) —
            // retreat and preserve the army, per the user's own "just a skirmish" framing.
            bool shouldRetreat = ownLossFraction > enemyLossFraction;
            return new RetreatAssessment { ShouldRetreat = shouldRetreat, IsCitadelDefense = false };
        }

        private static float ExpectedDamagePerRound(ArmyData attackers, ArmyData defenders)
        {
            if (attackers == null || defenders == null || attackers.Members.Count == 0 || defenders.Members.Count == 0)
                return 0f;

            float avgDefense = AverageDefense(defenders);
            float total = 0f;
            foreach (UnitData member in attackers.Members)
            {
                if (member.IsHero)
                    continue; // heroes never attack
                float expected = member.Attack * 0.5f - avgDefense * 0.5f;
                if (expected > 0f)
                    total += expected;
            }
            return total;
        }

        private static float AverageDefense(ArmyData army)
        {
            int count = 0;
            float sum = 0f;
            foreach (UnitData member in army.Members)
            {
                if (member.IsHero)
                    continue;
                sum += member.Defense;
                count++;
            }
            return count > 0 ? sum / count : 0f;
        }

        private static float TotalHp(ArmyData army)
        {
            float total = 0f;
            if (army == null)
                return total;
            foreach (UnitData member in army.Members)
                if (!member.IsHero)
                    total += member.HitPointsCurrent;
            return total;
        }

        // ---- Per-unit tactical decision ----

        public enum AiActionKind { Move, Attack, Pass }

        public struct AiAction
        {
            public AiActionKind Kind;
            public int Row;
            public int Col;
            public UnitData Target;
            public AiThoughtCategory Reason;
        }

        // After this many consecutive "waited instead of advancing" turns, the next one advances
        // regardless of exposure risk — per the user's own anti-stalling spec.
        private const int MaxWaitStreak = 3;

        public static AiAction ChooseAction(BattleGrid grid, UnitData actor, Dictionary<UnitData, int> waitStreak)
        {
            var passAction = new AiAction { Kind = AiActionKind.Pass, Reason = AiThoughtCategory.CautiousWait };
            if (grid == null || actor == null || waitStreak == null
                || !grid.TryFindPosition(actor, out int actorRow, out int actorCol))
                return passAction;

            if (TryChooseAttackTarget(grid, actor, actorRow, actorCol, out AiAction attackAction))
            {
                waitStreak[actor] = 0;
                return attackAction;
            }

            bool actorIsAttackerSide = BattleGrid.IsAttackerSideRow(actorRow);
            bool alreadyExposed = IsExposedToEnemy(grid, actorRow, actorCol, actor);
            (int row, int col)? step = FindStepToward(grid, actor, actorRow, actorCol, actorIsAttackerSide);

            if (step == null)
            {
                waitStreak[actor] = 0;
                return passAction;
            }

            bool stepExposes = !alreadyExposed && IsExposedToEnemy(grid, step.Value.row, step.Value.col, actor);
            int streak = waitStreak.TryGetValue(actor, out int s) ? s : 0;
            bool forceAdvance = streak >= MaxWaitStreak;

            if (alreadyExposed || !stepExposes || forceAdvance)
            {
                waitStreak[actor] = 0;
                return new AiAction
                {
                    Kind = AiActionKind.Move,
                    Row = step.Value.row,
                    Col = step.Value.col,
                    Reason = forceAdvance ? AiThoughtCategory.ForcedAdvance : AiThoughtCategory.AdvanceMove,
                };
            }

            waitStreak[actor] = streak + 1;
            return passAction;
        }

        // Target priority, highest to lowest: finishing blow > highest-threat > (skip if our
        // expected damage against them is ~0, prefer any other legal target instead).
        private static bool TryChooseAttackTarget(BattleGrid grid, UnitData actor, int actorRow, int actorCol, out AiAction action)
        {
            UnitData bestTarget = null;
            int bestRow = -1, bestCol = -1;
            float bestScore = float.NegativeInfinity;
            AiThoughtCategory bestReason = AiThoughtCategory.PriorityTarget;

            foreach (UnitData candidate in grid.AllUnits())
            {
                if (candidate.IsHero || candidate.Owner == actor.Owner)
                    continue;
                if (!grid.TryFindPosition(candidate, out int candRow, out int candCol))
                    continue;
                if (!BattleGrid.IsInRange(actorRow, actorCol, candRow, candCol, actor.Range))
                    continue;

                float expectedDamage = Mathf.Max(0f, actor.Attack * 0.5f - candidate.Defense * 0.5f);
                float score;
                AiThoughtCategory reason;
                if (candidate.HitPointsCurrent > 0 && expectedDamage >= candidate.HitPointsCurrent)
                {
                    // Among multiple finishable targets, prefer the cheapest kill.
                    score = 10000f - candidate.HitPointsCurrent;
                    reason = AiThoughtCategory.FinishingBlow;
                }
                else if (expectedDamage <= 0.01f)
                {
                    // Can't penetrate this one's Defense — only picked if literally nothing
                    // better is in range at all.
                    score = -1000f + candidate.Attack;
                    reason = AiThoughtCategory.UselessTargetSkip;
                }
                else
                {
                    score = 100f + candidate.Attack;
                    reason = AiThoughtCategory.PriorityTarget;
                }

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

            action = new AiAction { Kind = AiActionKind.Attack, Target = bestTarget, Row = bestRow, Col = bestCol, Reason = bestReason };
            return true;
        }

        // True if any enemy unit currently on the grid could attack `row`/`col` from where it
        // stands right now.
        private static bool IsExposedToEnemy(BattleGrid grid, int row, int col, UnitData actor)
        {
            foreach (UnitData candidate in grid.AllUnits())
            {
                if (candidate.IsHero || candidate.Owner == actor.Owner)
                    continue;
                if (!grid.TryFindPosition(candidate, out int candRow, out int candCol))
                    continue;
                if (BattleGrid.IsInRange(candRow, candCol, row, col, candidate.Range))
                    return true;
            }
            return false;
        }

        // A single greedy step (orthogonal, own-side/neutral-row only — same legality
        // BattleScreenUI.IsAdjacentOwnSide enforces for the human) toward whichever enemy
        // non-hero unit is currently closest. Null if there's nowhere legal to go.
        private static (int row, int col)? FindStepToward(BattleGrid grid, UnitData actor, int actorRow, int actorCol, bool actorIsAttackerSide)
        {
            UnitData nearestEnemy = null;
            int nearestRow = -1, nearestCol = -1;
            int nearestDist = int.MaxValue;
            foreach (UnitData candidate in grid.AllUnits())
            {
                if (candidate.IsHero || candidate.Owner == actor.Owner)
                    continue;
                if (!grid.TryFindPosition(candidate, out int candRow, out int candCol))
                    continue;
                int dist = Mathf.Abs(actorRow - candRow) + Mathf.Abs(actorCol - candCol);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestEnemy = candidate;
                    nearestRow = candRow;
                    nearestCol = candCol;
                }
            }
            if (nearestEnemy == null)
                return null;

            (int row, int col)? best = null;
            int bestDist = int.MaxValue;
            int[] dRows = { -1, 1, 0, 0 };
            int[] dCols = { 0, 0, -1, 1 };
            for (int i = 0; i < 4; i++)
            {
                int row = actorRow + dRows[i];
                int col = actorCol + dCols[i];
                if (!BattleGrid.InBounds(row, col) || grid.Get(row, col) != null)
                    continue;
                bool sameSideOk = actorIsAttackerSide
                    ? (BattleGrid.IsAttackerSideRow(row) || row == BattleGrid.NeutralRow)
                    : (BattleGrid.IsDefenderSideRow(row) || row == BattleGrid.NeutralRow);
                if (!sameSideOk)
                    continue;

                int dist = Mathf.Abs(row - nearestRow) + Mathf.Abs(col - nearestCol);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = (row, col);
                }
            }
            return best;
        }

        // ---- Defender's/Attacker's Prerogative Fate spend (BattleAttackPopupUI) ----

        // isDefender: true when evaluating the DEFENDER's own spend (wants damage reduced to 0),
        // false for the ATTACKER's (wants damage to actually land) — see the user's own spec:
        // spend as many times as it takes as long as each one still matters, never spend once the
        // exchange is already settled in this side's favor.
        public static bool ShouldSpendFate(bool[] attackerDice, bool[] defenderDice, int fateAvailable, bool isDefender)
        {
            if (fateAvailable <= 0)
                return false;
            bool[] ownDice = isDefender ? defenderDice : attackerDice;
            if (ownDice == null || !HasMiss(ownDice))
                return false;

            int damage = new ChallengeResult(attackerDice, defenderDice).Damage;
            return isDefender ? damage > 0 : damage <= 0;
        }

        private static bool HasMiss(bool[] dice)
        {
            if (dice == null)
                return false;
            foreach (bool hit in dice)
                if (!hit)
                    return true;
            return false;
        }
    }
}
