#!/bin/bash
# Shared helpers for the ai-verify scripts. Source, don't run.
set -o pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(git -C "$HERE" rev-parse --show-toplevel)"
WORK="${AI_VERIFY_WORK:-${TMPDIR:-/tmp}/ai-verify}"
mkdir -p "$WORK"

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
