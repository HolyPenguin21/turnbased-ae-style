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

        // isDefender/terrainDefMod/buildingDefMod: the same terrain + Base-tagged-building
        // defense bonus Ground Combat itself will roll with (see BattleScreenUI.Combat.cs's
        // BeginAttack) — computed by the caller (BattleContactPopupUI already needs the map/
        // BuildingRegistry lookups for its own side lists) and shown here, attacker column
        // included in the call but never carrying a bonus of its own, so it stays silent about
        // this entirely rather than printing a redundant "+0".
        public void Setup(ArmyData army, FactionCardCatalog catalog, bool isDefender = false,
            int terrainDefMod = 0, int buildingDefMod = 0)
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
                // Initiative is stored as the flat bonus a hero grants every unit in its army
                // (see UnitData.Initiative's own comment), not a standalone stat of the hero's
                // own — labelling it "bonus" here avoids reading like the hero personally acts
                // at that initiative value.
                string heroLine = hero != null ? $"Initiative bonus: {hero.Initiative:+0;-0;+0}\nFate: {hero.Fate}" : "No Hero";
                string text = $"{army.Name}\n{count} unit{(count == 1 ? "" : "s")}\n{heroLine}";
                if (isDefender)
                    text += $"\nTerrain Def: {terrainDefMod:+0;-0;+0}\nConstruction Def: {buildingDefMod:+0;-0;+0}";
                infoText.text = text;
            }
        }
    }
}
