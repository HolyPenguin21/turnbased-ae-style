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
    // Attack resolution and battle-end handling half of BattleScreenUI — split out purely for
    // file size, same reasoning as HexSelectionController's own multi-file split. Shares this
    // class's fields (_grid/_attacker/_defender/etc.) and the state-machine methods in the main
    // file (EndTurn, Show/Hide, ShowAiThought) automatically via `partial`.
    public partial class BattleScreenUI
    {
        private void BeginAttack(UnitData attacker, UnitData defender)
        {
            if (attackPopup == null)
                return;
            bool attackerIsAttackerSide = _grid.TryFindPosition(attacker, out int aRow, out _) && BattleGrid.IsAttackerSideRow(aRow);
            UnitData attackerHero = BattleTurnOrder.FindHero(_grid, attackerIsAttackerSide);
            UnitData defenderHero = BattleTurnOrder.FindHero(_grid, !attackerIsAttackerSide);

            // New rule, per the user: an army with a unit that attacks in the Tactical Battle
            // Module loses its remaining strategic-map movement for the current player turn —
            // applied the moment the attack is declared (popup opened), regardless of the roll's
            // outcome.
            ArmyData attackerArmy = attackerIsAttackerSide ? _attacker : _defender;
            if (attackerArmy != null)
                foreach (UnitData member in attackerArmy.Members)
                    member.MoveCurrent = 0;

            attackPopup.Begin(attacker, attackerHero, defender, defenderHero, catalog != null ? catalog.logo : null,
                (damage, died) => OnAttackResolved(attacker, defender, damage, died), ShowAiThought);
        }

        private void OnAttackResolved(UnitData attacker, UnitData defender, int damage, bool defenderDied)
        {
            // A reaction from the side that TOOK the damage, only when that side is AI-controlled
            // — this is a first-person "how do I feel about this" line, not omniscient narration.
            // A death fires its own more specific UnitDied/EnemyKilled thought instead (see
            // RemoveUnit), so this only runs for a hit that didn't finish the target off.
            if (!defenderDied && damage > 0 && defender.Owner != null && !defender.Owner.IsHuman)
            {
                bool major = defender.HitPointsMax > 0 && defender.HitPointsCurrent <= defender.HitPointsMax / 3f;
                UnitData sideHero = BattleTurnOrder.FindHero(_grid, IsAttackerSide(defender));
                aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(
                    major ? AiThoughtCategory.DamageTakenMajor : AiThoughtCategory.DamageTakenMinor, attacker?.Name, sideHero != null));
            }

            if (defenderDied)
                RemoveUnit(defender);
            RefreshGrid();
            if (!CheckBattleEnd())
                EndTurn();
        }

        private bool IsAttackerSide(UnitData unit) => _grid.TryFindPosition(unit, out int row, out _) && BattleGrid.IsAttackerSideRow(row);

        private void RemoveUnit(UnitData unit)
        {
            // Captured before the grid cell is cleared below — needed both for the hero-side
            // lookup and because TryFindPosition can no longer find it afterward.
            bool wasAttackerSide = IsAttackerSide(unit);

            if (_grid.TryFindPosition(unit, out int row, out int col))
                _grid.Set(row, col, null);
            _attacker?.Members.Remove(unit);
            _defender?.Members.Remove(unit);

            // Whichever side `unit` belonged to reacts with UnitDied; the OTHER side (if
            // AI-controlled) gets a small EnemyKilled reaction instead — heroes never die this
            // way (never a valid attack target), so no special-casing needed there.
            UnitData deadSideHero = BattleTurnOrder.FindHero(_grid, wasAttackerSide);
            UnitData killerSideHero = BattleTurnOrder.FindHero(_grid, !wasAttackerSide);
            ArmyData deadSideArmy = wasAttackerSide ? _attacker : _defender;
            ArmyData killerSideArmy = wasAttackerSide ? _defender : _attacker;
            if (deadSideArmy?.Owner != null && !deadSideArmy.Owner.IsHuman)
                aiThoughts?.Show(deadSideHero, BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.UnitDied, unit.Name, deadSideHero != null));
            if (killerSideArmy?.Owner != null && !killerSideArmy.Owner.IsHuman)
                aiThoughts?.Show(killerSideHero, BattleAiPhraseBank.GetRandomPhrase(AiThoughtCategory.EnemyKilled, unit.Name, killerSideHero != null));

            // Otherwise a unit killed mid-round could still come up "on turn" later this same
            // round (BuildOrder is only recomputed at BeginRound). Removing by index (not
            // List.Remove) so _turnIndex can be corrected if the removed entry sat before it —
            // it never IS _turnIndex itself (the current actor is always the attacker here, a
            // different unit from whichever defender just died).
            if (_turnOrder != null)
            {
                int index = _turnOrder.IndexOf(unit);
                if (index >= 0)
                {
                    _turnOrder.RemoveAt(index);
                    if (index <= _turnIndex)
                        _turnIndex--;
                }
            }
        }

        // Manual's "Battle Results": ends the instant either side has no more combat-capable
        // units (BattleInitiator.IsCombatCapable's own "at least one non-hero unit" rule) — true
        // if the battle just ended (caller should NOT also EndTurn in that case).
        private bool CheckBattleEnd()
        {
            bool attackerAlive = BattleInitiator.IsCombatCapable(_attacker);
            bool defenderAlive = BattleInitiator.IsCombatCapable(_defender);
            if (attackerAlive && defenderAlive)
                return false;

            FireBattleEndThought(_attacker, attackerAlive);
            FireBattleEndThought(_defender, defenderAlive);

            string message;
            if (_localArmy == null)
                message = attackerAlive ? "Attacker wins." : defenderAlive ? "Defender wins." : "Draw.";
            else
            {
                bool localWon = _localArmy == _attacker ? attackerAlive : defenderAlive;
                message = localWon ? "Victory!" : "Defeat!";
            }

            if (outcomePopup != null)
                outcomePopup.Show(message, OnBattleOutcomeAcknowledged);
            else
                OnBattleOutcomeAcknowledged();
            return true;
        }

        private void FireBattleEndThought(ArmyData army, bool survived)
        {
            if (army?.Owner == null || army.Owner.IsHuman)
                return;
            UnitData sideHero = BattleTurnOrder.FindHero(_grid, army == _attacker);
            aiThoughts?.Show(sideHero, BattleAiPhraseBank.GetRandomPhrase(
                survived ? AiThoughtCategory.BattleWon : AiThoughtCategory.BattleLost, hasHero: sideHero != null));
        }

        private void OnBattleOutcomeAcknowledged()
        {
            // DeleteArmyIfEmptied only unregisters/destroys the ONE army's own marker — it
            // doesn't re-pick which army's marker should now represent this hex for a given
            // owner (see RestackArmiesOn's own comment). Without this, a second, untouched enemy
            // army sharing the hex (this battle only ever involved _attacker/_defender, see
            // BattleInitiator.FindEnemyAt picking just the first one) stayed hidden until the
            // player happened to reselect the hex — the same restack ArmyViewerModalUI's own
            // Hide() relies on, just done explicitly here since nothing re-selects the hex after
            // a battle closes.
            HexCoord hex = _attacker != null ? _attacker.Hex : (_defender != null ? _defender.Hex : default);

            // A normal fight-to-conclusion (not a retreat relocating one side away) leaves both
            // _attacker/_defender still on the SAME hex — if exactly one of them is still combat
            // capable and a DIFFERENT enemy army is still sitting on that hex, the manual's own
            // "two armies, one hex" case means this fight isn't actually over: the winner
            // continues straight into the next one instead of being left stuck on a hex it can
            // no longer leave (BattleInitiator.FindEnemyAt would still see the untouched army)
            // with no way to ever actually fight it (see the user's own report). Checked BEFORE
            // DeleteArmyIfEmptied below so a still-shared hex is the actual signal, not an
            // artifact of cleanup order.
            bool stillSameHex = _attacker != null && _defender != null && _attacker.Hex.Equals(_defender.Hex);
            ArmyData survivor = null;
            if (stillSameHex)
            {
                bool attackerCapable = BattleInitiator.IsCombatCapable(_attacker);
                bool defenderCapable = BattleInitiator.IsCombatCapable(_defender);
                if (attackerCapable != defenderCapable)
                    survivor = attackerCapable ? _attacker : _defender;
            }

            hexSelectionController?.DeleteArmyIfEmptied(_attacker);
            hexSelectionController?.DeleteArmyIfEmptied(_defender);
            hexSelectionController?.RestackArmiesOn(hex, null);

            ArmyData nextEnemy = survivor?.Owner != null ? BattleInitiator.FindEnemyAt(hex, survivor.Owner) : null;
            if (nextEnemy != null)
            {
                // Straight into the next fight, no separate Fight/Delay choice this time (the
                // survivor is already fully committed to this hex, there's nowhere else for it
                // to go) — same completion callback as the original Show, only actually invoked
                // once the whole chain finally ends in the Hide() below, not per-link.
                Show(hex, new List<ArmyData> { survivor, nextEnemy }, _onClosed);
                return;
            }

            Hide();
        }
    }
}
