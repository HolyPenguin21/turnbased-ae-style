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

        public bool IsShowing => isActiveAndEnabled && panelRoot != null && panelRoot.activeInHierarchy;
        private Action _onCancelled;

        // Lets GameTurnController react to this popup opening/closing instead of polling
        // IsShowing every frame (see GameTurnController.InputBlocked), same pattern every other
        // map-level popup already follows.
        public event Action VisibilityChanged;

        public void Show(UnitData aaUnit, ArmyData airArmy, Action onAttack, Action onSkip, Action onCancelled = null)
        {
            if (aaUnit == null || airArmy == null)
                return;
            Hide();
            _onCancelled = onCancelled;

            if (aaUnitArtImage != null)
            {
                Sprite art = aaUnit.DetailArt != null ? aaUnit.DetailArt : aaUnit.Art;
                aaUnitArtImage.sprite = art;
                aaUnitArtImage.gameObject.SetActive(art != null);
            }
            if (airArmyArtImage != null)
            {
                // Never the portrait of an aircraft the AA owner cannot see (stealth).
                UnitData portraitSource = null;
                foreach (UnitData member in StealthSystem.TargetableMembersFor(airArmy, aaUnit.Owner))
                {
                    portraitSource = member;
                    break;
                }
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
                Game.UI.UIUtility.ResetRuntimeListeners(attackButton);
                attackButton.onClick.AddListener(() => Resolve(onAttack));
            }
            if (skipButton != null)
            {
                Game.UI.UIUtility.ResetRuntimeListeners(skipButton);
                skipButton.onClick.AddListener(() => Resolve(onSkip));
            }

            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            VisibilityChanged?.Invoke();
        }

        private void Resolve(Action answer)
        {
            _onCancelled = null;
            Hide();
            answer?.Invoke();
        }

        private void OnDisable() => Hide();

        public void Hide()
        {
            var cancelled = _onCancelled;
            _onCancelled = null;
            try
            {
                if (panelRoot != null && panelRoot.activeSelf)
                {
                    panelRoot.SetActive(false);
                    VisibilityChanged?.Invoke();
                }
            }
            finally { cancelled?.Invoke(); }
        }
    }
}
