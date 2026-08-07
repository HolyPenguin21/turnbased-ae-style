using Game.Cards;
using Game.Map;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // One side's column inside BattleContactPopupUI — that army's faction logo, its commanding
    // hero (if any; first hero in the roster, see ArmyData.AddMemberSorted keeping heroes at the
    // front), and a short summary. Purely informational, same as the read-only Army Viewer — no
    // drag, no click actions.
    public class BattleParticipantColumnUI : MonoBehaviour
    {
        [SerializeField] private Image factionLogo;
        [SerializeField] private GameObject commanderRoot;
        [SerializeField] private Image commanderArt;
        [SerializeField] private TMP_Text commanderNameText;
        [SerializeField] private TMP_Text infoText;

        public void Setup(ArmyData army, FactionCardCatalog catalog)
        {
            if (factionLogo != null)
            {
                factionLogo.sprite = catalog != null ? catalog.logo : null;
                factionLogo.gameObject.SetActive(factionLogo.sprite != null);
            }

            UnitData hero = army?.Members.Find(m => m.IsHero);
            if (commanderRoot != null)
                commanderRoot.SetActive(hero != null);
            if (hero != null)
            {
                if (commanderArt != null)
                    commanderArt.sprite = hero.Art;
                if (commanderNameText != null)
                    commanderNameText.text = hero.Name;
            }

            if (infoText != null && army != null)
            {
                int count = army.Members.Count;
                string heroLine = hero != null ? $"Initiative: {hero.Initiative}\nFate: {hero.Fate}" : "No Hero";
                infoText.text = $"{army.Name}\n{count} unit{(count == 1 ? "" : "s")}\n{heroLine}";
            }
        }
    }
}
