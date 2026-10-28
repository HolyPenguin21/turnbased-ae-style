# Frozen renderer-role regression

Run `DOTNET=/path/to/dotnet python Tools/match-save-verify/visual-role-audit/run.py`.

Compiles actual `MapObjectVisual`, its persistence bridge, frozen-tree capture/validation/
reconstruction and production JSON. Engine-only surfaces supply component trees and rendering
fields; no snapshot, role mapping, copy or codec operation is replaced. The scenario saves and
reconstructs an observed army marker, then performs the next real observation's
`CopyAppearanceFrom`, checking all original primary and ordered auxiliary roles (including a
null auxiliary slot). Invalid roles and inconsistent visibility declarations are rejected.

The initial code failed at the re-observed icon assertion. The native EditMode counterpart
`FrozenRendererRoundtrip_ReobservationRefreshesOriginalRendererRoles` requires Unity;
this runner does not prove native clone callbacks, activation order or actual rendering.
