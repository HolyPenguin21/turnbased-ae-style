using System.Collections.Generic;
using System.Linq;
using Game.Cards;

namespace Game.Ai.V2
{
    // ARCH-02 §9 — MaterializationChainMatching: the pure capability / trait / equipment-host
    // predicates a chain enumeration consults to decide which cards are even relevant to a demand.
    // Read-only, no plan construction. Bodies verbatim from MaterializationCandidateBuilder.
    internal static class MaterializationChainMatching
    {
        internal static IReadOnlyList<string> EffectiveAbilities(CardDefinition def, CardDefinition attachedEquipment)
        {
            var baseList = def?.grantedAbilities != null ? new List<string>(def.grantedAbilities) : new List<string>();
            if (attachedEquipment?.equipment == null) return baseList;
            return EquipmentSystem.EffectiveAbilities(baseList, attachedEquipment.equipment);
        }

        internal static bool MatchesCapabilityDef(CardDefinition d, CapabilityKind kind)
        {
            if (d == null || d.isAviation) return false;
            bool recce = AbilityParams.AbilitiesHaveAnyRecce(d.grantedAbilities);
            switch (kind)
            {
                case CapabilityKind.ScoutCapability: return recce;
                case CapabilityKind.Hero: return d.cardType == CardType.Hero && !recce;
                case CapabilityKind.FieldCombatPower:
                case CapabilityKind.GarrisonCombatPower:
                    return !recce && (d.cardType == CardType.Unit || d.cardType == CardType.Hero);
                default: return false;
            }
        }

        internal static bool AbilitiesSatisfyCapability(IReadOnlyList<string> abilities, CardType type, CapabilityKind kind)
        {
            bool recce = AbilityParams.AbilitiesHaveAnyRecce(abilities);
            switch (kind)
            {
                case CapabilityKind.ScoutCapability: return recce;
                case CapabilityKind.Hero: return type == CardType.Hero;
                case CapabilityKind.FieldCombatPower:
                case CapabilityKind.GarrisonCombatPower: return type == CardType.Unit || type == CardType.Hero;
                default: return false;
            }
        }

        internal static bool MeetsRequiredTraits(IReadOnlyList<string> abilities, TraitPreference required)
        {
            if (required == TraitPreference.None) return true;
            if ((required & TraitPreference.Stealth) != 0 && !AbilityParams.AbilitiesHaveAnyStealth(abilities))
                return false;
            if ((required & (TraitPreference.AntiArmour | TraitPreference.Ranged | TraitPreference.Melee)) != 0)
                return false;
            return true;
        }

        internal static TraitPreference TraitsOf(IReadOnlyList<string> abilities)
        {
            TraitPreference t = TraitPreference.None;
            if (AbilityParams.AbilitiesHaveAnyStealth(abilities)) t |= TraitPreference.Stealth;
            return t;
        }

        internal static bool EquipmentDefFitsHostDef(CardDefinition eq, CardDefinition host)
        {
            if (eq == null || eq.cardType != CardType.Equipment || eq.equipment == null) return false;
            if (host == null || (host.cardType != CardType.Unit && host.cardType != CardType.Hero)) return false;
            EquipmentHostKind kind = host.cardType == CardType.Hero ? EquipmentHostKind.Hero : EquipmentHostKind.Unit;
            EquipmentGrant grant = eq.equipment;
            if (grant.hostKinds == null || !grant.hostKinds.Contains(kind)) return false;
            if (grant.hostTypeTags != null && grant.hostTypeTags.Count > 0)
            {
                if (host.unitTypeTags == null || !grant.hostTypeTags.Any(need => host.unitTypeTags.Contains(need)))
                    return false;
            }
            return true;
        }
    }
}
