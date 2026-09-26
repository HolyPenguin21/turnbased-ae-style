using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // Actor-priced Raid estimate. Requirements is deliberately CURRENT-TURN only and is the only
    // part ResourceAllocator may fund. RecurringActivationAp is a planning/scoring fact for future
    // turns of a multi-turn route; it must never leak into the allocator envelope or reservation
    // ledger. PlannedMoverArmyId is the actor whose route/AP facts were priced before funding.
    public readonly struct RaidCostEstimate
    {
        public readonly MissionRequirements Requirements;
        public readonly float RecurringActivationAp;
        public readonly int? PlannedMoverArmyId;

        public RaidCostEstimate(MissionRequirements requirements, float recurringActivationAp,
            int? plannedMoverArmyId)
        {
            Requirements = requirements;
            RecurringActivationAp = Mathf.Max(0f, recurringActivationAp);
            PlannedMoverArmyId = plannedMoverArmyId;
        }
    }

    // Shared physical cost estimator for Raid. If assembly/durable intent selected a primary mover,
    // every cost/distance fact is derived from that exact actor or remains explicitly unknown if the
    // actor no longer resolves. A deterministic fallback actor is selected only when no actor was
    // supplied at all (never AP from one army, distance from another).
    public static class RaidCostModel
    {
        // Compatibility projection for callers that only need the current-turn allocator envelope.
        public static MissionRequirements Build(WorldSnapshot snap, RaidMissionTarget target,
            int? selectedMoverArmyId = null, int? projectedRosterActivationAp = null) =>
            Estimate(snap, target, selectedMoverArmyId, projectedRosterActivationAp).Requirements;

        // `projectedRosterActivationAp` (AI-01) — the full activation AP of the roster
        // GroundCombatAssemblyPlanner will ACTUALLY produce for an Assault (host members plus every
        // planned transfer), from the single owner of that projection
        // (GroundCombatAssemblyPlanner.ProjectedActivationApCost). Without it the estimate priced
        // the untouched host, so a raid that assembled three extra bodies was funded 1 AP and then
        // rejected by MissionRevalidator at its real 4 AP. Null keeps the host-only pricing used by
        // every non-Assault phase, whose cost contracts are unchanged.
        public static RaidCostEstimate Estimate(WorldSnapshot snap, RaidMissionTarget target,
            int? selectedMoverArmyId = null, int? projectedRosterActivationAp = null)
        {
            float currentTurnActivationAp = AiConfigV2.raidNotionalActivationAp;
            float recurringActivationAp = AiConfigV2.raidNotionalActivationAp;
            bool moverKnown = false;
            int? plannedMoverArmyId = selectedMoverArmyId;
            int eta = Mathf.Max(1, target.EstimatedEta);
            bool isAssault = target.Phase == RaidMissionPhase.Assault;
            HexCoord destination = target.Phase == RaidMissionPhase.Assault
                ? target.LastKnownHex : target.DestinationHex;
            // No actor means there is no actor-specific route yet. Keep the physical unit honest:
            // use the shared nearest-owned-home hex distance as a notional fallback, never ETA turns.
            int distance = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, destination, 0);

            if (snap?.Self?.Armies != null)
            {
                ArmySnapshot mover;
                if (selectedMoverArmyId.HasValue)
                {
                    // Preserve actor identity. Do not silently price a durable/preferred actor's
                    // mission from some other ready army merely because the preferred actor has no
                    // movement left this turn.
                    mover = snap.Self.Armies.FirstOrDefault(a =>
                        a != null && a.ArmyId == selectedMoverArmyId.Value
                        && !a.IsPrison && !a.IsAir && !a.IsGarrison && !a.IsSoloRecce
                        && a.MemberCount > 0);
                }
                else
                {
                    mover = snap.Self.Armies
                        .Where(a => a != null && !a.IsPrison && !a.IsAir && !a.IsGarrison && !a.IsSoloRecce
                                    && a.MemberCount > 0 && a.CurrentMovement > 0)
                        .OrderBy(a => HexGridMath.Distance(a.Hex, destination))
                        .ThenBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                        .ThenBy(a => a.ArmyId)
                        .FirstOrDefault();
                }

                if (mover != null)
                {
                    moverKnown = true;
                    plannedMoverArmyId = mover.ArmyId;
                    // The current turn can legitimately be free when this army was already activated.
                    // Future route turns are not free: a fresh turn requires the normal activation AP.
                    // Assault may reinforce the mover before it marches; every body joining it
                    // carries its own ActivationApCost, so the projected roster — not the host as
                    // it stands right now — is the honest price of getting this force moving, this
                    // turn and on every later turn of the route.
                    bool useProjected = isAssault && projectedRosterActivationAp.HasValue;
                    recurringActivationAp = Mathf.Max(0f, useProjected
                        ? projectedRosterActivationAp.Value : mover.ActivationApCost);
                    currentTurnActivationAp = mover.HasActivatedThisTurn ? 0f : recurringActivationAp;
                    distance = HexGridMath.Distance(mover.Hex, destination);
                    // Movement spent earlier THIS turn cannot be spent again. The old full-speed
                    // ceil(distance / MaxMovement) underpriced a pinned, already-activated mover
                    // with 0 MP as a same-turn Raid, omitting its next-turn activation from Delivery.
                    // Match ScoutCostModel.PairCost's remaining-MP ETA without reserving future AP.
                    int moveBudget = Mathf.Max(1, mover.MaxMovement);
                    eta = mover.CurrentMovement >= distance ? 1
                        : 1 + CeilDiv(distance - Mathf.Max(0, mover.CurrentMovement), moveBudget);
                }
            }

            // AI-01 — an Assault's minimum is the WHOLE activation charge the executor will be
            // asked for. The old min(cost, 1) admission floor let a raid be funded 1 AP and only
            // discover its real 4 AP after the assembly transfers had already happened; the
            // allocator has to see the true floor before it commits. Non-Assault phases keep the
            // historical admission floor: their cost contracts are deliberately untouched here.
            float admissionAp = currentTurnActivationAp <= 0f ? 0f
                : isAssault ? currentTurnActivationAp
                : Mathf.Min(currentTurnActivationAp, 1f);
            float combatMin = Mathf.Max(0f, target.TargetPower);
            // The one ground-combat requirement owner, on the same known defenders and site bonus
            // the Raid objective sized its shortage by (AggressionObjectiveEvaluator).
            // Never below the minimum: the requirement envelope must stay coherent even when
            // memory no longer resolves the defenders behind a frozen TargetPower.
            float combatDesired = combatMin <= 0f ? 0f
                : Mathf.Max(combatMin, GroundCombatFeasibility.RequiredPower(
                    AiV2Util.KnownDefenders(snap, target.Target),
                    AiV2Util.KnownRaidDefenceBonus(snap, target.Target)));

            var requirements = new MissionRequirements
            {
                MoverKnown = moverKnown,
                ApMinimum = admissionAp,
                ApDesired = currentTurnActivationAp,
                // Never below the minimum: a projected activation dearer than raidActivationApMax
                // must still present a coherent [min..max] envelope to the allocator.
                ApMaximum = Mathf.Max(admissionAp,
                    Mathf.Max(currentTurnActivationAp, AiConfigV2.raidActivationApMax)),
                RequiresArmy = true,
                // Hero is a possible way to satisfy a combat-capability shortage, never a Raid
                // prerequisite. GroundCombatAssemblyPlanner/WorthIt owns whether the concrete roster
                // can win; AggressionDemandEvaluator raises a Hero demand only when readiness proves
                // one is actually missing.
                RequiresHero = false,
                CombatPowerMinimum = combatMin,
                CombatPowerDesired = combatDesired,
                RequiredCombatTraits = TraitPreference.None,
                EtaTurns = eta,
                EstimatedDistance = distance,
            };
            return new RaidCostEstimate(requirements, recurringActivationAp, plannedMoverArmyId);
        }

        private static int CeilDiv(int a, int b) => AiV2Util.CeilDiv(a, b);
    }
}
