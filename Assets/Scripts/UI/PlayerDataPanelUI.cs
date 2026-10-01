using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Core;
using Game.Map;
using Game.Players;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Authored on Canvas_UI/Panel_Data in the Game scene; every reference is assigned there.
    // The numbers are PlayerForceAnalysis's; this class only reads sources and formats them.
    [DisallowMultipleComponent]
    public sealed class PlayerDataPanelUI : MonoBehaviour
    {
        [SerializeField] private Button dataButton;
        [SerializeField] private GameObject contentPanel;
        [SerializeField] private TMP_Text dataTmpText;
        [SerializeField] private CardHandUI cardHand;
        private float _nextRefresh;

        private void Awake()
        {
            if (dataButton == null || contentPanel == null || dataTmpText == null || cardHand == null)
            {
                Debug.LogWarning("PlayerDataPanelUI: assign dataButton, contentPanel, dataTmpText "
                    + "and cardHand in the scene.", this);
                enabled = false;
                return;
            }
            // These labels may contain player/army names; render them as plain text.
            dataTmpText.richText = false;
            contentPanel.SetActive(false);
        }

        private void OnEnable()
        {
            if (dataButton != null) dataButton.onClick.AddListener(Toggle);
        }

        private void OnDisable()
        {
            if (dataButton != null) dataButton.onClick.RemoveListener(Toggle);
        }

        public void Toggle()
        {
            if (contentPanel == null) return;
            bool open = !contentPanel.activeSelf;
            contentPanel.SetActive(open);
            if (open) Refresh();
        }

        private void Update()
        {
            if (contentPanel != null && contentPanel.activeInHierarchy && Time.unscaledTime >= _nextRefresh)
                Refresh();
        }

        private void Refresh()
        {
            _nextRefresh = Time.unscaledTime + 1f;
            var output = new StringBuilder();
            foreach (PlayerSetupData player in GameSession.Players)
            {
                if (player == null || player.IsNeutral) continue;
                if (output.Length > 0) output.Append("\n\n");
                string faction = cardHand.StartingDeckCatalog?.GetCatalog(player.Faction)?.displayName
                    ?? player.Faction.ToString();
                output.Append(player.Nickname).Append(" — ").Append(faction);
                if (player.IsEliminated)
                {
                    output.Append(" (eliminated)");
                    continue;
                }
                IEnumerable<CardData> hand;
                IEnumerable<CardDefinition> deck;
                if (player.IsHuman)
                {
                    hand = cardHand.HumanHand;
                    deck = cardHand.HumanRemainingDeck;
                }
                else
                {
                    // Read-only lookup: opening the panel never creates, deals or draws an AI hand.
                    // Before the first AI turn the whole authored pool is its hand + remaining deck.
                    AiHandData aiHand = AiHandRegistry.Peek(player);
                    hand = aiHand?.Hand;
                    deck = aiHand != null ? aiHand.RemainingDeck
                        : cardHand.StartingDeckCatalog?.BuildDeckPool(player.Faction);
                }
                PlayerForceAnalysis force = PlayerForceAnalysis.Calculate(player,
                    ArmyRegistry.AllForOwner(player), hand, deck);
                output.Append("\nTroops on map: ")
                    .Append(Ratio(force.DeployedPower, force.TotalAvailablePower, force.DeployedPercent))
                    .Append(';');
                // Attack mobilization start (B): the field force against the Attack peak (the AI's
                // own AttackPeak); the gate opens strictly above 80% of it.
                string bar = force.AttackBar.ToString("0.0", CultureInfo.InvariantCulture);
                output.Append("\nField strike force: ")
                    .Append(Ratio(force.FieldStrikePotential, force.AttackPeak, force.FieldStrikePercent))
                    .Append(force.FieldStrikeReady ? "; gate open (>" : "; gate >").Append(bar)
                    .Append(force.FieldStrikeReady ? ")" : " (80%)")
                    .Append(';');
                ArmyData army = force.StrongestArmy;
                output.Append("\nStrongest field army: ");
                if (army == null) output.Append("none");
                else output.Append(army.Name).Append(" (").Append(army.Hex.Q).Append(", ")
                    .Append(army.Hex.R).Append("); ").Append(army.Members.Count).Append('/')
                    .Append(army.Capacity);
                output.Append("; power ")
                    .Append(Ratio(force.StrongestArmyPower, force.AttackPeak, force.ReadinessPercent))
                    .Append(force.ForceReady ? "; march open" : "; march >").Append(force.ForceReady ? "" : bar)
                    .Append(';');
            }
            string value = output.Length > 0 ? output.ToString() : "No players yet.";
            if (dataTmpText.text != value) dataTmpText.text = value;
        }

        // The percent comes from the raw values (PlayerForceAnalysis), never from the rounded text.
        private static string Ratio(float part, float whole, float percent) =>
            part.ToString("0.0", CultureInfo.InvariantCulture) + " / "
            + whole.ToString("0.0", CultureInfo.InvariantCulture) + " ("
            + percent.ToString("0.00", CultureInfo.InvariantCulture) + "%)";
    }
}
