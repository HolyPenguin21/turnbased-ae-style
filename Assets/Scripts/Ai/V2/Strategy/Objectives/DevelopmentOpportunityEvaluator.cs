using System.Collections.Generic;
using System.Linq;
using Game.Cards;
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
            var raidHexes = new HashSet<HexCoord>();
            if (aggObjectives != null)
                foreach (AggressionObjective o in aggObjectives)
                    if (o != null) raidHexes.Add(o.LastKnownHex);

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
                    off, snap, inv, player, root, hand, raidHexes, out string recipDiag, supportsNeed);
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
                // Reuse the mode's existing facility. Preparation must not manufacture another
                // lab at every base while the first one can already serve the same output.
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
                    // UnitData is plain data. This unregistered preview is used only for the
                    // canonical probability calculation, never as a generation/execution actor.
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
                        root, hand, new HashSet<HexCoord>(), out _, supportsNeed);
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

        // Re-score an already-built opportunity against the CURRENT snapshot (surplus + best
        // alternative shift as earlier Challenges spend resources). ExpectedGain / SuccessChance
        // stay frozen — the recipient is structural; Phase-A's TryFulfill catches a recipient that
        // vanished.
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
            float surplusRetain = 1f - Curves.Ramp(snap?.Development?.SurplusFraction ?? 0f,
                AiConfigV2.devSurplusRampLo, AiConfigV2.devSurplusRampHi);
            // Resource opportunity cost is marginal: only charge power that this exact stake
            // makes unavailable. Charging the strongest currently affordable Unit unconditionally
            // rejected upgrades even when both actions could still be paid for.
            float bestBeforeStake = BestAffordableHandUnitPower(hand, root, null);
            float bestAfterStake = BestAffordableHandUnitPower(hand, root, op.Card?.resourceCost);
            float displacedAlternative = Mathf.Max(0f, bestBeforeStake - bestAfterStake);
            float aTotal = AiConfigV2.devAlternativeWeight * surplusRetain * displacedAlternative;
            // Challenge AP is certain; attach AP is paid only after a successful roll.
            float challengeAp = ResearchProductionSystem.AttemptApCost(op.Card);
            float expectedAttachAp = op.SuccessChance * Mathf.Max(0, op.Card.activationApCost);
            op.ExpectedApCost = challengeAp + expectedAttachAp;

            op.AlternativeValue = aTotal;
            op.ResourceCostValue =
                StrategicCardEvaluator.StrategicResourceCostValue(op.Card?.resourceCost, snap);
            // Equipment is a persistent improvement, while its AP/resource payment is one-shot.
            // Keep the raw projected delta in ExpectedGain for diagnostics and convert it to
            // lifetime strategic value only at the EV boundary.
            float persistentExpectedGain = op.SuccessChance * op.ExpectedGain
                * AiConfigV2.devEquipmentPersistenceMultiplier;
            // This evaluator produces Equipment in either Research or Production mode.
            // Economy support follows the produced capability, never the facility's label;
            // StrategicCardEvaluator applies this same output-type rule to Unit/Hero mints.
            op.ProductionSupport = snap?.Development?.ProductionSupport
                ?? AiConfigV2.productionSupportMin;
            op.Ev = persistentExpectedGain * op.ProductionSupport
                - aTotal - op.ResourceCostValue
                - op.ExpectedApCost * AiConfigV2.devApValue;
            op.BaseValue = Mathf.Clamp(AiConfigV2.devEvToBaseValue * op.Ev, 0f, 100f);
        }

        // Best legal recipient for an Equipment offering. Hand Unit/Hero cards + own on-map units,
        // gated by EquipmentSystem.CanAttach (host kind + type tags + free slot + affordability).
        private static DevelopmentOpportunity BestEquipmentOpportunity(DevelopmentOffering off,
            WorldSnapshot snap, CapabilityInventory inv, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, HashSet<HexCoord> raidHexes, out string diag,
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
            void Consider(DevelopmentOpportunity cand)
            {
                if (cand == null || (supportsNeed != null && !supportsNeed(cand))) return;
                if (cand.ExpectedGain <= 0f) { gainZero++; return; }
                if (best == null || cand.ExpectedGain > best.ExpectedGain)
                    best = cand;
            }

            // Hand cards — projected delta via AiPower.EffectiveLine (composes any equipment
            // already stashed on the card + the new grant).
            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                {
                    if (c?.Definition == null) continue;
                    if (c.Definition.cardType != CardType.Unit && c.Definition.cardType != CardType.Hero) continue;
                    handChecked++;
                    if (!EquipmentSystem.CanAttach(generatedPreview, c, root, out string why))
                    { lastReject = why; continue; }
                    float delta = StrategicCardEvaluator.EquipmentUpgradeUtilityFor(
                        off.Card, c, snap, inv) * AiConfigV2.combatPowerPerBodyEstimate;
                    Consider(Make(off, DevRecipientKind.HandCard, c, null,
                        $"hand:{c.Definition.displayName}", delta * AiConfigV2.devImportanceHandCard));
                }

            // On-map own units — projected delta from the unit's OriginatingCard (+ its current
            // equipment) + the new grant. Fallback to a flat fraction only when OriginatingCard is
            // unknown (minted / event units).
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army == null || army.IsPrison) continue;
                foreach (UnitData u in army.Members)
                {
                    if (u == null || u.IsPrisoner) continue;
                    mapChecked++;
                    if (!EquipmentSystem.CanAttach(generatedPreview, u, root, out string whyU))
                    { lastReject = whyU; continue; }
                    float importance = army.IsGarrison
                        ? AiConfigV2.devImportanceGarrison
                        : raidHexes.Contains(army.Hex) ? AiConfigV2.devImportanceRaidMatch
                        : AiConfigV2.devImportanceField;
                    float gain = StrategicCardEvaluator.EquipmentUpgradeUtilityFor(
                        off.Card, u, snap, inv) * AiConfigV2.combatPowerPerBodyEstimate * importance;
                    Consider(Make(off, army.IsGarrison ? DevRecipientKind.GarrisonUnit : DevRecipientKind.FieldUnit,
                        null, u, $"{(army.IsGarrison ? "garr" : "field")}:{u.Name ?? "unit"}@{army.Hex.Q},{army.Hex.R}", gain));
                }
            }

            if (best == null)
                diag = $"hand checked {handChecked}, on-map checked {mapChecked}, "
                    + $"positive-gain 0 (zero-gain {gainZero})"
                    + (lastReject != null ? $"; last CanAttach reject: \"{lastReject}\"" : "; all attachable but gain <= 0");
            return best;
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

        // Strongest hand Unit payable after a hypothetical resource commitment. AP is checked
        // against the live pool in both passes: Development's own AP is already charged explicitly
        // in Score, so subtracting it here would count AP scarcity twice.
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
