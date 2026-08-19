using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using Game.Units;

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
        // One-sided comparison — kept only for AiTurnController's own "even a theoretical fully-
        // merged army still couldn't out-muscle their defense" dead-end check (MaxPossibleAttack
        // isn't a real army that would ever take return fire, just a hypothetical ceiling, so a
        // two-sided Score doesn't apply there). Every real "should we commit to this fight"
        // decision uses Score/IsWorthIt instead.
        public static bool Beats(float attackerSum, float defenderSum) => attackerSum > defenderSum;

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

        // Graded two-sided net-advantage margin — see this class's own comment for why this stops
        // short of a full BattleAi.SimulateRounds playout. Positive = worth it, magnitude = how
        // lopsided; `enemyDefense`/`enemyAttack` are always the OTHER side's remembered aggregate
        // sums (KnownEnemySighting.DefenseSum/AttackSum, or a Hex Event guard's card-stat sums —
        // `enemyDefense` should already include HexDefenseBonus where the caller has a hex to add
        // it from, same as the old flat comparison always did).
        public static float Score(ArmyData attacker, float enemyDefense, float enemyAttack)
        {
            float ourEdge = AttackSum(attacker) - enemyDefense; // can we get through their defense
            float theirEdge = enemyAttack - DefenseSum(attacker); // how hard they hit back through ours
            return ourEdge - theirEdge;
        }

        public static bool IsWorthIt(ArmyData attacker, float enemyDefense, float enemyAttack) => Score(attacker, enemyDefense, enemyAttack) > 0f;

        // Minimal per-defender read for the coverage check below — just enough to reuse the same
        // expected-damage step BattleTargetSelector.TryScoreTarget already uses for a real attack
        // pick (rawExpected = Attack*0.5 − Defense*0.5), plus the one ability that started this:
        // CeramicArmor's flat reduction. Deliberately NOT a full UnitData/ability set — this is the
        // "adequate, not battle-detailed" version (per the project owner's own call): every other
        // ability (Hyperkinetic/Pyrokinetic/CriticalDamage — all attacker-side bonuses) is left out
        // on purpose. Skipping those can only make CanDamage MORE cautious than a real hit would
        // be, never falsely confident — the safe direction for a rough pre-contact check to be
        // wrong in, unlike skipping the defender's own CeramicArmor would be.
        public readonly struct DefenderProfile
        {
            public readonly float Defense;
            public readonly bool HasCeramicArmor;

            public DefenderProfile(float defense, bool hasCeramicArmor)
            {
                Defense = defense;
                HasCeramicArmor = hasCeramicArmor;
            }
        }

        // Same rawExpected half-stat step as BattleTargetSelector.TryScoreTarget, CeramicArmor's
        // flat reduction applied on top if the defender carries it (AbilityMagnitudes.Default —
        // this runs long before any BattleAttackPopupUI/live battle exists to read a tuned value
        // from). No FloorToInt/ApplyAbilityModifiers integer rounding here on purpose — this only
        // ever asks "positive or not", so the exact rounding rule doesn't change the answer.
        private static bool CanDamage(float attack, DefenderProfile defender)
        {
            float expected = attack * 0.5f - defender.Defense * 0.5f;
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
        public static bool CanDamageAll(ArmyData attacker, IReadOnlyCollection<DefenderProfile> defenders)
        {
            if (defenders == null || defenders.Count == 0)
                return true;
            List<UnitData> ourUnits = attacker?.Members.Where(m => !m.IsHero).ToList();
            if (ourUnits == null || ourUnits.Count == 0)
                return false;
            foreach (DefenderProfile defender in defenders)
                if (!ourUnits.Any(u => CanDamage(u.Attack, defender)))
                    return false;
            return true;
        }

        // Score/IsWorthIt's own two-sided net-edge margin, PLUS the coverage gate above — both
        // need to pass. `defenders` is optional (pass null/empty where no per-unit read is
        // available at all) so existing callers keep working unchanged.
        public static bool IsWorthIt(ArmyData attacker, float enemyDefense, float enemyAttack, IReadOnlyCollection<DefenderProfile> defenders)
            => IsWorthIt(attacker, enemyDefense, enemyAttack) && CanDamageAll(attacker, defenders);

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
