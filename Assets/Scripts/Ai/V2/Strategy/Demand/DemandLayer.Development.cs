using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
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
            IReadOnlyList<DevelopmentOpportunity> devOpportunities,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player, AiTurnContext ctx, PlayerRoot root)
        {
            // Radar §E — AxisDemand.Value is the demand's OWN intrinsic merit, never pre-scaled by
            // radar. Radar is applied exactly once, at the point competing spends are compared
            // (MissionProposal.EffectiveValue / the allocator); Demand/urgency thresholds must not
            // shift just because the radar weight moved.
            if (s?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=NONE reason=no_self_snapshot");
                yield break;
            }

            AiHandData hand = AiHandRegistry.Peek(player);
            int operatorPrerequisites = 0;
            // An investment is justified by its own legal output, positive recipient gain and EV,
            // not by a currently active Recon/Economy/Raid mission. EnumeratePreparation owns the
            // actual catalog/recipient/operator/preparation-cost checks. Other demands may provide
            // context to its evaluator, but cannot veto an independent production investment.
            // One best prerequisite per pass; the next settled pass sees the completed stage.
            DevelopmentOpportunity preparation = DevelopmentOpportunityEvaluator.EnumeratePreparation(
                s, player, root, hand, ctx, op => op != null && op.ExpectedGain > 0f).FirstOrDefault();
            if (preparation != null)
            {
                bool facilityReady = s.Development?.Facilities?.Any(f => f.Mode == preparation.Mode
                    && f.Hex.Equals(preparation.FacilityHex)) == true;
                operatorPrerequisites++;
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Development,
                    Capability = facilityReady ? CapabilityKind.DevelopmentOperator
                        : CapabilityKind.DevelopmentInfrastructure,
                    DesiredAmount = 1,
                    TargetHex = preparation.FacilityHex,
                    DevelopmentOperatorMode = preparation.Mode,
                    DevOpportunity = preparation,
                    Value = preparation.BaseValue,
                    Explain = preparation.Explain,
                };
            }
            // Filter illegal recipients before selecting the best one for each offering. The
            // opportunity evaluator already does this via CanAttach and signed card utility;
            // demanding a witness from another axis here would block useful autonomous Production.
            if (root != null && hand != null)
                devOpportunities = DevelopmentOpportunityEvaluator.Enumerate(
                    s, player, root, hand, null);
            // An unstaffed mode must not suppress real opportunities from another ready mode.
            int emitted = 0;
            if (devOpportunities != null)
                foreach (DevelopmentOpportunity op in devOpportunities)
                {
                    if (op == null || op.BaseValue <= 0f) continue;
                    emitted++;
                    yield return new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Development,
                        Capability = CapabilityKind.CardUpgrade,
                        DesiredAmount = 1,
                        RequiredTraits = TraitPreference.None,
                        MinimumFollowupAp = 0f,
                        TargetHex = op.FacilityHex,
                        Value = op.BaseValue,   // radar-blind — see Radar §E note above
                        DevOpportunity = op,
                        Explain = op.Explain,
                    };
                }

            if (emitted > 0)
                AiDebugLog.Write($"[AI][V2][Demand][Development] decision=UPGRADE count={emitted} "
                    + "reason=facility_ready_scored_opportunities");
            else if (operatorPrerequisites == 0)
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=SATISFIED "
                    + "reason=no_profitable_development_use");
        }


        // Production amplifies an already-owned need; never invent a mission. Recon and
        // Economy keep their existing evidence; a bound active Raid can ALSO witness equipment
        // when its own primary combat roster improves against the known target by WorthIt.
        // Independent reinforcement FieldCombatPower demands are NOT fulfilled by upgrading
        // their existing primary: only a separate deployable army can close those.
        internal static bool HasSupportedDevelopmentAxisDemand(DevelopmentOpportunity op,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player, WorldSnapshot snap = null)
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
            if (reconWitness && ImprovesReconCapability(op))
                return true;

            // Only an ACTUAL owned Raid primary may justify strengthening its existing unit.
            // Research/Production mode has no bearing here: the offered output must be Equipment.
            // A potential future raid (or an independent reinforcement demand) cannot create
            // a generic "upgrade the strongest body" entitlement without a named recipient.
            if (snap == null || op.RecipientUnit.IsHero || op.Card?.cardType != CardType.Equipment
                || op.Card.equipment == null || activeIntents == null)
                return false;
            foreach (MissionIntent intent in activeIntents)
            {
                RaidIntent raid = intent?.Raid;
                if (intent == null || intent.Status != IntentStatus.Active
                    || intent.Kind != MissionKind.Raid || raid == null
                    || !raid.Target.HasValue || raid.PrimaryArmyId != army.Id
                    || (raid.Phase != RaidMissionPhase.Assault
                        && raid.Phase != RaidMissionPhase.Reinforcement))
                    continue;
                var defenders = AiV2Util.KnownDefenders(snap, raid.Target);
                if (defenders.Count == 0)
                    continue;
                // The same defender-side base bonus enters both immutable projections.
                // Terrain is not present in the snapshot, so this is a marginal signal,
                // never a substitute for Raid's final live WorthIt admission.
                float hexBonus = WorthIt.HexDefenseBonus(raid.LastKnownHex, null);
                if (ImprovesRaidCombatOutcome(op.RecipientUnit, army.Members,
                    op.Card.equipment, defenders, hexBonus))
                    return true;
            }
            return false;
        }

        // WorthIt owns combat rules and simulation. EquipmentSystem owns the exact stat/ability
        // projection. Compare the SAME primary's roster before/after replacing only its recipient,
        // without mutating gameplay UnitData or pretending the grant created a new combat body.
        internal static bool ImprovesRaidCombatOutcome(UnitData recipient,
            IReadOnlyCollection<UnitData> members, EquipmentGrant grant,
            IReadOnlyCollection<WorthIt.DefenderProfile> defenders, float hexBonus = 0f)
        {
            if (recipient == null || recipient.IsHero || grant == null || members == null
                || defenders == null || defenders.Count == 0 || !members.Contains(recipient))
                return false;

            var before = new List<WorthIt.DefenderProfile>();
            var after = new List<WorthIt.DefenderProfile>();
            var stats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = recipient.Attack,
                [EquipmentStat.Defense] = recipient.Defense,
                [EquipmentStat.HitPoints] = recipient.HitPointsMax,
                [EquipmentStat.Initiative] = recipient.Initiative,
            };
            PredictedEquipmentState predicted = EquipmentSystem.Predict(grant, stats, recipient.Abilities);
            int attack = predicted.Stats.TryGetValue(EquipmentStat.Attack, out int atk)
                ? atk : recipient.Attack;
            int defense = predicted.Stats.TryGetValue(EquipmentStat.Defense, out int def)
                ? def : recipient.Defense;
            int maxHp = predicted.Stats.TryGetValue(EquipmentStat.HitPoints, out int hp)
                ? hp : recipient.HitPointsMax;
            int currentHp = Mathf.Clamp(recipient.HitPointsCurrent
                + Mathf.Max(0, maxHp - recipient.HitPointsMax), 1, maxHp);
            int initiative = predicted.Stats.TryGetValue(EquipmentStat.Initiative, out int init)
                ? init : recipient.Initiative;
            var projected = new WorthIt.DefenderProfile(defense,
                predicted.Abilities.Contains(UnitAbilities.CeramicArmor), recipient.TypeTags.ToList(),
                attack, currentHp, initiative, predicted.Abilities, maxHp);

            foreach (UnitData unit in members)
            {
                if (unit == null || unit.IsHero)
                    continue;
                before.Add(WorthIt.FromLiveUnit(unit));
                after.Add(object.ReferenceEquals(unit, recipient) ? projected : WorthIt.FromLiveUnit(unit));
            }
            bool coversBefore = WorthIt.CanDamageAll(before, defenders, hexBonus);
            bool coversAfter = WorthIt.CanDamageAll(after, defenders, hexBonus);
            if (!coversAfter)
                return false;
            if (!coversBefore)
                return true;

            WorthIt.BattleEstimate previous = WorthIt.Estimate(before, defenders, hexBonus);
            WorthIt.BattleEstimate improved = WorthIt.Estimate(after, defenders, hexBonus);
            return improved.WinChance > previous.WinChance
                || (improved.WinChance == previous.WinChance
                    && (improved.ExpectedSurvivingHpRatioOnWin > previous.ExpectedSurvivingHpRatioOnWin
                        || improved.CriticalAfterBattleChance < previous.CriticalAfterBattleChance));
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
            // Compare two states normalized by the same gameplay-owned predictor. In
            // particular, a host that already has RapidReaction already has effective activation
            // AP 0 before this grant; the grant must not receive credit for that existing ability.
            PredictedEquipmentState before = EquipmentSystem.Predict(
                null, beforeStats, beforeAbilities);
            PredictedEquipmentState after = EquipmentSystem.Predict(
                grant, beforeStats, beforeAbilities);
            int normalizedBeforeMove = before.Stats.TryGetValue(
                EquipmentStat.MoveMax, out int beforePredictedMove)
                ? beforePredictedMove : beforeMove;
            int normalizedBeforeActivation = before.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int beforePredictedActivation)
                ? beforePredictedActivation : beforeActivation;
            int afterMove = after.Stats.TryGetValue(EquipmentStat.MoveMax, out int move)
                ? move : normalizedBeforeMove;
            int afterActivation = after.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int activation)
                ? activation : normalizedBeforeActivation;
            return AbilityParams.GetBestRecceRadius(after.Abilities)
                    > AbilityParams.GetBestRecceRadius(before.Abilities)
                || AbilityParams.GetBestRecceSpotStrength(after.Abilities)
                    > AbilityParams.GetBestRecceSpotStrength(before.Abilities)
                || BestStealthLevel(after.Abilities) > BestStealthLevel(before.Abilities)
                || afterMove > normalizedBeforeMove
                || afterActivation < normalizedBeforeActivation;
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
