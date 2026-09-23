using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §41/§43/§75 — ATTACK CAPABILITY SHORTAGE.
    //
    //  A mechanical partial of the existing Aggression demand owner. The ONLY thing that may create
    //  a demand here is a PROVEN structural shortage of a bound, live Attack operation: its primary
    //  no longer clears the target site, and no existing free army could fix that by joining it.
    //
    //  Explicitly NOT shortages (§41):
    //    * an army is already committed elsewhere      -> actor contention, the allocator's problem
    //    * the primary has 0 MP this turn              -> a timing fact, not a capability gap
    //    * there is no AP left this turn               -> a budget fact, not a capability gap
    //    * an existing free army could reinforce       -> use it; nothing needs to be materialised
    //    * a side enemy on the route is too strong     -> ignore it (§14); it is not this operation
    //
    //  Production never invents a war (§43): every demand below names an EXISTING intent whose
    //  physical deficit was measured against a REAL known defender package.
    // ===========================================================================================
    public static partial class AggressionDemandEvaluator
    {
        internal static void AppendAttackDemands(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            CapabilityInventory inv, List<string> diag, List<AxisDemand> demands)
        {
            if (snap?.Self == null || activeIntents == null)
                return;

            foreach (MissionIntent i in activeIntents)
            {
                AttackIntent ai = i?.Kind == MissionKind.Attack ? i.Attack : null;
                if (ai == null || i.Status != IntentStatus.Active || !ai.Target.HasValue)
                    continue;

                // A walking-home leg needs no combat capability at all.
                if (ai.Phase == AttackMissionPhase.RecoveryReturn
                    || ai.Phase == AttackMissionPhase.SupportReturn)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + $"reason=attack_in_{ai.Phase.ToString().ToLowerInvariant()}_phase");
                    continue;
                }

                // Only a BOUND operation may ask for anything. Without a claimed primary there is
                // no proven obligation yet — the fresh-objective path owns that case.
                if (!ai.PrimaryArmyId.HasValue || commitments == null
                    || !commitments.IsArmyClaimed(ai.PrimaryArmyId.Value))
                    continue;
                int primaryId = ai.PrimaryArmyId.Value;

                IReadOnlyList<WorthIt.DefenderProfile> defenders =
                    AttackObjectiveEvaluator.KnownSiteDefenders(snap, ai.Target.Hex);
                float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(
                    snap, null, ai.Target.Hex);

                GroundCombatAssemblyPlan primaryPlan = GroundCombatAssemblyPlanner.PlanForArmyAt(
                    snap, defenders, primaryId, AiConfigV2.raidMinViableWinChance, hexBonus);
                if (primaryPlan.Feasible)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} primary={primaryId} "
                        + $"win={primaryPlan.ProjectedWinChance:0.00} "
                        + "reason=primary_clears_worthit_against_attack_site");
                    continue;
                }

                if (ai.SupportArmyId.HasValue)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} primary={primaryId} "
                        + $"support={ai.SupportArmyId.Value} "
                        + "reason=reinforcement_already_assigned_or_en_route");
                    continue;
                }

                if (ai.ReinforcementRequestedTurn == snap.TurnNumber)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + "reason=reinforcement_already_requested_this_turn");
                    continue;
                }

                // §41 — an EXISTING free army may already fix this. Nothing needs materialising.
                List<int> existing = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                    snap, primaryId, defenders, commitments.ClaimedArmyIdSet, hexBonus);
                if (existing.Count > 0)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} primary={primaryId} "
                        + $"candidates={existing.Count} "
                        + "reason=existing_free_army_available_as_support");
                    continue;
                }

                ArmySnapshot primary = snap.Self.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == primaryId);
                float sitePower = AiPower.EffectiveArmyPowerFromProfiles(defenders);
                float required = Mathf.Max(1f, sitePower * AiConfigV2.raidCombatPowerMargin);
                float deficit = Mathf.Max(1f, required - (primary?.EffectiveArmyPower ?? 0f));

                // If the power exists numerically but cannot PHYSICALLY be delivered as a separate
                // army, that is a bounded DEFER, never a phantom capability claim.
                if (!CanDeliverIndependentFieldArmy(snap, inv))
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} primary={primaryId} "
                        + $"reason=no_independent_field_army_deliverable deficit={deficit:0.#}");
                    continue;
                }

                AttackObjective objective = AttackObjectiveEvaluator.ForTrackedTarget(snap, ai.Target);
                TaskScore score = objective?.TaskScore ?? default;

                diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE intent={i.IntentKey} "
                    + $"target={ai.Target.DiagnosticLabel} capability=FieldCombatPower "
                    + $"shape=IndependentFieldArmy desired={deficit:0.#} primary={primaryId} "
                    + $"required={required:0.#} have={(primary?.EffectiveArmyPower ?? 0f):0.#} "
                    + $"hexDef={hexBonus:0.#} task={score.Value:0.##} "
                    + "reason=attack_primary_cannot_clear_known_site_defence");
                demands.Add(new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.FieldCombatPower,
                    DeliveryShape = CapabilityDeliveryShape.IndependentFieldArmy,
                    ConsumerIntentKey = i.IntentKey,
                    ConsumerMissionKind = MissionKind.Attack,
                    DesiredAmount = deficit,
                    RequiredCapabilityPower = deficit,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = primary?.Hex,
                    // The same world objective, so the same canonical intrinsic TaskScore. No
                    // synthetic lifecycle value is injected into this transport.
                    WorldTaskScore = score,
                    Value = score.Value,
                    Explain = $"attack {ai.Target.DiagnosticLabel}: primary #{primaryId} no longer clears "
                        + $"WorthIt against the site ({(primary?.EffectiveArmyPower ?? 0f):0.#} of "
                        + $"{required:0.#} needed, hex defence {hexBonus:0.#}); deliver ~{deficit:0.#} "
                        + $"field power as a SEPARATE support army; task={score.Value:0.##}",
                });
            }
        }
    }
}
