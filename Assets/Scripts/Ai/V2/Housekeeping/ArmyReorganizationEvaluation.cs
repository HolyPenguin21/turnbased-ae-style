using System;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;

namespace Game.Ai.V2
{
    public static partial class ArmyReorganizationPlanner
    {
        private static Outcome Evaluate(VState s)
        {
            int garrisonDeficit = 0;
            int legality = 0;
            int operatorExposure = 0;
            int preparationSlotWaste = 0;
            int singles = 0;
            int nonViable = 0;
            int commandWaste = 0;
            IReadOnlyList<WorthIt.DefendingArmy> commandContext = CommandContext(s);
            float composition = 0f;
            var formationStrengths = new List<float>();

            // Formation leadership: a "benched" hero sits in a garrison or a lone-hero container; a heroless
            // viable field formation is a fixable defect only when such a hero exists on the hex.
            int benchedCombatCapable = 0;
            int unledViableFields = 0;
            int supportLedWhileCombatBenched = 0;

            // A lone free hero is a bench, not a defect, until a known enemy can reach this hex within
            // activeDefenceWithdrawMaxEnemyEta turns; then it shelters in the local garrison (exposed like an
            // operator outside it) while the garrison has a slot and no army of the hex holds a fighter: the
            // hardest combat-capable army fights first (BattleInitiator.CompareDefenderHardness), so beside
            // any fighter a hero-only army is safe.
            bool shelterLoneHeroes = s.ThreatBenchmarks.Any(t => !t.HiddenFromUs
                    && t.EtaToGroup <= AiConfigV2.activeDefenceWithdrawMaxEnemyEta)
                && !s.Roster.Values.Any(r => r.Any(u => u != null && u.IsGroundCombatant))
                && s.Meta.Any(kv => kv.Value.IsGarrison && kv.Value.CanReceive
                    && ReorgViability.Capacity(s.Roster[kv.Key], true) > s.Roster[kv.Key].Count);

            foreach (int id in s.Meta.Keys.OrderBy(x => x))
            {
                ReorgContainer meta = s.Meta[id];
                List<ReorgUnit> units = s.Roster[id];

                if (ReorgViability.Capacity(units, meta.IsGarrison) < units.Count)
                    legality++;
                if (!meta.IsGarrison)
                {
                    // A hero-only bench on the hex keeps them where they serve (IsHexBench).
                    if (!ReorgViability.IsHexBench(meta, units))
                        operatorExposure += units.Count(u => u != null
                            && (u.IsDevelopmentOperator || u.IsGarrisonHero));
                    if (shelterLoneHeroes && meta.IsMutableGround && meta.CanDonate
                        && units.Count == 1 && units[0] != null && units[0].IsHero
                        && !units[0].IsCommitted && !units[0].IsDevelopmentOperator
                        && !units[0].IsGarrisonHero)
                        operatorExposure++;
                }

                if (meta.CanReorderCommander)
                    commandWaste += CommanderMismatch(units, meta, commandContext);
                if (meta.MayReleaseExcessHeroes)
                    preparationSlotWaste += HeroSlotWaste(units, keepGarrisonHeroes: false);
                else if (meta.IsMutableGround && meta.CanDonate)
                    preparationSlotWaste += HeroSlotWaste(units, keepGarrisonHeroes: true);
                if (meta.PreparationTargetKeys != null)
                    preparationSlotWaste += ReorgViability.PreparationRosterPenalty(meta, units,
                        s.Meta.Select(kv => new KeyValuePair<ReorgContainer, List<ReorgUnit>>(
                            kv.Value, s.Roster[kv.Key])));

                if (meta.IsGarrison)
                {
                    int nonHero = units.Count(u => u.IsGroundCombatant);
                    garrisonDeficit += Math.Max(0, meta.GarrisonNonHeroFloor - nonHero);
                    if (units.Count == 0 && meta.GarrisonNonHeroFloor > 0)
                        garrisonDeficit++;
                    if (meta.CanChangeComposition)
                    {
                        benchedCombatCapable += units.Count(u => u != null && u.IsHero
                            && u.HeroRole != HeroOperationalRole.SupportOperator
                            && GarrisonMayRelease(units, u, meta));
                        // The garrison's own strength and composition are no reason to move bodies (a sound free army
                        // would be folded into it, burying its commander); its floor stays (GarrisonDeficit, the release
                        // rule). The defence of the hex is the night fold's.
                    }
                    continue;
                }

                // An empty reusable army is neutral: filled, it is a normal field formation; empty, it adds
                // neither a defect nor a zero-strength entry that would pressure the planner to seed it. A
                // mission receiver is scored as the formation it is (fixable by inbound members only), but a hero
                // in it is never benched: a claimed container gives nobody away.
                if (!meta.IsScoredField || units.Count == 0)
                    continue;

                if (!meta.SingletonExempt && ReorgViability.IsSingletonShape(units))
                    singles++;

                bool loneHero = units.Count == 1 && units[0].IsHero;
                if (loneHero && meta.CanDonate && units[0].HeroRole != HeroOperationalRole.SupportOperator
                    && !units[0].IsGarrisonHero)
                    benchedCombatCapable++;

                // A commander/worker waiting for a task is a bench, not a weak fighting army.
                // Folding three zero-power hero benches into one must not improve NonViable.
                if (!units.Any(u => u != null && u.IsGroundCombatant))
                    continue;

                if (ReorgViability.IsViable(units))
                {
                    formationStrengths.Add(ReorgViability.EffectivePower(units));
                    composition += ReorgViability.CompositionQuality(units);

                    ReorgUnit commander = units.FirstOrDefault(u => u.IsHero);
                    if (commander == null && units.Count(u => u.IsGroundCombatant) >= 2)
                        unledViableFields++;
                    else if (commander != null && commander.HeroRole == HeroOperationalRole.SupportOperator)
                        supportLedWhileCombatBenched++;
                }
                else if (!meta.SingletonExempt)
                {
                    nonViable++;
                }
            }

            int formationDefect = benchedCombatCapable > 0
                ? Math.Min(unledViableFields + supportLedWhileCombatBenched, benchedCombatCapable)
                : 0;

            formationStrengths.Sort((a, b) => b.CompareTo(a));
            // The day layout is the task layout: the strongest-first power profile of the field formations,
            // with or without an enemy in sight. A known or sudden attack is met by the night fold.
            return new Outcome(garrisonDeficit, legality, operatorExposure, preparationSlotWaste,
                singles, nonViable, commandWaste, formationDefect, formationStrengths,
                -composition, s.Transfers.Count);
        }

        // 1 when a container with two or more heroes is not led by the one commander evaluation's
        // best hero (HeroRoleEvaluator — fight against the group's strongest threat, then capacity,
        // then role/leadership). The commander reorder candidate is the zero-AP fix.
        private static int CommanderMismatch(List<ReorgUnit> units, ReorgContainer meta,
            IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            bool isGarrison = meta.IsGarrison;
            // A garrison is judged on its whole hero order, not on the identity of its first hero.
            if (isGarrison)
                return ReorgViability.IsCanonicalGarrisonOrder(units) ? 0 : 1;
            if (units.Count(u => u != null && u.IsHero) < 2)
                return 0;
            ReorgUnit current = units.First(u => u != null && u.IsHero);
            return ReferenceEquals(BestCommander(units, meta, context), current) ? 0 : 1;
        }
    }
}
