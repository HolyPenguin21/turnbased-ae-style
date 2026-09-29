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

        // Equipment: marginal gain on the recipient (AiPower units). Deployable: projected net
        // value of the output, already discounted by the operator-generation chance.
        public float ExpectedGain;
        // Equipment only: the tactical share of ExpectedGain (Move/Range/AP/Command/roles, AiPower
        // units) — the part StrategicCardEvaluator.EquipmentUpgradeValue never matchup-gates.
        public float TacticalGain;
        // Equipment: [0..1] share of known threats against which the upgrade improves the outcome.
        public float MatchupFit;
        // PREPARE only: card-currency investment EV (output card score - Challenge -
        // prerequisites). Card-level decisions only; never a world-task value.
        public float Ev;
        // The canonical world-task value of this opportunity (see the header).
        public TaskScore WorldTaskScore;
        public float BaseValue => WorldTaskScore.Value;
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
                        player, root, hand, ctx, activeIntents, ref generatedOperatorSources);
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
                // Sunk facility: no investment. The world-task score only orders ready
                // opportunities; the card decision (and its AP/resource price) is
                // StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade's inside the card portfolio.
                best.WorldTaskScore = BuildDevelopmentScore(
                    best.SuccessChance * StrategicCardEvaluator.EquipmentUpgradeValue(best));
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
            IReadOnlyList<MissionIntent> activeIntents, ref List<GenerationStep> generatedOperatorSources)
        {
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
                    && MaterializationChainMatching.EffectiveAbilities(c.Definition, c.Equipment)
                        .Contains(ResearchProductionSystem.RoleAbility(mode))
                    && garrison != null && CardPlayExecutor.Preflight(player, root, hand, ctx,
                        CardPlayPlan.Into(c, hex, DeploymentKind.Garrison, garrison), out _))
                .OrderBy(c => ActionPrice.ToCardScore(c.EffectivePlayApCost)
                    + StrategicCardEvaluator.StrategicResourceCostValue(c.EffectivePlayResourceCost, snap))
                .FirstOrDefault();
            UnitData remote = null;
            int? remoteArmyId = null;
            int remoteTravel = int.MaxValue;
            float remoteCost = float.PositiveInfinity;
            // Raw facts of the chosen operator walk, priced once by the world-task score.
            float remoteActionAp = 0f, remoteActivationNow = 0f;
            int remoteEtaTurns = 0;
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
                    int walkTurns = Mathf.Max(1, Mathf.CeilToInt(route / (float)Mathf.Max(1, candidate.MoveMax)));
                    float cost = ActionPrice.ToCardScore(
                        (army.IsGarrison ? ArmyActions.CreateArmyApCost : 0f)
                        + (army.HasActivatedThisTurn ? 0f : candidate.ActivationApCost)
                        + ActionPrice.RecurringAp(candidate.ActivationApCost, walkTurns));
                    if (cost >= remoteCost)
                        continue;
                    remote = candidate;
                    remoteArmyId = army.Id;
                    remoteTravel = route;
                    remoteCost = cost;
                    remoteActionAp = army.IsGarrison ? ArmyActions.CreateArmyApCost : 0f;
                    remoteActivationNow = army.HasActivatedThisTurn ? 0f : candidate.ActivationApCost;
                    remoteEtaTurns = Mathf.Max(1, Mathf.CeilToInt(route / (float)Mathf.Max(1, candidate.MoveMax)));
                }
                if (operatorCard != null && remote != null)
                {
                    float handCost = ActionPrice.ToCardScore(operatorCard.EffectivePlayApCost)
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
            if (capacityTier != null)
                // The tier is a one-time investment like the facility, priced once on the canonical
                // AP/resource table (the same price the global-source upgrade path charges).
                preparationCost += ActionPrice.ToCardScore(ActionPrice.Ap(capacityTier.apCost)
                    + ActionPrice.Resources(capacityTier.cost, snap));
            ResourceCost stageCost = facility != null
                ? StrategicCardEvaluator.AddResourceCosts(facility.EffectivePlayResourceCost,
                    capacityTier?.cost)
                : actor == null && remote == null ? operatorResourceCost : null;
            List<ResourceType> stageShort = ResourceBundle.All.Where(t => (stageCost?.Get(t) ?? 0)
                > StrategicSpendability.SpendableAmount(player, root, ctx, t)).ToList();
            if (stageShort.Count > 0)
                return $"reason=stage_unaffordable({ResourceList(stageShort)})";
            List<ResourceType> stageClosed = DevelopmentInvestmentGate.ClosedResources(
                player, snap.TurnNumber, stageCost);
            if (stageClosed.Count > 0)
                return $"reason=window_closed({ResourceList(stageClosed)})";
            float operatorDisplaced = remoteArmyId.HasValue
                ? MissionIntent.DisplacementValueOf(activeIntents, remoteArmyId.Value) : 0f;
            ForceNeed forceNeed = ForceNeedModel.JustifiedForceNeed(snap);
            // The facility and its operator are a one-time investment the site then reuses for
            // every later Challenge; one output carries only its share of that investment.
            float preparationShare = preparationCost / Mathf.Max(1, AiConfigV2.devFacilityExpectedUses);

            int outputs = 0, admitted = 0;
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
                if (shortfall.Count > 0)
                {
                    unaffordable.UnionWith(shortfall);
                    continue;
                }

                DevelopmentOpportunity op = card.isAviation
                        || card.cardType == CardType.Unit || card.cardType == CardType.Hero
                    ? PrepareDeployable(card, mode, hex, projectedActor, operatorChance,
                        preparationShare, snap, inv, occupied, player, root, hand, ctx)
                    : PrepareEquipment(card, mode, hex, projectedActor, operatorChance,
                        preparationShare, snap, inv, player, root, hand, ctx);
                if (op == null)
                    continue;
                // Every output term is conditional on first winning a generated operator.
                float outputChance = operatorChance * Mathf.Clamp01(op.SuccessChance);
                float amplificationBodies = op.ProducesEquipment
                    ? outputChance * StrategicCardEvaluator.EquipmentUpgradeValue(op)
                    : outputChance * forceNeed.Total * ForceBodies(card);
                op.WorldTaskScore = BuildDevelopmentScore(amplificationBodies,
                    remoteActionAp, remoteActivationNow,
                    remote != null ? remote.ActivationApCost : 0f, remoteEtaTurns, operatorDisplaced);
                if (op.BaseValue > bestValue)
                {
                    bestValue = op.BaseValue;
                    bestEv = op.Ev;
                    bestRejected = card.displayName;
                }
                // Card chain worth its cards (card currency) AND a positive world task.
                if (op.Ev <= AiConfigV2.devEvMargin
                    || op.BaseValue <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                op.StageResourceCost = stageCost;
                op.PreparationCapacityTier = capacityTier;
                op.PreparationFacilityCard = facility;
                op.PreparationOperatorCard = operatorCard;
                op.PreparationOperatorGeneration = generatedOperator;
                op.PreparationExistingHero = remote;
                op.PreparationSourceArmyId = remoteArmyId;
                op.PreparationTravelCost = remoteTravel;
                op.Explain = $"{mode} @({hex.Q},{hex.R}) for {card.displayName} -> "
                    + $"{op.RecipientLabel}; task={op.BaseValue:0.##} "
                    + $"amplify={op.WorldTaskScore.ForceAmplification:0.##} "
                    + $"price={op.WorldTaskScore.CardPrice:0.##} delivery={op.WorldTaskScore.Delivery:0.##} "
                    + $"moverOpp={op.WorldTaskScore.MoverOpportunityCost:0.##} {forceNeed}; "
                    + $"card EV={op.Ev:0.##}";
                result.Add(op);
                admitted++;
            }
            string need = $"need[{(capacityTier != null ? "upgrade " : "")}{(facility != null ? "facility" : "")}"
                + $"{(actor == null ? (remote != null ? " hero-travel" : operatorCard != null ? " hero-card" : deckOperator != null ? " hero-deck" : " hero-generate") : "")}]";
            return $"{need} outputs={outputs} admitted={admitted}"
                + (admitted == 0
                    ? (bestRejected != null
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
            DevelopmentOpportunity op = BestEquipmentOpportunity(mode, hex, card,
                ResearchProductionSystem.EstimateSuccessChance(projectedActor, card), null,
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
            float output = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                op, plan, snap, player, root, ctx);
            if (float.IsNaN(output) || float.IsNegativeInfinity(output))
                return null;
            op.Ev = operatorChance * output - preparationCost;
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
        // StrategicCardEvaluator.EquipmentUpgradeValue. null when no recipient gains anything.
        private static DevelopmentOpportunity BestEquipmentOpportunity(ResearchProductionMode mode,
            HexCoord facilityHex, CardDefinition equipment, float successChance,
            GenerationStep generation, WorldSnapshot snap, CapabilityInventory inv,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, out string diag)
        {
            diag = "no equipment grant on the card";
            if (equipment?.equipment == null)
                return null;
            CardData generatedPreview = ResearchProductionSystem.MintCard(equipment);

            int handChecked = 0, mapChecked = 0, gainZero = 0, noNeed = 0;
            string lastReject = null;
            DevelopmentOpportunity best = null;
            float bestSelectionValue = float.NegativeInfinity;
            void Consider(DevelopmentOpportunity cand, ArmyData army = null)
            {
                if (cand.ExpectedGain <= 0f) { gainZero++; return; }
                cand.MatchupFit = StrategicCardEvaluator.EquipmentMatchupFit(cand, army, snap);
                float selection = StrategicCardEvaluator.EquipmentUpgradeValue(cand);
                // A ground-combat upgrade that turns no known fight has no justified need.
                if (selection <= 0f) { noNeed++; return; }
                if (best == null || selection > bestSelectionValue)
                {
                    best = cand;
                    bestSelectionValue = selection;
                }
            }
            float powerUnit = AiConfigV2.combatPowerPerBodyEstimate;
            DevelopmentOpportunity Make(DevRecipientKind kind, CardData card, UnitData unit,
                int? armyId, string label, StrategicCardEvaluator.EquipmentDelta delta) =>
                new DevelopmentOpportunity
            {
                Mode = mode, FacilityHex = facilityHex, Card = equipment, ProducesEquipment = true,
                SuccessChance = successChance, Generation = generation,
                RecipientKind = kind, RecipientCard = card, RecipientUnit = unit,
                RecipientArmyId = armyId, RecipientLabel = label,
                ExpectedGain = Mathf.Max(0f, delta.Total * powerUnit),
                TacticalGain = delta.Tactical * powerUnit,
            };

            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                {
                    if (c?.Definition == null) continue;
                    if (c.Definition.cardType != CardType.Unit && c.Definition.cardType != CardType.Hero) continue;
                    handChecked++;
                    if (!EquipmentSystem.CanAttach(generatedPreview, c, root, out string why))
                    { lastReject = why; continue; }
                    StrategicCardEvaluator.EquipmentDelta gain =
                        StrategicCardEvaluator.EquipmentDeltaParts(equipment, c, snap, inv);
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
                    StrategicCardEvaluator.EquipmentDelta gain =
                        StrategicCardEvaluator.EquipmentDeltaParts(equipment, u, snap, inv);
                    Consider(Make(army.IsGarrison ? DevRecipientKind.GarrisonUnit : DevRecipientKind.FieldUnit,
                        null, u, army.Id,
                        $"{(army.IsGarrison ? "garr" : "field")}:{u.Name ?? "unit"}@{army.Hex.Q},{army.Hex.R}", gain), army);
                }
            }

            if (best == null)
                diag = $"hand {handChecked}, map {mapChecked}, zero-gain {gainZero}, no-need {noNeed}"
                    + (lastReject != null ? $", last reject \"{lastReject}\"" : "");
            return best;
        }
    }
}
