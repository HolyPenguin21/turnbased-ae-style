#!/usr/bin/env python3
"""Validate serialized integration without needing Unity. Does not compile shaders or run UI."""
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[2]
scene = (ROOT / 'Assets/Scenes/MainMenu.unity').read_text()
blocks = {int(m.group(2)): (int(m.group(1)), m.group(3)) for m in re.finditer(
    r'^--- !u!(\d+) &(\d+)\n(.*?)(?=^--- |\Z)', scene, re.M | re.S)}


def named(name):
    matches = [key for key, (kind, body) in blocks.items() if kind == 1 and f'  m_Name: {name}\n' in body]
    assert len(matches) == 1, f'Expected one {name}, got {len(matches)}'
    return matches[0]


def component(go, kind):
    ids = [int(s) for s in re.findall(r'component: \{fileID: (\d+)\}', blocks[go][1])]
    return next(key for key in ids if blocks[key][0] == kind)


def ref(body, key):
    return int(re.search(rf'^  {key}: \{{fileID: (\d+)\}}', body, re.M).group(1))


effect = named('MenuAtmosphere')
background = named('Image_Background')
transform = component(effect, 224)
assert ref(blocks[transform][1], 'm_Father') == component(background, 224), 'Effects must follow the exact background transform'
assert f'  - {{fileID: {transform}}}' in blocks[component(background, 224)][1], 'Parent must list effect child'
assert 'm_AnchorMin: {x: 0, y: 0}' in blocks[transform][1]
assert 'm_AnchorMax: {x: 1, y: 1}' in blocks[transform][1]
graphic = blocks[component(effect, 114)][1]
assert ref(graphic, 'mainMenuPanel') == named('MainMenuPanel'), 'Wrong menu visibility root'
assert ref(graphic, 'collectionWindow') == named('CollectionWindow'), 'Collection must explicitly block wind'
assert 'm_RaycastTarget: 0' in graphic, 'Atmosphere must not capture UI clicks'
canvas = blocks[component(effect, 223)][1]
assert 'm_OverrideSorting: 0' in canvas, 'Nested canvas must retain background sibling order'
flags = int(re.search(r'm_AdditionalShaderChannelsFlag: (\d+)', canvas).group(1))
assert flags & 1, 'Architecture UVs require TexCoord1'
bg_sprite = re.search(r'm_Sprite: (.+)', blocks[component(background, 114)][1]).group(1)
assert '4c85a84f370e1de42a22d8f30f2fac07' in bg_sprite, 'Keep the existing background GUID'
for field in ('dustAtlas', 'architectureMask', 'atmosphereShader'):
    assert re.search(rf'{field}: \{{fileID: [1-9]\d*, guid: [a-f0-9]{{32}}, type: 3\}}', graphic), f'{field} must be explicitly wired'
print('PASS scene hierarchy, collection/menu visibility roots, input isolation, UV channel and explicit asset wiring')
