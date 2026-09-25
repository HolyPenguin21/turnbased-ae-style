#!/bin/bash
# Compile Assets/ outside Unity (UnityEngine reference DLLs from NuGet + EngineStubs.cs for TMPro,
# InputSystem, UnityEditor). A small set of errors in UI/editor code is expected and permanent, so
# the check is DIFFERENTIAL against a baseline:
#   compile_check.sh --baseline [rev]   record the baseline (default: HEAD)
#   compile_check.sh                    compile the working tree; exit 1 on any error not in the baseline
source "$(dirname "$0")/common.sh"
D="$WORK/check"; mkdir -p "$D"
cp "$HERE/EngineStubs.cs" "$D/"
compile() {  # compile <assets-root> <out>
  sed "s#\$(SrcAssets)#$1#g" "$HERE/Check.csproj.template" > "$D/Check.csproj"
  (cd "$D" && timeout 900 dotnet build -nologo -v q 2>&1) | grep -E " error " \
    | sed -E 's/ \[.*csproj\]//; s#^.*/Assets/#Assets/#; s/\([0-9]+,[0-9]+\)//' | sort -u > "$2"
}
if [ "$1" = "--baseline" ]; then
  mirror_assets "$WORK/check-base-src" "${2:-HEAD}"
  compile "$WORK/check-base-src/Assets" "$WORK/compile_baseline.txt"
  echo "baseline errors: $(wc -l < "$WORK/compile_baseline.txt") (rev ${2:-HEAD})"; exit 0
fi
[ -f "$WORK/compile_baseline.txt" ] || { echo "run: $0 --baseline [rev] first"; exit 2; }
compile "$REPO/Assets" "$WORK/compile_current.txt"
NEW=$(comm -13 "$WORK/compile_baseline.txt" "$WORK/compile_current.txt")
echo "errors=$(wc -l < "$WORK/compile_current.txt") new=$(printf "%s" "$NEW" | grep -c .)"
[ -z "$NEW" ] || { echo "$NEW" | cut -c1-300; exit 1; }
