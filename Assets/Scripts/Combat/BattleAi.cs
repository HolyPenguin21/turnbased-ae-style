using System.Collections.Generic;
using Game.Cards;
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
        // ---- Arrangement (see the user's own spec: range-aware and simulation-scored, not the
        // generic sequential fill BattleGrid.FromArmies uses by default) ----

        // Replaces whatever FromArmies's generic default already placed for this army. Computed
        // from this army's own roster — it never looks at the opposing side's PLACEMENT (same
        // restriction the human's own Arrangement phase has, and unknowable anyway — both sides
        // arrange independently at the same time; see BattleScreenUI.Show(), which calls this
        // for both AI sides before either has a real layout on the grid). It DOES read the
        // opposing side's own current STATS via `enemyArmy` (see the user's own spec — this is
        // public information either side could already look up by hand via
        // HexSelectionController.TryHandleEnemyArmyMarkerClick's read-only army viewer, so it
        // isn't an unfair advantage).
        //
        // Which ROW each of our own non-hero members ends up in is still forced by their own
        // Range (melee can't threaten anything from the back row on round 1, so there's no
        // decision to make there). What actually gets searched here is: which COLUMN is the
        // best front-line "anchor" — left-packed (tankiest at column 0, the traditional shape)
        // or center-anchored (tankiest in the middle, per the user's own call that a tank
        // sometimes belongs in the middle rather than always at the edge) — and the hero (who is
        // free to stand in ANY column of the back row, not just BattleGrid.HeroColumn; nothing
        // enforces that reservation for anyone but FromArmies's own no-arrangement fallback, see
        // BattleGrid.PlaceArmy) simply stands directly behind whichever column that turns out to
        // be, rather than always defaulting to column 0.
        //
        // Both candidate shapes get played forward with the exact same grid-honest SimulateRounds
        // AssessRetreat already relies on, against a hypothetical enemy layout — since the
        // enemy's own real column choices are unknowable, that hypothetical uses the same plain
        // Range-forced shape (PlaceRangeSplit) for both candidates, so the comparison is only
        // ever about OUR OWN column choice, never about guessing theirs. No literal stat
        // thresholds are hardcoded anywhere here; whichever shape actually comes out ahead in the
        // simulation wins.
        public static void ArrangeArmy(BattleGrid grid, ArmyData army, int frontRow, int backRow, ArmyData enemyArmy,
            AbilityMagnitudes magnitudes)
        {
            if (grid == null || army == null)
                return;

            foreach (UnitData member in army.Members)
                if (grid.TryFindPosition(member, out int row, out int col))
                    grid.Set(row, col, null);

            if (enemyArmy == null)
            {
                // No opposing roster to weigh candidates against (shouldn't happen in a real
                // battle, kept only as a safe fallback) — tank-anchored/left-packed is still a
                // strictly sound default over plain army-list order even with zero information
                // about the enemy, so skip straight to it without any simulation to score against.
                PlaceTankAnchoredSplit(grid, army, frontRow, backRow, centerOut: false, enemyArmy: null);
                return;
            }

            bool weAreAttackerSide = frontRow == BattleGrid.AttackerFrontRow;
            int enemyFrontRow = weAreAttackerSide ? BattleGrid.DefenderFrontRow : BattleGrid.AttackerFrontRow;
            int enemyBackRow = weAreAttackerSide ? BattleGrid.DefenderBackRow : BattleGrid.AttackerBackRow;

            float ownTotalHp = TotalHp(army);
            float enemyTotalHp = TotalHp(enemyArmy);

            bool[] centerOutCandidates = { false, true };
            float bestScore = float.NegativeInfinity;
            bool bestCenterOut = false;

            foreach (bool centerOut in centerOutCandidates)
            {
                var trial = new BattleGrid();
                PlaceTankAnchoredSplit(trial, army, frontRow, backRow, centerOut, enemyArmy);
                PlaceRangeSplit(trial, enemyArmy, enemyFrontRow, enemyBackRow);

                SimulationOutcome outcome = SimulateRounds(trial, army, enemyArmy, 3, magnitudes);

                float ownLoss = ownTotalHp > 0f ? Mathf.Clamp01((ownTotalHp - outcome.OwnHpRemaining) / ownTotalHp) : 1f;
                float enemyLoss = enemyTotalHp > 0f ? Mathf.Clamp01((enemyTotalHp - outcome.EnemyHpRemaining) / enemyTotalHp) : 1f;
                float score = enemyLoss - ownLoss;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCenterOut = centerOut;
                }
            }

            PlaceTankAnchoredSplit(grid, army, frontRow, backRow, bestCenterOut, enemyArmy);
        }

        // Rough "how good a soak is this unit" score — Defense+HP is the base survivability, but
        // CeramicArmor's flat -1 to every hit and Berserk's own "getting hit makes it stronger"
        // trade both make a unit a better front-liner than raw stats alone suggest, so both get
        // folded in as a flat bonus rather than left for raw stats to under-rate them.
        private static float TankScore(UnitData unit)
        {
            float score = unit.Defense + unit.HitPointsMax;
            if (unit.HasAbility(UnitAbilities.CeramicArmor)) score += 2f;
            if (unit.HasAbility(UnitAbilities.Berserk)) score += 2f;
            return score;
        }

        // Column visit order for filling the front row: either plain left-to-right, or center
        // column first and alternating outward — see ArrangeArmy's own comment on why both get
        // tried. Whichever column comes first in this order is where the single tankiest melee
        // member (and therefore the hero standing behind it) ends up.
        private static List<int> ColumnFillOrder(bool centerOut)
        {
            var order = new List<int>();
            if (!centerOut)
            {
                for (int c = 0; c < BattleGrid.Columns; c++)
                    order.Add(c);
                return order;
            }

            int center = (BattleGrid.Columns - 1) / 2;
            order.Add(center);
            for (int d = 1; d < BattleGrid.Columns; d++)
            {
                int right = center + d, left = center - d;
                if (right < BattleGrid.Columns) order.Add(right);
                if (left >= 0) order.Add(left);
            }
            return order;
        }

        // Same "is the back row actually a safe haven this round" read ArrangeArmy always did —
        // extracted so both PlaceTankAnchoredSplit (real placement) and the hypothetical shapes
        // built for scoring can share it.
        private static bool BackRowExposed(int frontRow, int backRow, ArmyData enemyArmy)
        {
            int enemyMaxRange = 0;
            if (enemyArmy != null)
                foreach (UnitData enemyMember in enemyArmy.Members)
                    if (!enemyMember.IsHero && enemyMember.Range > enemyMaxRange)
                        enemyMaxRange = enemyMember.Range;

            bool weAreAttackerSide = frontRow == BattleGrid.AttackerFrontRow;
            int enemyFrontRow = weAreAttackerSide ? BattleGrid.DefenderFrontRow : BattleGrid.AttackerFrontRow;
            int enemyBackRow = weAreAttackerSide ? BattleGrid.DefenderBackRow : BattleGrid.AttackerBackRow;
            int closestEnemyReachToOurBack = Mathf.Min(Mathf.Abs(enemyFrontRow - backRow), Mathf.Abs(enemyBackRow - backRow));
            return enemyMaxRange >= closestEnemyReachToOurBack;
        }

        // Tank-anchored shape: melee sorted tankiest-first into `frontOrder` (so the tankiest
        // lands on whichever column that order visits first), hero moved to stand directly behind
        // that same column instead of the fixed BattleGrid.HeroColumn, ranged filling the back
        // row (tankiest-first for column order, but only if BackRowExposed says the back row
        // isn't actually safe this round — same call the old logic made; see further down for
        // what happens when ranged doesn't all fit). If there's no melee member to anchor on, the
        // hero just falls back to BattleGrid.HeroColumn.
        //
        // The front/back split itself is Range <= 2, not <= 1 — per the user's own report, a
        // Range-2 unit placed in the BACK row can't reach anything on round 1 at all (front row
        // to front row is 2 cells apart, back row to front row is 3 — see BattleGrid's row
        // layout), so a tanky Range-2 unit used to lose its opening turn walking up for nothing
        // instead of anchoring the front line it was tough enough to hold. Range 2 still reaches
        // the enemy front row perfectly well FROM this army's own front row, so it now competes
        // for a front column by TankScore exactly like a true melee (Range 1) member; only
        // Range >= 3 (genuinely usable from the back row without moving first) still goes back.
        private static void PlaceTankAnchoredSplit(BattleGrid grid, ArmyData army, int frontRow, int backRow, bool centerOut, ArmyData enemyArmy)
        {
            UnitData hero = null;
            var melee = new List<UnitData>();
            var ranged = new List<UnitData>();
            foreach (UnitData member in army.Members)
            {
                if (member.IsHero) { hero = member; continue; }
                if (member.Range <= 2) melee.Add(member);
                else ranged.Add(member);
            }
            melee.Sort((a, b) => TankScore(b).CompareTo(TankScore(a)));

            List<int> frontOrder = ColumnFillOrder(centerOut);
            int heroColumn = melee.Count > 0 ? frontOrder[0] : BattleGrid.HeroColumn;

            int frontIndex = 0;
            var overflow = new List<UnitData>();
            foreach (UnitData member in melee)
            {
                if (frontIndex < frontOrder.Count) grid.Set(frontRow, frontOrder[frontIndex++], member);
                else overflow.Add(member);
            }

            if (hero != null)
                grid.Set(backRow, heroColumn, hero);

            var backColumns = new List<int>();
            for (int c = 0; c < BattleGrid.Columns; c++)
                if (c != heroColumn) backColumns.Add(c);

            // A roster can field more ranged members than the back row has room for (a
            // high-CommandRating hero fielding a mostly-ranged army — see ArmyData.Capacity).
            // Whoever doesn't fit gets pushed into `overflow` below and ends up exposed in the
            // FRONT row, so it needs to be the TANKIEST of the bunch, not whoever a plain
            // tankiest-first fill happens to leave over (that used to bump the squishiest ranged
            // unit to the front instead of the toughest — see the user's own report). Protecting
            // the weakest ones in the back row first, then letting the tankiest remainder
            // overflow, fixes that regardless of which side is arranging.
            List<UnitData> rangedForBack = ranged;
            if (ranged.Count > backColumns.Count)
            {
                ranged.Sort((a, b) => TankScore(a).CompareTo(TankScore(b)));
                rangedForBack = ranged.GetRange(0, backColumns.Count);
                overflow.AddRange(ranged.GetRange(backColumns.Count, ranged.Count - backColumns.Count));
            }
            if (BackRowExposed(frontRow, backRow, enemyArmy))
                rangedForBack.Sort((a, b) => TankScore(b).CompareTo(TankScore(a)));

            int backIndex = 0;
            foreach (UnitData member in rangedForBack)
                grid.Set(backRow, backColumns[backIndex++], member);

            foreach (UnitData member in overflow)
            {
                if (frontIndex < frontOrder.Count) grid.Set(frontRow, frontOrder[frontIndex++], member);
                else if (backIndex < backColumns.Count) grid.Set(backRow, backColumns[backIndex++], member);
                // Beyond that there's nowhere left — not reachable given ArmyData.Capacity's cap.
            }
        }

        // Plain Range-forced shape with no tank-anchoring at all: melee front row left-to-right,
        // ranged back row left-to-right, hero at the fixed BattleGrid.HeroColumn. Two jobs: (1)
        // the safe zero-information fallback in ArrangeArmy when enemyArmy is null, and (2) the
        // hypothetical enemy layout ArrangeArmy simulates our own candidates against — which ROW
        // an enemy unit ends up in is forced by its own Range stat, not a guess about their
        // intent, so assuming this shape for them isn't reading anything we don't already know.
        // Same Range <= 2 front/back split as PlaceTankAnchoredSplit (see its own comment) — the
        // hypothetical enemy shouldn't be modeled as parking a Range-2 unit somewhere it
        // couldn't actually reach from either.
        private static void PlaceRangeSplit(BattleGrid grid, ArmyData army, int frontRow, int backRow)
        {
            if (army == null)
                return;

            UnitData hero = null;
            var melee = new List<UnitData>();
            var ranged = new List<UnitData>();
            foreach (UnitData member in army.Members)
            {
                if (member.IsHero) { hero = member; continue; }
                if (member.Range <= 2) melee.Add(member);
                else ranged.Add(member);
            }

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
            // True when the SAME projection that cleared this army to keep fighting also shows a
            // clear advantage in our favor (see FavorMargin below) at BOTH the 2- and 3-round
            // mark — fed into ChooseAction (see BattleScreenUI.AutoActAfterDelay) so units in a
            // fight this one-sidedly favorable don't sit waiting up to MaxWaitStreak turns just to
            // avoid a single round of return fire they can clearly afford (see the user's own
            // report: a numerically superior AI army refusing to close distance and engage even
            // though this exact projection already said the fight was worth it).
            public bool FavorableForAdvance;
        }

        // Margin of HP-loss-fraction disadvantage the AI must be projected to suffer before it
        // actually bails — per the user's own call: a razor-thin projected disadvantage (51%
        // vs 49%) shouldn't read the same as a genuine beating. Only a gap bigger than this
        // triggers a retreat; anything closer is "close enough to fight it out".
        private const float RetreatMarginFraction = 0.15f;

        // Margin of HP-loss-fraction ADVANTAGE required to unlock FavorableForAdvance — lower
        // than RetreatMarginFraction on purpose. Reusing the same 0.15 bar for both directions
        // meant an army needed a near-blowout projection before exposure caution ever relaxed, so
        // units with a clear but more modest numeric edge kept sitting through MaxWaitStreak turns
        // instead of closing distance (see the user's own report this struct's doc-comment already
        // references). Advancing only relaxes exposure caution for a single step, it never commits
        // to anything as hard to undo as a retreat does, so it doesn't need the same high bar.
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
            AbilityMagnitudes magnitudes)
        {
            if (defendingOwnCitadel)
                return new RetreatAssessment { ShouldRetreat = false, IsCitadelDefense = true };

            float ownHp = TotalHp(aiArmy);
            float enemyHp = TotalHp(enemyArmy);

            SimulationOutcome twoRound = SimulateRounds(grid, aiArmy, enemyArmy, 2, magnitudes);
            SimulationOutcome threeRound = SimulateRounds(grid, aiArmy, enemyArmy, 3, magnitudes);

            float twoRoundMargin = LossMarginAgainstUs(ownHp, enemyHp, twoRound);
            float threeRoundMargin = LossMarginAgainstUs(ownHp, enemyHp, threeRound);

            bool shouldRetreat = twoRoundMargin > RetreatMarginFraction || threeRoundMargin > RetreatMarginFraction;
            // AND, not OR — advancing into the open is a bigger commitment than holding back is,
            // so both horizons need to agree the fight is comfortably ours before this relaxes
            // ChooseAction's exposure caution (see FavorableForAdvance's own comment); shouldRetreat
            // stays OR since either horizon looking bad is already reason enough to be cautious.
            // Uses AdvanceMarginFraction, not RetreatMarginFraction — see that constant's own
            // comment for why the two directions need different bars.
            bool favorableForAdvance = !shouldRetreat
                && twoRoundMargin < -AdvanceMarginFraction && threeRoundMargin < -AdvanceMarginFraction;
            return new RetreatAssessment { ShouldRetreat = shouldRetreat, IsCitadelDefense = false, FavorableForAdvance = favorableForAdvance };
        }

        // Positive = we're projected to come out worse off (feeds the retreat check); negative =
        // we're projected to come out ahead (feeds FavorableForAdvance) — same HP-loss-fraction
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
        public static SimulationOutcome SimulateRounds(BattleGrid liveGrid, ArmyData ownArmy, ArmyData enemyArmy, int rounds,
            AbilityMagnitudes magnitudes)
        {
            var grid = new BattleGrid();
            var hp = new Dictionary<UnitData, float>();
            var ownUnits = new List<UnitData>();
            var enemyUnits = new List<UnitData>();

            if (liveGrid != null)
            {
                CollectLivingMembers(liveGrid, ownArmy, grid, hp, ownUnits);
                CollectLivingMembers(liveGrid, enemyArmy, grid, hp, enemyUnits);
            }

            List<UnitData> order = BuildInterleavedOrder(ownUnits, enemyUnits, null);
            for (int round = 0; round < rounds; round++)
                RunOneRound(grid, hp, order, magnitudes, null, smartAdvance: true);

            var result = new SimulationOutcome();
            foreach (UnitData member in ownUnits)
                result.OwnHpRemaining += Mathf.Max(0f, hp[member]);
            foreach (UnitData member in enemyUnits)
                result.EnemyHpRemaining += Mathf.Max(0f, hp[member]);
            return result;
        }

        // Interleaved rather than "resolve all of one side, then the other" — a flat
        // army-then-army order would let whichever side goes first always react to a fresh board
        // while the second side only ever reacts to an already-damaged one. `exclude`, if given,
        // is left out of the order entirely — see FindBestAdvanceStep, where the unit whose
        // candidate move is being scored has already "spent" that round's action moving there.
        private static List<UnitData> BuildInterleavedOrder(List<UnitData> ownUnits, List<UnitData> enemyUnits, UnitData exclude)
        {
            var order = new List<UnitData>();
            int maxCount = Mathf.Max(ownUnits.Count, enemyUnits.Count);
            for (int i = 0; i < maxCount; i++)
            {
                if (i < ownUnits.Count && ownUnits[i] != exclude) order.Add(ownUnits[i]);
                if (i < enemyUnits.Count && enemyUnits[i] != exclude) order.Add(enemyUnits[i]);
            }
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
        // smartAdvance picks which step-finder runs a unit with nothing in range: the plain
        // greedy FindStepToward (nearest enemy, straight-line), or FindBestAdvanceStepInSim's own
        // one-round lookahead against the SAME shadow grid/hp this round is already working
        // against. SimulateRounds (AssessRetreat's own projection, and ArrangeArmy's candidate
        // scoring) passes true — per the user's own report, projecting every unit's movement as
        // dumb greedy stepping understated how fast the AI could actually close distance, which
        // made FavorableForAdvance trigger far less often than the real matchup justified.
        // FindBestAdvanceStep's own two calls (the live per-turn lookahead) pass false — it IS
        // already the smart lookahead, so recursing into another one per candidate direction
        // would multiply the search without changing the answer meaningfully.
        private static void RunOneRound(BattleGrid grid, Dictionary<UnitData, float> hp, List<UnitData> order,
            AbilityMagnitudes magnitudes, Dictionary<UnitData, float> damageDealtByUnit, bool smartAdvance = false)
        {
            // Indexed rather than foreach — BattleTargetSelector needs to know each actor's own
            // position within `order` to tell whether a candidate target still has an action
            // coming later this same round (see ShockAttack's own scoring bonus there).
            for (int i = 0; i < order.Count; i++)
            {
                UnitData actor = order[i];
                if (!hp.TryGetValue(actor, out float actorHp) || actorHp <= 0f)
                    continue;
                if (!grid.TryFindPosition(actor, out int row, out int col))
                    continue;

                if (BattleTargetSelector.TryFindBestReachableTarget(grid, hp, actor, row, col, magnitudes, order, i,
                    out UnitData target, out float damage))
                {
                    hp[target] -= damage;
                    if (damageDealtByUnit != null)
                        damageDealtByUnit[actor] = damageDealtByUnit.TryGetValue(actor, out float dealt) ? dealt + damage : damage;
                    if (hp[target] <= 0f && grid.TryFindPosition(target, out int tRow, out int tCol))
                        grid.Set(tRow, tCol, null);
                    continue;
                }

                (int row, int col)? step = smartAdvance
                    ? FindBestAdvanceStepInSim(grid, hp, order, actor, row, col, magnitudes) ?? FindStepToward(grid, actor, row, col)
                    : FindStepToward(grid, actor, row, col);
                if (step != null)
                {
                    grid.Set(row, col, null);
                    grid.Set(step.Value.row, step.Value.col, actor);
                }
            }
        }

        private static void CollectLivingMembers(BattleGrid liveGrid, ArmyData army, BattleGrid shadowGrid,
            Dictionary<UnitData, float> hp, List<UnitData> units)
        {
            if (army == null)
                return;
            foreach (UnitData member in army.Members)
            {
                if (member.IsHero || member.HitPointsCurrent <= 0) // heroes never attack
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

        // ownArmy/enemyArmy are only used for the FindBestAdvanceStep lookahead below; `magnitudes`
        // feeds both that lookahead AND BattleTargetSelector.TryChooseAttackTarget's own scoring —
        // see BattleScreenUI.AutoActAfterDelay's call site for where these come from.
        // turnOrder/turnIndex: this round's live turn order (BattleScreenUI's own _turnOrder/
        // _turnIndex) — passed straight through to BattleTargetSelector so a ShockAttack-carrying
        // actor can weigh knocking a still-to-act enemy out of the round (see that method's own
        // comment); ChooseAction has no turn-order concept of its own.
        // favorableFight: this round's own AssessRetreat already ran the SAME multi-round
        // projection for this army and found a clear advantage (see RetreatAssessment.
        // FavorableForAdvance) — when true, exposure risk on the step itself is no longer a
        // reason to wait, so a unit advances the instant it has nowhere better to shoot from
        // instead of sitting through up to MaxWaitStreak turns first (see the user's own report:
        // an army with the numbers to win was refusing to close distance at all).
        public static AiAction ChooseAction(BattleGrid grid, UnitData actor, Dictionary<UnitData, int> waitStreak,
            ArmyData ownArmy, ArmyData enemyArmy, AbilityMagnitudes magnitudes,
            List<UnitData> turnOrder, int turnIndex, bool favorableFight = false)
        {
            var passAction = new AiAction { Kind = AiActionKind.Pass, Reason = AiThoughtCategory.CautiousWait };
            if (grid == null || actor == null || waitStreak == null
                || !grid.TryFindPosition(actor, out int actorRow, out int actorCol))
                return passAction;

            if (BattleTargetSelector.TryChooseAttackTarget(grid, actor, actorRow, actorCol, magnitudes,
                turnOrder, turnIndex, out AiAction attackAction))
            {
                waitStreak[actor] = 0;
                return attackAction;
            }

            Debug.Log($"[MoveDiag] actor {actor.Name} at ({actorRow},{actorCol}): no attack target in range, evaluating a move");

            bool alreadyExposed = IsExposedToEnemy(grid, actorRow, actorCol, actor);
            (int row, int col)? bestStep = FindBestAdvanceStep(grid, ownArmy, enemyArmy, actor, actorRow, actorCol, magnitudes);
            (int row, int col)? step = bestStep ?? FindStepToward(grid, actor, actorRow, actorCol);
            Debug.Log($"[MoveDiag] actor {actor.Name}: FindBestAdvanceStep={(bestStep.HasValue ? bestStep.Value.ToString() : "null")} " +
                $"finalStep={(step.HasValue ? step.Value.ToString() : "null")} (fallback used: {!bestStep.HasValue})");

            if (step == null)
            {
                waitStreak[actor] = 0;
                Debug.Log($"[MoveDiag] actor {actor.Name}: no legal step at all -> Pass");
                return passAction;
            }

            bool stepExposes = !alreadyExposed && IsExposedToEnemy(grid, step.Value.row, step.Value.col, actor);
            int streak = waitStreak.TryGetValue(actor, out int s) ? s : 0;
            bool forceAdvance = streak >= MaxWaitStreak;
            // Melee (Range<=2) never hesitates over exposure risk before closing distance — per
            // the user's own call, there's no point in a melee unit hanging back to avoid a
            // single round of return fire when the whole point of the unit is to reach melee
            // range. FindBestAdvanceStep/FindStepToward already guarantee `step` itself is never
            // a retreat for a melee actor (see their own isMelee handling), so this only ever
            // skips the WAIT-and-eat-the-risk-later behavior, never turns a real retreat into an
            // advance.
            bool isMelee = actor.Range <= 2;

            Debug.Log($"[MoveDiag] actor {actor.Name}: alreadyExposed={alreadyExposed} stepExposes={stepExposes} " +
                $"waitStreak={streak} forceAdvance={forceAdvance} favorableFight={favorableFight} isMelee={isMelee} " +
                $"-> {((isMelee || alreadyExposed || !stepExposes || forceAdvance || favorableFight) ? "MOVE" : "WAIT")} to {step.Value}");

            if (isMelee || alreadyExposed || !stepExposes || forceAdvance || favorableFight)
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

        // A single greedy step (orthogonal, any empty cell on the grid — same legality
        // BattleScreenUI.IsAdjacentMoveTarget enforces for the human, per the user's own report
        // that a Range-1 unit could never reach the enemy's Back row because it could never step
        // past the Neutral row into the enemy's own Front row first) toward whichever enemy
        // non-hero unit is currently closest. Null if there's nowhere legal to go.
        private static (int row, int col)? FindStepToward(BattleGrid grid, UnitData actor, int actorRow, int actorCol)
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

            // Melee never retreats (see isMelee's own comment on FindBestAdvanceStep) — a step
            // that would land farther from the nearest enemy than staying put is dropped from
            // consideration entirely here too, since this is also the fallback FindBestAdvanceStep
            // itself uses whenever it finds no legal step at all.
            bool isMelee = actor.Range <= 2;
            int currentDist = Mathf.Abs(actorRow - nearestRow) + Mathf.Abs(actorCol - nearestCol);

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

                int dist = Mathf.Abs(row - nearestRow) + Mathf.Abs(col - nearestCol);
                if (isMelee && dist > currentDist)
                    continue;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = (row, col);
                }
            }
            return best;
        }

        // Grid-honest lookahead for a melee unit with nothing in range THIS round: evaluates
        // every legal single step (same legality FindStepToward already enforces — empty,
        // orthogonally adjacent) by actually playing the fight forward one more round from each
        // candidate — this round spent moving there, the next spent acting normally via the same
        // RunOneRound everything else in this file uses — and picks whichever candidate lets
        // `actor` itself land a hit soonest/hardest, rather than blindly walking toward whichever
        // enemy happens to be nearest in a straight line. A target screened off by its own
        // formation still can't be "reached" here either: the candidates are exactly the cells
        // FindStepToward would already consider, and the lookahead round obeys the same
        // occupied-cell blocking as every other simulated step in this file. Returns null (same
        // as FindStepToward) if there's nowhere legal to step, or if every candidate gets `actor`
        // killed before it would even get a real turn — callers should fall back to the plain
        // FindStepToward in that case.
        private static (int row, int col)? FindBestAdvanceStep(BattleGrid liveGrid, ArmyData ownArmy, ArmyData enemyArmy, UnitData actor,
            int actorRow, int actorCol, AbilityMagnitudes magnitudes)
        {
            if (liveGrid == null || ownArmy == null || enemyArmy == null)
                return null;

            int[] dRows = { -1, 1, 0, 0 };
            int[] dCols = { 0, 0, -1, 1 };

            // Melee (Range<=2) never retreats — per the user's own call, there's no tactical
            // point in a melee unit backing away from a fight it's trying to close on, so a step
            // that lands farther from the nearest enemy than staying put isn't a real candidate
            // at all, and dying before its own next turn no longer disqualifies a candidate either
            // (a melee unit accepts that risk rather than dance sideways forever to avoid it —
            // see the user's own report that the old death-pruning left only sideways/no-progress
            // steps standing almost every turn). Non-melee units keep the old risk-averse
            // behavior unchanged.
            bool isMelee = actor.Range <= 2;
            int curDistanceToNearest = NearestEnemyManhattanDistance(liveGrid, actor, actorRow, actorCol);

            (int row, int col)? bestStep = null;
            float bestDamage = -1f;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < 4; i++)
            {
                int candRow = actorRow + dRows[i];
                int candCol = actorCol + dCols[i];
                if (!BattleGrid.InBounds(candRow, candCol) || liveGrid.Get(candRow, candCol) != null)
                    continue;

                int immediateDistance = NearestEnemyManhattanDistance(liveGrid, actor, candRow, candCol);
                if (isMelee && immediateDistance > curDistanceToNearest)
                {
                    Debug.Log($"[AdvanceDiag] actor {actor.Name} at ({actorRow},{actorCol}): candidate ({candRow},{candCol}) " +
                        $"SKIPPED — retreat step (immediateDistance={immediateDistance} > curDistanceToNearest={curDistanceToNearest})");
                    continue;
                }

                var grid = new BattleGrid();
                var hp = new Dictionary<UnitData, float>();
                var ownUnits = new List<UnitData>();
                var enemyUnits = new List<UnitData>();
                CollectLivingMembers(liveGrid, ownArmy, grid, hp, ownUnits);
                CollectLivingMembers(liveGrid, enemyArmy, grid, hp, enemyUnits);
                if (!hp.ContainsKey(actor))
                    continue;

                // This round is spent moving to the candidate cell instead of acting normally.
                if (grid.TryFindPosition(actor, out int curRow, out int curCol))
                    grid.Set(curRow, curCol, null);
                grid.Set(candRow, candCol, actor);

                RunOneRound(grid, hp, BuildInterleavedOrder(ownUnits, enemyUnits, actor), magnitudes, null);

                if (!isMelee && hp[actor] <= 0f)
                {
                    Debug.Log($"[AdvanceDiag] actor {actor.Name} at ({actorRow},{actorCol}): candidate ({candRow},{candCol}) " +
                        $"SKIPPED — projected dead (hp<=0) before its own next turn from there");
                    continue; // didn't survive to get a real turn from here — not a real candidate
                }

                float damage = 0f;
                int distance = immediateDistance;
                if (hp[actor] > 0f)
                {
                    var damageDealt = new Dictionary<UnitData, float>();
                    RunOneRound(grid, hp, BuildInterleavedOrder(ownUnits, enemyUnits, null), magnitudes, damageDealt);

                    // Same "not a real candidate" rule as the movement-round check above, just
                    // applied to the round this whole lookahead exists to score — see the old
                    // comment history: skipped only for non-melee, where survivability still
                    // matters (a melee actor accepts dying here per isMelee's own comment above).
                    if (!isMelee && hp[actor] <= 0f)
                    {
                        Debug.Log($"[AdvanceDiag] actor {actor.Name} at ({actorRow},{actorCol}): candidate ({candRow},{candCol}) " +
                            $"SKIPPED — projected dead (hp<=0) during its own acting round from there");
                        continue;
                    }

                    damage = damageDealt.TryGetValue(actor, out float dealt) ? dealt : 0f;
                    distance = grid.TryFindPosition(actor, out int aRow, out int aCol)
                        ? NearestEnemyManhattanDistance(grid, actor, aRow, aCol)
                        : immediateDistance;
                }

                Debug.Log($"[AdvanceDiag] actor {actor.Name} at ({actorRow},{actorCol}, distToNearest={curDistanceToNearest}): " +
                    $"candidate ({candRow},{candCol}) projectedDamageDealt={damage} distToNearestAfter={distance} " +
                    $"(closer={distance < curDistanceToNearest}, farther={distance > curDistanceToNearest}) survivedHp={hp[actor]}");

                // Most damage dealt within the 2-round window wins; ties (usually both 0, nobody
                // gets there in time) fall back to the same "closer to the nearest enemy" the old
                // plain-greedy FindStepToward already used.
                if (damage > bestDamage || (damage == bestDamage && distance < bestDistance))
                {
                    bestDamage = damage;
                    bestDistance = distance;
                    bestStep = (candRow, candCol);
                }
            }

            Debug.Log($"[AdvanceDiag] actor {actor.Name}: chosen bestStep={(bestStep.HasValue ? bestStep.Value.ToString() : "null")} " +
                $"bestDamage={bestDamage} bestDistance={bestDistance}");
            return bestStep;
        }

        private static int NearestEnemyManhattanDistance(BattleGrid grid, UnitData actor, int row, int col)
        {
            int nearest = int.MaxValue;
            foreach (UnitData candidate in grid.AllUnits())
            {
                if (candidate.Owner == actor.Owner || !grid.TryFindPosition(candidate, out int candRow, out int candCol))
                    continue;
                int dist = Mathf.Abs(row - candRow) + Mathf.Abs(col - candCol);
                if (dist < nearest)
                    nearest = dist;
            }
            return nearest;
        }

        // Same one-round lookahead FindBestAdvanceStep already does for the live grid/ArmyData,
        // but scored against the shadow grid/hp state a SimulateRounds playout is already mid-way
        // through, instead of re-deriving positions/HP from the real UnitData — a unit already
        // damaged earlier in this same simulated playout needs to plan its advance around ITS
        // current shadow HP and the shadow board, not the pristine real one. Every trial clones
        // `grid`/`hp` (RunOneRound mutates both in place) so scoring one candidate direction can
        // never leak into another's, or into the real round this was called from.
        private static (int row, int col)? FindBestAdvanceStepInSim(BattleGrid grid, Dictionary<UnitData, float> hp,
            List<UnitData> order, UnitData actor, int actorRow, int actorCol, AbilityMagnitudes magnitudes)
        {
            int[] dRows = { -1, 1, 0, 0 };
            int[] dCols = { 0, 0, -1, 1 };

            // Same never-retreat/never-refuse-the-risk rule as the live FindBestAdvanceStep (see
            // its own comment) — kept in sync so AssessRetreat/ArrangeArmy's own projections don't
            // diverge from how melee actually behaves on a real turn.
            bool isMelee = actor.Range <= 2;
            int curDistanceToNearest = NearestEnemyManhattanDistance(grid, actor, actorRow, actorCol);

            var orderExcludingActor = new List<UnitData>();
            foreach (UnitData unit in order)
                if (unit != actor)
                    orderExcludingActor.Add(unit);

            (int row, int col)? bestStep = null;
            float bestDamage = -1f;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < 4; i++)
            {
                int candRow = actorRow + dRows[i];
                int candCol = actorCol + dCols[i];
                if (!BattleGrid.InBounds(candRow, candCol) || grid.Get(candRow, candCol) != null)
                    continue;

                int immediateDistance = NearestEnemyManhattanDistance(grid, actor, candRow, candCol);
                if (isMelee && immediateDistance > curDistanceToNearest)
                    continue;

                BattleGrid trialGrid = CloneGrid(grid);
                var trialHp = new Dictionary<UnitData, float>(hp);
                trialGrid.Set(actorRow, actorCol, null);
                trialGrid.Set(candRow, candCol, actor);

                // This round is spent moving to the candidate cell instead of acting normally.
                RunOneRound(trialGrid, trialHp, orderExcludingActor, magnitudes, null);

                if (!trialHp.TryGetValue(actor, out float actorHpAfter))
                    continue;
                if (!isMelee && actorHpAfter <= 0f)
                    continue; // didn't survive to get a real turn from here — not a real candidate

                float damage = 0f;
                int distance = immediateDistance;
                if (actorHpAfter > 0f)
                {
                    var damageDealt = new Dictionary<UnitData, float>();
                    RunOneRound(trialGrid, trialHp, order, magnitudes, damageDealt);

                    damage = damageDealt.TryGetValue(actor, out float dealt) ? dealt : 0f;
                    distance = trialGrid.TryFindPosition(actor, out int aRow, out int aCol)
                        ? NearestEnemyManhattanDistance(trialGrid, actor, aRow, aCol)
                        : immediateDistance;
                }

                if (damage > bestDamage || (damage == bestDamage && distance < bestDistance))
                {
                    bestDamage = damage;
                    bestDistance = distance;
                    bestStep = (candRow, candCol);
                }
            }

            return bestStep;
        }

        private static BattleGrid CloneGrid(BattleGrid source)
        {
            var clone = new BattleGrid();
            for (int r = 0; r < BattleGrid.Rows; r++)
                for (int c = 0; c < BattleGrid.Columns; c++)
                {
                    UnitData unit = source.Get(r, c);
                    if (unit != null)
                        clone.Set(r, c, unit);
                }
            return clone;
        }

        // Fate-duel spend decisions now live in FateDuelAi (BattleAttackPopupUI.RunAiTurn calls
        // into it directly) — kept out of this file to keep BattleAi.cs to arrangement/retreat/
        // tactical-move logic only.
    }
}
