using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §28/§46/§47 — the two NON-assault ground-combat legs, shared by every lane.
    //
    //  Walking a mover home and bringing a support convoy to a primary are physical procedures with
    //  no lane semantics at all: resolve the actor, refuse a contended one, check it can actually
    //  take a first step, and check the activation AP fits both the mission envelope and the turn.
    //  Each lane still builds its OWN ProvisionedMission payload from the validated result, because
    //  that payload is where its semantics live.
    // ===========================================================================================
    internal sealed class GroundCombatLegCheck
    {
        public bool Ok;
        public ProvisioningResult Failure;
        public ArmyData Mover;
        public int ActivationAp;

        public static GroundCombatLegCheck Failed(ProvisioningResult failure) =>
            new GroundCombatLegCheck { Ok = false, Failure = failure };
    }

    internal static class GroundCombatLegChecks
    {
        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // §47 — the mover walks to an ALREADY CHOSEN destination. This never re-picks the base:
        // Continuity owns that decision (AiReturnBasePolicy.SelectReturnBase).
        internal static GroundCombatLegCheck ValidateWalkHome(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisioningSession session, FundedEntry funded, StableMissionKey key,
            float eps, int? moverArmyId, HexCoord home, string lane, string roleLabel)
        {
            if (!moverArmyId.HasValue)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.TargetInvalidated($"{lane} {roleLabel} return has no mover assigned")));
            ArmyData mover = AiV2Util.ResolveArmy(player, moverArmyId.Value);
            if (!AiV2Util.IsLiveOwnArmy(mover, player)
                || mover.IsPrison || mover.IsAirfield || AviationRules.IsAirArmy(mover))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.TargetInvalidated(
                        $"{lane} {roleLabel} return mover #{moverArmyId.Value} is no longer a usable field army")));
            if (session.ClaimedArmyIds.Contains(mover.Id))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"{lane} {roleLabel} return mover #{mover.Id} was claimed by an earlier mission this cycle")));

            if (mover.Hex.Equals(home))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.TargetSatisfied(
                        $"{lane} {roleLabel} return mover #{mover.Id} is already home at ({home.Q},{home.R})")));
            if (mover.CurrentMovement <= 0)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.NoExecutableStep(
                        $"{lane} {roleLabel} return mover #{mover.Id} has no movement left")));
            if (SafeStepPathing.FindNextSafeStep(ctx.Map, mover, home,
                    profile: SafeRouteProfile.Combat) == null)
            {
                // Defensive re-check only; the frozen Analysis reachability fact
                // (ReturnBaseStillValid) already retargets a genuinely unreachable base at turn-start
                // reconciliation. This classifies the rare same-turn edge case distinctly from an
                // ordinary "blocked only this turn" retry.
                ArmySnapshot moverSnap = session.Snapshot?.Self?.Armies?
                    .FirstOrDefault(a => a != null && a.ArmyId == mover.Id);
                bool genuinelyUnreachable = moverSnap != null && moverSnap.IsStructuralRaidActor
                    && !moverSnap.ReachableOwnBaseHexes.Contains(home);
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(genuinelyUnreachable
                    ? ProvisionFailure.DestinationUnreachable(
                        $"return base ({home.Q},{home.R}) has no safe route at all from "
                        + $"({mover.Hex.Q},{mover.Hex.R})")
                    : ProvisionFailure.NoExecutableStep(
                        $"no safe first step from ({mover.Hex.Q},{mover.Hex.R}) toward return base ({home.Q},{home.R})")));
            }

            int activationAp = mover.HasActivatedThisTurn ? 0 : mover.ActivationApCost;
            if (activationAp > funded.Tentative.Ap + eps)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.EnvelopeTooSmall(activationAp,
                        $"{lane} return needs {N(activationAp)} AP, envelope is {N(funded.Tentative.Ap)}")));
            if (!session.FitsTurnAp(root, activationAp, eps))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"turn AP exhausted: {lane} return needs {N(activationAp)}")));

            return new GroundCombatLegCheck { Ok = true, Mover = mover, ActivationAp = activationAp };
        }

        // §46 — the support convoy leg. `atRendezvous` tells the caller whether this step is the
        // atomic same-hex roster handoff or another transit step toward the primary.
        // `meetingHex` (a committed Attack's rendezvous on the primary's route): the support walks
        // there instead of to the primary's current hex, and waits there for the primary; the
        // handoff still needs both armies on one hex.
        internal static GroundCombatLegCheck ValidateReinforcement(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ProvisioningSession session, FundedEntry funded,
            StableMissionKey key, float eps, ArmyData primary, int supportArmyId,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            string lane, out bool atRendezvous, bool allowCommandHandover = false,
            bool capacityIsProgress = false, HexCoord? meetingHex = null,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null)
        {
            atRendezvous = false;
            ArmyData support = AiV2Util.ResolveArmy(player, supportArmyId);
            if (support == null || support.Owner != player || support.Id == primary.Id
                || support.Members.Count == 0 || support.IsPrison || support.IsGarrison
                || support.IsAirfield || AviationRules.IsAirArmy(support))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.NoMoverExists(
                        $"{lane} reinforcement support #{supportArmyId} is not a separate mobile ground army")));
            if (session.ExcludedForGroundCombat(funded.Mission).Contains(support.Id))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"{lane} reinforcement support #{support.Id} is claimed by another mission or leg")));

            atRendezvous = support.Hex.Equals(primary.Hex);
            HexCoord rendezvous = atRendezvous ? primary.Hex : meetingHex ?? primary.Hex;
            if (!atRendezvous && support.Hex.Equals(rendezvous))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"{lane} reinforcement support #{support.Id} waits on the rendezvous "
                        + $"({rendezvous.Q},{rendezvous.R}) for primary #{primary.Id}")));

            // Does the projected delivered roster actually improve the primary's odds? The SAME
            // WorthIt projection provisioning/execution will use, never a separate estimator.
            // ATK-F04 — composition only: whether the arrival handoff strengthens the primary does
            // not depend on today's AP; the rendezvous step below prices and checks the charge.
            string improveWhy;
            bool improves = lane == "attack"
                ? GroundCombatReinforcement.ImprovesAttackForce(primary, support, opposition,
                    defenderHexDefenseBonus, out improveWhy, capacityIsProgress: capacityIsProgress,
                    rosterAdmissible: rosterAdmissible)
                : GroundCombatReinforcement.ImprovesOdds(primary, support, opposition,
                    defenderHexDefenseBonus, out improveWhy, allowCommandHandover, rosterAdmissible);
            if (!improves)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.AssemblyInfeasible(
                        $"{lane} reinforcement #{support.Id} -> #{primary.Id} cannot strengthen its legal roster"
                        + $" at ({primary.Hex.Q},{primary.Hex.R}), support at ({support.Hex.Q},{support.Hex.R}): "
                        + $"{improveWhy}")));

            if (!atRendezvous)
            {
                if (support.CurrentMovement <= 0)
                    return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                        ProvisionFailure.NoExecutableStep(
                            $"{lane} reinforcement support #{support.Id} has no movement left")));
                if (SafeStepPathing.FindNextSafeStep(ctx.Map, support, rendezvous,
                    profile: SafeRouteProfile.Combat) == null)
                    return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                        ProvisionFailure.NoExecutableStep(
                            $"no safe first step from ({support.Hex.Q},{support.Hex.R}) toward rendezvous "
                            + $"({rendezvous.Q},{rendezvous.R})")));
            }

            // At the rendezvous the step IS the handoff. The odds projection above does not check
            // that the armies can actually exchange anyone; the handoff plan does, so no plan
            // means there is no step to provision.
            HandoffPlan plan = null;
            if (atRendezvous)
            {
                string planWhy;
                plan = lane == "attack"
                    ? GroundCombatReinforcement.PlanAttackHandoff(primary, support, opposition,
                        defenderHexDefenseBonus, requireChargeNow: true, out planWhy, capacityIsProgress,
                        rosterAdmissible)
                    : GroundCombatReinforcement.PlanHandoff(primary, support,
                        allowCommandHandover ? opposition : null, defenderHexDefenseBonus, out planWhy,
                        rosterAdmissible: rosterAdmissible);
                if (plan == null)
                    return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                        ProvisionFailure.NoExecutableStep(
                            $"{lane} reinforcement #{support.Id} -> #{primary.Id} has no executable handoff: {planWhy}")));
            }
            int activationAp = ReinforcementStepAp(support, primary, plan);
            if (activationAp > funded.Tentative.Ap + eps)
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.EnvelopeTooSmall(activationAp,
                        $"{lane} reinforcement needs {N(activationAp)} AP, envelope is {N(funded.Tentative.Ap)}")));
            if (!session.FitsTurnAp(root, activationAp, eps))
                return GroundCombatLegCheck.Failed(ProvisioningResult.Fail(
                    ProvisionFailure.MoverContended(
                        $"turn AP exhausted: {lane} reinforcement needs {N(activationAp)}")));

            return new GroundCombatLegCheck { Ok = true, Mover = support, ActivationAp = activationAp };
        }

        // The AP of one reinforcement step. A transit step activates the support. The handoff
        // step (`handoff` given) moves no one — the executor never moves and hands off in one
        // step — so it costs exactly its own charges: newcomers to an army that already acted,
        // both directions (GroundCombatReinforcement.HandoffApCost).
        internal static int ReinforcementStepAp(ArmyData support, ArmyData primary, HandoffPlan handoff) =>
            handoff != null ? GroundCombatReinforcement.HandoffApCost(handoff, primary, support)
                : support.HasActivatedThisTurn ? 0 : support.ActivationApCost;
    }
}
