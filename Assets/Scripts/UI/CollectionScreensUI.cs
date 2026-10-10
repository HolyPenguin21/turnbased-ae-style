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
    // Collection browser and deck editor share presentation, never independent ownership rules.
    // The browser and editor are separate views of the local profile and a working deck copy.
    public sealed class CollectionScreensUI : MonoBehaviour
    {
        private GameConfig config;
        private CollectionService collection;
        private RectTransform canvas, root, left, grid, right;
        private TMP_Dropdown dropdownTemplate;
        private TMP_InputField inputTemplate;
        private Faction faction = Faction.IronConcord;
        private SavedDeck draft;
        private bool editing, dirty;
        private int category, ownership, sort;
        private string search = "", status = "";
        private CardDefinition selected;
        private Action closed;
        private GameObject hiddenPanel;
        private RectTransform modal;
        private static CollectionScreensUI active;

        public void Configure(GameConfig gameConfig, TMP_Dropdown dropdown, TMP_InputField input)
        { config = gameConfig; dropdownTemplate = dropdown; inputTemplate = input; }
        public void Show(bool builder, GameObject panel, Action onClosed = null)
        {
            if (root != null) return;
            if (!ProgressionContext.Initialize(config)) { ShowMessage(transform, ProgressionContext.Error); return; }
            if (active != null && active != this) return;
            active = this; collection = ProgressionContext.Collection;
            hiddenPanel = panel; hiddenPanel?.SetActive(false); closed = onClosed;
            editing = builder; ownership = builder ? 1 : 0; dirty = false;
            status = ProgressionContext.Notice ?? "";
            draft = builder ? collection.DefaultDeck(faction) : null;
            if (builder && draft == null) draft = NewDeck();
            canvas = CollectionUIElements.Canvas("CollectionCanvas");
            root = CollectionUIElements.Panel(canvas, builder ? "DeckBuilder" : "Collection"); CollectionUIElements.Stretch(root);
            UIFocusUtility.SetOverlay(this, true);
            collection.Changed += OnCollectionChanged;
            Draw();
        }
        private SavedDeck NewDeck() => new SavedDeck { deckId = Guid.NewGuid().ToString("N"), name = "New Deck", faction = faction };
        private void OnCollectionChanged() { if (root != null) Draw(); }
        private void Draw(bool preserveScroll = true)
        {
            if (root == null) return;
            float navigation = preserveScroll && left != null ? left.GetComponentInParent<ScrollRect>().verticalNormalizedPosition : 1f;
            float cards = preserveScroll && grid != null ? grid.GetComponentInParent<ScrollRect>().verticalNormalizedPosition : 1f;
            float details = preserveScroll && right != null ? right.GetComponentInParent<ScrollRect>().verticalNormalizedPosition : 1f;
            CollectionUIElements.Clear(root);
            CollectionUIElements.Label(root, editing ? "DECK BUILDER" : "COLLECTION", 24, 12, 500, 40, 28);
            CollectionUIElements.Button(root, "Back", 890, 16, 110, 34, () => Guard(Close));
            left = CollectionUIElements.Scroll(root, "Navigation", 16, 66, 205, 628);
            grid = CollectionUIElements.Scroll(root, "Cards", 232, 164, 494, 530);
            right = CollectionUIElements.Scroll(root, "Details", 738, 66, 270, 628);
            var f = Dropdown(left, DeckRules.PlayableFactions.Select(LabelFaction).ToList(), Array.IndexOf(DeckRules.PlayableFactions, faction), 0, 0, 186, value => Guard(() =>
            { faction = DeckRules.PlayableFactions[value]; selected = null; draft = editing ? collection.DefaultDeck(faction) ?? NewDeck() : null; Draw(false); }));
            float y = 45;
            if (editing)
            {
                foreach (var deck in collection.Snapshot.savedDecks.Where(d => d.faction == faction))
                {
                    var copy = deck;
                    CollectionUIElements.Button(left, deck.name, 0, y, 186, 32, () => Guard(() => { draft = copy; dirty = false; Draw(false); })); y += 37;
                }
                CollectionUIElements.Button(left, "New", 0, y, 90, 30, () => Guard(() => { draft = NewDeck(); dirty = true; Draw(); }));
                CollectionUIElements.Button(left, "Starter", 96, y, 90, 30, () => Guard(() => { draft = collection.Starter(faction); dirty = true; Draw(); })); y += 38;
                CollectionUIElements.Button(left, "Copy", 0, y, 90, 30, () => { draft = CollectionProfile.CopyDeck(draft); draft.deckId = Guid.NewGuid().ToString("N"); draft.name += " Copy"; dirty = true; Draw(); });
                CollectionUIElements.Button(left, "Delete", 96, y, 90, 30, () => Guard(() => Confirm("Delete this deck?", () =>
                { if (collection.DeleteDeck(draft.deckId, out status)) { draft = collection.DefaultDeck(faction) ?? NewDeck(); dirty = false; } Draw(); }))); y += 38;
                Input(left, draft.name, 0, y, 186, value => { draft.name = value; dirty = true; }); y += 44;
            }
            string[] categories = { "All Cards", "Heroes", "Units", "Buildings", "Equipment", "Mutators" };
            for (int i = 0; i < categories.Length; i++) { int index = i; CollectionUIElements.Button(left, categories[i], 0, y, 186, 32, () => { category = index; Draw(false); }); y += 38; }
            left.sizeDelta = new Vector2(left.sizeDelta.x, y);
            Input(root, search, 232, 70, 260, value => { search = value; RefreshCards(); });
            Dropdown(root, new List<string> { "Все", "Мои карты", "Не получены" }, ownership, 500, 70, 226, value => { ownership = value; RefreshCards(); });
            Dropdown(root, new List<string> { "Name", "Points", "Type", "Owned" }, sort, 232, 116, 494, value => { sort = value; RefreshCards(); });
            CollectionUIElements.Label(root, status ?? "", 232, 704, 770, 52, 15);
            RefreshCards(); RefreshRight();
            Canvas.ForceUpdateCanvases();
            left.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = navigation;
            grid.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = cards;
            right.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = details;
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
                float x = index % 3 * 157, y = index / 3 * 232;
                var cell = CollectionUIElements.Panel(grid, card.authoredKey); CollectionUIElements.Place(cell, x, y, 150, 224);
                var view = Instantiate(config.armyUnitCardPrefab, cell); view.SetupPreview(card, config, c => { selected = c; if (editing) DetailModal(c); else RefreshRight(); },
                    () => modal == null && !CollectionMessageUI.IsShowing,
                    c => { selected = c; if (!editing) RefreshRight(); });
                var rt = (RectTransform)view.transform;
                float scale = Mathf.Min(142f / Mathf.Max(1, rt.rect.width), 160f / Mathf.Max(1, rt.rect.height)); rt.localScale = Vector3.one * scale;
                rt.anchorMin = rt.anchorMax = new Vector2(.5f, 1); rt.pivot = new Vector2(.5f, 1); rt.anchoredPosition = Vector2.zero;
                int owned = collection.Owned(card.authoredKey);
                view.gameObject.AddComponent<CanvasGroup>().alpha = owned == 0 ? .4f : 1;
                CollectionUIElements.Label(cell, $"{owned}/{card.deckCopyLimit}   {card.deckPointCost} pt" + (owned >= card.deckCopyLimit ? " MAX" : ""), 4, 166, 142, 26, 14);
                if (editing) CollectionUIElements.Button(cell, "+  Used " + Used(card.authoredKey), 4, 195, 142, 26, () => Change(card.authoredKey, 1));
                index++;
            }
            grid.sizeDelta = new Vector2(grid.sizeDelta.x, Mathf.Max(1, (index + 2) / 3 * 232));
        }
        private int Used(string key) => draft == null ? 0 : draft.mainCards.Concat(draft.equipment).Concat(draft.mutators).Where(e => e.cardKey == key).Sum(e => e.count);
        private void Change(string key, int delta)
        { if (collection.Rules.TryChange(draft, key, delta, collection.Owned, out status)) dirty = true; Draw(); }
        private void RefreshRight()
        {
            CollectionUIElements.Clear(right);
            if (!editing)
            {
                if (selected != null)
                {
                    var art = CollectionUIElements.Rect(right, "Art"); CollectionUIElements.Place(art, 0, 0, 244, 250);
                    var image = art.gameObject.AddComponent<Image>(); image.sprite = selected.detailArt ?? selected.art; image.preserveAspect = true;
                }
                var text = CollectionCardDetail.Describe(selected, config, selected == null ? 0 : collection.Owned(selected.authoredKey));
                var label = CollectionUIElements.Label(right, text, 0, 260, 244, 1, 16);
                float height = Mathf.Max(300, label.GetPreferredValues(text, 244, 10000).y);
                ((RectTransform)label.transform).sizeDelta = new Vector2(244, height);
                CollectionUIElements.Button(right, "My Decks", 0, 270 + height, 244, 34, () => { editing = true; ownership = 1; draft = collection.DefaultDeck(faction) ?? NewDeck(); Draw(false); });
                right.sizeDelta = new Vector2(244, 315 + height); return;
            }
            var validation = collection.Rules.Validate(draft, collection.Owned);
            CollectionUIElements.Label(right, $"{draft.name}\n{validation.Points} / 100", 0, 0, 244, 58, 20);
            var bar = CollectionUIElements.Panel(right, "Budget"); CollectionUIElements.Place(bar, 0, 62, 244, 8);
            var fill = CollectionUIElements.Panel(bar, "Fill"); CollectionUIElements.Place(fill, 0, 0, 244 * Mathf.Clamp01(validation.Points / 100f), 8);
            fill.GetComponent<Image>().color = validation.Points > 100 ? Color.red : new Color(.36f, .55f, .3f);
            float y = 80;
            foreach (var c in new[] { DeckCategory.Main, DeckCategory.Equipment, DeckCategory.Mutator })
            {
                CollectionUIElements.Label(right, c.ToString(), 0, y, 244, 25); y += 28;
                foreach (var entry in DeckRules.Entries(draft, c).ToList())
                {
                    var card = collection.Rules.Resolve(entry.cardKey); string key = entry.cardKey;
                    CollectionUIElements.Button(right, card?.displayName ?? key, 0, y, 244, 30, () => DetailModal(card)); y += 31;
                    CollectionUIElements.Button(right, "−", 0, y, 30, 26, () => Change(key, -1));
                    CollectionUIElements.Label(right, $"{entry.count}   {(card?.deckPointCost ?? 0) * entry.count} pt", 37, y, 130, 26);
                    CollectionUIElements.Button(right, "+", 164, y, 30, 26, () => Change(key, 1));
                    CollectionUIElements.Button(right, "×", 204, y, 30, 26, () => RemoveEntry(key)); y += 36;
                }
            }
            string checks = string.Join("\n", validation.Errors.Concat(validation.Warnings));
            var info = CollectionUIElements.Label(right, checks, 0, y, 244, 1, 14);
            float h = info.GetPreferredValues(checks, 244, 10000).y; ((RectTransform)info.transform).sizeDelta = new Vector2(244, h); y += h + 10;
            CollectionUIElements.Button(right, "Save Deck", 0, y, 244, 34, () => { Save(); Draw(); }); y += 40;
            CollectionUIElements.Button(right, "Collection", 0, y, 244, 34, () => Guard(() => { editing = false; draft = null; ownership = 0; Draw(); })); y += 40;
            right.sizeDelta = new Vector2(244, y);
        }
        private void RemoveEntry(string key)
        { foreach (var rows in new[] { draft.mainCards, draft.equipment, draft.mutators }) rows.RemoveAll(e => e.cardKey == key); dirty = true; Draw(); }
        private bool Save() { bool ok = collection.SaveDeck(draft, out status); if (ok) { dirty = false; status = "Deck saved."; } return ok; }
        private void Guard(Action action)
        {
            if (!dirty) { action(); return; }
            modal = CollectionUIElements.Panel(canvas, "Unsaved"); CollectionUIElements.Stretch(modal);
            CollectionUIElements.Label(modal, "Unsaved changes", 280, 280, 480, 40, 24);
            CollectionUIElements.Button(modal, "Save", 280, 340, 140, 36, () => { if (Save()) { CloseModal(); action(); } else { CloseModal(); Draw(); ShowMessage(canvas, status); } });
            CollectionUIElements.Button(modal, "Discard", 442, 340, 140, 36, () => { dirty = false; CloseModal(); action(); });
            CollectionUIElements.Button(modal, "Cancel", 604, 340, 140, 36, () => { CloseModal(); Draw(); });
        }
        private void Confirm(string text, Action action)
        {
            modal = CollectionUIElements.Panel(canvas, "Confirm"); CollectionUIElements.Stretch(modal);
            CollectionUIElements.Label(modal, text, 280, 280, 480, 40, 24);
            CollectionUIElements.Button(modal, "Delete", 280, 340, 200, 36, () => { CloseModal(); action(); });
            CollectionUIElements.Button(modal, "Cancel", 520, 340, 200, 36, CloseModal);
        }
        private void DetailModal(CardDefinition card)
        {
            if (card == null) return;
            modal = CollectionUIElements.Panel(canvas, "CardDetail"); CollectionUIElements.Stretch(modal);
            var content = CollectionUIElements.Scroll(modal, "CardDescription", 250, 100, 525, 530);
            var imageRect = CollectionUIElements.Rect(content, "Art"); CollectionUIElements.Place(imageRect, 0, 0, 495, 230);
            var image = imageRect.gameObject.AddComponent<Image>(); image.sprite = card.detailArt ?? card.art; image.preserveAspect = true;
            string text = CollectionCardDetail.Describe(card, config, collection.Owned(card.authoredKey));
            var label = CollectionUIElements.Label(content, text, 0, 240, 495, 1);
            float height = label.GetPreferredValues(text, 495, 10000).y; ((RectTransform)label.transform).sizeDelta = new Vector2(495, height);
            content.sizeDelta = new Vector2(495, height + 250);
            CollectionUIElements.Button(modal, "Close", 410, 650, 200, 36, CloseModal);
        }
        private void CloseModal() { if (modal != null) Destroy(modal.gameObject); modal = null; }
        private void Update() { if (CollectionMessageUI.IsShowing || root == null || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return; if (modal != null) CloseModal(); else Guard(Close); }
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
            var dropdown = Instantiate(dropdownTemplate, parent); CollectionUIElements.Place((RectTransform)dropdown.transform, x, y, w, 34);
            dropdown.onValueChanged.RemoveAllListeners(); dropdown.ClearOptions(); dropdown.AddOptions(options); dropdown.SetValueWithoutNotify(Mathf.Max(0, value)); dropdown.onValueChanged.AddListener(i => changed(i)); return dropdown;
        }
        private void Input(Transform parent, string value, float x, float y, float w, Action<string> changed)
        {
            var input = Instantiate(inputTemplate, parent); CollectionUIElements.Place((RectTransform)input.transform, x, y, w, 34);
            input.onValueChanged.RemoveAllListeners(); input.onEndEdit.RemoveAllListeners(); input.SetTextWithoutNotify(value); input.onValueChanged.AddListener(s => changed(s));
        }
        public static void ShowMessage(Transform parent, string message) => CollectionMessageUI.Show(message);
    }
}
