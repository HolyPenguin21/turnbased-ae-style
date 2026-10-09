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
    //  game). Two multiplicative damps on the attachment's gain: the same RepeatFactor is read
    //  by READY recipient valuation and generated attachment deployment valuation.
    //
    //    saturation (moved to EquipmentEfficiency, priced for hand items too) = 1 / (1 + carriers) —
    //                 only for abilities whose value saturates (Stealth,
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

        // One paid attachment Challenge, recorded by MaterializationExecutor.TryGenerate before
        // its roll (won or lost). Consumers must not record the same attempt again.
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

        // Every card attempted inside the window with its count, for the Development admission
        // fingerprint: the repeat damp is an input of the decision, so it must invalidate it.
        internal static string HistoryKey(PlayerSetupData player, int turn)
        {
            if (player == null || !History.TryGetValue(player, out var list))
                return "-";
            return string.Join(",", list.Where(e => turn - e.Turn < AiConfigV2.devDiversityWindowTurns)
                .GroupBy(e => e.CardKey).OrderBy(g => g.Key, System.StringComparer.Ordinal)
                .Select(g => g.Key + "x" + g.Count()));
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

        // Pure rule (EditMode-testable): 1 for a card not attempted lately, falling with every recent
        // attempt. Ability saturation by carriers is priced where the ability is valued
        // (EquipmentEfficiency), so existing hand items saturate too.
        internal static float RepeatFactor(int recentAttempts) =>
            1f / (1f + AiConfigV2.devDiversityRecentWeight * System.Math.Max(0, recentAttempts));

        internal static float RepeatFactor(PlayerSetupData player, int turn, CardDefinition card,
            out string note)
        {
            int recent = RecentAttempts(player, turn, card);
            float factor = RepeatFactor(recent);
            note = recent > 0 ? $"diversity={factor:0.00} (recent {recent}) " : string.Empty;
            return factor;
        }

        private static string CardKey(CardDefinition card) =>
            string.IsNullOrEmpty(card.authoredKey) ? card.displayName : card.authoredKey;
    }
}
