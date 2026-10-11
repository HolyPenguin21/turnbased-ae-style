using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cameras;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Styles;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Retreat-destination resolution half of BattleScreenUI — split out purely for file size,
    // same reasoning as HexSelectionController's own multi-file split. Shares this class's
    // fields and the state-machine methods in the main file (BeginRound, EndTurn) automatically
    // via `partial`.
    public partial class BattleScreenUI
    {
        // The grace round is over — actually relocate (or destroy) the retreating army now, per
        // the user's own confirmed algorithm:
        //   - Battle hex isn't the retreating owner's own Barracks hex: aim for the nearest
        //     own-Barracks hex anywhere on the map, step one cell toward it.
        //   - Battle hex IS the retreating owner's own Barracks hex: aim for the nearest OTHER
        //     own-Barracks hex instead (can't "step toward" the hex already stood on); if none
        //     exists, step to a random free neighbor.
        //   - Whichever neighbor is picked must be a real map hex (IsRetreatableHex) — if every
        //     neighbor is off the map entirely, the manual's own "no valid retreat hex" rule
        //     applies: the army is destroyed outright, every card it contains discarded. Landing
        //     on a hex held by someone hostile is no longer a blocker (per the user's own later
        //     spec): PerformRetreat below handles the consequences — an undefended enemy facility
        //     is destroyed/captured on arrival, and an engageable enemy army/garrison triggers a
        //     fresh Battle/Capture Kill Challenge exactly like an ordinary strategic move would
        //     (see PerformRetreat's own comments).
        private void ResolveRetreat()
        {
            ArmyData army = _retreatingArmy;
            _retreatingArmy = null;
            if (army == null)
            {
                _round++;
                BeginRound();
                return;
            }

            // The OTHER side (not the one that just retreated) is who's still standing on the
            // battle hex — PerformRetreat has already relocated or cleared `army` by this point,
            // so it can never be the survivor for DescribeNextAction's own purposes.
            ArmyData survivingArmy = army == _attacker ? _defender : _attacker;

            BattleRetreatApplication retreat = _battleEngine != null
                ? _battleEngine.CompleteRetreat(army, survivingArmy, map, hexSelectionController)
                : BattleEngine.PerformRetreat(_battleState, army, survivingArmy, map, hexSelectionController);
            bool destroyed = retreat.Destroyed;
            _retreatingArmy = null;
            if (retreat.ContactParticipants != null)
                _pendingRetreatContacts.Enqueue((retreat.Destination, retreat.ContactParticipants));
            string title = destroyed
                ? (_localArmy == army ? "Your army is destroyed retreating!" : "The enemy army is destroyed retreating!")
                : (_localArmy == army ? "Your army retreats." : "The enemy retreats.");
            string detail = destroyed
                ? $"{army.Name} is destroyed retreating."
                : $"{army.Name} retreats from the battle.";
            string message = $"{detail}\n{DescribeNextAction(survivingArmy)}";

            // Same auto-close-if-no-human as FinishBattleEnd (BattleScreenUI.Combat.cs) — a
            // retreat resolved between two AI-owned armies has nobody there to click Ok either.
            if (outcomePopup != null)
                outcomePopup.Show(title, message, OnBattleOutcomeAcknowledged, autoCloseNoHuman: _localArmy == null);
            else
                OnBattleOutcomeAcknowledged();
        }


    }
}
