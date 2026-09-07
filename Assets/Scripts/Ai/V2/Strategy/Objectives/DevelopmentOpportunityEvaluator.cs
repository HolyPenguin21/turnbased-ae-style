using System.Collections.Generic;
using System.Linq;
using Game.Cards;
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
    //      projected power gain G), score EV = p*G - A_total - apCost, keep it if EV > margin.
    //      Phase A then executes the surviving opportunities in descending BaseValue, re-scoring
    //      after each Challenge. "Which card gets created" == "highest EV that still passes the
    //      live gates".
    //
    //      p       = ResearchProductionSystem.EstimateSuccessChance  (deterministic)
    //      G       = projected BasePower(recipient WITH the equipment) - BasePower(WITHOUT),
    //                * recipient importance (raid-bound field unit > field > garrison > hand card)
    //      A_total = value of the best ALTERNATIVE use of the same resources this turn — first
    //                pass: strongest affordable hand Unit card, discounted by surplus depth
    //                (deep surplus -> ~0). This is the "upgrade vs. play a new unit" comparison.
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

        public DevRecipientKind RecipientKind;
        public CardData RecipientCard;       // HandCard
        public UnitData RecipientUnit;       // Garrison / Field
        public string RecipientLabel;

        public float ExpectedGain;           // G — frozen once (recipient is structural)
        public float AlternativeValue;       // A_total — re-scored each Phase-A round
        public float Ev;
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

            var raidHexes = new HashSet<HexCoord>();
            if (aggObjectives != null)
                foreach (AggressionObjective o in aggObjectives)
                    if (o != null) raidHexes.Add(o.LastKnownHex);

            foreach (DevelopmentOffering off in rd.Offerings)
            {
                if (!off.ProducesEquipment)
                    continue;   // scope: equipment upgrades only
                DevelopmentOpportunity best = BestEquipmentOpportunity(off, player, root, hand, raidHexes);
                if (best == null)
                    continue;

                Score(best, snap, root, hand);
                if (best.Ev <= AiConfigV2.devEvMargin)
                    continue;
                best.Explain = $"{best.Mode} '{off.Card.displayName}' -> {best.RecipientLabel} "
                    + $"p={best.SuccessChance:0.00} G={best.ExpectedGain:0.0} "
                    + $"A={best.AlternativeValue:0.0} EV={best.Ev:0.0}";
                result.Add(best);
            }

            result.Sort((a, b) => b.BaseValue.CompareTo(a.BaseValue));
            AiDebugLog.Write($"[AI][V2][Dev] objectives {result.Count}");
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
            if (op != null)
                Score(op, snap, root, hand);
        }

        private static void Score(DevelopmentOpportunity op, WorldSnapshot snap, PlayerRoot root, AiHandData hand)
        {
            float surplusRetain = 1f - Curves.Ramp(snap?.Development?.SurplusFraction ?? 0f,
                AiConfigV2.devSurplusRampLo, AiConfigV2.devSurplusRampHi);
            float aTotal = surplusRetain * BestAffordableHandUnitPower(hand, root);
            float apCostValue = AiConfigV2.devRpApCost * AiConfigV2.devApValue;

            op.AlternativeValue = aTotal;
            op.Ev = op.SuccessChance * op.ExpectedGain - aTotal - apCostValue;
            op.BaseValue = Mathf.Clamp(AiConfigV2.devEvToBaseValue * op.Ev, 0f, 100f);
        }

        // Best legal recipient for an Equipment offering. Hand Unit/Hero cards + own on-map units,
        // gated by EquipmentSystem.CanAttach (host kind + type tags + free slot + affordability).
        private static DevelopmentOpportunity BestEquipmentOpportunity(DevelopmentOffering off,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, HashSet<HexCoord> raidHexes)
        {
            EquipmentGrant grant = off.Card.equipment;
            if (grant == null)
                return null;

            DevelopmentOpportunity best = null;
            void Consider(DevelopmentOpportunity cand)
            {
                if (cand != null && (best == null || cand.ExpectedGain > best.ExpectedGain))
                    best = cand;
            }

            // Hand cards — projected delta via AiPower.EffectiveLine (composes any equipment
            // already stashed on the card + the new grant).
            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                {
                    if (c?.Definition == null) continue;
                    if (c.Definition.cardType != CardType.Unit && c.Definition.cardType != CardType.Hero) continue;
                    if (!EquipmentSystem.CanAttach(off.Card, c, root, out _)) continue;
                    float delta = Mathf.Max(0f,
                        AiPower.EffectiveLine(c.Definition, c.Equipment != null ? c.Equipment.equipment : null, grant).BasePower
                        - AiPower.EffectiveLine(c.Definition, c.Equipment != null ? c.Equipment.equipment : null).BasePower);
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
                    if (!EquipmentSystem.CanAttach(off.Card, u, root, out _)) continue;
                    float importance = army.IsGarrison
                        ? AiConfigV2.devImportanceGarrison
                        : raidHexes.Contains(army.Hex) ? AiConfigV2.devImportanceRaidMatch
                        : AiConfigV2.devImportanceField;
                    float gain = OnMapEquipmentGain(u, grant) * importance;
                    Consider(Make(off, army.IsGarrison ? DevRecipientKind.GarrisonUnit : DevRecipientKind.FieldUnit,
                        null, u, $"{(army.IsGarrison ? "garr" : "field")}:{u.Name ?? "unit"}@{army.Hex.Q},{army.Hex.R}", gain));
                }
            }
            return best;
        }

        // Real projected power delta for equipping an on-map unit. Uses OriginatingCard so it
        // scores the same weighted stat line AiPower.EffectiveLine gives a hand card.
        private static float OnMapEquipmentGain(UnitData u, EquipmentGrant grant)
        {
            if (u?.OriginatingCard == null)
                return AiPower.UnitPower(u) * AiConfigV2.devEquipGainFraction;
            EquipmentGrant existing = u.Equipment != null ? u.Equipment.equipment : null;
            float with = AiPower.EffectiveLine(u.OriginatingCard, existing, grant).BasePower;
            float without = AiPower.EffectiveLine(u.OriginatingCard, existing).BasePower;
            return Mathf.Max(0f, with - without);
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
            RecipientKind = kind,
            RecipientCard = card,
            RecipientUnit = unit,
            RecipientLabel = label,
            ExpectedGain = Mathf.Max(0f, gain),
        };

        private static float BestAffordableHandUnitPower(AiHandData hand, PlayerRoot root)
        {
            if (hand?.Hand == null)
                return 0f;
            float best = 0f;
            foreach (CardData c in hand.Hand)
            {
                if (c?.Definition == null || c.Definition.cardType != CardType.Unit) continue;
                ResourceCost cost = c.EffectivePlayResourceCost;
                if (cost != null && !cost.CanAfford(root)) continue;
                best = Mathf.Max(best, AiPower.ToPowerUnit(c.Definition).BasePower);
            }
            return best;
        }
    }
}
