#!/usr/bin/env python3
"""Source-only Unity UI validation. Does not claim an Editor or player run."""
import functools
import copy
import math
import re
from pathlib import Path
import yaml

ROOT = Path(__file__).resolve().parents[2]
RESOLUTIONS = [(1280, 720), (1600, 900), (1920, 1080), (2560, 1440)]


class UniqueLoader(yaml.SafeLoader):
    pass


def unique_mapping(loader, node, deep=False):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        if key in result:
            raise ValueError(f"Duplicate YAML key: {key}")
        result[key] = loader.construct_object(value_node, deep=deep)
    return result


UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, unique_mapping)


def load(path):
    raw = path.read_text(encoding="utf-8")
    headers = [(int(t), int(i)) for t, i in re.findall(r"^--- !u!(\d+) &(-?\d+)", raw, re.M)]
    assert len(set(i for _, i in headers)) == len(headers), f"Duplicate fileID: {path}"
    clean = re.sub(r"^%.*\n", "", raw, flags=re.M)
    clean = re.sub(r"^--- !u!\d+ &[-\d]+(?: stripped)?$", "---", clean, flags=re.M)
    docs = list(yaml.load_all(clean, Loader=UniqueLoader))
    assert len(headers) == len(docs), f"Document count: {path}"
    return {i: (t, next(iter(doc.values()))) for (t, i), doc in zip(headers, docs)}


def refs(value):
    if isinstance(value, dict):
        if "fileID" in value and "guid" not in value and value["fileID"]:
            yield value["fileID"]
        for child in value.values():
            yield from refs(child)
    elif isinstance(value, list):
        for child in value:
            yield from refs(child)


def validate(path, objects):
    for id, (kind, obj) in objects.items():
        assert -(2**63) <= id < 2**63, f"Int64 overflow: {path}:{id}"
        for ref in refs(obj):
            assert ref in objects, f"Missing local reference: {path}:{id}->{ref}"
        if "m_GameObject" in obj:
            go = obj["m_GameObject"]["fileID"]
            # A stripped component is a proxy for its external prefab source. Unity
            # leaves m_GameObject=0 until that prefab is resolved during import.
            if not go and obj.get("m_CorrespondingSourceObject", {}).get("guid"):
                assert obj.get("m_PrefabInstance", {}).get("fileID") in objects
                continue
            assert objects[go][0] == 1, f"Wrong GameObject type: {path}:{id}"
            # Stripped prefab components have no local component list.
            if "m_Component" in objects[go][1]:
                components = [c["component"]["fileID"] for c in objects[go][1]["m_Component"]]
                assert id in components, f"Dangling component: {path}:{id}"
        if "m_Father" in obj and obj["m_Father"]["fileID"]:
            parent = obj["m_Father"]["fileID"]
            assert objects[parent][0] in (4, 224), f"Wrong parent type: {path}:{id}"
            if "m_Children" in objects[parent][1]:
                children = [c["fileID"] for c in objects[parent][1]["m_Children"]]
                assert id in children, f"Parent omits child: {path}:{parent}->{id}"
        for child in obj.get("m_Children", []):
            child_id = child["fileID"]
            if "m_Father" in objects[child_id][1]:
                assert objects[child_id][1]["m_Father"]["fileID"] == id, f"Wrong child parent: {path}:{child_id}"


class Scene:
    def __init__(self, objects, canvas_size):
        self.objects = objects
        self.canvas_size = canvas_size

    def data(self, id):
        return self.objects[id][1]

    def name(self, id):
        return self.data(self.data(id)["m_GameObject"]["fileID"]).get("m_Name", "")

    @functools.lru_cache(None)
    def box(self, id):
        obj = self.data(id)
        if "m_AnchorMin" not in obj:
            return None
        parent = obj["m_Father"]["fileID"]
        if not parent:
            return (0, 0, *self.canvas_size)
        parent_box = self.box(parent)
        if parent_box is None:
            return None
        x, y, w, h = parent_box
        amin, amax = obj["m_AnchorMin"], obj["m_AnchorMax"]
        pos, delta, pivot = obj["m_AnchoredPosition"], obj["m_SizeDelta"], obj["m_Pivot"]
        return (x + w * amin["x"] + pos["x"] - delta["x"] * pivot["x"],
                y + h * amin["y"] + pos["y"] - delta["y"] * pivot["y"],
                w * (amax["x"] - amin["x"]) + delta["x"],
                h * (amax["y"] - amin["y"]) + delta["y"])


def contains(outer, inner):
    return (inner[0] >= outer[0] - .01 and inner[1] >= outer[1] - .01
            and inner[0] + inner[2] <= outer[0] + outer[2] + .01
            and inner[1] + inner[3] <= outer[1] + outer[3] + .01)


def grid_extent(group, count):
    cols = group["m_ConstraintCount"]
    rows = math.ceil(count / cols)
    cell, gap, pad = group["m_CellSize"], group["m_Spacing"], group["m_Padding"]
    return (cols * cell["x"] + (cols - 1) * gap["x"] + pad["m_Left"] + pad["m_Right"],
            rows * cell["y"] + max(0, rows - 1) * gap["y"] + pad["m_Top"] + pad["m_Bottom"])


def check_layout(game, menu, assets):
    for width, height in RESOLUTIONS:
        scale = math.sqrt((width / 1280) * (height / 720))
        logical = (width / scale, height / scale)
        assert math.isclose(logical[0], 1280) and math.isclose(logical[1], 720)
        for objects in (game, menu):
            scene = Scene(objects, logical)
            for id, (kind, obj) in objects.items():
                if "m_ReferenceResolution" in obj:
                    assert obj["m_UiScaleMode"] == 1
                    assert obj["m_ReferenceResolution"] == {"x": 1280, "y": 720}
                    assert obj["m_ScreenMatchMode"] == 0 and obj["m_MatchWidthOrHeight"] == .5
                if kind != 224 or "m_AnchorMin" not in obj:
                    continue
                parent = obj["m_Father"]["fileID"]
                if not parent or scene.box(id) is None:
                    continue
                if "Canvas" in scene.name(parent):
                    assert contains((0, 0, *logical), scene.box(id)), f"Root overflow: {scene.name(id)} at {width}x{height}"

            # Audio settings are the scenes' only prefab instances. Resolve their
            # authored RectTransforms and verify override targets against that source.
            for _, (kind, instance) in objects.items():
                if kind != 1001:
                    continue
                assert instance["m_SourcePrefab"]["guid"] == "c57408e190d6427ba15bf2c1537800e4"
                prefab = copy.deepcopy(assets["Assets/Prefabs/UI/AudioOptionsPanel.prefab"])
                for override in instance["m_Modification"]["m_Modifications"]:
                    target = override["target"]["fileID"]
                    assert target in prefab, f"Missing prefab override target: {target}"
                    # Slider anchors are driven by Slider.value; vertical collapse overrides
                    # must not replace the source's full-height fill and handle.
                    assert not override["propertyPath"].startswith("m_Anchor"), "Stale audio slider anchor override"
                prefab[-1] = (224, {"m_Father": {"fileID": 0}, "m_AnchorMin": {"x": 0, "y": 0}})
                prefab[1950000001][1]["m_Father"] = {"fileID": -1}
                options = Scene(prefab, logical)
                assert contains((0, 0, *logical), options.box(1950000001))

        g, m = Scene(game, logical), Scene(menu, logical)
        stats_viewport = g.box(8880000000000001102)
        assert contains(g.box(1456844806), stats_viewport)
        stats_content = g.data(1505820716)
        assert stats_content["m_Father"]["fileID"] == 8880000000000001102
        assert stats_content["m_AnchorMin"] == {"x": 0, "y": 1}
        assert stats_content["m_AnchorMax"] == {"x": 1, "y": 1}
        assert game[1505820717][1]["m_enableAutoSizing"] == 0
        assert game[1505820717][1]["m_fontSize"] == 16
        assert game[1505820717][1]["m_Maskable"] == 1
        battlefield = g.box(8880000000000000070)
        required = grid_extent(g.data(8880000000000000071), 25)
        fitted = min(battlefield[2] / required[0], battlefield[3] / required[1])
        fitted = max(.25, min(1.35, fitted))
        assert required[0] * fitted <= battlefield[2] + .01
        assert required[1] * fitted <= battlefield[3] + .01
        assert contains(g.box(8880000000000000032), battlefield)
        for id in [8880000000000000086, 8880000000000000089, 8880000000000000062,
                   8880000000000000066, 8880000000000000073, 8880000000000000077,
                   8880000000000000112, 8880000000000000130, 8880000000000000369]:
            assert contains(g.box(8880000000000000032), g.box(id))

        for rect, layout, count in [(178252509, 178252510, 8),
                                    (6660000000000090032, 6660000000000090033, 6)]:
            box, needed = g.box(rect), grid_extent(g.data(layout), count)
            assert needed[0] <= box[2] + .01 and needed[1] <= box[3] + .01, f"Modal grid overflow: {rect}"

        hand = g.data(7770000000000010003)
        needed = 5 * (hand["cardSize"]["x"] * (1 - hand["overlapFraction"]) + hand["cardSpacing"]) + hand["cardSize"]["x"]
        assert needed <= g.box(136068550)[2]
        drawer = g.box(1139703871)
        hand_top = g.box(7770000000000010002)[1] + g.box(7770000000000010002)[3]
        resources = g.box(6660000000000082002)
        for count, fraction in enumerate(g.data(1139703872)["resourceScreenOffsets"]):
            shift = -fraction * logical[1]
            if count:
                lowest = resources[1] + resources[3] - count * resources[3] / 4 + shift
                assert lowest >= hand_top - .01, f"Drawer resource hidden by hand: {count}"
            # The second navigation row ends 127.1 units below the drawer top.
            assert drawer[1] + drawer[3] + shift - 127.1 >= hand_top - .01

        row = assets["Assets/Prefabs/UI/PlayerRow.prefab"][2038967255859839617][1]
        required_height = 6 * row["m_PreferredHeight"] + 5 * m.data(162901673)["m_Spacing"]
        area = m.box(162901672)
        assert required_height <= area[3]
        assert contains(m.box(759005952), area)
        assert area[1] >= m.box(502412740)[1] + m.box(502412740)[3]
        print(f"{width}x{height}: scale={scale:g}; root bounds, battle grid, modal grids, hand, drawer and six-player setup PASS")

    # Every overflowing list has a viewport, a real scroll content child and one extent owner.
    for id, (kind, obj) in game.items():
        if "UnityEngine.UI.ScrollRect" not in obj.get("m_EditorClassIdentifier", ""):
            continue
        content, viewport = obj["m_Content"]["fileID"], obj["m_Viewport"]["fileID"]
        assert game[content][1]["m_Father"]["fileID"] == viewport
        viewport_go = game[viewport][1]["m_GameObject"]["fileID"]
        components = [game[c["component"]["fileID"]][1] for c in game[viewport_go][1]["m_Component"]]
        assert any("RectMask2D" in c.get("m_EditorClassIdentifier", "") for c in components)
    for path in ["Assets/Prefabs/UI/Card_Army.prefab", "Assets/Prefabs/UI/ArmyButton_Map.prefab",
                 "Assets/Prefabs/UI/ArmyButton_Modal.prefab",
                 "Assets/Prefabs/UI/BattleScreen/BattleTurnOrderIcon.prefab"]:
        assert all(obj["m_Maskable"] == 1 for _, obj in assets[path].values() if "m_Maskable" in obj), path
    dice = assets["Assets/Prefabs/UI/DiceRow.prefab"][5551000000000103][1]
    assert dice["m_ChildControlWidth"] == 1 and dice["m_ChildControlHeight"] == 1


def main():
    settings = load(ROOT / "ProjectSettings/ProjectSettings.asset")
    player = next(obj for _, obj in settings.values() if "defaultScreenWidth" in obj)
    assert (player["defaultScreenWidth"], player["defaultScreenHeight"]) == (1920, 1080)
    assert player["resizableWindow"] == 1
    assert "6000.5.4f1" in (ROOT / "ProjectSettings/ProjectVersion.txt").read_text()
    paths = sorted((ROOT / "Assets/Scenes").glob("*.unity")) + sorted((ROOT / "Assets/Prefabs").rglob("*.prefab"))
    assets = {}
    for path in paths:
        objects = load(path)
        validate(path, objects)
        assets[path.relative_to(ROOT).as_posix()] = objects
    print(f"{len(paths)} scenes/prefabs: YAML syntax, duplicate keys, Int64 IDs, references, component ownership and hierarchy PASS")
    check_layout(assets["Assets/Scenes/Game.unity"], assets["Assets/Scenes/MainMenu.unity"], assets)
    print("Source-only checks complete. Unity compilation, text rendering, input and animations still require the Editor.")


if __name__ == "__main__":
    main()
