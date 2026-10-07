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
    //               best legal recipient for admission; Phase A retains live recipient alternatives.
    //               The operational card decision belongs to
    //               StrategicCardEvaluator + the Phase-A portfolio (a built, staffed facility is sunk;
    //               no second investment-EV veto). Unit/Hero outputs of a ready facility are
    //               materialization chains (MaterializationChainEnumerator), not upgrades.
    //    PREPARE  — facility card in hand and/or operator missing: every catalog output (Equipment,
    //               Unit/Hero, Aviation) projected through the canonical scorers. Preparation
    //               carries no GenerationStep and never mints.
    //  Two currencies, each for its own decision — a PREPARE must pass both:
    //    Ev       — card currency: is the card chain worth its cards (output card score -
    //               Challenge - facility/operator cards, priced by StrategicCardEvaluator, the only
    //               place a chain is charged for cost). The facility/operator investment is spread
    //               over devFacilityExpectedUses outputs: one output carries only its share.
    //               Also read by the capacity-upgrade look-ahead.
    //    WorldTaskScore — world-task currency (TaskScore): what the demand/mission competes with
    //               in the allocator next to Raid, Recon and Economy. Intrinsic ForceAmplification
    //               (the need-weighted force the output adds) plus the execution of the world task
    //               itself — the existing operator hero's walk (CreateArmy AP, activation, recurring
    //               AP) and the task it abandons. Card plays are NOT priced here: their chains price
    //               them. Never a conversion of Ev.
    //  Equipment value is StrategicCardEvaluator.EquipmentUpgradeValue — the ONE value of an
    //  equipment upgrade (predicted delta x known-threat matchup x persistence). A minted
    //  Unit/Hero/Aviation is worth its force only in the share ForceNeedModel.JustifiedForceNeed
    //  says Attack/Defence cannot meet (Production amplifies a need, it never creates one).
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
        // PREPARE only: the H/E/M/T THIS pass's stage consumes (facility, else operator; null for a
        // walking hero). The one staged-funding fact — later stages fit projected income instead.
        public ResourceCost StageResourceCost;
        // PREPARE facility stage only: the Base tier bought with the facility because every
        // unlocked slot of FacilityHex is taken (already inside StageResourceCost).
        public BaseUpgradeTier PreparationCapacityTier;
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

        // Equipment: signed marginal gain on the recipient (AiPower units). Deployable: projected net
        // value of the output, already discounted by the operator-generation chance.
        public float ExpectedGain;
        // Equipment only: the tactical share of ExpectedGain (Move/Range/AP/Command/roles, AiPower
        // units) — the part StrategicCardEvaluator.EquipmentUpgradeValue never matchup-gates.
        public float TacticalGain;
        // PREPARE only: card-currency investment EV (output card score - Challenge -
        // prerequisites). Card-level decisions only; never a world-task value.
        public float Ev;
        // The canonical world-task value of this opportunity (see the header).
        public TaskScore WorldTaskScore;
        public float BaseValue => WorldTaskScore.Value;
        public string Explain = "";

        // Forecast-only cost beyond cards already counted in hand/deck. Never a spend authority.
        internal ResourceCost ForecastExtraCost;
        internal int ForecastUses = 1;
        public bool IsPreparation => Generation == null;

        internal DevelopmentOpportunity Clone() => (DevelopmentOpportunity)MemberwiseClone();
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
            var forecasts = new List<DevelopmentOpportunity>();
            if (player != null && snap != null)
                ResourceStarvationRegistry.ReplaceOperationalForecast(player, snap.TurnNumber,
                    "development", null);
            if (snap?.Self?.BaseHexes == null || snap.Development == null || player == null
                || root == null || hand?.Hand == null || ctx?.ResearchProductionCatalog == null)
                return result;
            // Exact staffed sources, including those rejected only by today's resources/window.
            // Executable enumeration elsewhere keeps its original gates.
            List<GenerationStep> forecastSources;
            CapabilityInventory inv;
            using (new Game.Core.ProfileScope("AI/Dev.Sources"))
            {
                forecastSources = GenerationSource.Enumerate(player, root, ctx,
                    hand, null, null, resourceForecast: true);
                inv = CapabilityInventory.Build(snap, player, null);
            }
            ActorCommitments occupied = ActorCommitments.FromIntents(activeIntents, snap, null);
            // One settled evaluation has one immutable set of available operator sources.
            List<GenerationStep> generatedOperatorSources = null;
            using var recipientMemo = new RecipientEvaluationMemo();
            using var targetMemo = new StrategicCardEvaluator.EquipmentTargetMemo();
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
                    ? AddReady(result, forecasts, forecastSources, mode, hex, snap, inv, occupied, player, root, hand, ctx)
                    : AddPreparation(result, forecasts, mode, hex, facilityReady, actor, snap, inv, occupied,
                        player, root, hand, ctx, activeIntents, ref generatedOperatorSources);
                AiDebugLog.WriteDeduped($"site:{mode}:{hex.Q},{hex.R}",
                    $"[AI][V2][Dev] site {mode} @({hex.Q},{hex.R}) "
                    + $"stage={(facilityReady && actor != null ? "READY" : "PREPARE")} {reason}");
            }

            // One best useful action, not the sum of a catalog or mutually exclusive recipients.
            DevelopmentOpportunity forecast = forecasts.OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.Card?.authoredKey, System.StringComparer.Ordinal).FirstOrDefault();
            if (forecast != null)
            {
                int uses = Mathf.Max(1, Mathf.Min(AiConfigV2.devFacilityExpectedUses,
                    forecast.ProducesEquipment ? forecast.ForecastUses
                        : AiConfigV2.devFacilityExpectedUses));
                var cost = new ResourceBundle();
                foreach (ResourceType t in ResourceBundle.All)
                    cost.Add(t, Mathf.Max(0, forecast.Card.resourceCost?.Get(t) ?? 0) * uses
                        + Mathf.Max(0, forecast.ForecastExtraCost?.Get(t) ?? 0));
                ResourceStarvationRegistry.ReplaceOperationalForecast(player, snap.TurnNumber,
                    "development", new Dictionary<string, ResourceBundle> { ["development"] = cost });
            }

            result = result.OrderByDescending(o => o.BaseValue)
                .ThenBy(o => (int)o.Mode).ThenBy(o => o.FacilityHex.Q).ThenBy(o => o.FacilityHex.R)
                .ThenBy(o => o.Card?.authoredKey, System.StringComparer.Ordinal)
                .ThenBy(o => o.Generation?.CardKey, System.StringComparer.Ordinal)
                .ThenBy(o => RecipientKey(o), System.StringComparer.Ordinal).ToList();
            foreach (DevelopmentOpportunity op in result)
                AiDebugLog.WriteDeduped(
                    $"op:{op.Mode}:{op.FacilityHex.Q},{op.FacilityHex.R}:{op.Card?.authoredKey}",
                    $"[AI][V2][Dev]   {(op.IsPreparation ? "prepare" : "ready")} "
                    + $"{op.Explain} base {op.BaseValue:0.0}");
            return result;
        }

        // READY — one opportunity per affordable Equipment offering at this staffed facility.
        private static string AddReady(List<DevelopmentOpportunity> result,
            List<DevelopmentOpportunity> forecasts, IReadOnlyList<GenerationStep> sources,
            ResearchProductionMode mode, HexCoord hex, WorldSnapshot snap, CapabilityInventory inv,
            ActorCommitments occupied, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx)
        {
            using var __scope = new Game.Core.ProfileScope("AI/Dev.AddReady");
            int admitted = 0, offered = 0;
            string last = "no_affordable_offering";
            foreach (GenerationStep source in sources)
            {
                if (source.Mode != mode || !source.FacilityHex.Equals(hex) || source.CardDef == null)
                    continue;
                offered++;
                CardDefinition card = source.CardDef;
                if (!source.ProducesEquipment)
                {
                    // The same canonical investment scorer proves force/placement need. Only
                    // Materialization may actually choose and execute a deployable output.
                    DevelopmentOpportunity future = PrepareDeployable(card, mode, hex, source.Hero,
                        1f, 0f, snap, inv, occupied, player, root, hand, ctx);
                    if (future != null)
                    {
                        future.WorldTaskScore = BuildDevelopmentScore(future.SuccessChance
                            * ForceNeedModel.JustifiedForceNeed(snap).Total * ForceBodies(card));
                        if (future.Ev > AiConfigV2.devEvMargin
                            && future.BaseValue > AiConfigV2.allocatorSliceEpsilon) forecasts.Add(future);
                    }
                    last = $"'{card.displayName}':output_is_materialization_chain";
                    continue;
                }
                DevelopmentOpportunity best = BestEquipmentOpportunity(mode, hex, card,
                    source.SuccessChance, source, snap, inv, player, root, hand, out string recipient);
                if (best == null)
                {
                    last = $"'{card.displayName}':no_recipient({recipient})";
                    continue;
                }
                best.WorldTaskScore = BuildDevelopmentScore(
                    best.SuccessChance * StrategicCardEvaluator.EquipmentUpgradeValue(best));
                MaterializationPlan plan = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(
                    best, source, DesireAxis.Development);
                float ev = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(best, plan,
                    snap, player, root, ctx);
                if (ev > AiConfigV2.devEvMargin
                    && best.BaseValue > AiConfigV2.allocatorSliceEpsilon) forecasts.Add(best);

                List<ResourceType> closed = DevelopmentInvestmentGate.ClosedResources(
                    player, snap.TurnNumber, card.resourceCost);
                if (closed.Count > 0)
                {
                    last = $"'{card.displayName}':window_closed({ResourceList(closed)})";
                    continue;
                }
                if (!ResearchProductionSystem.CanAffordCard(root, card)
                    || !GenerationSource.FitsReservedAffordability(root, player, ctx, card))
                {
                    last = $"'{card.displayName}':resources_unavailable";
                    continue;
                }
                int creationAp = ResearchProductionSystem.AttemptApCost(card);
                if (!root.CanSpendActionPoints(creationAp))
                {
                    last = $"'{card.displayName}':needs_{creationAp}_creation_ap";
                    continue;
                }
                // Preserve sunk-facility admission: Phase A owns the final card EV comparison.
                best.Explain = $"{best.Mode} '{card.displayName}' -> {best.RecipientLabel} "
                    + $"p={best.SuccessChance:0.00} G={best.ExpectedGain:0.0} tactical={best.TacticalGain:0.0} "
                    + best.Explain;
                result.Add(best);
                admitted++;
            }
            return $"offerings={offered} admitted={admitted}"
                + (admitted == 0 ? $" reason={last}" : "");
        }

        // PREPARE — the facility card and/or the operator are still missing. Every catalog output
        // is projected through its canonical scorer; the investment EV admits it.
        private static string AddPreparation(List<DevelopmentOpportunity> result,
            List<DevelopmentOpportunity> forecasts,
            ResearchProductionMode mode, HexCoord hex, bool facilityReady, UnitData actor,
            WorldSnapshot snap, CapabilityInventory inv, ActorCommitments occupied,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            IReadOnlyList<MissionIntent> activeIntents, ref List<GenerationStep> generatedOperatorSources)
        {
            using var __scope = new Game.Core.ProfileScope("AI/Dev.AddPreparation");
            if (BattleInitiator.FindEnemyAt(hex, player) != null)
                return "reason=enemy_on_site";
            // A built facility of this mode elsewhere is reused, never duplicated.
            if (!facilityReady && snap.Development.Facilities.Any(f => f.Mode == mode))
                return "reason=mode_facility_exists_elsewhere";
            CardData facility = facilityReady ? null : hand.Hand
                .Where(c => c?.Definition?.cardType == CardType.Facility
                    && c.Definition.grantedAbilities?.Contains(ResearchProductionSystem.FacilityAbility(mode)) == true)
                .OrderBy(c => ActionPrice.ToCardScore(c.EffectivePlayApCost)
                    + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap))
                .FirstOrDefault();
            if (!facilityReady && facility == null)
                return "reason=no_facility_card";
            // Every unlocked slot of this Base is taken: the next Base tier is this site's first
            // preparation stage, bought together with the facility (StrategicMaintenancePolicy owns
            // which tier opens a slot). No unlockable tier left -> the site cannot host it.
            BaseUpgradeTier capacityTier = null;
            if (facility != null && BuildingRegistry.FindAt(hex)?.FindFirstAvailableFacilitySlot() < 0)
            {
                capacityTier = StrategicMaintenancePolicy.CapacityUnlockTierAt(
                    BuildingRegistry.FindAt(hex), ctx);
                if (capacityTier == null)
                    return "reason=no_facility_slot";
            }
            ArmyData garrison = ArmyRegistry.AllAt(hex)
                .FirstOrDefault(a => a.Owner == player && a.IsGarrison && !a.IsPrison);
            CardData operatorCard = actor != null ? null : hand.Hand
                .Where(c => c?.Definition?.cardType == CardType.Hero
                    && MaterializationChainMatching.EffectiveAbilities(c.Definition, c.Equipment, c.Mutator)
                        .Contains(ResearchProductionSystem.RoleAbility(mode))
                    && garrison != null && CardPlayExecutor.Preflight(player, root, hand, ctx,
                        CardPlayPlan.Into(c, hex, DeploymentKind.Garrison, garrison), out _,
                        resourceForecast: true))
                .OrderBy(c => ActionPrice.ToCardScore(c.EffectivePlayApCost)
                    + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap))
                .FirstOrDefault();
            UnitData remote = null;
            ArmyData remoteArmy = null;
            int remoteTravel = int.MaxValue;
            float remoteCost = float.PositiveInfinity;
            if (actor == null && ctx.Map != null)
            {
                foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
                {
                    if (army == null || army.IsPrison || army.Hex.Equals(hex)
                        || occupied.IsArmyClaimed(army.Id))
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
                    // The walk's real AP (container, activation now, re-activation on every
                    // further turn of the march) at the one price; the live AP envelope is
                    // recalculated in Provisioning, not here.
                    var delivery = OperatorDeliveryFacts(army, candidate, route);
                    float cost = ActionPrice.ToCardScore(delivery.ActionAp + delivery.ActivationNow
                        + ActionPrice.RecurringAp(delivery.RecurringActivationAp, delivery.EtaTurns));
                    if (cost >= remoteCost)
                        continue;
                    remote = candidate;
                    remoteArmy = army;
                    remoteTravel = route;
                    remoteCost = cost;
                }
                if (operatorCard != null && remote != null)
                {
                    float handCost = ActionPrice.ToCardScore(operatorCard.EffectivePlayApCost)
                        + StrategicCardEvaluator.StrategicResourceCostValue(
                            operatorCard.EffectivePlayResourceCost, snap);
                    if (handCost <= remoteCost)
                    {
                        remote = null;
                        remoteArmy = null;
                        remoteTravel = 0;
                    }
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
                        claimedUseKeys: null, triedCardKeys: null, resourceForecast: true);
                generatedOperator = generatedOperatorSources
                    .Where(g => IsGeneratedOperatorCandidate(g, mode)
                        && ArmyActions.HasRequiredGroundDeploymentBuilding(player, hex, g.CardDef)
                        && CardPlayExecutor.CanFitAfterDeploy(garrison, g.CardDef)
                        && ArmyRegistry.AllAt(g.FacilityHex).Any(source => source != null
                            && source.Owner == player && !source.IsPrison
                            && source.Members.Contains(g.Hero)
                            && !occupied.IsArmyClaimed(source.Id)))
                    .OrderBy(g => ActionPrice.ToCardScore(ResearchProductionSystem.AttemptApCost(g.CardDef))
                        + StrategicCardEvaluator.StrategicResourceCostValue(
                            g.GenerationResourceCost, snap))
                    .ThenBy(g => g.CardKey, System.StringComparer.Ordinal)
                    .FirstOrDefault();
            }
            // Facility stage only: the qualified Hero may still be in the remaining deck. Building
            // the facility now and drawing its operator on a later turn is a normal staged plan;
            // the operator stage itself still needs a real operator (hand, map or Challenge).
            // One deck promise at a time: while any facility already waits for its operator, a
            // second facility must not be built on the same future draw.
            CardDefinition deckOperator = null;
            if (!facilityReady && actor == null && operatorCard == null && remote == null
                && generatedOperator == null && !snap.Development.AnyOperatorlessFacility)
                deckOperator = snap.Self.Deck?
                    .Where(d => d != null && d.cardType == CardType.Hero
                        && MaterializationChainMatching.EffectiveAbilities(d, null)
                            .Contains(ResearchProductionSystem.RoleAbility(mode)))
                    .OrderBy(d => StrategicCardEvaluator.StrategicResourceCostValue(d.resourceCost, snap))
                    .ThenBy(d => d.authoredKey, System.StringComparer.Ordinal)
                    .FirstOrDefault();
            if (actor == null && operatorCard == null && remote == null && generatedOperator == null
                && deckOperator == null)
                return "reason=no_operator";

            UnitData projectedActor = actor ?? remote;
            if (projectedActor == null)
            {
                CardDefinition operatorDefinition = operatorCard?.Definition
                    ?? generatedOperator?.CardDef ?? deckOperator;
                CardDefinition operatorEquipment = operatorCard?.Equipment;
                var operatorState = EquipmentSystem.Project(operatorDefinition, operatorEquipment, operatorCard?.Mutator);
                int fate = operatorState.Stats[EquipmentStat.Fate];
                // Unregistered preview used only for the canonical probability calculation,
                // never as a generation/execution actor.
                projectedActor = new UnitData { Fate = Mathf.Max(0, fate), IsHero = true,
                    Owner = player, OriginatingCard = operatorDefinition,
                    Equipment = operatorEquipment, Mutator = operatorCard?.Mutator };
                projectedActor.Abilities.UnionWith(MaterializationChainMatching.EffectiveAbilities(
                    operatorDefinition, operatorEquipment, operatorCard?.Mutator));
            }
            float preparationCost = new[] { facility, operatorCard }.Where(c => c != null)
                .Sum(c => ActionPrice.ToCardScore(c.EffectivePlayApCost)
                    + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap))
                + (remote != null ? remoteCost : 0f);
            float operatorChance = 1f;
            if (generatedOperator != null)
            {
                // Challenge is paid even on loss; deploy AP only on a won roll. Its
                // resource stake belongs to the source Challenge and is charged ONCE.
                var mintedPreview = new CardData(generatedOperator.CardDef)
                    { ResearchProductionCreated = true };
                preparationCost += ActionPrice.ToCardScore(ResearchProductionSystem.AttemptApCost(
                        generatedOperator.CardDef)
                    + generatedOperator.SuccessChance * CardCostRules.PlayAp(mintedPreview))
                    + StrategicCardEvaluator.StrategicResourceCostValue(
                        generatedOperator.GenerationResourceCost, snap);
                operatorChance = Mathf.Clamp01(generatedOperator.SuccessChance);
            }
            if (deckOperator != null)
            {
                // Its card is paid on the later operator stage; drawing it is not certain.
                preparationCost += ActionPrice.ToCardScore(new CardData(deckOperator).EffectivePlayApCost)
                    + StrategicCardEvaluator.StrategicResourceCostValue(deckOperator.resourceCost, snap);
                operatorChance = AiConfigV2.devDeckOperatorConfidence;
            }

            // Staged funding: only THIS pass's stage (facility, else the operator) is paid from
            // today's spendable stock and judged by the investment window; the rest of the chain
            // (operator, output) must fit today's stock plus devChainFundingHorizonTurns of income.
            // Demand emits one stage per pass and each stage's executor re-checks its own cost.
            ResourceCost operatorResourceCost = operatorCard?.EffectivePlayResourceCost
                ?? generatedOperator?.GenerationResourceCost ?? deckOperator?.resourceCost;
            // The tier is a one-time investment like the facility, priced once (the same price the
            // global-source upgrade path charges).
            preparationCost += StrategicMaintenancePolicy.CapacityTierPrice(capacityTier, snap);
            ResourceCost stageCost = facility != null
                ? StrategicCardEvaluator.AddResourceCosts(facility.EffectivePlayResourceCost,
                    capacityTier?.cost)
                : actor == null && remote == null ? operatorResourceCost : null;
            List<ResourceType> stageShort = ResourceBundle.All.Where(t => (stageCost?.Get(t) ?? 0)
                > StrategicSpendability.SpendableAmount(player, root, ctx, t)).ToList();
            List<ResourceType> stageClosed = DevelopmentInvestmentGate.ClosedResources(
                player, snap.TurnNumber, stageCost);
            // Derive all walk facts from the selected witness AFTER hand-vs-map selection.
            // Discarded map candidates must contribute neither delivery nor displacement.
            var chosenDelivery = OperatorDeliveryFacts(remoteArmy, remote, remoteTravel);
            float operatorDisplaced = remoteArmy != null
                ? MissionIntent.DisplacementValueOf(activeIntents, remoteArmy.Id) : 0f;
            ForceNeed forceNeed = ForceNeedModel.JustifiedForceNeed(snap);
            // The facility and its operator are a one-time investment the site then reuses for
            // every later Challenge; one output carries only its share of that investment.
            float preparationShare = preparationCost / Mathf.Max(1, AiConfigV2.devFacilityExpectedUses);

            int outputs = 0, admitted = 0;
            // Diagnostic only (2026-10-07): why each Production output was kept or dropped. Printed
            // after the loop and only when nothing was admitted, so a working site stays quiet.
            List<string> outputTrace = mode == ResearchProductionMode.Production ? new List<string>() : null;
            float bestValue = float.NegativeInfinity, bestEv = float.NegativeInfinity;
            string bestRejected = null;
            var unaffordable = new HashSet<ResourceType>();
            foreach (CardDefinition card in ResearchProductionSystem.OfferedCards(
                ctx.ResearchProductionCatalog, mode, player.Faction))
            {
                if (card == null || (!card.isAviation
                    && card.cardType != CardType.Equipment
                    && card.cardType != CardType.Unit && card.cardType != CardType.Hero))
                    continue;
                outputs++;
                ResourceCost chainCost = SumCost(facility?.EffectivePlayResourceCost,
                    operatorResourceCost, card.resourceCost, capacityTier?.cost);
                List<ResourceType> shortfall = ResourceBundle.All.Where(t => chainCost.Get(t)
                    > StrategicSpendability.SpendableAmount(player, root, ctx, t)
                        + AiConfigV2.devChainFundingHorizonTurns
                            * Mathf.Max(0f, snap.Self.PerTurnIncome.Get(t))).ToList();
                DevelopmentOpportunity op = card.isAviation
                        || card.cardType == CardType.Unit || card.cardType == CardType.Hero
                    ? PrepareDeployable(card, mode, hex, projectedActor, operatorChance,
                        preparationShare, snap, inv, occupied, player, root, hand, ctx)
                    : PrepareEquipment(card, mode, hex, projectedActor, operatorChance,
                        preparationShare, snap, inv, player, root, hand, ctx);
                if (op == null)
                {
                    outputTrace?.Add($"{card.displayName} [{card.cardType}{(card.isAviation ? "/air" : "")}] "
                        + "dropped=no_priced_output (no placement / no recipient gains / value<=0)");
                    continue;
                }
                // Every output term is conditional on first winning a generated operator.
                float outputChance = operatorChance * Mathf.Clamp01(op.SuccessChance);
                float amplificationBodies = op.ProducesEquipment
                    ? outputChance * StrategicCardEvaluator.EquipmentUpgradeValue(op)
                    : outputChance * forceNeed.Total * ForceBodies(card);
                op.WorldTaskScore = BuildDevelopmentScore(amplificationBodies,
                    chosenDelivery.ActionAp, chosenDelivery.ActivationNow,
                    chosenDelivery.RecurringActivationAp, chosenDelivery.EtaTurns, operatorDisplaced);
                if (op.BaseValue > bestValue)
                {
                    bestValue = op.BaseValue;
                    bestEv = op.Ev;
                    bestRejected = card.displayName;
                }
                // Card chain worth its cards (card currency) AND a positive world task.
                if (op.Ev <= AiConfigV2.devEvMargin
                    || op.BaseValue <= AiConfigV2.allocatorSliceEpsilon)
                {
                    outputTrace?.Add($"{card.displayName} [{card.cardType}{(card.isAviation ? "/air" : "")}] "
                        + $"dropped={(op.Ev <= AiConfigV2.devEvMargin ? "card_ev" : "task_value")} "
                        + $"ev={op.Ev:0.##} (opChance {operatorChance:0.##} x output "
                        + $"{(op.Ev + preparationShare) / Mathf.Max(0.01f, operatorChance):0.##} "
                        + $"- prepShare {preparationShare:0.##}) success={op.SuccessChance:0.##} "
                        + (op.ProducesEquipment
                            ? $"equipBenefit={op.SuccessChance * StrategicCardEvaluator.EquipmentUpgradeValue(op):0.##} "
                            : string.Empty)
                        + $"gain={op.ExpectedGain:0.##} task={op.BaseValue:0.##} "
                        + $"amplify={op.WorldTaskScore.ForceAmplification:0.##} recipient={op.RecipientLabel}");
                    continue;
                }
                // Resource/window rejection comes AFTER useful-output proof. Hand/deck already
                // include facility/operator cards; only capacity and a minted operator are extra.
                op.ForecastExtraCost = SumCost(capacityTier?.cost,
                    generatedOperator?.GenerationResourceCost);
                forecasts.Add(op);
                if (stageShort.Count > 0 || stageClosed.Count > 0 || shortfall.Count > 0)
                {
                    unaffordable.UnionWith(shortfall);
                    continue;
                }
                op.StageResourceCost = stageCost;
                op.PreparationCapacityTier = capacityTier;
                op.PreparationFacilityCard = facility;
                op.PreparationOperatorCard = operatorCard;
                op.PreparationOperatorGeneration = generatedOperator;
                op.PreparationExistingHero = remote;
                op.PreparationSourceArmyId = remoteArmy?.Id;
                op.PreparationTravelCost = remote != null ? remoteTravel : 0;
                op.Explain = $"{mode} @({hex.Q},{hex.R}) for {card.displayName} -> "
                    + $"{op.RecipientLabel}; task={op.BaseValue:0.##} "
                    + $"amplify={op.WorldTaskScore.ForceAmplification:0.##} "
                    + $"price={op.WorldTaskScore.CardPrice:0.##} delivery={op.WorldTaskScore.Delivery:0.##} "
                    + $"moverOpp={op.WorldTaskScore.MoverOpportunityCost:0.##} {forceNeed}; "
                    + op.Explain + "; "
                    + $"card EV={op.Ev:0.##}";
                result.Add(op);
                admitted++;
            }
            if (outputTrace != null && admitted == 0)
                foreach (string line in outputTrace)
                    AiDebugLog.WriteDeduped("devprod:" + line.Substring(0, line.IndexOf('[')),
                        $"[AI][V2][Dev][ProductionTrace] opChance={operatorChance:0.##} "
                        + $"prepShare={preparationShare:0.##} {line}");
            string need = $"need[{(capacityTier != null ? "upgrade " : "")}{(facility != null ? "facility" : "")}"
                + $"{(actor == null ? (remote != null ? " hero-travel" : operatorCard != null ? " hero-card" : deckOperator != null ? " hero-deck" : " hero-generate") : "")}]";
            return $"{need} outputs={outputs} admitted={admitted}"
                + (admitted == 0
                    ? (stageShort.Count > 0
                        ? $" reason=stage_unaffordable({ResourceList(stageShort)})"
                        : stageClosed.Count > 0
                            ? $" reason=window_closed({ResourceList(stageClosed)})"
                            : bestRejected != null
                        ? $" reason=ev_or_task_value_not_positive(best '{bestRejected}' task={bestValue:0.##} ev={bestEv:0.##} prepShare={preparationShare:0.##})"
                        : unaffordable.Count > 0
                            ? $" reason=chain_unaffordable({ResourceList(unaffordable)})"
                            : " reason=no_valuable_output")
                    : "");
        }

        // Development's ONE world-task score assembly point. Every argument is a raw fact;
        // conversion to score units happens here through TaskScoreEvaluator. Execution slots
        // describe only the world task's own actor — the existing operator hero's walk
        // (CreateArmy AP when it leaves a garrison, activation now, recurring AP) and the task it
        // abandons. Card plays are priced by the chains that play them.
        private static TaskScore BuildDevelopmentScore(float needWeightedBodies,
            float operatorActionAp = 0f, float activationApNow = 0f,
            float recurringActivationAp = 0f, int etaTurns = 0, float displacedTaskValue = 0f) =>
            new TaskScore(
                forceAmplification: TaskScoreEvaluator.ForceAmplification(needWeightedBodies),
                cardPrice: TaskScoreEvaluator.Price(operatorActionAp + activationApNow),
                delivery: TaskScoreEvaluator.Price(
                    ActionPrice.RecurringAp(recurringActivationAp, etaTurns)),
                moverOpportunityCost: TaskScoreEvaluator.MoverOpportunityCost(displacedTaskValue));

        // A minted Unit/Hero/Aviation's force in combat-body units (its printed line).
        private static float ForceBodies(CardDefinition card) =>
            card == null ? 0f
                : Mathf.Max(0f, AiPower.EffectiveLine(card).BasePower)
                    / Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);

        // The complete H/E/M/T a PREPARE chain consumes: facility + operator + output.
        private static ResourceCost SumCost(params ResourceCost[] costs) => new ResourceCost
        {
            human = costs.Sum(c => c?.Get(ResourceType.Human) ?? 0),
            energy = costs.Sum(c => c?.Get(ResourceType.Energy) ?? 0),
            materials = costs.Sum(c => c?.Get(ResourceType.Materials) ?? 0),
            tech = costs.Sum(c => c?.Get(ResourceType.Tech) ?? 0),
        };

        // A field operator moves with its whole army; a garrison hero is extracted alone.
        // Null is the selected hand/local/generated operator, with no delivery investment.
        internal static (float ActionAp, float ActivationNow, float RecurringActivationAp, int EtaTurns)
            OperatorDeliveryFacts(ArmyData army, UnitData hero, int route)
        {
            if (army == null || hero == null) return default;
            int activation = army.IsGarrison ? hero.ActivationApCost : army.ActivationApCost;
            int movement = army.IsGarrison ? hero.MoveMax : army.MaxMovement;
            return (army.IsGarrison ? ArmyActions.CreateArmyApCost : 0f,
                army.HasActivatedThisTurn ? 0f : activation, activation,
                Mathf.Max(1, Mathf.CeilToInt(route / (float)Mathf.Max(1, movement))));
        }

        private static string ResourceList(IEnumerable<ResourceType> types) =>
            string.Concat(ResourceBundle.All.Where(types.Contains)
                .Select(DevelopmentInvestmentGate.Abbrev));

        private static DevelopmentOpportunity PrepareEquipment(CardDefinition card,
            ResearchProductionMode mode, HexCoord hex, UnitData projectedActor, float operatorChance,
            float preparationCost, WorldSnapshot snap, CapabilityInventory inv,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (card.equipment == null)
                return null;
            using var __scope = new Game.Core.ProfileScope("AI/Dev.PrepareEquipment");
            // The projected output (best recipient + its priced value) depends on the card and the
            // operator's success chance, never on the site: one settled Enumerate prices it once
            // per (mode, card, chance) and every site re-uses a copy with its own hex and cost.
            float chance = ResearchProductionSystem.EstimateSuccessChance(projectedActor, card);
            float output;
            DevelopmentOpportunity op;
            if (RecipientEvaluationMemo.TryGetPreview(mode, card, chance, out var cachedOp, out output))
                op = cachedOp?.Clone();
            else
            {
                op = PriceEquipmentPreview(card, mode, hex, projectedActor, chance, snap, inv,
                    player, root, hand, ctx, out output);
                RecipientEvaluationMemo.StorePreview(mode, card, chance, op?.Clone(), output);
            }
            if (op == null)
                return null;
            op.FacilityHex = hex;
            op.Ev = operatorChance * output - preparationCost;
            return op;
        }

        private static DevelopmentOpportunity PriceEquipmentPreview(CardDefinition card,
            ResearchProductionMode mode, HexCoord hex, UnitData projectedActor, float chance,
            WorldSnapshot snap, CapabilityInventory inv, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, out float output)
        {
            output = 0f;
            DevelopmentOpportunity op = BestEquipmentOpportunity(mode, hex, card, chance, null,
                snap, inv, player, root, hand, out _);
            if (op == null)
                return null;
            // The future output is priced exactly as the staffed facility will price it: the SAME
            // plan shape and StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade (equipment value
            // minus its Challenge + attach cost). The preview source is never executed. Every
            // Challenge-side term is conditional on first winning a generated operator.
            var preview = new GenerationStep
            {
                Mode = mode, FacilityHex = hex, Hero = projectedActor, CardDef = card,
                ProducesEquipment = true, SuccessChance = op.SuccessChance,
                UseKey = "investment-preview", CardKey = "investment-preview:" + card.authoredKey,
            };
            MaterializationPlan plan = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(
                op, preview, DesireAxis.Development);
            if (plan == null)
                return null;
            output = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                op, plan, snap, player, root, ctx);
            return float.IsNaN(output) || float.IsNegativeInfinity(output) ? null : op;
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
            using var __scope = new Game.Core.ProfileScope("AI/Dev.PrepareDeployable");
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

        // The best legal recipient (hand card or deployed unit) for this Equipment output, by
        // StrategicCardEvaluator.EquipmentUpgradeValue. null when no recipient gains anything.
        private static DevelopmentOpportunity BestEquipmentOpportunity(ResearchProductionMode mode,
            HexCoord facilityHex, CardDefinition equipment, float successChance,
            GenerationStep generation, WorldSnapshot snap, CapabilityInventory inv,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, out string diag)
        {
            List<DevelopmentOpportunity> recipients = EquipmentOpportunities(mode, facilityHex,
                equipment, successChance, generation, snap, inv, player, root, hand, out diag, futureAttachment: true);
            DevelopmentOpportunity best = recipients
                .OrderByDescending(o => StrategicCardEvaluator.EquipmentUpgradeValue(o))
                .ThenBy(o => RecipientKey(o), System.StringComparer.Ordinal).FirstOrDefault();
            // The repeated-cost witness counts distinct legal, useful recipient slots, rather
            // than the coarse radar target count or every card in the catalog.
            if (best != null) best.ForecastUses = recipients.Count;
            return best;
        }

        // One settled Enumerate evaluates the same (equipment, recipient) pair for every mode and
        // site, and the verdict depends on neither: legality, signed delta, purpose label and the
        // pending-hand check read only the snapshot, the inventory, the hand and the registries,
        // none of which Enumerate mutates. Memoised for that one call only; outside it (hand
        // attachment, materialization re-enumeration) nothing is cached.
        private readonly struct RecipientVerdict
        {
            public readonly bool Legal;
            public readonly string Why;
            public readonly StrategicCardEvaluator.EquipmentDelta Gain;
            public readonly string Purpose;
            public readonly bool NoNeed;
            public readonly bool PendingCovers;
            public RecipientVerdict(bool legal, string why, StrategicCardEvaluator.EquipmentDelta gain,
                string purpose, bool noNeed, bool pendingCovers)
            { Legal = legal; Why = why; Gain = gain; Purpose = purpose; NoNeed = noNeed; PendingCovers = pendingCovers; }
        }

        private sealed class RecipientEvaluationMemo : System.IDisposable
        {
            [System.ThreadStatic] private static RecipientEvaluationMemo s_current;
            private readonly RecipientEvaluationMemo _outer;
            private readonly Dictionary<(CardDefinition, object, bool), RecipientVerdict> _verdicts = new();
            private readonly Dictionary<(ResearchProductionMode, CardDefinition, float),
                (DevelopmentOpportunity Op, float Output)> _previews = new();

            public RecipientEvaluationMemo() { _outer = s_current; s_current = this; }
            public void Dispose() => s_current = _outer;

            public static bool TryGet(CardDefinition equipment, object recipient, bool future,
                out RecipientVerdict verdict)
            {
                verdict = default;
                return s_current != null
                    && s_current._verdicts.TryGetValue((equipment, recipient, future), out verdict);
            }

            public static bool TryGetPreview(ResearchProductionMode mode, CardDefinition card,
                float chance, out DevelopmentOpportunity op, out float output)
            {
                op = null; output = 0f;
                if (s_current == null
                    || !s_current._previews.TryGetValue((mode, card, chance), out var hit))
                    return false;
                op = hit.Op; output = hit.Output;
                return true;
            }

            public static void StorePreview(ResearchProductionMode mode, CardDefinition card,
                float chance, DevelopmentOpportunity op, float output)
            {
                if (s_current != null) s_current._previews[(mode, card, chance)] = (op, output);
            }

            public static void Store(CardDefinition equipment, object recipient, bool future,
                RecipientVerdict verdict)
            {
                if (s_current != null) s_current._verdicts[(equipment, recipient, future)] = verdict;
            }
        }

        // Keep recipient alternatives until the shared portfolio has checked competing chains.
        // Re-enumeration also refreshes slot legality, signed deltas and matchup after each action.
        internal static List<DevelopmentOpportunity> EquipmentOpportunities(ResearchProductionMode mode,
            HexCoord facilityHex, CardDefinition equipment, float successChance,
            GenerationStep generation, WorldSnapshot snap, CapabilityInventory inv,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, out string diag,
            CardData attachmentCard = null, bool futureAttachment = false)
        {
            var result = new List<DevelopmentOpportunity>();
            diag = "no equipment grant on the card";
            if (equipment?.equipment == null)
                return result;
            // Standalone hand cards retain their actual paid/unpaid cost. Only a future
            // generated output uses the minted activation-cost preview.
            CardData generatedPreview = attachmentCard ?? ResearchProductionSystem.MintCard(equipment);

            int handChecked = 0, mapChecked = 0, noNeed = 0;
            string lastReject = null;
            float powerUnit = AiConfigV2.combatPowerPerBodyEstimate;
            // Memoised only for the standard (no explicit attachment card) call.
            bool memoised = attachmentCard == null;

            RecipientVerdict Evaluate(object recipient, CardData card, UnitData unit)
            {
                if (memoised && RecipientEvaluationMemo.TryGet(equipment, recipient, futureAttachment,
                    out RecipientVerdict cached))
                    return cached;
                string why;
                bool legal = unit != null
                    ? futureAttachment
                        ? EquipmentSystem.CanAttachPreview(equipment, unit, out why)
                        : EquipmentSystem.CanAttach(generatedPreview, unit, root, out why)
                    : futureAttachment
                        ? EquipmentSystem.CanAttachPreview(equipment, card, out why)
                        : EquipmentSystem.CanAttach(generatedPreview, card, root, out why);
                RecipientVerdict verdict;
                if (!legal)
                    verdict = new RecipientVerdict(false, why, default, null, false, false);
                else
                {
                    StrategicCardEvaluator.EquipmentDelta gain = unit != null
                        ? StrategicCardEvaluator.EquipmentDeltaParts(equipment, unit, snap, inv)
                        : StrategicCardEvaluator.EquipmentDeltaParts(equipment, card, snap, inv);
                    string purpose = StrategicCardEvaluator.EquipmentPurposeLabel(snap, card, unit);
                    DevelopmentOpportunity probe = MakeProbe(equipment, card, unit, gain, powerUnit);
                    // Delta already includes mission-scoped penetration and effect usefulness.
                    // Utility is required even for in-advance investment; surplus alone cannot admit it.
                    bool noNeed = StrategicCardEvaluator.EquipmentUpgradeValue(probe) <= 0f;
                    // A real card in hand is the pending stage. Reuse it before manufacturing more
                    // for its useful recipient slot; ordinary hand attachment enumeration stays live.
                    bool covers = !noNeed && futureAttachment
                        && PendingEquipmentCovers(probe, hand, snap, inv);
                    verdict = new RecipientVerdict(true, null, gain, purpose, noNeed, covers);
                }
                if (memoised) RecipientEvaluationMemo.Store(equipment, recipient, futureAttachment, verdict);
                return verdict;
            }

            void Consider(RecipientVerdict v, DevRecipientKind kind, CardData card, UnitData unit,
                int? armyId, string label)
            {
                string explain = "purpose=" + v.Purpose + " slot=" + equipment.attachmentSlot + " "
                    + v.Gain.Detail;
                if (v.NoNeed) { noNeed++; lastReject = explain; return; }
                if (v.PendingCovers) { lastReject = "use_pending_equipment_first"; return; }
                result.Add(new DevelopmentOpportunity
                {
                    Mode = mode, FacilityHex = facilityHex, Card = equipment, ProducesEquipment = true,
                    SuccessChance = successChance, Generation = generation,
                    RecipientKind = kind, RecipientCard = card, RecipientUnit = unit,
                    RecipientArmyId = armyId, RecipientLabel = label,
                    ExpectedGain = v.Gain.Total * powerUnit,
                    TacticalGain = v.Gain.Tactical * powerUnit, Explain = explain,
                });
            }

            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                {
                    if (c?.Definition == null) continue;
                    if (c.Definition.cardType != CardType.Unit && c.Definition.cardType != CardType.Hero) continue;
                    handChecked++;
                    RecipientVerdict v = Evaluate(c, c, null);
                    if (!v.Legal)
                    { lastReject = v.Why; continue; }
                    Consider(v, DevRecipientKind.HandCard, c, null, null, $"hand:{c.Definition.displayName}");
                }

            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army == null || army.IsPrison) continue;
                foreach (UnitData u in army.Members)
                {
                    if (u == null || u.IsPrisoner) continue;
                    mapChecked++;
                    RecipientVerdict v = Evaluate(u, null, u);
                    if (!v.Legal)
                    { lastReject = v.Why; continue; }
                    Consider(v, army.IsGarrison ? DevRecipientKind.GarrisonUnit : DevRecipientKind.FieldUnit,
                        null, u, army.Id,
                        $"{(army.IsGarrison ? "garr" : "field")}:{u.Name ?? "unit"}@{army.Hex.Q},{army.Hex.R}");
                }
            }

            if (result.Count == 0)
                diag = $"hand {handChecked}, map {mapChecked}, no-need {noNeed}"
                    + (lastReject != null ? $", last reject \"{lastReject}\"" : "");
            return result;
        }

        // The slice of a candidate that PendingEquipmentCovers and the need check read.
        private static DevelopmentOpportunity MakeProbe(CardDefinition equipment, CardData card,
            UnitData unit, StrategicCardEvaluator.EquipmentDelta gain, float powerUnit) =>
            new DevelopmentOpportunity
            {
                Card = equipment, RecipientCard = card, RecipientUnit = unit,
                ExpectedGain = gain.Total * powerUnit, TacticalGain = gain.Tactical * powerUnit,
            };

        internal static bool PendingEquipmentCovers(DevelopmentOpportunity op, AiHandData hand,
            WorldSnapshot snap, CapabilityInventory inv)
        {
            foreach (CardData pending in hand?.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
            {
                CardDefinition def = pending?.Definition;
                if (def?.cardType != CardType.Equipment || def.attachmentSlot != op.Card.attachmentSlot)
                    continue;
                bool legal = op.RecipientCard != null
                    ? EquipmentSystem.CanAttachPreview(def, op.RecipientCard, out _)
                    : EquipmentSystem.CanAttachPreview(def, op.RecipientUnit, out _);
                if (!legal) continue;
                var delta = op.RecipientCard != null
                    ? StrategicCardEvaluator.EquipmentDeltaParts(def, op.RecipientCard, snap, inv)
                    : StrategicCardEvaluator.EquipmentDeltaParts(def, op.RecipientUnit, snap, inv);
                var pendingUse = new DevelopmentOpportunity
                {
                    Card = def, RecipientCard = op.RecipientCard, RecipientUnit = op.RecipientUnit,
                    ExpectedGain = delta.Total * AiConfigV2.combatPowerPerBodyEstimate,
                    TacticalGain = delta.Tactical * AiConfigV2.combatPowerPerBodyEstimate,
                };
                if (StrategicCardEvaluator.EquipmentUpgradeValue(pendingUse) > 0f) return true;
            }
            return false;
        }

        internal static string RecipientKey(DevelopmentOpportunity op) => op?.RecipientUnit != null
            ? $"unit:{op.RecipientUnit.RuntimeId}"
            : $"hand:{GenerationSource.StableCardKey(op?.RecipientCard)}";
    }
}
