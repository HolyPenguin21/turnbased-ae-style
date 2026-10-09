#!/usr/bin/env python3
"""check_boundaries.py run.jsonl

Single-run structural assertions over an AI V2 test-recorder trace (schema: README.md).
Exit 0 when every rule holds, 1 when any rule is violated (all violations are printed), 2 on bad input.

Rules (per player+turn scope unless stated):
  R1 ordinal      records of a scope have strictly increasing `ordinal`.
  R2 commit       a world revision is committed at most once (`event=commit`, field `world_revision`).
  R3 owner        every owner row has a non-empty `owner` and `reason`.
  R4 take-order   `take` -> `consume` -> `reentry`: consume never precedes its take and never
                  follows the reentry that took the same `take_id`; reentry needs a prior take.
  R5 income-cover `income_cover_release` happens at most once per scope, after a `terminal_force`
                  and before the first `tempo_round_start`.
  R6 expiry       at `turn_start`, no visible row has `expiry` earlier than that turn
                  (an expired view must not reach the next turn).
"""
import json
import sys
from collections import defaultdict


def load(path):
    out = []
    with open(path, encoding="utf-8") as f:
        for n, line in enumerate(f, 1):
            line = line.strip()
            if line:
                try:
                    out.append(json.loads(line))
                except json.JSONDecodeError as e:
                    print(f"{path}:{n}: not JSON ({e})", file=sys.stderr)
                    sys.exit(2)
    return out


def check(records):
    bad = []

    def fail(rule, rec, text):
        bad.append(f"{rule} player={rec.get('player')} turn={rec.get('turn')} "
                   f"ordinal={rec.get('ordinal')} event={rec.get('event')}: {text}")

    scopes = defaultdict(list)
    for r in records:
        scopes[(r.get("player"), r.get("turn"))].append(r)

    for (player, turn), recs in scopes.items():
        last = None
        commits = set()
        takes = {}          # take_id -> state: "taken" | "consumed" | "reentered"
        terminal_seen = False
        tempo_seen = False
        cover_released = False
        for r in recs:
            ev = r.get("event")
            o = r.get("ordinal")
            if last is not None and (o is None or o <= last):
                fail("R1", r, f"ordinal {o} not after {last}")
            last = o if o is not None else last

            for row in r.get("rows") or []:
                if not row.get("owner") or not row.get("reason"):
                    fail("R3", r, f"row without owner/reason: {json.dumps(row, ensure_ascii=False)}")

            if ev == "commit":
                rev = r.get("world_revision")
                if rev in commits:
                    fail("R2", r, f"world revision {rev} committed twice")
                commits.add(rev)
            elif ev == "take":
                takes[r.get("take_id")] = "taken"
            elif ev == "consume":
                tid = r.get("take_id")
                if takes.get(tid) != "taken":
                    fail("R4", r, f"consume of take {tid} in state {takes.get(tid)}")
                else:
                    takes[tid] = "consumed"
            elif ev == "reentry":
                tid = r.get("take_id")
                if takes.get(tid) != "consumed":
                    fail("R4", r, f"reentry of take {tid} in state {takes.get(tid)} (needs take then consume)")
                else:
                    takes[tid] = "reentered"
            elif ev == "terminal_force":
                terminal_seen = True
            elif ev == "tempo_round_start":
                tempo_seen = True
            elif ev == "income_cover_release":
                if cover_released:
                    fail("R5", r, "income cover released twice in one turn")
                if not terminal_seen:
                    fail("R5", r, "income cover released before any terminal_force")
                if tempo_seen:
                    fail("R5", r, "income cover released after the first tempo round started")
                cover_released = True
            elif ev == "turn_start":
                for row in r.get("rows") or []:
                    exp = row.get("expiry")
                    # `expiry` is a turn number, or a stage name (rows are turn-scoped; no number to check).
                    if isinstance(exp, (int, float)) and turn is not None and exp < turn:
                        fail("R6", r, f"expired row visible at turn start: "
                             f"{json.dumps(row, ensure_ascii=False)}")
    return bad


def main(argv):
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    records = load(argv[0])
    bad = check(records)
    if bad:
        print(f"{len(bad)} violation(s):")
        for b in bad:
            print("  " + b)
        return 1
    print(f"ok: {len(records)} records")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
