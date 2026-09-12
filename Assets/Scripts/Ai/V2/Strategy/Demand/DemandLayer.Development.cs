using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // DevelopmentDemands and its private helpers.
    // File-split (mechanical, no behaviour change) from DemandLayer.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 4. Still exactly the DemandLayer
    // class; only this axis's slice moved to its own file.
    public static partial class DemandLayer
    {
        private static IEnumerable<AxisDemand> DevelopmentDemands(WorldSnapshot s, DesireBreakdown b,
            IReadOnlyList<DevelopmentOpportunity> devOpportunities, Radar radar,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player)
        {
            float devScale = radar != null ? RadarValueScale.For(radar, DesireAxis.Development) : 1f;
            if (s?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=NONE reason=no_self_snapshot");
                yield break;
            }

            if (!s.Self.HasDevFacility)
            {
                if (s.Self.BaseHexes == null || s.Self.BaseHexes.Count == 0)
                {
                    AiDebugLog.Write("[AI][V2][Demand][Development] decision=NONE reason=no_base_to_expand");
                    yield break;
                }
                HexCoord anchor = s.Self.BaseHexes[0];
                AiDebugLog.Write($"[AI][V2][Demand][Development] decision=CREATE anchor=({anchor.Q},{anchor.R}) "
                    + "capability=DevelopmentInfrastructure desired=1 reason=no_research_production_facility");
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Development,
                    Capability = CapabilityKind.DevelopmentInfrastructure,
                    DesiredAmount = 1,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = anchor,
                    Value = 45f,
                    Explain = "no Research/Production facility — Development axis has no operator base",
                };
                yield break;
            }

            // Facility built but no qualifying operator hero -> stage the hero. PER MODE: Research and
            // Production need DIFFERENT hero abilities (Researcher vs Assembler), so a staffed b_Lab
            // must NOT suppress the b_Factory operator demand — each unstaffed mode gets its own.
            DevelopmentReadiness rd = s.Development;
            int operatorPrerequisites = 0;
            if (rd != null && rd.Facilities != null)
            {
                foreach (ResearchProductionMode mode in new[]
                    { ResearchProductionMode.Research, ResearchProductionMode.Production })
                {
                    bool modeStaffed = false, modeHasOpenFacility = false;
                    HexCoord at = default;
                    foreach (DevelopmentFacility f in rd.Facilities)
                    {
                        if (f.Mode != mode) continue;
                        if (f.HasHero) { modeStaffed = true; break; }
                        if (!f.Contested && !modeHasOpenFacility)
                        {
                            modeHasOpenFacility = true;
                            at = f.Hex;
                        }
                    }
                    if (modeStaffed || !modeHasOpenFacility)
                        continue;

                    bool haveCard = mode == ResearchProductionMode.Research
                        ? rd.ResearcherCardInHand
                        : rd.AssemblerCardInHand;
                    operatorPrerequisites++;
                    AiDebugLog.Write($"[AI][V2][Demand][Development] decision=CREATE anchor=({at.Q},{at.R}) "
                        + $"capability=DevelopmentOperator mode={mode} desired=1 "
                        + $"reason={(haveCard ? "unstaffed_facility_operator_card_in_hand" : "unstaffed_facility_no_operator_card_yet")}");
                    yield return new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Development,
                        Capability = CapabilityKind.DevelopmentOperator,
                        DesiredAmount = 1,
                        RequiredTraits = TraitPreference.None,
                        MinimumFollowupAp = 0f,
                        TargetHex = at,
                        DevelopmentOperatorMode = mode,
                        Value = 45f * devScale,
                        Explain = $"facility @({at.Q},{at.R}) has no {mode} operator — "
                            + "Development axis cannot run a Challenge until a qualifying hero stands on it",
                    };
                }
            }
            // An unstaffed mode must not suppress real opportunities from another ready mode.
            int emitted = 0;
            if (devOpportunities != null)
                foreach (DevelopmentOpportunity op in devOpportunities)
                {
                    if (op == null || op.BaseValue <= 0f) continue;
                    if (!HasSupportedDevelopmentAxisDemand(
                        op, formedDemands, activeIntents, player))
                    {
                        AiDebugLog.Write($"[AI][V2][Demand][Development] decision=REJECT "
                            + $"card={op.Card?.displayName ?? "?"} recipient={op.RecipientLabel ?? "?"} "
                            + "reason=no_supported_axis_demand");
                        continue;
                    }
                    emitted++;
                    yield return new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Development,
                        Capability = CapabilityKind.CardUpgrade,
                        DesiredAmount = 1,
                        RequiredTraits = TraitPreference.None,
                        MinimumFollowupAp = 0f,
                        TargetHex = op.FacilityHex,
                        Value = op.BaseValue * devScale,   // radar model #1a — Development weight scales merit
                        DevOpportunity = op,
                        Explain = op.Explain,
                    };
                }

            if (emitted > 0)
                AiDebugLog.Write($"[AI][V2][Demand][Development] decision=UPGRADE count={emitted} "
                    + "reason=facility_ready_scored_opportunities");
            else if (operatorPrerequisites == 0)
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=SATISFIED "
                    + "reason=facility_ready_no_worthwhile_upgrade");
        }


        // Production amplifies an already-owned need; it never originates one. In the current
        // scope only a real Recon capability delta or the exact builder of an Economy obligation
        // is a valid witness. Attack/Defence matching remains with WorthIt when those axes return.
        internal static bool HasSupportedDevelopmentAxisDemand(DevelopmentOpportunity op,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player)
        {
            if (op == null)
                return false;

            bool hasReconDemand = formedDemands?.Any(d => d != null
                && d.RequestingAxis == DesireAxis.Recon
                && d.Capability == CapabilityKind.ScoutCapability) == true;
            if (op.RecipientKind == DevRecipientKind.HandCard)
                return hasReconDemand && ImprovesReconCapability(op);

            if (op.RecipientUnit == null || player == null)
                return false;
            ArmyData army = ArmyRegistry.AllForOwner(player)
                .FirstOrDefault(a => a?.Members != null && a.Members.Contains(op.RecipientUnit));
            if (army == null)
                return false;

            bool economyWitness = formedDemands?.Any(d => d != null
                    && d.RequestingAxis == DesireAxis.Economy
                    && d.EconomyPreferredBuilderArmyId == army.Id) == true
                || activeIntents?.Any(i => i != null && i.Status == IntentStatus.Active
                    && i.Kind == MissionKind.Economy
                    // A builder already walking home (ReturnBuilder) has no outstanding build
                    // obligation left — it cannot justify a fresh Production/CardUpgrade demand.
                    && (i.Economy?.Kind == EconomyTaskKind.BuildExtraction
                        || i.Economy?.Kind == EconomyTaskKind.FoundBase)
                    && (i.PreferredMoverArmyId == army.Id
                        || i.Economy?.BuilderArmyId == army.Id)) == true;
            if (economyWitness)
                return true;

            bool reconWitness = activeIntents?.Any(i => i != null
                && i.Status == IntentStatus.Active && i.Kind == MissionKind.Scout
                && i.PreferredMoverArmyId == army.Id) == true;
            return reconWitness && ImprovesReconCapability(op);
        }

        private static bool ImprovesReconCapability(DevelopmentOpportunity op)
        {
            EquipmentGrant grant = op?.Card?.equipment;
            if (grant == null)
                return false;

            IEnumerable<string> beforeAbilities;
            int beforeMove;
            int beforeActivation;
            if (op.RecipientKind == DevRecipientKind.HandCard)
            {
                CardDefinition host = op.RecipientCard?.Definition;
                if (host == null)
                    return false;
                beforeAbilities = EquipmentSystem.EffectiveAbilities(
                    host.grantedAbilities, op.RecipientCard.Equipment?.equipment);
                beforeMove = host.moveMax;
                beforeActivation = host.activationApCost;
            }
            else
            {
                if (op.RecipientUnit == null)
                    return false;
                beforeAbilities = op.RecipientUnit.Abilities;
                beforeMove = op.RecipientUnit.MoveMax;
                beforeActivation = op.RecipientUnit.ActivationApCost;
            }

            var beforeStats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.MoveMax] = beforeMove,
                [EquipmentStat.ActivationApCost] = beforeActivation,
            };
            PredictedEquipmentState after = EquipmentSystem.Predict(
                grant, beforeStats, beforeAbilities);
            int afterMove = after.Stats.TryGetValue(EquipmentStat.MoveMax, out int move)
                ? move : beforeMove;
            int afterActivation = after.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int activation)
                ? activation : beforeActivation;
            return AbilityParams.GetBestRecceRadius(after.Abilities)
                    > AbilityParams.GetBestRecceRadius(beforeAbilities)
                || AbilityParams.GetBestRecceSpotStrength(after.Abilities)
                    > AbilityParams.GetBestRecceSpotStrength(beforeAbilities)
                || BestStealthLevel(after.Abilities) > BestStealthLevel(beforeAbilities)
                || afterMove > beforeMove
                || afterActivation < beforeActivation;
        }

        private static int BestStealthLevel(IEnumerable<string> abilities)
        {
            int best = 0;
            if (abilities == null)
                return best;
            foreach (string ability in abilities)
                if (AbilityParams.TryGetStealthLevel(ability, out int level))
                    best = System.Math.Max(best, level);
            return best;
        }

    }
}
