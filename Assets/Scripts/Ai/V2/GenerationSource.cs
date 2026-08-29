using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  GENERATION SOURCE  (Strategy V2 — Strategic Manager, Step 8B)
    // ===========================================================================================
    //  Enumerates the IMMEDIATELY usable Research/Production generation options — ONE mechanism,
    //  both modes (Research ~ Lab, Production ~ Factory). A generation step is a candidate ONLY
    //  when a qualifying non-prisoner Researcher/Assembler Hero ALREADY stands on an own Facility
    //  hex this turn. Step 8B adds NO hero positioning and NO multi-turn planning: "MoveArmy ->
    //  Facility -> Generate" is out of scope.
    //
    //  Feasibility is gated exactly the way V1 AiDevelopmentPlanner gates it — the SAME
    //  AiConfig.developmentMinSuccessChance threshold (no second one), the same reservation-aware
    //  resource-surplus rule — minus any (hero) use already claimed by an earlier selected chain
    //  this pass and any (hero, card) pair already attempted this pass.
    //
    //  This is NOT a separate generation manager: it only exposes options. StrategicManager
    //  decides whether generating anything is worth it, compares generation chains against direct
    //  / equipment chains as complete plans, and owns execution.
    // ===========================================================================================
    public static class GenerationSource
    {
        private static readonly ResearchProductionMode[] Modes =
            { ResearchProductionMode.Research, ResearchProductionMode.Production };

        // Every (hero-on-Facility, offered card) pair usable RIGHT NOW, in a deterministic order
        // (Facility hex, then mode, then card display name). `claimedUseKeys` / `triedCardKeys`
        // are the pass-local exclusion sets owned by MaterializationReservation.
        public static List<GenerationStep> Enumerate(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiHandData hand, ISet<string> claimedUseKeys, ISet<string> triedCardKeys)
        {
            var result = new List<GenerationStep>();
            if (player == null || root == null || ctx?.ResearchProductionCatalog == null || hand == null)
                return result;

            List<BuildingData> ownBuildings = BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player)
                .OrderBy(b => b.Hex.Q).ThenBy(b => b.Hex.R)
                .ToList();

            foreach (BuildingData b in ownBuildings)
            {
                foreach (ResearchProductionMode mode in Modes)
                {
                    if (!b.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode)))
                        continue;
                    if (!ResearchProductionSystem.IsEligible(player, b.Hex, mode, out _))
                        continue;
                    UnitData hero = ResearchProductionSystem.FindActor(player, b.Hex, mode);
                    if (hero == null)
                        continue;

                    string useKey = $"{mode}:{b.Hex.Q},{b.Hex.R}:{StableHeroKey(hero)}";
                    if (claimedUseKeys != null && claimedUseKeys.Contains(useKey))
                        continue;

                    foreach (CardDefinition card in ResearchProductionSystem
                        .OfferedCards(ctx.ResearchProductionCatalog, mode, player.Faction)
                        .Where(c => c != null)
                        .OrderBy(c => c.displayName, System.StringComparer.Ordinal))
                    {
                        string cardKey = useKey + "|" + card.displayName;
                        if (triedCardKeys != null && triedCardKeys.Contains(cardKey))
                            continue;
                        if (!ResearchProductionSystem.CanAffordCard(root, card))
                            continue;
                        if (!FitsReservationSurplus(root, player, card))
                            continue;
                        float chance = ResearchProductionSystem.EstimateSuccessChance(hero, card);
                        if (chance < AiConfig.developmentMinSuccessChance)
                            continue;

                        result.Add(new GenerationStep
                        {
                            Mode = mode,
                            FacilityHex = b.Hex,
                            Hero = hero,
                            CardDef = card,
                            SuccessChance = chance,
                            ProducesEquipment = card.cardType == CardType.Equipment,
                            UseKey = useKey,
                            CardKey = cardKey,
                        });
                    }
                }
            }
            return result;
        }

        // Stable within a turn — a hero object is identity-stable, and hero names are unique in
        // practice. The hash fallback keeps the key well-formed for an unnamed unit.
        public static string StableHeroKey(UnitData hero) =>
            hero == null ? "?" : (!string.IsNullOrEmpty(hero.Name) ? hero.Name : hero.GetHashCode().ToString());

        // spec §9 parity with AiDevelopmentPlanner.FitsResourceSurplus — the reservation-aware
        // free surplus of every resource type the card needs must stay at/above
        // AiConfig.developmentMinResourceKeep after paying.
        private static bool FitsReservationSurplus(PlayerRoot root, PlayerSetupData player, CardDefinition card)
        {
            ResourceCost cost = card.resourceCost;
            if (cost == null)
                return true;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int need = cost.Get(t);
                if (need <= 0)
                    continue;
                if (AiResourceReservation.Available(root, player, t) - need < AiConfig.developmentMinResourceKeep)
                    return false;
            }
            return true;
        }
    }
}
