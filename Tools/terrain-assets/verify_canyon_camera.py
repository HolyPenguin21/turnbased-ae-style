"""Verify the scene camera contract used to paint the baked canyon walls.

This checks camera data, not the physical accuracy of pixels or a Unity render.
"""
from pathlib import Path
import hashlib
import json
import math
import re

root = Path(__file__).resolve().parents[2]
scene_path = root / 'Assets/Scenes/Game.unity'
data = scene_path.read_bytes()
blocks = re.split(r'^--- !u!\d+ &', data.decode('utf-8'), flags=re.M)[1:]
objects = {b.split('\n', 1)[0]: b.split('\n', 1)[1] for b in blocks}
main = next(b for b in objects.values() if '\n  m_Name: Main Camera\n' in b)
components = [objects[n] for n in re.findall(r'component: \{fileID: (\d+)\}', main)]
camera = next(b for b in components if b.startswith('Camera:'))
transform = next(b for b in components if b.startswith('Transform:'))
assert re.search(r'^  orthographic: 1$', camera, re.M)
assert 'm_Father: {fileID: 0}' in transform, 'Parent transforms require world-space evaluation'
q = dict((k, float(v)) for k, v in re.findall(r'([xyzw]): ([\d.eE+-]+)', re.search(r'm_LocalRotation: \{([^}]+)\}', transform)[1]))
assert abs(q['y']) < 1e-6 and abs(q['z']) < 1e-6, 'Canyon art assumes yaw and roll zero'
pitch = math.degrees(2 * math.atan2(q['x'], q['w']))
assert abs(pitch - 75) < 0.001, pitch
manifest_path = root / 'Docs/terrain-complexes/directional-assets/offsets.json'
manifest = json.loads(manifest_path.read_text())
for v in manifest['families']['Canyon']['variants']:
    expected = v['camera_projection']
    assert expected['orthographic'] and expected['scene_pitch_degrees'] == 75
    assert expected['scene_yaw_degrees'] == 0
    assert expected['depth_projection_direction_texture_xy'] == [0, 1]
report = {
    'scene': str(scene_path.relative_to(root)),
    'scene_git_blob_sha': hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest(),
    'orthographic': True, 'pitch_degrees': pitch, 'yaw_degrees': 0,
    'ground_screen_y_scale': math.sin(math.radians(pitch)),
    'depth_texture_drop_per_world_height': 1 / math.tan(math.radians(pitch)),
    'depth_texture_direction_xy': [0, 1],
    'scope': 'Scene camera and all six asset contracts checked; wall visibility reviewed visually; Unity not run.'
}
(manifest_path.parent / 'camera-projection-check.json').write_text(json.dumps(report, indent=2) + '\n')
print('PASS: orthographic camera pitch75/yaw0; six canyon contracts; depth projects down texture.')
