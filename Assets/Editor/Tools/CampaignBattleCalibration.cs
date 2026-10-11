#if UNITY_EDITOR
using System.Linq;
using Game.Campaign;
using Game.Cards;
using Game.Core;
using UnityEditor;
using UnityEngine;

public static class CampaignBattleCalibration
{
    [MenuItem("Tools/Campaign/Compare standard decks (1000 fast results)")]
    public static void Compare()
    {
        var config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Config/GameConfig.asset");
        if (config == null) { Debug.LogError("Missing GameConfig."); return; }
        var rules = new DeckRules(config.collectionDeckCatalog, config.collectionResearchCatalog);
        var decks = DeckRules.PlayableFactions.Select(f => CampaignDeck.Standard(f, rules)).ToList();
        var resolver = new CampaignBattleResolver();
        foreach (var d in decks)
        {
            var score = resolver.Evaluate(d);
            Debug.Log($"{d.Name}: combat={score.CombatPotential:F2}, commanders={score.CommanderPotential:F2}, strategic={score.StrategicPotential:F2}, attachments={score.AttachmentPotential:F2}; total={score.Total:F2}");
        }
        for (int a = 0; a < decks.Count; a++) for (int b = a + 1; b < decks.Count; b++)
        {
            double p = resolver.Compare(decks[a], decks[b]).WinChance;
            int wins = Enumerable.Range(0, 1000).Count(i => resolver.Resolve(decks[a], decks[b], CampaignRandom.Derive(419, i)).Outcome == CampaignOutcome.AttackerVictory);
            Debug.Log($"{decks[a].Name} vs {decks[b].Name}: predicted {p:P2}; fast victories {wins}/1000; full-match victories NOT MEASURED.");
        }
    }
}
#endif
