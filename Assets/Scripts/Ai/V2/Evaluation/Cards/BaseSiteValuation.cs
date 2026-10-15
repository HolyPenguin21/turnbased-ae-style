using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // Base card site facts (marginal income, global effect, airfield coverage), split from
    // StrategicCardEvaluator. DemandLayer builds the one Economy TaskScore from them.
    internal static class BaseSiteValuation
    {
        // Base card facts only. DemandLayer builds the sole TaskScore; this method does NOT
        // calculate a second, bespoke strategic value or an imaginary extraction loss.
        internal readonly struct BaseSiteValue
        {
            internal readonly float HexYield;
            internal readonly float GlobalEffect;
            internal readonly float Airfield;

            internal BaseSiteValue(float hexYield, float globalEffect, float airfield)
            {
                HexYield = hexYield;
                GlobalEffect = globalEffect;
                Airfield = airfield;
            }
        }

        internal static BaseSiteValue ScoreBaseSite(WorldSnapshot s, EconomyBaseOpportunity site,
            CardData card) => new BaseSiteValue(
                BaseCardMarginalYield(s, site, card.Definition),
                GlobalEffectValue(s, card.Definition),
                BaseAirfieldValue(s, card.Definition, site.Hex));

        // One owner of the gameplay fact "what resource income can this exact Base card collect on
        // this hex?". This private helper is only the new card's additional Collect capacity;
        // actual OWNER gain below also accounts for existing army collection.
        private static int BaseCardAdditionalCollectCapacity(ResourceBundle yield,
            CardDefinition definition, ResourceType type)
        {
            if (definition?.grantedAbilities == null
                || !definition.grantedAbilities.Contains(UnitAbilities.CollectAbilityFor(type)))
                return 0;
            return Mathf.RoundToInt(Mathf.Max(0f, Mathf.Min(1f, yield.Get(type))));
        }

        // Net OWNER gain, not the gross Base collection. A Base takes the first cut,
        // and can displace our own army's collection without raising the owner's income.
        internal static float BaseCardMarginalYield(WorldSnapshot s, EconomyBaseOpportunity site,
            CardDefinition definition) => ResourceBundle.All.Sum(type =>
                BaseCardMarginalGain(s, site, definition, type));

        internal static float BaseCardMarginalGain(WorldSnapshot s, EconomyBaseOpportunity site,
            CardDefinition definition, ResourceType type)
        {
            int addedCapacity = BaseCardAdditionalCollectCapacity(site.HexYield, definition, type);
            if (addedCapacity <= 0)
                return 0f;
            int remainingYield = Mathf.RoundToInt(site.HexYield.Get(type));
            int ownArmyCollectors = Mathf.RoundToInt((s?.Self?.Armies
                ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.Hex.Equals(site.Hex))
                .Sum(a => a.CollectionCapacity.Get(type)));
            bool armiesCanCollect = !(s?.Known?.EnemySightings
                ?? System.Array.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>())
                .Any(enemy => enemy.Hex.Equals(site.Hex));
            // BaseUncollectedYield has already subtracted existing building collection:
            // work on the remainder to avoid charging carried-over facilities twice.
            return IncomeProjection.MarginalOwnerCollectionAtHex(
                remainingYield, 0, addedCapacity, ownArmyCollectors, armiesCanCollect);
        }

        // The ONE Economy-side value of a card's PlayerGlobal effects, in TaskScore units: the
        // FoundBase site score and the global-source demand (DemandLayer.Economy) both read it.
        internal static float GlobalEffectValue(WorldSnapshot s, CardDefinition definition)
        {
            if (definition?.grantedAbilities == null)
                return 0f;
            EffectContribution contribution = StrategicEffectRegistry.Contributions(
                IntendedRole.Economy, definition.grantedAbilities, 0,
                new EffectEvaluationContext(s));
            return contribution.GlobalRoleFit + contribution.GlobalImmediateTempo
                + contribution.GlobalThreatResponse + contribution.GlobalCapabilityGap
                + contribution.GlobalForceGrowth + contribution.GlobalSynergy;
        }

        private static float BaseAirfieldValue(WorldSnapshot s, CardDefinition definition,
            HexCoord target)
        {
            if (definition == null || definition.airfieldCapacity <= 0 || s?.Self == null)
                return 0f;
            bool aviationRelevant = (s.Self.Hand ?? System.Array.Empty<CardData>())
                    .Any(c => c?.Definition?.isAviation == true)
                || (s.Self.Armies ?? System.Array.Empty<ArmySnapshot>()).Any(a => a != null && a.IsAir);
            if (!aviationRelevant)
                return 0f;
            List<ArmySnapshot> airfields = (s.Self.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.IsAirfield).ToList();
            if (airfields.Count == 0)
                return 1f;
            int distance = airfields.Min(a => HexGridMath.Distance(a.Hex, target));
            return Mathf.Clamp01(distance / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius));
        }
    }
}
