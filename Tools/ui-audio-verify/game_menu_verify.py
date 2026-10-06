from pathlib import Path
import re
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
  for name in ['GameMenuUI','GameMenuPanel','SaveGameDisabled','LoadGameDisabled','ContinueGame']:
   self.assertTrue('m_Name: '+name in s,name)
 def test_corner_button_sprite(self):
  sprite=ROOT/'Assets/Textures/UI/Buttons/Button_Settings.png'
  self.assertTrue(sprite.exists())
  guid=re.search(r'^guid: (\w+)$',Path(str(sprite)+'.meta').read_text(),re.M).group(1)
  scene=(ROOT/'Assets/Scenes/Game.unity').read_text()
  blocks=re.split(r'^--- ',scene,flags=re.M)
  gear=next(b for b in blocks if '  m_Name: GameMenuGear\n' in b)
  gear_id=re.search(r'^!u!1 &(\d+)',gear).group(1)
  image=next(b for b in blocks if f'  m_GameObject: {{fileID: {gear_id}}}\n' in b and '  m_Sprite:' in b)
  self.assertIn(f'm_Sprite: {{fileID: 21300000, guid: {guid}, type: 3}}',image)
if __name__=='__main__':unittest.main()
