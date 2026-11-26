using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Authored presentation only. MatchResultOperation owns the reward workflow.
    public sealed class CollectionRewardView : MonoBehaviour
    {
        [SerializeField] private TMP_Text title, subtitle, status, body, primaryLabel;
        [SerializeField] private RectTransform cardsHost;
        [SerializeField] private GameObject bodyRoot;
        [SerializeField] private Button primaryButton;
        [SerializeField] private Image recipientImage, rewardImage;
        public TMP_Text Title => title;
        public TMP_Text Subtitle => subtitle;
        public TMP_Text Status => status;
        public TMP_Text Body => body;
        public TMP_Text PrimaryLabel => primaryLabel;
        public GameObject BodyRoot => bodyRoot;
        public RectTransform CardsHost => cardsHost;
        public Button PrimaryButton => primaryButton;
        public Image RecipientImage => recipientImage;
        public Image RewardImage => rewardImage;
    }
}
