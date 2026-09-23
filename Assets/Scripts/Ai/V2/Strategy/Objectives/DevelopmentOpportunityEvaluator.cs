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
    //  READY opportunities represent Equipment recipients; Unit/Hero generation stays with
    //  MaterializationChainEnumerator. Preparation may project those deployable catalog outputs
    //  through the canonical scorer to justify an independent investment, never to mint them.
    //  Preparation owns the
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
        // Only an existing, eligible staffed source can mint an operator. The resulting card
        // goes into the hand first; deployment remains the usual DevelopmentOperator action.
        public GenerationStep PreparationOperatorGeneration;
        // Existing qualified hero selected for delivery, never misrepresented as an on-site
        // actor. Continuity pins this exact object until arrival or structural cancellation.
        public UnitData PreparationExistingHero;
        public int? PreparationSourceArmyId;
        public int PreparationTravelCost;

        public DevRecipientKind RecipientKind;
        public CardData RecipientCard;
        public UnitData RecipientUnit;
        // Stable owning-army identity captured at recipient selection. Demand must not rediscover
        // membership by scanning every live army from the UnitData reference a second time.
        public int? RecipientArmyId;
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
            System.Func<DevelopmentOpportunity, bool> supportsNeed,
            IReadOnlyList<MissionIntent> activeIntents = null)
        {
            var result = new List<DevelopmentOpportunity>();
            if (snap?.Self?.BaseHexes == null || player == null || root == null
                || hand?.Hand == null || ctx?.ResearchProductionCatalog == null || supportsNeed == null)
                return result;
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            ActorCommitments occupied = ActorCommitments.FromIntents(activeIntents, snap, null);
            // One settled preparation evaluation has one immutable set of available sources.
            // Keep site/mode-specific admission in the loop below, not in a global cache.
            List<GenerationStep> generatedOperatorSources = null;
            foreach (ResearchProductionMode mode in new[]
                { ResearchProductionMode.Research, ResearchProductionMode.Production })
            foreach (HexCoord hex in snap.Self.BaseHexes)
            {
                BuildingData building = BuildingRegistry.FindAt(hex);
                if (building == null || building.Owner != player
                    || Game.Combat.BattleInitiator.FindEnemyAt(hex, player) != null)
                    continue;
                bool facilityReady = building.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode));
                // A staffed facility at base A must not silently veto staffing a separate
                // already-built facility at base B. The duplicate-construction guard below
                // remains global for the same mode; only the ready-site check is site-local.
                if (snap.Development?.Facilities?.Any(f => f.Mode == mode && f.Hex.Equals(hex)
                        && f.HasHero && !f.Contested) == true
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
                UnitData remote = null;
                int? remoteArmyId = null;
                int remoteTravel = int.MaxValue;
                float remoteCost = float.PositiveInfinity;
                if (actor == null && ctx.Map != null)
                {
                    foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
                    {
                        if (army == null || army.IsPrison || army.Hex.Equals(hex)
                            || occupied.IsArmyClaimed(army.Id)
                            || DemandLayer.EconomyBuilderUnderImmediateThreat(snap, army.Hex))
                            continue;
                        UnitData candidate = army.IsGarrison
                            ? AiArmyRoles.BestSparableDevelopmentHero(player, army,
                                ResearchProductionSystem.RoleAbility(mode))
                            : AiArmyRoles.IsHeroLed(army) ? army.Members.FirstOrDefault(u =>
                                u != null && u.IsHero && !u.IsPrisoner
                                && u.HasAbility(ResearchProductionSystem.RoleAbility(mode))) : null;
                        if (candidate == null || candidate.Owner != player)
                            continue;
                        // Never strip a different active facility of its exact operator.
                        BuildingData sourceBuilding = BuildingRegistry.FindAt(army.Hex);
                        if (sourceBuilding != null && sourceBuilding.Owner == player
                            && new[] { ResearchProductionMode.Research, ResearchProductionMode.Production }
                                .Any(m => sourceBuilding.HasFacilityWithAbility(
                                    ResearchProductionSystem.FacilityAbility(m))
                                    && ResearchProductionSystem.ActorStillQualifies(
                                        player, candidate, army.Hex, m)))
                            continue;
                        int route = army.IsGarrison
                            ? SafeStepPathing.FindSafePathCost(ctx.Map, player,
                                army.Hex, hex, candidate.MoveMax)
                            : SafeStepPathing.FindSafePathCost(ctx.Map, army, hex);
                        if (route == int.MaxValue)
                            continue;
                        // Count reassignment/route AP with the existing Development AP price;
                        // the live AP envelope is recalculated in Provisioning, not here.
                        float cost = (army.IsGarrison ? ArmyActions.CreateArmyApCost : 0f)
                            + (army.HasActivatedThisTurn ? 0f : candidate.ActivationApCost)
                            + (float)route / Mathf.Max(1, candidate.MoveMax);
                        cost *= AiConfigV2.devApValue;
                        if (cost >= remoteCost)
                            continue;
                        remote = candidate;
                        remoteArmyId = army.Id;
                        remoteTravel = route;
                        remoteCost = cost;
                    }
                    if (operatorCard != null && remote != null)
                    {
                        float handCost = operatorCard.EffectivePlayApCost * AiConfigV2.devApValue
                            + StrategicCardEvaluator.StrategicResourceCostValue(
                                operatorCard.EffectivePlayResourceCost, snap);
                        if (handCost <= remoteCost)
                            remote = null;
                        else
                            operatorCard = null;
                    }
                }
                GenerationStep generatedOperator = null;
                // Do not start a factory on the fantasy of a future Hero. A real, staffed,
                // currently eligible OTHER facility must already offer the exact qualified
                // Hero card. Source eligibility, authored identity and resources belong to
                // GenerationSource; this evaluator only picks the cheapest legal witness.
                if (actor == null && operatorCard == null && remote == null
                    && garrison != null && PlacementRules.CanDepositIntoGarrison(garrison))
                {
                    if (generatedOperatorSources == null)
                        generatedOperatorSources = GenerationSource.Enumerate(player, root, ctx, hand,
                            claimedUseKeys: null, triedCardKeys: null);
                    generatedOperator = generatedOperatorSources
                        .Where(g => IsGeneratedOperatorCandidate(g, mode)
                            && ArmyActions.HasRequiredGroundDeploymentBuilding(player, hex, g.CardDef)
                            && CardPlayExecutor.CanFitAfterDeploy(garrison, g.CardDef)
                            && ArmyRegistry.AllAt(g.FacilityHex).Any(source => source != null
                                && source.Owner == player && !source.IsPrison
                                && source.Members.Contains(g.Hero)
                                && !occupied.IsArmyClaimed(source.Id)))
                        .OrderBy(g => ResearchProductionSystem.AttemptApCost(g.CardDef)
                            * AiConfigV2.devApValue
                            + StrategicCardEvaluator.StrategicResourceCostValue(
                                g.GenerationResourceCost, snap))
                        .ThenBy(g => g.CardKey, System.StringComparer.Ordinal)
                        .FirstOrDefault();
                }
                if (actor == null && operatorCard == null && remote == null
                    && generatedOperator == null)
                    continue;
                UnitData projectedActor = actor ?? remote;
                if (projectedActor == null)
                {
                    CardDefinition operatorDefinition = operatorCard?.Definition
                        ?? generatedOperator.CardDef;
                    CardDefinition operatorEquipment = operatorCard?.Equipment;
                    int fate = operatorDefinition.fate;
                    if (operatorEquipment?.equipment != null)
                    {
                        PredictedEquipmentState projected = EquipmentSystem.Predict(
                            operatorEquipment.equipment,
                            new Dictionary<EquipmentStat, int> { [EquipmentStat.Fate] = fate },
                            operatorDefinition.grantedAbilities);
                        if (projected.Stats.TryGetValue(EquipmentStat.Fate, out int equippedFate))
                            fate = equippedFate;
                    }
                    projectedActor = new UnitData { Fate = Mathf.Max(0, fate), IsHero = true,
                        Owner = player, OriginatingCard = operatorDefinition,
                        Equipment = operatorEquipment };
                    projectedActor.Abilities.UnionWith(MaterializationChainMatching.EffectiveAbilities(
                        operatorDefinition, operatorEquipment));
                }
                float preparationCost = new[] { facility, operatorCard }.Where(c => c != null)
                    .Sum(c => c.EffectivePlayApCost * AiConfigV2.devApValue
                        + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap))
                    + (remote != null ? remoteCost : 0f);
                if (generatedOperator != null)
                {
                    // Challenge is paid even on loss; deploy AP only on a won roll. Its
                    // resource stake belongs to the source Challenge and is charged ONCE.
                    var mintedPreview = new CardData(generatedOperator.CardDef)
                        { ResearchProductionCreated = true };
                    preparationCost += (ResearchProductionSystem.AttemptApCost(
                            generatedOperator.CardDef)
                        + generatedOperator.SuccessChance * CardCostRules.PlayAp(mintedPreview))
                        * AiConfigV2.devApValue
                        + StrategicCardEvaluator.StrategicResourceCostValue(
                            generatedOperator.GenerationResourceCost, snap);
                }
                foreach (CardDefinition card in ResearchProductionSystem.OfferedCards(
                    ctx.ResearchProductionCatalog, mode, player.Faction))
                {
                    if (card == null || (!card.isAviation
                        && card.cardType != CardType.Equipment
                        && card.cardType != CardType.Unit && card.cardType != CardType.Hero))
                        continue;
                    if (ResourceBundle.All.Any(t =>
                        (facility?.EffectivePlayResourceCost?.Get(t) ?? 0)
                        + (operatorCard?.EffectivePlayResourceCost?.Get(t) ?? 0)
                        + (generatedOperator?.GenerationResourceCost?.Get(t) ?? 0)
                        + (card.resourceCost?.Get(t) ?? 0)
                        > StrategicSpendability.SpendableAmount(player, root, ctx, t)))
                        continue;

                    // Non-equipment outputs already have ONE canonical materialization/scoring
                    // path. Use a projected GenerateDeploy plan to witness future investment
                    // utility; do NOT claim it is an executable source or create a CardUpgrade
                    // demand (those are specifically for Equipment/recipient attachments).
                    if (card.isAviation || card.cardType == CardType.Unit
                        || card.cardType == CardType.Hero)
                    {
                        // GenerationSource will not mint cards lacking an authored retry key.
                        if (string.IsNullOrWhiteSpace(card.authoredKey))
                            continue;
                        // Aviation is owned by the generated non-combat lane and needs real
                        // airfield capacity; never pretend it is a ground GenerateDeploy.
                        float futureValue = card.isAviation
                            ? NonCombatCardPlayer.ProjectedAviationInvestmentValue(card, mode, hex,
                                projectedActor, snap, player, root, hand, ctx)
                            : ProjectedDeployableInvestmentValue(card, mode, hex,
                                projectedActor, snap, player, root, hand, ctx, inv, occupied);
                        if (futureValue <= 0f)
                            continue;
                        float operatorChance = generatedOperator != null
                            ? Mathf.Clamp01(generatedOperator.SuccessChance) : 1f;
                        var deployable = new DevelopmentOpportunity
                        {
                            Mode = mode, FacilityHex = hex, Card = card,
                            SuccessChance = ResearchProductionSystem.EstimateSuccessChance(projectedActor, card),
                            ProducesEquipment = false, ExpectedGain = futureValue * operatorChance,
                            Ev = futureValue * operatorChance - preparationCost,
                            PreparationFacilityCard = facility, PreparationOperatorCard = operatorCard,
                            PreparationOperatorGeneration = generatedOperator,
                            PreparationExistingHero = remote, PreparationSourceArmyId = remoteArmyId,
                            PreparationTravelCost = remoteTravel,
                            RecipientLabel = (card.isAviation ? "aviation:" : "deployable:")
                                + card.displayName,
                        };
                        if (deployable.Ev <= AiConfigV2.devEvMargin || !supportsNeed(deployable))
                            continue;
                        deployable.BaseValue = Mathf.Clamp(AiConfigV2.devEvToBaseValue
                            * deployable.Ev, 0f, 100f);
                        deployable.Explain = $"prepare {mode} @({hex.Q},{hex.R}) for "
                            + $"{(card.isAviation ? "aviation" : "deployable")} {card.displayName}; "
                            + $"canonical future value={futureValue:0.##} "
                            + $"EV after prerequisites={deployable.Ev:0.##}";
                        result.Add(deployable);
                        continue;
                    }
                    if (card.equipment == null)
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
                    op.PreparationOperatorGeneration = generatedOperator;
                    op.PreparationExistingHero = remote;
                    op.PreparationSourceArmyId = remoteArmyId;
                    op.PreparationTravelCost = remoteTravel;
                    // The output payoff is conditional on winning the prerequisite operator
                    // Challenge; its AP/resources were already included in preparationCost.
                    if (generatedOperator != null)
                        op.Ev *= Mathf.Clamp01(generatedOperator.SuccessChance);
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

        // The operator-generation witness is a *real* existing source; its exact identity
        // must remain stable through the stage transition. No hypothetical source or retry key.
        internal static bool IsGeneratedOperatorCandidate(GenerationStep g,
            ResearchProductionMode targetMode) => g?.CardDef?.cardType == CardType.Hero
            && !g.CardDef.isAviation && g.SuccessChance > 0f
            && !string.IsNullOrWhiteSpace(g.CardDef.authoredKey)
            && MaterializationChainMatching.EffectiveAbilities(g.CardDef, null)
                .Contains(ResearchProductionSystem.RoleAbility(targetMode));

        // Objectives only projects a prospective deployment. The future source becomes real
        // exclusively after Building/Actor delivery; GenerationSource and MaterializationFeasibility
        // retain all actual Challenge, placement and reservation gates. One canonical card scorer,
        // one cost model, and no synthetic Production-only Unit/Hero strength formula.
        internal static float ProjectedDeployableInvestmentValue(CardDefinition card,
            ResearchProductionMode mode, HexCoord facilityHex, UnitData operatorHero,
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, CapabilityInventory inv, ActorCommitments commitments)
        {
            if (card == null || operatorHero == null || snap?.Self == null || player == null
                || root == null || hand == null || ctx == null || card.isAviation
                || string.IsNullOrWhiteSpace(card.authoredKey)
                || (card.cardType != CardType.Unit && card.cardType != CardType.Hero))
                return float.NegativeInfinity;
            IReadOnlyList<string> abilities = MaterializationChainMatching.EffectiveAbilities(card, null);
            bool soloOnly = card.cardType == CardType.Unit
                && AbilityParams.AbilitiesHaveAnyRecce(abilities);
            List<PlacementOption> options = PlacementSelector.BuildOptions(
                snap, player, card, commitments, soloOnly, phaseBSurplus: true);
            if (options.Count == 0)
                return float.NegativeInfinity;
            var projectedGeneration = new GenerationStep
            {
                Mode = mode, FacilityHex = facilityHex, Hero = operatorHero, CardDef = card,
                ProducesEquipment = false,
                SuccessChance = ResearchProductionSystem.EstimateSuccessChance(operatorHero, card),
                UseKey = "investment-preview", CardKey = "investment-preview:" + card.authoredKey,
            };
            float best = float.NegativeInfinity;
            foreach (PlacementOption option in options)
            {
                // Exactly the same solo-Recce vs Hero vs combat classification as Phase B.
                CapabilityKind capability = MaterializationChainEnumerator.SurplusCapability(
                    card, abilities, option);
                bool recce = capability == CapabilityKind.ScoutCapability
                    && AbilityParams.AbilitiesHaveAnyRecce(abilities);
                MaterializationPlan plan = MaterializationPlanFactory.MakeGeneratedPlan(
                    MaterializationChainKind.GenerateDeploy, null, projectedGeneration,
                    baseInHand: null, baseIdx: -1, generatedIsEquipment: false,
                    opt: option, projected: abilities);
                plan.FinalCapability = capability;
                // This includes actual mint/deploy AP and H/E/M/T costs, Challenge chance,
                // card effects, alternative role and hold. The investment cost is added ONCE
                // by EnumeratePreparation after this score; it is not included in this plan.
                StrategicCardUseCandidate score = StrategicCardEvaluator.ScoreSurplus(
                    plan, inv, recce, card.cardType == CardType.Hero, hand, abilities, snap,
                    spendableResource: t => StrategicSpendability.SpendableAmount(player, root, ctx, t),
                    player: player);
                best = Mathf.Max(best, score.NetScore);
            }
            return best;
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
                        null, $"hand:{c.Definition.displayName}", gain));
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
                        null, u, army.Id,
                        $"{(army.IsGarrison ? "garr" : "field")}:{u.Name ?? "unit"}@{army.Hex.Q},{army.Hex.R}", gain), army);
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
            EquipmentGrant grant = cand?.Card?.equipment;
            if (grant == null)
                return 0f;

            List<IReadOnlyList<WorthIt.DefenderProfile>> threats = EquipmentValuationThreats(snap);
            if (threats.Count == 0)
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
            foreach (IReadOnlyList<WorthIt.DefenderProfile> defenders in threats)
            {
                if (defenders == null || defenders.Count == 0)
                    continue;
                comparable++;
                if (cand.RecipientUnit != null && army?.Members != null)
                {
                    if (DemandLayer.ImprovesGroundCombatOutcome(
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

        private static List<IReadOnlyList<WorthIt.DefenderProfile>> EquipmentValuationThreats(WorldSnapshot snap)
        {
            var result = new List<IReadOnlyList<WorthIt.DefenderProfile>>();
            // Only composition crosses the TrueWorld boundary. Ground and aviation rosters are
            // both legitimate Production valuation inputs; neither hidden coordinates nor army
            // identity is passed to recipient selection or Mission planning.
            if (snap?.TrueWorld?.EnemyArmies != null)
                result.AddRange(snap.TrueWorld.EnemyArmies
                    .Where(a => a != null && a.Members != null && a.Members.Count > 0)
                    .Select(a => a.Members));

            // A neutral's last honestly observed defender profiles are the only permitted
            // composition witness. After it disappears into fog, its hidden live roster may
            // change; matching a known ArmyId back into TrueWorld would silently cheat.
            if (snap?.Known?.NeutralSightings != null)
                result.AddRange(snap.Known.NeutralSightings
                    .Where(s => s.Defenders != null && s.Defenders.Count > 0)
                    .Select(s => s.Defenders));

            // An event guard is not a live ArmyData until triggered. Its legitimately observed
            // defender profiles already belong to Known, so use those directly for WorthIt;
            // never invent a synthetic army or read hidden live event state/positions.
            if (snap?.Known?.EventGuards != null)
                result.AddRange(snap.Known.EventGuards
                    .Where(g => g.Defenders != null && g.Defenders.Count > 0)
                    .Select(g => g.Defenders));
            return result;
        }

        private static DevelopmentOpportunity Make(DevelopmentOffering off, DevRecipientKind kind,
            CardData card, UnitData unit, int? recipientArmyId, string label, float gain) => new DevelopmentOpportunity
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
            RecipientArmyId = recipientArmyId,
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
