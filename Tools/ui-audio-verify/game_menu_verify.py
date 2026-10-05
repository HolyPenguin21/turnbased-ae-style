from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[2]
class MenuIntegration(unittest.TestCase):
 def test_shared_options_asset(self):
  p=ROOT/'Assets/Prefabs/UI/AudioOptionsPanel.prefab'
  self.assertTrue(p.exists())
  guid=Path(str(p)+'.meta').read_text().split('guid: ')[1].splitlines()[0]
  for name in ['MainMenu','Game']:
   self.assertTrue(guid in (ROOT/f'Assets/Scenes/{name}.unity').read_text(),name)
 def test_scene_menu_has_disabled_placeholders(self):
  s=(ROOT/'Assets/Scenes/Game.unity').read_text()
  for name in ['GameMenuCanvas','GameMenuPanel','SaveGameDisabled','LoadGameDisabled','ContinueGame']:
   self.assertTrue('m_Name: '+name in s,name)
 def test_corner_button_sprite(self):
  self.assertTrue((ROOT/'Assets/Resources/UI/GameMenuGear.png').exists())
if __name__=='__main__':unittest.main()
