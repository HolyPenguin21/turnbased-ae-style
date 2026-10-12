using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class CollectionRewardCardView : MonoBehaviour
    {
        [SerializeField] private RectTransform artHost;
        [SerializeField] private Button detailsButton, chooseButton;
        [SerializeField] private TMP_Text copyCountLabel;
        [SerializeField] private Image selectionFrame;
        public RectTransform ArtHost => artHost;
        public Button DetailsButton => detailsButton;
        public Button ChooseButton => chooseButton;
        public TMP_Text CopyCountLabel => copyCountLabel;
        public void SetSelected(bool selected)
        {
            selectionFrame.enabled = selected;
            chooseButton.GetComponentInChildren<TMP_Text>().text = selected ? "SELECTED" : "SELECT";
        }
    }
}
