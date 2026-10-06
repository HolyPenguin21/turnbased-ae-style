# Hex panel motion implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan inline.

**Goal:** Apply the owner's three corrections on top of f38ae755 without changing the authored scene layout.

**Architecture:** HexSelectionController decides visibility and counts unextracted resource types independently of build eligibility. HexInfoPanelUI animates only anchoredPosition.y and keeps four section buttons active, using interactable for eligibility. ResourceActionRowUI stacks extraction actions vertically inside its authored container.

**Tech Stack:** Unity 6000.5.4f1, C#, uGUI, NUnit EditMode.

**Spec:** Owner's corrections in this conversation: initially disabled; show for building/garrison; offsets 0/31/56/81/107 for 4/3/2/1/0 unextracted resource types; screen-height proportions; quick smooth vertical movement; fixed square buttons.

## Global constraints
- Preserve scene, prefab, GUIDs, horizontal position, anchors, size and square-button layout.
- Reference screen height is the scene CanvasScaler's 768 units. Fully open anchored Y is zero.
- Existing public section-button methods remain callable.
- Unity is unavailable here; record that compilation, EditMode and game checks cannot run.

## Review focus
- Fast selection changes retarget current motion without restarting from below screen.
- Empty hexes stay hidden even if resource build actions exist.
- Missing hero affects action eligibility, not unextracted resource count.
- Resize uses current canvas height rather than fixed pixels.
- Disabled actions clear stale callbacks.

### Task 1: Panel movement and fixed sections
**Files:** Assets/Scripts/UI/HexInfoPanelUI.cs; Assets/Editor/HexInfoDrawerTests.cs.
- [x] Replace old height/layout tests with fixed-layout, disabled callback, proportional offset, retarget and hidden/reopen tests.
- [x] Replace height resizing with SmoothDamp Y movement (0.06-second smoothing), from a below-screen starting point; keep section objects active and clear callbacks when disabled.
- [x] Run available source checks; leave Unity tests explicitly unexecuted.

### Task 2: Selection and resources
**Files:** Assets/Scripts/Map/HexSelectionController.cs; Assets/Scripts/UI/ResourceActionRowUI.cs.
- [x] Count types with positive remaining yield and no collector; show only when building or garrison exists.
- [x] Stack resource action buttons vertically within the existing authored container; preserve container transform.
- [ ] Review changes against source and owner scene; commit atomically on current master with expected-SHA protection.
