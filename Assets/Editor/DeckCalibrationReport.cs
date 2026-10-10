using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game.Ai.V2;
using Game.Cards;
using Game.Progression;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // Offline authoring report only: fixed costs never depend on AI state during play.
    public static class DeckCalibrationReport
    {
        [MenuItem("Game/Collection/Generate Calibration Report")]
        public static void Generate()
        {
            var starting = AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset");
            var research = AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset");
            var rules = new DeckRules(starting, research);
            var report = new StringBuilder("CardKey,HostKey,HostClass,CatalogPower,DeltaCombatContribution,CombatUtility,StrategicUtility,TotalAttachmentUtility,FixedPoints\n");
            foreach (var faction in DeckRules.PlayableFactions)
            {
                var hosts = rules.Cards(faction).Where(c => c.cardType == CardType.Unit || c.cardType == CardType.Hero).ToList();
                foreach (var attachment in rules.Cards(faction).Where(c => c.cardType == CardType.Equipment))
                {
                    var compatible = hosts.Where(h => EquipmentSystem.FitsHost(attachment, h, out _)).ToList();
                    var samples = new Dictionary<string, CardDefinition>
                    {
                        ["weak"] = compatible.OrderBy(h => AiPower.ToPowerUnit(h).BasePower).FirstOrDefault(h => h.cardType == CardType.Unit),
                        ["strong"] = compatible.OrderByDescending(h => AiPower.ToPowerUnit(h).BasePower).FirstOrDefault(h => h.cardType == CardType.Unit),
                        ["armored"] = compatible.FirstOrDefault(h => h.unitTypeTags.Contains(UnitTypeTag.Armored)),
                        ["bio"] = compatible.FirstOrDefault(h => h.unitTypeTags.Contains(UnitTypeTag.Bio)),
                        ["recon"] = compatible.FirstOrDefault(h => AbilityParams.GetBestRecceRadius(h.grantedAbilities) > 0),
                        ["hero"] = compatible.FirstOrDefault(h => h.cardType == CardType.Hero),
                    };
                    foreach (var sample in samples)
                    {
                        var host = sample.Value;
                        if (host == null) { report.AppendLine($"{attachment.authoredKey},none,{faction}:{sample.Key},,,,,,{attachment.deckPointCost}"); continue; }
                        var before = EquipmentSystem.Project(host, null, null);
                        var after = EquipmentSystem.Project(host, null, null, attachment);
                        var context = new EfficiencyContext
                        {
                            IsHero = host.cardType == CardType.Hero,
                            HostTags = host.unitTypeTags,
                            StealthUsable = AbilityParams.GetBestRecceRadius(after.Abilities) > 0,
                            IsFacilityOperator = host.grantedAbilities.Contains(UnitAbilities.Researcher) || host.grantedAbilities.Contains(UnitAbilities.Assembler),
                        };
                        var utility = EquipmentEfficiency.Utility(Stats(before), before.Abilities.ToList(), Stats(after), after.Abilities.ToList(), context);
                        report.AppendLine(FormattableString.Invariant($"{attachment.authoredKey},{host.authoredKey},{faction}:{sample.Key},{AiPower.ToPowerUnit(host).BasePower},{utility.DeltaC},{utility.Combat},{utility.Tactical},{utility.Total},{attachment.deckPointCost}"));
                    }
                }
            }
            Directory.CreateDirectory("Docs/DeckCollection");
            File.WriteAllText("Docs/DeckCollection/attachment-calibration-unity.csv", report.ToString());
            File.WriteAllText("Docs/DeckCollection/strategy-decks-unity.json",
                JsonUtility.ToJson(new CollectionProfile { savedDecks = CreateStrategyDecks(rules) }, true));
            Debug.Log("Deck attachment calibration written. This report does not establish competitive balance; run strategy matches before changing fixed costs.");
        }
        // Full-collection authoring fixtures, not a grant to a real profile.
        internal static List<SavedDeck> CreateStrategyDecks(DeckRules rules)
        {
            var result = new List<SavedDeck>();
            foreach (var faction in DeckRules.PlayableFactions)
            {
                var cards = rules.Cards(faction).Where(c => c.deckCopyLimit > 0).ToList();
                Func<string, int> owned = key => rules.Resolve(key)?.deckCopyLimit ?? 0;
                foreach (var theme in new[] { "Mass", "Armor", "Recon", "Mixed", "Aviation", "Economy" })
                {
                    var deck = new SavedDeck { deckId = faction + "." + theme, name = theme, faction = faction };
                    var foundation = cards.Where(c => c.cardType == CardType.Base || c.cardType == CardType.Facility)
                        .Concat(cards.Where(c => c.cardType == CardType.Hero).Take(1));
                    foreach (var card in foundation) rules.TryChange(deck, card.authoredKey, 1, owned, out _);
                    var preferred = cards.OrderByDescending(c => ThemeScore(c, theme)).ThenBy(c => c.authoredKey).ToList();
                    for (int copy = 0; copy < 4; copy++)
                        foreach (var card in preferred) rules.TryChange(deck, card.authoredKey, 1, owned, out _);
                    var validation = rules.Validate(deck, owned);
                    if (!validation.IsValid || validation.Points < 95)
                        throw new InvalidOperationException("Invalid or underfilled strategy fixture: " + faction + "/" + theme);
                    result.Add(deck);
                }
            }
            return result;
        }
        private static float ThemeScore(CardDefinition card, string theme)
        {
            if (theme == "Economy") return card.grantedAbilities.Count(a => a == UnitAbilities.Researcher || a == UnitAbilities.Assembler || a.StartsWith("Collect", StringComparison.Ordinal)) * 20;
            if (card.cardType != CardType.Unit) return -100;
            if (theme == "Mass") return 20 - card.deckPointCost;
            if (theme == "Armor") return (card.unitTypeTags.Contains(UnitTypeTag.Armored) ? 20 : 0) + card.attack + card.hitPoints;
            if (theme == "Recon") return AbilityParams.GetBestRecceRadius(card.grantedAbilities) * 10 + card.moveMax;
            if (theme == "Aviation") return card.isAviation ? 30 + card.moveMax : 0;
            return card.attack + card.defenseRating + card.range;
        }
        private static EfficiencyStats Stats(PredictedEquipmentState state)
            => new EfficiencyStats(state.Stats[EquipmentStat.Attack], state.Stats[EquipmentStat.Defense],
                state.Stats[EquipmentStat.HitPoints], state.Stats[EquipmentStat.Range], state.Stats[EquipmentStat.MoveMax],
                state.Stats[EquipmentStat.Initiative], state.Stats[EquipmentStat.ActivationApCost], state.Stats[EquipmentStat.Fate]);
    }
}
