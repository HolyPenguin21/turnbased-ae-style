# Armageddon Empires remake — project notes for Claude

Unity 4X hex-strategy game (remake of *Armageddon Empires*). URP. Single `Assembly-CSharp`
assembly — no `.asmdef` split, so any `Game.*` namespace can reference any other with no
assembly-reference restrictions (namespace boundaries are organizational only).

## Compiling without opening Unity

`dotnet build Assembly-CSharp.csproj` from the project root compiles the real game code against
actual Unity assemblies — use this after every C# edit made without a live Editor session.

- The `.csproj`/`.sln` are Unity-generated and gitignored (regenerate any time via **Assets → Open
  C# Project** in the Editor, or **Edit → Preferences → External Tools → Regenerate project
  files**). If neither exists yet, ask the user to trigger it once — External Script Editor in
  Preferences must point at an editor that's actually installed, or generation silently fails with
  a `CodeEditorProjectSync` console error.
- **The `.csproj`'s file list is static, not a wildcard** — it's `<Compile Include="...">` entries
  frozen at generation time. A brand-new `.cs` file (e.g. splitting a class into
  `Foo.Bar.cs`) won't compile until either the user regenerates project files in Unity, or you add
  the matching `<Compile Include>` line yourself (safe to hand-edit for a same-session check — the
  file's gitignored, Unity overwrites it correctly next regeneration). Confirmed by hitting exactly
  this the first time this pipeline was used — silent `CS0103`/`CS1061` errors that looked like a
  real code bug until the missing `<Compile>` entry was spotted.
- This only builds script code — it does NOT validate scenes/prefabs, does NOT run Play Mode, and
  can't catch a wrong-but-existing `fileID` reference in hand-edited YAML. Treat a clean build as
  "the C# compiles," not "the feature works."

## Hand-editing scenes/prefabs (`.unity`, `.prefab`, `.asset`)

This project's YAML gets edited directly by hand fairly often (not only through the Editor). After
any such edit, run both:

```sh
node Tools/unity-yaml-verify/verify_full.js Assets/Scenes/Game.unity
node Tools/unity-yaml-verify/verify_types.js Assets/Scenes/Game.unity
```

See `Tools/unity-yaml-verify/README.md` for what each one actually checks. Both are read-only.

## No automated tests, by the project owner's own choice

Don't add a test framework/test files unless explicitly asked — this was raised and declined.
Verification is: `dotnet build` for compile correctness, the two verify scripts above for scene
integrity, and the user's own manual Play Mode testing for actual behavior. Be extra careful with
anything a compiler can't catch (event subscription lifetimes, Unity lifecycle ordering, timing).

## Established conventions (follow these, don't reinvent per-file)

- **Popup/modal show-hide**: `[SerializeField] private GameObject panelRoot;`,
  `IsShowing => panelRoot != null && panelRoot.activeSelf`, a `Show()`/`Hide()` pair. Popups that
  feed `GameTurnController.InputBlocked`/`CardDraggingBlocked` raise a
  `public event Action VisibilityChanged;` from the one place their own `panelRoot.SetActive`
  actually changes (see `BattleContactPopupUI.SetPanelActive` for the wrapper pattern when a popup
  has more than one call site touching `panelRoot`).
- **Change notification over polling**: this is a turn-based game — most state changes on discrete
  actions (a button click, a turn transition), not continuously. Default to a C# event fired from
  the one place a value actually changes (see `PlayerRoot.ResourcesChanged`,
  `GameTurnController.TurnStateChanged`/`InputBlockedChanged`/`CardDraggingBlockedChanged`) over an
  `Update()` poll. `Update()` should only do things that generically can't be events — keyboard
  polling (Unity's Input System has no "key pressed" event), and per-frame work gated on an actual
  changed input (e.g. `HexSelectionController.RaycastHexCached` — re-raycasts only when the mouse
  or camera actually moved, not every frame).
- **Shared human-player lookup**: `GameSession.FindHumanPlayer()` / `GameSession.FindHumanRoot()`
  — don't reimplement `GameSession.Players?.Find(p => p.IsHuman)` locally.
- **Hex neighbors**: `HexGridMath.Neighbors(HexCoord)` — don't hand-loop
  `HexGridMath.NeighborDirectionsByEdge` for a plain "all 6 neighbors" case (a few sites need the
  raw direction array itself — e.g. edge-paired boundary tracing, a single random direction pick —
  those are fine as-is).
- **Space-bar-as-confirm**: `UIFocusUtility.WasSpacePressed()`.
- **Comment style**: comments explain *why*, often referencing a specific past decision or user
  request, not *what* the code does. This is intentional and consistent throughout the project —
  match it, don't strip it down to terser default style.

## Tactical Battle Module — key facts

- Fixed 5-row grid: `DefenderBackRow=0, DefenderFrontRow=1, NeutralRow=2, AttackerFrontRow=3,
  AttackerBackRow=4` (`BattleGrid.cs`). Chebyshev distance for `Range`. Opposing front rows are 2
  apart — a melee (`Range==1`) unit must step into the neutral row before it can hit anything.
- `BattleAi` is pure static (mirrors `BattleTurnOrder`/`BattleInitiator`) — it only *decides*;
  `BattleScreenUI` executes via the same `PerformMove`/`BeginAttack`/`OnPassClicked` the human path
  uses. Never give the AI its own execution path.
- `BattleAiPhraseBank`: separate phrase pools for a hero-present side (personal, "I/we") vs.
  hero-less (impersonal status reports) — see `GetRandomPhrase`'s `hasHero` parameter. Named
  variants (a specific unit's name via `{0}`) are pooled in *alongside* the plain ones, weighted by
  count, only when a caller has a name to give.

## Working style the project owner has asked for repeatedly

- **Ask before big/ambiguous design decisions, especially AI behavior** — don't guess and build;
  clarify first. Once a decision is confirmed, proactive/decisive execution on the *details* of
  that decision is welcome (don't ask about every sub-choice).
- **git commit granularly** — one logical change per commit, clear message explaining *why*, not
  just *what*. `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>` trailer.
- Large/risky refactors (class splits, behavior changes) should be verified (`dotnet build` at
  minimum, ideally the user's own Play Mode pass) before being treated as done — don't present
  unverified work as finished.
