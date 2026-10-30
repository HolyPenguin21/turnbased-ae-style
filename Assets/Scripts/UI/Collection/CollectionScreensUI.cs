using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.Map;
using Game.Styles;
using Game.Players;
using Game.Progression;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    // One collection/deck workspace living in the scene canvas; ownership and composition rules stay in
    // CollectionService/DeckRules. The hierarchy is authored (see Tools/UI/Build Collection Window);
    // cells and deck rows are pooled and updated in place instead of rebuilding the screen.
    public sealed class CollectionScreensUI : MonoBehaviour
    {
        [Header("Filters")]
        [SerializeField] private TMP_Dropdown factionDropdown;
        [SerializeField] private TMP_Dropdown categoryDropdown;
        [SerializeField] private TMP_Dropdown ownershipDropdown;
        [Header("Card grid and details")]
        [SerializeField] private ScrollRect cardsScroll;
        [SerializeField] private RectTransform gridContent;
        [SerializeField] private CollectionCardCellView cellPrefab;
        [SerializeField] private ScrollRect detailsScroll;
        // Selected card's art (Image_Card); hidden while the faction logo (Image_FactionLogo, found by name) is shown.
        [SerializeField] private Image detailsArt;
        [SerializeField] private Image detailsFactionLogo;
        // Frame around the selected card's art; hidden while the faction logo is shown instead.
        [SerializeField] private GameObject detailsCardFrame;
        [SerializeField] private TMP_Text detailsText;
        // Found by object name when not assigned.
        [SerializeField] private TMP_Text cardTitle, cardType, cardDescription;
        [SerializeField] private Button addToDeckButton;
        [Header("Deck panel")]
        [SerializeField] private TMP_Dropdown deckDropdown;
        [SerializeField] private TMP_InputField deckNameInput;
        [SerializeField] private Button createButton, starterButton, copyButton, deleteButton, saveButton, backButton;
        [SerializeField] private TMP_Text pointsLabel, statusLabel, checksLabel;
        [SerializeField] private RectTransform budgetFill;
        [SerializeField] private Image[] costIcons;
        [SerializeField] private TMP_Text[] costValues;
        [SerializeField] private ScrollRect deckScroll;
        [SerializeField] private RectTransform deckContent;
        [SerializeField] private CollectionDeckRowView rowPrefab;
        [Header("Modals")]
        [SerializeField] private GameObject unsavedModal;
        [SerializeField] private Button unsavedSaveButton, unsavedDiscardButton, unsavedCancelButton;
        [SerializeField] private GameObject confirmModal;
        [SerializeField] private TMP_Text confirmLabel;
        [SerializeField] private Button confirmAcceptButton, confirmCancelButton;

        private static readonly Color BudgetOk = new Color(.56f, .55f, .27f);
        private GameConfig config;
        private CollectionService collection;
        private Faction faction = Faction.IronConcord;
        private SavedDeck draft;
        private bool dirty, open, initialized, busy;
        private int category, ownership;
        private string status = "";
        private CardDefinition selected;
        private Action closed, pendingAction;
        private GameObject hiddenPanel, modal;
        private List<DeckSummary> deckList = new List<DeckSummary>();
        private string deckNamesKey;
        private readonly List<CollectionCardCellView> cells = new List<CollectionCardCellView>();
        private readonly List<CollectionDeckRowView> deckItems = new List<CollectionDeckRowView>();
        private readonly List<CardDefinition> visible = new List<CardDefinition>();
        private readonly Dictionary<string, int> used = new Dictionary<string, int>();
        private readonly List<string> deckNames = new List<string>();
        private static CollectionScreensUI active;
        private GridLayoutGroup cardGrid;
        private Vector2 authoredCellSize;
        private float lastGridWidth = -1f;

        private void LateUpdate()
        {
            if (open) FitCardGrid();
        }

        private void FitCardGrid()
        {
            if (gridContent == null) return;
            if (cardGrid == null)
            {
                cardGrid = gridContent.GetComponent<GridLayoutGroup>();
                if (cardGrid == null) return;
                authoredCellSize = cellPrefab != null ? ((RectTransform)cellPrefab.transform).rect.size : cardGrid.cellSize;
            }
            float width = gridContent.rect.width;
            if (width <= 0f || Mathf.Abs(width - lastGridWidth) < .01f || authoredCellSize.x <= 0f) return;
            lastGridWidth = width;
            int columns = Mathf.Max(1, cardGrid.constraintCount);
            float cellWidth = Mathf.Max(1f, (width - cardGrid.padding.horizontal -
                (columns - 1) * cardGrid.spacing.x) / columns);
            // Scale the visual as a whole inside this proportional layout cell, including fonts.
            cardGrid.cellSize = authoredCellSize * (cellWidth / authoredCellSize.x);
        }

        public void Configure(GameConfig gameConfig) { config = gameConfig; }
        // startFaction/startDeckId preselect a deck (Edit Deck from game setup); otherwise the
        // window keeps its last faction and opens that faction's default deck.
        public void Show(GameObject panel, Action onClosed = null, Faction? startFaction = null, string startDeckId = null)
        {
            if (open) return;
            if (!ProgressionContext.Initialize(config)) { ShowMessage(transform, ProgressionContext.Error); return; }
            if (active != null && active != this) return;
            gameObject.SetActive(true); transform.SetAsLastSibling();
            EnsureInitialized();
            active = this; open = true; collection = ProgressionContext.Collection;
            hiddenPanel = panel; if (hiddenPanel != null) hiddenPanel.SetActive(false); closed = onClosed;
            ownership = 0; dirty = false; selected = null;
            status = ProgressionContext.Notice ?? "";
            if (startFaction.HasValue && Array.IndexOf(DeckRules.PlayableFactions, startFaction.Value) >= 0) faction = startFaction.Value;
            draft = (startDeckId != null ? collection.GetDeck(startDeckId, faction) : null) ?? collection.DefaultDeck(faction) ?? NewDeck();
            UIFocusUtility.SetOverlay(this, true);
            collection.Changed += OnCollectionChanged;
            RefreshAll(true);
        }
        private SavedDeck NewDeck() => new SavedDeck { deckId = Guid.NewGuid().ToString("N"), name = "New Deck", faction = faction };
        private void OnCollectionChanged() { if (open && !busy) RefreshAll(); }

        private void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;
            factionDropdown.ClearOptions(); factionDropdown.AddOptions(DeckRules.PlayableFactions.Select(LabelFaction).ToList());
            categoryDropdown.ClearOptions(); categoryDropdown.AddOptions(new List<string> { "All Cards", "Heroes", "Units", "Buildings", "Equipment", "Mutators" });
            ownershipDropdown.ClearOptions(); ownershipDropdown.AddOptions(new List<string> { "All Cards", "Owned Cards", "Not Owned" });
            CollectionUIElements.ApplyCollectionScrollSkin(transform);
            factionDropdown.onValueChanged.AddListener(value => Guard(() =>
            { faction = DeckRules.PlayableFactions[value]; selected = null; draft = collection.DefaultDeck(faction) ?? NewDeck(); dirty = false; RefreshAll(true); }));
            categoryDropdown.onValueChanged.AddListener(value => { category = value; RefreshCards(true); });
            ownershipDropdown.onValueChanged.AddListener(value => { ownership = value; RefreshCards(true); });
            deckDropdown.onValueChanged.AddListener(value => Guard(() =>
            {
                var loaded = value < deckList.Count ? collection.GetDeck(deckList[value].DeckId) : null;
                if (loaded != null) { draft = loaded; dirty = false; }
                RefreshAll();
            }));
            deckNameInput.onValueChanged.AddListener(value => { draft.name = value; MarkDirty(); });
            backButton.onClick.AddListener(() => Guard(Close));
            createButton.onClick.AddListener(() => Guard(() => { draft = NewDeck(); dirty = true; RefreshAll(); }));
            starterButton.onClick.AddListener(() => Guard(() =>
            {
                var starter = collection.Starter(faction);
                var existing = collection.StarterDeck(faction);
                if (existing != null) starter.deckId = existing.deckId;
                draft = starter; dirty = true; RefreshAll();
            }));
            copyButton.onClick.AddListener(() =>
            { draft = CollectionProfile.CopyDeck(draft); draft.deckId = Guid.NewGuid().ToString("N"); draft.isStarter = false; draft.name += " Copy"; dirty = true; RefreshAll(); });
            deleteButton.onClick.AddListener(() => Confirm("Delete this deck? Unsaved changes will be discarded.", () =>
            {
                busy = true;
                try { if (collection.DeleteDeck(draft.deckId, out status)) { draft = collection.DefaultDeck(faction) ?? NewDeck(); dirty = false; } }
                finally { busy = false; }
                RefreshAll();
            }));
            saveButton.onClick.AddListener(() => { Save(); RefreshAll(); });
            unsavedSaveButton.onClick.AddListener(() =>
            {
                var action = pendingAction; CloseModal();
                if (Save()) action?.Invoke(); else { RefreshAll(); ShowMessage(transform, status); }
            });
            unsavedDiscardButton.onClick.AddListener(() => { dirty = false; var action = pendingAction; CloseModal(); action?.Invoke(); });
            unsavedCancelButton.onClick.AddListener(() => { CloseModal(); RefreshAll(); });
            confirmAcceptButton.onClick.AddListener(() => { var action = pendingAction; CloseModal(); action?.Invoke(); });
            confirmCancelButton.onClick.AddListener(CloseModal);
            var ap = config != null && config.armyUnitCardPrefab != null ? config.armyUnitCardPrefab.ActionPointBadge : null;
            for (int i = 0; i < costIcons.Length; i++)
            {
                var icon = costIcons[i];
                icon.sprite = i == 0 ? (ap != null ? ap.sprite : null) : config != null && config.resourceIconPrefab != null ? config.resourceIconPrefab.Icon : null;
                icon.color = i == 0 ? ap != null ? ap.color : Color.white : ResourceIconVisual.GetColor((ResourceType)(i - 1));
                icon.enabled = icon.sprite != null;
            }
            unsavedModal.SetActive(false); confirmModal.SetActive(false);
        }

        // Filters and faction changes start the grid from the top; deck edits keep the scroll position.
        private void RefreshAll(bool resetCardScroll = false)
        {
            if (!open) return;
            factionDropdown.SetValueWithoutNotify(Mathf.Max(0, Array.IndexOf(DeckRules.PlayableFactions, faction)));
            categoryDropdown.SetValueWithoutNotify(category);
            ownershipDropdown.SetValueWithoutNotify(ownership);
            RebuildUsed();
            RefreshCards(resetCardScroll); RefreshDetails(); RefreshDeck();
        }
        private void RefreshCards(bool resetScroll)
        {
            FitCardGrid();
            visible.Clear();
            var cards = collection.Rules.Cards(faction).Where(c =>
                (ownership == 0 || (ownership == 1 ? collection.Owned(c.authoredKey) > 0 : collection.Owned(c.authoredKey) == 0))
                && (category == 0 || category == 1 && c.cardType == CardType.Hero || category == 2 && c.cardType == CardType.Unit
                    || category == 3 && (c.cardType == CardType.Base || c.cardType == CardType.Facility)
                    || category == 4 && DeckRules.Category(c) == DeckCategory.Equipment || category == 5 && DeckRules.Category(c) == DeckCategory.Mutator));
            visible.AddRange(OrderCards(cards, c => c));
            for (int i = 0; i < visible.Count; i++)
            {
                if (i == cells.Count)
                {
                    var created = Instantiate(cellPrefab, gridContent);
                    created.Init(card => Change(card.authoredKey, 1));
                    cells.Add(created);
                }
                cells[i].gameObject.SetActive(true);
                cells[i].Bind(visible[i], config, SelectCard, CardInputAllowed);
            }
            for (int i = visible.Count; i < cells.Count; i++) cells[i].gameObject.SetActive(false);
            RefreshCardCounters();
            UpdateSelectionGlow();
            if (resetScroll) cardsScroll.verticalNormalizedPosition = 1f;
        }
        private bool CardInputAllowed() => modal == null && !CollectionMessageUI.IsShowing;
        private void RefreshCardCounters()
        {
            for (int i = 0; i < visible.Count; i++) cells[i].SetCounts(collection.Owned(visible[i].authoredKey), Used(visible[i].authoredKey),
                collection.Rules.CanAdd(draft, visible[i].authoredKey, collection.Owned));
        }
        private static int DeckSection(CardDefinition card)
        {
            int order = DeckRules.SectionOrder(card);
            return card == null || order > 4 ? 5 : order;
        }
        private static IOrderedEnumerable<T> OrderCards<T>(IEnumerable<T> rows, Func<T, CardDefinition> resolve)
            => DeckRules.OrderCards(rows, resolve);
        // The highlight frame is authored on the CollectionCard prefab ("Selection"); only toggled here.
        private void UpdateSelectionGlow()
        {
            for (int i = 0; i < visible.Count; i++) cells[i].SetSelected(selected != null && visible[i] == selected);
        }
        private void RebuildUsed()
        {
            used.Clear();
            foreach (var rows in new[] { draft.mainCards, draft.equipment, draft.mutators })
                foreach (var e in rows) used[e.cardKey] = Used(e.cardKey) + e.count;
        }
        private int Used(string key) => used.TryGetValue(key, out int n) ? n : 0;
        private void MarkDirty()
        {
            if (dirty) return;
            dirty = true; RefreshDeck();
        }
        private void Change(string key, int delta)
        { if (collection.Rules.TryChange(draft, key, delta, collection.Owned, out status)) dirty = true; RefreshDeck(); RefreshCardCounters(); }
        private void SelectCard(CardDefinition card)
        {
            if (selected == card) return;
            selected = card; RefreshDetails(); UpdateSelectionGlow();
            detailsScroll.verticalNormalizedPosition = 1f;
        }
        // Details-panel "add to deck" button: hidden without a selected card, disabled when no further copy can be added.
        private void RefreshAddButton()
        {
            if (addToDeckButton == null)
            {
                foreach (var b in GetComponentsInChildren<Button>(true))
                    if (b.name == "Button_AddToDeck") { addToDeckButton = b; break; }
                if (addToDeckButton == null) return;
                addToDeckButton.onClick.AddListener(() =>
                { if (selected != null && CardInputAllowed()) Change(selected.authoredKey, 1); });
            }
            addToDeckButton.gameObject.SetActive(selected != null);
            if (selected != null)
                addToDeckButton.interactable = collection.Rules.CanAdd(draft, selected.authoredKey, collection.Owned);
        }
        // The details scroll only scrolls if its Content grows with the text: Content and each text field must size to
        // their preferred height (a fixed-height Content leaves nothing to scroll).
        private void FitDetailsContent()
        {
            if (detailsScroll == null || detailsScroll.content == null) return;
            FitHeight(detailsScroll.content.gameObject);
            if (cardTitle != null) FitHeight(cardTitle.gameObject);
            if (cardType != null) FitHeight(cardType.gameObject);
            if (cardDescription != null) FitHeight(cardDescription.gameObject);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(detailsScroll.content);
        }
        private static void FitHeight(GameObject go)
        {
            var fitter = go.GetComponent<ContentSizeFitter>() ?? go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
        private TMP_Text FindDetailText(string objectName)
        {
            foreach (var t in GetComponentsInChildren<TMP_Text>(true))
                if (t.name == objectName) return t;
            return null;
        }
        private void RefreshDetails()
        {
            if (detailsFactionLogo == null)
                foreach (var t in GetComponentsInChildren<Image>(true))
                    if (t.name == "Image_FactionLogo") { detailsFactionLogo = t; break; }
            if (detailsCardFrame == null)
                foreach (var t in GetComponentsInChildren<Transform>(true))
                    if (t.name == "Image_CardFrame") { detailsCardFrame = t.gameObject; break; }
            // No card selected: the faction logo replaces the card art and its frame.
            if (selected == null)
            {
                var logo = collection.Rules.Starting.GetCatalog(faction)?.logo;
                if (detailsFactionLogo != null) { detailsFactionLogo.sprite = logo; detailsFactionLogo.enabled = logo != null; }
                detailsArt.gameObject.SetActive(false);
            }
            else
            {
                var sprite = selected.detailArt != null ? selected.detailArt : selected.art;
                detailsArt.gameObject.SetActive(true);
                detailsArt.sprite = sprite; detailsArt.enabled = sprite != null;
            }
            if (detailsFactionLogo != null) detailsFactionLogo.gameObject.SetActive(selected == null);
            if (detailsCardFrame != null) detailsCardFrame.SetActive(selected != null);
            RefreshAddButton();
            string title = selected == null ? LabelFaction(faction) : CollectionCardDetail.Title(selected);
            string type = selected == null ? string.Empty : CollectionCardDetail.Type(selected);
            string description = CollectionCardDetail.Description(selected, config, selected == null ? 0 : collection.Owned(selected.authoredKey));
            if (cardTitle == null) cardTitle = FindDetailText("CardTitle");
            if (cardType == null) cardType = FindDetailText("CardType");
            if (cardDescription == null) cardDescription = FindDetailText("CardDescription");
            if (cardTitle != null) cardTitle.text = title;
            if (cardType != null) cardType.text = type;
            if (cardDescription != null) cardDescription.text = description;
            FitDetailsContent();
            // Old single-label layout, until the scene has the three fields.
            if (cardDescription == null && detailsText != null)
                detailsText.text = (type.Length > 0 ? title + "\n" + type + "\n" : title + "\n") + description;
        }
        private void RefreshDeck()
        {
            RebuildUsed();
            deckList = collection.DeckSummaries(faction);
            int index = deckList.FindIndex(d => d.DeckId == draft.deckId);
            if (index < 0) { deckList.Add(new DeckSummary(draft.deckId, draft.name, draft.isStarter)); index = deckList.Count - 1; }
            deckNames.Clear();
            foreach (var d in deckList)
                deckNames.Add((d.DeckId == draft.deckId ? draft.name + (dirty ? " *" : "") : d.Name) + (d.IsStarter ? " [Starter]" : ""));
            string key = string.Join("\n", deckNames);
            if (key != deckNamesKey)
            {
                deckNamesKey = key; deckDropdown.ClearOptions(); deckDropdown.AddOptions(deckNames);
                CollectionUIElements.ConfigureDropdownScroll(deckDropdown);
            }
            deckDropdown.SetValueWithoutNotify(index); deckDropdown.RefreshShownValue();
            if (deckNameInput.text != draft.name) deckNameInput.SetTextWithoutNotify(draft.name);
            deleteButton.interactable = !draft.isStarter && !collection.IsStarter(draft.deckId);

            var validation = collection.Rules.Validate(draft, collection.Owned);
            pointsLabel.text = $"{validation.Points} / {DeckRules.MaximumPoints} points" + (dirty ? " - unsaved" : "");
            budgetFill.anchorMax = new Vector2(Mathf.Clamp01(validation.Points / (float)DeckRules.MaximumPoints), 1);
            // Keep the fill inside the new frame, including a zero-width empty budget.
            budgetFill.offsetMin = new Vector2(4f, 4f);
            budgetFill.offsetMax = new Vector2(4f - 8f * budgetFill.anchorMax.x, -4f);
            budgetFill.GetComponent<Image>().color = validation.Points > DeckRules.MaximumPoints ? Color.red : BudgetOk;
            RefreshTotalCost();
            RefreshAddButton();

            int n = 0;
            var allEntries = new[] { DeckCategory.Main, DeckCategory.Equipment, DeckCategory.Mutator }
                .SelectMany(c => DeckRules.Entries(draft, c)).Where(e => collection.Rules.Resolve(e.cardKey) != null).ToList();
            // Same section order as CollectionOrder: Buildings, Heroes, Units, Equipment, Mutators.
            string[] sections = { "Buildings", "Heroes", "Units", "Equipment", "Mutators", "Other" };
            for (int s = 0; s < sections.Length; s++)
            {
                var entries = allEntries.Where(e => DeckSection(collection.Rules.Resolve(e.cardKey)) == s).ToList();
                if (entries.Count == 0) continue;
                DeckItem(n++).SetHeader(sections[s]);
                foreach (var entry in OrderCards(entries, e => collection.Rules.Resolve(e.cardKey)))
                {
                    var card = collection.Rules.Resolve(entry.cardKey);
                    DeckItem(n++).SetEntry(card, entry.cardKey, entry.count,
                        card != null && collection.Rules.Permitted(card, faction) && Used(entry.cardKey) < Math.Min(collection.Owned(entry.cardKey), DeckRules.CopyLimit(card)));
                }
            }
            for (int i = n; i < deckItems.Count; i++) deckItems[i].gameObject.SetActive(false);
            checksLabel.text = string.Join("\n", validation.Errors.Concat(validation.Warnings));
            // An empty TMP label still contributes line height and spacing to the deck list.
            checksLabel.gameObject.SetActive(!string.IsNullOrWhiteSpace(checksLabel.text));
            checksLabel.transform.SetAsLastSibling();
            statusLabel.text = status ?? "";
        }
        private CollectionDeckRowView DeckItem(int index)
        {
            if (index == deckItems.Count)
            {
                var created = Instantiate(rowPrefab, deckContent);
                created.Init(SelectCard, Change);
                deckItems.Add(created);
            }
            var item = deckItems[index]; item.gameObject.SetActive(true);
            return item;
        }
        private long[] TotalCost()
        {
            var total = new long[5]; // AP, Human, Energy, Materials, Tech — existing card badge order.
            foreach (var entry in draft.mainCards.Concat(draft.equipment).Concat(draft.mutators))
            {
                var card = collection.Rules.Resolve(entry.cardKey);
                if (card == null) continue;
                long count = entry.count;
                total[0] += count * card.apCost;
                total[1] += count * (card.resourceCost?.human ?? 0);
                total[2] += count * (card.resourceCost?.energy ?? 0);
                total[3] += count * (card.resourceCost?.materials ?? 0);
                total[4] += count * (card.resourceCost?.tech ?? 0);
            }
            return total;
        }
        private void RefreshTotalCost()
        {
            var total = TotalCost();
            for (int i = 0; i < total.Length && i < costValues.Length; i++) costValues[i].text = total[i].ToString();
        }

        private bool Save()
        {
            busy = true;
            bool ok;
            try { ok = collection.SaveDeck(draft, out status); } finally { busy = false; }
            if (ok) { dirty = false; status = "Deck saved."; }
            return ok;
        }
        private void Guard(Action action)
        {
            if (!dirty) { action(); return; }
            pendingAction = action; ShowModal(unsavedModal);
        }
        private void Confirm(string text, Action action)
        {
            pendingAction = action; confirmLabel.text = text; ShowModal(confirmModal);
        }
        private void ShowModal(GameObject window)
        { modal = window; window.SetActive(true); window.transform.SetAsLastSibling(); }
        private void CloseModal()
        { if (modal != null) modal.SetActive(false); modal = null; pendingAction = null; }
        private void Update()
        {
            if (!open || CollectionMessageUI.IsShowing || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return;
            if (modal != null) { CloseModal(); RefreshAll(); } else Guard(Close);
        }
        private void Close()
        {
            collection.Changed -= OnCollectionChanged; UIFocusUtility.SetOverlay(this, false);
            open = false; active = null; gameObject.SetActive(false);
            if (hiddenPanel != null) hiddenPanel.SetActive(true);
            closed?.Invoke();
        }
        private void OnDestroy()
        {
            if (collection != null) collection.Changed -= OnCollectionChanged;
            UIFocusUtility.SetOverlay(this, false);
            if (active == this) active = null;
        }
        private static string LabelFaction(Faction f) => f == Faction.IronConcord ? "Iron Concord" : f == Faction.Ashen ? "The Ashen" : "The Vessels";
        public static void ShowMessage(Transform parent, string message) => CollectionMessageUI.Show(message);
    }
}



