#!/usr/bin/env python3
"""selftest.py — proves the two checkers catch what they exist to catch.

Builds one good trace, then mutates it five ways (the negative fixtures required by the task):
swap consume/reentry, erase an owner, add a second commit, move the income-cover release past the
first tempo round, show an expired row to the next turn. Every mutation must make the checkers exit 1;
the good trace must exit 0. Exit 0 when all of that holds.
"""
import copy
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))


def ev(ordinal, event, **kw):
    r = {"player": 1, "turn": 3, "work": kw.pop("work", "Mission"), "ordinal": ordinal, "event": event,
         "ap": 4, "h": 0, "e": 0, "m": 0, "t": 0, "rows": [], "operation_keys": [], "actors": [],
         "world_revision": 10, "knowledge_version": 5, "pathing_version": 2, "scope_id": 7,
         "consumed": [], "pending": []}
    r.update(kw)
    return r


ROW = {"owner": "Economy#1", "reason": "BuildExtraction", "resource": "AP", "amount": 2, "expiry": 3}


def good():
    return [
        ev(1, "turn_start", rows=[ROW]),
        ev(2, "take", take_id="a", pending=["Actor"]),
        ev(3, "consume", take_id="a", consumed=["Actor"]),
        ev(4, "reentry", take_id="a"),
        ev(5, "commit", world_revision=11, rows=[ROW]),
        ev(6, "terminal_force", work="Pass"),
        ev(7, "income_cover_release", work="Tempo"),
        ev(8, "tempo_round_start", work="Tempo"),
        ev(9, "commit", world_revision=12, rows=[ROW]),
    ]


def swap(t, i, j):
    t = copy.deepcopy(t)
    t[i], t[j] = t[j], t[i]
    for n, r in enumerate(t, 1):
        r["ordinal"] = n
    return t


RULE = {"consume_reentry_swapped": "R4", "owner_erased": "R3", "second_commit": "R2",
        "income_cover_after_tempo": "R5", "expired_view_next_turn": "R6"}


def mutations():
    base = good()
    m = {}
    m["consume_reentry_swapped"] = swap(base, 2, 3)
    t = copy.deepcopy(base); t[4]["rows"] = [dict(ROW, owner="")]; m["owner_erased"] = t
    t = copy.deepcopy(base); t.append(ev(10, "commit", world_revision=12)); m["second_commit"] = t
    m["income_cover_after_tempo"] = swap(base, 6, 7)
    t = copy.deepcopy(base); t[0]["turn"] = 5
    for r in t: r["turn"] = 5
    m["expired_view_next_turn"] = t
    return m


def write(path, records):
    with open(path, "w", encoding="utf-8") as f:
        for r in records:
            f.write(json.dumps(r) + "\n")


def run(script, *args, want_rule=None):
    r = subprocess.run([sys.executable, os.path.join(HERE, script), *args],
                       capture_output=True, text=True)
    if want_rule is not None and (r.returncode != 1 or want_rule not in r.stdout):
        return -1   # rejected for the wrong reason (or not rejected)
    return r.returncode


def main():
    ok = True
    with tempfile.TemporaryDirectory() as d:
        g = os.path.join(d, "good.jsonl")
        write(g, good())
        for name, code in (("check_boundaries(good)", run("check_boundaries.py", g)),
                           ("compare_traces(good,good)", run("compare_traces.py", g, g))):
            print(f"{name}: exit {code} (expected 0)")
            ok &= code == 0
        for name, trace in mutations().items():
            p = os.path.join(d, name + ".jsonl")
            write(p, trace)
            # Order/ownership/commit/expiry mutations are structural: check_boundaries must reject them.
            c = run("check_boundaries.py", p, want_rule=RULE[name])
            # The trace comparer must reject every mutation against the good trace as well.
            k = run("compare_traces.py", g, p)
            print(f"{name}: check_boundaries exit {c}, compare_traces exit {k} (expected 1 by the named rule, 1)")
            ok &= c == 1 and k == 1
    print("SELFTEST " + ("PASSED" if ok else "FAILED"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
