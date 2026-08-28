using System;
using System.Collections.Generic;
using System.Text;
using Game.Cards;
using Game.Core;
using Game.Players;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    // The shared Research / Production catalog picker. One modal, two modes (see
    // ResearchProductionMode) — Research and Production differ only in which list of the
    // ResearchProductionCatalog they page through and which qualifying Hero is shown on the
    // left. Opened by HexSelectionController's Research/Production hex actions AFTER their
    // existing eligibility check passes; this modal never re-checks eligibility.
    //
    // Reuses GameConfig.armyUnitCardPrefab for the grid cells (ArmyUnitCardUI has a dedicated
    // preview mode — see SetupPreview — that shows a CardDefinition without any Army-specific
    // drag/repair/stealth/equipment behaviour). At most PageSize cells exist at once, so a large
    // catalog never instantiates its whole list.
    //
    // This milestone wires selection only: Create_Button toggles interactable with the current
    // selection and does nothing else yet. The actual Research/Production action is the next task.
    public class ResearchProductionModalUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private Button closeButton;

        [Header("Grid")]
        [SerializeField] private Transform gridContainer;
        // Same GameObject as gridContainer — a separately typed reference purely so SlotPosition
        // can read the grid's own cell size / spacing / column count directly, same pattern as
        // ArmyViewerModalUI.grid. The prefab cells are ignoreLayout and positioned manually.
        [SerializeField] private GridLayoutGroup grid;
        // "Prev" — active from page 2 onward, disabled on the first page.
        [SerializeField] private Button scrollLeftButton;
        // "Next" — active while cards remain after the current page, disabled on the last page.
        [SerializeField] private Button scrollRightButton;

        [Header("Hero panel (left)")]
        [SerializeField] private Image detailArtHero;
        [SerializeField] private TMP_Text detailTextHero;

        [Header("Result panel (right)")]
        [SerializeField] private Image detailArtResult;
        [SerializeField] private TMP_Text detailTextResult;

        [SerializeField] private Button createButton;

        [Header("Data")]
        [SerializeField] private GameConfig gameConfig;
        [SerializeField] private ResearchProductionCatalog catalog;

        // Exactly 8 cards per page, per the spec's pagination rules.
        private const int PageSize = 8;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Lets GameTurnController fold this modal into InputBlocked the same way it does
        // armyViewerModal — fired from Show/Hide whenever visibility actually changes.
        public event Action VisibilityChanged;

        private ResearchProductionMode _mode;
        private UnitData _hero;
        // The faction-filtered card list (filter applied in ResearchProductionCatalog.ResolveFor,
        // BEFORE pagination) — this is what _page indexes into.
        private List<CardDefinition> _cards = new List<CardDefinition>();
        private int _page;
        private CardDefinition _selected;
        private readonly List<ArmyUnitCardUI> _cardViews = new List<ArmyUnitCardUI>();

        private void Awake()
        {
            if (closeButton != null)
                closeButton.onClick.AddListener(Hide);
            if (scrollLeftButton != null)
                scrollLeftButton.onClick.AddListener(PrevPage);
            if (scrollRightButton != null)
                scrollRightButton.onClick.AddListener(NextPage);
            if (panelRoot != null)
                panelRoot.SetActive(false);
            if (createButton != null)
                createButton.interactable = false;
        }

        // Opened by HexSelectionController once Research/Production eligibility already passed.
        // `player` supplies the faction filter; `hero` is the qualifying Researcher/Assembler
        // Hero found by HexSelectionController (first match by the existing search — no picker UI
        // yet when several qualify).
        public void Show(ResearchProductionMode mode, PlayerSetupData player, UnitData hero)
        {
            bool wasShowing = IsShowing;

            _mode = mode;
            _hero = hero;
            Faction viewerFaction = player != null ? player.Faction : Faction.None;
            _cards = catalog != null
                ? catalog.ResolveFor(mode, viewerFaction)
                : new List<CardDefinition>();
            _page = 0;
            _selected = null;

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                // Draw order on this project's single shared Canvas is sibling order — force to
                // the end so this renders over whatever else is up, same as ArmyViewerModalUI.
                panelRoot.transform.SetAsLastSibling();
            }
            if (titleText != null)
                titleText.text = mode == ResearchProductionMode.Research ? "Research" : "Production";

            RefreshHeroPanel();
            RefreshGrid();
            RefreshResultPanel();

            if (!wasShowing)
                VisibilityChanged?.Invoke();
        }

        public void Hide()
        {
            bool wasShowing = IsShowing;
            if (panelRoot != null)
                panelRoot.SetActive(false);
            ClearGrid();

            _hero = null;
            _selected = null;
            _page = 0;
            _cards = new List<CardDefinition>();

            RefreshResultPanel();

            if (wasShowing)
                VisibilityChanged?.Invoke();
        }

        // ESC closes the modal — same shape as ArmyViewerModalUI.Update.
        private void Update()
        {
            if (!IsShowing || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
                return;
            Hide();
        }

        private int PageCount => Mathf.Max(1, Mathf.CeilToInt(_cards.Count / (float)PageSize));

        private bool HasNextPage => (_page + 1) * PageSize < _cards.Count;

        private void PrevPage()
        {
            if (_page <= 0)
                return;
            _page--;
            OnPageChanged();
        }

        private void NextPage()
        {
            if (!HasNextPage)
                return;
            _page++;
            OnPageChanged();
        }

        // Changing page clears the current selection: Result panel empties, Create goes disabled.
        private void OnPageChanged()
        {
            _selected = null;
            RefreshGrid();
            RefreshResultPanel();
        }

        // Instantiates at most PageSize preview cells for the current page — never the whole
        // catalog. Cells are positioned manually via SlotPosition (the prefab's LayoutElement is
        // ignoreLayout, so the GridLayoutGroup only supplies metrics, same as ArmyViewerModalUI).
        private void RefreshGrid()
        {
            ClearGrid();
            if (gridContainer == null || gameConfig == null || gameConfig.armyUnitCardPrefab == null)
                return;

            int start = _page * PageSize;
            int end = Mathf.Min(start + PageSize, _cards.Count);
            for (int i = start; i < end; i++)
            {
                CardDefinition card = _cards[i];
                ArmyUnitCardUI view = Instantiate(gameConfig.armyUnitCardPrefab, gridContainer);
                view.SetupPreview(card, gameConfig, OnCardClicked);
                view.SetSlot(SlotPosition(i - start), animated: false);
                _cardViews.Add(view);
            }

            RefreshPageButtons();
        }

        private void ClearGrid() => UIListUtility.DestroyAndClear(_cardViews);

        private void RefreshPageButtons()
        {
            if (scrollLeftButton != null)
            {
                scrollLeftButton.gameObject.SetActive(true);
                scrollLeftButton.interactable = _page > 0;
            }
            if (scrollRightButton != null)
            {
                scrollRightButton.gameObject.SetActive(true);
                scrollRightButton.interactable = HasNextPage;
            }
        }

        // Same top-left-origin cell maths as ArmyViewerModalUI.SlotPosition.
        private Vector2 SlotPosition(int index)
        {
            int columns = grid != null ? Mathf.Max(1, grid.constraintCount) : 1;
            int col = index % columns;
            int row = index / columns;
            Vector2 cellSize = grid != null ? grid.cellSize : Vector2.zero;
            Vector2 spacing = grid != null ? grid.spacing : Vector2.zero;
            RectOffset padding = grid != null ? grid.padding : null;
            float left = padding != null ? padding.left : 0f;
            float top = padding != null ? padding.top : 0f;
            float x = left + col * (cellSize.x + spacing.x);
            float y = -(top + row * (cellSize.y + spacing.y));
            return new Vector2(x, y);
        }

        private void OnCardClicked(CardDefinition card)
        {
            _selected = card;
            RefreshResultPanel();
        }

        private void RefreshHeroPanel()
        {
            if (detailArtHero != null)
            {
                Sprite sprite = _hero != null
                    ? (_hero.DetailArt != null ? _hero.DetailArt : _hero.Art)
                    : null;
                detailArtHero.sprite = sprite;
                detailArtHero.gameObject.SetActive(sprite != null);
            }
            if (detailTextHero != null)
                detailTextHero.text = _hero != null ? DescribeHero(_hero) : string.Empty;
        }

        private void RefreshResultPanel()
        {
            if (detailArtResult != null)
            {
                Sprite sprite = _selected != null
                    ? (_selected.detailArt != null ? _selected.detailArt : _selected.art)
                    : null;
                detailArtResult.sprite = sprite;
                detailArtResult.gameObject.SetActive(sprite != null);
            }
            if (detailTextResult != null)
                detailTextResult.text = _selected != null ? DescribeCard(_selected) : string.Empty;
            if (createButton != null)
                createButton.interactable = _selected != null;
        }

        private string DescribeHero(UnitData hero)
        {
            var sb = new StringBuilder();
            sb.AppendLine(hero.Name);
            sb.AppendLine($"Command Rating: {hero.CommandRating}");
            sb.AppendLine($"Fate: {hero.Fate}");
            string abilities = gameConfig != null ? gameConfig.FormatAbilitiesDetailed(hero.Abilities) : null;
            if (!string.IsNullOrEmpty(abilities))
                sb.Append(abilities);
            return sb.ToString().TrimEnd();
        }

        // Card detail shown in the Result panel. Per the detail-panel spec this shows the same
        // "who it fits / skills granted / stats changed" content as the grid card's SkillsText
        // (see ArmyUnitCardUI.RefreshSkillsText) but NOT the activation cost and NOT the card
        // type. Hero/Unit/Base also keep their own stat block.
        private string DescribeCard(CardDefinition card)
        {
            var sb = new StringBuilder();
            sb.AppendLine(card.displayName);

            switch (card.cardType)
            {
                case CardType.Hero:
                    sb.AppendLine($"Command Rating {card.commandRating}");
                    sb.AppendLine($"Fate {card.fate}");
                    sb.AppendLine($"Attack {card.attack}");
                    sb.AppendLine($"Defense {card.defenseRating}");
                    sb.AppendLine($"Range {card.range}");
                    sb.AppendLine($"HP {card.hitPoints}");
                    sb.AppendLine($"Move {card.moveMax}");
                    sb.AppendLine($"Initiative {card.initiative}");
                    break;
                case CardType.Unit:
                    sb.AppendLine($"Attack {card.attack}");
                    sb.AppendLine($"Defense {card.defenseRating}");
                    sb.AppendLine($"Range {card.range}");
                    sb.AppendLine($"HP {card.hitPoints}");
                    sb.AppendLine($"Move {card.moveMax}");
                    sb.AppendLine($"Initiative {card.initiative}");
                    break;
                case CardType.Base:
                    sb.AppendLine($"Defense {card.defenseRating}");
                    sb.AppendLine($"Resistance {card.resistanceRating}");
                    sb.AppendLine($"HP {card.hitPoints}");
                    break;
                case CardType.Facility:
                case CardType.Tactic:
                case CardType.Equipment:
                    // No stat block — the ability list below carries the meaningful content.
                    break;
            }

            // Who it fits / skills granted / stats changed. Equipment reads its grant via
            // EquipmentCardText; any other card lists its own abilities. No activation cost, no
            // card type line.
            string effect = card.cardType == CardType.Equipment
                ? EquipmentCardText.CardFace(card, gameConfig)
                : (gameConfig != null ? gameConfig.FormatAbilitiesDetailed(card.grantedAbilities) : null);
            if (!string.IsNullOrEmpty(effect))
                sb.AppendLine(effect);

            return sb.ToString().TrimEnd();
        }
    }
}
