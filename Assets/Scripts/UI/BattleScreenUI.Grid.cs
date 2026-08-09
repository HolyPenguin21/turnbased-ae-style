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
    // Battle-grid rendering and click/drag handling half of BattleScreenUI — split out purely
    // for file size, same reasoning as HexSelectionController's own multi-file split. Shares
    // this class's fields (_grid/_cells/_arranging/etc.) and the state-machine methods in the
    // main file (EndTurn) and BattleScreenUI.Combat.cs (BeginAttack) automatically via `partial`.
    public partial class BattleScreenUI
    {
        private bool IsLocalRow(int row) => _localArmy != null && (row == _localFrontRow || row == _localBackRow);

        // Orthogonally adjacent to the actor's own current cell AND within the actor's own two
        // rows or the shared neutral row — never directly into the enemy's own two rows (same
        // same-side restriction the Arrangement phase's own drag-and-drop already enforces, just
        // for a single active unit moving one step instead of free rearranging). Row/adjacency
        // helpers live on BattleGrid itself now (shared with BattleAi's own movement logic).
        private bool IsAdjacentOwnSide(UnitData actor, int row, int col)
        {
            if (actor == null || _grid == null || !_grid.TryFindPosition(actor, out int actorRow, out int actorCol))
                return false;
            if (!BattleGrid.IsOrthogonallyAdjacent(actorRow, actorCol, row, col))
                return false;
            return BattleGrid.IsAttackerSideRow(actorRow) ? (BattleGrid.IsAttackerSideRow(row) || row == BattleGrid.NeutralRow)
                                                            : (BattleGrid.IsDefenderSideRow(row) || row == BattleGrid.NeutralRow);
        }

        private void RefreshGrid()
        {
            UIListUtility.DestroyAndClear(_cells);
            if (gridContainer == null || gridCellPrefab == null || _grid == null)
                return;

            // Legal-target hints only make sense once a real round is underway (not Arranging)
            // and only for the local human's own current unit — an AI turn has no player input to
            // hint at.
            bool canAct = !_arranging && _currentActingUnit != null
                && _currentActingUnit.Owner != null && _currentActingUnit.Owner.IsHuman;
            int actorRow = -1, actorCol = -1;
            if (canAct)
                _grid.TryFindPosition(_currentActingUnit, out actorRow, out actorCol);

            for (int row = 0; row < BattleGrid.Rows; row++)
                for (int col = 0; col < BattleGrid.Columns; col++)
                {
                    // During Arrangement, only the local player's own two rows are shown as
                    // they really are on the grid — the opponent's side renders as empty cells
                    // regardless of what's actually placed there (see the user's own spec: the
                    // player doesn't see the enemy's cards before committing to a layout).
                    bool hideForArrangement = _arranging && !IsLocalRow(row);
                    UnitData unit = hideForArrangement ? null : _grid.Get(row, col);
                    bool draggable = _arranging && _arrangeInteractive && IsLocalRow(row) && unit != null;
                    bool isActingUnit = unit != null && unit == _currentActingUnit;

                    bool isLegalMoveTarget = canAct && unit == null && IsAdjacentOwnSide(_currentActingUnit, row, col);
                    bool isLegalAttackTarget = canAct && unit != null && unit.Owner != _currentActingUnit.Owner
                        && BattleGrid.IsInRange(actorRow, actorCol, row, col, _currentActingUnit.Range);

                    BattleGridCellUI cell = Instantiate(gridCellPrefab, gridContainer);
                    cell.Setup(this, unit, row, col, draggable, isActingUnit, isLegalMoveTarget, isLegalAttackTarget);
                    _cells.Add(cell);
                }
        }

        // Click-to-act for the local human's current unit — no-op for anything else (Arranging
        // uses its own drag-and-drop, an AI turn has no player input, and a click on a cell that
        // isn't a legal move/attack target for the current unit is just an inspect, already
        // handled by BattleGridCellUI.OnPointerClick calling ShowUnitDetail unconditionally).
        public void OnCellClicked(BattleGridCellUI cell)
        {
            if (_arranging || _isAnimatingMove || cell == null || _currentActingUnit == null)
                return;
            if (_currentActingUnit.Owner == null || !_currentActingUnit.Owner.IsHuman)
                return;
            if (!_grid.TryFindPosition(_currentActingUnit, out int actorRow, out int actorCol))
                return;

            if (cell.Unit == null)
            {
                if (IsAdjacentOwnSide(_currentActingUnit, cell.Row, cell.Col))
                    PerformMove(actorRow, actorCol, cell.Row, cell.Col);
                return;
            }

            if (cell.Unit.Owner == _currentActingUnit.Owner)
                return;
            if (BattleGrid.IsInRange(actorRow, actorCol, cell.Row, cell.Col, _currentActingUnit.Range))
                BeginAttack(_currentActingUnit, cell.Unit);
        }

        private BattleGridCellUI FindCell(int row, int col)
        {
            foreach (BattleGridCellUI cell in _cells)
                if (cell.Row == row && cell.Col == col)
                    return cell;
            return null;
        }

        private void PerformMove(int fromRow, int fromCol, int toRow, int toCol)
        {
            if (_isAnimatingMove)
                return;
            StartCoroutine(AnimateThenMove(fromRow, fromCol, toRow, toCol));
        }

        // Fast but smooth, per the user's own spec — the moving cell's own card slides from the
        // source cell to the destination (see BattleGridCellUI.AnimateMoveTo), then the actual
        // grid swap/rebuild/turn advance happens all at once, same as before. Input is blocked
        // for the animation's short duration (see _isAnimatingMove) so a second click can't
        // queue up mid-slide. gridContainer's GridLayoutGroup is disabled for that same span —
        // otherwise it fights the manual position Lerp and, worse, reflows every other cell the
        // instant sibling order/state changes underneath it (that's what made neighbouring units
        // visibly jump) — it's re-enabled just before RefreshGrid rebuilds everything anyway.
        private IEnumerator AnimateThenMove(int fromRow, int fromCol, int toRow, int toCol)
        {
            _isAnimatingMove = true;
            BattleGridCellUI fromCell = FindCell(fromRow, fromCol);
            BattleGridCellUI toCell = FindCell(toRow, toCol);
            GridLayoutGroup layoutGroup = gridContainer != null ? gridContainer.GetComponent<GridLayoutGroup>() : null;
            if (fromCell != null && toCell != null)
            {
                if (layoutGroup != null)
                    layoutGroup.enabled = false;
                yield return StartCoroutine(fromCell.AnimateMoveTo(toCell.RectTransform, moveAnimDuration));
                if (layoutGroup != null)
                    layoutGroup.enabled = true;
            }

            _grid.Swap(fromRow, fromCol, toRow, toCol);
            _isAnimatingMove = false;
            RefreshGrid();
            EndTurn();
        }

        // Resolves a drag started on `dragged` (see BattleGridCellUI.OnEndDrag) — the drop only
        // succeeds while Arranging and only onto another cell within the SAME local player's own
        // rows (may be empty or occupied; occupied just swaps the two). Anywhere else (enemy
        // rows, the neutral row, off the grid entirely) snaps back to where it was picked up
        // from by simply doing nothing here — RefreshGrid was never called, so the cell's own
        // Setup data (and therefore its rendered position/content) is untouched.
        public void TryDropOnCell(BattleGridCellUI dragged, Vector2 screenPosition)
        {
            if (!_arranging || !_arrangeInteractive || dragged == null || !IsLocalRow(dragged.Row))
                return;

            Camera cam = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;
            foreach (BattleGridCellUI cell in _cells)
            {
                if (cell == dragged || !IsLocalRow(cell.Row))
                    continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(cell.RectTransform, screenPosition, cam))
                    continue;

                _grid.Swap(dragged.Row, dragged.Col, cell.Row, cell.Col);
                RefreshGrid();
                return;
            }
        }

        // Shown for whoever's currently up in the turn order by default, or any unit clicked
        // directly in the grid (see BattleGridCellUI.OnPointerClick) — same "click to inspect"
        // pattern as ArmyViewerModalUI.ShowUnitDetail, just this screen's own copy since the
        // framing here is pure combat stats, not army/capacity.
        public void ShowUnitDetail(UnitData unit)
        {
            if (detailArt != null)
            {
                detailArt.sprite = unit != null ? unit.Art : null;
                detailArt.gameObject.SetActive(unit != null);
            }
            if (detailText == null)
                return;
            if (unit == null)
            {
                detailText.text = string.Empty;
                return;
            }

            string text = $"{unit.Name}\n" +
                $"Attack {unit.Attack}\n" +
                $"Defense {unit.Defense}\n" +
                $"Resistance {unit.Resistance}\n" +
                $"Range {unit.Range}\n" +
                $"HP {unit.HitPointsCurrent}/{unit.HitPointsMax}\n" +
                $"Move {unit.MoveCurrent}/{unit.MoveMax}\n" +
                $"Initiative {unit.Initiative}";
            if (unit.IsHero)
                text += $"\nCommand Rating: {unit.CommandRating}\nFate: {unit.Fate}";
            string abilities = gameConfig != null ? gameConfig.FormatAbilitiesDetailed(unit.Abilities) : null;
            if (!string.IsNullOrEmpty(abilities))
                text += $"\n{abilities}";
            detailText.text = text;
        }
    }
}
