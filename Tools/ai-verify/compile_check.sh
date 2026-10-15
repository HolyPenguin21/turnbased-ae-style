#!/bin/bash
# Compile Assets/ outside Unity. Stub mode (UnityEngine reference DLLs from NuGet + EngineStubs.cs for
# TMPro, InputSystem, UnityEditor) has a permanent set of errors in UI/editor code; unity mode
# (AI_VERIFY_UNITY, the real assemblies) is expected to have none. Either way the check is
# DIFFERENTIAL against a baseline of the same mode:
#   compile_check.sh --baseline [rev]   record the baseline (default: HEAD)
#   compile_check.sh                    compile the working tree; exit 1 on any error not in the baseline
source "$(dirname "$0")/common.sh"
D="$WORK/check-$MODE"; mkdir -p "$D"
cp "$HERE/EngineStubs.cs" "$D/"
compile() {  # compile <assets-root> <out>
  sed "s#\$(SrcAssets)#$1#g" "$HERE/Check.csproj.template" > "$D/Check.csproj"
  (cd "$D" && rm -rf bin obj && timeout 900 dotnet build -nologo -v q "${MSB_PROPS[@]}" 2>&1) | grep -E " error " \
    | sed -E 's/ \[.*csproj\]//; s#^.*/Assets/#Assets/#; s/\([0-9]+,[0-9]+\)//' | sort -u > "$2"
}
if [ "$1" = "--baseline" ]; then
  mirror_assets "$WORK/check-base-src" "${2:-HEAD}"
  compile "$WORK/check-base-src/Assets" "$WORK/compile_baseline_$MODE.txt"
  echo "baseline errors: $(wc -l < "$WORK/compile_baseline_$MODE.txt") (rev ${2:-HEAD})"; exit 0
fi
[ -f "$WORK/compile_baseline_$MODE.txt" ] || { echo "run: $0 --baseline [rev] first"; exit 2; }
compile "$REPO/Assets" "$WORK/compile_current_$MODE.txt"
NEW=$(comm -13 "$WORK/compile_baseline_$MODE.txt" "$WORK/compile_current_$MODE.txt")
echo "errors=$(wc -l < "$WORK/compile_current_$MODE.txt") new=$(printf "%s" "$NEW" | grep -c .)"
[ -z "$NEW" ] || { echo "$NEW" | cut -c1-300; exit 1; }
