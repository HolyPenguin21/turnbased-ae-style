"""Read-only scene/catalog integration checks; engine behavior still needs Unity."""
from pathlib import Path
import re
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[2]


def unity_objects(path):
    source = (ROOT / path).read_text()
    return {
        int(match[2]): (int(match[1]), next(iter(yaml.safe_load(match[3]).values())))
        for match in re.finditer(
            r'^--- !u!(\d+) &(\d+)(?: stripped)?\n([\s\S]*?)(?=^--- !u!|\Z)', source, re.M)
    }


class HelpIntegration(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.objects = unity_objects('Assets/Scenes/Game.unity')
        cls.catalog = unity_objects('Assets/Config/GameHelpCatalog.asset')[11400000][1]
        cls.menu = next(obj for _, obj in cls.objects.values()
                        if obj.get('m_EditorClassIdentifier') == 'Assembly-CSharp::Game.UI.GameMenuPanelUI')
        cls.view = cls.objects[cls.menu['helpPanel']['fileID']][1]

    def resolve(self, ref):
        return self.objects[ref['fileID']][1]

    def component(self, game_object_id, kind):
        return next(obj for _, obj in self.objects.values()
                    if obj.get('m_GameObject', {}).get('fileID') == game_object_id
                    and obj.get('m_EditorClassIdentifier', '').endswith('::' + kind))

    def rect(self, game_object_id):
        return next(obj for type_id, obj in self.objects.values()
                    if type_id == 224 and obj.get('m_GameObject', {}).get('fileID') == game_object_id)

    def test_help_is_separate_sibling_of_menu_blocking_root(self):
        help_object = self.resolve(self.view['m_GameObject'])
        self.assertEqual(help_object['m_Name'], 'GameHelpBlockingRoot')
        self.assertEqual(help_object['m_IsActive'], 0)
        help_rect = self.rect(self.view['m_GameObject']['fileID'])
        menu_rect = self.rect(self.menu['blockingRoot']['fileID'])
        self.assertEqual(help_rect['m_Father'], menu_rect['m_Father'])
        parent = self.resolve(help_rect['m_Father'])
        self.assertEqual(parent['m_GameObject'], self.menu['m_GameObject'])
        for game_object_id in [self.view['m_GameObject']['fileID'], self.menu['blockingRoot']['fileID']]:
            child_rect_id = next(key for key, (kind, obj) in self.objects.items()
                                 if kind == 224 and obj.get('m_GameObject', {}).get('fileID') == game_object_id)
            self.assertIn({'fileID': child_rect_id}, parent['m_Children'])

    def test_existing_help_button_and_all_bindings_have_correct_types(self):
        help_button = self.resolve(self.menu['helpButton'])
        self.assertEqual(self.resolve(help_button['m_GameObject'])['m_Name'], 'Button_Help')
        for field, kind in {
            'windowTitle': 'TMPro.TextMeshProUGUI', 'closeLabel': 'TMPro.TextMeshProUGUI',
            'articleTitle': 'TMPro.TextMeshProUGUI', 'sectionTitleTemplate': 'TMPro.TextMeshProUGUI',
            'textBlockTemplate': 'TMPro.TextMeshProUGUI', 'closeButton': 'UnityEngine.UI.Button',
            'topicButtonTemplate': 'UnityEngine.UI.Button', 'imageBlockTemplate': 'UnityEngine.UI.Image',
            'topicsScroll': 'UnityEngine.UI.ScrollRect', 'articleScroll': 'UnityEngine.UI.ScrollRect',
        }.items():
            self.assertTrue(self.resolve(self.view[field])['m_EditorClassIdentifier'].endswith('::' + kind), field)
        for field, path in [('m_Script', 'Assets/Scripts/UI/Panels/GameHelpPanelUI.cs'),
                            ('catalog', 'Assets/Config/GameHelpCatalog.asset')]:
            guid = re.search(r'^guid: (\w+)$', (ROOT / (path + '.meta')).read_text(), re.M)[1]
            self.assertEqual(self.view[field]['guid'], guid)

    def test_both_scroll_columns_are_independent_masked_and_clamped(self):
        content_ids = set()
        for field in ['topicsScroll', 'articleScroll']:
            scroll = self.resolve(self.view[field])
            self.assertEqual(scroll['m_Horizontal'], 0)
            self.assertEqual(scroll['m_Vertical'], 1)
            self.assertEqual(scroll['m_MovementType'], 2)
            viewport = self.resolve(scroll['m_Viewport'])
            self.component(viewport['m_GameObject']['fileID'], 'UnityEngine.UI.RectMask2D')
            content = self.resolve(scroll['m_Content'])
            self.assertEqual(content['m_Father'], scroll['m_Viewport'])
            group = self.component(content['m_GameObject']['fileID'], 'UnityEngine.UI.VerticalLayoutGroup')
            self.assertEqual(group['m_ChildControlWidth'], 1)
            self.assertEqual(group['m_ChildControlHeight'], 1)
            self.assertEqual(group['m_ChildForceExpandHeight'], 0)
            fit = self.component(content['m_GameObject']['fileID'], 'UnityEngine.UI.ContentSizeFitter')
            self.assertEqual((fit['m_HorizontalFit'], fit['m_VerticalFit']), (0, 2))
            bar = self.resolve(scroll['m_VerticalScrollbar'])
            self.assertTrue(bar['m_EditorClassIdentifier'].endswith('UnityEngine.UI.Scrollbar'))
            self.assertEqual(bar['m_Direction'], 2)
            content_ids.add(scroll['m_Content']['fileID'])
        self.assertEqual(len(content_ids), 2)

    def test_help_owner_is_wired_into_the_match_input_controller(self):
        menu_id = next(key for key, (_, obj) in self.objects.items() if obj is self.menu)
        turns = [obj for _, obj in self.objects.values()
                 if obj.get('m_EditorClassIdentifier', '').endswith('::Game.Turns.GameTurnController')]
        self.assertEqual(len(turns), 1)
        self.assertEqual(turns[0]['gameMenu']['fileID'], menu_id)

    def test_templates_are_inactive_and_localized_text_can_wrap(self):
        for field in ['sectionTitleTemplate', 'textBlockTemplate', 'imageBlockTemplate', 'topicButtonTemplate']:
            template = self.resolve(self.view[field])
            self.assertEqual(self.resolve(template['m_GameObject'])['m_IsActive'], 0, field)
        for field in ['sectionTitleTemplate', 'textBlockTemplate', 'articleTitle']:
            text = self.resolve(self.view[field])
            self.assertEqual((text['m_RaycastTarget'], text['m_Maskable'], text['m_TextWrappingMode']), (0, 1, 1))
        row = self.resolve(self.view['topicButtonTemplate'])
        layout = self.component(row['m_GameObject']['fileID'], 'UnityEngine.UI.VerticalLayoutGroup')
        self.assertEqual(layout['m_ChildControlHeight'], 1)
        size = self.component(row['m_GameObject']['fileID'], 'UnityEngine.UI.LayoutElement')
        self.assertLess(size['m_PreferredHeight'], 0, 'Long translations must grow the row.')

    def test_all_topics_have_unique_stable_ids_keys_and_review_rows(self):
        ids, keys = set(), set()
        review = (ROOT / 'Docs/GameHelp_Topics_Validation.md').read_text()
        def localized(text):
            self.assertTrue(text['key'].startswith('help.'))
            self.assertNotIn(text['key'], keys)
            keys.add(text['key'])
            self.assertTrue(text['fallback'].strip())
        for key in ['windowTitle', 'closeLabel', 'pendingArticle']:
            localized(self.catalog[key])
        for section in self.catalog['sections']:
            self.assertNotIn(section['id'], ids)
            ids.add(section['id']); localized(section['title'])
            for topic in section['topics']:
                self.assertNotIn(topic['id'], ids)
                ids.add(topic['id']); localized(topic['title'])
                self.assertEqual(topic['title']['key'], f"help.topic.{topic['id']}.title")
                self.assertIn(f"| `{topic['id']}` |", review)
        count = sum(len(s['topics']) for s in self.catalog['sections'])
        self.assertIn(f'тем: {count}.', review)
        self.assertEqual(len(re.findall(r'^\| `[^`]+` \|', review, re.M)), count)

    def test_ability_index_covers_the_actual_shared_ability_list(self):
        source = (ROOT / 'Assets/Scripts/Cards/UnitAbilities.cs').read_text()
        constants = dict(re.findall(r'public const string (\w+) = "([^"]+)";', source))
        all_list = re.search(r'public static readonly string\[\] All\s*=\s*\{([\s\S]*?)\};', source)[1]
        tags = [constants[key] for key in re.findall(r'\b\w+\b', all_list)]
        entries = next(s for s in self.catalog['sections'] if s['id'] == 'abilities')['topics']
        self.assertCountEqual([f'ability-{tag.lower()}' for tag in tags], [t['id'] for t in entries])

    def test_documented_source_paths_exist(self):
        review = (ROOT / 'Docs/GameHelp_Topics_Validation.md').read_text()
        for path in re.findall(r'Assets/[A-Za-z0-9_./-]+', review):
            self.assertTrue((ROOT / path).exists(), path)

    def test_new_objects_have_consistent_parent_child_links(self):
        for object_id, (kind, obj) in self.objects.items():
            if kind != 224 or object_id < 2160000000 or object_id > 2160001000:
                continue
            parent = self.resolve(obj['m_Father'])
            self.assertIn({'fileID': object_id}, parent['m_Children'], str(object_id))
            for child in obj['m_Children']:
                self.assertEqual(self.resolve(child)['m_Father']['fileID'], object_id)


if __name__ == '__main__':
    unittest.main()
