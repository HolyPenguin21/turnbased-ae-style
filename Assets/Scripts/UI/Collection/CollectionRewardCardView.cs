using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class CollectionRewardCardView : MonoBehaviour
    {
        [SerializeField] private RectTransform artHost;
        [SerializeField] private Button detailsButton, chooseButton;
        [SerializeField] private TMP_Text copyCountLabel, detailsLabel, chooseLabel;
        [SerializeField] private Image selectionFrame;
        [SerializeField] private ArmyUnitCardUI preview;
        public ArmyUnitCardUI Preview => preview;
        public Button DetailsButton => detailsButton;
        public Button ChooseButton => chooseButton;
        public TMP_Text DetailsLabel => detailsLabel;
        public TMP_Text CopyCountLabel => copyCountLabel;
        public void SetSelected(bool selected)
        {
            selectionFrame.enabled = selected;
            chooseLabel.text = selected ? "SELECTED" : "SELECT";
        }
    }
}
