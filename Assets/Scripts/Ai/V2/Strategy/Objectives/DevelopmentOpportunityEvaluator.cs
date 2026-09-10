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
    //  Turns the shared snapshot.Development readiness into concrete, EV-scored upgrade
    //  opportunities. One opportunity = "run THIS catalog card at THIS facility to strengthen THIS
    //  recipient". SCOPE: EQUIPMENT offerings only — this axis is "strengthen existing cards".
    //  Non-Equipment R/P mints (a whole new Unit/Hero card) stay with the existing
    //  GenerationSource / MaterializationCandidateBuilder path.
    //
    //  HOW A CARD IS PICKED
    //      For every Equipment offering: find its BEST legal recipient (the one with the largest
    //      signed strategic gain G, including lost roles/abilities), then score
    //      EV = p*G*persistence - A_total - dynamicResourceCost - apCost,
    //      keep it if EV > margin.
    //      Phase A then executes the surviving opportunities in descending BaseValue, re-scoring
    //      after each Challenge. "Which card gets created" == "highest EV that still passes the
    //      live gates".
    //
    //      p       = ResearchProductionSystem.EstimateSuccessChance  (deterministic)
    //      G       = signed stat + ability/role delta under the canonical card evaluator,
    //                * recipient importance (raid-bound field unit > field > garrison > hand card)
    //      A_total = MARGINAL value actually displaced by staking these resources this turn:
    //                strongest affordable hand Unit before the stake minus strongest still
    //                affordable after it, discounted by surplus depth (deep surplus -> ~0).
    //                AP is deliberately excluded from this delta because it is priced separately.
    //
    //  NOT a scoring gate: the enemy-on-hex rule (facility contested) — that is a Phase-A
    //  execution precondition only.
    //
    //  Like DemandLayer this is NOT a pure WorldSnapshot function: CanAttach / recipient
    //  enumeration need live CardData / UnitData. Deliberate, same exception DemandLayer takes.
    // ===========================================================================================
    public enum DevRecipientKind { HandCard, GarrisonUnit, FieldUnit }

    public sealed class DevelopmentOpportunity
    {
        public ResearchProductionMode Mode;
        public HexCoord FacilityHex;
        public CardDefinition Card;          // the Equipment card a won Challenge mints
        public bool ProducesEquipment;       // always true (kept for symmetry with the offering)
        public ResourceBundle StakeCost;
        public float SuccessChance;
        public GenerationStep Generation;    // exact operator/source selected by GenerationSource

        public DevRecipientKind RecipientKind;
        public CardData RecipientCard;       // HandCard
        public UnitData RecipientUnit;       // Garrison / Field
        public string RecipientLabel;

        public float ExpectedGain;           // G — frozen once (recipient is structural)
        public float AlternativeValue;       // A_total — re-scored each Phase-A round
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
            PlayerRoot root, AiHandData hand, IReadOnlyList<AggressionObjective> aggObjectives)
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
                    off, snap, inv, player, root, hand, raidHexes, out string recipDiag);
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

                Score(best, snap, root, hand);
                string verdict = best.Ev > AiConfigV2.devEvMargin ? "ACCEPT" : "REJECT ev<=margin";
                AiDebugLog.Write($"[AI][V2][Dev]   offering '{card}' {off.Mode} -> {best.RecipientLabel} "
                    + $"p={best.SuccessChance:0.00} G={best.ExpectedGain:0.0} A={best.AlternativeValue:0.0} "
                    + $"resCost={best.ResourceCostValue:0.##} apCost={best.ExpectedApCost:0.##} "
                    + $"prodSupport={best.ProductionSupport:0.00} EV={best.Ev:0.00} "
                    + $"(margin {AiConfigV2.devEvMargin:0.00}) => {verdict}");
                if (best.Ev <= AiConfigV2.devEvMargin)
                    continue;
                best.Explain = $"{best.Mode} '{off.Card.displayName}' -> {best.RecipientLabel} "
                    + $"p={best.SuccessChance:0.00} G={best.ExpectedGain:0.0} "
                    + $"A={best.AlternativeValue:0.0} EV={best.Ev:0.0}";
                result.Add(best);
            }

            result.Sort((a, b) => b.BaseValue.CompareTo(a.BaseValue));
            AiDebugLog.Write($"[AI][V2][Dev] objectives {result.Count} "
                + $"(offerings {rd.Offerings.Count}, non-equip skipped {skippedNonEquip})");
            foreach (DevelopmentOpportunity op in result)
                AiDebugLog.Write($"[AI][V2][Dev]   {op.Explain} base {op.BaseValue:0.0}");
            return result;
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
            op.ProductionSupport = op.Mode == ResearchProductionMode.Production
                ? snap?.Development?.ProductionSupport ?? AiConfigV2.productionSupportMin
                : 1f;
            op.Ev = persistentExpectedGain * op.ProductionSupport
                - aTotal - op.ResourceCostValue
                - op.ExpectedApCost * AiConfigV2.devApValue;
            op.BaseValue = Mathf.Clamp(AiConfigV2.devEvToBaseValue * op.Ev, 0f, 100f);
        }

        // Best legal recipient for an Equipment offering. Hand Unit/Hero cards + own on-map units,
        // gated by EquipmentSystem.CanAttach (host kind + type tags + free slot + affordability).
        private static DevelopmentOpportunity BestEquipmentOpportunity(DevelopmentOffering off,
            WorldSnapshot snap, CapabilityInventory inv, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, HashSet<HexCoord> raidHexes, out string diag)
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
                if (cand == null) return;
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
                        off.Card, c, snap, inv) * AiConfigV2.defencePerBodyPowerEstimate;
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
                        off.Card, u, snap, inv) * AiConfigV2.defencePerBodyPowerEstimate * importance;
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
