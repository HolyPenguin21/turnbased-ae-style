using System;
using System.Collections.Generic;
using System.Text;
using Game.Combat;
using Game.Map;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Shown at the start of every battle round (title increments — "Round 1", "Round 2", ...)
    // before the full grid/initiative queue reveal (see BattleScreenUI.BeginRound) — a preview
    // of both sides' rosters and their effective initiative for the round about to happen. A
    // side's hero (if any) is listed separately as its own "Name: bonus N" line rather than
    // mixed into the acting list, matching BattleTurnOrder.BuildOrder's own rule that heroes
    // never act. Retreat doesn't end the battle on the spot — see BattleScreenUI.OnRetreatClicked
    // for the actual "one more grace round for the other side" flow the user asked for.
    public class BattleRoundStartPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text roundTitleText;
        // Only one faction is fully set up in this project right now (see BattleScreenUI's own
        // identical `catalog` field) — both logos come from the same sprite regardless of which
        // side it's showing, same as everywhere else that needs one.
        [SerializeField] private Image attackerLogo;
        [SerializeField] private Image defenderLogo;
        [SerializeField] private TMP_Text attackerNameText;
        [SerializeField] private TMP_Text defenderNameText;
        [SerializeField] private TMP_Text attackerRosterText;
        [SerializeField] private TMP_Text defenderRosterText;
        // Never enabled during round 1 (see the user's own spec) or for a garrison (which can
        // never retreat, per the manual — see canRetreat in Show).
        [SerializeField] private Button retreatButton;
        [SerializeField] private Button startRoundButton;

        private Action _onStartRound;
        private Action _onRetreat;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        private void Awake()
        {
            if (startRoundButton != null)
                startRoundButton.onClick.AddListener(OnStartRoundClicked);
            if (retreatButton != null)
                retreatButton.onClick.AddListener(OnRetreatClicked);
        }

        // canRetreat is false whenever there's no local human side to retreat, or that side's
        // army is a garrison (garrisons can never retreat, per the manual) — combined with the
        // round > 1 gate below regardless.
        public void Show(int round, BattleGrid grid, ArmyData attacker, ArmyData defender, Sprite factionLogo,
            bool canRetreat, Action onStartRound, Action onRetreat)
        {
            _onStartRound = onStartRound;
            _onRetreat = onRetreat;
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            if (roundTitleText != null)
                roundTitleText.text = $"Round {round}";
            if (retreatButton != null)
                retreatButton.interactable = canRetreat && round > 1;

            if (attackerLogo != null)
            {
                attackerLogo.sprite = factionLogo;
                attackerLogo.gameObject.SetActive(factionLogo != null);
            }
            if (defenderLogo != null)
            {
                defenderLogo.sprite = factionLogo;
                defenderLogo.gameObject.SetActive(factionLogo != null);
            }
            if (attackerNameText != null)
                attackerNameText.text = attacker != null ? attacker.Name : string.Empty;
            if (defenderNameText != null)
                defenderNameText.text = defender != null ? defender.Name : string.Empty;

            if (attackerRosterText != null)
                attackerRosterText.text = FormatRoster(BattleTurnOrder.BuildSideSummary(grid, attackerSide: true));
            if (defenderRosterText != null)
                defenderRosterText.text = FormatRoster(BattleTurnOrder.BuildSideSummary(grid, attackerSide: false));
        }

        private static string FormatRoster((UnitData hero, List<(UnitData unit, int initiative)> acting) side)
        {
            var sb = new StringBuilder();
            if (side.hero != null)
                sb.AppendLine($"{side.hero.Name}: bonus {side.hero.Initiative}");
            foreach (var (unit, initiative) in side.acting)
                sb.AppendLine($"{unit.Name}: {initiative}");
            return sb.ToString();
        }

        private void OnStartRoundClicked()
        {
            Hide();
            _onRetreat = null;
            Action callback = _onStartRound;
            _onStartRound = null;
            callback?.Invoke();
        }

        private void OnRetreatClicked()
        {
            Hide();
            _onStartRound = null;
            Action callback = _onRetreat;
            _onRetreat = null;
            callback?.Invoke();
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }
    }
}
