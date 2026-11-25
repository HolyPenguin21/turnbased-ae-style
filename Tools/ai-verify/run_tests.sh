#!/bin/bash
# Run the EditMode tests (Assets/Editor) outside Unity: net472 exe + NUnitLite under Mono.
# Tests that need the native engine (GameObject, ScriptableObject.CreateInstance, JsonUtility, ...)
# fail here by design; compare runs with test_regress.sh instead of reading absolute pass counts.
#   run_tests.sh <result.xml> [rev] [--all] [nunitlite args...]   (rev "" = working tree)
# Default scope "fast": skips [Category("Extended")] (as the routine Unity run does) and the tests
# listed in engine_bound.txt. --all runs every test (rebuilding the list, final check).
# Each revision builds in its own folder (tests-<mode>-rev-<sha> / tests-<mode>-wt), so a base run
# and a working-tree run can go in parallel.
source "$(dirname "$0")/common.sh"
OUT="$1"; REV="$2"; shift 2 || true
SCOPE=fast; [ "$1" = "--all" ] && { SCOPE=all; shift; }
if [ -n "$REV" ]; then TAG="rev-$(git -C "$REPO" rev-parse --short=12 "$REV")" || exit 2; else TAG=wt; fi
D="$WORK/tests-$MODE-$TAG"; mkdir -p "$D"
mirror_assets "$D/src" "$REV"; patch_for_net472 "$D/src"
cp "$HERE/EngineStubs.cs" "$HERE/TestRunStubs.cs" "$HERE/engine_bound.txt" "$D/"
cp "$HERE/TestMain.cs" "$D/Program.cs"
sed "s#\$(SrcAssets)#$D/src/Assets#g" "$HERE/Tests.csproj.template" > "$D/Tests.csproj"
rm -rf "$D/bin" "$D/obj" "$OUT"
(cd "$D" && timeout 900 dotnet build -nologo -v q "${MSB_PROPS[@]}" 2>&1) | grep -E " error " | head -20
EXE=bin/Debug/net472/Assembly-CSharp.exe
[ -f "$D/$EXE" ] || { echo "build failed ($TAG)"; exit 2; }
FILTER=(); EXCLUDE=
if [ "$SCOPE" = fast ]; then
  FILTER=(--where "cat != Extended"); EXCLUDE=engine_bound.txt
  echo "  scope fast: skipping Extended and $(grep -cv '^#' "$D/engine_bound.txt") engine-bound tests"
fi
T0=$SECONDS
(cd "$D" && AI_VERIFY_EXCLUDE="$EXCLUDE" timeout 1800 "$MONO" "$EXE" --labels=Off --result="$OUT" "${FILTER[@]}" "$@" 2>&1) \
  | grep -A1 "Test Count"
echo "  tests ran $((SECONDS - T0)) s ($TAG, scope $SCOPE)"
[ -f "$OUT" ] || { echo "no result written ($TAG)"; exit 2; }
