using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Authored on Canvas_UI/Panel_Data in the Game scene; every reference is assigned there.
    // Gameplay intelligence: all values come from existing read-only analysis and registries.
    [DisallowMultipleComponent]
    public sealed class PlayerDataPanelUI : MonoBehaviour
    {
        [SerializeField] private Button dataButton;
        [SerializeField] private GameObject contentPanel;
        [SerializeField] private TMP_Text dataTmpText;
        [SerializeField] private CardHandUI cardHand;
        [SerializeField] private GameTurnController turnController;
        [SerializeField] private HexMap map;
        private float _nextRefresh;

        private void Awake()
        {
            if (dataButton == null || contentPanel == null || dataTmpText == null || cardHand == null
                || turnController == null || map == null)
            {
                Debug.LogWarning("PlayerDataPanelUI: assign dataButton, contentPanel, dataTmpText "
                    + "cardHand, turnController and map in the scene.", this);
                if (dataButton != null) dataButton.gameObject.SetActive(false);
                if (contentPanel != null) contentPanel.SetActive(false);
                enabled = false;
                return;
            }
            // These labels may contain player/army names; render them as plain text.
            dataTmpText.richText = false;
            contentPanel.SetActive(false);
            UpdateAccess();
        }

        private void OnEnable()
        {
            if (dataButton != null) dataButton.onClick.AddListener(Toggle);
        }

        private void OnDisable()
        {
            if (dataButton != null) dataButton.onClick.RemoveListener(Toggle);
            if (contentPanel != null) contentPanel.SetActive(false);
            if (dataButton != null) dataButton.gameObject.SetActive(false);
        }

        public void Toggle()
        {
            if (contentPanel == null || !UpdateAccess()) return;
            bool open = !contentPanel.activeSelf;
            contentPanel.SetActive(open);
            if (open) Refresh();
        }

        private void Update()
        {
            if (!UpdateAccess()) return;
            if (contentPanel != null && contentPanel.activeInHierarchy && Time.unscaledTime >= _nextRefresh)
                Refresh();
        }

        private bool UpdateAccess()
        {
            bool access = TacticalIntelligence.HasAccess(GameSession.FindHumanPlayer());
            if (dataButton != null && dataButton.gameObject.activeSelf != access)
                dataButton.gameObject.SetActive(access);
            if (!access)
            {
                if (contentPanel != null) contentPanel.SetActive(false);
                if (dataTmpText != null) dataTmpText.text = string.Empty;
            }
            return access;
        }

        private void Refresh()
        {
            _nextRefresh = Time.unscaledTime + 1f;
            if (!UpdateAccess()) return;
            PlayerSetupData observer = GameSession.FindHumanPlayer();
            // CompletedTurns advances at END of our turn. Keep that report during other
            // players' turns; reveal the next one only when our next turn actually starts.
            long personalTurn = turnController.CompletedTurnsFor(observer);
            if (turnController.CurrentPlayer != observer && personalTurn > 0) personalTurn--;
            var output = new StringBuilder("Tactical Intelligence");
            foreach (PlayerSetupData player in GameSession.Players)
            {
                if (player == null || player == observer || player.IsNeutral) continue;
                string faction = cardHand.StartingDeckCatalog?.GetCatalog(player.Faction)?.displayName
                    ?? player.Faction.ToString();
                output.Append("\n\n").Append(player.Nickname).Append(" — ").Append(faction);
                if (player.IsEliminated)
                {
                    output.Append(" (eliminated)");
                    continue;
                }
                int count = TacticalIntelligence.VisibleFieldCount(observer, player,
                    ArmyRegistry.AllForOwner(observer));
                int mask = TacticalIntelligence.RevealMask(GameSession.MatchId,
                    GameSession.Players.IndexOf(observer), GameSession.Players.IndexOf(player), personalTurn, count);
                output.Append("\nIntel coverage: ").Append(count * 100 / TacticalIntelligence.FieldCount)
                    .Append("% (").Append(count).Append('/').Append(TacticalIntelligence.FieldCount).Append(')');
                AppendOpponent(output, player, mask);
            }
            string value = output.ToString();
            if (dataTmpText.text != value) dataTmpText.text = value;
            FitTextContent();
        }

        private void AppendOpponent(StringBuilder output, PlayerSetupData player, int mask)
        {
            // A zero-coverage report doesn't even request the hidden force/hand data.
            List<ArmyData> armies = mask != 0 ? ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && !a.IsPrison).ToList() : new List<ArmyData>();
            List<UnitData> units = armies.SelectMany(a => a.Members)
                .Where(u => u != null && !u.IsPrisoner && !u.IsSummoned && u.HitPointsCurrent > 0)
                .Distinct().ToList();
            PlayerForceAnalysis force = default;
            if ((mask & 0xf) != 0)
            {
                // Never create/draw an AI hand or advance its mission state to show a report.
                AiHandData hand = AiHandRegistry.Peek(player);
                IEnumerable<CardDefinition> deck = hand != null ? hand.RemainingDeck
                    : cardHand.StartingDeckCatalog?.BuildDeckPool(player);
                force = PlayerForceAnalysis.Calculate(player, armies, hand?.Hand, deck);
            }
            List<BuildingData> buildings = mask != 0 ? BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player).ToList() : new List<BuildingData>();
            PlayerRoot root = mask != 0 ? PlayerRootRegistry.FindFor(player) : null;
            for (int field = 0; field < TacticalIntelligence.FieldCount; field++)
            {
                output.Append('\n').Append(FieldLabel(field)).Append(": ");
                if ((mask & (1 << field)) == 0)
                {
                    output.Append("???");
                    continue;
                }
                switch (field)
                {
                    case 0: output.Append(Number(force.DeployedPower)); break;
                    case 1: output.Append(Number(force.AttackPeak)); break;
                    case 2: output.Append(Number(force.StrongestArmyPower)); break;
                    case 3:
                        ArmyData strongest = force.StrongestArmy;
                        if (strongest == null) output.Append("none");
                        else output.Append(strongest.Members.Count).Append('/').Append(strongest.Capacity);
                        break;
                    case 4: output.Append(player.IsHuman ? "unavailable" : AttackIntentLabel.Describe(player)); break;
                    case 5: output.Append(armies.Count(a => TacticalIntelligence.IsGroundObserver(a, player))); break;
                    case 6: output.Append(units.Count(u => !u.IsAviation && u.IsGroundCombatant)); break;
                    case 7: output.Append(units.Count(u => u.IsAviation)); break;
                    case 8: output.Append(units.Count(u => u.IsHero)); break;
                    case 9: output.Append(buildings.Count(b => b.IsBase)); break;
                    case 10: output.Append(buildings.Sum(b => b.FacilitySlots.Count(f => f != null))); break;
                    case 11:
                        var citadel = player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue
                            ? new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value) : (HexCoord?)null;
                        output.Append(Number(AiPower.EffectiveArmyPower(armies
                            .Where(a => a.IsGarrison && citadel.HasValue && a.Hex.Equals(citadel.Value))
                            .SelectMany(a => a.Members).ToList())));
                        break;
                    default:
                        if (root == null) { output.Append("unavailable"); break; }
                        ResourceType resource = (ResourceType)(field - 12);
                        output.Append(root.GetResource(resource)).Append(" (+")
                            .Append(IncomeProjection.IncomeFor(player, resource, map)).Append(')');
                        break;
                }
            }
        }

        private static string FieldLabel(int field)
        {
            switch (field)
            {
                case 0: return "Deployed ground power";
                case 1: return "Strike army potential";
                case 2: return "Strongest field army power";
                case 3: return "Strongest field army size";
                case 4: return "Attack operation";
                case 5: return "Ground field armies";
                case 6: return "Ground troops";
                case 7: return "Aircraft";
                case 8: return "Deployed heroes";
                case 9: return "Bases";
                case 10: return "Facilities";
                case 11: return "Citadel garrison power";
                case 12: return "Human (stock / income)";
                case 13: return "Energy (stock / income)";
                case 14: return "Materials (stock / income)";
                default: return "Tech (stock / income)";
            }
        }

        private void OnRectTransformDimensionsChange()
        {
            if (dataTmpText != null && contentPanel != null && contentPanel.activeInHierarchy)
                FitTextContent();
        }

        private void FitTextContent()
        {
            RectTransform content = dataTmpText.rectTransform;
            ScrollRect scroll = content.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.content != content || scroll.viewport == null)
                return;
            float height = Mathf.Max(scroll.viewport.rect.height,
                dataTmpText.GetPreferredValues(dataTmpText.text, content.rect.width, Mathf.Infinity).y);
            // This class owns only its text extent; ScrollRect owns scrolling. Updating the
            // statistics must preserve the reader's position unless the new text is shorter.
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            content.anchoredPosition = new Vector2(content.anchoredPosition.x,
                Mathf.Clamp(content.anchoredPosition.y, 0f, height - scroll.viewport.rect.height));
        }

        private static string Number(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
