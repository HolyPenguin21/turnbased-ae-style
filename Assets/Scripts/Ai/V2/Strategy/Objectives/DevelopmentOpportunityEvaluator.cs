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
    //  The ONE enumeration of Research/Production (Laboratory / Factory) opportunities. Laboratory
    //  and Factory are a late resource sink that strengthens the units the main deck already put
    //  on the map, so every opportunity must be inside DevelopmentInvestmentGate's window for the
    //  resources ITS OWN chain consumes (a READY Challenge: the card; a PREPARE: facility +
    //  operator + output). An unrelated empty resource never blocks it.
    //
    //  Each (mode, own base) site is in exactly one stage:
    //    READY    — facility built and staffed: every affordable Equipment offering, bound to its
    //               best legal recipient. The operational card decision belongs to
    //               StrategicCardEvaluator + the Phase-A portfolio (a built, staffed facility is sunk;
    //               no second investment-EV veto). Unit/Hero outputs of a ready facility are
    //               materialization chains (MaterializationChainEnumerator), not upgrades.
    //    PREPARE  — facility card in hand and/or operator missing: every catalog output (Equipment,
    //               Unit/Hero, Aviation) projected through the canonical scorers; the investment EV
    //               (output value - Challenge - prerequisites) is the admission. Preparation carries
    //               no GenerationStep and never mints.
    //  Value is intrinsic: the recipient's predicted stat/ability delta (StrategicCardEvaluator.
    //  EquipmentUpgradeUtilityFor) amplified by how many KNOWN threats it improves the WorthIt
    //  outcome against (EquipmentMatchupFit). No live mission has to "witness" a need first.
    // ===========================================================================================
    public enum DevRecipientKind { HandCard, GarrisonUnit, FieldUnit }

    public sealed class DevelopmentOpportunity
    {
        public ResearchProductionMode Mode;
        public HexCoord FacilityHex;
        public CardDefinition Card;
        public bool ProducesEquipment;
        public float SuccessChance;
        // READY only: the exact facility/operator source. null => a PREPARE opportunity.
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
        // Stable owning-army identity captured at recipient selection.
        public int? RecipientArmyId;
        public string RecipientLabel;

        // Equipment: marginal gain on the recipient (AiPower units). Deployable: projected net
        // value of the output, already discounted by the operator-generation chance.
        public float ExpectedGain;
        // Equipment: [0..1] share of known threats against which the upgrade improves the outcome.
        public float MatchupFit;
        // PREPARE only: the one investment EV (output value - Challenge - prerequisites).
        public float Ev;
        public float BaseValue;
        public string Explain = "";

        public bool IsPreparation => Generation == null;
    }

    public static class DevelopmentOpportunityEvaluator
    {
        private static readonly ResearchProductionMode[] Modes =
            { ResearchProductionMode.Research, ResearchProductionMode.Production };

        // Every admitted opportunity for the current settled state, best first. Empty while the
        // investment window is closed. Demand (and the capacity-upgrade look-ahead) consume this
        // list; they never re-admit with a different predicate.
        public static List<DevelopmentOpportunity> Enumerate(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var result = new List<DevelopmentOpportunity>();
            if (snap?.Self?.BaseHexes == null || snap.Development == null || player == null
                || root == null || hand?.Hand == null || ctx?.ResearchProductionCatalog == null)
                return result;
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
            ActorCommitments occupied = ActorCommitments.FromIntents(activeIntents, snap, null);
            // One settled evaluation has one immutable set of available operator sources.
            List<GenerationStep> generatedOperatorSources = null;
            IEnumerable<HexCoord> sites = snap.Self.BaseHexes
                .Concat(snap.Development.Facilities.Select(f => f.Hex))
                .Distinct().OrderBy(h => h.Q).ThenBy(h => h.R).ToList();

            foreach (ResearchProductionMode mode in Modes)
            foreach (HexCoord hex in sites)
            {
                BuildingData building = BuildingRegistry.FindAt(hex);
                if (building == null || building.Owner != player)
                    continue;
                bool facilityReady = building.HasFacilityWithAbility(
                    ResearchProductionSystem.FacilityAbility(mode));
                UnitData actor = ResearchProductionSystem.FindActor(player, hex, mode);
                string reason = facilityReady && actor != null
                    ? AddReady(result, mode, hex, snap, inv, player, root, hand)
                    : AddPreparation(result, mode, hex, facilityReady, actor, snap, inv, occupied,
                        player, root, hand, ctx, ref generatedOperatorSources);
                AiDebugLog.WriteDeduped($"site:{mode}:{hex.Q},{hex.R}",
                    $"[AI][V2][Dev] site {mode} @({hex.Q},{hex.R}) "
                    + $"stage={(facilityReady && actor != null ? "READY" : "PREPARE")} {reason}");
            }

            result.Sort((a, b) => b.BaseValue.CompareTo(a.BaseValue));
            foreach (DevelopmentOpportunity op in result)
                AiDebugLog.WriteDeduped(
                    $"op:{op.Mode}:{op.FacilityHex.Q},{op.FacilityHex.R}:{op.Card?.authoredKey}",
                    $"[AI][V2][Dev]   {(op.IsPreparation ? "prepare" : "ready")} "
                    + $"{op.Explain} base {op.BaseValue:0.0}");
            return result;
        }

        // READY — one opportunity per affordable Equipment offering at this staffed facility.
        private static string AddReady(List<DevelopmentOpportunity> result,
            ResearchProductionMode mode, HexCoord hex, WorldSnapshot snap, CapabilityInventory inv,
            PlayerSetupData player, PlayerRoot root, AiHandData hand)
        {
            int admitted = 0, offered = 0;
            string last = "no_affordable_offering";
            foreach (DevelopmentOffering off in snap.Development.Offerings)
            {
                if (off.Mode != mode || !off.FacilityHex.Equals(hex) || off.Card == null)
                    continue;
                offered++;
                List<ResourceType> closed = DevelopmentInvestmentGate.ClosedResources(
                    player, snap.TurnNumber, off.Card.resourceCost);
                if (closed.Count > 0)
                {
                    last = $"'{off.Card.displayName}':window_closed({ResourceList(closed)})";
                    continue;
                }
                if (!off.ProducesEquipment)
                {
                    // Unit/Hero/Aviation mints are materialization / non-combat chains.
                    last = $"'{off.Card.displayName}':output_is_materialization_chain";
                    continue;
                }
                DevelopmentOpportunity best = BestEquipmentOpportunity(off.Mode, off.FacilityHex,
                    off.Card, off.SuccessChance, off.Generation, snap, inv, player, root, hand,
                    out string recipient);
                if (best == null)
                {
                    last = $"'{off.Card.displayName}':no_recipient({recipient})";
                    continue;
                }
                int completeAp = ResearchProductionSystem.AttemptApCost(best.Card)
                                 + Mathf.Max(0, best.Card.activationApCost);
                if (!root.CanSpendActionPoints(completeAp))
                {
                    last = $"'{off.Card.displayName}':needs_{completeAp}_ap";
                    continue;
                }
                // Sunk facility: no investment EV. BaseValue only orders ready opportunities;
                // the card decision is StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade's.
                best.BaseValue = best.SuccessChance * RecipientSelectionValue(best);
                best.Explain = $"{best.Mode} '{off.Card.displayName}' -> {best.RecipientLabel} "
                    + $"p={best.SuccessChance:0.00} G={best.ExpectedGain:0.0} fit={best.MatchupFit:0.00}";
                result.Add(best);
                admitted++;
            }
            return $"offerings={offered} admitted={admitted}"
                + (admitted == 0 ? $" reason={last}" : "");
        }

        // PREPARE — the facility card and/or the operator are still missing. Every catalog output
        // is projected through its canonical scorer; the investment EV admits it.
        private static string AddPreparation(List<DevelopmentOpportunity> result,
            ResearchProductionMode mode, HexCoord hex, bool facilityReady, UnitData actor,
            WorldSnapshot snap, CapabilityInventory inv, ActorCommitments occupied,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            ref List<GenerationStep> generatedOperatorSources)
        {
            if (BattleInitiator.FindEnemyAt(hex, player) != null)
                return "reason=enemy_on_site";
            // A built facility of this mode elsewhere is reused, never duplicated.
            if (!facilityReady && snap.Development.Facilities.Any(f => f.Mode == mode))
                return "reason=mode_facility_exists_elsewhere";
            CardData facility = facilityReady ? null : hand.Hand
                .Where(c => c?.Definition?.cardType == CardType.Facility
                    && c.Definition.grantedAbilities?.Contains(ResearchProductionSystem.FacilityAbility(mode)) == true)
                .OrderBy(c => c.EffectivePlayApCost * AiConfigV2.devApValue
                    + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap))
                .FirstOrDefault();
            if (!facilityReady && facility == null)
                return "reason=no_facility_card";
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
                        && Modes.Any(m => sourceBuilding.HasFacilityWithAbility(
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
            if (actor == null && operatorCard == null && remote == null && generatedOperator == null)
                return "reason=no_operator";

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
                // Unregistered preview used only for the canonical probability calculation,
                // never as a generation/execution actor.
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
            float operatorChance = 1f;
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
                operatorChance = Mathf.Clamp01(generatedOperator.SuccessChance);
            }

            int outputs = 0, admitted = 0;
            float bestEv = float.NegativeInfinity;
            string bestRejected = null;
            var windowClosed = new HashSet<ResourceType>();
            foreach (CardDefinition card in ResearchProductionSystem.OfferedCards(
                ctx.ResearchProductionCatalog, mode, player.Faction))
            {
                if (card == null || (!card.isAviation
                    && card.cardType != CardType.Equipment
                    && card.cardType != CardType.Unit && card.cardType != CardType.Hero))
                    continue;
                outputs++;
                ResourceCost chainCost = SumCost(facility?.EffectivePlayResourceCost,
                    operatorCard?.EffectivePlayResourceCost,
                    generatedOperator?.GenerationResourceCost, card.resourceCost);
                if (ResourceBundle.All.Any(t => chainCost.Get(t)
                        > StrategicSpendability.SpendableAmount(player, root, ctx, t)))
                    continue;
                List<ResourceType> closed = DevelopmentInvestmentGate.ClosedResources(
                    player, snap.TurnNumber, chainCost);
                if (closed.Count > 0)
                {
                    windowClosed.UnionWith(closed);
                    continue;
                }

                DevelopmentOpportunity op = card.isAviation
                        || card.cardType == CardType.Unit || card.cardType == CardType.Hero
                    ? PrepareDeployable(card, mode, hex, projectedActor, operatorChance,
                        preparationCost, snap, inv, occupied, player, root, hand, ctx)
                    : PrepareEquipment(card, mode, hex, projectedActor, operatorChance,
                        preparationCost, snap, inv, player, root, hand);
                if (op == null)
                    continue;
                if (op.Ev > bestEv)
                {
                    bestEv = op.Ev;
                    bestRejected = card.displayName;
                }
                if (op.Ev <= AiConfigV2.devEvMargin)
                    continue;
                op.PreparationFacilityCard = facility;
                op.PreparationOperatorCard = operatorCard;
                op.PreparationOperatorGeneration = generatedOperator;
                op.PreparationExistingHero = remote;
                op.PreparationSourceArmyId = remoteArmyId;
                op.PreparationTravelCost = remoteTravel;
                op.BaseValue = Mathf.Clamp(AiConfigV2.devEvToBaseValue * op.Ev, 0f, 100f);
                op.Explain = $"{mode} @({hex.Q},{hex.R}) for {card.displayName} -> "
                    + $"{op.RecipientLabel}; prerequisites={preparationCost:0.##} "
                    + $"EV after prerequisites={op.Ev:0.##}";
                result.Add(op);
                admitted++;
            }
            string need = $"need[{(facility != null ? "facility" : "")}"
                + $"{(actor == null ? (remote != null ? " hero-travel" : operatorCard != null ? " hero-card" : " hero-generate") : "")}]";
            return $"{need} outputs={outputs} admitted={admitted}"
                + (admitted == 0
                    ? (bestRejected != null
                        ? $" reason=ev_below_margin(best '{bestRejected}' EV={bestEv:0.##})"
                        : windowClosed.Count > 0
                            ? $" reason=window_closed({ResourceList(windowClosed)})"
                            : " reason=no_valuable_output")
                    : "");
        }

        // The complete H/E/M/T a PREPARE chain consumes: facility + operator + output.
        private static ResourceCost SumCost(params ResourceCost[] costs) => new ResourceCost
        {
            human = costs.Sum(c => c?.Get(ResourceType.Human) ?? 0),
            energy = costs.Sum(c => c?.Get(ResourceType.Energy) ?? 0),
            materials = costs.Sum(c => c?.Get(ResourceType.Materials) ?? 0),
            tech = costs.Sum(c => c?.Get(ResourceType.Tech) ?? 0),
        };

        private static string ResourceList(IEnumerable<ResourceType> types) =>
            string.Concat(ResourceBundle.All.Where(types.Contains)
                .Select(DevelopmentInvestmentGate.Abbrev));

        private static DevelopmentOpportunity PrepareEquipment(CardDefinition card,
            ResearchProductionMode mode, HexCoord hex, UnitData projectedActor, float operatorChance,
            float preparationCost, WorldSnapshot snap, CapabilityInventory inv,
            PlayerSetupData player, PlayerRoot root, AiHandData hand)
        {
            if (card.equipment == null)
                return null;
            DevelopmentOpportunity op = BestEquipmentOpportunity(mode, hex, card,
                ResearchProductionSystem.EstimateSuccessChance(projectedActor, card), null,
                snap, inv, player, root, hand, out _);
            if (op == null)
                return null;
            // Equipment persists across turns/battles; Challenge + attach are paid once. Every
            // Challenge-side term is conditional on first winning a generated operator.
            float output = op.SuccessChance * RecipientSelectionValue(op)
                * AiConfigV2.devEquipmentPersistenceMultiplier;
            float challengeCost = StrategicCardEvaluator.StrategicResourceCostValue(card.resourceCost, snap)
                + (ResearchProductionSystem.AttemptApCost(card)
                    + op.SuccessChance * Mathf.Max(0, card.activationApCost)) * AiConfigV2.devApValue;
            op.Ev = operatorChance * (output - challengeCost) - preparationCost;
            return op;
        }

        // Non-equipment outputs already have ONE canonical materialization/scoring path. Use a
        // projected plan to value the future investment; never an executable source.
        private static DevelopmentOpportunity PrepareDeployable(CardDefinition card,
            ResearchProductionMode mode, HexCoord hex, UnitData projectedActor, float operatorChance,
            float preparationCost, WorldSnapshot snap, CapabilityInventory inv,
            ActorCommitments occupied, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx)
        {
            // GenerationSource will not mint cards lacking an authored retry key.
            if (string.IsNullOrWhiteSpace(card.authoredKey))
                return null;
            // Aviation is owned by the generated non-combat lane and needs real airfield
            // capacity; never pretend it is a ground GenerateDeploy.
            float futureValue = card.isAviation
                ? NonCombatCardPlayer.ProjectedAviationInvestmentValue(card, mode, hex,
                    projectedActor, snap, player, root, hand, ctx)
                : ProjectedDeployableInvestmentValue(card, mode, hex,
                    projectedActor, snap, player, root, hand, ctx, inv, occupied);
            if (futureValue <= 0f)
                return null;
            return new DevelopmentOpportunity
            {
                Mode = mode, FacilityHex = hex, Card = card, ProducesEquipment = false,
                SuccessChance = ResearchProductionSystem.EstimateSuccessChance(projectedActor, card),
                ExpectedGain = futureValue * operatorChance,
                Ev = futureValue * operatorChance - preparationCost,
                RecipientLabel = (card.isAviation ? "aviation:" : "deployable:") + card.displayName,
            };
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
                // by AddPreparation after this score; it is not included in this plan.
                StrategicCardUseCandidate score = StrategicCardEvaluator.ScoreSurplus(
                    plan, inv, recce, card.cardType == CardType.Hero, hand, abilities, snap,
                    spendableResource: t => StrategicSpendability.SpendableAmount(player, root, ctx, t),
                    player: player);
                best = Mathf.Max(best, score.NetScore);
            }
            return best;
        }

        // Live refresh before Phase A prices a READY upgrade: the operator's Fate may have
        // changed since the opportunity was enumerated.
        public static void Rescore(DevelopmentOpportunity op)
        {
            if (op?.Generation?.Hero == null)
                return;
            op.SuccessChance = ResearchProductionSystem.EstimateSuccessChance(
                op.Generation.Hero, op.Card);
            op.Generation.SuccessChance = op.SuccessChance;
        }

        // The best legal recipient (hand card or deployed unit) for this Equipment output, by
        // RecipientSelectionValue. null when no recipient gains anything.
        private static DevelopmentOpportunity BestEquipmentOpportunity(ResearchProductionMode mode,
            HexCoord facilityHex, CardDefinition equipment, float successChance,
            GenerationStep generation, WorldSnapshot snap, CapabilityInventory inv,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, out string diag)
        {
            diag = "no equipment grant on the card";
            if (equipment?.equipment == null)
                return null;
            CardData generatedPreview = ResearchProductionSystem.MintCard(equipment);

            int handChecked = 0, mapChecked = 0, gainZero = 0;
            string lastReject = null;
            DevelopmentOpportunity best = null;
            float bestSelectionValue = float.NegativeInfinity;
            void Consider(DevelopmentOpportunity cand, ArmyData army = null)
            {
                if (cand.ExpectedGain <= 0f) { gainZero++; return; }
                cand.MatchupFit = EquipmentMatchupFit(cand, army, snap);
                float selection = RecipientSelectionValue(cand);
                if (best == null || selection > bestSelectionValue)
                {
                    best = cand;
                    bestSelectionValue = selection;
                }
            }
            DevelopmentOpportunity Make(DevRecipientKind kind, CardData card, UnitData unit,
                int? armyId, string label, float gain) => new DevelopmentOpportunity
            {
                Mode = mode, FacilityHex = facilityHex, Card = equipment, ProducesEquipment = true,
                SuccessChance = successChance, Generation = generation,
                RecipientKind = kind, RecipientCard = card, RecipientUnit = unit,
                RecipientArmyId = armyId, RecipientLabel = label,
                ExpectedGain = Mathf.Max(0f, gain),
            };

            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                {
                    if (c?.Definition == null) continue;
                    if (c.Definition.cardType != CardType.Unit && c.Definition.cardType != CardType.Hero) continue;
                    handChecked++;
                    if (!EquipmentSystem.CanAttach(generatedPreview, c, root, out string why))
                    { lastReject = why; continue; }
                    float gain = StrategicCardEvaluator.EquipmentUpgradeUtilityFor(
                        equipment, c, snap, inv) * AiConfigV2.combatPowerPerBodyEstimate;
                    Consider(Make(DevRecipientKind.HandCard, c, null, null,
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
                        equipment, u, snap, inv) * AiConfigV2.combatPowerPerBodyEstimate;
                    Consider(Make(army.IsGarrison ? DevRecipientKind.GarrisonUnit : DevRecipientKind.FieldUnit,
                        null, u, army.Id,
                        $"{(army.IsGarrison ? "garr" : "field")}:{u.Name ?? "unit"}@{army.Hex.Q},{army.Hex.R}", gain), army);
                }
            }

            if (best == null)
                diag = $"hand {handChecked}, map {mapChecked}, zero-gain {gainZero}"
                    + (lastReject != null ? $", last reject \"{lastReject}\"" : "");
            return best;
        }

        // Gain amplified by the share of known threats it improves: an upgrade that turns real
        // fights is worth more than the same stat delta against nothing the AI has seen.
        internal static float RecipientSelectionValue(DevelopmentOpportunity cand) =>
            cand == null || cand.ExpectedGain <= 0f
                ? float.NegativeInfinity
                : cand.ExpectedGain * (1f + Mathf.Clamp01(cand.MatchupFit));

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
                    if (ImprovesGroundCombatOutcome(
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

        // WorthIt owns combat rules and simulation. EquipmentSystem owns the exact stat/ability
        // projection. Compare the SAME army's roster before/after replacing only its recipient,
        // without mutating gameplay UnitData or pretending the grant created a new combat body.
        internal static bool ImprovesGroundCombatOutcome(UnitData recipient,
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
                // A non-combatant recipient's projected profile is built by hand above and would
                // otherwise default to a combatant — skip it on the domain rule, not a hero check.
                if (unit == null || !unit.IsGroundCombatant)
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
    }
}
