using System;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;

namespace Game.Ai.V2
{
    public static partial class ArmyReorganizationPlanner
    {
        // The whole hex against one known attacker (HousekeepingNightFold's layouts).
        private readonly struct ThreatContactRow
        {
            public readonly int ThreatArmyId;
            // The whole hex against this attacker: every contactable formation in the rules' fight
            // order, the attacker's wounds carried from one battle to the next.
            public readonly float HexReadiness;
            // Below this HexReadiness difference two rosters hold the hex equally (Monte Carlo
            // noise, AiConfigV2.housekeepingHexHoldMargin in this threat's ETA weighting).
            public readonly float HexTolerance;

            public ThreatContactRow(int threatArmyId, float hexReadiness, float hexTolerance)
            {
                ThreatArmyId = threatArmyId;
                HexReadiness = hexReadiness;
                HexTolerance = hexTolerance;
            }
        }

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

            // §9 — formation-leadership bookkeeping. A "benched" hero sits in a garrison or a lone
            // hero container; a heroless viable field formation is only a fixable defect when such
            // a hero exists in the same group.
            int benchedCombatCapable = 0;
            int unledViableFields = 0;
            int supportLedWhileCombatBenched = 0;

            // 2026-10-15 (user decision) — a lone free hero is a bench, not a defect, until a known
            // enemy can reach this hex within activeDefenceWithdrawMaxEnemyEta turns: then it
            // shelters in the local garrison (exposed like an operator outside it), which its
            // command rating also serves. Economy still reaches it there by garrison extraction.
            // 2026-11-18 — only while the garrison has a slot to take it: a garrison packed with
            // fighters for the same threat (passive defence) cannot shelter it, and the hero then
            // stands as a bench on the hex instead of blocking that packing.
            // 2026-11-26 (owner, stand E04) — only while no army of the hex can take the blow: the
            // hardest combat-capable army fights first (BattleInitiator.CompareDefenderHardness) and
            // a hero-only army meets the enemy only after them, so beside any fighter it is safe.
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
                    preparationSlotWaste += PreparationSlotWaste(units);
                else if (meta.IsMutableGround && meta.CanDonate)
                    // Ordinary secondary heroes add no command or force. Keep canonical passive
                    // battle carriers (Heal/Drill) for the mission's own counterfactual check.
                    // A Support hero is OperatorExposure's business (2026-11-18: off a hero-only
                    // bench it is exposed; on it, it takes no fighter slot).
                    preparationSlotWaste += units.Where(u => u != null && u.IsHero).Skip(1)
                        .Count(u => !u.IsCommitted && !u.IsDevelopmentOperator
                            && !u.IsGarrisonHero && !u.IsArmySupportCarrier);
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
                        // 2026-11-25 (owner, housekeeping stand Q1: "why repackage when the
                        // strongest army is already formed?") — the garrison's own strength and
                        // composition are no reason to move bodies: a sound free field army was
                        // folded whole into a garrison with room, burying its commander. Its floor
                        // (GarrisonDeficit, the release rule) stays; the defence of the hex is the
                        // night fold's (HousekeepingNightFold).
                    }
                    continue;
                }

                // EmptyReusableArmy is neutral: once filled it is evaluated as a normal field
                // formation, but while empty it contributes neither a defect nor a zero-strength
                // entry that would pressure the planner to seed it.
                // T05 — a mission receiver is scored as the field formation it is (its defects
                // can only be fixed by inbound members), but a hero in it is never "benched":
                // a claimed container gives nobody away.
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
            // 2026-11-25 (owner) — the day layout is the task layout: the strongest-first AiPower
            // profile of the field formations, with or without an enemy in sight. A known or a
            // sudden attack is met by the night fold (HousekeepingNightFold: every formation of the
            // hex in one fight until the owner's next turn), so the threat-first whole-hex profile,
            // the passive garrison packing and the lost-hex fold no longer move bodies by day.
            return new Outcome(garrisonDeficit, legality, operatorExposure, preparationSlotWaste,
                singles, nonViable, commandWaste, formationDefect, new LazyProfile(formationStrengths),
                -composition, s.Transfers.Count);
        }

        // The per-threat contact rows of one roster (whole-hex hold, first and second defender),
        // built once per VState: the night fold (HousekeepingNightFold) compares its layouts on
        // them. Candidate states are fresh clones never mutated after enumeration.
        private static List<ThreatContactRow> ThreatRowsOf(VState state)
        {
            if (state.ThreatRows != null)
                return state.ThreatRows;
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
                            ? commander : default, environment: state.Environment);
                    ranked.Add((kv.Key, selection, defenders, commander));
                }

                // This attacker cannot contact any non-hero defender on the hex (for example every
                // unit is hidden from it), so it exerts no Housekeeping packaging pressure here.
                if (ranked.Count == 0)
                    continue;

                ranked.Sort((a, b) => BattleInitiator.CompareDefenderHardness(
                    a.selection, a.containerId, b.selection, b.containerId));

                rows.Add(new ThreatContactRow(threat.ArmyId, HexReadiness(state, threat, ranked),
                    AiConfigV2.housekeepingHexHoldMargin / (1f + Math.Max(0, threat.EtaToGroup))));
            }
            state.ThreatRows = rows;
            return rows;
        }

        // 2026-11-18 (owner, match Kryll vs Tessek T31) — the hex is held or lost as a whole: the
        // attacker fights every formation in turn and carries its wounds on (BattleInitiator,
        // WorthIt.EstimateHexControl — the one hex-hold model ActiveDefence's HoldChanceAtAsset
        // reads too). The first/second contact rows alone never saw the third and later
        // formations, so folding two heroless 2-body armies into a 3/7 hero army changed nothing
        // there and was refused (Kryll (0,4): hold 0.32 as it stood, 0.92 folded; lost all six).
        private static float HexReadiness(VState state, ReorgThreatBenchmark threat,
            List<(int containerId, WorthIt.BattleEstimate selection, List<WorthIt.DefenderProfile> defenders,
                WorthIt.SideCommander commander)> ranked)
        {
            var armies = new List<WorthIt.DefendingArmy>(ranked.Count);
            foreach (var r in ranked)
                // The building part rides with every army: the estimator removes it for an
                // attacker carrying Disruption (DefendingArmy.DefenseBonus), as in the real battle.
                armies.Add(new WorthIt.DefendingArmy(r.defenders, r.commander, armyId: r.containerId,
                    buildingDefenseBonus: state.BuildingDefenseBonus));
            float attackerWins = WorthIt.EstimateHexControl(threat.Members, threat.Commander, armies,
                state.HexDefenseBonus, environment: state.Environment,
                monteCarloTrials: AiConfigV2.housekeepingHexHoldTrials).WinChance;
            return 1f - attackerWins / (1f + Math.Max(0, threat.EtaToGroup));
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
