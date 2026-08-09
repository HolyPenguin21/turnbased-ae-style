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
        //   - Whichever neighbor is picked must be a real map hex not held by an enemy
        //     combat-capable army (BattleInitiator.FindEnemyAt); if every neighbor is blocked,
        //     the manual's own "no valid retreat hex" rule applies — the army is destroyed
        //     outright, every card it contains discarded.
        private void ResolveRetreat()
        {
            ArmyData army = _retreatingArmy;
            _retreatingArmy = null;
            Debug.Log($"[Battle] ResolveRetreat: army={army?.Name ?? "none"}, round={_round}");
            if (army == null)
            {
                _round++;
                BeginRound();
                return;
            }

            PerformRetreat(army, out bool destroyed);
            string message = destroyed
                ? (_localArmy == army ? "Your army is destroyed retreating!" : "The enemy army is destroyed retreating!")
                : (_localArmy == army ? "Your army retreats." : "The enemy retreats.");

            if (outcomePopup != null)
                outcomePopup.Show(message, OnBattleOutcomeAcknowledged);
            else
                OnBattleOutcomeAcknowledged();
        }

        // The relocate-or-destroy half of a retreat, shared by a player's voluntary Retreat
        // Army/Retreat All Armies choice (above) and a hero-only army automatically fleeing
        // after its hero evades a Capture Kill Challenge (see BattleScreenUI.Combat.cs's
        // HandleCaptureKillOutcome — same algorithm, per the user's own spec: "по тем же
        // правилам что и отступление из боя"). `destroyed` (out) tells the caller which of the
        // two happened, since each has its own message/next-step to show.
        private void PerformRetreat(ArmyData army, out bool destroyed)
        {
            HexCoord battleHex = army.Hex;

            // The tactical _grid (see BattleGrid) holds its own UnitData references per cell,
            // entirely separate from ArmyData.Members — relocating or clearing Members below
            // never touched them before this, so a "retreated" army's units stayed sitting in
            // the grid exactly as if they were still fighting. BattleTurnOrder.BuildOrder(_grid)
            // (next OnStartRoundClicked) and BattleInitiator.IsCombatCapable(army) (which reads
            // Members, still non-empty after a relocate) would both still see them: the SAME
            // already-fled army could get re-evaluated by ConsiderAiRetreat and "retreat" a
            // second time next round (see the user's own report — retreat announced again before
            // round 3 for what should already be a resolved retreat).
            if (_grid != null)
                foreach (UnitData member in army.Members)
                    if (_grid.TryFindPosition(member, out int row, out int col))
                        _grid.Set(row, col, null);

            bool relocated = TryFindRetreatDestination(army, battleHex, out HexCoord destination);
            destroyed = !relocated;
            Debug.Log($"[Battle] PerformRetreat: {army.Name} (owner={army.Owner?.Nickname}) from {battleHex} -> {(relocated ? destination.ToString() : "DESTROYED")}");
            if (relocated)
            {
                ArmyRegistry.MoveArmy(army, destination);
                hexSelectionController?.RestackArmiesOn(battleHex, null);
                hexSelectionController?.RestackArmiesOn(destination, null);
            }
            else
            {
                army.Members.Clear();
                hexSelectionController?.DeleteArmyIfEmptied(army);
            }
        }

        private bool TryFindRetreatDestination(ArmyData army, HexCoord battleHex, out HexCoord destination)
        {
            destination = default;
            if (map == null || army?.Owner == null)
                return false;

            BuildingData battleHexBuilding = BuildingRegistry.FindAt(battleHex);
            bool battleHexIsOwnBarracks = battleHexBuilding != null && battleHexBuilding.Owner == army.Owner
                && battleHexBuilding.HasAbility(BuildingAbilities.Barracks);

            HexCoord? target = FindNearestOwnBarracksHex(army.Owner, battleHex, excludeBattleHex: battleHexIsOwnBarracks);
            if (target.HasValue)
                return TryPickNeighborToward(army, battleHex, target.Value, out destination);

            // No Barracks hex to aim for at all (either none exist, or the only one IS the
            // battle hex itself with nowhere else useful to point at) — a random free neighbor.
            return TryPickRandomNeighbor(army, battleHex, out destination);
        }

        // This project's existing stand-in for the manual's "friendly outpost or stronghold"
        // (see HexSelectionController.DeleteArmyIfEmptied's identical Barracks-ability
        // convention) — nearest by plain hex distance, no pathfinding/obstacle-avoidance,
        // matching the user's own "just aim toward it" spec.
        private static HexCoord? FindNearestOwnBarracksHex(PlayerSetupData owner, HexCoord from, bool excludeBattleHex)
        {
            HexCoord? best = null;
            int bestDist = int.MaxValue;
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != owner || !building.HasAbility(BuildingAbilities.Barracks))
                    continue;
                if (excludeBattleHex && building.Hex.Equals(from))
                    continue;
                int dist = HexGridMath.Distance(from, building.Hex);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = building.Hex;
                }
            }
            return best;
        }

        // A single greedy step among battleHex's 6 neighbors — whichever unblocked one reduces
        // distance to target the most, not a full path. False if every neighbor is off the map
        // or held by an enemy combat-capable army.
        private bool TryPickNeighborToward(ArmyData army, HexCoord battleHex, HexCoord target, out HexCoord destination)
        {
            destination = default;
            int bestDist = int.MaxValue;
            bool found = false;
            foreach (HexCoord candidate in HexGridMath.Neighbors(battleHex))
            {
                if (!IsFreeRetreatHex(army, candidate))
                    continue;
                int dist = HexGridMath.Distance(candidate, target);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    destination = candidate;
                    found = true;
                }
            }
            return found;
        }

        private bool TryPickRandomNeighbor(ArmyData army, HexCoord battleHex, out HexCoord destination)
        {
            destination = default;
            var options = new List<HexCoord>();
            foreach (HexCoord candidate in HexGridMath.Neighbors(battleHex))
            {
                if (IsFreeRetreatHex(army, candidate))
                    options.Add(candidate);
            }
            if (options.Count == 0)
                return false;
            destination = options[UnityEngine.Random.Range(0, options.Count)];
            return true;
        }

        private bool IsFreeRetreatHex(ArmyData army, HexCoord hex)
        {
            if (map == null || !map.TryGetTerrainAt(hex, out _))
                return false;
            return BattleInitiator.FindEnemyAt(hex, army.Owner) == null;
        }
    }
}
