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
    // One admission for preparation and ready production. A prerequisite opens a capability:
    // only its own current cost, delivery and displacement are evaluated. Missing facility and
    // operator are peer alternatives; neither pins a future product or recipient. Preparation
    // uses structural evidence in hand/map/deck, never predicted future output value.
    // A staffed facility selects actual outputs through the canonical card/recipient scorers.
    // Today's resource-window and spendable-bank gates apply only to today's step.
    // A card step (facility, hand/generated operator) carries PreparationCardScore: the ONE
    // intrinsic score of StrategicCardEvaluator (DevelopmentPreparationScorer), already net of the
    // current price, which Phase A's arbiter ranks; admission needs it positive. Only the walk of an
    // existing hero is a world task on WorldTaskScore. PreparationRank only orders peer steps.
    public enum DevRecipientKind { HandCard, GarrisonUnit, FieldUnit }
    // Identities are persisted in logs/fingerprints: Facility=0, Operator=1; new kinds go last.
    // CapacityUnlock is the stand-alone step "buy the Base's next level" for a full Base whose
    // Research/Production Facility card waits in hand: the card is only its witness, never consumed.
    public enum DevelopmentPreparationKind { Facility, Operator, CapacityUnlock }

    public sealed class DevelopmentOpportunity
    {
        public ResearchProductionMode Mode;
        public DevelopmentPreparationKind? PreparationKind;
        public HexCoord FacilityHex;
        public CardDefinition Card;
        public bool ProducesEquipment;
        public float SuccessChance;
        // READY only: the exact facility/operator source. null => a PREPARE opportunity.
        public GenerationStep Generation;
        // PREPARE only: the resources this step consumes; null for a walking hero.
        public ResourceCost StageResourceCost;
        // Ordering key between peer prerequisite steps, never added to a final score. A card step
        // (facility, hand operator, generated operator) ranks by PreparationCardScore; a walk of an
        // existing hero ranks by its TaskScore, read in card units through ActionPrice.
        internal float PreparationRank;
        // PREPARE card step only: StrategicCardEvaluator's intrinsic NetScore of exactly this
        // action (benefit - current price) from DevelopmentPreparationScorer, the same number the
        // Phase A arbiter ranks. null for an existing hero's delivery (a world task).
        public float? PreparationCardScore;
        // CapacityUnlock only: the Base tier this step buys because every unlocked slot of FacilityHex is
        // taken (it IS the whole StageResourceCost / AP of the step; the Facility placement is a later,
        // separately evaluated action).
        public BaseUpgradeTier PreparationCapacityTier;
        // Facility: the card to place. CapacityUnlock: the exact hand card the opened slot will take - a
        // structural witness, not consumed. Planning-only: its own price is the later placement's bill.
        public CardData PreparationFacilityCard;
        // CapacityUnlock plan identity, re-confirmed immediately before payment.
        public int PreparationExpectedLevel;
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
        // Legacy preview helper result; not used to admit or rank prerequisite steps.
        public float Ev;
        // The canonical world-task value of this opportunity (see the header).
        public TaskScore WorldTaskScore;
        public float BaseValue => WorldTaskScore.Value;
        public string Explain = "";

        // Forecast-only cost beyond cards already counted in hand/deck. Never a spend authority.
        internal ResourceCost ForecastExtraCost;
        internal int ForecastUses = 1;
        public bool IsPreparation => PreparationKind.HasValue;

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
            DevelopmentOpportunity forecast = forecasts.OrderByDescending(o => o.IsPreparation ? o.PreparationRank : o.BaseValue)
                .ThenBy(o => o.Card?.authoredKey, System.StringComparer.Ordinal).FirstOrDefault();
            if (forecast != null)
            {
                int uses = Mathf.Max(1, Mathf.Min(AiConfigV2.devFacilityExpectedUses,
                    forecast.IsPreparation ? 1 : forecast.ProducesEquipment ? forecast.ForecastUses
                        : AiConfigV2.devFacilityExpectedUses));
                var cost = new ResourceBundle();
                foreach (ResourceType t in ResourceBundle.All)
                    cost.Add(t, Mathf.Max(0, (forecast.IsPreparation ? forecast.StageResourceCost : forecast.Card?.resourceCost)?.Get(t) ?? 0) * uses
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
                    $"op:{op.Mode}:{op.FacilityHex.Q},{op.FacilityHex.R}:{op.PreparationKind}:{op.Card?.authoredKey}",
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
                        1f, snap, inv, occupied, player, root, hand, ctx);
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
                // Repeat damping is already inside the gain (EquipmentOpportunities, source set); there is no supply term.
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
            IReadOnlyList<MissionIntent> activeIntents, ref List<GenerationStep> generatedOperatorSources,
            bool describeOnly = false)
        {
            using var __scope = new Game.Core.ProfileScope("AI/Dev.AddPreparation");
            if (BattleInitiator.FindEnemyAt(hex, player) != null)
                return "reason=enemy_on_site";
            if (!IsPreparationSite(player, hex)) return "reason=no_own_base";
            if (ctx.ResearchProductionCatalog.ResolveFor(mode, player.Faction).Count == 0)
                return "reason=no_mode_catalog";
            // A built facility of this mode elsewhere is reused, never duplicated.
            if (!facilityReady && snap.Development.Facilities.Any(f => f.Mode == mode))
                return "reason=mode_facility_exists_elsewhere";
            List<CardData> facilityCards = facilityReady ? new List<CardData>() : hand.Hand
                .Where(c => c?.Definition?.cardType == CardType.Facility
                    && c.Definition.grantedAbilities?.Contains(ResearchProductionSystem.FacilityAbility(mode)) == true)
                .OrderBy(c => ActionPrice.ToCardScore(c.EffectivePlayApCost)
                    + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap))
                .ToList();
            bool futureFacility = facilityReady || facilityCards.Count > 0 || snap.Self.Deck?.Any(d =>
                d?.cardType == CardType.Facility && d.grantedAbilities?.Contains(
                    ResearchProductionSystem.FacilityAbility(mode)) == true) == true;
            if (!futureFacility)
                return "reason=no_facility_path";
            // Every unlocked slot of this Base is taken: the next Base tier is a stand-alone step of its
            // own (CapacityUnlock), priced and paid for itself; the Facility placement it makes possible is a
            // later action evaluated on the refreshed world. StrategicMaintenancePolicy owns which tier
            // opens a slot. No unlockable tier left -> the site cannot host it.
            BaseUpgradeTier capacityTier = null;
            if (!facilityReady && BuildingRegistry.FindAt(hex)?.FindFirstAvailableFacilitySlot() < 0)
            {
                capacityTier = StrategicMaintenancePolicy.CapacityUnlockTierAt(
                    BuildingRegistry.FindAt(hex), ctx);
                if (capacityTier == null)
                    return "reason=no_facility_slot";
            }
            System.Func<ResourceType, float> spendable = t => StrategicSpendability.SpendableAmount(player, root, ctx, t);
            // Peer hand facilities are ranked by the one card score of today's step (placement),
            // not by the cheapest bill: a weak cheap card must not hide a better one from the arbiter.
            // Desire facts keep the structural order. On a full Base the card is only the capacity
            // step's witness (the cheapest structural card; the step's score does not depend on it).
            CardData facility = facilityCards.FirstOrDefault();
            float facilityScore = 0f;
            if (capacityTier != null)
            {
                if (!describeOnly)
                    facilityScore = DevelopmentPreparationScorer.CapacityUnlock(capacityTier, snap, spendable, player);
            }
            else if (!describeOnly && facilityCards.Count > 1)
            {
                facility = null;
                facilityScore = float.NegativeInfinity;
                foreach (CardData candidate in facilityCards)
                {
                    float s = DevelopmentPreparationScorer.Facility(candidate,
                        candidate.EffectivePlayApCost,
                        candidate.EffectivePlayResourceCost,
                        snap, inv, hand, spendable, player);
                    if (s > facilityScore) { facility = candidate; facilityScore = s; }
                }
            }
            else if (!describeOnly && facility != null)
                facilityScore = DevelopmentPreparationScorer.Facility(facility,
                    facility.EffectivePlayApCost,
                    facility.EffectivePlayResourceCost,
                    snap, inv, hand, spendable, player);
            ArmyData garrison = ArmyRegistry.AllAt(hex)
                .FirstOrDefault(a => a.Owner == player && a.IsGarrison && !a.IsPrison);
            bool operatorInTransit = activeIntents?.Any(i => i?.Status == IntentStatus.Active
                && i.Development != null && i.Development.Mode == mode
                && i.Development.FacilityHex.Equals(hex) && i.Development.Hero?.Owner == player
                && !i.Development.Hero.IsPrisoner
                && i.Development.Hero.HasAbility(ResearchProductionSystem.RoleAbility(mode))
                && ArmyRegistry.AllForOwner(player).Any(a => a != null && !a.IsPrison
                    && a.Members.Contains(i.Development.Hero))) == true;
            DevelopmentLifecycleState lifecycle = MissionIntentRegistry.Peek(player)?.Development;
            CardData pendingOperator = lifecycle?.GeneratedOperatorFor(hex, mode, snap.TurnNumber);
            CardData operatorCard = null;
            float operatorScore = 0f;
            if (actor == null && !operatorInTransit && garrison != null)
            {
                // Peer hand operators compete as the hero cards they are: the one card score of
                // deploying each into this garrison, then the cheaper bill. A pending generated
                // card keeps its claim first.
                var eligible = hand.Hand
                    .Select((c, ordinal) => (card: c, ordinal))
                    .Where(x => x.card?.Definition?.cardType == CardType.Hero
                        && MaterializationChainMatching.EffectiveAbilities(x.card.Definition, x.card.Equipment, x.card.Mutator)
                            .Contains(ResearchProductionSystem.RoleAbility(mode))
                        && (lifecycle?.CanUseGeneratedOperatorAt(x.card, hex, mode, snap.TurnNumber) ?? true)
                        && CardPlayExecutor.Preflight(player, root, hand, ctx,
                            CardPlayPlan.Into(x.card, hex, DeploymentKind.Garrison, garrison), out _,
                            resourceForecast: true))
                    .Select(x => (x.card, score: describeOnly ? 0f
                        : DevelopmentPreparationScorer.HandOperator(x.card, x.ordinal, hex, mode, garrison,
                            snap, inv, spendable, player)))
                    .ToList();
                var best = eligible
                    .OrderBy(x => ReferenceEquals(x.card, pendingOperator) ? 0 : 1)
                    .ThenByDescending(x => x.score)
                    .ThenBy(x => ActionPrice.ToCardScore(x.card.EffectivePlayApCost)
                        + StrategicCardEvaluator.StrategicResourceCostValue(x.card.EffectivePlayResourceCost, snap))
                    .Select(x => ((CardData card, float score)?)x)
                    .FirstOrDefault();
                if (best.HasValue) { operatorCard = best.Value.card; operatorScore = best.Value.score; }
            }
            UnitData remote = null;
            ArmyData remoteArmy = null;
            int remoteTravel = int.MaxValue;
            float remoteCost = float.PositiveInfinity;
            if (actor == null && !operatorInTransit && ctx.Map != null)
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
            float generatedScore = 0f;
            // Do not start a factory on the fantasy of a future Hero. A real, staffed,
            // currently eligible OTHER facility must already offer the exact qualified
            // Hero card. Source eligibility, authored identity and resources belong to
            // GenerationSource; this evaluator only picks the cheapest legal witness.
            if (actor == null && !operatorInTransit && operatorCard == null && remote == null
                && garrison != null && PlacementRules.CanDepositIntoGarrison(garrison))
            {
                if (generatedOperatorSources == null)
                    generatedOperatorSources = GenerationSource.Enumerate(player, root, ctx, hand,
                        claimedUseKeys: null, triedCardKeys: null, resourceForecast: true);
                // Peer Challenges rank by the card score of the operator each would mint (benefit
                // x success chance - the Challenge paid in full), then the cheaper bill.
                var witnesses = generatedOperatorSources
                    .Where(g => IsGeneratedOperatorCandidate(g, mode)
                        && ArmyActions.HasRequiredGroundDeploymentBuilding(player, hex, g.CardDef)
                        && CardPlayExecutor.CanFitAfterDeploy(garrison, g.CardDef)
                        && ArmyRegistry.AllAt(g.FacilityHex).Any(source => source != null
                            && source.Owner == player && !source.IsPrison
                            && source.Members.Contains(g.Hero)
                            && !occupied.IsArmyClaimed(source.Id)))
                    .Select(g => (step: g, score: describeOnly ? 0f
                        : DevelopmentPreparationScorer.GeneratedOperator(g, hex, mode, garrison, snap, inv,
                            spendable, player)))
                    .OrderByDescending(x => x.score)
                    .ThenBy(x => ActionPrice.ToCardScore(ResearchProductionSystem.AttemptApCost(x.step.CardDef))
                        + StrategicCardEvaluator.StrategicResourceCostValue(
                            x.step.GenerationResourceCost, snap))
                    .ThenBy(x => x.step.CardKey, System.StringComparer.Ordinal)
                    .ToList();
                if (witnesses.Count > 0)
                {
                    generatedOperator = witnesses[0].step;
                    generatedScore = witnesses[0].score;
                }
            }
            // Facility stage only: the qualified Hero may still be in the remaining deck. Building
            // the facility now and drawing its operator on a later turn is a normal staged plan;
            // the operator stage itself still needs a real operator (hand, map or Challenge).
            // One deck promise at a time: while any facility already waits for its operator, a
            // second facility must not be built on the same future draw.
            CardDefinition deckOperator = null;
            if (actor == null && !operatorInTransit && operatorCard == null && remote == null
                && generatedOperator == null && !snap.Development.AnyOperatorlessFacility)
                deckOperator = snap.Self.Deck?
                    .Where(d => d != null && d.cardType == CardType.Hero
                        && MaterializationChainMatching.EffectiveAbilities(d, null)
                            .Contains(ResearchProductionSystem.RoleAbility(mode)))
                    .OrderBy(d => StrategicCardEvaluator.StrategicResourceCostValue(d.resourceCost, snap))
                    .ThenBy(d => d.authoredKey, System.StringComparer.Ordinal)
                    .FirstOrDefault();
            if (actor == null && !operatorInTransit && operatorCard == null && remote == null && generatedOperator == null
                && deckOperator == null)
                return "reason=no_operator";

            // Preparation opens a capability; it does not commit to a future catalog output.
            // Both components are alternatives at the first step. Only the selected action's bill
            // and actor opportunity cost are evaluated; completed components are sunk facts.
            int candidates = 0, admitted = 0;
            string rejection = "no_current_component";
            // cardScore - the intrinsic card score of this exact step (null: a walk of an existing
            // hero, a world task priced in TaskScore).
            void Add(DevelopmentPreparationKind kind, ResourceCost cost, CardData card,
                GenerationStep generated, UnitData hero, ArmyData sourceArmy, int travel,
                float? cardScore = null)
            {
                candidates++;
                var delivery = hero != null ? OperatorDeliveryFacts(sourceArmy, hero, travel) : default;
                float displaced = hero != null && sourceArmy != null
                    ? MissionIntent.DisplacementValueOf(activeIntents, sourceArmy.Id) : 0f;
                // A card step is valued ONLY by the shared card scorer (cardScore). The TaskScore
                // below is the world task of walking an existing hero: its compatibility baseline
                // is the infrastructure value, with no completion bonus or predicted output EV.
                var score = new TaskScore(strategicRelevance: AiConfigV2.nonCombatFacilityValue
                        * (generated != null ? Mathf.Clamp01(generated.SuccessChance) : 1f),
                    cardPrice: TaskScoreEvaluator.Price(delivery.ActionAp + delivery.ActivationNow),
                    delivery: TaskScoreEvaluator.Price(ActionPrice.RecurringAp(
                        delivery.RecurringActivationAp, delivery.EtaTurns)),
                    moverOpportunityCost: TaskScoreEvaluator.MoverOpportunityCost(displaced));
                if (cardScore.HasValue)
                    score = default;
                var op = new DevelopmentOpportunity
                {
                    Mode = mode, FacilityHex = hex, PreparationKind = kind,
                    StageResourceCost = cost, WorldTaskScore = score,
                    PreparationCapacityTier = kind == DevelopmentPreparationKind.CapacityUnlock ? capacityTier : null,
                    PreparationExpectedLevel = kind == DevelopmentPreparationKind.CapacityUnlock
                        ? BuildingRegistry.FindAt(hex)?.Level ?? 0 : 0,
                    PreparationFacilityCard = kind == DevelopmentPreparationKind.Operator ? null : card,
                    PreparationOperatorCard = kind == DevelopmentPreparationKind.Operator ? card : null,
                    PreparationOperatorGeneration = generated, PreparationExistingHero = hero,
                    PreparationSourceArmyId = sourceArmy?.Id, PreparationTravelCost = travel,
                    PreparationCardScore = cardScore,
                    Explain = $"{mode} @({hex.Q},{hex.R}) prepare {kind} "
                        + $"facility={(facilityReady ? 1 : 0)} operator={(actor != null ? 1 : 0)} "
                        + $"stageCost={cost} "
                        + (cardScore.HasValue ? $"card={cardScore.Value:0.##}" : $"task={score.Value:0.##}"),
                };
                // CapacityUnlock pays only the tier; its witness card is not played by this step.
                int stageAp = kind == DevelopmentPreparationKind.CapacityUnlock ? capacityTier?.apCost ?? 0
                    : card?.EffectivePlayApCost ?? (generated == null ? 0
                        : ResearchProductionSystem.AttemptApCost(generated.CardDef));
                if (cardScore.HasValue)
                {
                    // Already net of the one canonical price: the number the Phase A arbiter ranks.
                    op.PreparationRank = cardScore.Value;
                    if (!describeOnly && cardScore.Value <= AiConfigV2.allocatorSliceEpsilon)
                    { rejection = "current_stage_not_worth_playing"; return; }
                }
                else
                {
                    // As in the shared card scorer: task merit and the canonical card price are
                    // separate terms. The delivery's net is read in card units (ActionPrice's own
                    // conversion) only so peer steps can be ordered; it is never written back.
                    float taskNet = score.Value - ActionPrice.ToCardScore(ActionPrice.Ap(stageAp))
                        - StrategicCardEvaluator.StrategicResourceCostValue(cost, snap,
                            spendable, player);
                    op.PreparationRank = ActionPrice.ToCardScore(ActionPrice.FromTaskScore(taskNet));
                    if (!describeOnly && taskNet <= AiConfigV2.allocatorSliceEpsilon)
                    { rejection = "current_stage_opportunity_cost"; return; }
                }
                forecasts.Add(op);
                if (!describeOnly)
                {
                    List<ResourceType> closed = DevelopmentInvestmentGate.ClosedResources(
                        player, snap.TurnNumber, cost);
                    if (closed.Count > 0)
                    { rejection = $"window_closed({ResourceList(closed)})"; return; }
                    if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, cost))
                    { rejection = "current_stage_resources_unavailable"; return; }
                    if (hero == null && StrategicSpendability.SpendableAp(player, root, ctx)
                            + AiConfigV2.allocatorSliceEpsilon < stageAp)
                    { rejection = "current_stage_ap_unavailable"; return; }
                }
                result.Add(op);
                admitted++;
            }
            if (!facilityReady && facility != null)
            {
                if (capacityTier != null)
                    Add(DevelopmentPreparationKind.CapacityUnlock, capacityTier.cost,
                        facility, null, null, null, 0, facilityScore);
                else
                    Add(DevelopmentPreparationKind.Facility, facility.EffectivePlayResourceCost,
                        facility, null, null, null, 0, facilityScore);
            }
            if (actor == null && !operatorInTransit)
            {
                if (operatorCard != null)
                    Add(DevelopmentPreparationKind.Operator, operatorCard.EffectivePlayResourceCost,
                        operatorCard, null, null, null, 0, operatorScore);
                else if (remote != null)
                    Add(DevelopmentPreparationKind.Operator, null, null, null, remote, remoteArmy, remoteTravel);
                else if (generatedOperator != null)
                    Add(DevelopmentPreparationKind.Operator, generatedOperator.GenerationResourceCost,
                        null, generatedOperator, null, null, 0, generatedScore);
            }
            return $"components={candidates} admitted={admitted}"
                + (admitted == 0 ? $" reason={rejection}" : "");
        }

        // THE live confirmation of an admitted CapacityUnlock plan, shared by Phase A and Phase B: everything
        // the admission assumed is re-derived from the live world immediately before payment. A mismatch is
        // a stale plan (nothing is re-targeted at another base, tier or card). The structural witnesses
        // (operator path, catalog, no facility of the mode elsewhere) are re-read from PreparationFacts,
        // the one owner of that rule.
        internal static bool ConfirmCapacityUnlock(DevelopmentOpportunity plan, WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            IReadOnlyList<MissionIntent> activeIntents, out BuildingData building, out BaseUpgradeTier tier)
        {
            building = null;
            tier = null;
            if (plan?.PreparationKind != DevelopmentPreparationKind.CapacityUnlock
                || plan.PreparationCapacityTier == null || plan.PreparationFacilityCard == null
                || hand?.Hand == null || !hand.Hand.Contains(plan.PreparationFacilityCard))
                return false;
            HexCoord hex = plan.FacilityHex;
            ResearchProductionMode mode = plan.Mode;
            BuildingData b = BuildingRegistry.FindAt(hex);
            if (b == null || b.Owner != player || !b.IsBase || !b.HasTieredUnlock
                || Game.Combat.BattleInitiator.FindEnemyAt(hex, player) != null
                || b.FindFirstAvailableFacilitySlot() >= 0          // a slot is already open: nothing to buy
                || b.Level != plan.PreparationExpectedLevel)
                return false;
            BaseUpgradeTier next = StrategicMaintenancePolicy.CapacityUnlockTierAt(b, ctx);
            if (!ReferenceEquals(next, plan.PreparationCapacityTier))
                return false;
            CardDefinition witness = plan.PreparationFacilityCard.Definition;
            if (witness?.cardType != CardType.Facility
                || witness.grantedAbilities?.Contains(ResearchProductionSystem.FacilityAbility(mode)) != true)
                return false;
            if (!DevelopmentInvestmentGate.IsOpenFor(player, ctx.TurnNumber, next.cost))
                return false;
            bool stillStructural = PreparationFacts(snap, player, root, hand, ctx, activeIntents)
                .Any(f => f.PreparationKind == DevelopmentPreparationKind.CapacityUnlock
                    && f.Mode == mode && f.FacilityHex.Equals(hex)
                    && ReferenceEquals(f.PreparationFacilityCard, plan.PreparationFacilityCard)
                    && ReferenceEquals(f.PreparationCapacityTier, next));
            if (!stillStructural)
                return false;
            building = b;
            tier = next;
            return true;
        }

        // Structural facts for desire, using the SAME prerequisite enumeration as execution.
        // No resource-window or today's affordability veto; no output/recipient pricing.
        internal static List<DevelopmentOpportunity> PreparationFacts(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var result = new List<DevelopmentOpportunity>();
            if (snap?.Self?.BaseHexes == null || snap.Development == null || hand?.Hand == null
                || player == null || root == null || ctx?.ResearchProductionCatalog == null)
                return result;
            var forecasts = new List<DevelopmentOpportunity>();
            ActorCommitments occupied = ActorCommitments.FromIntents(activeIntents, snap, null);
            List<GenerationStep> sources = null;
            foreach (ResearchProductionMode mode in Modes)
            foreach (HexCoord hex in snap.Self.BaseHexes.OrderBy(h => h.Q).ThenBy(h => h.R))
            {
                BuildingData b = BuildingRegistry.FindAt(hex);
                if (b == null || b.Owner != player) continue;
                bool ready = b.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode));
                UnitData actor = ResearchProductionSystem.FindActor(player, hex, mode);
                if (ready && actor != null) continue;
                AddPreparation(result, forecasts, mode, hex, ready, actor, snap, null, occupied,
                    player, root, hand, ctx, activeIntents, ref sources, describeOnly: true);
            }
            return result;
        }

        internal static float StepResourceHeadroom(ResourceBundle surplus, ResourceCost cost)
        {
            float headroom = 1f;
            foreach (ResourceType t in ResourceBundle.All)
                if ((cost?.Get(t) ?? 0) > 0) headroom = Mathf.Min(headroom, surplus.Get(t));
            return Mathf.Clamp01(headroom);
        }

        // A prepared operator can stand on its own base before a facility exists. This is NOT
        // permission to generate: ResearchProductionSystem keeps the staffed-source contract.
        internal static bool IsPreparationSite(Game.Players.PlayerSetupData player, HexCoord hex)
        {
            BuildingData b = BuildingRegistry.FindAt(hex);
            return b != null && b.Owner == player && b.IsBase;
        }

        internal static bool OperatorPreparedAt(PlayerSetupData player, UnitData hero, HexCoord hex,
            ResearchProductionMode mode) => IsPreparationSite(player, hex)
                && hero != null && hero.Owner == player && hero.IsHero && !hero.IsPrisoner
                && hero.HasAbility(ResearchProductionSystem.RoleAbility(mode))
                && ArmyRegistry.AllAt(hex).Any(a => a.Owner == player && !a.IsPrison
                    && a.Members.Contains(hero));

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

        // Non-equipment outputs already have ONE canonical materialization/scoring path. Use a
        // projected plan to value the future investment; never an executable source.
        private static DevelopmentOpportunity PrepareDeployable(CardDefinition card,
            ResearchProductionMode mode, HexCoord hex, UnitData projectedActor, float operatorChance,
            WorldSnapshot snap, CapabilityInventory inv,
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
                Ev = futureValue * operatorChance,
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
                // card effects, alternative role and hold. Prerequisite actions are sunk here.
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
            // Pending-hand coverage depends only on the recipient and the slot being produced
            // (snapshot, inventory and hand are fixed for one Enumerate); negatives are cached too.
            private readonly Dictionary<(object, object), bool> _pending = new();
            public RecipientEvaluationMemo() { _outer = s_current; s_current = this; }
            public void Dispose() => s_current = _outer;

            public static bool TryGet(CardDefinition equipment, object recipient, bool future,
                out RecipientVerdict verdict)
            {
                verdict = default;
                return s_current != null
                    && s_current._verdicts.TryGetValue((equipment, recipient, future), out verdict);
            }

            public static void Store(CardDefinition equipment, object recipient, bool future,
                RecipientVerdict verdict)
            {
                if (s_current != null) s_current._verdicts[(equipment, recipient, future)] = verdict;
            }

            public static bool TryGetPending(object recipient, object slot, out bool covers)
            {
                covers = false;
                return s_current != null && s_current._pending.TryGetValue((recipient, slot), out covers);
            }

            public static void StorePending(object recipient, object slot, bool covers)
            {
                if (s_current != null) s_current._pending[(recipient, slot)] = covers;
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
            RecipientVerdict? lastNoNeed = null;
            float powerUnit = AiConfigV2.combatPowerPerBodyEstimate;
            // Memoised only for the standard (no explicit attachment card) call.
            bool memoised = attachmentCard == null;

            // Production context of a real generation source (READY, and its materialization
            // re-enumeration), applied BEFORE the gain is priced so the task score, the card EV and
            // the plan Phase A rebuilds all see one value: a card made lately is damped (diversity).
            // The deck-size "supply" multiplier is gone: it never belonged to the item's own utility.
            // Previews (no source) and hand cards keep their own rules.
            float productionScale = 1f;
            string productionNote = string.Empty;
            if (generation != null)
                productionScale = DevelopmentDiversity.RepeatFactor(player, snap?.TurnNumber ?? 0,
                    equipment, out productionNote);

            RecipientVerdict Evaluate(object recipient, CardData card, UnitData unit)
            {
                if (memoised && RecipientEvaluationMemo.TryGet(equipment, recipient, futureAttachment,
                    out RecipientVerdict cached))
                    return cached;
                using var __miss = new Game.Core.ProfileScope("AI/Dev.RecipientVerdict");
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
                    string purpose;
                    using (new Game.Core.ProfileScope("AI/Dev.PurposeLabel"))
                        purpose = StrategicCardEvaluator.EquipmentPurposeLabel(snap, card, unit);
                    DevelopmentOpportunity probe = MakeProbe(equipment, card, unit, gain, powerUnit);
                    // Delta already includes mission-scoped penetration and effect usefulness.
                    // Utility is required even for in-advance investment; surplus alone cannot admit it.
                    bool noNeed = StrategicCardEvaluator.EquipmentUpgradeValue(probe) <= 0f;
                    // A real card in hand is the pending stage. Reuse it before manufacturing more
                    // for its useful recipient slot; ordinary hand attachment enumeration stays live.
                    bool covers = false;
                    if (!noNeed && futureAttachment)
                    {
                        // Boxed slot is the memo key; recipient identity separates CardData from UnitData.
                        object slot = equipment.attachmentSlot;
                        if (!memoised || !RecipientEvaluationMemo.TryGetPending(recipient, slot, out covers))
                        {
                            using (new Game.Core.ProfileScope("AI/Dev.PendingCovers"))
                                covers = PendingEquipmentCovers(probe, hand, snap, inv);
                            if (memoised) RecipientEvaluationMemo.StorePending(recipient, slot, covers);
                        }
                    }
                    verdict = new RecipientVerdict(true, null, gain, purpose, noNeed, covers);
                }
                if (memoised) RecipientEvaluationMemo.Store(equipment, recipient, futureAttachment, verdict);
                return verdict;
            }

            void Consider(RecipientVerdict v, DevRecipientKind kind, CardData card, UnitData unit,
                int? armyId, string label)
            {
                // The explanation is formatted only for an accepted opportunity and for the
                // no-need reject kept in the empty-result diagnostic.
                if (v.NoNeed) { noNeed++; lastReject = null; lastNoNeed = v; return; }
                if (v.PendingCovers) { lastReject = "use_pending_equipment_first"; lastNoNeed = null; return; }
                string explain = "purpose=" + v.Purpose + " slot=" + equipment.attachmentSlot + " "
                    + v.Gain.Detail;
                result.Add(new DevelopmentOpportunity
                {
                    Mode = mode, FacilityHex = facilityHex, Card = equipment, ProducesEquipment = true,
                    SuccessChance = successChance, Generation = generation,
                    RecipientKind = kind, RecipientCard = card, RecipientUnit = unit,
                    RecipientArmyId = armyId, RecipientLabel = label,
                    ExpectedGain = v.Gain.Total * powerUnit * productionScale,
                    TacticalGain = v.Gain.Tactical * powerUnit * productionScale,
                    Explain = productionNote + explain,
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
                    { lastReject = v.Why; lastNoNeed = null; continue; }
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
                    { lastReject = v.Why; lastNoNeed = null; continue; }
                    Consider(v, army.IsGarrison ? DevRecipientKind.GarrisonUnit : DevRecipientKind.FieldUnit,
                        null, u, army.Id,
                        $"{(army.IsGarrison ? "garr" : "field")}:{u.Name ?? "unit"}@{army.Hex.Q},{army.Hex.R}");
                }
            }

            if (result.Count == 0 && lastNoNeed.HasValue)
                lastReject = "purpose=" + lastNoNeed.Value.Purpose + " slot=" + equipment.attachmentSlot + " "
                    + lastNoNeed.Value.Gain.Detail;
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

