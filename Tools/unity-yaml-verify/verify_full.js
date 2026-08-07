#!/usr/bin/env node
// Referential integrity check for a hand-edited Unity YAML file (.unity scene or .prefab).
//
// Unity YAML documents are a flat list of objects, each starting with a header line like
// `--- !u!114 &8880000000000000035` (the number after `&` is that object's fileID within this
// file) and referencing each other via `{fileID: N}` (optionally with a `guid:` for a reference
// into a DIFFERENT asset file, which this script intentionally ignores — cross-file references
// aren't checkable without loading every other asset too).
//
// This only checks REFERENTIAL integrity: every same-file fileID mentioned in a `{fileID: N}`
// block actually has a matching `--- !u!TYPE &N` definition somewhere in the file, every
// definition is unique, and no fileID exceeds Int64 range (Unity silently corrupts on overflow
// if a hand-picked ID is too large). It does NOT check that a reference points at an object of
// the expected TYPE — see verify_types.js for that.
//
// Usage: node verify_full.js <path-to-.unity-or-.prefab> [more paths...]
const fs = require('fs');

function verifyFile(path) {
    const text = fs.readFileSync(path, 'utf8');
    const lines = text.split(/\r?\n/);

    const defined = new Map(); // fileID -> count
    const referenced = new Set();
    let overflow = 0;

    const headerRe = /^--- !u!\d+ &(-?\d+)/;
    const fileIdRe = /\{fileID:\s*(-?\d+)(?:,\s*guid:)?[^}]*\}/g;

    for (const line of lines) {
        const header = headerRe.exec(line);
        if (header) {
            const id = header[1];
            defined.set(id, (defined.get(id) || 0) + 1);
            if (BigInt(id) > 9223372036854775807n || BigInt(id) < -9223372036854775808n) overflow++;
            continue;
        }
        let m;
        while ((m = fileIdRe.exec(line)) !== null) {
            const id = m[1];
            if (id === '0') continue; // {fileID: 0} is Unity's own "null reference", not a real target
            // Skip guid-qualified refs (point into a different asset file — not checkable here).
            if (/guid:/.test(m[0])) continue;
            referenced.add(id);
        }
    }

    const missing = [...referenced].filter(id => !defined.has(id));
    const duplicates = [...defined.entries()].filter(([, count]) => count > 1);

    console.log(`${path} : checked ${referenced.size} referenced, ${defined.size} defined, ` +
        `${missing.length} missing, ${duplicates.length} duplicate defs, ${overflow} overflow`);
    if (missing.length) console.log('  MISSING:', missing.slice(0, 20).join(', '), missing.length > 20 ? '...' : '');
    if (duplicates.length) console.log('  DUPLICATES:', duplicates.slice(0, 20).map(([id]) => id).join(', '));
    if (overflow) console.log('  OVERFLOW: one or more fileIDs exceed Int64 range');

    return missing.length === 0 && duplicates.length === 0 && overflow === 0;
}

const paths = process.argv.slice(2);
if (paths.length === 0) {
    console.error('Usage: node verify_full.js <path-to-.unity-or-.prefab> [more paths...]');
    process.exit(2);
}
let ok = true;
for (const path of paths) ok = verifyFile(path) && ok;
process.exit(ok ? 0 : 1);
