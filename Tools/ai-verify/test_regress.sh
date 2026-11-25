#!/bin/bash
# Behavioural regression gate: every test that PASSES at <base-rev> must still pass in the
# working tree. Exit 1 on any regression.   test_regress.sh [--all] [base-rev]   (default: HEAD)
# Both sides run in the same scope (default fast, see run_tests.sh); the base result is cached per
# scope (and, for fast, per engine_bound.txt + TestMain.cs content). Without a cached base both
# sides run in parallel.
source "$(dirname "$0")/common.sh"
SCOPE=(); KEY=fast-$(cat "$HERE/engine_bound.txt" "$HERE/TestMain.cs" | tr -d '\r' | md5sum | cut -c1-8)
[ "$1" = "--all" ] && { SCOPE=(--all); KEY=all; shift; }
BASE="${1:-HEAD}"; BASE_SHA=$(git -C "$REPO" rev-parse "$BASE") || exit 2
BX="$WORK/tests_base_${MODE}_${KEY}_$BASE_SHA.xml"
CX="$WORK/tests_current_${MODE}_${KEY}.xml"
if [ -f "$BX" ]; then
  echo "base $BASE ($BASE_SHA): cached"
  echo "working tree:"; "$HERE/run_tests.sh" "$CX" "" "${SCOPE[@]}" || exit 2
else
  "$HERE/run_tests.sh" "$BX.tmp" "$BASE_SHA" "${SCOPE[@]}" > "$WORK/regress_base_$KEY.log" 2>&1 & BP=$!
  "$HERE/run_tests.sh" "$CX" "" "${SCOPE[@]}" > "$WORK/regress_current_$KEY.log" 2>&1; CS=$?
  wait $BP; BS=$?
  echo "base $BASE ($BASE_SHA):"; cat "$WORK/regress_base_$KEY.log"
  echo "working tree:"; cat "$WORK/regress_current_$KEY.log"
  [ $BS -eq 0 ] && [ $CS -eq 0 ] || exit 2
  mv "$BX.tmp" "$BX"
fi
"$PYTHON" - "$BX" "$CX" <<'PY'
import sys, re, xml.etree.ElementTree as ET
def res(f): return {t.get('fullname'): t for t in ET.parse(f).getroot().iter('test-case')}
def ok(t): return t is not None and t.get('result') == 'Passed'
def msg(t):
    m = t.find('.//message') if t is not None else None
    return re.sub(r'\s+', ' ', (m.text or '') if m is not None else 'missing').strip()[:300]
b, c = res(sys.argv[1]), res(sys.argv[2])
lost = sorted(n for n, t in b.items() if ok(t) and not ok(c.get(n)))
gain = sorted(n for n, t in c.items() if ok(t) and not ok(b.get(n)))
moved = sorted(n for n in b.keys() & c.keys()
               if not ok(b[n]) and not ok(c[n]) and msg(b[n]) != msg(c[n]))
print(f"base passed {sum(map(ok, b.values()))}/{len(b)}, current passed {sum(map(ok, c.values()))}/{len(c)}")
print(f"REGRESSIONS: {len(lost)}   newly passing: {len(gain)}   failing on both, message changed: {len(moved)}")
for n in lost:
    print("  -", n, "->", msg(c.get(n)))
for n in moved:
    print("  ~", n, "\n      base:", msg(b[n]), "\n      now: ", msg(c[n]))
sys.exit(1 if lost else 0)
PY
