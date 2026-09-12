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
                    if (m != null && !m.IsHero) targets++;
            }
            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                    if (c?.Definition != null && c.Definition.cardType == CardType.Unit) targets++;
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
                    if (d == null || d.grantedAbilities == null)
                        continue;
                    if (d.cardType == CardType.Facility
                        && (d.grantedAbilities.Contains(UnitAbilities.Research)
                            || d.grantedAbilities.Contains(UnitAbilities.Production)))
                        facilityCardInHand = true;
                    if (d.cardType == CardType.Hero)
                    {
                        if (d.grantedAbilities.Contains(UnitAbilities.Researcher)) researcherCardInHand = true;
                        if (d.grantedAbilities.Contains(UnitAbilities.Assembler)) assemblerCardInHand = true;
                    }
                }

            rd.AnyFacilityWithHero = facilities.Any(f => f.HasHero);
            rd.AnyOperatorlessFacility = facilities.Any(f => !f.HasHero && !f.Contested);
            rd.ResearcherCardInHand = researcherCardInHand;
            rd.AssemblerCardInHand = assemblerCardInHand;
            rd.DevPathViable = rd.AnyFacilityWithHero || facilities.Count > 0 || facilityCardInHand;
            rd.BestSuccessChance = offerings.Count > 0 ? offerings.Max(o => o.SuccessChance) : 0f;
            rd.SurplusFraction = SurplusFraction(player, root, ctx);
            return rd;
        }

        // [0..1] proxy for "am I spending surplus, not resources I need". Per resource type:
        // spendable(t) (the tighter of the legacy + strategic reservation floors) over two turns of
        // income; the WORST type governs. First pass — the analyzer's A_total (opportunity cost vs
        // playing a card) is the real gate; this is the radar's coarse appetite signal. Tune later.
        private static float SurplusFraction(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            if (player == null || root == null)
                return 0f;
            float worst = 1f;
            foreach (ResourceType t in ResourceBundle.All)
            {
                float legacy = AiResourceReservation.Available(root, player, t);
                float strategic = ctx != null
                    ? StrategicResourceReservationLedger.Spendable(player, ctx.TurnNumber,
                        StrategicResourceReservationLedger.Map(t), root.GetResource(t))
                    : float.MaxValue;
                float spendable = Mathf.Max(0f, Mathf.Min(legacy, strategic));
                float income = Mathf.Max(1f, IncomeProjection.IncomeFor(player, t, ctx?.Map));
                worst = Mathf.Min(worst, Mathf.Clamp01(spendable / (income * 2f)));
            }
            return worst;
        }

    }
}
