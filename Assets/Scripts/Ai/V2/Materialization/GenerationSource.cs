using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

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
    //  This class owns SOURCE validity only: gameplay eligibility, exact-combination retry guard,
    //  reservation-aware affordability, and the existing AiConfig.developmentMinSuccessChance
    //  quality floor. It deliberately does NOT apply V1 AiDevelopmentPlanner's
    //  developmentMinResourceKeep investment policy. Phase A generation may be a necessary way to
    //  satisfy another axis's hard demand; Phase B applies its own surplus reserve policy when the
    //  complete MaterializationPlan is evaluated.
    //
    //  This is NOT a separate generation manager: it only exposes options. StrategicManager
    //  decides whether generating anything is worth it, compares generation chains against direct
    //  / equipment chains as complete plans, and owns execution.
    // ===========================================================================================
    public static class GenerationSource
    {
        private static readonly ResearchProductionMode[] Modes =
            { ResearchProductionMode.Research, ResearchProductionMode.Production };

        // Every (hero-on-Facility, offered card) combination usable RIGHT NOW, in deterministic
        // order. `triedCardKeys` is the actual retry guard: gameplay/V1 defines the spent attempt as
        // (hero, mode, card), not "this hero may only Challenge once". `claimedUseKeys` remains in
        // the signature for the Step-8B reservation contract but is intentionally NOT a feasibility
        // gate; the shared maxGenerationActionsPerTurn bound is the AI-wide attempt limiter.
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
                        if (!FitsReservedAffordability(root, player, ctx, card))
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

        // Source-level resource gate: do not offer a card whose cost would consume resources
        // already reserved elsewhere. AI-MGR-02 §P1.5 — the canonical "spendable" is the SAME one
        // the end-of-turn tempo arbiter uses: the strategic reservation ledger AND the legacy
        // recon-air reservation, whichever is tighter — so planning affordability == execution
        // affordability. No arbitrary post-spend minimum is imposed here.
        private static bool FitsReservedAffordability(PlayerRoot root, PlayerSetupData player,
            AiTurnContext ctx, CardDefinition card)
        {
            ResourceCost cost = card.resourceCost;
            if (cost == null)
                return true;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int need = cost.Get(t);
                if (need <= 0)
                    continue;
                float legacy = AiResourceReservation.Available(root, player, t);
                float strategic = ctx != null
                    ? StrategicResourceReservationLedger.Spendable(
                        player, ctx.TurnNumber, StrategicResourceReservationLedger.Map(t), root.GetResource(t))
                    : float.MaxValue;
                if (Mathf.Min(legacy, strategic) < need)
                    return false;
            }
            return true;
        }
    }
}
