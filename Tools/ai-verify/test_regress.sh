#!/bin/bash
# Behavioural regression gate: every test that PASSES at <base-rev> must still pass in the
# working tree. Exit 1 on any regression.   test_regress.sh [base-rev]   (default: HEAD)
source "$(dirname "$0")/common.sh"
BASE="${1:-HEAD}"; BASE_SHA=$(git -C "$REPO" rev-parse "$BASE")
BX="$WORK/tests_base_$BASE_SHA.xml"
[ -f "$BX" ] || { echo "base $BASE ($BASE_SHA):"; "$HERE/run_tests.sh" "$BX" "$BASE_SHA"; }
echo "working tree:"; "$HERE/run_tests.sh" "$WORK/tests_current.xml" ""
python3 - "$BX" "$WORK/tests_current.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
def res(f): return {t.get('fullname'): t for t in ET.parse(f).getroot().iter('test-case')}
b, c = res(sys.argv[1]), res(sys.argv[2])
lost = sorted(n for n, t in b.items() if t.get('result') == 'Passed'
              and (n not in c or c[n].get('result') != 'Passed'))
gain = sorted(n for n, t in c.items() if t.get('result') == 'Passed'
              and (n not in b or b[n].get('result') != 'Passed'))
print(f"REGRESSIONS: {len(lost)}   newly passing: {len(gain)}")
for n in lost:
    m = c[n].find('.//message') if n in c else None
    print("  -", n, "->", ((m.text or '') if m is not None else 'missing')[:300])
sys.exit(1 if lost else 0)
PY
