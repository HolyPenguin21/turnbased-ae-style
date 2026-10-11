using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Campaign
{
    // Prefab root for one neighboring-region line in the campaign sidebar.
    public sealed class CampaignNeighborRowView : MonoBehaviour
    {
        [SerializeField] private Image dot;
        [SerializeField] private TMP_Text label;
        public Image Dot => dot;
        public TMP_Text Label => label;
    }
}
