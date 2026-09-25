#!/bin/bash
# Run the EditMode tests (Assets/Editor) outside Unity: net472 exe + NUnitLite under Mono.
# Tests that need the native engine (UnityEngine.Object, scenes, ...) fail here by design; compare
# runs with test_regress.sh instead of reading absolute pass counts.
#   run_tests.sh <result.xml> [rev] [nunitlite args...]   (rev "" = working tree)
source "$(dirname "$0")/common.sh"
OUT="$1"; REV="$2"; shift 2 || true
D="$WORK/tests"; mkdir -p "$D"
mirror_assets "$D/src" "$REV"; patch_for_net472 "$D/src"
cp "$HERE/EngineStubs.cs" "$HERE/TestRunStubs.cs" "$D/"
echo 'public static class TestMain { public static int Main(string[] a) => new NUnitLite.AutoRun(typeof(TestMain).Assembly).Execute(a); }' > "$D/Program.cs"
sed "s#\$(SrcAssets)#$D/src/Assets#g" "$HERE/Tests.csproj.template" > "$D/Tests.csproj"
(cd "$D" && timeout 900 dotnet build -nologo -v q 2>&1) | grep -E " error " | head -20
(cd "$D" && timeout 1500 mono bin/Debug/net472/Assembly-CSharp.exe --labels=Off --result="$OUT" "$@" 2>&1) \
  | grep -A1 "Test Count"
