using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Campaign
{
    // Prefab root for one saved-deck entry in the campaign deck-selection window.
    public sealed class CampaignDeckRowView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private TMP_Text buttonLabel;
        [SerializeField] private TMP_Text status;
        public Button Button => button;
        public TMP_Text ButtonLabel => buttonLabel;
        public TMP_Text Status => status;
    }
}
