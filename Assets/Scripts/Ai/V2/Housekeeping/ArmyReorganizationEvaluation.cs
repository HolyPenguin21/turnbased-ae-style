using System;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;

namespace Game.Ai.V2
{
    public static partial class ArmyReorganizationPlanner
    {
        private readonly struct ThreatContactRow
        {
            public readonly int ThreatArmyId;
            public readonly float FirstReadiness;
            public readonly float SecondReadiness;

            public ThreatContactRow(int threatArmyId, float firstReadiness, float secondReadiness)
            {
                ThreatArmyId = threatArmyId;
                FirstReadiness = firstReadiness;
                SecondReadiness = secondReadiness;
            }
        }

        private static Outcome Evaluate(VState s)
        {
            bool hasThreatBenchmarks = s.ThreatBenchmarks.Count > 0;
            IReadOnlyList<float> contactReadiness = hasThreatBenchmarks
                ? BuildThreatContactReadiness(s)
                : System.Array.Empty<float>();

            int garrisonDeficit = 0;
            int legality = 0;
            int operatorExposure = 0;
            int singles = 0;
            int nonViable = 0;
            int commandWaste = 0;
            IReadOnlyList<WorthIt.DefendingArmy> commandContext = CommandContext(s);
            float composition = 0f;
            var formationStrengths = new List<float>();

            // §9 — formation-leadership bookkeeping. A "benched" hero sits in a garrison or a lone
            // hero container; a heroless viable field formation is only a fixable defect when such
            // a hero exists in the same group.
            int benchedCombatCapable = 0;
            int unledViableFields = 0;
            int supportLedWhileCombatBenched = 0;

            foreach (int id in s.Meta.Keys.OrderBy(x => x))
            {
                ReorgContainer meta = s.Meta[id];
                List<ReorgUnit> units = s.Roster[id];

                if (ReorgViability.Capacity(units, meta.IsGarrison) < units.Count)
                    legality++;
                if (!meta.IsGarrison)
                    operatorExposure += units.Count(u => u != null && u.IsDevelopmentOperator);

                if (meta.CanChangeComposition)
                    commandWaste += CommanderMismatch(units, commandContext);

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
                        if (nonHero > 0)
                        {
                            if (!hasThreatBenchmarks)
                                formationStrengths.Add(ReorgViability.EffectivePower(units));
                            composition += ReorgViability.CompositionQuality(units);
                        }
                    }
                    continue;
                }

                // EmptyReusableArmy is neutral: once filled it is evaluated as a normal field
                // formation, but while empty it contributes neither a defect nor a zero-strength
                // entry that would pressure the planner to seed it.
                if (!IsFieldContainer(meta) || !meta.CanChangeComposition || units.Count == 0)
                    continue;

                if (!meta.SingletonExempt && ReorgViability.IsSingletonShape(units))
                    singles++;

                bool loneHero = units.Count == 1 && units[0].IsHero;
                if (loneHero && units[0].HeroRole != HeroOperationalRole.SupportOperator)
                    benchedCombatCapable++;

                if (ReorgViability.IsViable(units))
                {
                    if (!hasThreatBenchmarks)
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

            if (hasThreatBenchmarks)
            {
                // Already ordered by the worst enemy contact first. Keep enemy identity and the
                // first/second contact layers adjacent; never collapse them back per container.
                formationStrengths.AddRange(contactReadiness);
            }
            else
            {
                // No deployed enemy benchmark: retain the canonical AiPower fallback.
                formationStrengths.Sort((a, b) => b.CompareTo(a));
            }

            return new Outcome(garrisonDeficit, legality, operatorExposure, singles, nonViable,
                commandWaste, formationDefect, formationStrengths, -composition, s.Transfers.Count);
        }

        // Threat-first defensive profile. For each concrete enemy attacker, reproduce
        // BattleInitiator's observer-specific contact ordering with its canonical comparator.
        // The plan then compares the worst threat's first defender, that threat's second line,
        // and only then the next threat. No position coefficient or per-container max is used.
        private static IReadOnlyList<float> BuildThreatContactReadiness(VState state)
        {
            var rows = new List<ThreatContactRow>();

            foreach (ReorgThreatBenchmark threat in state.ThreatBenchmarks)
            {
                var ranked = new List<(int containerId, WorthIt.BattleEstimate selection,
                    List<WorthIt.DefenderProfile> defenders, WorthIt.SideCommander commander)>();

                foreach (KeyValuePair<int, ReorgContainer> kv in state.Meta)
                {
                    ReorgContainer meta = kv.Value;
                    if (meta.Role == ReorgPhysicalRole.Aviation
                        || meta.Role == ReorgPhysicalRole.SpecialExcludedContainer)
                        continue;
                    if (!state.Roster.TryGetValue(kv.Key, out List<ReorgUnit> units))
                        continue;

                    var defenders = units
                        .Where(u => u != null && threat.TargetableUnitKeys.Contains(u.Key))
                        .Select(u => u.CombatProfile)
                        .ToList();
                    if (defenders.Count == 0)
                        continue;
                    // The container's commander: its first hero (ArmyData.Commander's rule).
                    ReorgUnit commanderUnit = units.FirstOrDefault(u => u != null && u.IsHero);
                    WorthIt.SideCommander commander = commanderUnit?.AsCommander ?? default;

                    // BattleInitiator ranks contact candidates with a zero bonus and only the
                    // commander it can see. Keep selection identical. The actual readiness read
                    // below applies the group's terrain/base defence and the real commander.
                    WorthIt.BattleEstimate selection = WorthIt.Estimate(threat.Members, defenders, 0f,
                        threat.Commander,
                        commanderUnit != null && threat.TargetableUnitKeys.Contains(commanderUnit.Key)
                            ? commander : default);
                    ranked.Add((kv.Key, selection, defenders, commander));
                }

                // This attacker cannot contact any non-hero defender on the hex (for example every
                // unit is hidden from it), so it exerts no Housekeeping packaging pressure here.
                if (ranked.Count == 0)
                    continue;

                ranked.Sort((a, b) => BattleInitiator.CompareDefenderHardness(
                    a.selection, a.containerId, b.selection, b.containerId));

                float first = ContactReadiness(state, threat, ranked[0].defenders, ranked[0].commander);
                // If the first army falls and no second contactable formation remains, the second
                // defensive layer is empty: model certain passage at the same distance weighting.
                float second = ranked.Count > 1
                    ? ContactReadiness(state, threat, ranked[1].defenders, ranked[1].commander)
                    : NoDefenderReadiness(threat.EtaToGroup);
                rows.Add(new ThreatContactRow(threat.ArmyId, first, second));
            }

            // Lowest readiness is the most dangerous enemy contact. Stable army id is ordering
            // only; it never becomes strategic utility.
            rows.Sort((a, b) =>
            {
                if (a.FirstReadiness < b.FirstReadiness - FloatEps) return -1;
                if (a.FirstReadiness > b.FirstReadiness + FloatEps) return 1;
                if (a.SecondReadiness < b.SecondReadiness - FloatEps) return -1;
                if (a.SecondReadiness > b.SecondReadiness + FloatEps) return 1;
                return a.ThreatArmyId.CompareTo(b.ThreatArmyId);
            });

            var profile = new List<float>(rows.Count * 2);
            foreach (ThreatContactRow row in rows)
            {
                profile.Add(row.FirstReadiness);
                profile.Add(row.SecondReadiness);
            }
            return profile;
        }

        private static float ContactReadiness(VState state, ReorgThreatBenchmark threat,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, WorthIt.SideCommander commander)
        {
            WorthIt.BattleEstimate estimate = WorthIt.Estimate(threat.Members, defenders,
                state.HexDefenseBonus, threat.Commander, commander);
            float success = WorthIt.CanDamageAll(
                    threat.Members, defenders, state.HexDefenseBonus)
                ? estimate.WinChance
                : 0f;
            return 1f - success / (1f + Math.Max(0, threat.EtaToGroup));
        }

        private static float NoDefenderReadiness(int eta) =>
            1f - 1f / (1f + Math.Max(0, eta));

        // 1 when a container with two or more heroes is not led by the one commander evaluation's
        // best hero (HeroRoleEvaluator — fight against the group's strongest threat, then capacity,
        // then role/leadership). The commander reorder candidate is the zero-AP fix.
        private static int CommanderMismatch(List<ReorgUnit> units,
            IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            if (units.Count(u => u != null && u.IsHero) < 2)
                return 0;
            ReorgUnit current = units.First(u => u != null && u.IsHero);
            return ReferenceEquals(BestCommander(units, context), current) ? 0 : 1;
        }
    }
}
