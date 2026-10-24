using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // Tactical decisions and deployment search. BattleEngine executes live actions; projections
    // work on cloned grids/HP. ArrangeArmy commits only the chosen initial positions.
    public static class BattleAi
    {
        // Deployment is scored against two plausible enemy layouts, never the enemy's
        // actual hidden positions. Near-equal outcomes prefer earlier participation, then
        // more front-line combatants and a central anchor. Movement in every projection uses
        // the same firing-position search as live ChooseAction.
        private const float ScoreTieTolerance = 0.015f;

        private enum FormationPattern { LeftPacked, CenterOut, EdgesIn, Staggered }

        public static void ArrangeArmy(BattleGrid grid, ArmyData army, int frontRow, int backRow, ArmyData enemyArmy,
            AbilityMagnitudes magnitudes, ArmyData battleDefender = null, int battleDefenderDefenseBonus = 0)
        {
            if (grid == null || army == null)
                return;

            foreach (UnitData member in army.Members)
                if (grid.TryFindPosition(member, out int row, out int col))
                    grid.Set(row, col, null);

            int combatantCount = CountDeploymentCombatants(army);

            if (enemyArmy == null)
            {
                // No opposing roster to weigh candidates against (shouldn't happen in a real
                // battle) — keep the usual short-range front and long-range rear as a fallback.
                PlaceTankAnchoredSplit(grid, army, frontRow, backRow, FormationPattern.CenterOut,
                    enemyArmy: null, frontCount: army.Members.Count(u => u.IsGroundCombatant && u.Range <= 2));
                return;
            }

            bool weAreAttackerSide = frontRow == BattleGrid.AttackerFrontRow;
            int enemyFrontRow = weAreAttackerSide ? BattleGrid.DefenderFrontRow : BattleGrid.AttackerFrontRow;
            int enemyBackRow = weAreAttackerSide ? BattleGrid.DefenderBackRow : BattleGrid.AttackerBackRow;

            float ownTotalHp = TotalHp(army);
            float enemyTotalHp = TotalHp(enemyArmy);

            FormationPattern[] formationCandidates =
            {
                FormationPattern.LeftPacked,
                FormationPattern.CenterOut,
                FormationPattern.EdgesIn,
                FormationPattern.Staggered,
            };
            (float score, int moves) Evaluate(FormationPattern pattern, int frontCount, bool expected)
            {
                float score = 0f;
                int moves = 0;
                for (int opponentLayout = 0; opponentLayout < 2; opponentLayout++)
                {
                    var trial = new BattleGrid();
                    PlaceTankAnchoredSplit(trial, army, frontRow, backRow, pattern, enemyArmy, frontCount);
                    if (opponentLayout == 0)
                        PlaceRangeSplit(trial, enemyArmy, enemyFrontRow, enemyBackRow);
                    else
                        PlaceTankAnchoredSplit(trial, enemyArmy, enemyFrontRow, enemyBackRow,
                            FormationPattern.CenterOut, army, CountDeploymentCombatants(enemyArmy));
                    moves += MovesToParticipate(trial, army, magnitudes);
                    SimulationOutcome outcome = SimulateRounds(trial, army, enemyArmy, 3, magnitudes,
                        battleDefender, battleDefenderDefenseBonus,
                        expected ? BattleSimulationTrials : ArrangementSimulationTrials,
                        deterministic: expected, holdingArmy: expected ? enemyArmy : null);
                    score += -LossMarginAgainstUs(ownTotalHp, enemyTotalHp, outcome) / 2f;
                }
                return (score, moves);
            }

            var candidates = new List<(FormationPattern pattern, int frontCount, float score, int moves)>();
            int maxFrontCount = Mathf.Min(BattleGrid.Columns, combatantCount);
            int heroCount = army.Members.Count(u => u.IsHero);
            int minFrontCount = Mathf.Min(maxFrontCount,
                Mathf.Max(0, combatantCount - Mathf.Max(0, BattleGrid.Columns - heroCount)));
            if (army.Members.Any(u => u.IsGroundCombatant && u.Range <= 2))
                minFrontCount = Mathf.Max(minFrontCount, 1);
            for (int frontCount = minFrontCount; frontCount <= maxFrontCount; frontCount++)
                foreach (FormationPattern pattern in formationCandidates)
                {
                    var outcome = Evaluate(pattern, frontCount, expected: false);
                    candidates.Add((pattern, frontCount, outcome.score, outcome.moves));
                }

            // Holding a shooter out of opening range must beat a ready line against an enemy
            // that holds its ground as well. Average equal-initiative orders, not just tokens.
            int earliestParticipation = candidates.Min(c => c.moves);
            var ready = candidates.Where(c => c.moves == earliestParticipation).ToList();
            float readyScore = ready.Max(c => c.score);
            var eligible = ready.ToList();
            // Candidates below the best ready score's tie window cannot be selected. Avoid
            // the additional expected-damage projection for those already-eliminated layouts.
            var delayed = candidates.Where(c => c.moves > earliestParticipation
                && c.score >= readyScore - ScoreTieTolerance).ToList();
            if (delayed.Count > 0)
            {
                float readyExpected = ready.Max(c => Evaluate(c.pattern, c.frontCount, expected: true).score);
                foreach (var candidate in delayed)
                    if (Evaluate(candidate.pattern, candidate.frontCount, expected: true).score
                        > readyExpected + ScoreTieTolerance)
                        eligible.Add(candidate);
            }
            float bestScore = eligible.Max(c => c.score);

            // Select only after finding the true maximum. A later, tiny score improvement must
            // not undo the participation tie-break or make a sequence of near-ties drift.
            var best = eligible.Where(c => c.score >= bestScore - ScoreTieTolerance)
                .OrderBy(c => c.moves)
                .ThenByDescending(c => c.frontCount)
                .ThenBy(c => c.pattern == FormationPattern.CenterOut ? 0 : 1)
                .First();
            BattleDebugLog.Write($"[ArrangeDiag] army \"{army.Name}\": " +
                $"frontlineCount={combatantCount} -> chosen pattern={best.pattern} frontCount={best.frontCount} " +
                $"score={best.score} bestScore={bestScore} movesToParticipate={best.moves}");
            PlaceTankAnchoredSplit(grid, army, frontRow, backRow, best.pattern, enemyArmy, best.frontCount);
        }

        private static int MovesToParticipate(BattleGrid grid, ArmyData army, AbilityMagnitudes magnitudes)
        {
            int moves = 0;
            foreach (UnitData member in army.Members)
                if (member.IsGroundCombatant && member.HitPointsCurrent > 0 && !IsRearSupport(member, army)
                    && grid.TryFindPosition(member, out int row, out int col))
                    moves += FiringPositionDistance(grid, member, row, col, magnitudes);
            return moves;
        }

        private static int CountDeploymentCombatants(ArmyData army)
            => army.Members.Count(u => u.IsGroundCombatant);

        // Healing is army-wide. A healer whose attack is below its soldiers' is a rear
        // support candidate, even when Defense+HP alone would sort it ahead of those soldiers.
        private static bool IsRearSupport(UnitData unit, ArmyData army)
            => unit.HasAbility(UnitAbilities.Heal) && army.Members.Any(other => other != unit
                && other.IsGroundCombatant && other.HitPointsCurrent > 0 && other.Attack > unit.Attack);

        // Rough "how good a soak is this unit" score — Defense+HP is the base survivability, but
        // CeramicArmor's flat -1 to every hit and Berserk's own "getting hit makes it stronger"
        // trade both make a unit a better front-liner than raw stats alone suggest, so both get
        // folded in as a flat bonus rather than left for raw stats to under-rate them. Bodyguard
        // (covers its orthogonal neighbours) and Counterattack (answers melee attackers) also
        // want to stand where the enemy arrives first.
        private static float TankScore(UnitData unit)
        {
            float score = unit.Defense + Mathf.Max(0, unit.HitPointsCurrent);
            if (unit.HasAbility(UnitAbilities.CeramicArmor)) score += 2f;
            if (unit.HasAbility(UnitAbilities.Berserk)) score += 2f;
            if (unit.HasAbility(UnitAbilities.Bodyguard)) score += 2f;
            if (unit.HasAbility(UnitAbilities.Counterattack)) score += 1f;
            return score;
        }

        // The first column anchors the front line and its commander. Each pattern is searched.
        private static List<int> ColumnFillOrder(FormationPattern pattern)
        {
            switch (pattern)
            {
                case FormationPattern.CenterOut:
                    return new List<int> { 2, 3, 1, 4, 0 };
                case FormationPattern.EdgesIn:
                    return new List<int> { 0, 4, 1, 3, 2 };
                case FormationPattern.Staggered:
                    return new List<int> { 1, 3, 2, 0, 4 };
                default:
                    return new List<int> { 0, 1, 2, 3, 4 };
            }
        }

        // Same "is the back row actually a safe haven this round" read ArrangeArmy always did —
        // extracted so both PlaceTankAnchoredSplit (real placement) and the hypothetical shapes
        // built for scoring can share it.
        private static bool BackRowExposed(int frontRow, int backRow, ArmyData enemyArmy)
        {
            int enemyMaxRange = 0;
            if (enemyArmy != null)
                foreach (UnitData enemyMember in enemyArmy.Members)
                    if (enemyMember.IsGroundCombatant && enemyMember.Range > enemyMaxRange)
                        enemyMaxRange = enemyMember.Range;

            bool weAreAttackerSide = frontRow == BattleGrid.AttackerFrontRow;
            int enemyFrontRow = weAreAttackerSide ? BattleGrid.DefenderFrontRow : BattleGrid.AttackerFrontRow;
            int enemyBackRow = weAreAttackerSide ? BattleGrid.DefenderBackRow : BattleGrid.AttackerBackRow;
            int closestEnemyReachToOurBack = Mathf.Min(Mathf.Abs(enemyFrontRow - backRow), Mathf.Abs(enemyBackRow - backRow));
            return enemyMaxRange >= closestEnemyReachToOurBack;
        }

        private static void SetDeploymentUnit(BattleGrid grid, UnitData unit, int row, int col,
            int frontRow, int backRow)
        {
            if (BattleRules.CanPlace(unit, row, col, frontRow, backRow))
                grid.Set(row, col, unit);
        }

        // Every combatant may occupy either legal row. Short-range fighters anchor the
        // front before artillery; weak healers are protected behind them. The search decides
        // how many to expose, so a pure artillery roster can also start in firing range.
        private static void PlaceTankAnchoredSplit(BattleGrid grid, ArmyData army, int frontRow, int backRow,
            FormationPattern pattern, ArmyData enemyArmy, int frontCount)
        {
            var heroes = army.Members.Where(u => u.IsHero).ToList();
            var combatants = army.Members.Where(u => u.IsGroundCombatant)
                .OrderBy(u => IsRearSupport(u, army) ? 2 : u.Range <= 2 ? 0 : 1)
                .ThenByDescending(TankScore).ToList();
            UnitData commander = army.Commander;
            if (commander != null && heroes.Remove(commander)) heroes.Insert(0, commander);
            int toFront = Mathf.Clamp(frontCount, 0, Mathf.Min(BattleGrid.Columns, combatants.Count));
            List<UnitData> frontMelee = combatants.GetRange(0, toFront);
            List<UnitData> heldBackMelee = combatants.GetRange(toFront, combatants.Count - toFront);

            List<int> frontOrder = ColumnFillOrder(pattern);
            int primaryHeroColumn = frontMelee.Count > 0 ? frontOrder[0] : BattleGrid.HeroColumn;

            int frontIndex = 0;
            var overflow = new List<UnitData>();
            foreach (UnitData member in frontMelee)
            {
                if (frontIndex < frontOrder.Count) SetDeploymentUnit(grid, member, frontRow, frontOrder[frontIndex++], frontRow, backRow);
                else overflow.Add(member);
            }

            var heroColumns = new List<int>();
            if (heroes.Count > 0)
            {
                heroColumns.Add(primaryHeroColumn);
                foreach (int col in frontOrder)
                    if (col != primaryHeroColumn)
                        heroColumns.Add(col);
            }

            int placedHeroes = Mathf.Min(heroes.Count, heroColumns.Count);
            var occupiedHeroColumns = new HashSet<int>();
            for (int i = 0; i < placedHeroes; i++)
            {
                SetDeploymentUnit(grid, heroes[i], backRow, heroColumns[i], frontRow, backRow);
                occupiedHeroColumns.Add(heroColumns[i]);
            }

            var backColumns = new List<int>();
            for (int c = 0; c < BattleGrid.Columns; c++)
                if (!occupiedHeroColumns.Contains(c))
                    backColumns.Add(c);

            // A roster can field more ranged-plus-held-back members than the back row has room
            // for (a high-CommandRating hero fielding a mostly-ranged army — see
            // ArmyData.Capacity — or ArrangeArmy's own search choosing to hold back more melee
            // than the back row can fit). Whoever doesn't fit gets pushed into `overflow` below
            // and ends up exposed in the FRONT row, so it needs to be the TANKIEST of the bunch,
            // not whoever a plain tankiest-first fill happens to leave over (that used to bump
            // the squishiest ranged unit to the front instead of the toughest — see the user's
            // own report). Protecting the weakest ones in the back row first, then letting the
            // tankiest remainder overflow, fixes that regardless of which side is arranging —
            // held-back melee compete for that same protection by TankScore exactly like ranged.
            var backPool = heldBackMelee;
            List<UnitData> forBack = backPool;
            if (backPool.Count > backColumns.Count)
            {
                backPool.Sort((a, b) => TankScore(a).CompareTo(TankScore(b)));
                forBack = backPool.GetRange(0, backColumns.Count);
                overflow.AddRange(backPool.GetRange(backColumns.Count, backPool.Count - backColumns.Count));
            }
            if (BackRowExposed(frontRow, backRow, enemyArmy))
                forBack.Sort((a, b) => TankScore(b).CompareTo(TankScore(a)));

            int backIndex = 0;
            foreach (UnitData member in forBack)
                SetDeploymentUnit(grid, member, backRow, backColumns[backIndex++], frontRow, backRow);

            foreach (UnitData member in overflow)
            {
                if (frontIndex < frontOrder.Count)
                    SetDeploymentUnit(grid, member, frontRow, frontOrder[frontIndex++], frontRow, backRow);
                else if (backIndex < backColumns.Count)
                    SetDeploymentUnit(grid, member, backRow, backColumns[backIndex++], frontRow, backRow);
                // Beyond that there's nowhere left — not reachable given ArmyData.Capacity's cap.
            }
        }

        // One plausible opponent layout: short-range front, long-range rear, commander first.
        // The second model uses a full anchored line. Neither reads the real hidden deployment.
        private static void PlaceRangeSplit(BattleGrid grid, ArmyData army, int frontRow, int backRow)
        {
            if (army == null)
                return;

            var heroes = new List<UnitData>();
            var melee = new List<UnitData>();
            var ranged = new List<UnitData>();
            foreach (UnitData member in army.Members)
            {
                if (member.IsHero) { heroes.Add(member); continue; }
                if (member.Range <= 2) melee.Add(member);
                else ranged.Add(member);
            }
            UnitData commander = army.Commander;
            if (commander != null && heroes.Remove(commander))
                heroes.Insert(0, commander);

            int backCol = BattleGrid.HeroColumn;
            foreach (UnitData hero in heroes)
            {
                if (backCol >= BattleGrid.Columns)
                    break;
                SetDeploymentUnit(grid, hero, backRow, backCol++, frontRow, backRow);
            }

            int frontCol = 0;
            var overflow = new List<UnitData>();

            foreach (UnitData member in melee)
            {
                if (frontCol < BattleGrid.Columns)
                    SetDeploymentUnit(grid, member, frontRow, frontCol++, frontRow, backRow);
                else overflow.Add(member);
            }
            foreach (UnitData member in ranged)
            {
                if (backCol < BattleGrid.Columns)
                    SetDeploymentUnit(grid, member, backRow, backCol++, frontRow, backRow);
                else overflow.Add(member);
            }
            foreach (UnitData member in overflow)
            {
                if (frontCol < BattleGrid.Columns)
                    SetDeploymentUnit(grid, member, frontRow, frontCol++, frontRow, backRow);
                else if (backCol < BattleGrid.Columns)
                    SetDeploymentUnit(grid, member, backRow, backCol++, frontRow, backRow);
                // Beyond that there's nowhere left — not reachable given ArmyData.Capacity's cap.
            }
        }

        // ---- Round-start retreat/fight assessment ----

        public struct RetreatAssessment
        {
            public bool ShouldRetreat;
            public bool IsCitadelDefense;
            // Army-level advantage hint retained for callers/narration. Movement no longer
            // needs this hint to override a separate exposure veto.
            public bool FavorableForAdvance;
        }

        // Margin of HP-loss-fraction disadvantage the AI must be projected to suffer before it
        // actually bails — per the user's own call: a razor-thin projected disadvantage (51%
        // vs 49%) shouldn't read the same as a genuine beating. Only a gap bigger than this
        // triggers a retreat; anything closer is "close enough to fight it out".
        private const float RetreatMarginFraction = 0.15f;

        // Existing advantage classification; it does not gate individual movement.
        private const float AdvanceMarginFraction = 0.08f;

        // Full grid-aware playouts, not a static per-round rate — see SimulateRounds. Used ONLY
        // for this fight/retreat call, never for in-round target coordination (that emerges on
        // its own from ChooseAction's fresh-each-turn finishing-blow priority). defendingOwnCitadel
        // short-circuits straight to "never retreat" regardless of the numbers.
        // magnitudes: same tunable ability magnitudes FateDuelAi/BattleTargetSelector already
        // take — see BattleAttackPopupUI's own Magnitudes property, so this call site and the
        // actual damage resolution can never disagree about what those abilities are worth.
        //
        // Checked at BOTH the 2-round and the 3-round mark, same RetreatMarginFraction on each —
        // per the user's own call, that margin already IS the "how much worse is too much"
        // coefficient, no separate one needed. A fight that's already a beating by round 2 counts
        // as a beating even if the raw 3-round aggregate looks closer on paper — kills inside
        // SimulateRounds are permanent for the rest of that same playout, so a bad round 2 rarely
        // "recovers" by round 3 the way a smoothed average might suggest.
        public static RetreatAssessment AssessRetreat(BattleGrid grid, ArmyData aiArmy, ArmyData enemyArmy, bool defendingOwnCitadel,
            AbilityMagnitudes magnitudes, ArmyData battleDefender = null, int battleDefenderDefenseBonus = 0)
        {
            if (defendingOwnCitadel)
                return new RetreatAssessment { ShouldRetreat = false, IsCitadelDefense = true };

            float ownHp = TotalHp(aiArmy);
            float enemyHp = TotalHp(enemyArmy);

            SimulationOutcome twoRound = SimulateRounds(grid, aiArmy, enemyArmy, 2, magnitudes,
                battleDefender, battleDefenderDefenseBonus);
            SimulationOutcome threeRound = SimulateRounds(grid, aiArmy, enemyArmy, 3, magnitudes,
                battleDefender, battleDefenderDefenseBonus);

            float twoRoundMargin = LossMarginAgainstUs(ownHp, enemyHp, twoRound);
            float threeRoundMargin = LossMarginAgainstUs(ownHp, enemyHp, threeRound);

            bool shouldRetreat = twoRoundMargin > RetreatMarginFraction || threeRoundMargin > RetreatMarginFraction;
            // Keep the army-level advantage classification stable for existing callers.
            bool favorableForAdvance = !shouldRetreat
                && twoRoundMargin < -AdvanceMarginFraction && threeRoundMargin < -AdvanceMarginFraction;
            BattleDebugLog.Write($"[RetreatDiag] army \"{aiArmy?.Name}\" (hp={ownHp}) vs \"{enemyArmy?.Name}\" (hp={enemyHp}): " +
                $"2-round margin={twoRoundMargin:F3} 3-round margin={threeRoundMargin:F3} " +
                $"-> shouldRetreat={shouldRetreat} favorableForAdvance={favorableForAdvance}");
            return new RetreatAssessment { ShouldRetreat = shouldRetreat, IsCitadelDefense = false, FavorableForAdvance = favorableForAdvance };
        }

        // Positive = we're projected to come out worse off (feeds the retreat check); negative =
        // we're projected to come out ahead — same HP-loss-fraction
        // comparison either way, just read from whichever side of zero matters to the caller.
        private static float LossMarginAgainstUs(float ownHp, float enemyHp, SimulationOutcome outcome)
        {
            float ownLossFraction = ownHp > 0f ? Mathf.Clamp01((ownHp - outcome.OwnHpRemaining) / ownHp) : 1f;
            float enemyLossFraction = enemyHp > 0f ? Mathf.Clamp01((enemyHp - outcome.EnemyHpRemaining) / enemyHp) : 1f;
            return ownLossFraction - enemyLossFraction;
        }

        public struct SimulationOutcome
        {
            public float OwnHpRemaining;
            public float EnemyHpRemaining;
        }

        // Honest round-by-round playout on a cloned grid/HP shadow — no real UnitData/BattleGrid
        // ever gets mutated. Each simulated round, every living non-hero unit either attacks its
        // single best REACHABLE target right now (same BattleGrid.IsInRange check + ability
        // modifiers as the live game) or, if nothing is in range yet, takes one step chosen by
        // the same smart lookahead ChooseAction itself uses for a real advance (see RunOneRound's
        // smartAdvance) — which already refuses to step onto an occupied cell. So a melee unit
        // boxed in by its own formation or walled off by the enemy's can't magically close on a
        // screened-off archer; it only gets there if there's an actual empty path, exactly like a
        // real turn. Kills happen mid-round (a unit reaching 0 HP is removed from the shadow grid
        // immediately), so a finished-off target correctly stops absorbing/dealing further
        // "phantom" damage for the rest of that same round, same as it would in the real turn
        // order.
        // Public (not just AssessRetreat's private helper) — this is the reusable "who wins this
        // fight" core the design doc's Combat Worth-It Score (the proactive pre-contact gate) is
        // meant to call into later, per the user's own note that whatever gets built for the
        // reactive in-battle retreat should be reusable there too.
        private const int BattleSimulationTrials = 8;
        private const int ArrangementSimulationTrials = 4;

        private static Dictionary<UnitData, UnitData> CloneConsumableState(ref BattleGrid grid,
            ref ArmyData own, ref ArmyData enemy, ref ArmyData defender, ref ArmyData holding)
        {
            var copies = new Dictionary<UnitData, UnitData>();
            foreach (UnitData u in grid.AllUnits())
                if (EquipmentSystem.IsSingleUse(u.Equipment) || ExplosionAttack.IsSingleUseUnit(u)
                    || ExplosionAttack.TargetLimit(u.Abilities) > 0) copies[u] = u.CloneForCombat();
            if (copies.Count == 0) return copies;
            UnitData Unit(UnitData u) => copies.TryGetValue(u, out var c) ? c : u;
            ArmyData Army(ArmyData a)
            {
                if (a == null) return null;
                var c = ArmyData.CreateVisualSnapshot();
                c.Owner = a.Owner; c.Hex = a.Hex; c.IsGarrison = a.IsGarrison;
                c.Members.AddRange(a.Members.Select(Unit));
                return c;
            }
            ArmyData oldOwn = own, oldEnemy = enemy;
            own = Army(own); enemy = Army(enemy);
            defender = defender == oldOwn ? own : defender == oldEnemy ? enemy : defender;
            holding = holding == oldOwn ? own : holding == oldEnemy ? enemy : holding;
            var copyGrid = new BattleGrid();
            foreach (UnitData u in grid.AllUnits())
                if (grid.TryFindPosition(u, out int r, out int c)) copyGrid.Set(r, c, Unit(u));
            grid = copyGrid;
            return copies;
        }

        private static Dictionary<UnitData, T> RemapShadow<T>(Dictionary<UnitData, T> values,
            Dictionary<UnitData, UnitData> copies) => values == null ? null
                : values.ToDictionary(p => copies.TryGetValue(p.Key, out var c) ? c : p.Key, p => p.Value);

        public static SimulationOutcome SimulateRounds(BattleGrid liveGrid, ArmyData ownArmy, ArmyData enemyArmy, int rounds,
            AbilityMagnitudes magnitudes, ArmyData battleDefender = null, int battleDefenderDefenseBonus = 0,
            int simulationTrials = BattleSimulationTrials, bool deterministic = false,
            ArmyData holdingArmy = null)
        {
            if (liveGrid == null || ownArmy == null || enemyArmy == null || rounds <= 0)
                return default;

            int trials = Mathf.Max(1, simulationTrials);
            int baseSeed = BuildSimulationSeed(ownArmy, enemyArmy);
            float ownHpSum = 0f;
            float enemyHpSum = 0f;

            for (int trial = 0; trial < trials; trial++)
            {
                BattleGrid sourceGrid = liveGrid;
                ArmyData trialOwn = ownArmy, trialEnemy = enemyArmy, trialDefender = battleDefender,
                    trialHolding = holdingArmy;
                CloneConsumableState(ref sourceGrid, ref trialOwn, ref trialEnemy, ref trialDefender, ref trialHolding);
                var grid = new BattleGrid();
                var hp = new Dictionary<UnitData, float>();
                var ownUnits = new List<UnitData>();
                var enemyUnits = new List<UnitData>();
                CollectLivingMembers(sourceGrid, trialOwn, grid, hp, ownUnits);
                CollectLivingMembers(sourceGrid, trialEnemy, grid, hp, enemyUnits);

                // A live Drill bonus belongs to the round in progress; every simulated round below
                // re-deals Drill itself (ApplySimDrill), so start from the undrilled Attack.
                var simulatedAttack = new Dictionary<UnitData, int>();
                var simulatedDefense = new Dictionary<UnitData, int>();
                foreach (UnitData unit in ownUnits)
                {
                    simulatedAttack[unit] = unit.Attack - unit.DrillAttackBonus;
                    simulatedDefense[unit] = unit.IsHero ? Mathf.Max(0, unit.FateMax) : unit.Defense;
                }
                foreach (UnitData unit in enemyUnits)
                {
                    simulatedAttack[unit] = unit.Attack - unit.DrillAttackBonus;
                    simulatedDefense[unit] = unit.IsHero ? Mathf.Max(0, unit.FateMax) : unit.Defense;
                }

                var fateByArmy = new Dictionary<ArmyData, int>
                {
                    [trialOwn] = Mathf.Max(0, BattleTurnOrder.LivingCommanderOnGrid(grid, trialOwn)?.Fate ?? 0),
                    [trialEnemy] = Mathf.Max(0, BattleTurnOrder.LivingCommanderOnGrid(grid, trialEnemy)?.Fate ?? 0),
                };
                var rng = deterministic ? null : new System.Random(unchecked(baseSeed + trial * 7919));
                var previousPositions = new Dictionary<UnitData, Vector2Int>();

                for (int round = 0; round < rounds; round++)
                {
                    List<UnitData> order = BattleTurnOrder.BuildOrder(grid, trialOwn, trialEnemy,
                        unchecked(baseSeed * 31 + trial * 131 + round + 1));
                    List<UnitData> drilled = ApplySimDrill(grid, hp, simulatedAttack, magnitudes,
                        trialOwn, trialEnemy);
                    RunOneRound(grid, hp, order, magnitudes, null, smartAdvance: true,
                        battleDefender: trialDefender, battleDefenderDefenseBonus: battleDefenderDefenseBonus,
                        rng: rng, fateByArmy: fateByArmy, simulatedAttack: simulatedAttack,
                        simulatedDefense: simulatedDefense, ownArmy: trialOwn, enemyArmy: trialEnemy,
                        previousPositions: previousPositions, holdingArmy: trialHolding);
                    RevertSimDrill(drilled, simulatedAttack, magnitudes);
                    if (!IsSimulationCombatCapable(grid, hp, trialOwn)
                        || !IsSimulationCombatCapable(grid, hp, trialEnemy))
                        break;
                }

                foreach (UnitData member in ownUnits)
                    if (member.IsGroundCombatant)
                        ownHpSum += Mathf.Max(0f, hp[member]);
                foreach (UnitData member in enemyUnits)
                    if (member.IsGroundCombatant)
                        enemyHpSum += Mathf.Max(0f, hp[member]);
            }

            return new SimulationOutcome
            {
                OwnHpRemaining = ownHpSum / trials,
                EnemyHpRemaining = enemyHpSum / trials,
            };
        }

        private static int BuildSimulationSeed(ArmyData ownArmy, ArmyData enemyArmy)
        {
            unchecked
            {
                int seed = 17;
                seed = seed * 31 + (ownArmy?.Id ?? 0);
                seed = seed * 31 + (enemyArmy?.Id ?? 0);
                foreach (UnitData unit in ownArmy?.Members ?? new List<UnitData>())
                    seed = seed * 31 + unit.RuntimeId;
                foreach (UnitData unit in enemyArmy?.Members ?? new List<UnitData>())
                    seed = seed * 31 + unit.RuntimeId;
                return seed;
            }
        }

        private static List<UnitData> BuildInitiativeOrderExcluding(BattleGrid grid, ArmyData ownArmy,
            ArmyData enemyArmy, UnitData exclude, int seed)
        {
            List<UnitData> order = BattleTurnOrder.BuildOrder(grid, ownArmy, enemyArmy, seed);
            if (exclude != null)
                order.Remove(exclude);
            return order;
        }

        // One simulated round: every living listed unit attacks its best reachable target, or
        // takes one step if nothing's in range. Extracted so SimulateRounds' own multi-round
        // projection and FindBestAdvanceStep's single-move lookahead share the exact same
        // round-resolution logic — a candidate position can never be scored as better than it
        // would actually play out for real. damageDealtByUnit, if given, accumulates how much
        // each attacker actually landed this round (FindBestAdvanceStep uses this to score
        // candidate moves; SimulateRounds itself only needs the final HP totals).
        //
        // Outer projections and live play share the army-scored movement selector. Its bounded
        // inner forecasts use the path-only selector to avoid recursive lookahead.
        private static void RunOneRound(BattleGrid grid, Dictionary<UnitData, float> hp, List<UnitData> order,
            AbilityMagnitudes magnitudes, Dictionary<UnitData, float> damageDealtByUnit, bool smartAdvance = false,
            ArmyData battleDefender = null, int battleDefenderDefenseBonus = 0, System.Random rng = null,
            Dictionary<ArmyData, int> fateByArmy = null, Dictionary<UnitData, int> simulatedAttack = null,
            Dictionary<UnitData, int> simulatedDefense = null, ArmyData ownArmy = null, ArmyData enemyArmy = null,
            Dictionary<UnitData, Vector2Int> previousPositions = null, bool healHeroesAtStart = true,
            ArmyData holdingArmy = null)
        {
            var suppressed = new HashSet<UnitData>();
            // One call is one round: Bodyguard is spent once per round.
            var reactions = new BattleReactionState();
            if (healHeroesAtStart)
                foreach (UnitData hero in grid.AllUnits().Where(u => u.IsHero).ToList())
                    ApplySimHeal(grid, hp, hero, rng, ownArmy, enemyArmy);
            for (int i = 0; i < order.Count; i++)
            {
                if (ownArmy != null && enemyArmy != null
                    && (!IsSimulationCombatCapable(grid, hp, ownArmy)
                        || !IsSimulationCombatCapable(grid, hp, enemyArmy)))
                    return;

                UnitData actor = order[i];
                if (suppressed.Contains(actor))
                    continue;
                if (!hp.TryGetValue(actor, out float actorHp) || actorHp <= 0f)
                    continue;
                if (!grid.TryFindPosition(actor, out int row, out int col))
                    continue;

                ApplySimHeal(grid, hp, actor, rng, ownArmy, enemyArmy);

                if (BattleTargetSelector.TryFindBestReachableTarget(grid, hp, actor, row, col, magnitudes, order, i,
                    out UnitData target, out float expectedDamage, battleDefender, battleDefenderDefenseBonus,
                    simulatedAttack, simulatedDefense, reactions))
                {
                    bool explosion = ExplosionAttack.TargetLimit(actor.Abilities) > 0;
                    UnitData victim = ApplySimAttack(grid, hp, actor, target, expectedDamage, false, magnitudes,
                        battleDefender, battleDefenderDefenseBonus, rng, fateByArmy, simulatedAttack,
                        simulatedDefense, ownArmy, enemyArmy, reactions, damageDealtByUnit,
                        out float damage, out bool suppressVictim, suppressed, order, i);

                    if (suppressVictim && order.IndexOf(victim) > i)
                        suppressed.Add(victim);

                    // UnitAbilities.Counterattack — a surviving victim answers before the next
                    // turn, through the same attack path (never intercepted, never answered).
                    if (!explosion && hp[actor] > 0 && !suppressVictim && BattleRules.CanCounterattack(grid, victim, actor,
                            hp.TryGetValue(victim, out float victimHp) ? victimHp : 0f,
                            Mathf.RoundToInt(damage)))
                    {
                        ApplySimAttack(grid, hp, victim, actor, null, true, magnitudes,
                            battleDefender, battleDefenderDefenseBonus, rng, fateByArmy, simulatedAttack,
                            simulatedDefense, ownArmy, enemyArmy, reactions, damageDealtByUnit,
                            out _, out _, suppressed, order, i);
                    }

                    if (ownArmy != null && enemyArmy != null
                        && (!IsSimulationCombatCapable(grid, hp, ownArmy)
                            || !IsSimulationCombatCapable(grid, hp, enemyArmy)))
                        return;
                    continue;
                }

                if (holdingArmy != null && holdingArmy.Members.Contains(actor))
                    continue;

                (int row, int col)? step = smartAdvance
                    ? FindBestAdvanceStepInSim(grid, hp, order, i, actor, row, col, magnitudes,
                        ownArmy, enemyArmy, battleDefender, battleDefenderDefenseBonus,
                        simulatedAttack, simulatedDefense, previousPositions)
                    : FindStepToward(grid, actor, row, col, magnitudes);
                if (step.HasValue)
                {
                    if (previousPositions != null)
                        previousPositions[actor] = new Vector2Int(row, col);
                    grid.Set(row, col, null);
                    grid.Set(step.Value.row, step.Value.col, actor);
                }
            }
        }

        // One attack of the shadow battle — a primary attack or a Counterattack answer — in the
        // live BattleEngine order: resolve (real tokens with `rng`, else the expected value), Bodyguard
        // interception (primary only), the victim's Berserk and the actor's Shock, Splash around
        // the victim, Commander hand-over and removal. Returns the unit the hit landed on.
        // expectedDamage: the deterministic path's damage; null computes it here (a counterattack
        // has no selector estimate). A primary's estimate already describes its Bodyguard.
        private static UnitData ApplySimAttack(BattleGrid grid, Dictionary<UnitData, float> hp,
            UnitData actor, UnitData target, float? expectedDamage, bool isCounterattack,
            AbilityMagnitudes magnitudes, ArmyData battleDefender, int battleDefenderDefenseBonus,
            System.Random rng, Dictionary<ArmyData, int> fateByArmy, Dictionary<UnitData, int> simulatedAttack,
            Dictionary<UnitData, int> simulatedDefense, ArmyData ownArmy, ArmyData enemyArmy,
            BattleReactionState reactions, Dictionary<UnitData, float> damageDealtByUnit,
            out float damage, out bool suppressVictim, HashSet<UnitData> suppressed = null,
            List<UnitData> order = null, int actingIndex = -1)
        {
            UnitData victim = target;
            suppressVictim = false;
            if (ExplosionAttack.TargetLimit(actor.Abilities) > 0)
            {
                damage = ApplySimExplosion(grid, hp, actor, magnitudes, battleDefender,
                    battleDefenderDefenseBonus, rng, fateByArmy, simulatedAttack, simulatedDefense,
                    ownArmy, enemyArmy, damageDealtByUnit, suppressed, order, actingIndex);
                return target;
            }
            if (rng != null)
            {
                BattleSimExchangeOutcome exchange = ResolveSimulatedExchange(
                    grid, actor, target, hp, magnitudes, battleDefender,
                    battleDefenderDefenseBonus, rng, fateByArmy, simulatedAttack,
                    simulatedDefense, ownArmy, enemyArmy);
                UnitData guard = !isCounterattack && exchange.Damage > 0
                    ? FindSimBodyguard(grid, hp, target, actor, reactions) : null;
                if (guard != null)
                {
                    reactions?.BodyguardsUsed.Add(guard);
                    victim = guard;
                    int intercepted = BattleSimulationKernel.InterceptedDamage(
                        exchange.AttackerSuccesses - exchange.DefenderSuccesses,
                        actor.Abilities, guard.TypeTags, guard.Abilities, magnitudes);
                    exchange = new BattleSimExchangeOutcome(intercepted, intercepted > 0,
                        exchange.AttackerSuccesses, exchange.DefenderSuccesses);
                }
                damage = exchange.Damage;

                float victimHp = hp[victim];
                int transientAttack = SimulatedAttackOf(victim, simulatedAttack);
                int transientDefense = SimulatedDefenseOf(victim, simulatedDefense);
                BattleSimulationKernel.ApplyPrimaryOutcome(
                    exchange, actor.Abilities, victim.Abilities,
                    ref victimHp, ref transientAttack, ref transientDefense,
                    magnitudes, out suppressVictim);
                hp[victim] = victimHp;
                if (simulatedAttack != null) simulatedAttack[victim] = transientAttack;
                if (simulatedDefense != null) simulatedDefense[victim] = transientDefense;
            }
            else
            {
                UnitData guard = !isCounterattack ? FindSimBodyguard(grid, hp, target, actor, reactions) : null;
                if (guard != null)
                {
                    reactions?.BodyguardsUsed.Add(guard);
                    victim = guard;
                }
                damage = expectedDamage ?? BattleCombatOdds.Evaluate(actor, target,
                    BattleRules.GetTotalDefenseBonus(grid, target, battleDefender, battleDefenderDefenseBonus),
                    magnitudes, hp.TryGetValue(target, out float targetHp) ? targetHp : 0f,
                    simulatedAttack != null && simulatedAttack.TryGetValue(actor, out int simAtk) ? simAtk : (int?)null,
                    simulatedDefense != null && simulatedDefense.TryGetValue(target, out int simDef) ? simDef : (int?)null)
                    .ExpectedDamage;
                hp[victim] -= damage;
                // Deterministic lookahead: an expected hit that rounds to a landed point
                // applies the victim's Berserk and the actor's ShockAttack, the same
                // rounding SimSideHit gives a Splash side-hit.
                int landed = Mathf.RoundToInt(damage);
                if (landed > 0 && simulatedAttack != null && simulatedDefense != null)
                {
                    int attack = SimulatedAttackOf(victim, simulatedAttack);
                    int defense = SimulatedDefenseOf(victim, simulatedDefense);
                    BattleSimulationKernel.ApplyBerserkIfHit(
                        true, victim.Abilities, ref attack, ref defense, magnitudes);
                    simulatedAttack[victim] = attack;
                    simulatedDefense[victim] = defense;
                }
                suppressVictim = BattleSimulationKernel.SuppressesTurn(landed, actor.Abilities);
            }

            if (damageDealtByUnit != null)
                damageDealtByUnit[actor] = damageDealtByUnit.TryGetValue(actor, out float dealt)
                    ? dealt + damage : damage;

            bool trackSplashCommanders = fateByArmy != null
                && (actor.HasAbility(UnitAbilities.Splash) || actor.HasAbility(UnitAbilities.Scorcher));
            UnitData ownCommander = trackSplashCommanders
                ? BattleTurnOrder.LivingCommanderOnGrid(grid, ownArmy) : null;
            UnitData enemyCommander = trackSplashCommanders
                ? BattleTurnOrder.LivingCommanderOnGrid(grid, enemyArmy) : null;
            ApplySimSplash(grid, hp, actor, victim, damage, magnitudes, rng,
                simulatedAttack, simulatedDefense);
            // A Commander killed by a side-hit (either side: Splash hits allies too) hands
            // command and its Fate pool over exactly like a primary kill below.
            if (trackSplashCommanders)
            {
                HandOverSplashKilledCommander(grid, ownArmy, ownCommander, fateByArmy);
                HandOverSplashKilledCommander(grid, enemyArmy, enemyCommander, fateByArmy);
            }
            if (hp[victim] <= 0f && grid.TryFindPosition(victim, out int vRow, out int vCol))
            {
                grid.Set(vRow, vCol, null);
                // A killed hero stops commanding immediately in the shadow battle. If the
                // army has another living hero, that hero becomes Commander for subsequent
                // rounds/exchanges and brings its own remaining Fate pool.
                if (victim.IsHero && fateByArmy != null)
                {
                    ArmyData victimArmy = FindSimulationArmy(victim, ownArmy, enemyArmy);
                    UnitData replacement = BattleTurnOrder.LivingCommanderOnGrid(grid, victimArmy);
                    if (victimArmy != null)
                        fateByArmy[victimArmy] = Mathf.Max(0, replacement?.Fate ?? 0);
                }
            }
            CompleteSimulatedAttack(grid, hp, actor, simulatedAttack, simulatedDefense, fateByArmy, ownArmy, enemyArmy);
            return victim;
        }

        private static float ApplySimExplosion(BattleGrid grid, Dictionary<UnitData, float> hp, UnitData actor,
            AbilityMagnitudes magnitudes, ArmyData battleDefender, int defenderBonus, System.Random rng,
            Dictionary<ArmyData, int> fateByArmy, Dictionary<UnitData, int> attacks, Dictionary<UnitData, int> defenses,
            ArmyData ownArmy, ArmyData enemyArmy, Dictionary<UnitData, float> dealtByUnit,
            HashSet<UnitData> suppressed, List<UnitData> order, int actingIndex)
        {
            bool expendUnit = ExplosionAttack.IsSingleUseUnit(actor);
            var pool = grid.AllUnits().Where(u => (!expendUnit || u != actor)
                && hp.TryGetValue(u, out float h) && h > 0).ToList();
            int count = Mathf.Min(ExplosionAttack.TargetLimit(actor.Abilities), pool.Count);
            float probability = count / (float)Mathf.Max(1, pool.Count);
            var shot = actor.CloneForCombat(); shot.Attack = ExplosionAttack.AttackTokens;
            float netDamage = 0;
            int iterations = rng == null ? pool.Count : count;
            for (int i = 0; i < iterations; i++)
            {
                if (rng != null) { int j = rng.Next(i, pool.Count); (pool[i], pool[j]) = (pool[j], pool[i]); }
                UnitData victim = pool[i];
                float damage;
                if (rng == null)
                    damage = probability * BattleCombatOdds.Evaluate(shot, victim,
                        BattleRules.GetTotalDefenseBonus(grid, victim, battleDefender, defenderBonus),
                        magnitudes, hp[victim], ExplosionAttack.AttackTokens,
                        defenses != null && defenses.TryGetValue(victim, out int d) ? d : (int?)null).ExpectedDamage;
                else
                    damage = ResolveSimulatedExchange(grid, actor, victim, hp, magnitudes, battleDefender,
                        defenderBonus, rng, fateByArmy, attacks, defenses, ownArmy, enemyArmy,
                        ExplosionAttack.AttackTokens).Damage;
                float landed = Mathf.Min(hp[victim], damage);
                hp[victim] = Mathf.Max(0, hp[victim] - damage);
                if (hp[victim] > 0 && BattleSimulationKernel.SuppressesTurn(Mathf.RoundToInt(damage), actor.Abilities)
                    && order != null && order.IndexOf(victim) > actingIndex) suppressed?.Add(victim);
                netDamage += victim.Owner == actor.Owner ? -landed : landed;
                if (damage > 0 && attacks != null && defenses != null)
                {
                    int a = SimulatedAttackOf(victim, attacks), d = SimulatedDefenseOf(victim, defenses);
                    BattleSimulationKernel.ApplyBerserkIfHit(true, victim.Abilities, ref a, ref d, magnitudes);
                    attacks[victim] = a; defenses[victim] = d;
                }
                if (hp[victim] <= 0 && grid.TryFindPosition(victim, out int r, out int c))
                {
                    grid.Set(r, c, null);
                    if (victim.IsHero && fateByArmy != null)
                    {
                        ArmyData army = FindSimulationArmy(victim, ownArmy, enemyArmy);
                        if (army != null) fateByArmy[army] = BattleTurnOrder.LivingCommanderOnGrid(grid, army)?.Fate ?? 0;
                    }
                }
            }
            if (dealtByUnit != null)
                dealtByUnit[actor] = (dealtByUnit.TryGetValue(actor, out float prior) ? prior : 0) + netDamage;
            CompleteSimulatedAttack(grid, hp, actor, attacks, defenses, fateByArmy, ownArmy, enemyArmy);
            return netDamage;
        }

        private static void CompleteSimulatedAttack(BattleGrid grid, Dictionary<UnitData, float> hp,
            UnitData actor, Dictionary<UnitData, int> simulatedAttack, Dictionary<UnitData, int> simulatedDefense,
            Dictionary<ArmyData, int> fateByArmy, ArmyData ownArmy, ArmyData enemyArmy)
        {
            if (EquipmentSystem.IsSingleUse(actor.Equipment))
            {
                // actor is a private projection prepared by CloneConsumableState. The shadow
                // HP/Attack/Defense contain prior simulated hits, so they move by the item's
                // delta. A hero's defense pool is FateMax, never its Defense field.
                float oldHp = hp[actor];
                int observedHp = oldHp > 0f ? Mathf.Max(1, Mathf.RoundToInt(oldHp)) : 0;
                actor.HitPointsCurrent = observedHp;
                int oldAttack = actor.Attack, oldPool = SimulatedDefenseOf(actor, null), oldFate = actor.Fate;
                EquipmentSystem.ConsumeAfterAttack(actor);
                hp[actor] = Mathf.Clamp(oldHp + actor.HitPointsCurrent - observedHp,
                    oldHp > 0f ? Mathf.Min(1f, oldHp) : 0f, actor.HitPointsMax);
                if (simulatedAttack != null && simulatedAttack.TryGetValue(actor, out int shadowAttack))
                    simulatedAttack[actor] = shadowAttack + actor.Attack - oldAttack;
                if (simulatedDefense != null && simulatedDefense.TryGetValue(actor, out int shadowDefense))
                    simulatedDefense[actor] = shadowDefense + SimulatedDefenseOf(actor, null) - oldPool;
                // A commanding hero's battle Fate pool loses Fate the item no longer provides.
                ArmyData actorArmy = actor.IsHero && fateByArmy != null
                    ? FindSimulationArmy(actor, ownArmy, enemyArmy) : null;
                if (actorArmy != null && fateByArmy.TryGetValue(actorArmy, out int fatePool)
                    && BattleTurnOrder.LivingCommanderOnGrid(grid, actorArmy) == actor)
                    fateByArmy[actorArmy] = Mathf.Max(0, fatePool + actor.Fate - oldFate);
            }
            if (ExplosionAttack.IsSingleUseUnit(actor))
            {
                hp[actor] = 0;
                if (grid.TryFindPosition(actor, out int ar, out int ac)) grid.Set(ar, ac, null);
            }
        }

        private static UnitData FindSimBodyguard(BattleGrid grid, Dictionary<UnitData, float> hp,
            UnitData target, UnitData attacker, BattleReactionState reactions) =>
            BattleRules.FindBodyguard(grid, target, attacker,
                u => hp.TryGetValue(u, out float guardHp) ? guardHp : 0f, reactions);

        private static int SimulatedAttackOf(UnitData unit, Dictionary<UnitData, int> simulatedAttack) =>
            simulatedAttack != null && simulatedAttack.TryGetValue(unit, out int attack) ? attack : unit.Attack;

        private static int SimulatedDefenseOf(UnitData unit, Dictionary<UnitData, int> simulatedDefense) =>
            simulatedDefense != null && simulatedDefense.TryGetValue(unit, out int defense)
                ? defense : (unit.IsHero ? Mathf.Max(0, unit.FateMax) : unit.Defense);

        // UnitAbilities.Drill for one simulated round — the live BattleEngine rule on shadow HP
        // and simulated Attack. Returns the buffed units so the caller takes the bonus back off.
        private static List<UnitData> ApplySimDrill(BattleGrid grid, Dictionary<UnitData, float> hp,
            Dictionary<UnitData, int> simulatedAttack, AbilityMagnitudes magnitudes, ArmyData ownArmy,
            ArmyData enemyArmy)
        {
            var drilled = new List<UnitData>();
            if (simulatedAttack == null || magnitudes.DrillAttackBonus == 0)
                return drilled;
            foreach (ArmyData army in new[] { ownArmy, enemyArmy })
            {
                if (army == null)
                    continue;
                List<UnitData> present = army.Members.Where(u => hp.TryGetValue(u, out float uHp) && uHp > 0f
                    && grid.TryFindPosition(u, out _, out _)).ToList();
                int carriers = present.Count(u => u.HasAbility(UnitAbilities.Drill));
                foreach (int index in BattleSimulationKernel.SelectDrillTargets(present.Count, carriers,
                             k => present[k].IsGroundCombatant, k => SimulatedAttackOf(present[k], simulatedAttack)))
                {
                    UnitData unit = present[index];
                    simulatedAttack[unit] = SimulatedAttackOf(unit, simulatedAttack) + magnitudes.DrillAttackBonus;
                    drilled.Add(unit);
                }
            }
            return drilled;
        }

        private static void RevertSimDrill(List<UnitData> drilled, Dictionary<UnitData, int> simulatedAttack,
            AbilityMagnitudes magnitudes)
        {
            foreach (UnitData unit in drilled)
                simulatedAttack[unit] -= magnitudes.DrillAttackBonus;
        }

        private static void HandOverSplashKilledCommander(BattleGrid grid, ArmyData army,
            UnitData commanderBefore, Dictionary<ArmyData, int> fateByArmy)
        {
            if (army == null || commanderBefore == null || grid.TryFindPosition(commanderBefore, out _, out _))
                return;
            UnitData replacement = BattleTurnOrder.LivingCommanderOnGrid(grid, army);
            fateByArmy[army] = Mathf.Max(0, replacement?.Fate ?? 0);
        }

        private static void ApplySimHeal(BattleGrid grid, Dictionary<UnitData, float> hp,
            UnitData carrier, System.Random rng, ArmyData ownArmy, ArmyData enemyArmy)
        {
            if (!carrier.HasAbility(UnitAbilities.Heal)
                || !hp.TryGetValue(carrier, out float carrierHp) || carrierHp <= 0f) return;
            ArmyData side = FindSimulationArmy(carrier, ownArmy, enemyArmy);
            var allies = grid.AllUnits().Where(u => !u.IsPrisoner
                && (side != null ? side.Members.Contains(u) : u.Owner == carrier.Owner)).ToList();
            int index = BattleSimulationKernel.SelectHealingTarget(allies.Count, i =>
                hp.TryGetValue(allies[i], out float current) && current > 0f
                && current < allies[i].HitPointsMax && allies[i].TypeTags.Contains(UnitTypeTag.Bio), rng);
            if (index >= 0)
            {
                UnitData target = allies[index];
                hp[target] = BattleSimulationKernel.HealedHitPoints(hp[target], target.HitPointsMax);
                if (EquipmentSystem.IsSingleUse(target.Equipment))
                {
                    target.HitPointsCurrent = Mathf.RoundToInt(hp[target]);
                    EquipmentSystem.ReconcileResourceRefill(target, EquipmentStat.HitPoints);
                }
            }
        }

        private static BattleSimExchangeOutcome ResolveSimulatedExchange(BattleGrid grid, UnitData actor, UnitData target,
            Dictionary<UnitData, float> hp, AbilityMagnitudes magnitudes, ArmyData battleDefender,
            int battleDefenderDefenseBonus, System.Random rng, Dictionary<ArmyData, int> fateByArmy,
            Dictionary<UnitData, int> simulatedAttack, Dictionary<UnitData, int> simulatedDefense,
            ArmyData ownArmy, ArmyData enemyArmy, int? attackTokensOverride = null)
        {
            int attackPool = attackTokensOverride ?? (simulatedAttack != null && simulatedAttack.TryGetValue(actor, out int simAtk)
                ? simAtk : actor.Attack);
            int defensePool = simulatedDefense != null && simulatedDefense.TryGetValue(target, out int simDef)
                ? simDef : (target.IsHero ? Mathf.Max(0, target.FateMax) : target.Defense);
            defensePool += BattleRules.GetTotalDefenseBonus(
                grid, target, battleDefender, battleDefenderDefenseBonus);

            ArmyData actorArmy = FindSimulationArmy(actor, ownArmy, enemyArmy);
            ArmyData targetArmy = FindSimulationArmy(target, ownArmy, enemyArmy);
            int actorFate = actorArmy != null && fateByArmy != null
                && fateByArmy.TryGetValue(actorArmy, out int af) ? af : 0;
            if (attackTokensOverride.HasValue && actorArmy == targetArmy) actorFate = 0;
            int targetFate = targetArmy != null && fateByArmy != null
                && fateByArmy.TryGetValue(targetArmy, out int tf) ? tf : 0;

            BattleSimExchangeOutcome outcome = BattleSimulationKernel.ResolveExchange(
                attackPool, defensePool, actor.Abilities, target.TypeTags, target.Abilities,
                ref actorFate, ref targetFate,
                hp.TryGetValue(target, out float targetHp) ? Mathf.CeilToInt(targetHp) : int.MaxValue,
                magnitudes, rng);

            if (actorArmy != null && fateByArmy != null && !(attackTokensOverride.HasValue && actorArmy == targetArmy))
                fateByArmy[actorArmy] = actorFate;
            if (targetArmy != null && fateByArmy != null)
                fateByArmy[targetArmy] = targetFate;

            return outcome;
        }

        private static ArmyData FindSimulationArmy(UnitData unit, ArmyData ownArmy, ArmyData enemyArmy)
        {
            if (unit == null)
                return null;
            if (ownArmy != null && ownArmy.Members.Contains(unit))
                return ownArmy;
            if (enemyArmy != null && enemyArmy.Members.Contains(unit))
                return enemyArmy;
            return null;
        }

        // Shadow-grid mirror of BattleScreenUI.Combat.cs's ResolveSplashSkills, for the round
        // projections above. Half (floored) of the primary damage minus the neighbour's own
        // CeramicArmor (Option A — the attacker's offensive bonuses are already in `primaryDamage`),
        // to up to two orthogonal neighbours of `target` for Splash and/or one Bio neighbour for
        // Scorcher. Deterministic neighbour order (no RNG in a projection). Only runs for a
        // Splash/Scorcher actor — every existing projection is byte-for-byte unchanged.
        private static void ApplySimSplash(BattleGrid grid, Dictionary<UnitData, float> hp, UnitData actor,
            UnitData target, float primaryDamage, AbilityMagnitudes magnitudes, System.Random rng = null,
            Dictionary<UnitData, int> simulatedAttack = null, Dictionary<UnitData, int> simulatedDefense = null)
        {
            bool splash = actor.HasAbility(UnitAbilities.Splash);
            bool scorcher = actor.HasAbility(UnitAbilities.Scorcher);
            if ((!splash && !scorcher) || primaryDamage <= 0f
                || !grid.TryFindPosition(target, out int tr, out int tc))
                return;

            int half = Mathf.FloorToInt(primaryDamage / 2f);
            if (half <= 0)
                return;

            var neighbours = new List<UnitData>();
            int[] dRow = { -1, 1, 0, 0 };
            int[] dCol = { 0, 0, -1, 1 };
            for (int i = 0; i < 4; i++)
            {
                UnitData n = grid.Get(tr + dRow[i], tc + dCol[i]);
                if (n != null && n != actor && n != target && hp.TryGetValue(n, out float nhp) && nhp > 0f)
                    neighbours.Add(n);
            }
            if (neighbours.Count == 0)
                return;

            List<BattleSimSecondaryTarget> selectedTargets =
                BattleSimulationKernel.SelectSecondaryTargets(
                    neighbours.Count,
                    i => neighbours[i].TypeTags.Contains(UnitTypeTag.Bio),
                    splash, scorcher, rng);
            foreach (BattleSimSecondaryTarget selected in selectedTargets)
            {
                SimSideHit(grid, hp, neighbours[selected.Index],
                    Mathf.RoundToInt(primaryDamage), magnitudes,
                    simulatedAttack, simulatedDefense);
            }
        }

        private static void SimSideHit(BattleGrid grid, Dictionary<UnitData, float> hp, UnitData victim,
            int primaryDamage, AbilityMagnitudes magnitudes, Dictionary<UnitData, int> simulatedAttack,
            Dictionary<UnitData, int> simulatedDefense)
        {
            int damage = BattleSimulationKernel.SecondaryDamage(primaryDamage, victim.Abilities, magnitudes);
            if (damage <= 0)
                return;

            hp[victim] -= damage;
            if (simulatedAttack != null && simulatedDefense != null)
            {
                int attack = simulatedAttack.TryGetValue(victim, out int atk) ? atk : victim.Attack;
                int defense = simulatedDefense.TryGetValue(victim, out int def) ? def : victim.Defense;
                BattleSimulationKernel.ApplyBerserkIfHit(
                    true, victim.Abilities, ref attack, ref defense, magnitudes);
                simulatedAttack[victim] = attack;
                simulatedDefense[victim] = defense;
            }

            if (hp[victim] <= 0f && grid.TryFindPosition(victim, out int vr, out int vc))
                grid.Set(vr, vc, null);
        }

        private static void CollectLivingMembers(BattleGrid liveGrid, ArmyData army, BattleGrid shadowGrid,
            Dictionary<UnitData, float> hp, List<UnitData> units)
        {
            if (army == null)
                return;
            foreach (UnitData member in army.Members)
            {
                // Heroes are passive tactical targets: present in the shadow grid/HP model and
                // attackable, while BattleTurnOrder still excludes them from taking actions.
                if (member.HitPointsCurrent <= 0)
                    continue;
                if (!liveGrid.TryFindPosition(member, out int row, out int col))
                    continue;
                shadowGrid.Set(row, col, member);
                hp[member] = member.HitPointsCurrent;
                units.Add(member);
            }
        }

        // Target-desirability scoring itself now lives in BattleTargetSelector (shared by the live
        // per-turn pick and the round-simulation pick) — see ChooseAction/RunOneRound's own calls
        // into it.

        private static bool IsSimulationCombatCapable(BattleGrid grid, Dictionary<UnitData, float> hp,
            ArmyData army)
        {
            if (grid == null || hp == null || army == null)
                return false;
            foreach (UnitData member in army.Members)
                if (member.IsGroundCombatant
                    && hp.TryGetValue(member, out float memberHp) && memberHp > 0f
                    && grid.TryFindPosition(member, out _, out _))
                    return true;
            return false;
        }

        private static float TotalHp(ArmyData army)
        {
            float total = 0f;
            if (army == null)
                return total;
            foreach (UnitData member in army.Members)
                if (member.IsGroundCombatant && member.HitPointsCurrent > 0)
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

        // Retreat is an army-level decision. A combatant with no attack follows a free
        // route to its own firing range; exposure is evaluated as part of the army trade,
        // not as a second veto that the deployment/retreat projection cannot reproduce.
        public static AiAction ChooseAction(BattleGrid grid, UnitData actor, Dictionary<UnitData, int> waitStreak,
            ArmyData ownArmy, ArmyData enemyArmy, AbilityMagnitudes magnitudes,
            List<UnitData> turnOrder, int turnIndex, bool favorableFight = false,
            ArmyData battleDefender = null, int battleDefenderDefenseBonus = 0,
            Dictionary<UnitData, Vector2Int> previousPositions = null, BattleReactionState reactions = null)
        {
            var passAction = new AiAction { Kind = AiActionKind.Pass, Reason = AiThoughtCategory.CautiousWait };
            if (grid == null || actor == null || waitStreak == null
                || !grid.TryFindPosition(actor, out int actorRow, out int actorCol))
                return passAction;

            // reactions: the live round's spent Bodyguard (BattleEngine.Reactions).
            if (BattleTargetSelector.TryChooseAttackTarget(grid, actor, actorRow, actorCol, magnitudes,
                turnOrder, turnIndex, out AiAction attackAction, battleDefender, battleDefenderDefenseBonus,
                reactions))
            {
                waitStreak[actor] = 0;
                previousPositions?.Remove(actor);
                return attackAction;
            }

            if (BattleDebugLog.Verbose) BattleDebugLog.Write($"[MoveDiag] actor {actor.Name} at ({actorRow},{actorCol}): no attack target in range, evaluating a move");

            (int row, int col)? step = FindBestAdvanceStep(grid, ownArmy, enemyArmy, actor,
                actorRow, actorCol, magnitudes, turnOrder, turnIndex, battleDefender,
                battleDefenderDefenseBonus, previousPositions);
            waitStreak[actor] = 0;
            if (!step.HasValue || (step.Value.row == actorRow && step.Value.col == actorCol))
                return passAction; // no empty route to any firing position, rather than a shuffle

            if (previousPositions != null)
                previousPositions[actor] = new Vector2Int(actorRow, actorCol);
            return new AiAction
            {
                Kind = AiActionKind.Move,
                Row = step.Value.row,
                Col = step.Value.col,
                Reason = AiThoughtCategory.AdvanceMove,
            };
        }

        // Minimum number of legal orthogonal moves to a cell from which this unit can
        // attack. Attack reach remains BattleGrid.IsInRange (diagonals included). Occupied
        // cells block travel, not shooting. The actor's old cell is free in a hypothetical move.
        // One extra sentinel beyond all 25 cells represents an unreachable firing position.
        private static int FiringPositionDistance(BattleGrid grid, UnitData actor, int row, int col,
            AbilityMagnitudes magnitudes, int? attackOverride = null)
        {
            // Most deployed artillery and engaged supporters need no path search at all.
            for (int r = 0; r < BattleGrid.Rows; r++)
                for (int c = 0; c < BattleGrid.Columns; c++)
                {
                    UnitData target = grid.Get(r, c);
                    if (target != null && target.Owner != actor.Owner && target.HitPointsCurrent > 0
                        && BattleGrid.IsInRange(row, col, r, c, actor.Range)
                        && BattleTargetSelector.CanDamage(actor, target, magnitudes, attackOverride)) return 0;
                }
            return FiringDistances(grid, actor, magnitudes, attackOverride)[row, col];
        }

        // Multi-source BFS: all empty firing cells are goals. A single map supplies the
        // current distance and every neighbouring candidate, without five repeated searches.
        private static int[,] FiringDistances(BattleGrid grid, UnitData actor,
            AbilityMagnitudes magnitudes, int? attackOverride = null)
        {
            const int unreachable = BattleGrid.Rows * BattleGrid.Columns + 1;
            var targets = new List<(int row, int col)>();
            for (int row = 0; row < BattleGrid.Rows; row++)
                for (int col = 0; col < BattleGrid.Columns; col++)
                {
                    UnitData target = grid.Get(row, col);
                    if (target != null && target.Owner != actor.Owner && target.HitPointsCurrent > 0
                        && BattleTargetSelector.CanDamage(actor, target, magnitudes, attackOverride))
                        targets.Add((row, col));
                }
            var distance = new int[BattleGrid.Rows, BattleGrid.Columns];
            var queue = new Queue<(int row, int col)>();
            for (int row = 0; row < BattleGrid.Rows; row++)
                for (int col = 0; col < BattleGrid.Columns; col++)
                {
                    distance[row, col] = unreachable;
                    if (grid.Get(row, col) != null && grid.Get(row, col) != actor) continue;
                    foreach (var target in targets)
                        if (BattleGrid.IsInRange(row, col, target.row, target.col, actor.Range))
                        {
                            distance[row, col] = 0;
                            queue.Enqueue((row, col));
                            break;
                        }
                }
            int[] dr = { -1, 1, 0, 0 }, dc = { 0, 0, -1, 1 };
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                for (int i = 0; i < dr.Length; i++)
                {
                    int row = cell.row + dr[i], col = cell.col + dc[i];
                    if (!BattleGrid.InBounds(row, col) || distance[row, col] != unreachable
                        || (grid.Get(row, col) != null && grid.Get(row, col) != actor)) continue;
                    distance[row, col] = distance[cell.row, cell.col] + 1;
                    queue.Enqueue((row, col));
                }
            }
            return distance;
        }

        // The bounded inner playout uses this path-only choice to avoid recursive lookahead.
        // It follows the same shortest route to firing range as the outer decision.
        private static (int row, int col)? FindStepToward(BattleGrid grid, UnitData actor, int actorRow, int actorCol,
            AbilityMagnitudes magnitudes)
        {
            var distances = FiringDistances(grid, actor, magnitudes);
            int bestDistance = distances[actorRow, actorCol];
            (int row, int col)? best = null;
            int[] dr = { -1, 1, 0, 0 }, dc = { 0, 0, -1, 1 };
            for (int i = 0; i < dr.Length; i++)
            {
                int row = actorRow + dr[i], col = actorCol + dc[i];
                if (!BattleGrid.InBounds(row, col) || grid.Get(row, col) != null) continue;
                int distance = distances[row, col];
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (row, col);
            }
            return best;
        }

        private static (int row, int col)? FindBestAdvanceStep(BattleGrid liveGrid,
            ArmyData ownArmy, ArmyData enemyArmy, UnitData actor, int actorRow, int actorCol,
            AbilityMagnitudes magnitudes, List<UnitData> order, int turnIndex,
            ArmyData battleDefender, int battleDefenderDefenseBonus,
            Dictionary<UnitData, Vector2Int> previousPositions)
        {
            if (ownArmy == null || enemyArmy == null)
                return FindStepToward(liveGrid, actor, actorRow, actorCol, magnitudes);
            var hp = liveGrid.AllUnits().ToDictionary(u => u, u => (float)u.HitPointsCurrent);
            var attack = liveGrid.AllUnits().ToDictionary(u => u, u => u.Attack);
            var defense = liveGrid.AllUnits().ToDictionary(u => u, u => u.IsHero ? u.FateMax : u.Defense);
            return FindBestAdvanceStepInSim(liveGrid, hp, order, turnIndex, actor, actorRow, actorCol,
                magnitudes, ownArmy, enemyArmy, battleDefender, battleDefenderDefenseBonus,
                attack, defense, previousPositions);
        }

        // Single movement decision for live play and deployment/retreat projections. First
        // require real progress along a legal route; compare the army's resulting exchange among
        // those routes. A fatal position is still evaluated: it may permit useful fire or protect
        // allies. Already-acted units never act again in the current-round remainder.
        private static (int row, int col)? FindBestAdvanceStepInSim(BattleGrid grid,
            Dictionary<UnitData, float> hp, List<UnitData> order, int turnIndex, UnitData actor,
            int actorRow, int actorCol, AbilityMagnitudes magnitudes, ArmyData ownArmy,
            ArmyData enemyArmy, ArmyData battleDefender, int battleDefenderDefenseBonus,
            Dictionary<UnitData, int> simulatedAttack = null,
            Dictionary<UnitData, int> simulatedDefense = null,
            Dictionary<UnitData, Vector2Int> previousPositions = null)
        {
            int? attackOverride = simulatedAttack != null && simulatedAttack.TryGetValue(actor, out int attack)
                ? attack : (int?)null;
            var distances = FiringDistances(grid, actor, magnitudes, attackOverride);
            int currentDistance = distances[actorRow, actorCol];
            if (currentDistance == 0 || currentDistance > BattleGrid.Rows * BattleGrid.Columns)
                return null;
            var remaining = order != null
                ? order.Skip(Mathf.Max(0, turnIndex + 1)).Where(u => u != actor).ToList()
                : new List<UnitData>();
            float ownHp = RemainingCombatHp(grid, hp, ownArmy);
            float enemyHp = RemainingCombatHp(grid, hp, enemyArmy);
            float bestTrade = float.NegativeInfinity, bestDamage = -1f;
            bool bestReverses = true;
            (int row, int col)? bestStep = null;
            int[] dr = { -1, 1, 0, 0 }, dc = { 0, 0, -1, 1 };
            for (int i = 0; i <= dr.Length; i++)
            {
                bool hold = i == dr.Length;
                if (hold && !SupportsAnEngagedAlly(grid, actor, ownArmy, magnitudes)) continue;
                int row = hold ? actorRow : actorRow + dr[i];
                int col = hold ? actorCol : actorCol + dc[i];
                if (!hold && (!BattleGrid.InBounds(row, col) || grid.Get(row, col) != null
                    || distances[row, col] >= currentDistance)) continue;

                BattleGrid trialGrid = CloneGrid(grid);
                var trialHp = new Dictionary<UnitData, float>(hp);
                var trialAttack = simulatedAttack != null ? new Dictionary<UnitData, int>(simulatedAttack) : null;
                var trialDefense = simulatedDefense != null ? new Dictionary<UnitData, int>(simulatedDefense) : null;
                trialGrid.Set(actorRow, actorCol, null);
                trialGrid.Set(row, col, actor);
                ArmyData trialOwn = ownArmy, trialEnemy = enemyArmy, trialDefender = battleDefender, noHolding = null;
                var copies = CloneConsumableState(ref trialGrid, ref trialOwn, ref trialEnemy, ref trialDefender, ref noHolding);
                UnitData trialActor = copies.TryGetValue(actor, out var actorCopy) ? actorCopy : actor;
                trialHp = RemapShadow(trialHp, copies);
                trialAttack = RemapShadow(trialAttack, copies); trialDefense = RemapShadow(trialDefense, copies);
                var trialRemaining = remaining.Select(u => copies.TryGetValue(u, out var c) ? c : u).ToList();
                RunOneRound(trialGrid, trialHp, trialRemaining, magnitudes, null,
                    battleDefender: trialDefender, battleDefenderDefenseBonus: battleDefenderDefenseBonus,
                    simulatedAttack: trialAttack, simulatedDefense: trialDefense,
                    ownArmy: trialOwn, enemyArmy: trialEnemy, healHeroesAtStart: false);

                var damageDealt = new Dictionary<UnitData, float>();
                List<UnitData> nextOrder = BattleTurnOrder.BuildOrder(trialGrid, trialOwn, trialEnemy,
                    unchecked(BuildSimulationSeed(ownArmy, enemyArmy) * 31 + 102));
                RunOneRound(trialGrid, trialHp, nextOrder, magnitudes, damageDealt,
                    battleDefender: trialDefender, battleDefenderDefenseBonus: battleDefenderDefenseBonus,
                    simulatedAttack: trialAttack, simulatedDefense: trialDefense,
                    ownArmy: trialOwn, enemyArmy: trialEnemy);
                float ownLoss = ownHp > 0f ? (ownHp - RemainingCombatHp(trialGrid, trialHp, trialOwn)) / ownHp : 0f;
                float enemyLoss = enemyHp > 0f ? (enemyHp - RemainingCombatHp(trialGrid, trialHp, trialEnemy)) / enemyHp : 0f;
                float trade = enemyLoss - ownLoss;
                float damage = damageDealt.TryGetValue(trialActor, out float dealt) ? dealt : 0f;
                bool reverses = previousPositions != null && previousPositions.TryGetValue(actor, out Vector2Int previous)
                    && previous.x == row && previous.y == col;
                bool tied = Mathf.Abs(trade - bestTrade) <= 0.0001f;
                bool usefulHold = hold && (trade > bestTrade + ScoreTieTolerance
                    || (trade >= bestTrade - ScoreTieTolerance && damage >= bestDamage));
                if (usefulHold || (!hold && trade > bestTrade + 0.0001f) || (!hold && tied && (damage > bestDamage
                    || (damage == bestDamage && bestReverses && !reverses))))
                {
                    bestTrade = trade;
                    bestDamage = damage;
                    bestReverses = reverses;
                    bestStep = (row, col);
                }
            }
            return bestStep;
        }

        private static bool SupportsAnEngagedAlly(BattleGrid grid, UnitData actor, ArmyData army, AbilityMagnitudes magnitudes)
        {
            if (army == null || (!actor.HasAbility(UnitAbilities.Heal)
                && !actor.HasAbility(UnitAbilities.Drill) && !actor.HasAbility(UnitAbilities.Bodyguard))) return false;
            foreach (UnitData ally in army.Members)
            {
                if (ally == actor || !ally.IsGroundCombatant || ally.HitPointsCurrent <= 0
                    || !grid.TryFindPosition(ally, out int row, out int col)) continue;
                if (FiringPositionDistance(grid, ally, row, col, magnitudes) != 0) continue;
                if ((actor.HasAbility(UnitAbilities.Heal) && ally.TypeTags.Contains(UnitTypeTag.Bio))
                    || actor.HasAbility(UnitAbilities.Drill)) return true;
                if (actor.HasAbility(UnitAbilities.Bodyguard)
                    && grid.TryFindPosition(actor, out int ar, out int ac)
                    && BattleGrid.IsOrthogonallyAdjacent(ar, ac, row, col)) return true;
            }
            return false;
        }

        private static BattleGrid CloneGrid(BattleGrid source)
        {
            var clone = new BattleGrid();
            for (int row = 0; row < BattleGrid.Rows; row++)
                for (int col = 0; col < BattleGrid.Columns; col++)
                    clone.Set(row, col, source.Get(row, col));
            return clone;
        }

        private static float RemainingCombatHp(BattleGrid grid, Dictionary<UnitData, float> hp, ArmyData army)
        {
            float total = 0f;
            if (army != null)
                foreach (UnitData unit in army.Members)
                    if (unit.IsGroundCombatant && grid.TryFindPosition(unit, out _, out _)
                        && hp.TryGetValue(unit, out float health)) total += Mathf.Max(0f, health);
            return total;
        }

        // Fate-duel spend decisions now live in FateDuelAi (BattleAttackPopupUI.RunAiTurn calls
        // into it directly) — kept out of this file to keep BattleAi.cs to arrangement/retreat/
        // tactical-move logic only.
    }
}

