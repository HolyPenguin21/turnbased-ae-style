using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
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
using Unity.Profiling;
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

        // Keep the existing GridLayoutGroup authoritative for cell placement. Only its
        // cell size and spacing are adapted to the available battle area; no second
        // layout system or changes to drag/move animation coordinates are introduced.
        private GridLayoutGroup _responsiveGridLayout;
        private Vector2 _authoredCellSize;
        private Vector2 _authoredSpacing;
        private bool _gridLayoutSizeCaptured;
        private bool _turnQueueLayoutDirty;
        private static readonly ProfilerMarker GridRefreshMarker = new ProfilerMarker("BattleUI.RefreshGrid");
        private Coroutine _moveRoutine;
        private BattleGridCellUI _movingCell;
        private bool _moveLayoutWasEnabled;

        private void RevealCurrentTurnIcon(bool layoutChanged)
        {
            if (!(turnQueueContainer is RectTransform content))
                return;
            ScrollRect scroll = content.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.content != content || scroll.viewport == null)
                return;
            if (layoutChanged || _turnQueueLayoutDirty)
            {
                _turnQueueLayoutDirty = false;
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            }
            float overflow = Mathf.Max(0f, content.rect.height - scroll.viewport.rect.height);
            float offset = 0f;
            if (_turnIndex >= 0 && _turnIndex < _queueIcons.Count)
            {
                RectTransform current = _queueIcons[_turnIndex].transform as RectTransform;
                if (current != null)
                    offset = Mathf.Clamp(-current.anchoredPosition.y, 0f, overflow);
            }
            scroll.StopMovement();
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, offset);
        }

        private void FitBattleGrid()
        {
            if (gridContainer == null || _isAnimatingMove)
                return;

            if (_responsiveGridLayout == null)
                _responsiveGridLayout = gridContainer.GetComponent<GridLayoutGroup>();
            if (_responsiveGridLayout == null)
                return;

            if (!_gridLayoutSizeCaptured)
            {
                _authoredCellSize = _responsiveGridLayout.cellSize;
                _authoredSpacing = _responsiveGridLayout.spacing;
                _gridLayoutSizeCaptured = true;
            }

            RectTransform area = gridContainer as RectTransform;
            if (area == null || area.rect.width <= 0f || area.rect.height <= 0f)
                return;

            float requiredWidth = BattleGrid.Columns * _authoredCellSize.x
                + (BattleGrid.Columns - 1) * _authoredSpacing.x
                + _responsiveGridLayout.padding.horizontal;
            float requiredHeight = BattleGrid.Rows * _authoredCellSize.y
                + (BattleGrid.Rows - 1) * _authoredSpacing.y
                + _responsiveGridLayout.padding.vertical;
            if (requiredWidth <= 0f || requiredHeight <= 0f)
                return;

            float scale = Mathf.Min(
                (area.rect.width - _responsiveGridLayout.padding.horizontal) /
                (requiredWidth - _responsiveGridLayout.padding.horizontal),
                (area.rect.height - _responsiveGridLayout.padding.vertical) /
                (requiredHeight - _responsiveGridLayout.padding.vertical));
            // Preserve authored proportions, avoid giant cards on ultrawide displays.
            scale = Mathf.Clamp(scale, 0.25f, 1.35f);
            Vector2 cellSize = _authoredCellSize * scale;
            Vector2 spacing = _authoredSpacing * scale;
            if (_responsiveGridLayout.cellSize != cellSize ||
                _responsiveGridLayout.spacing != spacing)
            {
                _responsiveGridLayout.cellSize = cellSize;
                _responsiveGridLayout.spacing = spacing;
            }
        }

        private void OnRectTransformDimensionsChange()
        {
            _turnQueueLayoutDirty = true;
            if (gridContainer == null || _isAnimatingMove)
                return;
            FitBattleGrid();
            RevealCurrentTurnIcon(layoutChanged: false);
        }

        private void RefreshGrid()
        {
            if (_isPresentingAttack || _presentationSuppressed || _isAnimatingMove
                || gridContainer == null || gridCellPrefab == null || _grid == null)
                return;

            using var sample = GridRefreshMarker.Auto();
            bool createCells = _cells.Count == 0;
            if (createCells) FitBattleGrid();

            // Legal-target hints only make sense once a real round is underway (not Arranging)
            // and only for the local human's own current unit — an AI turn has no player input to
            // hint at.
            bool canAct = !_arranging && IsHumanAction(_currentActingUnit);

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

                    bool isLegalMoveTarget = canAct && unit == null && _battleEngine != null
                        && _battleEngine.CanMoveUnit(_currentActingUnit, row, col);
                    bool isLegalAttackTarget = canAct && unit != null && _battleEngine != null
                        && _battleEngine.CanGroundAttack(_currentActingUnit, unit);

                    BattleGridCellUI cell;
                    if (createCells)
                    {
                        cell = Instantiate(gridCellPrefab, gridContainer);
                        Game.Audio.SceneUIAudioBinder.BindCreatedRoot(cell);
                        cell.Setup(this, unit, row, col, draggable, isActingUnit, isLegalMoveTarget, isLegalAttackTarget);
                        _cells.Add(cell);
                    }
                    else
                    {
                        cell = _cells[row * BattleGrid.Columns + col];
                        cell.Refresh(unit, draggable, isActingUnit, isLegalMoveTarget, isLegalAttackTarget);
                    }
                    if (unit != null && _healEffectStarts.TryGetValue(unit, out float started)
                        && Time.unscaledTime - started < BattleSkillEffect.Duration)
                        BattleSkillEffect.Show(cell.transform, started, BattleSkillEffect.Kind.Heal);
                    if (unit != null && _drillEffectStarts.TryGetValue(unit, out float drillStarted)
                        && Time.unscaledTime - drillStarted < BattleSkillEffect.Duration)
                        BattleSkillEffect.Show(cell.transform, drillStarted, BattleSkillEffect.Kind.Drill);
                }
        }

        // Any modal on top of the battle grid that OnCellClicked must not act underneath — chiefly
        // outcomePopup: EndTurn calls ResolveRetreat() directly once the grace round's last unit
        // finishes, without ever clearing _currentActingUnit first (that only happens later, via
        // ResetBattlePanel), so "The enemy retreats." can be showing while _currentActingUnit
        // still points at the unit that just acted and the grid still highlights its legal
        // targets — see the user's own report: still able to attack with that unit while the
        // outcome popup sits on top. attackPopup covers the equivalent case for its own duel (a
        // click during Fate spending shouldn't start ANOTHER attack with the same still-current
        // unit); roundStartPopup/battleContactPopup are the other two sub-popups that can show
        // while _currentActingUnit is left stale from before them.
        private bool AnyBattlePopupShowing => _isPresentingAttack ||
            (attackPopup != null && attackPopup.IsShowing)
            || (outcomePopup != null && outcomePopup.IsShowing)
            || (roundStartPopup != null && roundStartPopup.IsShowing)
            || (battleContactPopup != null && battleContactPopup.IsShowing);

        // Click-to-act for the local human's current unit — no-op for anything else (Arranging
        // uses its own drag-and-drop, an AI turn has no player input, and a click on a cell that
        // isn't a legal move/attack target for the current unit is just an inspect, already
        // handled by BattleGridCellUI.OnPointerClick calling ShowUnitDetail unconditionally).
        public void OnCellClicked(BattleGridCellUI cell)
        {
            if (_arranging || _isAnimatingMove || cell == null || _currentActingUnit == null)
                return;
            if (AnyBattlePopupShowing)
                return;
            if (!IsHumanAction(_currentActingUnit))
                return;
            if (_battleEngine == null
                || !_grid.TryFindPosition(_currentActingUnit, out int actorRow, out int actorCol))
                return;

            if (cell.Unit == null)
            {
                if (_battleEngine.CanMoveUnit(_currentActingUnit, cell.Row, cell.Col))
                    PerformMove(actorRow, actorCol, cell.Row, cell.Col);
                return;
            }

            if (_battleEngine.CanGroundAttack(_currentActingUnit, cell.Unit))
                BeginAttack(_currentActingUnit, cell.Unit);
        }

        private BattleGridCellUI FindCell(int row, int col)
        {
            if (!BattleGrid.InBounds(row, col)) return null;
            int index = row * BattleGrid.Columns + col;
            return index < _cells.Count ? _cells[index] : null;
        }

        private void PerformMove(int fromRow, int fromCol, int toRow, int toCol)
        {
            if (_isAnimatingMove) return;
            BattleGridCellUI fromCell = FindCell(fromRow, fromCol);
            BattleGridCellUI toCell = FindCell(toRow, toCol);
            if (!HasInteractiveParticipant || _presentationSuppressed || moveAnimDuration <= 0f
                || fromCell == null || toCell == null)
            {
                CompleteMove(fromRow, fromCol, toRow, toCol);
                return;
            }

            _isAnimatingMove = true;
            _movingCell = fromCell;
            if (_responsiveGridLayout != null)
            {
                _moveLayoutWasEnabled = _responsiveGridLayout.enabled;
                _responsiveGridLayout.enabled = false;
            }
            _moveRoutine = StartBattleRoutine(AnimateThenMove(fromCell, toCell));
        }

        private IEnumerator AnimateThenMove(BattleGridCellUI from, BattleGridCellUI to)
        {
            yield return from.AnimateMoveTo(to.RectTransform, moveAnimDuration);
            RestoreMoveLayout();
            CompleteMove(from.Row, from.Col, to.Row, to.Col);
        }

        private void CompleteMove(int fromRow, int fromCol, int toRow, int toCol)
        {
            if (_battleEngine == null
                || !_battleEngine.TryMoveUnit(_currentActingUnit, fromRow, fromCol, toRow, toCol))
            {
                RefreshGrid();
                return;
            }
            EndTurn();
        }

        private void CancelMovePresentation()
        {
            StopBattleRoutine(_moveRoutine);
            RestoreMoveLayout();
        }

        private void RestoreMoveLayout()
        {
            if (!_isAnimatingMove) return;
            _moveRoutine = null;
            if (_movingCell != null) _movingCell.RestoreMovePose();
            _movingCell = null;
            if (_responsiveGridLayout != null) _responsiveGridLayout.enabled = _moveLayoutWasEnabled;
            _isAnimatingMove = false;
            // Apply any viewport change that occurred during the slide.
            FitBattleGrid();
        }

        // Resolves a drag started on `dragged` (see BattleGridCellUI.OnEndDrag) — the drop only
        // succeeds while Arranging and only onto another cell within the SAME local player's own
        // rows (may be empty or occupied; occupied just swaps the two). Anywhere else (enemy
        // rows, the neutral row, off the grid entirely) snaps back to where it was picked up
        // from by simply doing nothing here.
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
                if (_battleEngine == null
                    || !_battleEngine.TrySwapDeployment(dragged.Row, dragged.Col, cell.Row, cell.Col,
                        _localFrontRow, _localBackRow))
                    continue;
                RefreshGrid();
                return;
            }
        }

        // Shown for whoever's currently up in the turn order by default, or any unit clicked
        // directly in the grid (see BattleGridCellUI.OnPointerClick) — same "click to inspect"
        // uses the same detail formatter as the army viewer with this battle's defense bonus.
        public void ShowUnitDetail(UnitData unit)
        {
            if (detailArt != null)
            {
                Sprite art = unit?.DetailArt;
                detailArt.sprite = art;
                detailArt.gameObject.SetActive(art != null);
            }
            if (detailText == null)
                return;
            if (unit == null)
            {
                detailText.text = string.Empty;
                return;
            }

            // Defense includes the same terrain/Base-building bonus BeginAttack actually rolls
            // with (only ever nonzero for the battle's original _defender — see
            // GetDisplayedDefenseBonus's own comment), so this always matches the real tokens pool.
            detailText.text = UnitDetailFormatter.Format(unit, gameConfig, GetDisplayedDefenseBonus(unit),
                GetDisplayedAttackRange(unit));
        }
    }
}

