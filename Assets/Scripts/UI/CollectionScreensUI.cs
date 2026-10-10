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
        [SerializeField] private Image detailsArt;
        [SerializeField] private TMP_Text detailsText;
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

        private static readonly Color BudgetOk = new Color(.36f, .55f, .3f);
        private GameConfig config;
        private CollectionService collection;
        private Faction faction = Faction.IronConcord;
        private SavedDeck draft;
        private bool dirty, open, initialized, busy;
        private int category, ownership;
        private string status = "";
        private CardDefinition selected;
        private UIRaggedGlowUI selectionGlow;
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

        public void Configure(GameConfig gameConfig) { config = gameConfig; }
        public void Show(GameObject panel, Action onClosed = null)
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
            draft = collection.DefaultDeck(faction) ?? NewDeck();
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
            for (int i = 0; i < visible.Count; i++) cells[i].SetCounts(collection.Owned(visible[i].authoredKey), Used(visible[i].authoredKey));
        }
        private static int CollectionOrder(CardDefinition card)
            => card == null ? 5 : card.cardType == CardType.Base || card.cardType == CardType.Facility ? 0
                : card.cardType == CardType.Hero ? 1 : card.cardType == CardType.Unit ? 2
                : DeckRules.Category(card) == DeckCategory.Equipment ? 3 : 4;
        private static IOrderedEnumerable<T> OrderCards<T>(IEnumerable<T> rows, Func<T, CardDefinition> resolve)
            => rows.OrderBy(row => CollectionOrder(resolve(row)))
                .ThenBy(row => resolve(row)?.deckPointCost ?? 0)
                .ThenBy(row => resolve(row)?.displayName ?? "", StringComparer.Ordinal);
        private void UpdateSelectionGlow()
        {
            CollectionCardCellView cell = null;
            if (selected != null)
                for (int i = 0; i < visible.Count; i++) if (visible[i] == selected) { cell = cells[i]; break; }
            if (cell == null) { if (selectionGlow != null) selectionGlow.Hide(); return; }
            if (selectionGlow == null)
            {
                var go = new GameObject("SelectedCardGlow", typeof(RectTransform));
                go.AddComponent<Image>().raycastTarget = false;
                selectionGlow = go.AddComponent<UIRaggedGlowUI>();
            }
            var glowRect = (RectTransform)selectionGlow.transform;
            glowRect.SetParent(cell.CardHost, false);
            glowRect.anchorMin = glowRect.anchorMax = new Vector2(.5f, .5f);
            glowRect.pivot = new Vector2(.5f, .5f);
            glowRect.anchoredPosition = Vector2.zero;
            selectionGlow.ApplyStyle(config.battleActingUnitHighlightStyle);
            selectionGlow.SetColor(TechnicalColors.BattleActingUnit);
            selectionGlow.ShowAt(cell.CardHost.rect.size);
            glowRect.SetAsLastSibling();
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
        private void RefreshDetails()
        {
            var sprite = selected == null ? collection.Rules.Starting.GetCatalog(faction)?.logo
                : selected.detailArt != null ? selected.detailArt : selected.art;
            detailsArt.sprite = sprite; detailsArt.enabled = sprite != null;
            detailsText.text = selected == null ? LabelFaction(faction) + "\nSelect a card."
                : CollectionCardDetail.Describe(selected, config, collection.Owned(selected.authoredKey));
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
            { deckNamesKey = key; deckDropdown.ClearOptions(); deckDropdown.AddOptions(deckNames); }
            deckDropdown.SetValueWithoutNotify(index); deckDropdown.RefreshShownValue();
            if (deckNameInput.text != draft.name) deckNameInput.SetTextWithoutNotify(draft.name);
            deleteButton.interactable = !draft.isStarter && !collection.IsStarter(draft.deckId);

            var validation = collection.Rules.Validate(draft, collection.Owned);
            pointsLabel.text = $"{validation.Points} / {DeckRules.MaximumPoints} points" + (dirty ? " — unsaved" : "");
            budgetFill.anchorMax = new Vector2(Mathf.Clamp01(validation.Points / (float)DeckRules.MaximumPoints), 1);
            budgetFill.GetComponent<Image>().color = validation.Points > DeckRules.MaximumPoints ? Color.red : BudgetOk;
            RefreshTotalCost();

            int n = 0;
            foreach (var c in new[] { DeckCategory.Main, DeckCategory.Equipment, DeckCategory.Mutator })
            {
                var entries = DeckRules.Entries(draft, c);
                if (entries.Count == 0) continue;
                DeckItem(n++).SetHeader(c.ToString());
                foreach (var entry in OrderCards(entries, e => collection.Rules.Resolve(e.cardKey)))
                {
                    var card = collection.Rules.Resolve(entry.cardKey);
                    DeckItem(n++).SetEntry(card, entry.cardKey, entry.count,
                        card != null && collection.Rules.Permitted(card, faction) && Used(entry.cardKey) < Math.Min(collection.Owned(entry.cardKey), card.deckCopyLimit));
                }
            }
            for (int i = n; i < deckItems.Count; i++) deckItems[i].gameObject.SetActive(false);
            checksLabel.text = string.Join("\n", validation.Errors.Concat(validation.Warnings));
            checksLabel.transform.SetAsLastSibling();
            statusLabel.text = status ?? "";
        }
        private CollectionDeckRowView DeckItem(int index)
        {
            if (index == deckItems.Count)
            {
                var created = Instantiate(rowPrefab, deckContent);
                created.Init(SelectCard, Change, RemoveEntry);
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

        private void RemoveEntry(string key)
        {
            foreach (var rows in new[] { draft.mainCards, draft.equipment, draft.mutators }) rows.RemoveAll(e => e.cardKey == key);
            dirty = true; RefreshDeck(); RefreshCardCounters();
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
