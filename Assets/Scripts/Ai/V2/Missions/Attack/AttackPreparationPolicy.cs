using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    internal readonly struct AttackPreparationAssessment
    {
        internal readonly bool HostAvailable, PowerReady, CoversAllDefenders, CombatFeasible;
        internal readonly bool StructuralActor;
        internal readonly float CurrentPower, RequiredPower, ProjectedWinChance;
        internal readonly string Reason;
        // Capability satisfaction is separate from admission of an actor to a march. Empty / hero
        // preparation containers can be strengthened before they become structural combat actors.
        internal bool CapabilityReady => HostAvailable && PowerReady && CombatFeasible;
        internal bool Ready => CapabilityReady && StructuralActor;

        internal AttackPreparationAssessment(ArmySnapshot host, float peak,
            bool feasible, float win, bool cover,
            IReadOnlyList<WorthIt.DefendingArmy> opposition = null)
        {
            HostAvailable = host != null;
            CurrentPower = host?.EffectiveArmyPower ?? 0f;
            RequiredPower = AttackForceReadiness.RequiredPower(peak);
            // Below the 80% floor the host is still ready by opportunity (AttackForceReadiness).
            PowerReady = AttackForceReadiness.ForceReady(CurrentPower, peak, win, opposition);
            StructuralActor = GroundCombatActorEligibility.IsStructuralActor(host);
            CombatFeasible = feasible;
            ProjectedWinChance = win;
            CoversAllDefenders = cover;
            // Coverage is named only where it actually gated the fight (a test switch may waive it).
            Reason = !HostAvailable ? "no_primary" : !PowerReady ? "fist_below_bar"
                : !feasible ? (!cover ? "coverage_missing" : "win_below_gate")
                : !StructuralActor ? "actor_ineligible"
                : AttackForceReadiness.ForceReady(CurrentPower, peak) ? "ready" : "ready_by_opportunity";
        }
    }

    internal static class AttackPreparationPolicy
    {
        // 2026-10-21 — the staging Base of a LIVE preparation: the one frozen in the intent
        // (AttackIntent.StagingHex) while it is still an own Base the host can reach, else the
        // fresh choice below. A host that arrived and finds nothing to take there STALLs and the
        // stall lifecycle ends the preparation; it never walks off to the next garrison donor.
        // Pure: Continuity stores the answer, the planner reads the same answer.
        internal static HexCoord? KeptStagingBase(WorldSnapshot snap, AttackIntent attack,
            ArmySnapshot host)
        {
            if (attack?.StagingHex is HexCoord kept && snap?.Self?.BaseHexes != null
                && snap.Self.BaseHexes.Contains(kept)
                && (host == null || !host.IsStructuralRaidActor || host.Hex.Equals(kept)
                    || (host.ReachableOwnBaseHexes != null && host.ReachableOwnBaseHexes.Contains(kept))))
                return kept;
            return attack == null ? null : PreparationStagingBase(snap, attack.Target.Hex, host);
        }

        // The own Base a mobilization preparation assembles on: of every held own Base
        // (SelfSnapshot.BaseHexes, starting Citadel included) the one nearest to the target; ties
        // go to the starting Citadel, then Q, R. Null when no own Base is held. Recomputed every
        // pass, so a lost Base simply moves the staging point. With a `host` whose route facts
        // Analysis measured (a structural field army, ArmySnapshot.ReachableOwnBaseHexes) only a
        // Base it can really reach qualifies — an unreachable staging point would otherwise hold
        // the preparation in WAIT forever.
        internal static HexCoord? PreparationStagingBase(WorldSnapshot snap, HexCoord targetHex,
            ArmySnapshot host = null)
        {
            IReadOnlyList<HexCoord> bases = snap?.Self?.BaseHexes;
            if (host != null && host.IsStructuralRaidActor && bases != null)
                bases = bases.Where(h => h.Equals(host.Hex)
                    || (host.ReachableOwnBaseHexes != null && host.ReachableOwnBaseHexes.Contains(h)))
                    .ToList();
            if (bases == null || bases.Count == 0)
                return null;
            HexCoord citadel = snap.Self.Citadel;
            // 2026-10-16 (user decision) — a garrison is a donor of the strike fist. A field host
            // still below the floor stages on the reachable own Base whose garrison can spare the
            // most power above its floor; the ordinary same-hex Assemble step takes those bodies
            // (AiArmyRoles.CanSpareGarrisonMembers). Once no garrison can spare a body the host
            // stages, as before, on the Base nearest to the target.
            // Only a host with a free roster slot can take a body: a full one would wait on that
            // Base for a transfer that never fits.
            // 2026-10-21 — a spare is measured against the threats that reach the garrison within
            // activeDefenceWithdrawMaxEnemyEta turns (AiArmyRoles.GarrisonThreatFloor); a donor the
            // host reaches only later is a promise past that horizon. Vex T16-T21 walked six
            // hexes for a front-line spare that was gone on arrival, then six back.
            if (host != null && host.IsStructuralRaidActor && host.MemberCount < host.Capacity
                && !AttackForceReadiness.ForceReady(host.EffectiveArmyPower, snap.Self.AttackPeak))
            {
                var donor = bases
                    .Where(h => AiV2Util.CeilDiv(HexGridMath.Distance(host.Hex, h), Mathf.Max(1, host.MaxMovement))
                        <= AiConfigV2.activeDefenceWithdrawMaxEnemyEta)
                    .Select(h => (Hex: h, Spare: SpareGarrisonPower(snap, h)))
                    .Where(x => x.Spare > 0f)
                    .OrderByDescending(x => x.Spare)
                    .ThenBy(x => HexGridMath.Distance(x.Hex, targetHex))
                    .ThenBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                    .FirstOrDefault();
                if (donor.Spare > 0f)
                    return donor.Hex;
            }
            return bases
                .OrderBy(h => HexGridMath.Distance(h, targetHex))
                .ThenByDescending(h => h.Equals(citadel))
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .First();
        }

        // Power the own garrison on `hex` can give away above its defence floor, from the scan's
        // frozen facts: its spareable non-hero bodies (ArmySnapshot.NonHeroSpareable), strongest
        // first, never more than leaves secureBaseMinNonHeroUnits behind.
        internal static float SpareGarrisonPower(WorldSnapshot snap, HexCoord hex)
        {
            ArmySnapshot garrison = snap?.Self?.Armies?.FirstOrDefault(a => a != null && a.IsGarrison
                && a.Hex.Equals(hex));
            IReadOnlyList<WorthIt.DefenderProfile> bodies = garrison?.Members;
            if (bodies == null || bodies.Count == 0)
                return 0f;
            int room = bodies.Count - AiConfig.secureBaseMinNonHeroUnits;
            if (room <= 0)
                return 0f;
            List<WorthIt.DefenderProfile> spare = bodies
                .Where((b, i) => garrison.NonHeroSpareable == null
                    || (i < garrison.NonHeroSpareable.Count && garrison.NonHeroSpareable[i]))
                .OrderByDescending(b => AiPower.EffectiveArmyPowerFromProfiles(new[] { b }))
                .Take(room)
                .ToList();
            return spare.Count == 0 ? 0f : AiPower.EffectiveArmyPowerFromProfiles(spare);
        }
        internal static (ArmyData garrison, UnitData hero, float power) FindCommanderUpgrade(
            WorldSnapshot snap, ArmySnapshot host, out float currentPower, out int currentCommand, out bool assessable)
        {
            assessable = false;
            currentPower = 0f;
            currentCommand = 0;
            PlayerSetupData player = snap.Observer;
            if (player == null || host == null || snap.Self.StrikePool == null || snap.Self.StrikePool.Count == 0)
                return default;

            assessable = true;
            StrikeRoster.ComposeUnder(snap.Self.StrikePool, host.CommanderRosterCandidate,
                host.RosterCapacity, out currentPower);
            // Under a fetched commander every support carrier of the host stays, the current
            // commander included when it is one (an ordinary one returns to the garrison).
            int retainedUnderUpgrade = host.SupportCarrierSlots;
            if (AttackForceReadiness.ForceReady(currentPower, snap.Self.AttackPeak))
                return default;
            currentCommand = host.CommanderCommandRating;
            (ArmyData garrison, UnitData hero, float power) best = (null, null, currentPower);
            foreach (ArmyData g in OwnWorld.Of(player).Armies)
            {
                if (g == null || !g.IsGarrison || g.Hex.Equals(host.Hex))
                    continue;
                foreach (UnitData u in g.Members)
                {
                    if (u == null || !u.IsHero || u.IsPrisoner || u.CommandRating <= currentCommand
                        || AiArmyRoles.IsGarrisonHero(u) || AiArmyRoles.IsFacilityOperator(player, g.Hex, u)
                        || !AiArmyRoles.CanSpareGarrisonMember(player, g, u))
                        continue;
                    StrikeRoster.ComposeUnder(snap.Self.StrikePool, StrikeRoster.CommanderCandidate(u),
                        u.CommandRating - retainedUnderUpgrade, out float power);
                    if (power > best.power + AiConfigV2.allocatorSliceEpsilon
                        || (best.hero != null && System.Math.Abs(power - best.power) <= AiConfigV2.allocatorSliceEpsilon
                            && HexGridMath.Distance(g.Hex, host.Hex) < HexGridMath.Distance(best.garrison.Hex, host.Hex)))
                        best = (g, u, power);
                }
            }
            return best;
        }

        // Another OBSERVED hostile structure (never a location-only guess) the host passes the
        // attack coverage / win-chance gate against right now, in the evaluator's own priority
        // order. Snapshot-pure: remembered opposition and structural defence only.
        internal static bool ClearedAlternativeTarget(WorldSnapshot snap, AttackTargetRef target, int hostId,
            out string label)
        {
            label = null;
            foreach (AttackObjective objective in AttackObjectiveEvaluator.Enumerate(snap))
            {
                if (objective == null)
                    continue;
                // The evaluator's order is what the fresh pick will follow: only a target that
                // ranks AHEAD of the current one can take over (otherwise the same one returns).
                if (objective.Target.Hex.Equals(target.Hex))
                    return false;
                if (AttackObjectiveEvaluator.IsLocationOnly(snap, objective.Target)
                    || objective.Opposition == null || objective.Opposition.Count == 0)
                    continue;
                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(
                    snap, objective.Opposition, hostId, GroundCombatAdmissionPolicy.AttackWinChanceGate,
                    objective.HexDefense);
                if (!plan.Feasible || !plan.CoversAllDefenders)
                    continue;
                label = $"{objective.Target} (win {plan.ProjectedWinChance:0.00})";
                return true;
            }
            return false;
        }
    

        // ---- Readiness ----
        // No mutation, phase transitions, or actor selection. Demand, Mission and Continuity read it;
        // only Continuity uses Ready to transition the operation. Location-only opposition remains the
        // current empty known package, so this policy adds no observation gate.
        internal static AttackPreparationAssessment AssessReadiness(ArmySnapshot host, float currentPeak,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus)
        {
            if (host == null)
                return new AttackPreparationAssessment(null, currentPeak, false, 0f, false);
            bool clears = GroundCombatFeasibility.Clears(
                host.Members ?? System.Array.Empty<WorthIt.DefenderProfile>(), host.Commander,
                opposition, GroundCombatAdmissionPolicy.AttackWinChanceGate, hexBonus,
                out float win, out bool cover);
            return new AttackPreparationAssessment(host, currentPeak, clears, win, cover, opposition);
        }

        // ---- Priority: attack preparation goes before fresh raids ----
        // 2026-10-01 (user decision, playtest #6): with mobilization open, Tessek's first preparation
        // step (value ~15, 4 AP) lost the allocator's value order to guarded-event Raids (value
        // 18-27 since EventReward) for six turns and never started. A preparation step exists only
        // while mobilization is open or a preparation is live; while a FRESH one (the first step) is
        // proposed, every FRESH Raid (no durable intent yet) ranks just below it. A Raid already
        // under way (a durable intent) keeps its rank; nothing else is reordered.
        internal static bool IsPreparationStep(MissionProposal m) =>
            m != null && m.Kind == MissionKind.Attack && m.Target is AttackMissionTarget t && t.Preparation;

        // Returns how many fresh Raids were ranked below the preparation. Call after
        // EffectiveValue is set; it only lowers EffectiveValue (the allocator's rank key).
        internal static int ApplyPriority(IList<MissionProposal> missions)
        {
            if (missions == null)
                return 0;
            // Only a FRESH step (the preparation's first, no intent yet) sets the bar: a live
            // preparation's steps are Hard commitments the allocator funds before every fresh
            // proposal anyway, and their lifecycle value (often 0) would sink the Raids below
            // every other task instead of just below the preparation.
            List<MissionProposal> steps = missions.Where(m => IsPreparationStep(m) && !m.FromDurableIntent).ToList();
            if (steps.Count == 0)
                return 0;
            float bar = steps.Max(m => m.EffectiveValue);
            var demoted = new List<string>();
            foreach (MissionProposal m in missions)
            {
                if (m == null || m.Kind != MissionKind.Raid || m.FromDurableIntent || m.EffectiveValue < bar)
                    continue;
                demoted.Add($"{StableMissionKey.For(m)} {m.EffectiveValue.ToString("0.#", CultureInfo.InvariantCulture)}");
                m.EffectiveValue = bar - AiConfigV2.allocatorSliceEpsilon;
            }
            if (demoted.Count > 0)
                AiDebugLog.Write($"[AI][V2][Attack][Mobilization] preparation step "
                    + $"(rank {bar.ToString("0.#", CultureInfo.InvariantCulture)}) goes before fresh raids: "
                    + string.Join("; ", demoted));
            return demoted.Count;
        }
    
    }
}
