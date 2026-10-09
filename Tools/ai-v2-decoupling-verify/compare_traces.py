#!/usr/bin/env python3
"""compare_traces.py before.jsonl after.jsonl [--ignore field,field]

Compares two AI V2 test-recorder traces record by record. A trace is JSONL, one object per
event, with the schema documented in README.md. Exit 0 when the traces are identical (after
removing --ignore fields), exit 1 on the first divergence or length mismatch, exit 2 on bad input.

Order is part of the result: the same final bank with a different spend order is a divergence.
"""
import json
import sys


def load(path):
    records = []
    with open(path, encoding="utf-8") as f:
        for n, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError as e:
                print(f"{path}:{n}: not JSON ({e})", file=sys.stderr)
                sys.exit(2)
    return records


def normalise(rec, ignore):
    out = {k: v for k, v in rec.items() if k not in ignore}
    rows = out.get("rows")
    if isinstance(rows, list):
        # Row order inside one record is not a decision; the order of records is.
        out["rows"] = sorted(rows, key=lambda r: json.dumps(r, sort_keys=True))
    return out


def describe(rec):
    return f"player={rec.get('player')} turn={rec.get('turn')} work={rec.get('work')} " \
           f"ordinal={rec.get('ordinal')} event={rec.get('event')}"


def main(argv):
    ignore = set()
    args = []
    i = 0
    while i < len(argv):
        if argv[i] == "--ignore" and i + 1 < len(argv):
            ignore = set(filter(None, argv[i + 1].split(",")))
            i += 2
        else:
            args.append(argv[i])
            i += 1
    if len(args) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    before, after = load(args[0]), load(args[1])
    for idx, (b, a) in enumerate(zip(before, after)):
        nb, na = normalise(b, ignore), normalise(a, ignore)
        if nb != na:
            keys = sorted(k for k in set(nb) | set(na) if nb.get(k) != na.get(k))
            print(f"DIVERGENCE at record {idx}: {describe(b)}")
            for k in keys:
                print(f"  {k}: before={json.dumps(nb.get(k), ensure_ascii=False)} "
                      f"after={json.dumps(na.get(k), ensure_ascii=False)}")
            return 1
    if len(before) != len(after):
        print(f"LENGTH MISMATCH: before={len(before)} after={len(after)}")
        return 1
    print(f"identical: {len(before)} records")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
