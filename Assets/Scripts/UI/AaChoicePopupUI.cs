using System;
using Game.Map;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // The Attack/Skip decision a human AA-unit owner gets the instant an enemy air army enters
    // (or a ground army carrying AA discovers) its radius — modelled on EventChoicePopupUI's own
    // "two-button choice, mouse only" shape (see its own comment for why that shape rather than
    // reusing BattleAttackPopupUI's Roll state further: Skip here means "never even roll", not
    // "decline a Fate reroll"). An AI-owned AA unit never sees this at all — Game.Aviation.
    // AviationCombatPresenter always attacks on its behalf without ever showing it.
    public class AaChoicePopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Image aaUnitArtImage;
        [SerializeField] private Image airArmyArtImage;
        [SerializeField] private TMP_Text descriptionText;
        [SerializeField] private Button attackButton;
        [SerializeField] private Button skipButton;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Lets GameTurnController react to this popup opening/closing instead of polling
        // IsShowing every frame (see GameTurnController.InputBlocked), same pattern every other
        // map-level popup already follows.
        public event Action VisibilityChanged;

        public void Show(UnitData aaUnit, ArmyData airArmy, Action onAttack, Action onSkip)
        {
            if (aaUnit == null || airArmy == null)
                return;

            if (aaUnitArtImage != null)
            {
                Sprite art = aaUnit.DetailArt != null ? aaUnit.DetailArt : aaUnit.Art;
                aaUnitArtImage.sprite = art;
                aaUnitArtImage.gameObject.SetActive(art != null);
            }
            if (airArmyArtImage != null)
            {
                UnitData portraitSource = airArmy.Members.Count > 0 ? airArmy.Members[0] : null;
                Sprite art = portraitSource != null
                    ? (portraitSource.DetailArt != null ? portraitSource.DetailArt : portraitSource.Art)
                    : null;
                airArmyArtImage.sprite = art;
                airArmyArtImage.gameObject.SetActive(art != null);
            }
            if (descriptionText != null)
                descriptionText.text = $"{aaUnit.Name} can fire on {airArmy.Name}.";

            if (attackButton != null)
            {
                attackButton.onClick.RemoveAllListeners();
                attackButton.onClick.AddListener(() => { Hide(); onAttack?.Invoke(); });
            }
            if (skipButton != null)
            {
                skipButton.onClick.RemoveAllListeners();
                skipButton.onClick.AddListener(() => { Hide(); onSkip?.Invoke(); });
            }

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            VisibilityChanged?.Invoke();
        }

        public void Hide()
        {
            if (panelRoot == null || !panelRoot.activeSelf)
                return;
            panelRoot.SetActive(false);
            VisibilityChanged?.Invoke();
        }
    }
}
