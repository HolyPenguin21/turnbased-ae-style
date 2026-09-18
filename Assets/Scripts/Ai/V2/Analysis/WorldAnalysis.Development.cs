using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Development — BuildDevelopment.
    // File-split (mechanical, no behaviour change) from WorldAnalysis.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 5. Still exactly the WorldAnalysis
    // class; only this snapshot family's slice moved to its own file.
    public static partial class WorldAnalysis
    {
        private static readonly ResearchProductionMode[] DevModes =
            { ResearchProductionMode.Research, ResearchProductionMode.Production };

        // The ONE Research/Production capability detect. Snapshot-pure: enumerates own facilities
        // (+ the qualifying hero), then every catalog card that passes facility ability + hero +
        // CanAffordCard. Success chance remains a soft EV/ranking input. The enemy-on-hex rule is recorded
        // as DevelopmentFacility.Contested but is NOT applied here — a contested facility still
        // produces offerings for the analyzer/radar; Phase A alone skips execution while contested.
        private static DevelopmentReadiness BuildDevelopment(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
        {
            var rd = new DevelopmentReadiness();
            var facilities = new List<DevelopmentFacility>();
            var offerings = new List<DevelopmentOffering>();
            rd.Facilities = facilities;
            rd.Offerings = offerings;
            if (player == null || root == null)
                return rd;

            int targets = 0;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
            {
                if (a == null || a.IsPrison) continue;
                foreach (UnitData m in a.Members)
                    // Equipment recipient enumeration includes Heroes, but never prisoners.
                    // This coarse readiness count must reflect the same eligible card classes.
                    if (m != null && !m.IsPrisoner) targets++;
            }
            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                    if (c?.Definition != null && (c.Definition.cardType == CardType.Unit
                        || c.Definition.cardType == CardType.Hero)) targets++;
            rd.UpgradeTargetCount = targets;

            ResearchProductionCatalog catalog = ctx?.ResearchProductionCatalog;

            foreach (BuildingData b in BuildingRegistry.AllBuildings())
            {
                if (b == null || b.Owner != player) continue;
                foreach (ResearchProductionMode mode in DevModes)
                {
                    if (!b.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode)))
                        continue;
                    UnitData hero = ResearchProductionSystem.FindActor(player, b.Hex, mode);
                    facilities.Add(new DevelopmentFacility
                    {
                        Hex = b.Hex,
                        Mode = mode,
                        HasHero = hero != null,
                        Contested = BattleInitiator.FindEnemyAt(b.Hex, player) != null,
                        HeroFate = hero != null ? Mathf.Max(0, hero.Fate) : 0,
                        HeroCommandRating = hero != null ? Mathf.Max(0, hero.CommandRating) : 0,
                    });
                    // Offering enumeration is owned by GenerationSource below. This loop
                    // records readiness only, so Analysis does not keep a second copy of source
                    // eligibility/card/operator logic.
                }
            }

            if (catalog != null)
                foreach (GenerationStep g in GenerationSource.Enumerate(
                    player, root, ctx, hand, null, null, includeContested: true))
                {
                    var stake = new ResourceBundle();
                    ResourceCost cost = g.CardDef?.resourceCost;
                    if (cost != null)
                        foreach (ResourceType t in ResourceBundle.All)
                            stake.Add(t, cost.Get(t));
                    offerings.Add(new DevelopmentOffering
                    {
                        FacilityHex = g.FacilityHex,
                        Mode = g.Mode,
                        Card = g.CardDef,
                        SuccessChance = g.SuccessChance,
                        ProducesEquipment = g.ProducesEquipment,
                        StakeCost = stake,
                        Generation = g,
                    });
                }

            bool facilityCardInHand = false, researcherCardInHand = false, assemblerCardInHand = false;
            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                {
                    CardDefinition d = c?.Definition;
                    if (d == null)
                        continue;
                    // Preparation and card deployment already use the canonical effective
                    // ability projection. The readiness snapshot must not miss a qualified Hero
                    // whose Researcher/Assembler ability comes from attached Equipment.
                    IReadOnlyList<string> abilities = d.cardType == CardType.Hero
                        ? MaterializationChainMatching.EffectiveAbilities(d, c.Equipment)
                        : d.grantedAbilities;
                    if (abilities == null)
                        continue;
                    if (d.cardType == CardType.Facility
                        && (abilities.Contains(UnitAbilities.Research)
                            || abilities.Contains(UnitAbilities.Production)))
                        facilityCardInHand = true;
                    if (d.cardType == CardType.Hero)
                    {
                        if (abilities.Contains(UnitAbilities.Researcher)) researcherCardInHand = true;
                        if (abilities.Contains(UnitAbilities.Assembler)) assemblerCardInHand = true;
                    }
                }

            rd.AnyFacilityWithHero = facilities.Any(f => f.HasHero);
            rd.AnyOperatorlessFacility = facilities.Any(f => !f.HasHero && !f.Contested);
            rd.ResearcherCardInHand = researcherCardInHand;
            rd.AssemblerCardInHand = assemblerCardInHand;
            rd.DevPathViable = rd.AnyFacilityWithHero || facilities.Count > 0 || facilityCardInHand;
            rd.BestSuccessChance = offerings.Count > 0 ? offerings.Max(o => o.SuccessChance) : 0f;

            // Operational generation is priced from the concrete chain by StrategicCardEvaluator /
            // StrategicSpendability. Do not reintroduce a global weakest-resource multiplier here.
            // Keeping this explicit makes every runtime snapshot neutral even while the legacy
            // diagnostic field remains on DevelopmentReadiness for old tests/snapshots.
            rd.ProductionSupport = 1f;

            float investmentSurplus = SurplusFraction(player, root, ctx);
            bool hasExecutableOffering = offerings.Any(o => facilities.Any(f =>
                f.Mode == o.Mode && f.Hex.Equals(o.FacilityHex) && f.HasHero && !f.Contested));
            rd.SurplusFraction = DevelopmentRadarSurplus(investmentSurplus, hasExecutableOffering);
            return rd;
        }

        // READY generation options have already passed ResearchProductionSystem affordability and
        // StrategicSpendability for every resource they actually consume in GenerationSource.
        // Applying the four-resource investment minimum again would make an unrelated empty
        // resource (for example Tech on an Energy+Materials Equipment) suppress legal production.
        // Without an executable ready offering, keep the coarse all-resource signal for the
        // infrastructure/latent-investment lane exactly as before.
        internal static float DevelopmentRadarSurplus(float investmentSurplus, bool hasExecutableOffering)
            => hasExecutableOffering ? 1f : Mathf.Clamp01(investmentSurplus);

        // Coarse, four-resource readiness for the RADAR / infrastructure-investment context,
        // not a gate for an individual Equipment chain. Its resource pool must come from the
        // SAME StrategicSpendability owner as materialization feasibility; do not recalculate
        // reservation floors in Analysis. Operational production must be priced separately using
        // the exact chain's nonzero ResourceCost types.
        private static float SurplusFraction(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            if (player == null || root == null)
                return 0f;
            float worst = 1f;
            foreach (ResourceType t in ResourceBundle.All)
            {
                float spendable = Mathf.Max(0f,
                    StrategicSpendability.SpendableAmount(player, root, ctx, t));
                float income = Mathf.Max(1f, IncomeProjection.IncomeFor(player, t, ctx?.Map));
                worst = Mathf.Min(worst, Mathf.Clamp01(spendable / (income * 2f)));
            }
            return worst;
        }

    }
}
