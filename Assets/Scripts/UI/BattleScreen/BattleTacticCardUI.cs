using Game.Cards;
using Game.Economy;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One Tactic card's visual inside BattleHandUI's vertical hand — art, name, type, and cost
    // badges (same badge convention as CardUI's own CostRow). No StatsRow (CardUI already hides
    // it for CardType.Tactic — Tactic cards have no unit/hero/building stats to show), no drag,
    // no hover-grow — playing a Tactic card during battle isn't wired up yet (see
    // project_combat_design_decisions memory), so this is a static, click-free display for now.
    public class BattleTacticCardUI : MonoBehaviour
    {
        [SerializeField] private RectTransform rectTransform;
        [SerializeField] private Image artImage;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text typeText;

        [SerializeField] private GameObject apBadgeRoot;
        [SerializeField] private TMP_Text apBadgeText;
        [SerializeField] private GameObject humanBadgeRoot;
        [SerializeField] private TMP_Text humanBadgeText;
        [SerializeField] private GameObject energyBadgeRoot;
        [SerializeField] private TMP_Text energyBadgeText;
        [SerializeField] private GameObject materialsBadgeRoot;
        [SerializeField] private TMP_Text materialsBadgeText;
        [SerializeField] private GameObject techBadgeRoot;
        [SerializeField] private TMP_Text techBadgeText;

        public CardData Data { get; private set; }

        private void Awake()
        {
            if (rectTransform == null)
                rectTransform = (RectTransform)transform;
        }

        public void Setup(CardData data)
        {
            Data = data;
            CardDefinition definition = data?.Definition;

            if (artImage != null)
                artImage.sprite = definition != null ? definition.art : null;
            if (nameText != null)
                nameText.text = definition != null ? definition.displayName : string.Empty;
            if (typeText != null)
                typeText.text = definition != null ? definition.cardType.ToString() : string.Empty;

            ResourceCost cost = definition?.resourceCost;
            SetBadge(apBadgeRoot, apBadgeText, definition != null ? definition.apCost : 0);
            SetBadge(humanBadgeRoot, humanBadgeText, cost != null ? cost.human : 0);
            SetBadge(energyBadgeRoot, energyBadgeText, cost != null ? cost.energy : 0);
            SetBadge(materialsBadgeRoot, materialsBadgeText, cost != null ? cost.materials : 0);
            SetBadge(techBadgeRoot, techBadgeText, cost != null ? cost.tech : 0);
        }

        // Same "hide entirely at 0 rather than show 0" convention as CardUI's own SetBadge.
        private static void SetBadge(GameObject root, TMP_Text text, int amount)
        {
            if (root == null)
                return;
            bool visible = amount > 0;
            root.SetActive(visible);
            if (visible && text != null)
                text.text = amount.ToString();
        }

        // Positioned by BattleHandUI's own vertical paging — no live-reorder/drag machinery
        // needed here (unlike CardHandUI's own hand), just a fixed slot per relayout.
        public void SetSlot(Vector2 anchoredPosition)
        {
            if (rectTransform != null)
                rectTransform.anchoredPosition = anchoredPosition;
        }
    }
}
