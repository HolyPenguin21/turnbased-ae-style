# Runtime UI responsiveness

Approved scope: game setup, collection card columns, and the map hand. Keep the authored
1280x720 appearance and existing gameplay; use anchors and layout groups first.

- [x] Setup: full 86-unit two-line player rows; scroll within a bounded viewport; 2..6 players.
- [x] Collection: fixed side columns anchored to their respective edges, flexible middle;
      four proportional cells; pooled cards resize without recreation or text distortion.
- [x] Hand: seven relative slots, one layout scale shared by ordinary/tactic/debug cards,
      hover/drag/flip, separators, scrolling and reorder hit geometry. Preserve the direct
      handContainer parent so existing input coordinates and presentation tests stay valid.
- [x] Verify source: authored geometry and links at 16:9, 16:10, ultrawide and 4:3;
      actual C# math, all C# syntax; independent bottom-up review with no confirmed defects.
- [ ] Native acceptance: Unity interaction tests, full EditMode suite and Game View checks.

Primary files: MainMenu.unity, PlayerRow.prefab, CollectionCard.prefab,
CollectionScreensUI.cs, CollectionCardCellView.cs, CollectionWindowBuilder.cs,
CollectionUIElements.cs, CardHandUI.cs, CardHandUI.Tactics.cs, CardUI.cs.

Native Unity 6000.5.4f1 and imported assemblies are not installed in this environment.
Source validation and standalone math checks cannot substitute for a Game View acceptance run.
Do not claim a native run or merge/push as part of implementation.

## Follow-up bottom-up audit

- Separator layer now resynchronizes its rect with the hand; pin vertical offsets use the
  card's uniform scale relative to its centre rather than a separate container-height ratio.
- Added rows are revealed after layout, while initial/reopened lists start at the top.
  Removed/replaced rows are deactivated before deferred destruction so they cannot affect layout.
- A hand resize cancels a carried card, restores its slot and clears cached drag/drop geometry.
  The subsequent pointer-up does not attempt a play.
- Status text has its own left header area, avoiding overlap with the centred title at 4:3/16:10.
- Prefab GUID/component references from MainMenu, Game and GameConfig were checked against
  current master. The older broad audit's ResourceBar root-bounds failure exists identically
  before these changes (2.2000122 units of top overflow); it was not silently waived or changed.
- Focused source tests and actual C# geometry run here; native interaction tests remain unrun.
