#!/bin/bash
# Shared helpers for the ai-verify scripts. Source, don't run.
set -o pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(git -C "$HERE" rev-parse --show-toplevel)"
WORK="${AI_VERIFY_WORK:-${TMPDIR:-/tmp}/ai-verify}"
mkdir -p "$WORK"
PYTHON="$(command -v python3 || command -v python)"

# Two reference modes, chosen by AI_VERIFY_UNITY:
#   unset  — "stub": NuGet UnityEngine.Modules 2021.3 (publicized reference DLLs: every method body
#            throws) + EngineStubs.cs + a managed Mathf. For containers without Unity.
#   set    — "unity": the installed editor's real managed assemblies. AI_VERIFY_UNITY is the
#            Editor/Data folder of the project's version (ProjectSettings/ProjectVersion.txt), e.g.
#            D:/Unity/Editor/6000.5.4f1/Editor/Data. Packages (TextMeshPro, InputSystem, uGUI) come
#            from the project's Library/ScriptAssemblies, so Unity must have imported the project.
#            No stubs: the compile is the real API, and the tests run on Unity's bundled Mono.
if [ -n "$AI_VERIFY_UNITY" ]; then
  MODE=unity
  [ -d "$AI_VERIFY_UNITY/Managed/UnityEngine" ] || { echo "AI_VERIFY_UNITY: no Managed/UnityEngine in $AI_VERIFY_UNITY"; exit 2; }
  [ -f "$REPO/Library/ScriptAssemblies/UnityEngine.UI.dll" ] || { echo "Library/ScriptAssemblies is missing: open the project in Unity once"; exit 2; }
  MONO="$AI_VERIFY_UNITY/MonoBleedingEdge/bin/mono"; [ -f "$MONO.exe" ] && MONO="$MONO.exe"
  UNITY_NUNIT="$(ls "$REPO"/Library/PackageCache/com.unity.ext.nunit*/net472/unity-custom/nunit.framework.dll 2>/dev/null | head -1)"
  [ -n "$UNITY_NUNIT" ] || { echo "com.unity.ext.nunit is missing from Library/PackageCache"; exit 2; }
  MSB_PROPS=(-p:UnityData="$AI_VERIFY_UNITY" -p:UnityLibrary="$REPO/Library/ScriptAssemblies" -p:UnityNUnit="$UNITY_NUNIT")
else
  MODE=stub
  MONO=mono
  MSB_PROPS=()
fi

# mirror_assets <dest> [rev] — a copy of Assets/ from the working tree or from a git revision,
# with the three .NET Framework 4.7.2 API gaps patched IN THE COPY (never in the repo).
mirror_assets() {
  local dest="$1" rev="$2"
  rm -rf "$dest" && mkdir -p "$dest"
  if [ -n "$rev" ]; then git -C "$REPO" archive "$rev" Assets | tar -x -C "$dest"
  else cp -r "$REPO/Assets" "$dest/"; fi
}
patch_for_net472() {
  local src="$1"
  find "$src" -name "*.cs" -exec sed -i 's/\(System\.\)\?BitConverter\.SingleToInt32Bits(/global::Net472Compat.SingleToInt32Bits(/g' {} +
  local f="$src/Assets/Scripts/Ai/V2/State/ReconIntelSnapshotRegistry.cs"
  [ -f "$f" ] && sed -i 's/new Dictionary<HexCoord, int>(lastObserved)/System.Linq.Enumerable.ToDictionary(lastObserved, kv => kv.Key, kv => kv.Value)/' "$f"
  return 0
}
