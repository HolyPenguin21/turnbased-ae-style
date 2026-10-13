using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Campaign
{
    // Prefab root for one available attack in the campaign sidebar. Every reference is optional except the button.
    public sealed class CampaignAttackRowView : MonoBehaviour
    {
        [SerializeField] private RawImage terrainPreview;
        [SerializeField] private Image ownerLogo;
        [SerializeField] private TMP_Text regionName, ownerName;
        [SerializeField] private Button attackButton;
        public RawImage TerrainPreview => terrainPreview;
        public Image OwnerLogo => ownerLogo;
        public TMP_Text RegionName => regionName;
        public TMP_Text OwnerName => ownerName;
        public Button AttackButton => attackButton;
    }
}
