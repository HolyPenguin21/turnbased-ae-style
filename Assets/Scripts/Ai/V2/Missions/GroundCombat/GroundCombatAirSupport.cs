using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // One way an already-fielded free wing can support a ground fight: fly to the target hex,
    // strike its defenders and keep its physical flight lifecycle independent of the ground mission.
    internal readonly struct AirSupportOption
    {
        internal readonly int WingArmyId;
        internal readonly HexCoord LandingHex;
        internal readonly int EtaTurns;
        internal readonly int FirstStrikeEta;
        internal readonly float WinAfter;
        // Activation AP now (0 when already paid this turn) and per extra turn.
        internal readonly float Ap;
        internal readonly float RecurringAp;
        // Launch Energy of every activation the sortie needs.
        internal readonly ResourceVector Resources;

        internal AirSupportOption(int wingArmyId, HexCoord landingHex, int etaTurns,
            int firstStrikeEta, float winAfter, float ap, float recurringAp, ResourceVector resources)
        {
            WingArmyId = wingArmyId;
            LandingHex = landingHex;
            EtaTurns = etaTurns;
            FirstStrikeEta = firstStrikeEta;
            WinAfter = winAfter;
            Ap = ap;
            RecurringAp = recurringAp;
            Resources = resources;
        }

    }

    // A resolved wing and its current sortie state, for the provisioning half below.
    internal readonly struct AirSupportWing
    {
        internal readonly ArmyData Wing;
        internal readonly AirSortie Active;
        // An existing strike sortie toward this target (outbound) or on its way home.
        internal readonly bool Continuing;
        internal readonly bool Returning;

        internal AirSupportWing(ArmyData wing, AirSortie active, bool continuing, bool returning)
        {
            Wing = wing;
            Active = active;
            Continuing = continuing;
            Returning = returning;
        }
    }

    // ===========================================================================================
    //  THE ONE AIR SUPPORT OF A GROUND FIGHT — shared by Raid (a recovery option of its
    //  AirSupport phase) and Attack (a strike on the site's defenders before the assault).
    //  Each lane keeps its own target identity, its own before/after win read (`winAgainst`), its
    //  own strike policy and its own lifecycle; the wing choice, strike estimate, landing base,
    //  leg requirements, sortie provisioning and flight step exist once. Repeat strikes are NOT
    //  estimated here: they belong to the common airborne lifecycle and TurnsWithoutRefuel.
    // ===========================================================================================
    internal static class GroundCombatAirSupport
    {
        // Every free wing's way to support the fight at `targetHex`, strictly improving the lane's
        // win (`winAgainst` over the opposition after the expected strike) on `currentWin`. The
        // opposition's bodies are struck in UnitsOf order; survivors are split back into their
        // armies (AfterStrike), so a multi-army site keeps its sequential battles.
        internal static List<AirSupportOption> Options(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, HexCoord targetHex,
            float knownDefense, float knownAttack, AirStrikePolicy policy,
            Func<IReadOnlyList<WorthIt.DefendingArmy>, float> winAgainst, float currentWin,
            ISet<int> unavailableArmyIds, int? fixedWingArmyId = null)
        {
            var result = new List<AirSupportOption>();
            if (snap?.Self?.Armies == null || winAgainst == null)
                return result;
            HexCoord? landing = LandingBase(snap, targetHex);
            if (!landing.HasValue)
                return result;
            opposition = opposition ?? Array.Empty<WorthIt.DefendingArmy>();
            List<WorthIt.DefenderProfile> defenders = WorthIt.UnitsOf(opposition);

            foreach (ArmySnapshot wing in snap.Self.Armies
                .Where(x => x != null && x.IsAir && !x.IsAirfield && !x.IsPrison
                    && x.MemberCount > 0 && x.CurrentMovement > 0
                    && (!fixedWingArmyId.HasValue || x.ArmyId == fixedWingArmyId.Value)
                    && (unavailableArmyIds == null || !unavailableArmyIds.Contains(x.ArmyId)))
                .OrderBy(x => x.ArmyId))
            {
                List<float> attacks = (wing.RecoveryMembers
                        ?? Array.Empty<RaidRecoveryMemberSnapshot>())
                    .Where(x => x.IsAviation)
                    .OrderBy(x => x.UnitIndex)
                    .Select(x => x.CurrentProfile.Attack).ToList();
                if (attacks.Count == 0)
                    continue;

                AviationCombatEstimator.AirStrikeEstimate estimate =
                    AviationCombatEstimator.EstimateAirStrike(attacks, knownDefense, knownAttack,
                        defenders, policy);
                if (estimate.ExpectedDamage <= AiConfigV2.allocatorSliceEpsilon
                    || estimate.ExpectedDefendersAfter.Count < policy.MinimumSurvivors)
                    continue;
                IReadOnlyList<int> firstSources = SourceIndices(estimate, defenders.Count);
                float after = winAgainst(AfterStrike(opposition, estimate.ExpectedDefendersAfter,
                    firstSources));
                if (after <= currentWin + AiConfigV2.allocatorSliceEpsilon)
                    continue;

                int eta = SortieEta(wing, targetHex);

                float finalAfter = after;
                int finalEta = eta;

                float ap = wing.HasActivatedThisTurn ? 0f : wing.ActivationApCost;
                float energy = wing.HasActivatedThisTurn ? 0f : wing.ActivationEnergyCost;
                result.Add(new AirSupportOption(wing.ArmyId, landing.Value, finalEta, eta,
                    finalAfter, ap, wing.ActivationApCost,
                    new ResourceVector(0f, 0f, energy, 0f, 0f)));
            }
            return result;
        }

        // The opposition after a strike: each survivor goes back to the army it came from (by its
        // index in UnitsOf(opposition)); an army with no survivor leaves the fight. Without
        // indices (no strike happened) the survivors are the opposition itself.
        internal static IReadOnlyList<WorthIt.DefendingArmy> AfterStrike(
            IReadOnlyList<WorthIt.DefendingArmy> opposition,
            IReadOnlyList<WorthIt.DefenderProfile> survivors, IReadOnlyList<int> sourceIndices)
        {
            opposition = opposition ?? Array.Empty<WorthIt.DefendingArmy>();
            survivors = survivors ?? Array.Empty<WorthIt.DefenderProfile>();
            var perArmy = new List<WorthIt.DefenderProfile>[opposition.Count];
            var armyOf = new List<int>();
            for (int a = 0; a < opposition.Count; a++)
            {
                perArmy[a] = new List<WorthIt.DefenderProfile>();
                int n = opposition[a].Units?.Count ?? 0;
                for (int k = 0; k < n; k++)
                    armyOf.Add(a);
            }
            for (int s = 0; s < survivors.Count; s++)
            {
                int source = sourceIndices != null && s < sourceIndices.Count ? sourceIndices[s] : s;
                if (source >= 0 && source < armyOf.Count)
                    perArmy[armyOf[source]].Add(survivors[s]);
            }
            var result = new List<WorthIt.DefendingArmy>();
            for (int a = 0; a < opposition.Count; a++)
                if (perArmy[a].Count > 0)
                    result.Add(new WorthIt.DefendingArmy(perArmy[a], opposition[a].Commander));
            return result;
        }

        private static IReadOnlyList<int> SourceIndices(
            AviationCombatEstimator.AirStrikeEstimate estimate, int defenderCount) =>
            estimate.SurvivorSourceIndices
            ?? Enumerable.Range(0, Math.Min(defenderCount, estimate.ExpectedDefendersAfter.Count)).ToList();

        // The own base the wing lands at: nearest to the target, then by coordinates.
        internal static HexCoord? LandingBase(WorldSnapshot snap, HexCoord targetHex)
        {
            List<HexCoord> bases = (snap?.Self?.BaseHexes ?? Array.Empty<HexCoord>())
                .Distinct().ToList();
            if (bases.Count == 0)
                return null;
            return bases.OrderBy(x => HexGridMath.Distance(x, targetHex))
                .ThenBy(x => x.Q).ThenBy(x => x.R).First();
        }

        // Turns the wing needs to reach `target`: this turn's remaining movement first.
        internal static int SortieEta(ArmySnapshot wing, HexCoord target) =>
            AiV2Util.TurnsToCover(wing, HexGridMath.Distance(wing.Hex, target));

        // The requirements of a support sortie leg: the wing's activation AP and launch Energy
        // (none once activated this turn), its distance and ETA.
        internal static MissionRequirements LegRequirements(ArmySnapshot wing, HexCoord destination,
            int etaTurns)
        {
            float activation = wing != null && !wing.HasActivatedThisTurn ? wing.ActivationApCost : 0f;
            float energy = wing != null && !wing.HasActivatedThisTurn ? wing.ActivationEnergyCost : 0f;
            return new MissionRequirements
            {
                RequiresArmy = true, RequiresHero = false, MoverKnown = wing != null,
                ApMinimum = activation, ApDesired = activation, ApMaximum = activation,
                EnergyMinimum = energy, EnergyDesired = energy, EnergyMaximum = energy,
                EstimatedDistance = wing == null ? 0 : HexGridMath.Distance(wing.Hex, destination),
                EtaTurns = Math.Max(1, etaTurns),
            };
        }

        // Is this wing still a valid air army flying a sortie right now?
        internal static bool SortieLive(PlayerSetupData player, int? wingArmyId, out bool wingValid)
        {
            ArmyData wing = wingArmyId.HasValue ? AiV2Util.ResolveArmy(player, wingArmyId.Value) : null;
            wingValid = wing != null && AviationRules.IsValidAirArmy(wing);
            AirSortie sortie = wingValid ? AirSortieRegistry.ForArmy(player, wing) : null;
            // Ground support owns only the strike leg. Once execution converts the sortie to
            // Rebase after the strike, the mission releases the wing and generic aviation
            // obligations own the physical return.
            return sortie != null && sortie.Kind == AirSortieKind.Strike;
        }

        // An airborne strike sortie is a physical landing obligation. When no operation holds its
        // wing any more (GroundCombatLegs.HeldAirSupportArmyId — the operation retired, released
        // the wing, or its leg failed), it is turned into the existing landing obligation: the
        // same sortie, homebound to its own landing base, continued by
        // AviationRebasePlanner.FindMandatoryContinuations (settled before any card play —
        // AviationObligations). Only this owner creates Strike sorties (GroundCombatLegStep).
        internal static void ReleaseOrphanStrikes(PlayerSetupData player, IEnumerable<MissionIntent> intents)
        {
            if (player == null)
                return;
            var held = new HashSet<int>((intents ?? Enumerable.Empty<MissionIntent>())
                .Select(GroundCombatLegs.HeldAirSupportArmyId)
                .Where(id => id.HasValue).Select(id => id.Value));
            foreach (AirSortie sortie in AirSortieRegistry.For(player).ToList())
            {
                if (sortie == null || sortie.Kind != AirSortieKind.Strike || sortie.Army == null
                    || held.Contains(sortie.Army.Id))
                    continue;
                sortie.Kind = AirSortieKind.Rebase;
                sortie.Outbound = false;
                sortie.TargetHex = sortie.LandingHex;
                AiDebugLog.Write($"[AI][V2][AirSupport] wing #{sortie.Army.Id} no longer held by an "
                    + $"operation — flies home to ({sortie.LandingHex.Q},{sortie.LandingHex.R})");
            }
        }

        // ---- provisioning -------------------------------------------------------------------

        // The wing itself: alive, ours, not claimed this pass, and either free of any sortie or
        // already flying a strike sortie toward this very target (or home from it).
        internal static bool TryResolveWing(PlayerSetupData player, ProvisioningSession session,
            int wingArmyId, HexCoord targetHex, string lane, out AirSupportWing resolved,
            out ProvisionFailure failure)
        {
            resolved = default;
            failure = default;
            ArmyData wing = AiV2Util.ResolveArmy(player, wingArmyId);
            if (wing == null || !AviationRules.IsValidAirArmy(wing) || wing.Owner != player
                || wing.Members.Count == 0 || session.ClaimedArmyIds.Contains(wing.Id))
            {
                failure = ProvisionFailure.MoverContended($"{lane} support wing #{wingArmyId} unavailable");
                return false;
            }
            AirSortie active = AirSortieRegistry.ForArmy(player, wing);
            bool continuing = active != null && active.Kind == AirSortieKind.Strike
                && (active.Outbound && active.TargetHex.Equals(targetHex) || !active.Outbound);
            bool returning = active != null && active.Kind == AirSortieKind.Strike && !active.Outbound;
            if (active != null && !continuing)
            {
                failure = ProvisionFailure.MoverContended($"wing #{wing.Id} is reserved by another sortie");
                return false;
            }
            resolved = new AirSupportWing(wing, active, continuing, returning);
            return true;
        }

        // The rest of the sortie: a recoverable route (unless it continues one), a strike
        // that really moves the lane's fight (`winAgainst`, the same read the lane planned on), and
        // the AP/Energy the funded envelope and the spendable Energy allow, including the next activation when the route spans turns.
        internal static bool TryFinishWing(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded, AirSupportWing w, HexCoord targetHex,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float knownDefense, float knownAttack,
            AirStrikePolicy policy, Func<IReadOnlyList<WorthIt.DefendingArmy>, float> winAgainst,
            string lane, float eps, out HexCoord landing, out float ap, out float energy,
            out float nextTurnEnergy, out float nextTurnAp, out ProvisionFailure failure)
        {
            ArmyData wing = w.Wing;
            failure = default;
            ap = 0f;
            energy = 0f;
            nextTurnEnergy = 0f;
            nextTurnAp = 0f;
            int requiredTurns = 1;
            if (w.Returning)
            {
                // The strike leg is over. Prove the remaining landing route without sending the
                // wing back through the old combat target.
                HexCoord? sameTurn = AiAirSortiePlanner.TryReplan(wing, ctx.Map, player);
                MultiTurnSortie? multi = sameTurn.HasValue ? null
                    : AiAirSortiePlanner.TryReplanMultiTurnReturn(wing, ctx.Map, player);
                if (!sameTurn.HasValue && !multi.HasValue)
                {
                    landing = default;
                    failure = ProvisionFailure.NoExecutableStep(
                        $"wing #{wing.Id} has no recoverable landing route after {lane} support");
                    return false;
                }
                landing = sameTurn ?? multi.Value.LandingHex;
                requiredTurns = multi?.RequiredTurns ?? 1;
            }
            else
            {
                // New and continuing outbound wings use the same live route proof.
                Sortie? sameTurn = AiAirSortiePlanner.TryPlanSortie(wing, targetHex, ctx.Map, player);
                MultiTurnSortie? multi = sameTurn.HasValue ? null
                    : AiAirSortiePlanner.TryPlanMultiTurnSortie(wing, targetHex, ctx.Map, player);
                if (!sameTurn.HasValue && !multi.HasValue)
                {
                    landing = default;
                    failure = ProvisionFailure.NoExecutableStep(
                        $"wing #{wing.Id} has no recoverable route to the {lane} target");
                    return false;
                }
                landing = sameTurn?.LandingHex ?? multi.Value.LandingHex;
                requiredTurns = sameTurn.HasValue ? 1 : multi.Value.RequiredTurns;

                if (!w.Continuing)
                {
                    opposition = opposition ?? Array.Empty<WorthIt.DefendingArmy>();
                    List<WorthIt.DefenderProfile> defenders = WorthIt.UnitsOf(opposition);
                    AviationCombatEstimator.AirStrikeEstimate estimate =
                        AviationCombatEstimator.EstimateAirStrike(wing.Members, knownDefense, knownAttack,
                            defenders, policy);
                    float beforeWin = winAgainst(opposition);
                    float afterWin = winAgainst(AfterStrike(opposition, estimate.ExpectedDefendersAfter,
                        SourceIndices(estimate, defenders.Count)));
                    if (estimate.ExpectedDamage <= eps
                        || estimate.ExpectedDefendersAfter.Count < policy.MinimumSurvivors
                        || afterWin <= beforeWin + eps)
                    {
                        failure = ProvisionFailure.SortieNotWorthwhile(
                            $"{lane} air support does not improve the primary's projected odds");
                        return false;
                    }
                }
            }

            ap = wing.HasActivatedThisTurn ? 0f : wing.ActivationApCost;
            energy = wing.HasActivatedThisTurn ? 0f : wing.ActivationEnergyCost;
            if (ap > funded.Tentative.Ap + eps || energy > funded.PhysicalDraw.Energy + eps)
            {
                failure = ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(ap, new ResourceVector(0f, 0f, energy, 0f, 0f)),
                    $"{lane} support wing #{wing.Id} exceeds AP/Energy envelope");
                return false;
            }
            float energyLeft = ProvisioningManager.AirSpendableEnergyLeft(player, root, ctx, session);
            if (energy > energyLeft + eps)
            {
                failure = ProvisionFailure.MoverContended(
                    $"spendable Energy exhausted: {lane} support wing #{wing.Id} needs {energy:0.##}, "
                    + $"{energyLeft:0.##} left after reservations and earlier claims this pass");
                return false;
            }

            if (requiredTurns > 1)
            {
                nextTurnEnergy = Math.Max(0, wing.ActivationEnergyCost);
                nextTurnAp = Math.Max(0, wing.ActivationApCost);
                if (!AviationContinuationBudget.CanGuaranteeNextActivation(
                        player, ctx.Map, energyLeft - energy,
                        session.NextTurnAirEnergyClaimed + nextTurnEnergy,
                        session.NextTurnAirApClaimed + nextTurnAp,
                        out string continuationBlock))
                {
                    failure = ProvisionFailure.NoExecutableStep(
                        $"{lane} support wing #{wing.Id} cannot guarantee next-turn activation ({continuationBlock})");
                    return false;
                }
            }
            return true;
        }
    }
}
