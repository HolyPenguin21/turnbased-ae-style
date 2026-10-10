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
        private int category, ownership;
        private string status = "";
        private CardDefinition selected;
        private UIRaggedGlowUI selectionGlow;
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
            ownership = 0; dirty = false; selected = null;
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
            var filters = CollectionUIElements.Panel(root, "Filters"); CollectionUIElements.Place(filters, 24, 88, 360, 166);
            Dropdown(filters, DeckRules.PlayableFactions.Select(LabelFaction).ToList(), Array.IndexOf(DeckRules.PlayableFactions, faction), 14, 10, 332, value => Guard(() =>
            { faction = DeckRules.PlayableFactions[value]; selected = null; draft = collection.DefaultDeck(faction) ?? NewDeck(); dirty = false; Draw(false); }));
            Dropdown(filters, new List<string> { "All Cards", "Heroes", "Units", "Buildings", "Equipment", "Mutators" }, category, 14, 60, 332, value => { category = value; RefreshCards(); });
            Dropdown(filters, new List<string> { "All Cards", "Owned Cards", "Not Owned" }, ownership, 14, 110, 332, value => { ownership = value; RefreshCards(); });
            details = CollectionUIElements.Scroll(root, "CardDetails", 24, 270, 360, 738);
            grid = CollectionUIElements.Scroll(root, "Cards", 408, 88, 936, 920, true);
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
            selectionGlow = null;
            CollectionUIElements.Clear(grid);
            var cards = collection.Rules.Cards(faction).Where(c =>
                (ownership == 0 || (ownership == 1 ? collection.Owned(c.authoredKey) > 0 : collection.Owned(c.authoredKey) == 0))
                && (category == 0 || category == 1 && c.cardType == CardType.Hero || category == 2 && c.cardType == CardType.Unit
                    || category == 3 && (c.cardType == CardType.Base || c.cardType == CardType.Facility)
                    || category == 4 && DeckRules.Category(c) == DeckCategory.Equipment || category == 5 && DeckRules.Category(c) == DeckCategory.Mutator));
            cards = OrderCards(cards, c => c);
            int index = 0;
            foreach (var card in cards)
            {
                float x = index % 4 * 226, y = index / 4 * 310;
                var cell = CollectionUIElements.Panel(grid, card.authoredKey); CollectionUIElements.Place(cell, x, y, 216, 300);
                var view = Instantiate(config.armyUnitCardPrefab, cell); view.SetupPreview(card, config, SelectCard,
                    () => modal == null && !CollectionMessageUI.IsShowing);
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
            UpdateSelectionGlow();
            grid.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = 1f;
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
            var cell = selected == null ? null : grid.Find(selected.authoredKey) as RectTransform;
            if (cell == null) { selectionGlow?.Hide(); return; }
            if (selectionGlow == null)
            {
                var rect = CollectionUIElements.Rect(grid, "SelectedCardGlow");
                rect.gameObject.AddComponent<Image>().raycastTarget = false;
                selectionGlow = rect.gameObject.AddComponent<UIRaggedGlowUI>();
            }
            var glowRect = (RectTransform)selectionGlow.transform;
            glowRect.anchorMin = glowRect.anchorMax = new Vector2(0, 1);
            glowRect.pivot = new Vector2(.5f, .5f);
            glowRect.anchoredPosition = cell.anchoredPosition + new Vector2(108, -116);
            selectionGlow.ApplyStyle(config.battleActingUnitHighlightStyle);
            selectionGlow.SetColor(TechnicalColors.BattleActingUnit);
            selectionGlow.ShowAt(new Vector2(208, 232));
            glowRect.SetAsLastSibling();
        }
        private int Used(string key) => draft == null ? 0 : draft.mainCards.Concat(draft.equipment).Concat(draft.mutators).Where(e => e.cardKey == key).Sum(e => e.count);
        private void Change(string key, int delta)
        { if (collection.Rules.TryChange(draft, key, delta, collection.Owned, out status)) dirty = true; Draw(); }
        private void SelectCard(CardDefinition card)
        {
            if (selected == card) return;
            selected = card; RefreshDetails(); UpdateSelectionGlow();
            Canvas.ForceUpdateCanvases();
            details.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = 1f;
        }
        private void RefreshDetails()
        {
            CollectionUIElements.Clear(details);
            float y = 12;
            var art = CollectionUIElements.Rect(details, selected == null ? "FactionLogo" : "Art"); CollectionUIElements.Place(art, 12, y, 322, 280);
            var image = art.gameObject.AddComponent<Image>();
            image.sprite = selected == null ? collection.Rules.Starting.GetCatalog(faction)?.logo : selected.detailArt ?? selected.art;
            image.enabled = image.sprite != null; image.preserveAspect = true; image.raycastTarget = false; y += 292;
            string text = selected == null ? LabelFaction(faction) + "\nSelect a card."
                : CollectionCardDetail.Describe(selected, config, collection.Owned(selected.authoredKey));
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
            var names = decks.Select(d => (d.deckId == draft.deckId ? draft.name + (dirty ? " *" : "") : d.name)
                + (d.isStarter ? " [Starter]" : "")).ToList();
            Dropdown(deckPanel, names, index, 14, 10, 500, value => Guard(() =>
            { draft = CollectionProfile.CopyDeck(decks[value]); dirty = false; Draw(false); }));
            CollectionUIElements.Button(deckPanel, "Create", 14, 64, 500, 44, () => Guard(() => { draft = NewDeck(); dirty = true; Draw(false); }), 20);
            Input(deckPanel, draft.name, 14, 118, 500, value => { draft.name = value; dirty = true; }, "Deck name");
            CollectionUIElements.Button(deckPanel, "Starter", 14, 172, 154, 40, () => Guard(() => { var starter = collection.Starter(faction);
                starter.deckId = collection.StarterDeck(faction)?.deckId ?? starter.deckId;
                draft = starter; dirty = true; Draw(false); }), 20);
            CollectionUIElements.Button(deckPanel, "Copy", 178, 172, 154, 40, () =>
            { draft = CollectionProfile.CopyDeck(draft); draft.deckId = Guid.NewGuid().ToString("N"); draft.isStarter = false; draft.name += " Copy"; dirty = true; Draw(false); }, 20);
            var delete = CollectionUIElements.Button(deckPanel, "Delete", 342, 172, 172, 40, () => Confirm("Delete this deck? Unsaved changes will be discarded.", () =>
            { if (collection.DeleteDeck(draft.deckId, out status)) { draft = collection.DefaultDeck(faction) ?? NewDeck(); dirty = false; } Draw(false); }), 20);
            delete.interactable = !draft.isStarter && !collection.IsStarter(draft.deckId);
            CollectionUIElements.Button(deckPanel, "Save Deck", 14, 222, 500, 44, () => { Save(); Draw(); }, 20);
            var validation = collection.Rules.Validate(draft, collection.Owned);
            CollectionUIElements.Label(deckPanel, $"{validation.Points} / {DeckRules.MaximumPoints} points" + (dirty ? " — unsaved" : ""), 14, 278, 500, 30, 22);
            DrawTotalCost();
            var bar = CollectionUIElements.Panel(deckPanel, "Budget"); CollectionUIElements.Place(bar, 14, 394, 500, 8);
            var fill = CollectionUIElements.Panel(bar, "Fill"); CollectionUIElements.Place(fill, 0, 0, 500 * Mathf.Clamp01(validation.Points / (float)DeckRules.MaximumPoints), 8);
            fill.GetComponent<Image>().color = validation.Points > 100 ? Color.red : new Color(.36f, .55f, .3f);
            CollectionUIElements.Label(deckPanel, "Cards", 14, 420, 390, 28, 20);
            CollectionUIElements.Label(deckPanel, "Count", 404, 420, 110, 28, 20);
            deckCards = CollectionUIElements.Scroll(deckPanel, "DeckCards", 14, 458, 500, 448, true);
            float y = 0;
            foreach (var c in new[] { DeckCategory.Main, DeckCategory.Equipment, DeckCategory.Mutator })
            {
                var entries = DeckRules.Entries(draft, c);
                if (entries.Count == 0) continue;
                CollectionUIElements.Label(deckCards, c.ToString(), 0, y, 478, 28, 18); y += 32;
                foreach (var entry in OrderCards(entries, e => collection.Rules.Resolve(e.cardKey)))
                {
                    var card = collection.Rules.Resolve(entry.cardKey); string key = entry.cardKey;
                    var row = CollectionUIElements.Panel(deckCards, key); CollectionUIElements.Place(row, 0, y, 478, 78);
                    var thumbnail = CollectionUIElements.Rect(row, "Art"); CollectionUIElements.Place(thumbnail, 4, 4, 52, 70);
                    var image = thumbnail.gameObject.AddComponent<Image>(); image.sprite = card?.detailArt ?? card?.art; image.preserveAspect = true; image.raycastTarget = false;
                    CollectionUIElements.Button(row, card?.displayName ?? key, 64, 4, 276, 40, () => SelectCard(card), 20);
                    CollectionUIElements.Label(row, $"{(long)(card?.deckPointCost ?? 0) * entry.count} pt", 69, 48, 230, 24, 18);
                    CollectionUIElements.Button(row, "−", 352, 24, 34, 34, () => Change(key, -1), 20);
                    var count = CollectionUIElements.Label(row, entry.count.ToString(), 390, 24, 40, 34, 22); count.alignment = TextAlignmentOptions.Center;
                    if (card != null && collection.Rules.Permitted(card, faction)
                        && Used(key) < Math.Min(collection.Owned(key), card.deckCopyLimit))
                        CollectionUIElements.Button(row, "+", 434, 24, 34, 34, () => Change(key, 1), 20);
                    CollectionUIElements.Button(row, "×", 310, 48, 30, 26, () => RemoveEntry(key), 20); y += 84;
                }
            }
            string checks = string.Join("\n", validation.Errors.Concat(validation.Warnings));
            var info = CollectionUIElements.Label(deckCards, checks, 0, y, 478, 1, 18);
            float h = info.GetPreferredValues(checks, 478, 10000).y; ((RectTransform)info.transform).sizeDelta = new Vector2(478, h);
            deckCards.sizeDelta = new Vector2(deckCards.sizeDelta.x, y + h + 12);
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
        private void DrawTotalCost()
        {
            var total = TotalCost();
            CollectionUIElements.Label(deckPanel, "Total cost", 14, 316, 500, 26, 18);
            var ap = config.armyUnitCardPrefab != null ? config.armyUnitCardPrefab.ActionPointBadge : null;
            for (int i = 0; i < total.Length; i++)
            {
                var badge = CollectionUIElements.Rect(deckPanel, i == 0 ? "AP" : ((ResourceType)(i - 1)).ToString());
                CollectionUIElements.Place(badge, 14 + i * 100, 352, 28, 28);
                var image = badge.gameObject.AddComponent<Image>();
                image.sprite = i == 0 ? ap?.sprite : config.resourceIconPrefab != null ? config.resourceIconPrefab.Icon : null;
                image.color = i == 0 ? ap != null ? ap.color : Color.white : ResourceIconVisual.GetColor((ResourceType)(i - 1));
                image.preserveAspect = true; image.raycastTarget = false;
                CollectionUIElements.Label(deckPanel, total[i].ToString(), 48 + i * 100, 352, 66, 28, 20);
            }
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
            CollectionUIElements.Label(modal, text, 660, 420, 600, 80, 28);
            CollectionUIElements.Button(modal, "Delete", 660, 532, 285, 48, () => { CloseModal(); action(); }, 20);
            CollectionUIElements.Button(modal, "Cancel", 975, 532, 285, 48, CloseModal, 20);
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
