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
        // Range-forced shape (PlaceRangeSplit) for both candidates. No literal stat thresholds
        // are hardcoded anywhere here; whichever shape actually comes out ahead in the
        // simulation wins.
        //
        // Two independent axes get searched, not just column choice: `centerOut` (see above) and
        // `frontMeleeCount` — how many of our own melee actually hold the front line versus fall
        // back into the back row alongside the ranged members (see PlaceTankAnchoredSplit's own
        // comment on why that's legal: nothing about the grid stops a melee unit from starting in
        // the back row, ArrangeArmy just never used to consider it). A melee unit held back
        // forfeits its own round-1 attack — nothing forces the AI to only ever field its full
        // melee line, per the user's own report that two spare 4/2s in a 3-unit melee line had no
        // way to get protected behind the one unit already anchoring the front. Every
        // (frontMeleeCount, centerOut) combination gets simulated exactly like the old
        // centerOut-only search; this is deliberately NOT a fixed rule like "only ever keep one
        // melee up front" — whichever combination's simulated 3-round outcome scores best wins,
        // even if that means the full melee line stays up front because holding anyone back
        // scored no better.
        //
        // Tie-break order when scores are within ScoreTieTolerance of each other (see that
        // constant's own comment — NOT bit-exact equality): (1) prefer MORE melee up front —
        // retreating a unit is only worth it when the simulation shows an actual benefit, never as
        // a zero-benefit default; (2) prefer centerOut — per the user's own call, a melee-only
        // front line usually plays out near-identically regardless of which column each member
        // occupies, so without this the center anchor the user expects (and the hero hiding
        // behind it, since heroColumn always follows frontOrder[0]) would almost never win.
        //
        // enemyLoss/ownLoss are each a Mathf.Clamp01 fraction of that side's own total army HP, so
        // score itself always lands in [-1, 1] — 0.015 reads as "differs by under 1.5% of either
        // side's own total HP pool over the full 3-round simulation", small enough that it's noise
        // from exactly which column a unit stands in rather than a real tactical edge (see
        // ArrangeArmy's own tie-break comment below for why that noise exists at all).
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

            int meleeCount = CountMelee(army);

            if (enemyArmy == null)
            {
                // No opposing roster to weigh candidates against (shouldn't happen in a real
                // battle, kept only as a safe fallback) — tank-anchored/left-packed with the full
                // melee line up front is still a strictly sound default over plain army-list
                // order even with zero information about the enemy, so skip straight to it
                // without any simulation to score against.
                PlaceTankAnchoredSplit(grid, army, frontRow, backRow, FormationPattern.CenterOut,
                    enemyArmy: null, frontMeleeCount: meleeCount);
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
            float bestScore = float.NegativeInfinity;
            FormationPattern bestPattern = FormationPattern.CenterOut;
            int bestFrontMeleeCount = meleeCount;

            int minFrontMeleeCount = meleeCount > 0 ? 1 : 0; // at least one melee anchors the front whenever there is one to anchor with
            for (int frontMeleeCount = minFrontMeleeCount; frontMeleeCount <= meleeCount; frontMeleeCount++)
            {
                foreach (FormationPattern pattern in formationCandidates)
                {
                    var trial = new BattleGrid();
                    PlaceTankAnchoredSplit(trial, army, frontRow, backRow, pattern, enemyArmy, frontMeleeCount);
                    PlaceRangeSplit(trial, enemyArmy, enemyFrontRow, enemyBackRow);

                    SimulationOutcome outcome = SimulateRounds(trial, army, enemyArmy, 3, magnitudes,
                        battleDefender, battleDefenderDefenseBonus, ArrangementSimulationTrials);

                    float ownLoss = ownTotalHp > 0f ? Mathf.Clamp01((ownTotalHp - outcome.OwnHpRemaining) / ownTotalHp) : 1f;
                    float enemyLoss = enemyTotalHp > 0f ? Mathf.Clamp01((enemyTotalHp - outcome.EnemyHpRemaining) / enemyTotalHp) : 1f;
                    float score = enemyLoss - ownLoss;

                    // Tie-break was originally gated on score == bestScore (exact float equality)
                    // — per the user's own report, the melee line essentially never actually landed
                    // centered in practice, because BattleTargetSelector's own reachability/targeting
                    // read is column-position-dependent (see its own comment), so two otherwise-
                    // equivalent formations differing only by which column the front line sits in
                    // almost never simulate to a bit-exact tie; whichever the search happened to try
                    // first (centerOut: false, frontMeleeCount at its lowest) then just won outright
                    // on a razor-thin, tactically meaningless score difference every single time,
                    // silently defeating the "prefer more melee up front, prefer centered" intent
                    // this whole search exists for. ScoreTieTolerance treats any difference this
                    // small as no real difference at all — small enough that a genuine tactical edge
                    // (a formation that actually trades hits differently) still wins outright, large
                    // enough to swallow the column-position noise that isn't a real edge.
                    bool withinTolerance = score > bestScore - ScoreTieTolerance;
                    bool preferredPattern = pattern == FormationPattern.CenterOut
                        && bestPattern != FormationPattern.CenterOut;
                    bool preferredOnTie = withinTolerance
                        && (frontMeleeCount > bestFrontMeleeCount
                            || (frontMeleeCount == bestFrontMeleeCount && preferredPattern));
                    if (score > bestScore || preferredOnTie)
                    {
                        bestPattern = pattern;
                        bestFrontMeleeCount = frontMeleeCount;
                    }
                    // Tracked independently of which candidate actually got selected above, so the
                    // tolerance window always anchors to the TRUE best score seen so far — never
                    // drifting downward step by step as successive near-ties each get accepted.
                    if (score > bestScore)
                        bestScore = score;
                }
            }

            BattleDebugLog.Write($"[ArrangeDiag] army \"{army.Name}\": " +
                $"frontlineCount={meleeCount} -> chosen pattern={bestPattern} frontCount={bestFrontMeleeCount} bestScore={bestScore}");
            PlaceTankAnchoredSplit(grid, army, frontRow, backRow, bestPattern, enemyArmy, bestFrontMeleeCount);
        }

        // Non-hero, Range<=2 members only — see PlaceTankAnchoredSplit's own melee/ranged split.
        private static int CountMelee(ArmyData army)
        {
            int count = 0;
            foreach (UnitData member in army.Members)
                if (member.IsGroundCombatant && member.Range <= 2)
                    count++;
            return count;
        }

        // Rough "how good a soak is this unit" score — Defense+HP is the base survivability, but
        // CeramicArmor's flat -1 to every hit and Berserk's own "getting hit makes it stronger"
        // trade both make a unit a better front-liner than raw stats alone suggest, so both get
        // folded in as a flat bonus rather than left for raw stats to under-rate them.
        private static float TankScore(UnitData unit)
        {
            float score = unit.Defense + Mathf.Max(0, unit.HitPointsCurrent);
            if (unit.HasAbility(UnitAbilities.CeramicArmor)) score += 2f;
            if (unit.HasAbility(UnitAbilities.Berserk)) score += 2f;
            return score;
        }

        // Column visit order for filling the front row: either plain left-to-right, or center
        // column first and alternating outward — see ArrangeArmy's own comment on why both get
        // tried. Whichever column comes first in this order is where the single tankiest melee
        // member (and therefore the hero standing behind it) ends up.
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
            if (BattlePlacementRules.CanPlace(unit, row, col, frontRow, backRow))
                grid.Set(row, col, unit);
        }

        // Tank-anchored shape: melee sorted tankiest-first, the top `frontMeleeCount` of them go
        // into `frontOrder` (so the tankiest lands on whichever column that order visits first),
        // primary Commander hero moved to stand directly behind that same column instead of the
        // fixed BattleGrid.HeroColumn; any additional heroes fill the remaining Back-row columns. Whichever melee DON'T make the front cut fall back into the same
        // back-row pool as the ranged members (see below) — see ArrangeArmy's own comment for why
        // that's a legal formation the search now considers, not just a leftover-overflow
        // accident. If there's no melee member to anchor on, the Commander falls back to
        // BattleGrid.HeroColumn and additional heroes remain beside it in the Back row.
        //
        // The front/back split itself is Range <= 2, not <= 1 — per the user's own report, a
        // Range-2 unit placed in the BACK row can't reach anything on round 1 at all (front row
        // to front row is 2 cells apart, back row to front row is 3 — see BattleGrid's row
        // layout), so a tanky Range-2 unit used to lose its opening turn walking up for nothing
        // instead of anchoring the front line it was tough enough to hold. Range 2 still reaches
        // the enemy front row perfectly well FROM this army's own front row, so it now competes
        // for a front column by TankScore exactly like a true melee (Range 1) member; only
        // Range >= 3 (genuinely usable from the back row without moving first) still goes back —
        // unless ArrangeArmy's own search decided to hold it back deliberately (frontMeleeCount).
        private static void PlaceTankAnchoredSplit(BattleGrid grid, ArmyData army, int frontRow, int backRow,
            FormationPattern pattern, ArmyData enemyArmy, int frontMeleeCount)
        {
            var heroes = new List<UnitData>();
            var melee = new List<UnitData>();
            var ranged = new List<UnitData>();
            foreach (UnitData member in army.Members)
            {
                if (member.IsHero) { heroes.Add(member); continue; }
                if (member.Range <= 2) melee.Add(member);
                else ranged.Add(member);
            }
            // Commander keeps the primary protected/anchored hero position. Additional heroes are
            // still real tactical pieces, but do not replace Commander for army-wide Fate/Initiative.
            UnitData commander = army.Commander;
            if (commander != null && heroes.Remove(commander))
                heroes.Insert(0, commander);

            melee.Sort((a, b) => TankScore(b).CompareTo(TankScore(a)));

            int meleeToFront = Mathf.Clamp(frontMeleeCount, 0, melee.Count);
            List<UnitData> frontMelee = melee.GetRange(0, meleeToFront);
            List<UnitData> heldBackMelee = melee.GetRange(meleeToFront, melee.Count - meleeToFront);

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
            var backPool = new List<UnitData>(ranged);
            backPool.AddRange(heldBackMelee);
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

        // Plain Range-forced shape with no tank-anchoring at all: melee front row left-to-right,
        // ranged back row left-to-right after all heroes, with Commander first at the fixed
        // BattleGrid.HeroColumn. Two jobs: (1)
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
            // AND, not OR — advancing into the open is a bigger commitment than holding back is,
            // so both horizons need to agree the fight is comfortably ours before this relaxes
            // ChooseAction's exposure caution (see FavorableForAdvance's own comment); shouldRetreat
            // stays OR since either horizon looking bad is already reason enough to be cautious.
            // Uses AdvanceMarginFraction, not RetreatMarginFraction — see that constant's own
            // comment for why the two directions need different bars.
            bool favorableForAdvance = !shouldRetreat
                && twoRoundMargin < -AdvanceMarginFraction && threeRoundMargin < -AdvanceMarginFraction;
            BattleDebugLog.Write($"[RetreatDiag] army \"{aiArmy?.Name}\" (hp={ownHp}) vs \"{enemyArmy?.Name}\" (hp={enemyHp}): " +
                $"2-round margin={twoRoundMargin:F3} 3-round margin={threeRoundMargin:F3} " +
                $"-> shouldRetreat={shouldRetreat} favorableForAdvance={favorableForAdvance}");
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
        private const int BattleSimulationTrials = 8;
        private const int ArrangementSimulationTrials = 4;

        public static SimulationOutcome SimulateRounds(BattleGrid liveGrid, ArmyData ownArmy, ArmyData enemyArmy, int rounds,
            AbilityMagnitudes magnitudes, ArmyData battleDefender = null, int battleDefenderDefenseBonus = 0,
            int simulationTrials = BattleSimulationTrials)
        {
            if (liveGrid == null || ownArmy == null || enemyArmy == null || rounds <= 0)
                return default;

            int trials = Mathf.Max(1, simulationTrials);
            int baseSeed = BuildSimulationSeed(ownArmy, enemyArmy);
            float ownHpSum = 0f;
            float enemyHpSum = 0f;

            for (int trial = 0; trial < trials; trial++)
            {
                var grid = new BattleGrid();
                var hp = new Dictionary<UnitData, float>();
                var ownUnits = new List<UnitData>();
                var enemyUnits = new List<UnitData>();
                CollectLivingMembers(liveGrid, ownArmy, grid, hp, ownUnits);
                CollectLivingMembers(liveGrid, enemyArmy, grid, hp, enemyUnits);

                var simulatedAttack = new Dictionary<UnitData, int>();
                var simulatedDefense = new Dictionary<UnitData, int>();
                foreach (UnitData unit in ownUnits)
                {
                    simulatedAttack[unit] = unit.Attack;
                    simulatedDefense[unit] = unit.IsHero ? Mathf.Max(0, unit.FateMax) : unit.Defense;
                }
                foreach (UnitData unit in enemyUnits)
                {
                    simulatedAttack[unit] = unit.Attack;
                    simulatedDefense[unit] = unit.IsHero ? Mathf.Max(0, unit.FateMax) : unit.Defense;
                }

                var fateByArmy = new Dictionary<ArmyData, int>
                {
                    [ownArmy] = Mathf.Max(0, BattleTurnOrder.LivingCommanderOnGrid(grid, ownArmy)?.Fate ?? 0),
                    [enemyArmy] = Mathf.Max(0, BattleTurnOrder.LivingCommanderOnGrid(grid, enemyArmy)?.Fate ?? 0),
                };
                var rng = new System.Random(unchecked(baseSeed + trial * 7919));
                var previousPositions = new Dictionary<UnitData, Vector2Int>();

                for (int round = 0; round < rounds; round++)
                {
                    List<UnitData> order = BattleTurnOrder.BuildOrder(grid, ownArmy, enemyArmy,
                        unchecked(baseSeed * 31 + trial * 131 + round + 1));
                    RunOneRound(grid, hp, order, magnitudes, null, smartAdvance: true,
                        battleDefender: battleDefender, battleDefenderDefenseBonus: battleDefenderDefenseBonus,
                        rng: rng, fateByArmy: fateByArmy, simulatedAttack: simulatedAttack,
                        simulatedDefense: simulatedDefense, ownArmy: ownArmy, enemyArmy: enemyArmy,
                        previousPositions: previousPositions);
                    if (!IsSimulationCombatCapable(grid, hp, ownArmy)
                        || !IsSimulationCombatCapable(grid, hp, enemyArmy))
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
            AbilityMagnitudes magnitudes, Dictionary<UnitData, float> damageDealtByUnit, bool smartAdvance = false,
            ArmyData battleDefender = null, int battleDefenderDefenseBonus = 0, System.Random rng = null,
            Dictionary<ArmyData, int> fateByArmy = null, Dictionary<UnitData, int> simulatedAttack = null,
            Dictionary<UnitData, int> simulatedDefense = null, ArmyData ownArmy = null, ArmyData enemyArmy = null,
            Dictionary<UnitData, Vector2Int> previousPositions = null)
        {
            var suppressed = new HashSet<UnitData>();
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

                if (BattleTargetSelector.TryFindBestReachableTarget(grid, hp, actor, row, col, magnitudes, order, i,
                    out UnitData target, out float expectedDamage, battleDefender, battleDefenderDefenseBonus,
                    simulatedAttack, simulatedDefense))
                {
                    float damage;
                    bool suppressTarget = false;
                    if (rng != null)
                    {
                        BattleSimExchangeOutcome exchange = ResolveSimulatedExchange(
                            grid, actor, target, hp, magnitudes, battleDefender,
                            battleDefenderDefenseBonus, rng, fateByArmy, simulatedAttack,
                            simulatedDefense, ownArmy, enemyArmy);
                        damage = exchange.Damage;

                        float targetHp = hp[target];
                        int transientAttack = simulatedAttack != null
                            && simulatedAttack.TryGetValue(target, out int atk) ? atk : target.Attack;
                        int transientDefense = simulatedDefense != null
                            && simulatedDefense.TryGetValue(target, out int def)
                            ? def : (target.IsHero ? Mathf.Max(0, target.FateMax) : target.Defense);
                        BattleSimulationKernel.ApplyPrimaryOutcome(
                            exchange, actor.Abilities, target.Abilities,
                            ref targetHp, ref transientAttack, ref transientDefense,
                            magnitudes, out suppressTarget);
                        hp[target] = targetHp;
                        if (simulatedAttack != null) simulatedAttack[target] = transientAttack;
                        if (simulatedDefense != null) simulatedDefense[target] = transientDefense;
                    }
                    else
                    {
                        damage = expectedDamage;
                        hp[target] -= damage;
                    }

                    if (damageDealtByUnit != null)
                        damageDealtByUnit[actor] = damageDealtByUnit.TryGetValue(actor, out float dealt)
                            ? dealt + damage : damage;

                    if (rng != null && suppressTarget && order.IndexOf(target) > i)
                        suppressed.Add(target);

                    ApplySimSplash(grid, hp, actor, target, damage, magnitudes, rng,
                        simulatedAttack, simulatedDefense);
                    if (hp[target] <= 0f && grid.TryFindPosition(target, out int tRow, out int tCol))
                    {
                        grid.Set(tRow, tCol, null);
                        // A killed hero stops commanding immediately in the shadow battle. If the
                        // army has another living hero, that hero becomes Commander for subsequent
                        // rounds/exchanges and brings its own remaining Fate pool.
                        if (target.IsHero && fateByArmy != null)
                        {
                            ArmyData targetArmy = FindSimulationArmy(target, ownArmy, enemyArmy);
                            UnitData replacement = BattleTurnOrder.LivingCommanderOnGrid(grid, targetArmy);
                            if (targetArmy != null)
                                fateByArmy[targetArmy] = Mathf.Max(0, replacement?.Fate ?? 0);
                        }
                    }
                    if (ownArmy != null && enemyArmy != null
                        && (!IsSimulationCombatCapable(grid, hp, ownArmy)
                            || !IsSimulationCombatCapable(grid, hp, enemyArmy)))
                        return;
                    continue;
                }

                (int row, int col)? step = smartAdvance
                    ? FindBestAdvanceStepInSim(grid, hp, order, actor, row, col, magnitudes,
                        battleDefender, battleDefenderDefenseBonus, simulatedAttack, simulatedDefense)
                        ?? FindStepToward(grid, actor, row, col)
                    : FindStepToward(grid, actor, row, col);
                if (step != null)
                {
                    int curDist = NearestEnemyManhattanDistance(grid, actor, row, col);
                    if (previousPositions != null
                        && previousPositions.TryGetValue(actor, out Vector2Int previous)
                        && previous.x == step.Value.row && previous.y == step.Value.col)
                    {
                        (int row, int col)? toward = FindClosingStepExcluding(
                            grid, actor, row, col, previous.x, previous.y);
                        if (toward.HasValue)
                            step = toward;
                    }

                    if (previousPositions != null)
                        previousPositions[actor] = new Vector2Int(row, col);
                    grid.Set(row, col, null);
                    grid.Set(step.Value.row, step.Value.col, actor);
                }
            }
        }

        private static BattleSimExchangeOutcome ResolveSimulatedExchange(BattleGrid grid, UnitData actor, UnitData target,
            Dictionary<UnitData, float> hp, AbilityMagnitudes magnitudes, ArmyData battleDefender,
            int battleDefenderDefenseBonus, System.Random rng, Dictionary<ArmyData, int> fateByArmy,
            Dictionary<UnitData, int> simulatedAttack, Dictionary<UnitData, int> simulatedDefense,
            ArmyData ownArmy, ArmyData enemyArmy)
        {
            int attackPool = simulatedAttack != null && simulatedAttack.TryGetValue(actor, out int simAtk)
                ? simAtk : actor.Attack;
            int defensePool = simulatedDefense != null && simulatedDefense.TryGetValue(target, out int simDef)
                ? simDef : (target.IsHero ? Mathf.Max(0, target.FateMax) : target.Defense);
            defensePool += BattleProtectionRules.GetTotalDefenseBonus(
                grid, target, battleDefender, battleDefenderDefenseBonus);

            ArmyData actorArmy = FindSimulationArmy(actor, ownArmy, enemyArmy);
            ArmyData targetArmy = FindSimulationArmy(target, ownArmy, enemyArmy);
            int actorFate = actorArmy != null && fateByArmy != null
                && fateByArmy.TryGetValue(actorArmy, out int af) ? af : 0;
            int targetFate = targetArmy != null && fateByArmy != null
                && fateByArmy.TryGetValue(targetArmy, out int tf) ? tf : 0;

            BattleSimExchangeOutcome outcome = BattleSimulationKernel.ResolveExchange(
                attackPool, defensePool, actor.Abilities, target.TypeTags, target.Abilities,
                ref actorFate, ref targetFate,
                hp.TryGetValue(target, out float targetHp) ? Mathf.CeilToInt(targetHp) : int.MaxValue,
                magnitudes, rng);

            if (actorArmy != null && fateByArmy != null)
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
            List<UnitData> turnOrder, int turnIndex, bool favorableFight = false,
            ArmyData battleDefender = null, int battleDefenderDefenseBonus = 0,
            Dictionary<UnitData, Vector2Int> previousPositions = null)
        {
            var passAction = new AiAction { Kind = AiActionKind.Pass, Reason = AiThoughtCategory.CautiousWait };
            if (grid == null || actor == null || waitStreak == null
                || !grid.TryFindPosition(actor, out int actorRow, out int actorCol))
                return passAction;

            if (BattleTargetSelector.TryChooseAttackTarget(grid, actor, actorRow, actorCol, magnitudes,
                turnOrder, turnIndex, out AiAction attackAction, battleDefender, battleDefenderDefenseBonus))
            {
                waitStreak[actor] = 0;
                previousPositions?.Remove(actor);
                return attackAction;
            }

            if (BattleDebugLog.Verbose) BattleDebugLog.Write($"[MoveDiag] actor {actor.Name} at ({actorRow},{actorCol}): no attack target in range, evaluating a move");

            bool alreadyExposed = IsExposedToEnemy(grid, actorRow, actorCol, actor);
            bool isMelee = actor.Range <= 1;
            (int row, int col)? bestStep = FindBestAdvanceStep(grid, ownArmy, enemyArmy, actor, actorRow, actorCol, magnitudes,
                battleDefender, battleDefenderDefenseBonus);
            (int row, int col)? step = bestStep ?? FindStepToward(grid, actor, actorRow, actorCol);
            if (BattleDebugLog.Verbose) BattleDebugLog.Write($"[MoveDiag] actor {actor.Name}: FindBestAdvanceStep={(bestStep.HasValue ? bestStep.Value.ToString() : "null")} " +
                $"finalStep={(step.HasValue ? step.Value.ToString() : "null")} (fallback used: {!bestStep.HasValue})");

            if (step == null)
            {
                waitStreak[actor] = 0;
                BattleDebugLog.Write($"[MoveDiag] actor {actor.Name}: no legal step at all -> Pass");
                return passAction;
            }

            int streak = waitStreak.TryGetValue(actor, out int s) ? s : 0;
            bool forceAdvance = streak >= MaxWaitStreak;

            // A step that doesn't actually close on the nearest enemy (sideways shuffle) is a
            // stall just like a wait: FindBestAdvanceStep's lookahead simulates the opponent
            // walking greedily into range, but the real (risk-averse) opponent may shuffle
            // sideways too, so two ranged units could trade sideways steps forever. Count such
            // steps toward the same streak and, once it's exhausted, take a real closing step.
            int curDist = NearestEnemyManhattanDistance(grid, actor, actorRow, actorCol);
            bool closes = NearestEnemyManhattanDistance(grid, actor, step.Value.row, step.Value.col) < curDist;
            // Once the army has chosen to keep fighting, a Range-1 unit's local tactical
            // policy is commitment, not self-preservation: if ANY legal adjacent step reduces
            // distance to a ground target, that closing step outranks a sideways/no-progress
            // lookahead result immediately. Retreat remains an army-level decision made by
            // AssessRetreat before the round; ChooseAction must not quietly undo it unit-by-unit.
            if (isMelee && !closes)
            {
                (int row, int col)? toward = FindStepToward(grid, actor, actorRow, actorCol);
                if (toward.HasValue && NearestEnemyManhattanDistance(grid, actor, toward.Value.row, toward.Value.col) < curDist)
                {
                    step = toward;
                    closes = true;
                    if (BattleDebugLog.Verbose)
                        BattleDebugLog.Write($"[MoveDiag] actor {actor.Name}: Range-1 commitment overrides non-closing lookahead -> {step.Value}");
                }
            }
            else if (!closes && forceAdvance)
            {
                (int row, int col)? toward = FindStepToward(grid, actor, actorRow, actorCol);
                if (toward.HasValue && NearestEnemyManhattanDistance(grid, actor, toward.Value.row, toward.Value.col) < curDist)
                {
                    step = toward;
                    closes = true;
                }
            }

            // Break the common ranged A->B->A oscillation immediately instead of waiting for
            // MaxWaitStreak. A reversal remains legal when there is no real closing alternative,
            // so this cannot strand a unit that only has one escape/path cell.
            if (previousPositions != null
                && previousPositions.TryGetValue(actor, out Vector2Int previous)
                && previous.x == step.Value.row && previous.y == step.Value.col)
            {
                (int row, int col)? toward = FindClosingStepExcluding(
                    grid, actor, actorRow, actorCol, previous.x, previous.y);
                if (toward.HasValue)
                {
                    step = toward;
                    closes = true;
                    if (BattleDebugLog.Verbose)
                        BattleDebugLog.Write($"[MoveDiag] actor {actor.Name}: immediate reversal ({actorRow},{actorCol})->({previous.x},{previous.y}) rejected; closing via {step.Value}");
                }
            }
            bool stepExposes = !alreadyExposed && IsExposedToEnemy(grid, step.Value.row, step.Value.col, actor);
            // Close-combat units (Range 1) never hesitates over exposure risk before closing distance — per
            // the user's own call, there's no point in a melee unit hanging back to avoid a
            // single round of return fire when the whole point of the unit is to reach melee
            // range. FindBestAdvanceStep/FindStepToward already guarantee `step` itself is never
            // a retreat for a melee actor (see their own isMelee handling), so this only ever
            // skips the WAIT-and-eat-the-risk-later behavior, never turns a real retreat into an
            // advance.

            if (BattleDebugLog.Verbose) BattleDebugLog.Write($"[MoveDiag] actor {actor.Name}: alreadyExposed={alreadyExposed} stepExposes={stepExposes} " +
                $"waitStreak={streak} forceAdvance={forceAdvance} favorableFight={favorableFight} isMelee={isMelee} closes={closes} " +
                $"-> {((isMelee || alreadyExposed || !stepExposes || forceAdvance || favorableFight) ? "MOVE" : "WAIT")} to {step.Value}");

            if (isMelee || alreadyExposed || !stepExposes || forceAdvance || favorableFight)
            {
                waitStreak[actor] = closes ? 0 : streak + 1;
                if (previousPositions != null)
                    previousPositions[actor] = new Vector2Int(actorRow, actorCol);
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
                // Heroes may be attacked, but never take a BattleTurnOrder action of their own,
                // so they must not make another unit think a destination is under return fire.
                if (!candidate.IsGroundCombatant || candidate.Owner == actor.Owner)
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
        // tactical target (combatant or hero) is currently closest. Null if there's nowhere legal to go.
        private static (int row, int col)? FindStepToward(BattleGrid grid, UnitData actor, int actorRow, int actorCol)
        {
            UnitData nearestEnemy = null;
            int nearestRow = -1, nearestCol = -1;
            int nearestDist = int.MaxValue;
            foreach (UnitData candidate in grid.AllUnits())
            {
                if (candidate.Owner == actor.Owner)
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
            bool isMelee = actor.Range <= 1;
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

        private static (int row, int col)? FindClosingStepExcluding(BattleGrid grid, UnitData actor,
            int actorRow, int actorCol, int excludedRow, int excludedCol)
        {
            int currentDistance = NearestEnemyManhattanDistance(grid, actor, actorRow, actorCol);
            (int row, int col)? best = null;
            int bestDistance = currentDistance;
            int[] dRows = { -1, 1, 0, 0 };
            int[] dCols = { 0, 0, -1, 1 };
            for (int i = 0; i < 4; i++)
            {
                int row = actorRow + dRows[i];
                int col = actorCol + dCols[i];
                if ((row == excludedRow && col == excludedCol)
                    || !BattleGrid.InBounds(row, col) || grid.Get(row, col) != null)
                    continue;
                int distance = NearestEnemyManhattanDistance(grid, actor, row, col);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
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
            int actorRow, int actorCol, AbilityMagnitudes magnitudes, ArmyData battleDefender,
            int battleDefenderDefenseBonus)
        {
            if (liveGrid == null || ownArmy == null || enemyArmy == null)
                return null;

            int[] dRows = { -1, 1, 0, 0 };
            int[] dCols = { 0, 0, -1, 1 };

            // Close-combat units (Range 1) never retreats — per the user's own call, there's no tactical
            // point in a melee unit backing away from a fight it's trying to close on, so a step
            // that lands farther from the nearest enemy than staying put isn't a real candidate
            // at all, and dying before its own next turn no longer disqualifies a candidate either
            // (a melee unit accepts that risk rather than dance sideways forever to avoid it —
            // see the user's own report that the old death-pruning left only sideways/no-progress
            // steps standing almost every turn). Non-melee units keep the old risk-averse
            // behavior unchanged.
            bool isMelee = actor.Range <= 1;
            int curDistanceToNearest = NearestEnemyManhattanDistance(liveGrid, actor, actorRow, actorCol);

            (int row, int col)? bestStep = null;
            float bestDamage = -1f;
            int bestDistance = int.MaxValue;
            bool bestCloses = false;

            for (int i = 0; i < 4; i++)
            {
                int candRow = actorRow + dRows[i];
                int candCol = actorCol + dCols[i];
                if (!BattleGrid.InBounds(candRow, candCol) || liveGrid.Get(candRow, candCol) != null)
                    continue;

                int immediateDistance = NearestEnemyManhattanDistance(liveGrid, actor, candRow, candCol);
                if (isMelee && immediateDistance > curDistanceToNearest)
                {
                    if (BattleDebugLog.Verbose) BattleDebugLog.Write($"[AdvanceDiag] actor {actor.Name} at ({actorRow},{actorCol}): candidate ({candRow},{candCol}) " +
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

                RunOneRound(grid, hp, BuildInitiativeOrderExcluding(grid, ownArmy, enemyArmy, actor,
                    unchecked(BuildSimulationSeed(ownArmy, enemyArmy) * 31 + 101)), magnitudes, null,
                    battleDefender: battleDefender, battleDefenderDefenseBonus: battleDefenderDefenseBonus);

                if (!isMelee && hp[actor] <= 0f)
                {
                    if (BattleDebugLog.Verbose) BattleDebugLog.Write($"[AdvanceDiag] actor {actor.Name} at ({actorRow},{actorCol}): candidate ({candRow},{candCol}) " +
                        $"SKIPPED — projected dead (hp<=0) before its own next turn from there");
                    continue; // didn't survive to get a real turn from here — not a real candidate
                }

                float damage = 0f;
                int distance = immediateDistance;
                if (hp[actor] > 0f)
                {
                    var damageDealt = new Dictionary<UnitData, float>();
                    RunOneRound(grid, hp, BattleTurnOrder.BuildOrder(grid, ownArmy, enemyArmy,
                        unchecked(BuildSimulationSeed(ownArmy, enemyArmy) * 31 + 102)), magnitudes, damageDealt,
                        battleDefender: battleDefender, battleDefenderDefenseBonus: battleDefenderDefenseBonus);

                    // Same "not a real candidate" rule as the movement-round check above, just
                    // applied to the round this whole lookahead exists to score — see the old
                    // comment history: skipped only for non-melee, where survivability still
                    // matters (a melee actor accepts dying here per isMelee's own comment above).
                    if (!isMelee && hp[actor] <= 0f)
                    {
                        if (BattleDebugLog.Verbose) BattleDebugLog.Write($"[AdvanceDiag] actor {actor.Name} at ({actorRow},{actorCol}): candidate ({candRow},{candCol}) " +
                            $"SKIPPED — projected dead (hp<=0) during its own acting round from there");
                        continue;
                    }

                    damage = damageDealt.TryGetValue(actor, out float dealt) ? dealt : 0f;
                    distance = grid.TryFindPosition(actor, out int aRow, out int aCol)
                        ? NearestEnemyManhattanDistance(grid, actor, aRow, aCol)
                        : immediateDistance;
                }

                if (BattleDebugLog.Verbose) BattleDebugLog.Write($"[AdvanceDiag] actor {actor.Name} at ({actorRow},{actorCol}, distToNearest={curDistanceToNearest}): " +
                    $"candidate ({candRow},{candCol}) projectedDamageDealt={damage} distToNearestAfter={distance} " +
                    $"(closer={distance < curDistanceToNearest}, farther={distance > curDistanceToNearest}) survivedHp={hp[actor]}");

                // Range-1 commitment is lexicographic: a step that closes NOW always beats a
                // sideways/no-progress step, regardless of projected two-round damage. Among two
                // equally-closing (or equally-non-closing) candidates, retain the tactical
                // lookahead: most projected damage first, then shortest resulting distance.
                bool candidateCloses = immediateDistance < curDistanceToNearest;
                bool betterClosingClass = isMelee && candidateCloses && !bestCloses;
                bool sameClosingClass = !isMelee || candidateCloses == bestCloses;
                if (betterClosingClass
                    || (sameClosingClass && (damage > bestDamage
                        || (damage == bestDamage && distance < bestDistance))))
                {
                    bestDamage = damage;
                    bestDistance = distance;
                    bestCloses = candidateCloses;
                    bestStep = (candRow, candCol);
                }
            }

            if (BattleDebugLog.Verbose) BattleDebugLog.Write($"[AdvanceDiag] actor {actor.Name}: chosen bestStep={(bestStep.HasValue ? bestStep.Value.ToString() : "null")} " +
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
            List<UnitData> order, UnitData actor, int actorRow, int actorCol, AbilityMagnitudes magnitudes,
            ArmyData battleDefender, int battleDefenderDefenseBonus,
            Dictionary<UnitData, int> simulatedAttack = null, Dictionary<UnitData, int> simulatedDefense = null)
        {
            int[] dRows = { -1, 1, 0, 0 };
            int[] dCols = { 0, 0, -1, 1 };

            // Same never-retreat/never-refuse-the-risk rule as the live FindBestAdvanceStep (see
            // its own comment) — kept in sync so AssessRetreat/ArrangeArmy's own projections don't
            // diverge from how melee actually behaves on a real turn.
            bool isMelee = actor.Range <= 1;
            int curDistanceToNearest = NearestEnemyManhattanDistance(grid, actor, actorRow, actorCol);

            var orderExcludingActor = new List<UnitData>();
            foreach (UnitData unit in order)
                if (unit != actor)
                    orderExcludingActor.Add(unit);

            (int row, int col)? bestStep = null;
            float bestDamage = -1f;
            int bestDistance = int.MaxValue;
            bool bestCloses = false;

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
                var trialAttack = simulatedAttack != null
                    ? new Dictionary<UnitData, int>(simulatedAttack) : null;
                var trialDefense = simulatedDefense != null
                    ? new Dictionary<UnitData, int>(simulatedDefense) : null;
                trialGrid.Set(actorRow, actorCol, null);
                trialGrid.Set(candRow, candCol, actor);

                // This round is spent moving to the candidate cell instead of acting normally.
                RunOneRound(trialGrid, trialHp, orderExcludingActor, magnitudes, null,
                    battleDefender: battleDefender, battleDefenderDefenseBonus: battleDefenderDefenseBonus,
                    simulatedAttack: trialAttack, simulatedDefense: trialDefense);

                if (!trialHp.TryGetValue(actor, out float actorHpAfter))
                    continue;
                if (!isMelee && actorHpAfter <= 0f)
                    continue; // didn't survive to get a real turn from here — not a real candidate

                float damage = 0f;
                int distance = immediateDistance;
                if (actorHpAfter > 0f)
                {
                    var damageDealt = new Dictionary<UnitData, float>();
                    RunOneRound(trialGrid, trialHp, order, magnitudes, damageDealt,
                        battleDefender: battleDefender, battleDefenderDefenseBonus: battleDefenderDefenseBonus,
                        simulatedAttack: trialAttack, simulatedDefense: trialDefense);

                    damage = damageDealt.TryGetValue(actor, out float dealt) ? dealt : 0f;
                    distance = trialGrid.TryFindPosition(actor, out int aRow, out int aCol)
                        ? NearestEnemyManhattanDistance(trialGrid, actor, aRow, aCol)
                        : immediateDistance;
                }

                // Keep simulation behavior aligned with live ChooseAction: after the side has
                // committed to the fight, Range-1 units prefer any immediate closing step over a
                // no-progress step. This keeps ArrangeArmy/AssessRetreat projections honest.
                bool candidateCloses = immediateDistance < curDistanceToNearest;
                bool betterClosingClass = isMelee && candidateCloses && !bestCloses;
                bool sameClosingClass = !isMelee || candidateCloses == bestCloses;
                if (betterClosingClass
                    || (sameClosingClass && (damage > bestDamage
                        || (damage == bestDamage && distance < bestDistance))))
                {
                    bestDamage = damage;
                    bestDistance = distance;
                    bestCloses = candidateCloses;
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
