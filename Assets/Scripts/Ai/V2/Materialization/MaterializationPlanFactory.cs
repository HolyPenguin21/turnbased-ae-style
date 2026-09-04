using System.Collections.Generic;
using Game.Cards;
using Game.Map;

namespace Game.Ai.V2
{
    // ARCH-02 §9/§12/§13 — MaterializationPlanFactory: constructs a concrete MaterializationPlan
    // for a chosen chain shape and computes its canonical StrategicActionCost (AP + H/E/M/T +
    // hand-slot peak) and stable key. No enumeration, no scoring, no feasibility. Bodies verbatim
    // from MaterializationCandidateBuilder.
    internal static class MaterializationPlanFactory
    {
        internal static MaterializationPlan MakeExistingPlan(MaterializationChainKind kind, AxisDemand demand,
            CardData baseCard, int baseIdx, CardData equip, int equipIdx, PlacementOption opt,
            IReadOnlyList<string> projected)
        {
            var p = new MaterializationPlan
            {
                Kind = kind,
                OwnerAxis = demand?.RequestingAxis,
                FinalCapability = demand?.Capability ?? CapabilityKind.FieldCombatPower,
                ExpectedTraits = MaterializationChainMatching.TraitsOf(projected),
                BaseCardInHand = baseCard,
                EquipmentInHand = kind == MaterializationChainKind.AttachDeploy ? equip : null,
                Deploy = opt,
                ProjectedAbilities = projected,
            };
            FillCostsAndKey(p, baseCard.Definition, baseCard, equip, baseIdx, equipIdx, -1);
            return p;
        }

        internal static MaterializationPlan MakeGeneratedPlan(MaterializationChainKind kind, AxisDemand demand,
            GenerationStep g, CardData baseInHand, int baseIdx, bool generatedIsEquipment, PlacementOption opt,
            IReadOnlyList<string> projected, CardData equipInHand = null, int equipIdx = -1)
        {
            var p = new MaterializationPlan
            {
                Kind = kind,
                OwnerAxis = demand?.RequestingAxis,
                FinalCapability = demand?.Capability ?? CapabilityKind.FieldCombatPower,
                ExpectedTraits = MaterializationChainMatching.TraitsOf(projected),
                Generation = g,
                Deploy = opt,
                ProjectedAbilities = projected,
            };
            CardDefinition baseDef;
            if (generatedIsEquipment)
            {
                p.BaseCardInHand = baseInHand;
                p.GeneratedEquipmentDef = g.CardDef;
                baseDef = baseInHand.Definition;
            }
            else
            {
                p.GeneratedBaseDef = g.CardDef;
                p.EquipmentInHand = kind == MaterializationChainKind.GenerateAttachDeploy ? equipInHand : null;
                baseDef = g.CardDef;
            }
            FillCostsAndKey(p, baseDef, p.BaseCardInHand, p.EquipmentInHand ?? equipInHand, baseIdx, equipIdx, 0);
            return p;
        }

        internal static void FillCostsAndKey(MaterializationPlan p, CardDefinition baseDef, CardData baseInstance,
            CardData equipInstance, int baseIdx, int equipIdx, int genMark)
        {
            int human = 0, energy = 0, materials = 0, tech = 0;
            float ap = 0f;
            ap += p.Deploy.Kind == DeploymentKind.NewArmy ? ArmyActions.CreateArmyApCost : 0;
            if (p.GeneratedBaseDef != null)
                ap += baseDef != null ? ArmyActions.EffectiveDeployApCost(baseDef) : 0;
            else if (baseInstance != null)
            {
                ap += baseInstance.EffectivePlayApCost;
                Accumulate(baseInstance.EffectivePlayResourceCost, ref human, ref energy, ref materials, ref tech);
            }
            if (p.UsesEquipment)
            {
                if (p.GeneratedEquipmentDef != null)
                    ap += p.GeneratedEquipmentDef.activationApCost;
                else if (equipInstance != null)
                {
                    ap += equipInstance.EffectivePlayApCost;
                    Accumulate(equipInstance.EffectivePlayResourceCost, ref human, ref energy, ref materials, ref tech);
                }
            }
            if (p.Generation != null && p.Generation.CardDef?.resourceCost != null)
            {
                ResourceCost rc = p.Generation.CardDef.resourceCost;
                human += rc.human; energy += rc.energy; materials += rc.materials; tech += rc.tech;
            }
            p.ApCost = ap;
            p.ResCost = (human | energy | materials | tech) == 0
                ? null : new ResourceCost { human = human, energy = energy, materials = materials, tech = tech };
            p.HandSlotsNeededAtPeak = p.Generation != null ? 1 : 0;

            string baseKey = p.GeneratedBaseDef != null
                ? "gen:" + p.GeneratedBaseDef.displayName
                : (baseDef != null ? baseDef.displayName : "?") + ":" + baseIdx;
            string eqKey = p.GeneratedEquipmentDef != null
                ? "gen:" + p.GeneratedEquipmentDef.displayName
                : (p.EquipmentInHand?.Definition != null ? p.EquipmentInHand.Definition.displayName + ":" + equipIdx : "-");
            string genKey = p.Generation != null ? p.Generation.CardKey : "-";
            p.StableKey = $"{(int)p.Kind}|{(int)p.FinalCapability}|{baseKey}|{eqKey}|{genKey}|{p.Deploy.Key}";
        }

        internal static void Accumulate(ResourceCost c, ref int h, ref int e, ref int m, ref int t)
        {
            if (c == null) return;
            h += c.human; e += c.energy; m += c.materials; t += c.tech;
        }
    }
}
