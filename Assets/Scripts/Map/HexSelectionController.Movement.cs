using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Styles;
using Game.Terrain;
using Game.Turns;
using Game.UI;
using Game.Units;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Game.Map
{
    // Move preview (hover) and move order (right-click) half of HexSelectionController — split
    // out purely for file size, same reasoning as HexSelectionController.Factory.cs's own
    // comment. Shares _selectedArmy/_pathArrow/_lastPreviewedHover and the layout helpers
    // (ResolveArmyOffset, RestackArmiesOn, etc.) with the main file, which owns them since every
    // part of this class uses them, not just this one.
    public partial class HexSelectionController
    {
        private void UpdateMovePreview(HexCoord? hoverCoord)
        {
            ArmyData army = GetSelectedArmy();
            // No army (or it's the garrison, which can never move), or it's already mid-move
            // (see TryIssueMoveOrder) — nothing to preview. army.Hex only updates once the move
            // actually finishes (ArmyRegistry.MoveArmy), so without this check the preview would
            // keep pathfinding from the stale origin hex for the whole animation. Once the move
            // ends, SelectHex re-runs on the destination and a fresh hover elsewhere shows a new
            // arrow again, same as any other selection change.
            if (_selectedArmy == null || _selectedArmy.IsMoving || army == null || army.IsGarrison
                || !hoverCoord.HasValue || hoverCoord.Value.Equals(army.Hex))
            {
                if (_lastPreviewedHover.HasValue)
                {
                    _lastPreviewedHover = null;
                    HidePathPreview();
                }
                return;
            }

            if (_lastPreviewedHover.HasValue && _lastPreviewedHover.Value.Equals(hoverCoord.Value))
                return; // same hex as last frame — nothing changed, don't re-run pathfinding

            _lastPreviewedHover = hoverCoord;

            HexPath path = HexPathfinder.FindPath(map, army.Hex, hoverCoord.Value, AvoidEnemyHex(army));
            if (path == null)
            {
                HidePathPreview();
                return;
            }

            // Out of shared move points — still worth telling the player the cost, but don't
            // draw a route they can't actually order this turn.
            if (army.CurrentMovement <= 0)
            {
                if (_pathArrow != null)
                    _pathArrow.Hide();
            }
            else
            {
                // The preview arrow is cosmetic — a bug in its mesh/geometry code must never
                // take down hex selection or move orders with it. Update() runs this before
                // handling clicks, so an uncaught exception here would otherwise skip the rest
                // of that frame's input handling entirely.
                try
                {
                    ShowPathArrow(path, army);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                    if (_pathArrow != null)
                        _pathArrow.Hide();
                }
            }
        }

        private void ShowPathArrow(HexPath path, ArmyData army)
        {
            if (_pathArrow == null)
            {
                var arrowObject = new GameObject("MoveArrow");
                arrowObject.transform.SetParent(transform, false);
                _pathArrow = arrowObject.AddComponent<MoveArrowMarker>();
                // AddComponent runs Awake() synchronously, before there's any chance to hand
                // over gameConfig — Initialize is the actual place MoveArrowMarker pulls its
                // tunables (MoveArrowStyle) from.
                _pathArrow.Initialize(gameConfig);
            }

            // No longer the mover's own player colour — technical green for a plain move,
            // technical red for a path that ends in enemy contact (see Game.Combat.
            // BattleInitiator) — same truncation TryIssueMoveOrder itself will apply, so the
            // preview always shows exactly where the army will actually stop.
            path = TruncateAtEnemyContact(path, army, out ArmyData enemyArmy);
            Color color = enemyArmy != null ? gameConfig.moveArrowAttackColor : gameConfig.moveArrowMoveColor;

            var points = new List<Vector3>(path.Hexes.Count);
            foreach (HexCoord hex in path.Hexes)
                points.Add(map.HexToWorld(hex));
            int apCost = army.HasActivatedThisTurn ? 0 : army.ActivationApCost;
            _pathArrow.Show(points, path.TotalCost, apCost, color);
        }

        // Truncates `path` at the first hex (after the origin) holding a combat-capable enemy
        // army, if any — shared by TryIssueMoveOrder (the actual order) and ShowPathArrow (its
        // hover preview). Only checked for a combat-capable mover itself (a hero-only army isn't
        // "combat capable" per the manual and has its own separate — not yet implemented —
        // capture/kill handling), in which case this is a no-op and returns `path` unchanged.
        private static HexPath TruncateAtEnemyContact(HexPath path, ArmyData mover, out ArmyData enemyArmy)
        {
            enemyArmy = null;
            if (!BattleInitiator.IsCombatCapable(mover))
                return path;
            for (int i = 1; i < path.Hexes.Count; i++)
            {
                enemyArmy = BattleInitiator.FindEnemyAt(path.Hexes[i], mover.Owner);
                if (enemyArmy != null)
                    return new HexPath(path.Hexes.GetRange(0, i + 1), path.TotalCost);
            }
            return path;
        }

        // HexPathfinder's own soft-avoidance hook (see its own comment) — routes `mover` around
        // a hex holding an enemy army when a reasonable detour exists, instead of always taking
        // the geometrically shortest route straight at/through it and only finding out at
        // TruncateAtEnemyContact time that it has to stop short (see the user's own request:
        // the shortest route often isn't the one that actually lets the army get furthest this
        // turn, once contact truncation is accounted for). Not gated on IsCombatCapable like
        // TruncateAtEnemyContact — a hero-only mover benefits from steering clear too, even
        // though it never triggers a stop there itself.
        private static System.Func<HexCoord, bool> AvoidEnemyHex(ArmyData mover)
        {
            return hex => BattleInitiator.FindEnemyAt(hex, mover.Owner) != null;
        }

        private void HidePathPreview()
        {
            if (_pathArrow != null)
                _pathArrow.Hide();
        }

        private ArmyData GetSelectedArmy()
        {
            return _selectedArmy?.Data;
        }

        private void TryIssueMoveOrder(HexCoord destination)
        {
            if (_selectedArmy == null || _selectedArmy.IsMoving)
                return;

            // The garrison specifically can never move at all (it's anchored to its Barracks
            // building, not a mobile force) — assign units to a real army first (see the
            // garrison button on HexInfoPanelUI, or a precise click on its own marker).
            ArmyData army = GetSelectedArmy();
            if (army == null || army.IsGarrison)
            {
                turnController?.ShowSpawnHint($"{army?.Name ?? "This army"} can't move — assign its units to a real army first.");
                return;
            }

            // An army sharing its hex with a combat-capable enemy army can't just walk away —
            // the only way out is retreating from battle (see the manual's Retreat Challenge),
            // which doesn't exist yet, so for now this hex is a dead end until real combat
            // resolution can free it. See Game.Combat.BattleInitiator.
            if (BattleInitiator.FindEnemyAt(army.Hex, army.Owner) != null)
            {
                turnController?.ShowSpawnHint($"{army.Name} is locked in combat and can't move away.");
                return;
            }
            if (army.CurrentMovement <= 0)
            {
                turnController?.ShowSpawnHint($"{army.Name} has no movement points left this turn.");
                return;
            }
            if (destination.Equals(army.Hex))
                return;

            HexPath path = HexPathfinder.FindPath(map, army.Hex, destination, AvoidEnemyHex(army));
            if (path == null)
                return;

            // "Initiating Battle" (see Game.Combat.BattleInitiator) — moving onto a hex with a
            // combat-capable enemy army starts a fight there instead of continuing past it, even
            // if the original destination was further along the path. Shared with the hover
            // preview arrow (see ShowPathArrow) so it always shows exactly where the army will
            // actually stop.
            path = TruncateAtEnemyContact(path, army, out _);

            // An army only ever stops short of a hex it can't fully afford (see
            // ArmyController.MoveRoutine) — never enters it partway "in debt" any more. Caught
            // here specifically for the very next step: if that alone is already unaffordable,
            // MoveAlong would do nothing at all and the player would see no feedback for why the
            // order silently failed.
            map.TryGetTerrainAt(path.Hexes[1], out TerrainTypeEntry firstStepEntry);
            int firstStepCost = firstStepEntry != null ? Mathf.Max(1, firstStepEntry.moveCost) : 1;
            if (army.CurrentMovement < firstStepCost)
            {
                turnController?.ShowSpawnHint(
                    $"Not enough movement points to enter that hex ({firstStepCost} needed, {army.CurrentMovement} left).");
                return;
            }

            PlayerRoot ownerRoot = PlayerRootRegistry.FindFor(army.Owner);
            if (ownerRoot == null)
                return;

            // AP (ArmyData.ActivationApCost — the sum of every member's own ActivationApCost,
            // so a bigger army costs more to get moving) is only spent the first time this
            // army is given a move order in a turn — every move order after that, for the
            // rest of the turn, costs MoveCurrent only. See ArmyData.HasActivatedThisTurn.
            bool needsActivation = !army.HasActivatedThisTurn;
            if (needsActivation && !ownerRoot.CanSpendActionPoints(army.ActivationApCost))
            {
                turnController?.ShowSpawnHint($"Not enough action points to move {army.Name} ({army.ActivationApCost} AP needed).");
                return;
            }
            if (needsActivation)
            {
                ownerRoot.SpendActionPoints(army.ActivationApCost);
                army.HasActivatedThisTurn = true;
            }

            HexCoord originHex = army.Hex;
            ArmyController movingArmy = _selectedArmy;

            // Let the idle hover/pulse animation ease back to its resting pose first — jumping
            // straight from mid-bob/mid-pulse into the move animation was what made movement
            // look jerky right as it started. SettleThen already claims IsMoving immediately,
            // so a second order can't sneak in during that brief wait.
            movingArmy.SettleThen(() =>
            {
                movingArmy.MoveAlong(map, path.Hexes, hex => ResolveArmyOffset(hex, movingArmy), () =>
                {
                    // The army can stop short partway along the path, the moment the next hex
                    // would cost more than what's left — movingArmy.CurrentHex (tracked live
                    // during MoveAlong, see ArmyController) reflects wherever it actually ended
                    // up, which is NOT necessarily `destination`. ArmyRegistry.MoveArmy is the
                    // one place Data.Hex itself actually changes, once the whole move is
                    // finished.
                    HexCoord actualHex = movingArmy.CurrentHex;
                    ArmyRegistry.MoveArmy(army, actualHex);

                    // The origin hex's own layout (a building recentring once the last army
                    // actually leaves, in particular) only reads correctly once MoveArmy above
                    // has re-keyed `army` away from it — the earlier RestackArmiesOn(originHex,
                    // ...) call, made when the order was first given, still saw `army`
                    // registered there and couldn't reflect this.
                    RestackArmiesOn(originHex, null);

                    // The user's own Siege spec, undefended case: a building with no engageable
                    // army of its own owner present at all just changes hands/gets destroyed the
                    // moment an enemy army finishes moving onto its hex — no fight to trigger,
                    // nothing to delay, there was never anyone there to put one up (see
                    // BattleScreenUI.Combat.cs's HandleBuildingOnArmyDefeat for the "army got
                    // wiped mid-fight" case this doesn't cover). Runs regardless of whether an
                    // actual army contact ALSO fires below — a third player's building could sit
                    // on the same hex as someone else's army.
                    BuildingData contactedBuilding = BuildingRegistry.FindAt(actualHex);
                    if (contactedBuilding != null && contactedBuilding.Owner != null && contactedBuilding.Owner != army.Owner)
                    {
                        bool ownerDefended = false;
                        foreach (ArmyData resident in ArmyRegistry.AllAt(actualHex))
                            if (resident.Owner == contactedBuilding.Owner && BattleInitiator.IsEngageable(resident))
                            {
                                ownerDefended = true;
                                break;
                            }
                        if (!ownerDefended)
                            BuildingRegistry.CaptureOrDestroy(contactedBuilding, army.Owner);
                    }

                    // Re-checked fresh against `actualHex`, NOT the `enemyArmy` truncation found
                    // before the move even started — the army can run out of shared move points
                    // and stop well short of the hex TruncateAtEnemyContact had in mind (e.g. it
                    // takes 2 but only 1 remains), in which case there was never any real contact
                    // and this must stay silent.
                    //
                    // BattleInitiator.IsCombatCapable(army) (the MOVER, not the target) is
                    // required in addition to FindEnemyAt finding something — a hero-only mover
                    // can't fight a Ground Combat round OR hunt a Capture Kill Challenge without
                    // a skill this project doesn't have yet (see BattleAttackPopupUI.
                    // BeginCaptureKill's own note), so contact simply doesn't trigger anything
                    // for one; it just finishes its move as if the hex were empty.
                    //
                    // DelayedBattleRegistry.IsHexPending additionally guards against re-offering
                    // a Fight/Delay choice for a target already reserved for a different pending
                    // battle at this hex (e.g. a different attacking army delayed against it
                    // earlier this same turn) — per the user's own call, a reserved army can't be
                    // signed up for a second one. Left unresolved here on purpose; the mover just
                    // finishes onto the hex and coexists with it for now — GameTurnController's
                    // own end-of-turn sweep is what eventually forces this pairing too, once the
                    // earlier one's been drained.
                    ArmyData actualEnemyContact = DelayedBattleRegistry.IsHexPending(actualHex)
                        ? null
                        : BattleInitiator.FindEnemyAt(actualHex, army.Owner);
                    if (actualEnemyContact != null && battleContactPopup != null && BattleInitiator.IsCombatCapable(army))
                    {
                        // Not a full Deselect() (that would also clear _selectedArmy, still
                        // needed below in the general case) — but the ORIGIN hex's own selection
                        // must still go: the army has already left it, and the general
                        // re-select-at-actualHex logic below is skipped entirely on contact (see
                        // the else branch's own comment), so nothing else would ever clear it.
                        // Left alone, its highlight/info panels — and the multi-army button row,
                        // if the origin hex still has 2+ of the player's own armies left on it —
                        // kept pointing at a hex/roster the army no longer occupies/belongs to.
                        _selectedHex = null;
                        if (highlight != null) highlight.Hide();
                        if (infoPanel != null) infoPanel.Hide();
                        if (armyInfoPanel != null) armyInfoPanel.Hide();
                        if (armyButtonRow != null) armyButtonRow.Hide();
                        var participants = new List<ArmyData> { army, actualEnemyContact };
                        // A hero-only contact (see BattleInitiator.IsEngageable vs
                        // IsCombatCapable) has nothing for a normal Tactical Battle Module round
                        // to do — no acting units on that side, nothing to click/attack — so it
                        // skips the grid entirely and goes straight to a Capture Kill Challenge
                        // sequence instead (see BattleScreenUI.BeginCaptureKillEncounter).
                        bool targetHeroOnly = !BattleInitiator.IsCombatCapable(actualEnemyContact);
                        battleContactPopup.Show(actualHex, participants,
                            onFight: () =>
                            {
                                if (targetHeroOnly)
                                    battleScreen?.BeginCaptureKillEncounter(army, actualEnemyContact, null);
                                else
                                    battleScreen?.Show(actualHex, participants, null);
                            },
                            onDelay: () => DelayedBattleRegistry.Add(new PendingBattle { Hex = actualHex, Participants = participants }));
                    }
                    // Re-arm the hover/pulse animation once the move finishes, if it's still the
                    // active selection (the player may have selected something else meanwhile) —
                    // and follow the selection itself to wherever the army actually stopped,
                    // instead of leaving the highlight/info panels behind on wherever it was
                    // originally clicked from. Skipped entirely on contact — SelectHex would just
                    // re-show the very panels just hidden above, right underneath the popup.
                    else if (_selectedArmy == movingArmy)
                    {
                        movingArmy.SetSelected(true);
                        SelectHex(actualHex, preserveSelection: true);
                    }

                    // The army just settled on `actualHex` — anyone else already resting
                    // there (e.g. it now forms a two-different-owners pair) needs to shift to
                    // match.
                    RestackArmiesOn(actualHex, movingArmy);
                });
                // Leaving originHex can just as easily change what's left behind there (e.g. a
                // pair collapsing back down to one army, which should re-centre).
                RestackArmiesOn(originHex, movingArmy);
            });

            HidePathPreview();
            _lastPreviewedHover = null;
        }
    }
}
