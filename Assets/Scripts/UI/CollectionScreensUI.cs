using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Players;
using Game.Progression;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    // One collection/deck workspace; ownership and composition rules stay in CollectionService/DeckRules.
    public sealed class CollectionScreensUI : MonoBehaviour
    {
        private GameConfig config;
        private CollectionService collection;
        private RectTransform canvas, root, details, grid, deckPanel, deckCards;
        private TMP_Dropdown dropdownTemplate;
        private TMP_InputField inputTemplate;
        private Faction faction = Faction.IronConcord;
        private SavedDeck draft;
        private bool dirty;
        private int category, ownership, sort;
        private string search = "", status = "";
        private CardDefinition selected;
        private Action closed;
        private GameObject hiddenPanel;
        private RectTransform modal;
        private static CollectionScreensUI active;

        public void Configure(GameConfig gameConfig, TMP_Dropdown dropdown, TMP_InputField input)
        { config = gameConfig; dropdownTemplate = dropdown; inputTemplate = input; }
        public void Show(GameObject panel, Action onClosed = null)
        {
            if (root != null) return;
            if (!ProgressionContext.Initialize(config)) { ShowMessage(transform, ProgressionContext.Error); return; }
            if (active != null && active != this) return;
            active = this; collection = ProgressionContext.Collection;
            hiddenPanel = panel; hiddenPanel?.SetActive(false); closed = onClosed;
            ownership = 0; dirty = false;
            status = ProgressionContext.Notice ?? "";
            draft = collection.DefaultDeck(faction) ?? NewDeck();
            canvas = CollectionUIElements.Canvas("CollectionCanvas", new Vector2(1920, 1080));
            root = CollectionUIElements.Panel(canvas, "CollectionAndDecks"); CollectionUIElements.Stretch(root);
            UIFocusUtility.SetOverlay(this, true);
            collection.Changed += OnCollectionChanged;
            Draw();
        }
        private SavedDeck NewDeck() => new SavedDeck { deckId = Guid.NewGuid().ToString("N"), name = "New Deck", faction = faction };
        private void OnCollectionChanged() { if (root != null) Draw(); }
        private void Draw(bool preserveScroll = true)
        {
            if (root == null) return;
            float description = preserveScroll && details != null ? details.GetComponentInParent<ScrollRect>().verticalNormalizedPosition : 1f;
            float cards = preserveScroll && grid != null ? grid.GetComponentInParent<ScrollRect>().verticalNormalizedPosition : 1f;
            float rows = preserveScroll && deckCards != null ? deckCards.GetComponentInParent<ScrollRect>().verticalNormalizedPosition : 1f;
            CollectionUIElements.Clear(root);
            CollectionUIElements.Label(root, "COLLECTION & DECKS", 24, 18, 1000, 48, 30);
            CollectionUIElements.Button(root, "Back", 1746, 20, 150, 44, () => Guard(Close), 20);
            var filters = CollectionUIElements.Panel(root, "Filters"); CollectionUIElements.Place(filters, 24, 88, 360, 266);
            Dropdown(filters, DeckRules.PlayableFactions.Select(LabelFaction).ToList(), Array.IndexOf(DeckRules.PlayableFactions, faction), 14, 10, 332, value => Guard(() =>
            { faction = DeckRules.PlayableFactions[value]; selected = null; draft = collection.DefaultDeck(faction) ?? NewDeck(); dirty = false; Draw(false); }));
            Dropdown(filters, new List<string> { "All Cards", "Heroes", "Units", "Buildings", "Equipment", "Mutators" }, category, 14, 60, 332, value => { category = value; RefreshCards(); });
            Dropdown(filters, new List<string> { "Все", "Мои карты", "Не получены" }, ownership, 14, 110, 332, value => { ownership = value; RefreshCards(); });
            Dropdown(filters, new List<string> { "Name", "Points", "Type", "Owned" }, sort, 14, 160, 332, value => { sort = value; RefreshCards(); });
            Input(filters, search, 14, 210, 332, value => { search = value; RefreshCards(); }, "Search cards");
            details = CollectionUIElements.Scroll(root, "CardDetails", 24, 370, 360, 638);
            grid = CollectionUIElements.Scroll(root, "Cards", 408, 88, 936, 920);
            deckPanel = CollectionUIElements.Panel(root, "Deck"); CollectionUIElements.Place(deckPanel, 1368, 88, 528, 920);
            RefreshCards(); RefreshDetails(); RefreshDeck();
            CollectionUIElements.Label(root, status ?? "", 408, 1020, 1488, 48, 18);
            Canvas.ForceUpdateCanvases();
            details.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = description;
            grid.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = cards;
            deckCards.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = rows;
        }
        private void RefreshCards()
        {
            CollectionUIElements.Clear(grid);
            var cards = collection.Rules.Cards(faction).Where(c =>
                (search.Length == 0 || c.displayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                && (ownership == 0 || (ownership == 1 ? collection.Owned(c.authoredKey) > 0 : collection.Owned(c.authoredKey) == 0))
                && (category == 0 || category == 1 && c.cardType == CardType.Hero || category == 2 && c.cardType == CardType.Unit
                    || category == 3 && (c.cardType == CardType.Base || c.cardType == CardType.Facility)
                    || category == 4 && DeckRules.Category(c) == DeckCategory.Equipment || category == 5 && DeckRules.Category(c) == DeckCategory.Mutator));
            cards = sort == 1 ? cards.OrderBy(c => c.deckPointCost).ThenBy(c => c.displayName)
                : sort == 2 ? cards.OrderBy(c => DeckRules.Category(c)).ThenBy(c => c.cardType).ThenBy(c => c.displayName)
                : sort == 3 ? cards.OrderByDescending(c => collection.Owned(c.authoredKey)).ThenBy(c => c.displayName) : cards.OrderBy(c => c.displayName);
            int index = 0;
            foreach (var card in cards)
            {
                float x = index % 4 * 226, y = index / 4 * 310;
                var cell = CollectionUIElements.Panel(grid, card.authoredKey); CollectionUIElements.Place(cell, x, y, 216, 300);
                var view = Instantiate(config.armyUnitCardPrefab, cell); view.SetupPreview(card, config, SelectCard,
                    () => modal == null && !CollectionMessageUI.IsShowing,
                    SelectCard);
                var rt = (RectTransform)view.transform;
                float scale = Mathf.Min(208f / Mathf.Max(1, rt.rect.width), 232f / Mathf.Max(1, rt.rect.height)); rt.localScale = Vector3.one * scale;
                rt.anchorMin = rt.anchorMax = new Vector2(.5f, 1); rt.pivot = new Vector2(.5f, 1); rt.anchoredPosition = Vector2.zero;
                int owned = collection.Owned(card.authoredKey);
                view.gameObject.AddComponent<CanvasGroup>().alpha = owned == 0 ? .4f : 1;
                CollectionUIElements.Label(cell, $"{owned}/{card.deckCopyLimit}   {card.deckPointCost} pt" + (owned >= card.deckCopyLimit ? " MAX" : ""), 4, 238, 208, 26, 18);
                CollectionUIElements.Button(cell, "+  In deck: " + Used(card.authoredKey), 4, 270, 208, 30, () => Change(card.authoredKey, 1), 20);
                index++;
            }
            grid.sizeDelta = new Vector2(grid.sizeDelta.x, Mathf.Max(1, (index + 3) / 4 * 310));
            grid.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = 1f;
        }
        private int Used(string key) => draft == null ? 0 : draft.mainCards.Concat(draft.equipment).Concat(draft.mutators).Where(e => e.cardKey == key).Sum(e => e.count);
        private void Change(string key, int delta)
        { if (collection.Rules.TryChange(draft, key, delta, collection.Owned, out status)) dirty = true; Draw(); }
        private void SelectCard(CardDefinition card)
        {
            if (selected == card) return;
            selected = card; RefreshDetails();
            Canvas.ForceUpdateCanvases();
            details.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = 1f;
        }
        private void RefreshDetails()
        {
            CollectionUIElements.Clear(details);
            float y = 12;
            if (selected != null)
            {
                var art = CollectionUIElements.Rect(details, "Art"); CollectionUIElements.Place(art, 12, y, 322, 280);
                var image = art.gameObject.AddComponent<Image>(); image.sprite = selected.detailArt ?? selected.art;
                image.preserveAspect = true; image.raycastTarget = false; y += 292;
            }
            string text = CollectionCardDetail.Describe(selected, config, selected == null ? 0 : collection.Owned(selected.authoredKey));
            var label = CollectionUIElements.Label(details, text, 12, y, 322, 1, 20);
            float height = label.GetPreferredValues(text, 322, 10000).y;
            ((RectTransform)label.transform).sizeDelta = new Vector2(322, height);
            details.sizeDelta = new Vector2(details.sizeDelta.x, y + height + 12);
        }
        private void RefreshDeck()
        {
            var decks = collection.Snapshot.savedDecks.Where(d => d.faction == faction).ToList();
            int index = decks.FindIndex(d => d.deckId == draft.deckId);
            if (index < 0) { decks.Add(CollectionProfile.CopyDeck(draft)); index = decks.Count - 1; }
            var names = decks.Select(d => d.deckId == draft.deckId ? draft.name + (dirty ? " *" : "") : d.name).ToList();
            Dropdown(deckPanel, names, index, 14, 10, 500, value => Guard(() =>
            { draft = CollectionProfile.CopyDeck(decks[value]); dirty = false; Draw(false); }));
            CollectionUIElements.Button(deckPanel, "Create", 14, 64, 244, 44, () => Guard(() => { draft = NewDeck(); dirty = true; Draw(false); }), 20);
            CollectionUIElements.Button(deckPanel, "Delete", 270, 64, 244, 44, () => Guard(() => Confirm("Delete this deck?", () =>
            { if (collection.DeleteDeck(draft.deckId, out status)) { draft = collection.DefaultDeck(faction) ?? NewDeck(); dirty = false; } Draw(false); })), 20);
            Input(deckPanel, draft.name, 14, 118, 500, value => { draft.name = value; dirty = true; }, "Deck name");
            CollectionUIElements.Button(deckPanel, "Starter", 14, 172, 244, 40, () => Guard(() => { draft = collection.Starter(faction); dirty = true; Draw(false); }), 20);
            CollectionUIElements.Button(deckPanel, "Copy", 270, 172, 244, 40, () =>
            { draft = CollectionProfile.CopyDeck(draft); draft.deckId = Guid.NewGuid().ToString("N"); draft.name += " Copy"; dirty = true; Draw(false); }, 20);
            CollectionUIElements.Button(deckPanel, "Save Deck", 14, 222, 500, 44, () => { Save(); Draw(); }, 20);
            var validation = collection.Rules.Validate(draft, collection.Owned);
            CollectionUIElements.Label(deckPanel, $"{validation.Points} / {DeckRules.MaximumPoints} points" + (dirty ? " — unsaved" : ""), 14, 278, 500, 30, 22);
            var bar = CollectionUIElements.Panel(deckPanel, "Budget"); CollectionUIElements.Place(bar, 14, 316, 500, 8);
            var fill = CollectionUIElements.Panel(bar, "Fill"); CollectionUIElements.Place(fill, 0, 0, 500 * Mathf.Clamp01(validation.Points / (float)DeckRules.MaximumPoints), 8);
            fill.GetComponent<Image>().color = validation.Points > 100 ? Color.red : new Color(.36f, .55f, .3f);
            CollectionUIElements.Label(deckPanel, "Cards", 14, 340, 390, 28, 20);
            CollectionUIElements.Label(deckPanel, "Count", 424, 340, 90, 28, 20);
            deckCards = CollectionUIElements.Scroll(deckPanel, "DeckCards", 14, 378, 500, 528);
            float y = 0;
            foreach (var c in new[] { DeckCategory.Main, DeckCategory.Equipment, DeckCategory.Mutator })
            {
                var entries = DeckRules.Entries(draft, c);
                if (entries.Count == 0) continue;
                CollectionUIElements.Label(deckCards, c.ToString(), 0, y, 486, 28, 18); y += 32;
                foreach (var entry in entries)
                {
                    var card = collection.Rules.Resolve(entry.cardKey); string key = entry.cardKey;
                    var row = CollectionUIElements.Panel(deckCards, key); CollectionUIElements.Place(row, 0, y, 486, 78);
                    var thumbnail = CollectionUIElements.Rect(row, "Art"); CollectionUIElements.Place(thumbnail, 4, 4, 52, 70);
                    var image = thumbnail.gameObject.AddComponent<Image>(); image.sprite = card?.art; image.preserveAspect = true; image.raycastTarget = false;
                    CollectionUIElements.Button(row, card?.displayName ?? key, 64, 4, 276, 40, () => SelectCard(card), 20);
                    CollectionUIElements.Label(row, $"{(long)(card?.deckPointCost ?? 0) * entry.count} pt", 69, 48, 230, 24, 18);
                    CollectionUIElements.Button(row, "−", 352, 24, 34, 34, () => Change(key, -1), 20);
                    var count = CollectionUIElements.Label(row, entry.count.ToString(), 390, 24, 40, 34, 22); count.alignment = TextAlignmentOptions.Center;
                    CollectionUIElements.Button(row, "+", 434, 24, 34, 34, () => Change(key, 1), 20);
                    CollectionUIElements.Button(row, "×", 310, 48, 30, 26, () => RemoveEntry(key), 20); y += 84;
                }
            }
            string checks = string.Join("\n", validation.Errors.Concat(validation.Warnings));
            var info = CollectionUIElements.Label(deckCards, checks, 0, y, 486, 1, 18);
            float h = info.GetPreferredValues(checks, 486, 10000).y; ((RectTransform)info.transform).sizeDelta = new Vector2(486, h);
            deckCards.sizeDelta = new Vector2(deckCards.sizeDelta.x, y + h + 12);
        }
        private void RemoveEntry(string key)
        { foreach (var rows in new[] { draft.mainCards, draft.equipment, draft.mutators }) rows.RemoveAll(e => e.cardKey == key); dirty = true; Draw(); }
        private bool Save() { bool ok = collection.SaveDeck(draft, out status); if (ok) { dirty = false; status = "Deck saved."; } return ok; }
        private void Guard(Action action)
        {
            if (!dirty) { action(); return; }
            modal = CollectionUIElements.Panel(canvas, "Unsaved"); CollectionUIElements.Stretch(modal);
            CollectionUIElements.Label(modal, "Unsaved changes", 660, 420, 600, 48, 28);
            CollectionUIElements.Button(modal, "Save", 660, 492, 180, 48, () => { if (Save()) { CloseModal(); action(); } else { CloseModal(); Draw(); ShowMessage(canvas, status); } }, 20);
            CollectionUIElements.Button(modal, "Discard", 870, 492, 180, 48, () => { dirty = false; CloseModal(); action(); }, 20);
            CollectionUIElements.Button(modal, "Cancel", 1080, 492, 180, 48, () => { CloseModal(); Draw(); }, 20);
        }
        private void Confirm(string text, Action action)
        {
            modal = CollectionUIElements.Panel(canvas, "Confirm"); CollectionUIElements.Stretch(modal);
            CollectionUIElements.Label(modal, text, 660, 420, 600, 48, 28);
            CollectionUIElements.Button(modal, "Delete", 660, 492, 285, 48, () => { CloseModal(); action(); }, 20);
            CollectionUIElements.Button(modal, "Cancel", 975, 492, 285, 48, CloseModal, 20);
        }
        private void CloseModal() { if (modal != null) Destroy(modal.gameObject); modal = null; }
        private void Update() { if (CollectionMessageUI.IsShowing || root == null || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return; if (modal != null) { CloseModal(); Draw(); } else Guard(Close); }
        private void Close()
        {
            collection.Changed -= OnCollectionChanged; UIFocusUtility.SetOverlay(this, false);
            Destroy(canvas.gameObject); root = null; canvas = null; active = null;
            hiddenPanel?.SetActive(true); closed?.Invoke();
        }
        private void OnDestroy() { if (collection != null) collection.Changed -= OnCollectionChanged; UIFocusUtility.SetOverlay(this, false); if (canvas != null) Destroy(canvas.gameObject); if (active == this) active = null; }
        private static string LabelFaction(Faction f) => f == Faction.IronConcord ? "Iron Concord" : f == Faction.Ashen ? "The Ashen" : "The Vessels";
        private TMP_Dropdown Dropdown(Transform parent, List<string> options, int value, float x, float y, float w, Action<int> changed)
        {
            var dropdown = Instantiate(dropdownTemplate, parent); CollectionUIElements.Place((RectTransform)dropdown.transform, x, y, w, 44);
            if (dropdown.captionText != null) dropdown.captionText.fontSize = 20;
            if (dropdown.itemText != null) dropdown.itemText.fontSize = 18;
            dropdown.onValueChanged.RemoveAllListeners(); dropdown.ClearOptions(); dropdown.AddOptions(options); dropdown.SetValueWithoutNotify(Mathf.Max(0, value)); dropdown.onValueChanged.AddListener(i => changed(i)); return dropdown;
        }
        private void Input(Transform parent, string value, float x, float y, float w, Action<string> changed, string placeholder)
        {
            var input = Instantiate(inputTemplate, parent); CollectionUIElements.Place((RectTransform)input.transform, x, y, w, 44);
            if (input.textComponent != null) input.textComponent.fontSize = 20;
            if (input.placeholder is TMP_Text hint) { hint.text = placeholder; hint.fontSize = 20; }
            input.onValueChanged.RemoveAllListeners(); input.onEndEdit.RemoveAllListeners(); input.SetTextWithoutNotify(value); input.onValueChanged.AddListener(s => changed(s));
        }
        public static void ShowMessage(Transform parent, string message) => CollectionMessageUI.Show(message);
    }
}
