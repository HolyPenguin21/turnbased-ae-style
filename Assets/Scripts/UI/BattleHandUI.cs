using System.Collections.Generic;
using Game.Cards;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // A vertical, Tactic-card-only hand living on the battle screen itself — entirely separate
    // from CardHandUI's own main hand (see CardType.Tactic's own comment: these are never drawn
    // from the normal deck, only granted directly by a hero during battle — a mechanic that
    // doesn't exist yet, so this starts empty and has nothing to show until it does). Same
    // shape as CardHandUI's own paging (a handful of visible slots, scroll arrows) just
    // vertical instead of horizontal, and with no drag-to-play/reorder — see
    // BattleTacticCardUI, which is a static display, not an interactive one.
    public class BattleHandUI : MonoBehaviour
    {
        [SerializeField] private RectTransform cardContainer;
        [SerializeField] private BattleTacticCardUI cardPrefab;
        [SerializeField] private Button scrollUpButton;
        [SerializeField] private Button scrollDownButton;
        [SerializeField] private float cardHeight = 140f;
        [Range(0f, 0.3f)]
        [SerializeField] private float overlapFraction = 0.08f;
        // Fixed window size, matching the user's own spec ("помещается 4 карты") — unlike
        // CardHandUI's own MaxVisible, this isn't expected to ever need tuning per-instance.
        private const int MaxVisible = 4;

        private readonly List<CardData> _cards = new List<CardData>();
        private readonly List<BattleTacticCardUI> _visible = new List<BattleTacticCardUI>();
        private int _scrollOffset;

        private void Awake()
        {
            if (scrollUpButton != null)
                scrollUpButton.onClick.AddListener(() => Scroll(-1));
            if (scrollDownButton != null)
                scrollDownButton.onClick.AddListener(() => Scroll(1));
            Relayout();
        }

        // Where a hero-generated Tactic card lands, once that mechanic exists — nothing calls
        // this yet (see the class comment).
        public void AddCard(CardData data)
        {
            if (data == null || data.Definition == null || data.Definition.cardType != CardType.Tactic)
                return;
            _cards.Add(data);
            _scrollOffset = Mathf.Max(0, _cards.Count - MaxVisible);
            Relayout();
        }

        private void Scroll(int direction)
        {
            _scrollOffset = Mathf.Clamp(_scrollOffset + direction, 0, Mathf.Max(0, _cards.Count - MaxVisible));
            Relayout();
        }

        private void Relayout()
        {
            UIListUtility.DestroyAndClear(_visible);
            if (scrollUpButton != null)
                scrollUpButton.interactable = _scrollOffset > 0;
            if (scrollDownButton != null)
                scrollDownButton.interactable = _scrollOffset + MaxVisible < _cards.Count;

            if (cardContainer == null || cardPrefab == null)
                return;

            float step = cardHeight * (1f - overlapFraction);
            int end = Mathf.Min(_cards.Count, _scrollOffset + MaxVisible);
            for (int i = _scrollOffset; i < end; i++)
            {
                BattleTacticCardUI card = Instantiate(cardPrefab, cardContainer);
                card.Setup(_cards[i]);
                card.SetSlot(new Vector2(0f, -(i - _scrollOffset) * step));
                _visible.Add(card);
            }
        }
    }
}
