#!/usr/bin/env python3
"""coupling_matrix.py [--rev <git rev> | --root <dir>] [--json out.json] [--folder Orchestration]

Static folder-level type-dependency matrix of Assets/Scripts/Ai/V2. A reference from a file in
folder A to a type declared only in another folder B is one dependency A->B (strings and comments
are stripped first). This is a type-reference count, NOT a call graph: it does not see call order or
shared state, so it is a supporting metric only.

Prints: edge count, per-folder dependency counts, and for --folder (default Orchestration) the exact
foreign types referenced, grouped by owning folder, both for the whole folder and for
AiStrategyV2Pipeline.cs alone. Same method for every revision, so numbers are comparable.
"""
import collections
import json
import os
import re
import subprocess
import sys
import tempfile

DECL = re.compile(r'\b(?:class|struct|enum|interface|record)\s+([A-Z]\w*)')
IDENT = re.compile(r'\b[A-Z]\w*\b')
V2 = 'Assets/Scripts/Ai/V2'


INTERP = re.compile(r'\$@?"(?:[^"\\\n]|\\.)*"')
HOLE = re.compile(r'\{[^{}]*\}')


def strip(src):
    src = re.sub(r'//[^\n]*', '', src)
    src = re.sub(r'/\*.*?\*/', '', src, flags=re.S)
    # Interpolated strings: the text is dropped, the {expressions} are code and are kept.
    src = INTERP.sub(lambda m: ' ' + ' '.join(HOLE.findall(m.group(0))) + ' ', src)
    src = re.sub(r'@?"(?:[^"\\\n]|\\.)*"', '""', src)
    return src


def scan(root):
    base = os.path.join(root, V2)
    files = {}
    for dp, _, fn in os.walk(base):
        for f in fn:
            if f.endswith('.cs'):
                p = os.path.join(dp, f)
                rel = os.path.relpath(p, base).replace('\\', '/')
                files[rel] = strip(open(p, encoding='utf-8', errors='replace').read())
    owner = collections.defaultdict(set)
    for rel, src in files.items():
        folder = rel.split('/')[0] if '/' in rel else '(root)'
        for t in DECL.findall(src):
            owner[t].add(folder)
    edges = collections.defaultdict(collections.Counter)
    per_file = collections.defaultdict(lambda: collections.defaultdict(set))  # file -> folder -> types
    for rel, src in files.items():
        a = rel.split('/')[0] if '/' in rel else '(root)'
        for t in set(IDENT.findall(src)):
            for b in owner.get(t, ()):
                if b != a and a not in owner[t]:
                    edges[(a, b)][t] += 1
                    per_file[rel][b].add(t)
    return files, edges, per_file


def main(argv):
    rev = root = jout = None
    focus = 'Orchestration'
    i = 0
    while i < len(argv):
        if argv[i] == '--rev': rev = argv[i + 1]; i += 2
        elif argv[i] == '--root': root = argv[i + 1]; i += 2
        elif argv[i] == '--json': jout = argv[i + 1]; i += 2
        elif argv[i] == '--folder': focus = argv[i + 1]; i += 2
        else:
            print(__doc__, file=sys.stderr); return 2
    tmp = None
    if rev:
        tmp = tempfile.TemporaryDirectory()
        archive = subprocess.run(['git', 'archive', rev, V2], capture_output=True, check=True).stdout
        subprocess.run(['tar', '-x', '-C', tmp.name], input=archive, check=True)
        root = tmp.name
    root = root or '.'
    files, edges, per_file = scan(root)

    out_deg = collections.Counter()
    for (a, _b) in edges:
        out_deg[a] += 1
    print(f"files={len(files)} folder_edges={len(edges)}")
    for f, n in sorted(out_deg.items()):
        print(f"  {f}: depends on {n} folders")

    def foreign(prefix_ok):
        res = collections.defaultdict(set)
        for rel, by in per_file.items():
            if prefix_ok(rel):
                for b, ts in by.items():
                    res[b] |= ts
        return res

    whole = foreign(lambda r: r.startswith(focus + '/'))
    pipe = foreign(lambda r: r.endswith('AiStrategyV2Pipeline.cs'))
    for title, d in ((f"{focus} (whole folder)", whole), ("AiStrategyV2Pipeline.cs", pipe)):
        total = sum(len(v) for v in d.values())
        print(f"\n{title}: {len(d)} folders, {total} foreign types")
        for b in sorted(d):
            print(f"  {b} ({len(d[b])}): {', '.join(sorted(d[b]))}")
    if jout:
        json.dump({
            'edges': {f"{a}->{b}": sorted(c) for (a, b), c in edges.items()},
            'focus_whole': {k: sorted(v) for k, v in whole.items()},
            'pipeline': {k: sorted(v) for k, v in pipe.items()},
        }, open(jout, 'w', encoding='utf-8'), indent=1)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
