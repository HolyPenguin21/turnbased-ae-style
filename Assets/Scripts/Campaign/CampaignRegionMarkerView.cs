using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Campaign
{
    // Prefab root for the emblem and name drawn over each region's center. Every reference is optional.
    public sealed class CampaignRegionMarkerView : MonoBehaviour
    {
        [SerializeField] private Image emblem;
        [SerializeField] private TMP_Text label;
        [SerializeField] private GameObject selectedState;
        public Image Emblem => emblem;
        public TMP_Text Label => label;
        public GameObject SelectedState => selectedState;
    }
}
