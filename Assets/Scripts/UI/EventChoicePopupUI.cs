using System;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // The Explore/Skip popup shown the moment a "clean" event hex (see HexSelectionController.
    // Events.cs's TryHandleCleanHexEntry) is entered — modelled on BattleContactPopupUI's own
    // "two-button choice, mouse only" shape rather than repurposing BattleAttackPopupUI's
    // ChallengeResultRoot state further. Deliberately has NO Space-bar shortcut — per the user's
    // own spec, only the reward popup (see EventRewardPopupUI) gets one; a choice popup always
    // needs an explicit click.
    public class EventChoicePopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Image heroOrUnitArtImage;
        [SerializeField] private Image eventArtImage;
        [SerializeField] private TMP_Text descriptionText;
        [SerializeField] private Button exploreButton;
        [SerializeField] private Button skipButton;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Lets GameTurnController react to this popup opening/closing instead of polling
        // IsShowing every frame (see GameTurnController.InputBlocked/CardDraggingBlocked).
        public event Action VisibilityChanged;

        // Hero first, then the first non-hero unit — per the user's own spec ("герой армии или
        // первый юнит в армии"). DetailArt already carries the art-fallback baked in at spawn
        // time (see UnitData.DetailArt's own comment), no separate CardDefinition lookup needed
        // here. Public/static so EventRewardPopupUI can show the exact same portrait for the same
        // army without duplicating this lookup.
        public static Sprite ResolvePortrait(ArmyData army)
        {
            UnitData hero = army?.Members.FirstOrDefault(m => m.IsHero);
            UnitData portraitSource = hero ?? army?.Members.FirstOrDefault(m => !m.IsHero);
            return portraitSource?.DetailArt;
        }

        public void Show(ArmyData mover, EventDefinition definition, Action onExplore, Action onSkip)
        {
            if (definition == null)
                return;

            if (heroOrUnitArtImage != null)
            {
                Sprite portrait = ResolvePortrait(mover);
                heroOrUnitArtImage.sprite = portrait;
                heroOrUnitArtImage.gameObject.SetActive(portrait != null);
            }
            if (eventArtImage != null)
            {
                eventArtImage.sprite = definition.image;
                eventArtImage.gameObject.SetActive(definition.image != null);
            }
            if (descriptionText != null)
                descriptionText.text = definition.description;

            if (exploreButton != null)
            {
                exploreButton.onClick.RemoveAllListeners();
                exploreButton.onClick.AddListener(() => { Hide(); onExplore?.Invoke(); });
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
