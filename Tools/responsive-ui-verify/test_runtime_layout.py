"""Contracts for the authored layout; native Unity interactions are tested in EditMode."""
import math
import unittest
from pathlib import Path
from verify import load, Scene, contains, validate

ROOT = Path(__file__).resolve().parents[2]
SIZES = [(1280, 720), (1920, 1080), (2560, 1440), (1920, 1200), (2560, 1080), (1024, 768)]


class RuntimeLayoutContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.menu = load(ROOT / 'Assets/Scenes/MainMenu.unity')
        cls.row = load(ROOT / 'Assets/Prefabs/UI/Rows/PlayerRow.prefab')

    def test_collection_columns_do_not_overlap_at_supported_aspects(self):
        for w, h in SIZES:
            scale = math.sqrt(w / 1280 * h / 720)
            scene = Scene(self.menu, (w / scale, h / scale))
            left = scene.box(939962675)
            cards = scene.box(197671396)
            deck = scene.box(1258348604)
            with self.subTest(size=(w, h)):
                self.assertLessEqual(left[0] + left[2] + 8, cards[0])
                self.assertLessEqual(cards[0] + cards[2] + 8, deck[0])
                self.assertTrue(contains((0, 0, w / scale, h / scale), deck))
                self.assertGreater(cards[2], 300)

    def test_collection_description_keeps_a_readable_viewport_on_short_screens(self):
        for w, h in SIZES:
            scale = math.sqrt(w / 1280 * h / 720)
            scene = Scene(self.menu, (w / scale, h / scale))
            self.assertGreaterEqual(scene.box(1007287568)[3], 90,
                                    'Description must not collapse beneath the card preview')

    def test_collection_status_does_not_overlap_the_title_at_narrow_aspects(self):
        for w, h in SIZES:
            scale = math.sqrt(w / 1280 * h / 720)
            scene = Scene(self.menu, (w / scale, h / scale))
            status, title = scene.box(499957832), scene.box(1037226307)
            self.assertLessEqual(status[0] + status[2] + 8, title[0],
                                 'Status and title must have separate header areas')

    def test_six_player_rows_have_a_scrollable_viewport(self):
        scene = Scene(self.menu, (1280, 720))
        content = self.menu[162901672][1]
        viewport_id = content['m_Father']['fileID']
        viewport = self.menu[viewport_id][1]
        scroll_root = viewport['m_Father']['fileID']
        scroll_go = self.menu[scroll_root][1]['m_GameObject']['fileID']
        scrolls = [d for _, d in self.menu.values()
                   if d.get('m_GameObject', {}).get('fileID') == scroll_go
                   and 'm_Viewport' in d]
        self.assertEqual(len(scrolls), 1, 'Player list needs a ScrollRect')
        self.assertEqual(scrolls[0]['m_Content']['fileID'], 162901672)
        self.assertEqual(scrolls[0]['m_Viewport']['fileID'], viewport_id)
        self.assertEqual(scrolls[0]['m_Horizontal'], 0)
        self.assertEqual(scrolls[0]['m_Vertical'], 1)
        row_height = self.row[2038967255859839617][1]['m_PreferredHeight']
        self.assertGreaterEqual(row_height, 86)
        for w, h in SIZES:
            scale = math.sqrt(w / 1280 * h / 720)
            scene = Scene(self.menu, (w / scale, h / scale))
            window = scene.box(759005952)
            area = scene.box(viewport_id)
            self.assertTrue(contains(window, area))
            self.assertGreater(area[3], row_height)
            start = scene.box(502412740)
            self.assertGreaterEqual(area[1], start[1] + start[3] + 8)

    def test_changed_yaml_has_valid_links_and_component_ownership(self):
        for name in ['Assets/Scenes/MainMenu.unity', 'Assets/Scenes/Game.unity', 'Assets/Prefabs/UI/Rows/PlayerRow.prefab',
                     'Assets/Prefabs/UI/Collection/CollectionCard.prefab']:
            path = ROOT / name
            validate(path, load(path))


if __name__ == '__main__':
    unittest.main()
