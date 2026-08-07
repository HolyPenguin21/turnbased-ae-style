#!/usr/bin/env node
// Builds a compact, queryable index of a hand-authored Unity YAML file (.unity/.prefab) —
// GameObject name -> fileID -> parent/children -> components -> (for MonoBehaviours) script
// class + serialized field values. Exists because grepping the raw YAML repeatedly to answer
// "what's this object's hierarchy/wiring" is slow and burns a lot of context; this gives a
// single compact view instead.
//
// Not a general YAML parser — relies on Unity's own consistent serializer formatting (2-space
// indent per level, `- ` list items at the parent key's own indent) rather than pulling in a
// real YAML library, matching this project's other Tools/ scripts.
//
// Usage:
//   node scene_index.js <path> --tree                 Print the whole GameObject hierarchy
//   node scene_index.js <path> --find <namePattern>    Find objects by name (substring, case-insensitive),
//                                                       print their ancestor chain, children, and
//                                                       full component/field detail
//   node scene_index.js <path> --id <fileID>           Look up one object/component by fileID directly
//   node scene_index.js <path> --json <outPath>        Dump the full index as JSON (for scripting)
const fs = require('fs');

function parse(path) {
    const lines = fs.readFileSync(path, 'utf8').split(/\r?\n/);
    const objects = new Map(); // fileID -> {type, unityType, gameObjectId?, fatherId?, name?, componentIds?, scriptClass?, fields?}

    const headerRe = /^--- !u!(\d+) &(-?\d+)/;
    const fieldRe = /^  (\w+):\s*(.*)$/;
    const listItemRe = /^  - (.*)$/;
    const fileIdRe = /\{fileID:\s*(-?\d+)/;

    let i = 0;
    while (i < lines.length) {
        const header = headerRe.exec(lines[i]);
        if (!header) { i++; continue; }
        const [, typeNum, id] = header;
        const unityType = (lines[i + 1] || '').replace(':', '').trim();
        const obj = { type: typeNum, unityType, fields: {} };
        i += 2; // skip header + the "ClassName:" line

        while (i < lines.length && !headerRe.test(lines[i])) {
            const field = fieldRe.exec(lines[i]);
            if (!field) { i++; continue; }
            const [, key, inlineValue] = field;
            i++;

            if (inlineValue !== '') {
                obj.fields[key] = inlineValue;
                if (key === 'm_GameObject') obj.gameObjectId = (fileIdRe.exec(inlineValue) || [])[1];
                if (key === 'm_Father') obj.fatherId = (fileIdRe.exec(inlineValue) || [])[1];
                if (key === 'm_Name') obj.name = inlineValue;
                continue;
            }

            // Empty value on the key's own line — a list or nested block follows, indented as
            // `  - ...` items. Only m_Component/m_Children are collected structurally; anything
            // else empty-valued is skipped (nested non-reference structures aren't useful here).
            const collected = [];
            while (i < lines.length) {
                const item = listItemRe.exec(lines[i]);
                if (!item) break;
                const fid = fileIdRe.exec(item[1]);
                if (fid) collected.push(fid[1]);
                i++;
            }
            if (key === 'm_Component') obj.componentIds = collected;
            if (key === 'm_Children') obj.childIds = collected;
        }

        if (obj.fields.m_EditorClassIdentifier) {
            const parts = obj.fields.m_EditorClassIdentifier.split('::');
            obj.scriptClass = parts.length > 1 ? parts[1] : obj.fields.m_EditorClassIdentifier;
        }
        objects.set(id, obj);
    }
    return objects;
}

// GameObject id -> its own fileID (via componentIds -> Transform's own m_GameObject back-ref is
// redundant; the GameObject itself already has componentIds listing every component including
// its own Transform). Build GameObject-level parent/child links via each Transform/RectTransform
// component's m_Father, resolved through m_GameObject.
function buildHierarchy(objects) {
    const transformOf = new Map(); // gameObjectId -> transform component id
    for (const [id, obj] of objects)
        if ((obj.type === '4' || obj.type === '224') && obj.gameObjectId)
            transformOf.set(obj.gameObjectId, id);

    const parentGameObjectOf = new Map(); // gameObjectId -> parent gameObjectId (or null = root)
    for (const [goId, transformId] of transformOf) {
        const transform = objects.get(transformId);
        const fatherTransformId = transform.fatherId;
        if (!fatherTransformId || fatherTransformId === '0') { parentGameObjectOf.set(goId, null); continue; }
        const fatherTransform = objects.get(fatherTransformId);
        parentGameObjectOf.set(goId, fatherTransform ? fatherTransform.gameObjectId : null);
    }
    return parentGameObjectOf;
}

function gameObjectLabel(id, objects) {
    const obj = objects.get(id);
    if (!obj) return `<missing:${id}>`;
    return `${obj.name || '(unnamed)'} [&${id}]`;
}

function componentSummary(goId, objects) {
    const go = objects.get(goId);
    if (!go || !go.componentIds) return [];
    return go.componentIds.map(cid => {
        const c = objects.get(cid);
        if (!c) return `&${cid} <missing>`;
        const label = c.scriptClass || c.unityType;
        return `${label} [&${cid}]`;
    });
}

function printTree(objects, parentOf) {
    const childrenOf = new Map();
    for (const [goId, parentId] of parentOf) {
        const key = parentId || '<root>';
        if (!childrenOf.has(key)) childrenOf.set(key, []);
        childrenOf.get(key).push(goId);
    }
    function walk(id, depth) {
        console.log('  '.repeat(depth) + gameObjectLabel(id, objects) + '  {' + componentSummary(id, objects).join(', ') + '}');
        for (const childId of childrenOf.get(id) || [])
            walk(childId, depth + 1);
    }
    for (const rootId of childrenOf.get('<root>') || [])
        walk(rootId, 0);
}

function printDetail(id, objects, parentOf) {
    const obj = objects.get(id);
    if (!obj) { console.log(`&${id}: not found`); return; }
    if (obj.type === '1') {
        console.log(`GameObject ${gameObjectLabel(id, objects)}`);
        const parentId = parentOf.get(id);
        console.log(`  parent: ${parentId ? gameObjectLabel(parentId, objects) : '<root>'}`);
        console.log(`  components: ${componentSummary(id, objects).join(', ') || '(none)'}`);
        const childIds = [...parentOf.entries()].filter(([, p]) => p === id).map(([c]) => c);
        if (childIds.length) console.log(`  children: ${childIds.map(c => gameObjectLabel(c, objects)).join(', ')}`);
    } else {
        console.log(`${obj.scriptClass || obj.unityType} [&${id}]${obj.gameObjectId ? ` on ${gameObjectLabel(obj.gameObjectId, objects)}` : ''}`);
        for (const [k, v] of Object.entries(obj.fields))
            if (!['m_ObjectHideFlags', 'm_CorrespondingSourceObject', 'm_PrefabInstance', 'm_PrefabAsset', 'm_GameObject', 'm_EditorHideFlags', 'm_EditorClassIdentifier'].includes(k))
                console.log(`  ${k}: ${v}`);
    }
}

// --- CLI ---
const args = process.argv.slice(2);
const path = args[0];
if (!path) {
    console.error('Usage: node scene_index.js <path> [--tree | --find <pattern> | --id <fileID> | --json <outPath>]');
    process.exit(2);
}
const objects = parse(path);
const parentOf = buildHierarchy(objects);

const mode = args[1];
if (mode === '--tree' || !mode) {
    printTree(objects, parentOf);
} else if (mode === '--find') {
    const pattern = (args[2] || '').toLowerCase();
    for (const [id, obj] of objects) {
        if (obj.type === '1' && obj.name && obj.name.toLowerCase().includes(pattern)) {
            console.log('='.repeat(60));
            printDetail(id, objects, parentOf);
        }
    }
} else if (mode === '--id') {
    printDetail(args[2], objects, parentOf);
} else if (mode === '--json') {
    const plain = {};
    for (const [id, obj] of objects) plain[id] = obj;
    fs.writeFileSync(args[2], JSON.stringify(plain, null, 2));
    console.log(`Wrote ${objects.size} objects to ${args[2]}`);
} else {
    console.error(`Unknown mode: ${mode}`);
    process.exit(2);
}
