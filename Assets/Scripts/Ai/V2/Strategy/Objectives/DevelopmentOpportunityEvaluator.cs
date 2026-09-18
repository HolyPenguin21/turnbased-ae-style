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
    // ===========================================================================================
    //  DEVELOPMENT OPPORTUNITY EVALUATOR
    // ===========================================================================================
    //  Turns the shared snapshot.Development readiness into concrete upgrade opportunities.
    //  One opportunity = "run THIS catalog card at THIS facility for THIS recipient". Equipment
    //  only: Unit/Hero generation stays with MaterializationChainEnumerator. Preparation owns the
    //  prerequisite investment EV; a READY facility's operational card decision belongs to the
    //  canonical StrategicCardEvaluator and Phase-A portfolio, not another investment EV gate.
    //  The live recipient and Challenge admission remain gameplay-executor responsibilities.
    // ===========================================================================================
    public enum DevRecipientKind { HandCard, GarrisonUnit, FieldUnit }

    public sealed class DevelopmentOpportunity
    {
        public ResearchProductionMode Mode;
        public HexCoord FacilityHex;
        public CardDefinition Card;
        public bool ProducesEquipment;
        public ResourceBundle StakeCost;
        public float SuccessChance;
        public GenerationStep Generation;
        public CardData PreparationFacilityCard;
        public CardData PreparationOperatorCard;

        public DevRecipientKind RecipientKind;
        public CardData RecipientCard;
        public UnitData RecipientUnit;
        public string RecipientLabel;

        public float ExpectedGain;
        public float AlternativeValue;
        public float Ev;
        public float ExpectedApCost;
        public float ResourceCostValue;
        public float ProductionSupport = 1f;
        public float BaseValue;
        public string Explain = "";
    }

    public static class DevelopmentOpportunityEvaluator
    {
        public static List<DevelopmentOpportunity> Enumerate(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, IReadOnlyList<AggressionObjective> aggObjectives,
            System.Func<DevelopmentOpportunity, bool> supportsNeed = null)
        {
            var result = new List<DevelopmentOpportunity>();
            DevelopmentReadiness rd = snap?.Development;
            if (rd == null || rd.Offerings.Count == 0 || player == null || root == null)
                return result;

            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);

            int skippedNonEquip = 0;
            foreach (DevelopmentOffering off in rd.Offerings)
            {
                string card = off.Card != null ? off.Card.displayName : "?";
                if (!off.ProducesEquipment)
                {
                    skippedNonEquip++;
                    AiDebugLog.Write($"[AI][V2][Dev]   offering '{card}' {off.Mode} — SKIP not-equipment "
                        + $"(cardType {(off.Card != null ? off.Card.cardType.ToString() : "?")}); "
                        + "non-equipment R/P mints go through the materialization path, not this axis");
                    continue;
                }

                DevelopmentOpportunity best = BestEquipmentOpportunity(
                    off, snap, inv, player, root, hand, out string recipDiag, supportsNeed);
                if (best == null)
                {
                    AiDebugLog.Write($"[AI][V2][Dev]   offering '{card}' {off.Mode} p={off.SuccessChance:0.00} "
                        + $"— NO recipient: {recipDiag}");
                    continue;
                }

                int completeAp = ResearchProductionSystem.AttemptApCost(best.Card)
                                 + Mathf.Max(0, best.Card.activationApCost);
                if (!root.CanSpendActionPoints(completeAp))
                {
                    AiDebugLog.Write($"[AI][V2][Dev]   offering '{card}' {off.Mode} — REJECT "
                        + $"need {completeAp} AP for Challenge + attach");
                    continue;
                }

                // A built, staffed factory is a SUNK investment. Keep Score's EV and cost
                // breakdown for preparation diagnostics, but never use it as an admission veto
                // for an already-ready card. Readiness describes only the intrinsic, positive
                // marginal capability the card could add; StrategicCardEvaluator later prices
                // the complete live chain (Challenge/attach/resource/alternative) exactly once.
                Score(best, snap, root, hand);
                best.BaseValue = ReadyOpportunityValue(best);
                AiDebugLog.Write($"[AI][V2][Dev]   offering '{card}' {off.Mode} -> {best.RecipientLabel} "
                    + $"p={best.SuccessChance:0.00} G={best.ExpectedGain:0.0} A={best.AlternativeValue:0.0} "
                    + $"resCost={best.ResourceCostValue:0.##} apCost={best.ExpectedApCost:0.##} "
                    + $"prodSupport={best.ProductionSupport:0.00} investmentEV={best.Ev:0.00} "
                    + $"intrinsicNeed={best.BaseValue:0.00} "
                    + (best.BaseValue > 0f ? "=> ADMIT for card competition" : "=> REJECT no expected gain"));
                if (best.BaseValue <= 0f)
                    continue;
                best.Explain = $"{best.Mode} '{off.Card.displayName}' -> {best.RecipientLabel} "
                    + $"p={best.SuccessChance:0.00} G={best.ExpectedGain:0.0} "
                    + $"A={best.AlternativeValue:0.0} investmentEV={best.Ev:0.0}";
                result.Add(best);
            }

            result.Sort((a, b) => b.BaseValue.CompareTo(a.BaseValue));
            AiDebugLog.Write($"[AI][V2][Dev] objectives {result.Count} "
                + $"(offerings {rd.Offerings.Count}, non-equip skipped {skippedNonEquip})");
            foreach (DevelopmentOpportunity op in result)
                AiDebugLog.Write($"[AI][V2][Dev]   {op.Explain} base {op.BaseValue:0.0}");
            return result;
        }

        // This is a demand signal, NOT a second card-value score. It deliberately does not
        // include AP, resources, alternate hand cards, persistence or radar. The single shared
        // card scorer prices all of those at MaterializationCandidateBuilder.TopForDemand.
        internal static float ReadyOpportunityValue(DevelopmentOpportunity op) =>
            op == null ? 0f : Mathf.Clamp(AiConfigV2.devEvToBaseValue
                * Mathf.Clamp01(op.SuccessChance) * Mathf.Max(0f, op.ExpectedGain)
                / Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate), 0f, 100f);

        // Preparation witnesses use the same catalog, legal recipient enumeration and scorer
        // as a ready Challenge. They carry no GenerationStep and never authorize execution.
        internal static List<DevelopmentOpportunity> EnumeratePreparation(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            System.Func<DevelopmentOpportunity, bool> supportsNeed)
        {
            var result = new List<DevelopmentOpportunity>();
            if (snap?.Self?.BaseHexes == null || player == null || root == null
                || hand?.Hand == null || ctx?.ResearchProductionCatalog == null || supportsNeed == null)
                return result;
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            foreach (ResearchProductionMode mode in new[]
                { ResearchProductionMode.Research, ResearchProductionMode.Production })
            foreach (HexCoord hex in snap.Self.BaseHexes)
            {
                BuildingData building = BuildingRegistry.FindAt(hex);
                if (building == null || building.Owner != player
                    || Game.Combat.BattleInitiator.FindEnemyAt(hex, player) != null)
                    continue;
                bool facilityReady = building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode));
                if (snap.Development?.Facilities?.Any(f => f.Mode == mode && f.HasHero && !f.Contested) == true
                    || (!facilityReady && snap.Development?.Facilities?.Any(f => f.Mode == mode) == true))
                    continue;
                UnitData actor = ResearchProductionSystem.FindActor(player, hex, mode);
                if (facilityReady && actor != null)
                    continue;
                CardData facility = facilityReady ? null : hand.Hand
                    .Where(c => c?.Definition?.cardType == CardType.Facility
                        && c.Definition.grantedAbilities?.Contains(ResearchProductionSystem.FacilityAbility(mode)) == true)
                    .OrderBy(c => c.EffectivePlayApCost * AiConfigV2.devApValue
                        + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap))
                    .FirstOrDefault();
                if (!facilityReady && facility == null)
                    continue;
                ArmyData garrison = ArmyRegistry.AllAt(hex)
                    .FirstOrDefault(a => a.Owner == player && a.IsGarrison && !a.IsPrison);
                CardData operatorCard = actor != null ? null : hand.Hand
                    .Where(c => c?.Definition?.cardType == CardType.Hero
                        && MaterializationChainMatching.EffectiveAbilities(c.Definition, c.Equipment)
                            .Contains(ResearchProductionSystem.RoleAbility(mode))
                        && garrison != null && CardPlayExecutor.Preflight(player, root, hand, ctx,
                            CardPlayPlan.Into(c, hex, DeploymentKind.Garrison, garrison), out _))
                    .OrderBy(c => c.EffectivePlayApCost * AiConfigV2.devApValue
                        + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap))
                    .FirstOrDefault();
                if (actor == null && operatorCard == null)
                    continue;
                UnitData projectedActor = actor;
                if (projectedActor == null)
                {
                    int fate = operatorCard.Definition.fate;
                    if (operatorCard.Equipment?.equipment != null)
                    {
                        PredictedEquipmentState projected = EquipmentSystem.Predict(
                            operatorCard.Equipment.equipment,
                            new Dictionary<EquipmentStat, int> { [EquipmentStat.Fate] = fate },
                            operatorCard.Definition.grantedAbilities);
                        if (projected.Stats.TryGetValue(EquipmentStat.Fate, out int equippedFate))
                            fate = equippedFate;
                    }
                    projectedActor = new UnitData { Fate = Mathf.Max(0, fate), IsHero = true,
                        Owner = player, OriginatingCard = operatorCard.Definition,
                        Equipment = operatorCard.Equipment };
                    projectedActor.Abilities.UnionWith(MaterializationChainMatching.EffectiveAbilities(
                        operatorCard.Definition, operatorCard.Equipment));
                }
                float preparationCost = new[] { facility, operatorCard }.Where(c => c != null)
                    .Sum(c => c.EffectivePlayApCost * AiConfigV2.devApValue
                        + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap));
                foreach (CardDefinition card in ResearchProductionSystem.OfferedCards(
                    ctx.ResearchProductionCatalog, mode, player.Faction))
                {
                    if (card?.cardType != CardType.Equipment || card.equipment == null)
                        continue;
                    if (ResourceBundle.All.Any(t =>
                        (facility?.EffectivePlayResourceCost?.Get(t) ?? 0)
                        + (operatorCard?.EffectivePlayResourceCost?.Get(t) ?? 0)
                        + (card.resourceCost?.Get(t) ?? 0)
                        > StrategicSpendability.SpendableAmount(player, root, ctx, t)))
                        continue;
                    var off = new DevelopmentOffering
                    {
                        Mode = mode, FacilityHex = hex, Card = card, ProducesEquipment = true,
                        SuccessChance = ResearchProductionSystem.EstimateSuccessChance(projectedActor, card),
                    };
                    DevelopmentOpportunity op = BestEquipmentOpportunity(off, snap, inv, player,
                        root, hand, out _, supportsNeed);
                    if (op == null) continue;
                    Score(op, snap, root, hand);
                    op.PreparationFacilityCard = facility;
                    op.PreparationOperatorCard = operatorCard;
                    op.Ev -= preparationCost;
                    op.BaseValue = Mathf.Clamp(AiConfigV2.devEvToBaseValue * op.Ev, 0f, 100f);
                    if (op.Ev <= AiConfigV2.devEvMargin) continue;
                    op.Explain = $"prepare {mode} @({hex.Q},{hex.R}) for {card.displayName} -> "
                        + $"{op.RecipientLabel}; EV after prerequisites={op.Ev:0.##}";
                    result.Add(op);
                }
            }
            return result.OrderByDescending(o => o.BaseValue).ToList();
        }

        public static void Rescore(DevelopmentOpportunity op, WorldSnapshot snap, PlayerRoot root, AiHandData hand)
        {
            if (op == null)
                return;
            if (op.Generation?.Hero != null)
            {
                op.SuccessChance = ResearchProductionSystem.EstimateSuccessChance(
                    op.Generation.Hero, op.Card);
                op.Generation.SuccessChance = op.SuccessChance;
            }
            Score(op, snap, root, hand);
        }

        private static void Score(DevelopmentOpportunity op, WorldSnapshot snap, PlayerRoot root, AiHandData hand)
        {
            float surplusRetain = op.Generation != null ? 1f
                : 1f - Curves.Ramp(snap?.Development?.SurplusFraction ?? 0f,
                    AiConfigV2.devSurplusRampLo, AiConfigV2.devSurplusRampHi);
            float bestBeforeStake = BestAffordableHandUnitPower(hand, root, null);
            float bestAfterStake = BestAffordableHandUnitPower(hand, root, op.Card?.resourceCost);
            float displacedAlternative = Mathf.Max(0f, bestBeforeStake - bestAfterStake);
            float aTotal = AiConfigV2.devAlternativeWeight * surplusRetain * displacedAlternative;
            float challengeAp = ResearchProductionSystem.AttemptApCost(op.Card);
            float expectedAttachAp = op.SuccessChance * Mathf.Max(0, op.Card.activationApCost);
            op.ExpectedApCost = challengeAp + expectedAttachAp;

            op.AlternativeValue = aTotal;
            op.ResourceCostValue = StrategicCardEvaluator.StrategicResourceCostValue(op.Card?.resourceCost, snap);
            float persistentExpectedGain = op.SuccessChance * op.ExpectedGain
                * AiConfigV2.devEquipmentPersistenceMultiplier;
            op.ProductionSupport = op.Generation == null
                ? (snap?.Development?.ProductionSupport ?? AiConfigV2.productionSupportMin)
                : 1f;
            op.Ev = persistentExpectedGain * op.ProductionSupport
                - aTotal - op.ResourceCostValue
                - op.ExpectedApCost * AiConfigV2.devApValue;
            op.BaseValue = Mathf.Clamp(AiConfigV2.devEvToBaseValue * op.Ev, 0f, 100f);
        }

        private static DevelopmentOpportunity BestEquipmentOpportunity(DevelopmentOffering off,
            WorldSnapshot snap, CapabilityInventory inv, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, out string diag,
            System.Func<DevelopmentOpportunity, bool> supportsNeed = null)
        {
            diag = "no equipment grant on the card";
            EquipmentGrant grant = off.Card.equipment;
            if (grant == null)
                return null;
            CardData generatedPreview = ResearchProductionSystem.MintCard(off.Card);

            int handChecked = 0, mapChecked = 0, gainZero = 0;
            string lastReject = null;
            DevelopmentOpportunity best = null;
            float bestSelectionValue = float.NegativeInfinity;
            void Consider(DevelopmentOpportunity cand, ArmyData army = null)
            {
                if (cand == null || (supportsNeed != null && !supportsNeed(cand))) return;
                if (cand.ExpectedGain <= 0f) { gainZero++; return; }
                float selection = RecipientSelectionValue(cand, army, snap);
                if (best == null || selection > bestSelectionValue)
                {
                    best = cand;
                    bestSelectionValue = selection;
                }
            }

            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                {
                    if (c?.Definition == null) continue;
                    if (c.Definition.cardType != CardType.Unit && c.Definition.cardType != CardType.Hero) continue;
                    handChecked++;
                    if (!EquipmentSystem.CanAttach(generatedPreview, c, root, out string why))
                    { lastReject = why; continue; }
                    float gain = StrategicCardEvaluator.EquipmentUpgradeUtilityFor(
                        off.Card, c, snap, inv) * AiConfigV2.combatPowerPerBodyEstimate;
                    Consider(Make(off, DevRecipientKind.HandCard, c, null,
                        $"hand:{c.Definition.displayName}", gain));
                }

            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army == null || army.IsPrison) continue;
                foreach (UnitData u in army.Members)
                {
                    if (u == null || u.IsPrisoner) continue;
                    mapChecked++;
                    if (!EquipmentSystem.CanAttach(generatedPreview, u, root, out string whyU))
                    { lastReject = whyU; continue; }
                    float gain = StrategicCardEvaluator.EquipmentUpgradeUtilityFor(
                        off.Card, u, snap, inv) * AiConfigV2.combatPowerPerBodyEstimate;
                    Consider(Make(off, army.IsGarrison ? DevRecipientKind.GarrisonUnit : DevRecipientKind.FieldUnit,
                        null, u, $"{(army.IsGarrison ? "garr" : "field")}:{u.Name ?? "unit"}@{army.Hex.Q},{army.Hex.R}", gain), army);
                }
            }

            if (best == null)
                diag = $"hand checked {handChecked}, on-map checked {mapChecked}, "
                    + $"positive-gain 0 (zero-gain {gainZero})"
                    + (lastReject != null ? $"; last CanAttach reject: \"{lastReject}\"" : "; all attachable but gain <= 0");
            return best;
        }

        internal static float RecipientSelectionValue(DevelopmentOpportunity cand, ArmyData army,
            WorldSnapshot snap)
        {
            if (cand == null || cand.ExpectedGain <= 0f)
                return float.NegativeInfinity;
            return cand.ExpectedGain * (1f + EquipmentMatchupFit(cand, army, snap));
        }

        internal static float EquipmentMatchupFit(DevelopmentOpportunity cand, ArmyData army,
            WorldSnapshot snap)
        {
            IReadOnlyList<ArmySnapshot> enemies = snap?.TrueWorld?.EnemyArmies;
            EquipmentGrant grant = cand?.Card?.equipment;
            if (enemies == null || grant == null || enemies.Count == 0)
                return 0f;

            WorthIt.DefenderProfile handBefore = default;
            WorthIt.DefenderProfile handAfter = default;
            bool handUnit = cand.RecipientKind == DevRecipientKind.HandCard
                && cand.RecipientCard?.Definition?.cardType == CardType.Unit;
            if (handUnit)
            {
                CardDefinition host = cand.RecipientCard.Definition;
                EquipmentGrant existing = cand.RecipientCard.Equipment?.equipment;
                AiPower.ProjectedStrategicLine before = AiPower.EffectiveLine(host, existing);
                AiPower.ProjectedStrategicLine after = AiPower.EffectiveLine(host, existing, grant);
                WorthIt.DefenderProfile Profile(AiPower.ProjectedStrategicLine line) =>
                    new WorthIt.DefenderProfile(line.Defense,
                        line.EffectiveAbilities.Contains(UnitAbilities.CeramicArmor),
                        host.unitTypeTags, line.Attack, line.HitPoints, line.Initiative,
                        line.EffectiveAbilities);
                handBefore = Profile(before);
                handAfter = Profile(after);
            }

            int comparable = 0;
            int improved = 0;
            foreach (ArmySnapshot enemy in enemies)
            {
                IReadOnlyList<WorthIt.DefenderProfile> defenders = enemy?.Members;
                if (enemy == null || enemy.IsAir || defenders == null || defenders.Count == 0)
                    continue;
                comparable++;
                if (cand.RecipientUnit != null && army?.Members != null)
                {
                    if (DemandLayer.ImprovesRaidCombatOutcome(
                        cand.RecipientUnit, army.Members, grant, defenders))
                        improved++;
                }
                else if (handUnit)
                {
                    var beforeRoster = new[] { handBefore };
                    var afterRoster = new[] { handAfter };
                    bool coversBefore = WorthIt.CanDamageAll(beforeRoster, defenders);
                    bool coversAfter = WorthIt.CanDamageAll(afterRoster, defenders);
                    if (!coversAfter)
                        continue;
                    if (!coversBefore)
                    {
                        improved++;
                        continue;
                    }

                    // If this card already has enough penetration, defensive HP/Defense/Initiative
                    // changes can still be the real reason the attachment matters. Reuse the SAME
                    // full-roster WorthIt read as deployed recipients; never fall back to a private
                    // Attack+Defense heuristic.
                    WorthIt.BattleEstimate previous = WorthIt.Estimate(beforeRoster, defenders, 0f);
                    WorthIt.BattleEstimate next = WorthIt.Estimate(afterRoster, defenders, 0f);
                    if (next.WinChance > previous.WinChance
                        || (next.WinChance == previous.WinChance
                            && (next.ExpectedSurvivingHpRatioOnWin > previous.ExpectedSurvivingHpRatioOnWin
                                || next.CriticalAfterBattleChance < previous.CriticalAfterBattleChance)))
                        improved++;
                }
            }
            return comparable > 0 ? (float)improved / comparable : 0f;
        }

        private static DevelopmentOpportunity Make(DevelopmentOffering off, DevRecipientKind kind,
            CardData card, UnitData unit, string label, float gain) => new DevelopmentOpportunity
        {
            Mode = off.Mode,
            FacilityHex = off.FacilityHex,
            Card = off.Card,
            ProducesEquipment = off.ProducesEquipment,
            StakeCost = off.StakeCost,
            SuccessChance = off.SuccessChance,
            Generation = off.Generation,
            RecipientKind = kind,
            RecipientCard = card,
            RecipientUnit = unit,
            RecipientLabel = label,
            ExpectedGain = Mathf.Max(0f, gain),
        };

        private static float BestAffordableHandUnitPower(AiHandData hand, PlayerRoot root,
            ResourceCost committedResources)
        {
            if (hand?.Hand == null || root == null)
                return 0f;
            float best = 0f;
            foreach (CardData c in hand.Hand)
            {
                if (c?.Definition == null || c.Definition.cardType != CardType.Unit) continue;
                if (!root.CanSpendActionPoints(c.EffectivePlayApCost)) continue;
                if (!CanAffordAfterCommitment(c.EffectivePlayResourceCost, committedResources, root)) continue;
                best = Mathf.Max(best, AiPower.ToPowerUnit(c.Definition).BasePower);
            }
            return best;
        }

        private static bool CanAffordAfterCommitment(ResourceCost candidate, ResourceCost committed,
            PlayerRoot root)
        {
            if (root == null)
                return false;
            if (candidate == null)
                return true;

            int Committed(ResourceType type) => committed != null ? committed.Get(type) : 0;
            return root.GetResource(ResourceType.Human) - Committed(ResourceType.Human) >= candidate.human
                && root.GetResource(ResourceType.Energy) - Committed(ResourceType.Energy) >= candidate.energy
                && root.GetResource(ResourceType.Materials) - Committed(ResourceType.Materials) >= candidate.materials
                && root.GetResource(ResourceType.Tech) - Committed(ResourceType.Tech) >= candidate.tech;
        }
    }
}