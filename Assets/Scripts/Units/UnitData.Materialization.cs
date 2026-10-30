using System.Collections.Generic;
using Game.Cards;
using Game.Players;
using UnityEngine;
namespace Game.Units
{
    public partial class UnitData
    {
        // Scripted materialization has no payment or hand side effects. Play commands add
        // ownership, membership, target and debit around this same factory.
        public static UnitData FromDefinition(CardDefinition definition, PlayerSetupData owner,
            CardDefinition equipment = null, CardDefinition mutator = null)
        {
            if (definition == null || owner == null) return null;
            var unit = Materialize(definition.displayName, owner, definition.moveMax, definition.activationApCost,
                definition.cardType == CardType.Hero, definition.commandRating, definition.art,
                definition.grantedAbilities, definition.attack, definition.range, definition.hitPoints,
                definition.initiative, definition.fate, definition.defenseRating, definition.unitTypeTags,
                definition.detailArt, definition.apCost, definition.resourceCost, definition.isAviation,
                definition.launchEnergyCost, definition.turnsWithoutRefuel, definition.antiAirRadius, definition);
            EquipmentSystem.ApplyAttachments(unit, equipment, mutator);
            return unit;
        }
        public static UnitData Materialize(string unitName, PlayerSetupData owner, int moveMax, int activationApCost, bool isHero, int commandRating, Sprite art, IEnumerable<string> grantedAbilities = null, int attack = 0, int range = 1, int hitPoints = 1, int initiative = 1, int fate = 0, int defense = 1, IEnumerable<UnitTypeTag> typeTags = null, Sprite detailArt = null, int apCost = 0, ResourceCost resourceCost = null, bool isAviation = false, int launchEnergyCost = 0, int turnsWithoutRefuel = 0, int antiAirRadius = 1, CardDefinition sourceDefinition = null)
        {
            if (owner == null)
                return null;

            var data = new UnitData
            {
                Name = unitName, Owner = owner,
                MoveMax = moveMax, MoveCurrent = moveMax,
                ActivationApCost = activationApCost,
                IsHero = isHero, CommandRating = commandRating,
                Art = art, DetailArt = detailArt != null ? detailArt : art,
                Attack = attack, Defense = defense, Range = range, HitPointsMax = hitPoints, HitPointsCurrent = hitPoints,
                Initiative = initiative, Fate = fate, FateMax = fate,
                ApCost = apCost, OriginalResourceCost = resourceCost,
                IsAviation = isAviation,
                LaunchEnergyCost = launchEnergyCost,
                TurnsWithoutRefuel = Mathf.Max(0, turnsWithoutRefuel),
                AntiAirRadius = Mathf.Max(1, antiAirRadius),
                AirfieldCapacity = !isAviation && !isHero ? Mathf.Max(0, sourceDefinition?.airfieldCapacity ?? 0) : 0,
                OriginatingCard = sourceDefinition,
            };
            if (grantedAbilities != null)
                foreach (string ability in grantedAbilities)
                    data.Abilities.Add(ability);
            if (typeTags != null)
                foreach (UnitTypeTag tag in typeTags)
                    data.TypeTags.Add(tag);
            if (data.Abilities.Contains(UnitAbilities.RapidReaction))
                data.ActivationApCost = 0;
            UnitRepair.InitializeRepairCost(data);
            EquipmentSystem.InitializeBaseline(data);
            return data;
        }

    }
}
