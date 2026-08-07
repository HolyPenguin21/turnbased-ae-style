#!/usr/bin/env node
// Type-consistency check for a hand-edited Unity YAML file (.unity scene or .prefab) —
// complements verify_full.js, which only checks that a referenced fileID EXISTS, not that it's
// the right KIND of object. This catches the class of bug where a reference was hand-typed
// against the wrong ID by mistake (e.g. m_Father pointing at a GameObject's own fileID instead
// of its RectTransform's, which still "exists" so verify_full.js alone passes it clean, but
// Unity would refuse to parent a Transform under a GameObject at load time).
//
// Checks two well-known reference fields wherever they appear, regardless of which component
// type contains them:
//   - m_Father: must point at a Transform (type !u!4) or RectTransform (type !u!224).
//   - m_GameObject: must point at a GameObject (type !u!1).
//
// Usage: node verify_types.js <path-to-.unity-or-.prefab> [more paths...]
const fs = require('fs');

// Unity's built-in class IDs for the two types this checks against.
const GAMEOBJECT_TYPE = '1';
const TRANSFORM_TYPES = new Set(['4', '224']); // Transform, RectTransform

function verifyFile(path) {
    const text = fs.readFileSync(path, 'utf8');
    const lines = text.split(/\r?\n/);

    // fileID -> type id, from every `--- !u!TYPE &ID` header in the file.
    const typeOf = new Map();
    const headerRe = /^--- !u!(\d+) &(-?\d+)/;
    for (const line of lines) {
        const m = headerRe.exec(line);
        if (m) typeOf.set(m[2], m[1]);
    }

    const mismatches = [];
    const fieldRe = /^(\s*)(m_Father|m_GameObject):\s*\{fileID:\s*(-?\d+)/;
    let currentObjectId = null;
    for (const line of lines) {
        const header = headerRe.exec(line);
        if (header) { currentObjectId = header[2]; continue; }

        const m = fieldRe.exec(line);
        if (!m) continue;
        const [, , field, targetId] = m;
        if (targetId === '0') continue; // null reference — always valid regardless of field

        const targetType = typeOf.get(targetId);
        if (targetType === undefined) continue; // verify_full.js's job, not this script's

        const expectedOk = field === 'm_GameObject'
            ? targetType === GAMEOBJECT_TYPE
            : TRANSFORM_TYPES.has(targetType);

        if (!expectedOk) {
            mismatches.push(`object &${currentObjectId}: ${field} -> &${targetId} is type !u!${targetType}, expected ${field === 'm_GameObject' ? '!u!1 (GameObject)' : '!u!4 or !u!224 (Transform)'}`);
        }
    }

    if (mismatches.length === 0) {
        console.log(`${path} : OK, no type mismatches found`);
    } else {
        console.log(`${path} : ${mismatches.length} type mismatch(es) found`);
        for (const line of mismatches) console.log('  ' + line);
    }
    return mismatches.length === 0;
}

const paths = process.argv.slice(2);
if (paths.length === 0) {
    console.error('Usage: node verify_types.js <path-to-.unity-or-.prefab> [more paths...]');
    process.exit(2);
}
let ok = true;
for (const path of paths) ok = verifyFile(path) && ok;
process.exit(ok ? 0 : 1);
