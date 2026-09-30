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
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    // Binds the authored Canvas_UI/Panel_Data/{Button_Data,Panel_Data/Text} hierarchy.
    // Auto-binding lets the new local scene work without replacing it with a remote scene.
    [DisallowMultipleComponent]
    public sealed class PlayerDataPanelUI : MonoBehaviour
    {
        [SerializeField] private Button dataButton;
        [SerializeField] private GameObject contentPanel;
        [SerializeField] private Text dataText;
        [SerializeField] private TMP_Text dataTmpText;
        [SerializeField] private CardHandUI cardHand;
        private float _nextRefresh;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            BindScene(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => BindScene(scene);

        private static void BindScene(Scene scene)
        {
            if (scene.name != SceneNames.Game) return;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
                {
                    if (node.name != "Canvas_UI") continue;
                    Transform panel = node.Find("Panel_Data");
                    if (panel != null && panel.GetComponent<PlayerDataPanelUI>() == null)
                        panel.gameObject.AddComponent<PlayerDataPanelUI>();
                    return;
                }
        }

        private void Awake()
        {
            if (dataButton == null) dataButton = transform.Find("Button_Data")?.GetComponent<Button>();
            if (contentPanel == null) contentPanel = transform.Find("Panel_Data")?.gameObject;
            Transform text = contentPanel != null ? contentPanel.transform.Find("Text") : null;
            if (dataText == null) dataText = text?.GetComponent<Text>();
            if (dataTmpText == null) dataTmpText = text?.GetComponent<TMP_Text>();
            if (cardHand == null)
                cardHand = GetComponentInParent<Canvas>()?.GetComponentInChildren<CardHandUI>(true);

            if (dataButton == null || contentPanel == null || (dataText == null && dataTmpText == null))
            {
                Debug.LogWarning("PlayerDataPanelUI: expected Button_Data and Panel_Data/Text under "
                    + "Canvas_UI/Panel_Data.", this);
                enabled = false;
                return;
            }
            // These labels may contain player/army names; render them as plain text.
            if (dataText != null) dataText.supportRichText = false;
            if (dataTmpText != null) dataTmpText.richText = false;
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
                IEnumerable<CardData> hand;
                IEnumerable<CardDefinition> deck;
                if (player.IsHuman)
                {
                    hand = cardHand != null ? cardHand.HumanHand : null;
                    deck = cardHand != null ? cardHand.HumanRemainingDeck : null;
                }
                else
                {
                    AiHandData aiHand = AiHandRegistry.Peek(player);
                    hand = aiHand?.Hand;
                    // Before the first AI turn its hand has not been dealt. Reading the complete
                    // authored pool gives the same combined hand+deck potential without drawing.
                    deck = aiHand != null ? aiHand.RemainingDeck
                        : cardHand?.StartingDeckCatalog?.BuildDeckPool(player.Faction);
                }
                PlayerForceAnalysis force = PlayerForceAnalysis.Calculate(player,
                    ArmyRegistry.AllForOwner(player), hand, deck);
                if (output.Length > 0) output.Append("\n\n");
                string faction = cardHand?.StartingDeckCatalog?.GetCatalog(player.Faction)?.displayName
                    ?? player.Faction.ToString();
                output.Append(player.Nickname).Append(" — ").Append(faction);
                if (player.IsEliminated) output.Append(" (выбыл)");
                output.Append("\nВойска на карте: ").Append(Number(force.DeployedPower))
                    .Append(" / вся доступная колода: ").Append(Number(force.TotalAvailablePower));
                ArmyData army = force.StrongestArmy;
                output.Append("\nСильнейшая полевая армия: ");
                if (army == null) output.Append("нет");
                else output.Append(army.Name).Append(" (").Append(army.Hex.Q).Append(", ")
                    .Append(army.Hex.R).Append("); карт ").Append(army.Members.Count).Append('/')
                    .Append(army.Capacity).Append("; сила ").Append(Number(force.StrongestArmyPower));
                output.Append("; готовность ").Append(force.ReadinessPercent.ToString("0.00", CultureInfo.InvariantCulture))
                    .Append("% / потенциал армии ").Append(Number(force.GroundArmyPotential))
                    .Append(force.ForceReady ? "; порог >80% пройден" : "; порог >80% не пройден");
            }
            string value = output.Length > 0 ? output.ToString() : "Игроки ещё не созданы.";
            if (dataText != null && dataText.text != value) dataText.text = value;
            if (dataTmpText != null && dataTmpText.text != value) dataTmpText.text = value;
        }

        private static string Number(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
