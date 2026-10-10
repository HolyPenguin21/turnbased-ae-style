# Responsive UI verification

Run from the repository root (Python 3 with PyYAML):

```sh
python Tools/responsive-ui-verify/verify.py
node Tools/unity-yaml-verify/verify_full.js Assets/Scenes/Game.unity Assets/Scenes/MainMenu.unity
node Tools/unity-yaml-verify/verify_types.js Assets/Scenes/Game.unity Assets/Scenes/MainMenu.unity
```

The Python check parses every scene and prefab, checks duplicate YAML keys, local reference
integrity, component ownership and bidirectional hierarchy. It evaluates the serialized anchors
at 1280x720, 1600x900, 1920x1080 and 2560x1440 with the authored 1280x720 CanvasScaler.
It checks root bounds, the 5x5 battle grid, production/base grids, six hand slots, all five
resource-drawer states, six player setup rows, scroll wiring and maskable army cards.
It also checks player resolution defaults, the project's Unity version and the audio
settings prefab's root bounds and scene override targets.

This is a calculation over source assets, not an Editor screenshot or a Unity layout pass.
It does not model font metrics, physics raycasts, arbitrary prefab overrides, runtime animations or
arbitrary rotated/scaled RectTransforms. Run the two existing YAML validators on any other
manually changed prefab too.

In Unity 6000.5.4f1:

1. Run the full EditMode suite, including `ResponsiveUiInteractionTests` (nested drag scales,
   four tactic cards in constrained areas and clipped army-grid drop targets).
2. Run `Tools > UI > Audit Responsive Layout` in both scenes.
3. At each listed Game View size, open main menu, six-player setup, audio options, all HUD
   modals, encounter selection, event reward, challenge/result and the battle states.
4. Use a human battle to test arrangement dragging, equipment/mutator previews, move slides,
   initiative scroll and its current-unit follow, Pass/Ready and round/outcome popups.
5. Inspect a 12-slot army/garrison, scroll it and reorder visible cards; reject drops outside
   its viewport. Test transfers into another army, deployment from the hand, hover actions,
   card paging and animation after closing/reopening modals.
6. Exercise 0..4 resource actions, many armies on one hex, long player/unit names, many dice,
   and window resizing during an animation/drag. Confirm no masked entries remain clickable.

The canvas reference remains 1280x720 intentionally: at Full HD its scale is 1.5, preserving
the existing authored card sizes and font hierarchy. Player defaults are 1920x1080.
