using System.Collections.Generic;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Cards
{
    // The one place a CardType.Equipment card's effect is validated and applied — the analogue
    // of ArmyActions.DeployUnitFromCard for gear. Two attach targets, per the project owner's
    // own spec: a live UnitData already in an army, or a not-yet-spawned Unit/Hero CardData
    // still in hand (the grant then rides along and is applied by ArmyActions.DeployUnitFromCard
    // when that card is finally played). One slot per host, checked as "Equipment == null".
    // There is no un-attach (the manual: "Once placed an attachment card can never be removed").
    //
    // Cost to attach is the equipment card's own apCost + resourceCost (same fields every card
    // has) — spent once, here, whichever target kind it's attached to.
    public static class EquipmentSystem
    {
        // --- family matching (for EquipmentGrant.clearAbilityFamilies) ----------------------
        // Delegates to AbilityParams so the parameterized-tag grammar (r<N>s<M>, Stealth<N>)
        // stays defined in exactly one file.
        public static bool IsFamilyMember(string ability, AbilityFamily family)
        {
            switch (family)
            {
                case AbilityFamily.Recce: return AbilityParams.TryGetRecce(ability, out _, out _);
                case AbilityFamily.Stealth: return AbilityParams.TryGetStealthLevel(ability, out _);
                default: return false;
            }
        }

        // --- validation --------------------------------------------------------------------

        public static bool CanAttach(CardDefinition equipment, UnitData target, PlayerRoot owner, out string reason)
        {
            if (target == null)
            {
                reason = "No target.";
                return false;
            }
            EquipmentHostKind kind = target.IsHero ? EquipmentHostKind.Hero : EquipmentHostKind.Unit;
            return CanAttachCore(equipment, kind, target.TypeTags, target.Equipment != null, owner, out reason);
        }

        // Same checks against a card still in hand — host kind/tags come from the card's own
        // design (CardDefinition), not a spawned UnitData.
        public static bool CanAttach(CardDefinition equipment, CardData targetCard, PlayerRoot owner, out string reason)
        {
            CardDefinition def = targetCard?.Definition;
            if (def == null)
            {
                reason = "No target.";
                return false;
            }
            if (def.cardType != CardType.Unit && def.cardType != CardType.Hero)
            {
                reason = "Equipment can only go on a unit or hero card.";
                return false;
            }
            EquipmentHostKind kind = def.cardType == CardType.Hero ? EquipmentHostKind.Hero : EquipmentHostKind.Unit;
            return CanAttachCore(equipment, kind, def.unitTypeTags, targetCard.Equipment != null, owner, out reason);
        }

        private static bool CanAttachCore(CardDefinition equipment, EquipmentHostKind kind,
            ICollection<UnitTypeTag> hostTags, bool slotTaken, PlayerRoot owner, out string reason)
        {
            reason = null;
            if (equipment == null || equipment.cardType != CardType.Equipment || equipment.equipment == null)
            {
                reason = "Not an equipment card.";
                return false;
            }
            EquipmentGrant grant = equipment.equipment;

            if (grant.hostKinds == null || !grant.hostKinds.Contains(kind))
            {
                reason = $"{equipment.displayName} can't be attached to that.";
                return false;
            }

            // Empty hostTypeTags = fits any host of an allowed kind; otherwise ANY match.
            if (grant.hostTypeTags != null && grant.hostTypeTags.Count > 0)
            {
                bool match = false;
                if (hostTags != null)
                    foreach (UnitTypeTag needed in grant.hostTypeTags)
                        if (hostTags.Contains(needed)) { match = true; break; }
                if (!match)
                {
                    reason = $"{equipment.displayName} doesn't fit this unit.";
                    return false;
                }
            }

            if (slotTaken)
            {
                reason = "This already has equipment attached.";
                return false;
            }

            if (owner == null || !owner.CanSpendActionPoints(equipment.apCost))
            {
                reason = $"Not enough action points to attach {equipment.displayName}.";
                return false;
            }
            if (equipment.resourceCost != null && !equipment.resourceCost.CanAfford(owner))
            {
                reason = $"Not enough resources to attach {equipment.displayName}.";
                return false;
            }
            return true;
        }

        // --- attach ----------------------------------------------------------------------

        public static bool TryAttach(CardDefinition equipment, UnitData target, PlayerRoot owner, out string reason)
        {
            if (!CanAttach(equipment, target, owner, out reason))
                return false;
            PayCost(equipment, owner);
            Apply(equipment.equipment, target);
            target.Equipment = equipment;
            return true;
        }

        public static bool TryAttach(CardDefinition equipment, CardData targetCard, PlayerRoot owner, out string reason)
        {
            if (!CanAttach(equipment, targetCard, owner, out reason))
                return false;
            PayCost(equipment, owner);
            // Not applied now — the grant is stashed on the card and applied to the spawned
            // UnitData by ArmyActions.DeployUnitFromCard when this card is finally played.
            targetCard.Equipment = equipment;
            return true;
        }

        private static void PayCost(CardDefinition equipment, PlayerRoot owner)
        {
            owner.SpendActionPoints(equipment.apCost);
            equipment.resourceCost?.PayFrom(owner);
        }

        // --- effect application --------------------------------------------------------------
        // Also called by ArmyActions.DeployUnitFromCard for equipment that was attached to the
        // card while it was still in hand. Application order matches EquipmentGrant's own doc:
        // clear families -> remove exact -> add -> additive stats -> override stats.
        public static void Apply(EquipmentGrant grant, UnitData unit)
        {
            if (grant == null || unit == null)
                return;

            if (grant.clearAbilityFamilies != null)
                foreach (AbilityFamily family in grant.clearAbilityFamilies)
                {
                    if (family == AbilityFamily.None)
                        continue;
                    unit.Abilities.RemoveWhere(a => IsFamilyMember(a, family));
                }

            if (grant.removeAbilities != null)
                foreach (string tag in grant.removeAbilities)
                    if (!string.IsNullOrEmpty(tag))
                        unit.Abilities.Remove(tag);

            if (grant.addAbilities != null)
                foreach (string tag in grant.addAbilities)
                    if (!string.IsNullOrEmpty(tag))
                        unit.Abilities.Add(tag);

            if (grant.statChanges != null)
            {
                foreach (EquipmentStatChange change in grant.statChanges)
                    if (change != null && !change.isOverride)
                        ApplyStat(unit, change);
                foreach (EquipmentStatChange change in grant.statChanges)
                    if (change != null && change.isOverride)
                        ApplyStat(unit, change);
            }

            // Same parity ArmyActions/HexSelectionController.SpawnUnit enforce: an added
            // RapidReaction zeroes the activation cost outright.
            if (unit.Abilities.Contains(UnitAbilities.RapidReaction))
                unit.ActivationApCost = 0;
        }

        private static void ApplyStat(UnitData unit, EquipmentStatChange change)
        {
            switch (change.stat)
            {
                case EquipmentStat.Attack:
                    unit.Attack = Combine(unit.Attack, change, 0);
                    break;
                case EquipmentStat.Defense:
                    unit.Defense = Combine(unit.Defense, change, 1);
                    break;
                case EquipmentStat.Resistance:
                    unit.Resistance = Combine(unit.Resistance, change, 0);
                    break;
                case EquipmentStat.Range:
                    unit.Range = Combine(unit.Range, change, 1);
                    break;
                case EquipmentStat.Initiative:
                    unit.Initiative = Combine(unit.Initiative, change, 1);
                    break;
                case EquipmentStat.ActivationApCost:
                    unit.ActivationApCost = Combine(unit.ActivationApCost, change, 0);
                    break;
                case EquipmentStat.CommandRating:
                    unit.CommandRating = Combine(unit.CommandRating, change, 0);
                    break;
                case EquipmentStat.HitPoints:
                {
                    int newMax = Combine(unit.HitPointsMax, change, 1);
                    // A permanent buff raises current HP with max; an override that lowers max
                    // clamps current down to it, but never heals a wounded unit past what it had.
                    int delta = newMax - unit.HitPointsMax;
                    unit.HitPointsMax = newMax;
                    unit.HitPointsCurrent = Mathf.Clamp(unit.HitPointsCurrent + Mathf.Max(0, delta), 1, newMax);
                    break;
                }
                case EquipmentStat.MoveMax:
                {
                    int newMove = Combine(unit.MoveMax, change, 1);
                    int delta = newMove - unit.MoveMax;
                    unit.MoveMax = newMove;
                    unit.MoveCurrent = Mathf.Clamp(unit.MoveCurrent + Mathf.Max(0, delta), 0, newMove);
                    break;
                }
                case EquipmentStat.Fate:
                {
                    int newFateMax = Combine(unit.FateMax, change, 0);
                    int delta = newFateMax - unit.FateMax;
                    unit.FateMax = newFateMax;
                    unit.Fate = Mathf.Clamp(unit.Fate + Mathf.Max(0, delta), 0, newFateMax);
                    break;
                }
            }
        }

        // isOverride: set to `amount` (floored). Otherwise: add `amount` to `current` (floored).
        private static int Combine(int current, EquipmentStatChange change, int floor)
        {
            int result = change.isOverride ? change.amount : current + change.amount;
            return Mathf.Max(floor, result);
        }
    }
}
