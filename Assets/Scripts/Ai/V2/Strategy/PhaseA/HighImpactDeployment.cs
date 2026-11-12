using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // One opening opportunity, before discretionary card chains and army movement. This is
    // still ordinary materialization: existing plans, scoring, claims, preflight and executor.
    internal static class HighImpactDeployment
    {
        internal const int MinimumPlayAp = 7;
        internal const float MinimumPowerImprovement = 1.25f;

        // A held heavy card must remain affordable through the pre-scan refill. This is only
        // a temporary draw limit, established by the ordinary deployment preflight; if strategic
        // admission declines the card, Phase B can draw with the remaining AP as usual.
        internal static int RefillApToKeep(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
        {
            if (player == null || root == null || hand == null || ctx == null
                || !hand.Hand.Any(IsHeavyHandCard)) return 0;
            float spendable = StrategicSpendability.SpendableAp(player, root, ctx)
                - AviationObligations.ActivationAp(player, ctx);
            var world = OwnWorld.Of(player);
            var bases = world.Buildings.Where(b => b.IsBase).Select(b => b.Hex).ToHashSet();
            var armies = world.Armies.Where(a => !a.IsGarrison && !a.IsPrison
                && bases.Contains(a.Hex)).ToList();
            float strongest = world.Armies.Where(a => !a.IsPrison).SelectMany(a => a.Members)
                .Where(u => u != null && !u.IsHero && !u.IsAviation && !u.IsPrisoner)
                .Select(u => AiPower.EffectiveArmyPower(new[] { AiPower.ToPowerUnit(u) }))
                .DefaultIfEmpty(0f).Max();
            int keep = 0;
            foreach (CardData card in hand.Hand)
            {
                if (!IsHeavyHandCard(card) || AiPower.EffectiveArmyPower(new[] { AiPower.ToPowerUnit(card) })
                        <= strongest * MinimumPowerImprovement) continue;
                int cheapest = int.MaxValue;
                var options = armies.Select(a => CardPlayPlan.Into(card, a.Hex,
                        a.Members.Count == 0 ? DeploymentKind.ReusableShell : DeploymentKind.ExistingArmy, a))
                    .Concat(bases.Select(h => CardPlayPlan.NewArmyAt(card, h)));
                foreach (CardPlayPlan option in options)
                {
                    int followup = AiPower.EffectiveCardLine(card).ActivationApCost
                        + (option.TargetArmy?.PendingActivationApCost ?? 0);
                    float credit = option.TargetArmy == null ? 0f : StrategicSpendability.OperationContinuationCredit(
                        player, root, ctx, option.TargetArmy.Id);
                    int cost = option.TotalApCost + Mathf.CeilToInt(UnprotectedFollowupAp(followup, credit));
                    if (cost <= spendable && CardPlayExecutor.Preflight(player, root, hand, ctx, option,
                            out _, resourceForecast: true)
                        && TradeFunding.CanFund(player, root, ctx, CardCostRules.PlayResources(card)))
                        cheapest = Math.Min(cheapest, cost);
                }
                if (cheapest != int.MaxValue) keep = Math.Max(keep, cheapest);
            }
            return keep;
        }

        private static bool IsHeavyHandCard(CardData card) =>
            card?.Definition?.cardType == CardType.Unit && !card.Definition.isAviation
            && CardCostRules.PlayAp(card) >= MinimumPlayAp;

        internal static float UnprotectedFollowupAp(float activation, float continuationCredit,
            float promisedFollowup = 0f) => Mathf.Max(0f, activation - continuationCredit - promisedFollowup);

        internal static bool HasCriticalDefence(WorldSnapshot snap, IEnumerable<AxisDemand> demands) =>
            snap?.Threat?.UnderSiege == true
            || (snap?.Threat?.Threats?.Any(t => t != null && (!t.EnemyEta.HasValue || t.EnemyEta <= 1)
                && t.Severity >= AiConfigV2.threatSeverityTrigger) ?? false)
            || (demands?.Any(d => d?.ConsumerMissionKind == MissionKind.ActiveDefence
                && (!d.ActiveDefenceEnemyEta.HasValue || d.ActiveDefenceEnemyEta <= 1)) ?? false);

        internal static bool IsCandidate(WorldSnapshot snap, MaterializationPlan plan,
            float utility, float availableAp, float followupAp)
        {
            CardData card = plan?.BaseCardInHand;
            if (snap?.Self == null || !IsHeavyHandCard(card)
                || plan.Kind != MaterializationChainKind.Direct
                || plan.Generation != null || plan.FinalCapability != CapabilityKind.FieldCombatPower
                || plan.Deploy.Kind == DeploymentKind.Garrison
                || utility <= AiConfigV2.tempoMinSpendUtility
                || plan.ApCost + followupAp > availableAp + AiConfigV2.allocatorSliceEpsilon
                || !StrikeRoster.IsPeakBody(snap, card.Definition))
                return false;
            float incoming = AiPower.EffectiveArmyPower(new[] { AiPower.ToPowerUnit(card) });
            float strongest = 0f;
            foreach (ArmySnapshot army in snap.Self.Armies ?? Array.Empty<ArmySnapshot>())
                foreach (var body in army?.Members ?? Array.Empty<Game.Combat.WorthIt.DefenderProfile>())
                    if (body.IsGroundCombatant && !body.IsHero)
                        strongest = Mathf.Max(strongest,
                            AiPower.EffectiveArmyPowerFromProfiles(new[] { body }));
            return incoming > 0f && incoming > strongest * MinimumPowerImprovement;
        }

        internal static void PrepareConstructionClaims(PhaseAPass pass)
        {
            PhaseAEconomyHolds.ProtectActiveBuilds(pass);
            PhaseAEconomyHolds.ClaimDemandBuildCards(pass);
            pass.Partition();
            PhaseAEconomyHolds.HoldPreIntentBuild(pass);
        }

        internal static bool TryPlay(PhaseAPass pass)
        {
            if (pass.Result.OpeningDeploymentConsidered || pass.Snap?.Self == null
                || pass.Root == null || pass.Hand == null || pass.Ctx == null
                || AviationObligations.Pending(pass.Player, pass.Ctx))
                return false;
            pass.Result.OpeningDeploymentConsidered = true;
            if (!pass.Hand.Hand.Any(IsHeavyHandCard)) return false;
            pass.Result.Reservation ??= new MaterializationReservation();
            if (HasCriticalDefence(pass.Snap, pass.Demands)
                || RadarValueScale.For(pass.Radar, DesireAxis.Aggression) <= 0f)
                return false;
            var inventory = CapabilityInventory.Build(pass.Snap, pass.Player, pass.Commitments);
            bool militaryNeed = BaselineForceReadiness.Evaluate(pass.Snap, inventory, pass.Hand.Hand).MilitaryWitnessed
                || (pass.ActiveIntents?.Any(i => i?.Status == IntentStatus.Active && i.Attack != null) ?? false)
                || (pass.Snap.Known?.Buildings?.Any(b =>
                    AttackObjectiveEvaluator.IsHostileStrategicStructure(b, pass.Player)) ?? false);
            if (!militaryNeed) return false;

            // Renew the same construction holds Phase A uses before any early spending. A
            // pending strike stays senior too; ordinary demand-driven deployment remains available.
            PrepareConstructionClaims(pass);
            if (PhaseAOperationSupport.Assess(pass) != null) return pass.Result.StateChanged;
            // Unresolved operational claims must constrain the surplus placement as they do later
            // in the turn. Do not deploy a card into a reserve that its real consumer cannot use.
            foreach (AxisDemand demand in pass.Demands)
                if (!pass.Reservation.UnresolvedDemands.Contains(demand))
                    pass.Reservation.UnresolvedDemands.Add(demand);
            float available = StrategicSpendability.SpendableAp(pass.Player, pass.Root, pass.Ctx)
                - pass.ApBudget.ReservedFollowup();
            foreach (var candidate in MaterializationCandidateBuilder.RankedSurplus(pass.Snap,
                pass.Player, pass.Root, pass.Hand, pass.Ctx, inventory, pass.Commitments, pass.Reservation))
            {
                MaterializationPlan plan = candidate.plan;
                float activation = AiPower.ProjectMaterialization(plan).ActivationApCost
                    + (plan.Deploy.Army?.PendingActivationApCost ?? 0);
                float continuationCredit = plan.Deploy.Army == null ? 0f
                    : StrategicSpendability.OperationContinuationCredit(pass.Player, pass.Root,
                        pass.Ctx, plan.Deploy.Army.Id);
                float promised = plan.Deploy.Army == null ? 0f
                    : pass.ApBudget.ActorFollowup(plan.Deploy.Army.Id);
                float followup = UnprotectedFollowupAp(activation, continuationCredit, promised);
                if (!IsCandidate(pass.Snap, plan, candidate.utility, available, followup)) continue;
                var before = new HashSet<int>((pass.Snap.Self.Armies ?? Array.Empty<ArmySnapshot>())
                    .Where(a => a != null).Select(a => a.ArmyId));
                MaterializationResult played = MaterializationExecutor.Execute(pass.Snap, pass.Player,
                    pass.Root, pass.Hand, pass.Ctx, plan, pass.Commitments);
                pass.Result.RecordMaterialization(pass.Player, pass.Turn, plan, played);
                if (played.Deployed)
                {
                    pass.Result.CardsPlayed++;
                    ArmyData recipient = plan.Deploy.Army ?? OwnWorld.Of(pass.Player).Armies
                        .FirstOrDefault(a => !before.Contains(a.Id));
                    float afterCredit = recipient == null ? 0f : StrategicSpendability.OperationContinuationCredit(
                        pass.Player, pass.Root, pass.Ctx, recipient.Id);
                    pass.ApBudget.ReserveDeliveryFollowup(recipient,
                        UnprotectedFollowupAp(activation, afterCredit));
                    StrategicInterruptRegistry.MarkCapabilityChanged(pass.Player, pass.Turn, pass.Hand);
                }
                AiDebugLog.Write($"[AI][V2][OpeningUnit] card={plan.BaseCardInHand.Definition.displayName} "
                    + $"deployed={played.Deployed} ap={played.ApSpent} followup={followup} "
                    + $"utility={candidate.utility} reason={played.FailReason}");
                // Failure is not a licence to try several expensive transactions in one opening.
                return pass.Result.StateChanged;
            }
            // Renewing construction claims may also release/retarget an obsolete intent.
            // Its caller must refresh ownership even if no unit transaction was selected.
            return pass.Result.StateChanged;
        }
    }
}
