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

            // A retreat ends the battle without ever going through FinishBattleEnd
            // (BattleScreenUI.Combat.cs) — revert Berserk here too, on both sides, or the
            // "for the duration of the battle" buff/debuff sticks around permanently on whoever
            // retreated AND on whoever stayed and fought. Before PerformRetreat, which may clear
            // `army`'s Members outright (the destroyed/no-valid-hex case).
            RevertBerserkStacks(army);
            RevertBerserkStacks(survivingArmy);

            PerformRetreat(army, survivingArmy, out bool destroyed);
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

        // The relocate-or-destroy half of a retreat, shared by a player's voluntary Retreat
        // Army/Retreat All Armies choice (above) and a hero-only army automatically fleeing
        // after its hero evades a Capture Kill Challenge (see BattleScreenUI.Combat.cs's
        // HandleCaptureKillOutcome — same algorithm, per the user's own spec: "по тем же
        // правилам что и отступление из боя"). `destroyed` (out) tells the caller which of the
        // two happened, since each has its own message/next-step to show. `survivingArmy` is
        // whichever army is still standing on `battleHex` once this one leaves (the caller
        // already knows this — ResolveRetreat's own `survivingArmy`, HandleCaptureKillOutcome's
        // own `hunterArmy`) — needed below for the own-base-garrison handover.
        private void PerformRetreat(ArmyData army, ArmyData survivingArmy, out bool destroyed)
        {
            HexCoord battleHex = army.Hex;

            // 2026-08-24 fix (project owner's own root-cause report): captured BEFORE anything
            // below moves `army`, clears its IsGarrison flag, or hands the building over. A
            // retreating army defending its OWN base is the case a retreat must also hand the
            // base itself to the winner — deliberately NOT limited to army.IsGarrison (2026-08-24
            // follow-up, same report): the LAST defender of a base is routinely an ordinary field
            // army instead — a fresh base with no garrison yet, a garrison already lost earlier in
            // the same chained battle, a builder/returning army standing on it — and that army
            // retreating must vacate the base exactly the same way a garrison would. wasGarrison
            // (below) still tracks the narrower "this specific army IS the garrison" case, which
            // only matters for the IsGarrison-clear step just below, not for whether the base
            // changes hands at all (see TryHandoverVacatedBase's own comment for why "no other
            // engageable army of the old owner left on the hex" already covers the "garrison is
            // still there, only a field army retreated" case correctly either way).
            BuildingData defendedBuilding = BuildingRegistry.FindAt(battleHex);
            bool retreatingFromOwnBase = defendedBuilding != null && defendedBuilding.IsBase
                && defendedBuilding.Owner == army.Owner;
            bool wasGarrison = army.IsGarrison;
            PlayerSetupData previousOwner = defendedBuilding?.Owner;

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
            if (relocated)
            {
                // Cleared BEFORE MoveArmy — see this method's own class comment above. Without
                // this, army.IsGarrison stays true after landing on `destination`, an ordinary
                // field hex that isn't this (or any) base, so it keeps counting toward
                // AiTurnController.OwnGarrisonHexes forever. Only ever true for the actual garrison
                // army itself (wasGarrison) — an ordinary field army retreating off someone else's
                // base was never IsGarrison to begin with, nothing to clear.
                if (retreatingFromOwnBase && wasGarrison)
                    army.IsGarrison = false;

                ArmyRegistry.MoveArmy(army, destination);
                hexSelectionController?.RestackArmiesOn(battleHex, null);
                // Same arrival stealth check an ordinary strategic move runs (see
                // HexSelectionController.Movement.cs / Game.Map.StealthSystem).
                Game.Map.StealthSystem.RunChecksForArrival(army, destination);

                // Per the user's own spec: an undefended enemy extraction facility on the
                // destination hex doesn't survive the retreating army walking onto it, same rule
                // an ordinary strategic move already applies (see HexSelectionController.
                // Movement.cs's identical call). Run BEFORE the destination's own RestackArmiesOn
                // below, not after — otherwise that restack resolves the retreating army's offset
                // while the building it's about to destroy still counts as "hasBuilding", leaving
                // it stranded beside a marker that's gone a moment later (see HexSelectionController
                // .Movement.cs's own identical ordering fix for the same bug on an ordinary move).
                BuildingRegistry.CaptureOrDestroyIfUndefended(destination, army.Owner, hexSelectionController, army);
                hexSelectionController?.RestackArmiesOn(destination, null);

                // Per the user's own spec: landing on a hex held by an engageable hostile army
                // or garrison starts a fresh encounter there too — a full battle if both sides
                // still have combat-capable units, a Capture Kill Challenge if only one side
                // does (see BattleInitiator.IsCombatCapable vs IsEngageable, same split
                // HexSelectionController.Movement.cs's own contact check uses). Only enqueued,
                // not shown immediately: this battle's own outcome popup (ResolveRetreat, below)
                // and possible old-hex chain (OnBattleOutcomeAcknowledged) haven't had their turn
                // yet — TryChainPendingRetreatContact drains the queue once an encounter is
                // actually closing, one at a time, so an earlier still-unshown entry (e.g. from a
                // FIRST retreat, while a chained old-hex battle is still playing out) can never be
                // clobbered by a later one.
                //
                // Hunter/target is whichever side actually HAS non-hero units, not assumed to
                // always be the retreating army — per the user's own report: a hero fleeing a
                // Capture Kill Challenge who retreats onto a hex held by a combat-capable enemy
                // army got no follow-up challenge at all, even though that enemy army clearly
                // could (and, per the manual's own hunter rule, should) hunt the escaped hero
                // right there. Silent only when NEITHER side has any non-hero units to hunt with
                // (both hero-only) — same as a hero-only strategic mover contacting a hero-only
                // army (see HexSelectionController.Movement.cs's own identical case).
                ArmyData contactedEnemy = DelayedBattleRegistry.IsHexPending(destination)
                    ? null
                    : BattleInitiator.FindEnemyAt(destination, army.Owner);
                if (contactedEnemy != null)
                {
                    bool armyCanFight = BattleInitiator.IsCombatCapable(army);
                    bool enemyCanFight = BattleInitiator.IsCombatCapable(contactedEnemy);
                    if (armyCanFight || enemyCanFight)
                    {
                        ArmyData hunter = armyCanFight ? army : contactedEnemy;
                        ArmyData target = armyCanFight ? contactedEnemy : army;
                        _pendingRetreatContacts.Enqueue((destination, new List<ArmyData> { hunter, target },
                            !BattleInitiator.IsCombatCapable(target)));
                    }
                }
            }
            else
            {
                army.Members.Clear();
                hexSelectionController?.DeleteArmyIfEmptied(army);
            }

            // 2026-08-24 fix (project owner's own root-cause report — P0 #2): checked here,
            // AFTER the relocate/destroy branches above, not only inside the `relocated` one — a
            // retreat that ends in the army being DESTROYED outright (no valid retreat hex) never
            // goes through FinishBattleEnd/HandleBuildingOnArmyDefeat either, so without this the
            // base stayed with the old owner forever even though nothing of theirs was left
            // standing on it. One shared check covers both outcomes identically.
            if (retreatingFromOwnBase)
                TryHandoverVacatedBase(battleHex, defendedBuilding, previousOwner, survivingArmy);

            // Leaving a Hex Event's own guard fight via Retreat counts as declining the event:
            // guard torn down, entry.Triggered cleared, standalone marker shown — see
            // HexSelectionController.HandleRetreatFromHexEvent (no-op for any retreat unrelated
            // to an in-progress event guard fight).
            hexSelectionController?.HandleRetreatFromHexEvent(battleHex, army?.Owner);
        }

        // Per the user's own spec: a base loses its LAST defender of the old owner (garrison or
        // ordinary field army, whichever just retreated/was destroyed above) hands the base to
        // whoever's left standing there — same "nobody of the old owner left to defend it" rule
        // CaptureOrDestroyIfUndefended already applies to an ordinary undefended walk-in, just
        // reached here via combat+retreat instead. `previousOwner` (captured before this method
        // runs, in PerformRetreat) rather than re-reading `building.Owner` — this method only ever
        // runs once per retreat so the two are identical here, but naming it explicitly keeps this
        // self-contained if that ever changes.
        private void TryHandoverVacatedBase(HexCoord battleHex, BuildingData building, PlayerSetupData previousOwner,
            ArmyData survivingArmy)
        {
            if (building == null || survivingArmy == null || survivingArmy.Owner == null || survivingArmy.Owner == previousOwner)
                return; // no real winner to hand the base to (both sides gone, or it's still the old owner's own army)
            if (!survivingArmy.Hex.Equals(battleHex) || !BattleInitiator.IsEngageable(survivingArmy))
                return; // didn't actually end up holding the hex (retreated/destroyed too)

            // A resident every member of which is hidden from the winner doesn't hold the
            // base (see Game.Map.StealthSystem).
            bool otherDefenderRemains = ArmyRegistry.AllAt(battleHex)
                .Any(resident => resident.Owner == previousOwner
                    && BattleInitiator.IsEngageable(resident, survivingArmy.Owner));
            if (otherDefenderRemains)
                return; // a second defender (e.g. an intact garrison the retreating army wasn't) still holds it

            BuildingRegistry.CaptureOrDestroy(building, survivingArmy.Owner, hexSelectionController);
        }

        private bool TryFindRetreatDestination(ArmyData army, HexCoord battleHex, out HexCoord destination)
        {
            destination = default;
            if (map == null || army?.Owner == null)
                return false;

            BuildingData battleHexBuilding = BuildingRegistry.FindAt(battleHex);
            bool battleHexIsOwnBarracks = battleHexBuilding != null && battleHexBuilding.Owner == army.Owner
                && battleHexBuilding.HasAbility(UnitAbilities.Barracks);

            HexCoord? target = FindNearestOwnBarracksHex(army.Owner, battleHex, excludeBattleHex: battleHexIsOwnBarracks);
            if (target.HasValue)
                return TryPickNeighborToward(battleHex, target.Value, out destination);

            // No Barracks hex to aim for at all (either none exist, or the only one IS the
            // battle hex itself with nowhere else useful to point at) — a random free neighbor.
            return TryPickRandomNeighbor(battleHex, out destination);
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
                if (building.Owner != owner || !building.HasAbility(UnitAbilities.Barracks))
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

        // A single greedy step among battleHex's 6 neighbors — whichever on-map one reduces
        // distance to target the most, not a full path. False only if every neighbor is off the
        // map entirely (see IsRetreatableHex's own comment).
        private bool TryPickNeighborToward(HexCoord battleHex, HexCoord target, out HexCoord destination)
        {
            destination = default;
            int bestDist = int.MaxValue;
            bool found = false;
            foreach (HexCoord candidate in HexGridMath.Neighbors(battleHex))
            {
                if (!IsRetreatableHex(candidate))
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

        private bool TryPickRandomNeighbor(HexCoord battleHex, out HexCoord destination)
        {
            destination = default;
            var options = new List<HexCoord>();
            foreach (HexCoord candidate in HexGridMath.Neighbors(battleHex))
            {
                if (IsRetreatableHex(candidate))
                    options.Add(candidate);
            }
            if (options.Count == 0)
                return false;
            destination = options[UnityEngine.Random.Range(0, options.Count)];
            return true;
        }

        // Only "does this hex actually exist on the map" — who/what is standing on it no longer
        // disqualifies it (see PerformRetreat's own comment on why landing on a hostile hex is
        // now a valid, handled outcome rather than something retreat routing avoids).
        private bool IsRetreatableHex(HexCoord hex)
        {
            return map != null && map.TryGetTerrainAt(hex, out _);
        }
    }
}
