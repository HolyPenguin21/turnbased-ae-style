"""Read authored Unity object graphs and check save-window integration without Unity."""
import re
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
def documents(path):
    text = path.read_text()
    return {m[1]:m[2] for m in re.finditer(r'^--- !u!\d+ &(-?\d+)[^\n]*\n(.*?)(?=^--- !u!|\Z)', text, re.M | re.S)}
def reference(doc, field):
    m = re.search(r'^[ ]+'+re.escape(field)+r': \{fileID: (-?\d+)\}', doc, re.M)
    return m[1] if m else None
class SaveUiAssetTests(unittest.TestCase):
    def test_shared_prefab_is_wired_into_both_scenes(self):
        prefab = ROOT/'Assets/Prefabs/UI/Panels/MatchSaveSlots.prefab'
        self.assertTrue(prefab.is_file(), 'Save/Load prefab is missing')
        guid = re.search(r'guid: (\w+)', prefab.with_suffix('.prefab.meta').read_text())[1]
        pdocs = documents(prefab)
        owner = next(v for v in pdocs.values() if 'Game.UI.MatchSaveSlotsUI' in v)
        for field in ['title','description','status','action','actionText','previous','next','add','cancel','deleteButton','deleteConfirmation','deleteMessage','confirmDeleteButton','cancelDeleteButton','contentGroup']:
            self.assertIn(reference(owner, field), pdocs, field+' must resolve to an authored control')
        for array in ['rowButtons','rowLabels']:
            refs = re.search(r'^  '+array+r':\n((?:  -.*\n)+)',owner,re.M)[1]
            self.assertEqual(len(re.findall(r'fileID:',refs)),6)
        for scene, controller in [('MainMenu','Game.UI.MainMenuController'),('Game','Game.UI.GameMenuPanelUI')]:
            docs = documents(ROOT/f'Assets/Scenes/{scene}.unity')
            component = next(v for v in docs.values() if 'm_EditorClassIdentifier: Assembly-CSharp::'+controller in v)
            window = docs[reference(component,'saveSlots')]
            self.assertIn(guid,window)
            instance = docs[reference(window,'m_PrefabInstance')]
            self.assertIn('guid: '+guid,instance)
            self.assertRegex(instance,r'propertyPath: m_IsActive\n\s+value: 0')
    def test_both_instances_inherit_canvas_scaling(self):
        for scene, controller in [('MainMenu','Game.UI.MainMenuController'),('Game','Game.UI.GameMenuPanelUI')]:
            docs = documents(ROOT/f'Assets/Scenes/{scene}.unity')
            component = next(v for v in docs.values() if 'm_EditorClassIdentifier: Assembly-CSharp::'+controller in v)
            window = docs[reference(component,'saveSlots')]
            instance = docs[reference(window,'m_PrefabInstance')]
            parent = reference(instance,'m_TransformParent')
            found = False
            while parent and parent != '0':
                transform = docs[parent]
                go = docs[reference(transform,'m_GameObject')]
                components = re.findall(r'component: \{fileID: (-?\d+)\}',go)
                found |= any('UnityEngine.UI::UnityEngine.UI.CanvasScaler' in docs[c] for c in components)
                parent = reference(transform,'m_Father')
            self.assertTrue(found, scene+' window must inherit the same authored 1280x720 UI scale')
    def test_multiplayer_keeps_its_original_authored_disabled_button(self):
        docs = documents(ROOT/'Assets/Scenes/MainMenu.unity')
        button = docs['1704068190']
        self.assertIn('m_Interactable: 0',button)
        first = docs['1970000104']
        self.assertIn('m_MethodName: OnContinueClicked',first)
if __name__ == '__main__': unittest.main()
