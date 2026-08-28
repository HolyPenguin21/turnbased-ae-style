using System;
using System.Collections.Generic;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Cards
{
    // The single source of Research/Production RULES (spec P0 §4: "выделить общую Research/
    // Production gameplay-систему, которую одинаково используют человеческий UI и AI"). Everything
    // that used to be inlined in HexSelectionController's own R/P transaction lives here now:
    // eligibility, the exact acting Hero, the still-qualifies re-check, ResourceCost affordability
    // + payment, the Research Stealth reveal, the Challenge dice rules, a headless roll, and
    // minting the produced CardData.
    //
    // The human path (HexSelectionController + the animated Spend/Accept loop in
    // BattleAttackPopupUI) and the AI path (AiDevelopmentPlanner) both call in here. Presentation
    // — the popup's dice animation and the manual Fate button — stays in BattleAttackPopupUI, but
    // it now reads its pool size / required-successes / spend-eligibility from HERE so there is
    // exactly one rule set. Ground Combat, Capture and Aviation are untouched (spec §4).
    //
    // A plain static class, no MonoBehaviour and no scene wiring — same shape as EquipmentSystem.
    public static class ResearchProductionSystem
    {
        // ---- mode → ability tags -----------------------------------------------------------
        // The Facility ability an owned building must carry, and the role ability the acting
        // Hero must carry, for each mode. Research ⇒ (Research, Researcher); Production ⇒
        // (Production, Assembler) — the symmetric pair the data contract already established (see
        // UnitAbilities' own Research/Production comment).
        public static string FacilityAbility(ResearchProductionMode mode)
            => mode == ResearchProductionMode.Research ? UnitAbilities.Research : UnitAbilities.Production;

        public static string RoleAbility(ResearchProductionMode mode)
            => mode == ResearchProductionMode.Research ? UnitAbilities.Researcher : UnitAbilities.Assembler;

        // ---- Challenge rules (shared with BattleAttackPopupUI's interactive loop) ----------

        // The producing Hero always rolls this many dice — BattleAttackPopupUI.
        // BeginResearchProduction used a bare literal 5 in two places before this.
        public const int DicePoolSize = 5;

        // The card's own CardDefinition.fate, reused as the fixed number of guaranteed defender
        // successes the roll must meet or beat (see CardDefinition.fate's own comment). Never
        // negative; a 0-fate card auto-succeeds.
        public static int RequiredSuccesses(CardDefinition card)
            => Mathf.Max(0, card != null ? card.fate : 0);

        public static int CountHits(bool[] dice)
        {
            int n = 0;
            if (dice != null)
                foreach (bool hit in dice)
                    if (hit)
                        n++;
            return n;
        }

        public static bool HasMiss(bool[] dice)
        {
            if (dice != null)
                foreach (bool hit in dice)
                    if (!hit)
                        return true;
            return false;
        }

        // The one condition under which spending a Fate can still change a Research/Production
        // roll — BattleAttackPopupUI's interactive loop gates its Spend button on exactly this:
        // there is a miss to reroll, OR the target isn't met yet and an overflow die could still
        // help. Fate at 0 → nothing to spend.
        public static bool CanSpendFate(bool[] dice, int fate, int required)
            => fate > 0 && (HasMiss(dice) || CountHits(dice) < required);

        // ---- eligibility -----------------------------------------------------------------

        // The exact Hero that would run the Challenge on `hex` for `player` — first own,
        // non-prisoner Hero carrying the mode's role ability, in ArmyRegistry order then Members
        // order (no picker when several qualify). A hidden Hero still qualifies (Research reveals
        // it as its cost — see ApplyResearchReveal). Prison armies are skipped. This is the same
        // rule HexSelectionController.FindOwnHeroWithAbilityAt used to hold privately.
        public static UnitData FindActor(PlayerSetupData player, HexCoord hex, ResearchProductionMode mode)
        {
            if (player == null)
                return null;
            string role = RoleAbility(mode);
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner != player || army.IsPrison)
                    continue;
                foreach (UnitData member in army.Members)
                    if (member.IsHero && !member.IsPrisoner && member.HasAbility(role))
                        return member;
            }
            return null;
        }

        // Whole-hex eligibility (spec §4: eligibility + hero/Facility check): on `player`'s
        // behalf, an OWN building on `hex` carries the mode's Facility ability, no combat-capable
        // enemy stands on the hex, and at least one qualifying own Hero (FindActor) is present.
        // `player.IsHuman` is deliberately NOT checked here — the AI path calls the same rule;
        // the human call site keeps its own turn/human guard. `reason` is filled on failure for
        // the caller's hint / AI diagnostic log.
        public static bool IsEligible(PlayerSetupData player, HexCoord hex, ResearchProductionMode mode, out string reason)
        {
            reason = null;
            if (player == null)
            {
                reason = "no player";
                return false;
            }
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null || building.Owner != player)
            {
                reason = "no own building on the hex";
                return false;
            }
            if (BattleInitiator.FindEnemyAt(hex, player) != null)
            {
                reason = "an enemy stands on the hex";
                return false;
            }
            if (!building.HasFacilityWithAbility(FacilityAbility(mode)))
            {
                reason = $"the building has no {FacilityAbility(mode)} Facility";
                return false;
            }
            if (FindActor(player, hex, mode) == null)
            {
                reason = $"no own {RoleAbility(mode)} Hero on the hex";
                return false;
            }
            return true;
        }

        // The specific Hero the transaction started with must STILL personally qualify — its
        // army on `hex`, owned by `player`, not a Prison, the member itself a non-prisoner Hero
        // carrying the mode's role ability. Stronger than IsEligible, which only asks whether
        // *any* qualifying Hero is present. (Was HexSelectionController.
        // HeroStillQualifiesForResearchProduction.)
        public static bool ActorStillQualifies(PlayerSetupData player, UnitData hero, HexCoord hex, ResearchProductionMode mode)
        {
            if (hero == null || player == null || hero.Owner != player)
                return false;
            if (!hero.IsHero || hero.IsPrisoner || !hero.HasAbility(RoleAbility(mode)))
                return false;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army.Owner == player && !army.IsPrison && army.Members.Contains(hero))
                    return true;
            return false;
        }

        // ---- catalog / cost ------------------------------------------------------------------

        public static List<CardDefinition> OfferedCards(ResearchProductionCatalog catalog, ResearchProductionMode mode, Faction faction)
            => catalog != null ? catalog.ResolveFor(mode, faction) : new List<CardDefinition>();

        public static bool Offers(ResearchProductionCatalog catalog, ResearchProductionMode mode, Faction faction, CardDefinition card)
            => card != null && OfferedCards(catalog, mode, faction).Contains(card);

        // ResourceCost is the ONLY cost of the attempt (spec §4/§32: no Player AP for the roll
        // itself). A card with no resourceCost is always affordable.
        public static bool CanAffordCard(PlayerRoot root, CardDefinition card)
            => root != null && card != null && (card.resourceCost == null || card.resourceCost.CanAfford(root));

        // Spent immediately before the Challenge and NEVER refunded on a loss (spec §4).
        public static void PayCardCost(PlayerRoot root, CardDefinition card)
        {
            if (root != null && card != null)
                card.resourceCost?.PayFrom(root);
        }

        // ---- consequences -----------------------------------------------------------------

        // Choosing Research reveals the participating Researcher — the cost/consequence of
        // deciding to Research, not of the roll (see HexSelectionController's own long comment on
        // this rule, and spec §12). Only THIS hero loses Stealth; Production never reveals.
        // Never rolled back by a later insufficient-resources / full-hand / lost-Challenge exit.
        public static void ApplyResearchReveal(ResearchProductionMode mode, UnitData hero)
        {
            if (mode == ResearchProductionMode.Research && hero != null)
                StealthSystem.ExitStealth(hero);
        }

        // ---- headless Challenge ---------------------------------------------------------------

        public struct ChallengeOutcome
        {
            public bool Success;
            public int Successes;
            public int Required;
            public int FateSpent;
            public int FateAvailable; // hero.Fate at roll time (never mutated by the roll)
        }

        // Headless equivalent of BattleAttackPopupUI.RunResearchProductionChallenge — same dice
        // pool, same Fate-spend moves (reroll a miss; else, still short of target, append one
        // overflow die), with no UI and no animation. The Hero's real Fate is NOT mutated: the
        // interactive popup restores it verbatim on the way out (RestoreResearchProductionFate),
        // so a headless roll matches simply by never touching it.
        //
        // `maxFateSpend` caps how much of the Hero's pool the roll is willing to burn (default:
        // as much as it takes). The policy only ever spends while still BELOW target — an AI
        // never wastes Fate re-rolling a cosmetic miss once the card is already made.
        public static ChallengeOutcome RollChallenge(UnitData hero, CardDefinition card, int maxFateSpend = int.MaxValue)
        {
            int required = RequiredSuccesses(card);
            int fateAvail = hero != null ? Mathf.Max(0, hero.Fate) : 0;
            int fateBudget = Mathf.Clamp(maxFateSpend, 0, fateAvail);
            int fateSpent = 0;

            bool[] dice = ChallengeResolver.RollDice(DicePoolSize);
            while (fateSpent < fateBudget && CountHits(dice) < required)
            {
                // Same move order as the interactive loop: a miss is always rerolled before any
                // overflow die is appended.
                if (HasMiss(dice))
                {
                    for (int i = 0; i < dice.Length; i++)
                        if (!dice[i])
                        {
                            dice[i] = ChallengeResolver.RollDice(1)[0];
                            break;
                        }
                }
                else
                {
                    var grown = new bool[dice.Length + 1];
                    Array.Copy(dice, grown, dice.Length);
                    grown[dice.Length] = ChallengeResolver.RollDice(1)[0];
                    dice = grown;
                }
                fateSpent++;
            }

            int hits = CountHits(dice);
            return new ChallengeOutcome
            {
                Success = hits >= required,
                Successes = hits,
                Required = required,
                FateSpent = fateSpent,
                FateAvailable = fateAvail,
            };
        }

        // A deterministic estimate of RollChallenge's success probability, for AI card-value
        // scoring (spec §8) — NOT a Monte-Carlo run (the AI arbiter must score the same state the
        // same way every step; UnityEngine.Random would make it jitter). Closed-form
        // approximation: model the Hero's spendable Fate as that many extra 50/50 dice on top of
        // the base pool (each Fate point buys roughly one more attempt at a hit, whether spent as
        // a miss-reroll or an overflow die), then take the binomial tail P(hits >= required).
        // Slightly optimistic near the margin, but monotonic in every input that matters (higher
        // required ⇒ lower, more Fate ⇒ higher), which is all a ranking signal needs.
        public static float EstimateSuccessChance(UnitData hero, CardDefinition card, int maxFateSpend = int.MaxValue)
        {
            int required = RequiredSuccesses(card);
            if (required <= 0)
                return 1f;
            int fateAvail = hero != null ? Mathf.Max(0, hero.Fate) : 0;
            int dice = DicePoolSize + Mathf.Clamp(maxFateSpend, 0, fateAvail);
            return BinomialTailAtLeast(dice, 0.5, required);
        }

        // P(X >= k) for X ~ Binomial(n, p). Iterative pmf, no factorials — n is tiny here (5..~15).
        private static float BinomialTailAtLeast(int n, double p, int k)
        {
            if (k <= 0)
                return 1f;
            if (k > n)
                return 0f;
            double pmf = System.Math.Pow(1.0 - p, n); // P(X = 0)
            double cdfBelowK = 0.0;
            for (int i = 0; i < k; i++)
            {
                cdfBelowK += pmf;
                pmf *= (double)(n - i) / (i + 1) * (p / (1.0 - p));
            }
            return (float)System.Math.Max(0.0, System.Math.Min(1.0, 1.0 - cdfBelowK));
        }

        // The produced instance (spec §4: создание CardData) — flagged ResearchProductionCreated
        // so it is never charged its ResourceCost again and plays at activationApCost (see
        // CardData's own comment). Null card ⇒ null (a lost Challenge mints nothing).
        public static CardData MintCard(CardDefinition card)
            => card != null ? new CardData(card) { ResearchProductionCreated = true } : null;
    }
}
