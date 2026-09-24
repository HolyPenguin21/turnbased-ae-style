using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
    //  This class owns SOURCE validity only: the investment window (DevelopmentInvestmentGate),
    //  gameplay eligibility, exact-combination retry guard and reservation-aware affordability.
    //  Success probability is a soft value input, never a source-validity gate. Once the window is
    //  open, every consumer still prices the complete MaterializationPlan through the one card
    //  scorer.
    //
    //  This is NOT a separate generation manager: it only exposes options. StrategicManager
    //  decides whether generating anything is worth it, compares generation chains against direct
    //  / equipment chains as complete plans, and owns execution.
    // ===========================================================================================
    public static class GenerationSource
    {
        private static readonly ResearchProductionMode[] Modes =
            { ResearchProductionMode.Research, ResearchProductionMode.Production };

        // Generator retry identity belongs here, not to the hero's current army or hex. An army
        // transfer/reorder can happen between bounded mid-turn passes, but it cannot reset the
        // gameplay attempt identity (hero, mode, authored card). Weak keys avoid retaining dead
        // units across battles; the monotonic id keeps distinct identical-name heroes distinct.
        private sealed class HeroIdentity
        {
            public readonly long Id;
            public HeroIdentity(long id) { Id = id; }
        }
        private static readonly ConditionalWeakTable<UnitData, HeroIdentity> HeroIdentities =
            new ConditionalWeakTable<UnitData, HeroIdentity>();
        private static long _nextHeroIdentity;

        // Every (hero-on-Facility, offered card) combination usable RIGHT NOW, in deterministic
        // order. `triedCardKeys` is the actual retry guard: gameplay defines the spent attempt as
        // (hero, mode, authored card key), not "this hero may only Challenge once".
        // Every executable caller (materialization chains, generated non-combat plays, Development
        // upgrades, operator minting) receives only cards whose OWN Challenge cost is inside
        // DevelopmentInvestmentGate's window: Research/Production is a late resource sink and never
        // competes with the main deck, and a resource the card does not consume never blocks it.
        // `analysisView` is used only by Analysis to DESCRIBE readiness in its snapshot: it keeps
        // temporarily contested facilities and ignores the investment window.
        public static List<GenerationStep> Enumerate(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiHandData hand, ISet<string> claimedUseKeys, ISet<string> triedCardKeys,
            bool analysisView = false)
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
                    if (!analysisView
                        && !ResearchProductionSystem.IsEligible(player, b.Hex, mode, out _))
                        continue;

                    List<UnitData> actors = ResearchProductionSystem.FindActors(player, b.Hex, mode);
                    foreach (UnitData hero in actors)
                    {
                        // FacilityHex on GenerationStep records location; it must NOT enter the
                        // retry/portfolio key and enable a second identical Challenge after a move.
                        string useKey = StableGeneratorUseKey(mode, hero);

                        foreach (CardDefinition card in ResearchProductionSystem
                            .OfferedCards(ctx.ResearchProductionCatalog, mode, player.Faction)
                            .Where(card => card != null && !string.IsNullOrWhiteSpace(card.authoredKey))
                            .OrderBy(card => card.authoredKey, System.StringComparer.Ordinal))
                        {
                            string cardKey = useKey + "|" + card.authoredKey;
                            if (triedCardKeys != null && triedCardKeys.Contains(cardKey))
                                continue;
                            if (!ResearchProductionSystem.CanAffordCard(root, card))
                                continue;
                            if (!analysisView && !DevelopmentInvestmentGate.IsOpenFor(
                                    player, ctx.TurnNumber, card.resourceCost))
                                continue;
                            if (!FitsReservedAffordability(root, player, ctx, card))
                                continue;

                            result.Add(new GenerationStep
                            {
                                Mode = mode,
                                FacilityHex = b.Hex,
                                Hero = hero,
                                CardDef = card,
                                SuccessChance = ResearchProductionSystem.EstimateSuccessChance(hero, card),
                                ProducesEquipment = card.cardType == CardType.Equipment,
                                UseKey = useKey,
                                CardKey = cardKey,
                            });
                        }
                    }
                }
            }
            return result;
        }

        // Identity is tied to this UnitData instance rather than its army/member ordinal. The
        // same hero remains the same generator after extraction, transfer or regrouping.
        public static string StableHeroKey(UnitData hero)
        {
            if (hero == null)
                return "?";
            return HeroIdentities.GetValue(hero,
                _ => new HeroIdentity(Interlocked.Increment(ref _nextHeroIdentity))).Id.ToString();
        }

        internal static string StableGeneratorUseKey(ResearchProductionMode mode, UnitData hero) =>
            $"{mode}:{StableHeroKey(hero)}";

        // Source enumeration must use the SAME owner-aware spendability authority as
        // MaterializationFeasibility and WorldAnalysis.Development. Duplicating the ledger
        // intersection here made the legality and reservation rules drift independently.
        // Only the actually consumed resource types are checked by the canonical helper.
        internal static bool FitsReservedAffordability(PlayerRoot root, PlayerSetupData player,
            AiTurnContext ctx, CardDefinition card)
        {
            if (root == null || card == null)
                return false;
            return StrategicSpendability.FitsSpendableResources(
                player, root, ctx, card.resourceCost);
        }
    }
}
