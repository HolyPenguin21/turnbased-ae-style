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
    // + payment, the Research Stealth reveal, the Challenge tokens rules, a headless roll, and
    // minting the produced CardData.
    //
    // The human path (HexSelectionController + the animated Spend/Accept loop in
    // BattleAttackPopupUI) and the AI path (AiDevelopmentPlanner) both call in here. Presentation
    // — the popup's tokens animation and the manual Fate button — stays in BattleAttackPopupUI, but
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

        // A Facility card that founds a Research or Production site (Laboratory / Factory).
        public static bool IsResearchProductionFacility(CardDefinition def)
            => def != null && def.cardType == CardType.Facility && def.grantedAbilities != null
               && (def.grantedAbilities.Contains(UnitAbilities.Research)
                   || def.grantedAbilities.Contains(UnitAbilities.Production));

        // ---- Challenge rules (shared with BattleAttackPopupUI's interactive loop) ----------

        // Snapshot the hero's full, attachment-adjusted Fate stat before the attempt spends Fate.
        public static int TokenPoolSizeFor(UnitData hero)
            => Mathf.Max(0, hero?.FateMax ?? 0);

        // Misses already occupy places that Fate must cover; only the remaining budget can
        // extend the row. These slots are presentation only, never rolled/countable successes.
        public static int FatePreviewCount(bool[] tokens, int fate)
            => tokens == null ? 0 : Mathf.Max(0, fate - (tokens.Length - CountHits(tokens)));

        // The card's own CardDefinition.fate, reused as the fixed number of guaranteed defender
        // successes the roll must meet or beat (see CardDefinition.fate's own comment). Never
        // negative; a 0-fate card auto-succeeds.
        public static int RequiredSuccesses(CardDefinition card)
            => Mathf.Max(0, card != null ? card.fate : 0);

        public static int CountHits(bool[] tokens)
        {
            int n = 0;
            if (tokens != null)
                foreach (bool hit in tokens)
                    if (hit)
                        n++;
            return n;
        }

        public static bool HasMiss(bool[] tokens)
        {
            if (tokens != null)
                foreach (bool hit in tokens)
                    if (!hit)
                        return true;
            return false;
        }

        // The one condition under which spending a Fate can still change a Research/Production
        // roll — BattleAttackPopupUI's interactive loop gates its Spend button on exactly this:
        // there is a miss to reroll, OR the target isn't met yet and an overflow die could still
        // help. Fate at 0 → nothing to spend.
        public static bool CanSpendFate(bool[] tokens, int fate, int required)
            => fate > 0 && (HasMiss(tokens) || CountHits(tokens) < required);

        // ---- eligibility -----------------------------------------------------------------

        // Every Hero that can run the Challenge. AI enumeration must compare all operators; the
        // gameplay/UI compatibility helper FindActor below remains deterministic and returns the
        // first. Future unit/equipment/ability chance modifiers belong in EstimateSuccessChance,
        // so every caller automatically sees the same probability.
        public static List<UnitData> FindActors(PlayerSetupData player, HexCoord hex,
            ResearchProductionMode mode)
        {
            var result = new List<UnitData>();
            if (player == null)
                return result;
            string role = RoleAbility(mode);
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner != player || army.IsPrison)
                    continue;
                foreach (UnitData member in army.Members)
                    if (member != null && member.IsHero && !member.IsPrisoner && member.HasAbility(role))
                        result.Add(member);
            }
            return result;
        }

        public static UnitData FindActor(PlayerSetupData player, HexCoord hex, ResearchProductionMode mode)
        {
            List<UnitData> actors = FindActors(player, hex, mode);
            return actors.Count > 0 ? actors[0] : null;
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
                reason = "No active player.";
                return false;
            }
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null || building.Owner != player)
            {
                reason = "Requires one of your buildings on this hex.";
                return false;
            }
            if (BattleInitiator.FindEnemyAt(hex, player) != null)
            {
                reason = "An enemy army occupies this hex.";
                return false;
            }
            if (!building.HasFacilityWithAbility(FacilityAbility(mode)))
            {
                reason = $"Requires a {FacilityAbility(mode)} facility here.";
                return false;
            }
            if (FindActor(player, hex, mode) == null)
            {
                reason = $"Move your {RoleAbility(mode)} hero onto this hex.";
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

        public static List<CardDefinition> OfferedCards(ResearchProductionCatalog catalog, ResearchProductionMode mode, PlayerSetupData player)
        {
            var cards = OfferedCards(catalog, mode, player != null ? player.Faction : Faction.None);
            if (player != null && player.IsHuman)
                cards.RemoveAll(c => player.MatchLoadout == null || player.BlueprintQuota?.Available(c.authoredKey) != true);
            else if (player?.AiBlueprints != null)
                cards.RemoveAll(c => !player.AiBlueprints.Contains(c.authoredKey));
            return cards;
        }

        public static bool Offers(ResearchProductionCatalog catalog, ResearchProductionMode mode, Faction faction, CardDefinition card)
            => card != null && OfferedCards(catalog, mode, faction).Contains(card);

        // Starting a Challenge pays the card's authored play cost: apCost plus ResourceCost.
        // This is separate from playing a successfully minted card later, which still pays that
        // instance's activationApCost and never pays ResourceCost twice (see CardData).
        public static int AttemptApCost(CardDefinition card)
            => card != null ? Mathf.Max(0, card.apCost) : 0;

        public static bool CanAffordCard(PlayerRoot root, CardDefinition card)
            => card != null && new PaymentQuote(AttemptApCost(card), card.resourceCost).CanDebit(root, out _);

        // Single irreversible attempt-start transaction shared by the human animated Challenge
        // and AI headless Challenge. Every condition that can reject an attempt is checked before
        // reveal/payment; after true is returned the AP/resources are intentionally spent even if
        // the subsequent Challenge loses. Human Research may already have revealed the hero when
        // its modal opened; ApplyResearchReveal is idempotent, so the same transaction is still
        // safe and keeps AI/human execution on one mutation boundary.
        public static bool TryStartAttempt(PlayerSetupData player, PlayerRoot root, UnitData hero,
            HexCoord hex, ResearchProductionMode mode, CardDefinition card,
            ResearchProductionCatalog catalog, out string reason)
        {
            if (player?.IsHuman == true || player?.MatchLoadout != null)
            { reason = "Start research or production from its window."; return false; }
            return TryStartAttempt(player, root, hero, hex, mode, card, catalog, out reason, out _);
        }

        public static bool TryStartAttempt(PlayerSetupData player, PlayerRoot root, UnitData hero,
            HexCoord hex, ResearchProductionMode mode, CardDefinition card,
            ResearchProductionCatalog catalog, out string reason, out ProductionAttempt attempt)
        {
            using var mutation = VisionSystem.BeginMutation();
            attempt = null;
            if (!PrepareAttempt(player, root, hero, hex, mode, card, catalog,
                    out var quote, out var quota, out var output, out reason)) return false;
            if (quota != null && !quota.TryReserve(card.authoredKey))
            { reason = "Finish this blueprint's current challenge first."; return false; }
            if (!quote.TryDebit(root, out _, out _))
            { quota?.Release(card.authoredKey); reason = "The challenge cost is no longer affordable."; return false; }
            attempt = new ProductionAttempt(card, quota, output, player, root, player.Hand);
            ApplyResearchReveal(mode, hero);
            return true;
        }

        public static bool TryQuoteAttempt(PlayerSetupData player, PlayerRoot root, UnitData hero,
            HexCoord hex, ResearchProductionMode mode, CardDefinition card, ResearchProductionCatalog catalog,
            out PaymentQuote quote, out string reason)
            => PrepareAttempt(player, root, hero, hex, mode, card, catalog, out quote, out _, out _, out reason);

        private static bool PrepareAttempt(PlayerSetupData player, PlayerRoot root, UnitData hero,
            HexCoord hex, ResearchProductionMode mode, CardDefinition card, ResearchProductionCatalog catalog,
            out PaymentQuote quote, out BlueprintQuota quota, out CardDefinition output, out string reason)
        {
            reason = null; quota = null; output = null;
            quote = new PaymentQuote(AttemptApCost(card), card?.resourceCost);
            if (player == null || root == null || hero == null || card == null || catalog == null)
            {
                reason = "Cannot start this challenge. Reopen the window.";
                return false;
            }
            if (player.IsHuman && player.MatchLoadout == null)
            {
                reason = "Select a saved deck before research or production.";
                return false;
            }
            if (!object.ReferenceEquals(root, PlayerRootRegistry.FindFor(player))
                || !object.ReferenceEquals(root.Setup, player))
            {
                reason = "Your resources are unavailable. Try again.";
                return false;
            }
            if (!Offers(catalog, mode, player.Faction, card)
                || (!player.IsHuman && player.AiBlueprints != null && !player.AiBlueprints.Contains(card.authoredKey)))
            {
                reason = "This card is unavailable for research or production.";
                return false;
            }
            if (!IsEligible(player, hex, mode, out reason))
                return false;
            if (!ActorStillQualifies(player, hero, hex, mode))
            {
                reason = "This hero can no longer perform this action here.";
                return false;
            }
            output = catalog.ResolveResult(card);
            if (output == null)
            { reason = "This card cannot be produced."; return false; }
            if (!quote.CanDebit(root, out _))
            {
                reason = !root.CanSpendActionPoints(AttemptApCost(card))
                    ? $"Need {AttemptApCost(card)} AP to start this challenge." : "Not enough resources for this challenge.";
                return false;
            }

            quota = player.IsHuman && player.MatchLoadout != null ? player.BlueprintQuota : null;
            if (player.IsHuman && (quota == null || !quota.Available(card.authoredKey)))
            {
                reason = quota == null || !quota.Unlocked(card.authoredKey)
                    ? "This blueprint is not in your selected deck." : "Finish this blueprint's current challenge first.";
                return false;
            }
            return true;
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

        // Headless equivalent of BattleAttackPopupUI.RunResearchProductionChallenge — same tokens
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

            bool[] tokens = ChallengeResolver.RollTokens(TokenPoolSizeFor(hero));
            while (fateSpent < fateBudget && CountHits(tokens) < required)
            {
                // Same move order as the interactive loop: a miss is always rerolled before any
                // overflow die is appended.
                if (HasMiss(tokens))
                {
                    for (int i = 0; i < tokens.Length; i++)
                        if (!tokens[i])
                        {
                            tokens[i] = ChallengeResolver.RollTokens(1)[0];
                            break;
                        }
                }
                else
                {
                    var grown = new bool[tokens.Length + 1];
                    Array.Copy(tokens, grown, tokens.Length);
                    grown[tokens.Length] = ChallengeResolver.RollTokens(1)[0];
                    tokens = grown;
                }
                fateSpent++;
            }

            int hits = CountHits(tokens);
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
        // same way every step; UnityEngine.Random would make it jitter).
        // Each spent Fate supplies one more independent 50/50 attempt at a hit, either rerolling
        // a miss or extending the pool. Stopping at the target does not change P(hits >= target).
        public static float EstimateSuccessChance(UnitData hero, CardDefinition card, int maxFateSpend = int.MaxValue)
            => EstimateSuccessChanceForFate(hero != null ? hero.Fate : 0, card, maxFateSpend,
                TokenPoolSizeFor(hero));

        // Pure projection used by attachment valuation; never change a live hero's Fate.
        // Omitted baseTokenCount describes a fully replenished hero at the projected Fate stat.
        public static float EstimateSuccessChanceForFate(int fate, CardDefinition card,
            int maxFateSpend = int.MaxValue, int? baseTokenCount = null)
        {
            int required = RequiredSuccesses(card);
            if (required <= 0)
                return 1f;
            int fateAvail = Mathf.Max(0, fate);
            int tokens = Mathf.Max(0, baseTokenCount ?? fateAvail) + Mathf.Clamp(maxFateSpend, 0, fateAvail);
            return BinomialTailAtLeast(tokens, 0.5, required);
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
