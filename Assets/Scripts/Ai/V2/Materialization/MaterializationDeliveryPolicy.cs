using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ARCH-02 §16/§47 — the ONE owner of "can this materialization / army operationally satisfy a
    // capability demand" for FieldCombatPower / GarrisonCombatPower / Hero / ScoutCapability.
    // Before ARCH-02 the same switch lived three times: MaterializationCandidateBuilder
    // (CanDeliverDemandOperationally, plan-level, unclassified => deliverable), StrategicManager
    // (CanDeliverResidualOperationally, plan-level, unclassified => NOT deliverable) and
    // CapabilityDeliveryEvaluator (IsOperationalForDemand, army-level). They are unified here; the
    // former StrategicManager copy's "unclassified => false" default was unreachable (a
    // materialization plan's FinalCapability is never an infrastructure kind and
    // BestUnresolvedDemandFor filters on FinalCapability), so folding it onto this one loses no
    // behaviour.
    internal static class MaterializationDeliveryPolicy
    {
        // Plan-level: would executing this chain move the live capability inventory for `demand`?
        // (A garrison deposit is preparation, not Field/Hero delivery; a lone Hero shell is
        // reserve-only until it has an escort; a Scout placement always counts.)
        internal static bool CanDeliverDemandOperationally(MaterializationPlan p, AxisDemand demand)
        {
            if (p == null || demand == null) return false;
            switch (demand.Capability)
            {
                case CapabilityKind.ScoutCapability:
                    return true;
                case CapabilityKind.GarrisonCombatPower:
                    return p.Deploy.Kind == DeploymentKind.Garrison;
                case CapabilityKind.Hero:
                    // Economy does not need a combat-ready hero stack: its canonical builder shape
                    // is AiArmyRoles.IsHeroLed, so a legal field placement may create a solo hero.
                    // Keep the escort rule unchanged for every non-Economy Hero demand.
                    if (IsEconomyHeroDemand(demand))
                        return p.Deploy.Kind == DeploymentKind.NewArmy
                            || p.Deploy.Kind == DeploymentKind.ReusableShell
                            || p.Deploy.Kind == DeploymentKind.ExistingArmy;
                    return p.Deploy.Kind == DeploymentKind.ExistingArmy
                        && p.Deploy.Army != null
                        && p.Deploy.Army.Members.Any(u => u != null && !u.IsHero && !u.IsAviation);
                case CapabilityKind.FieldCombatPower:
                {
                    if (p.Deploy.Kind == DeploymentKind.Garrison) return false;
                    CardDefinition d = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                    bool hero = d != null && d.cardType == CardType.Hero;
                    if (!hero) return true;
                    return p.Deploy.Kind == DeploymentKind.ExistingArmy
                        && p.Deploy.Army != null
                        && p.Deploy.Army.Members.Any(u => u != null && !u.IsHero && !u.IsAviation);
                }
                default:
                    return true;
            }
        }

        // Army-level: is this already-existing army an operational instance of `demand`'s capability
        // (used to lease armies that satisfied a live strategic demand to Housekeeping).
        internal static bool IsArmyOperationalForDemand(ArmySnapshot army, AxisDemand demand)
        {
            if (army == null || demand == null)
                return false;
            return IsEconomyHeroDemand(demand)
                ? army.IsMobileEconomyBuilder
                : IsArmyOperationalForCapability(army, demand.Capability, demand.RequiredTraits);
        }

        internal static bool IsEconomyHeroDemand(AxisDemand demand)
            => demand != null
                && demand.RequestingAxis == DesireAxis.Economy
                && demand.Capability == CapabilityKind.Hero;

        // Capability-level form used when Phase B deliberately creates useful surplus without an
        // AxisDemand object. Keeping it here prevents lease bookkeeping from growing a second
        // definition of what an operational Scout is.
        internal static bool IsArmyOperationalForCapability(ArmySnapshot army,
            CapabilityKind capability, TraitPreference requiredTraits)
        {
            if (army == null)
                return false;
            switch (capability)
            {
                case CapabilityKind.FieldCombatPower:
                    return army.IsStructuralRaidActor;
                case CapabilityKind.GarrisonCombatPower:
                    return army.IsGarrison;
                case CapabilityKind.Hero:
                    return army.HasHero && army.IsStructuralRaidActor;
                case CapabilityKind.ScoutCapability:
                    if (!army.IsSoloRecce || army.CurrentMovement <= 0)
                        return false;
                    return (requiredTraits & TraitPreference.Stealth) == 0
                        || army.IsHidden || army.CanEnterStealth;
                default:
                    return false;
            }
        }
    }
}
