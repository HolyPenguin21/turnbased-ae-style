using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  DEVELOPMENT DIVERSITY  (Strategy V2 — Research/Production anti-monoculture)
    // ===========================================================================================
    //  Research/Production valued every (card, recipient) pair on its own, so the single best card
    //  won again and again (2026-10-07 log: Hunter Glands and Ghost Genome twice each in one
    //  game). Two multiplicative damps on the opportunity's gain, applied once where a READY site
    //  prices its output (DevelopmentOpportunityEvaluator.AddReady):
    //
    //    saturation = 1 / (1 + carriers)   — only for abilities whose value saturates (Stealth,
    //                 Recce, Splash, Scorcher, AA, Regeneration, RapidReaction): `carriers` is the
    //                 number of own units (map + hand) that already have the ability's family. The
    //                 sixth stealth unit is worth nothing; pure stat items are never damped, so
    //                 one unit can still be stacked into a strong fighter.
    //    repeat     = 1 / (1 + devDiversityRecentWeight x attempts) — the same card attempted in the
    //                 last devDiversityWindowTurns turns. Near-equal leaders then rotate.
    //
    //  The history is a decision input, unlike DevelopmentOutcomeTelemetry (log-only tally).
    // ===========================================================================================
    internal static class DevelopmentDiversity
    {
        private static readonly Dictionary<PlayerSetupData, List<(int Turn, string CardKey)>> History =
            new Dictionary<PlayerSetupData, List<(int Turn, string CardKey)>>();

        // Abilities whose strategic value saturates with the number of carriers.
        private static readonly HashSet<string> SaturatingAbilities = new HashSet<string>
        {
            UnitAbilities.Splash, UnitAbilities.Scorcher, UnitAbilities.AntiAir,
            UnitAbilities.Regeneration, UnitAbilities.RapidReaction,
        };

        internal static void ClearAll() => History.Clear();

        // One executed Challenge (won or lost: the resources are gone either way).
        internal static void RecordAttempt(PlayerSetupData player, int turn, CardDefinition card)
        {
            if (player == null || card == null)
                return;
            if (!History.TryGetValue(player, out var list))
                History[player] = list = new List<(int, string)>();
            list.Add((turn, CardKey(card)));
        }

        internal static int RecentAttempts(PlayerSetupData player, int turn, CardDefinition card)
        {
            if (player == null || card == null || !History.TryGetValue(player, out var list))
                return 0;
            string key = CardKey(card);
            return list.Count(e => e.CardKey == key && turn - e.Turn < AiConfigV2.devDiversityWindowTurns);
        }

        // The saturating family an added ability belongs to; null = it never saturates.
        internal static string FamilyOf(string ability)
        {
            if (string.IsNullOrEmpty(ability))
                return null;
            if (AbilityParams.TryGetStealthLevel(ability, out _))
                return "Stealth";
            if (AbilityParams.AbilitiesHaveAnyRecce(new[] { ability }))
                return "Recce";
            return SaturatingAbilities.Contains(ability) ? ability : null;
        }

        internal static List<string> SaturatingFamilies(CardDefinition card) =>
            (card?.equipment?.addAbilities ?? new List<string>())
                .Select(FamilyOf).Where(f => f != null).Distinct().ToList();

        // Own units (map + hand) that already carry at least one of the families.
        internal static int Carriers(PlayerSetupData player, AiHandData hand, ICollection<string> families)
        {
            if (player == null || families == null || families.Count == 0)
                return 0;
            int count = 0;
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army == null || army.IsPrison)
                    continue;
                foreach (UnitData unit in army.Members)
                    if (unit != null && unit.Abilities.Any(a => families.Contains(FamilyOf(a))))
                        count++;
            }
            foreach (CardData card in hand?.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
            {
                if (card?.Definition == null
                    || (card.Definition.cardType != CardType.Unit && card.Definition.cardType != CardType.Hero))
                    continue;
                IReadOnlyList<string> abilities = MaterializationChainMatching.EffectiveAbilities(
                    card.Definition, card.Equipment, card.Mutator);
                if (abilities.Any(a => families.Contains(FamilyOf(a))))
                    count++;
            }
            return count;
        }

        // Pure rule (EditMode-testable): 1 for a fresh pure-stat card, falling with saturation
        // carriers and recent repeats.
        internal static float Factor(int carriers, int recentAttempts) =>
            1f / ((1f + System.Math.Max(0, carriers))
                * (1f + AiConfigV2.devDiversityRecentWeight * System.Math.Max(0, recentAttempts)));

        internal static float Factor(PlayerSetupData player, AiHandData hand, int turn,
            CardDefinition card, out string note)
        {
            List<string> families = SaturatingFamilies(card);
            int carriers = families.Count > 0 ? Carriers(player, hand, families) : 0;
            int recent = RecentAttempts(player, turn, card);
            float factor = Factor(carriers, recent);
            note = factor < 1f
                ? $"diversity={factor:0.00} (carriers {carriers}{(families.Count > 0 ? " of " + string.Join("/", families) : "")}, recent {recent}) "
                : string.Empty;
            return factor;
        }

        private static string CardKey(CardDefinition card) =>
            string.IsNullOrEmpty(card.authoredKey) ? card.displayName : card.authoredKey;
    }
}
