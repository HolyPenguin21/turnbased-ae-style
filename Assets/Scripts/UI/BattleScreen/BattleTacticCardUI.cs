using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.Turns;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Tactical card face. CardUI owns the same drag gesture as the ordinary hand.
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
        public bool IsDragging => _interaction != null && _interaction.IsDragging;
        private CardUI _interaction;

        public void SetInteraction(bool enabled, System.Action onClick,
            System.Func<string> unavailableReason = null, System.Func<Vector2, Camera, bool> onDrop = null,
            System.Action<string> showError = null)
        {
            var button = GetComponent<Button>() ?? gameObject.AddComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.interactable = enabled;
            var graphic = GetComponent<Image>();
            if (graphic == null) graphic = gameObject.AddComponent<Image>();
            if (button.targetGraphic == null) button.targetGraphic = graphic;
            graphic.raycastTarget = true;
            _interaction = GetComponent<CardUI>() ?? gameObject.AddComponent<CardUI>();
            _interaction.ConfigureTacticInteraction(Data, onClick,
                () => unavailableReason?.Invoke() ?? (enabled ? null
                    : "Wait for your challenge decision to use a tactical card."),
                onDrop, showError ?? ReportError);
        }

        private static void ReportError(string reason) =>
            FindAnyObjectByType<GameTurnController>(FindObjectsInactive.Include)?.ShowSpawnHint(reason);

        public void CancelDrag() => _interaction?.CancelDrag();

        private void Awake()
        {
            if (rectTransform == null)
                rectTransform = (RectTransform)transform;
        }

        public void Setup(CardData data, GameConfig config)
        {
            Data = data;
            CardDefinition definition = data?.Definition;

            if (artImage != null)
                artImage.sprite = definition != null ? definition.art : null;
            if (nameText != null)
                nameText.text = definition != null ? definition.displayName : string.Empty;
            // Ability abbreviations only, same as every other card face (see CardUI) — never
            // the card type.
            if (typeText != null)
                typeText.text = TacticRules.Description(definition);

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

        // CardUI returns an unaccepted drag to this inventory slot.
        public void SetSlot(Vector2 anchoredPosition)
        {
            if (_interaction != null)
                _interaction.SetTacticHome(anchoredPosition, rectTransform.localScale.x);
            else if (rectTransform != null)
                rectTransform.anchoredPosition = anchoredPosition;
        }
    }
}
