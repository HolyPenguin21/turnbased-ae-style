"""Structural integration gate; real engine behavior is tested by Unity fixtures."""
from pathlib import Path
import re, unittest
ROOT=Path(__file__).resolve().parents[2]
class AudioIntegration(unittest.TestCase):
 def test_runtime_service_asset_exists(self):
  self.assertTrue((ROOT/'Assets/Resources/Audio/GameAudio.prefab').is_file())
 def test_legacy_audio_removed(self):
  self.assertFalse((ROOT/'Assets/Scripts/PersistentAudio.cs').exists())
  scene=(ROOT/'Assets/Scenes/MainMenu.unity').read_text()
  self.assertNotIn('PlayButtonSound',scene)
 def test_resets_owned_by_audio_utility(self):
  bad=[]
  for p in (ROOT/'Assets/Scripts').rglob('*.cs'):
   if '.onClick.RemoveAllListeners()' in p.read_text() and p.name!='UIButtonEventUtility.cs':bad.append(str(p.relative_to(ROOT)))
  self.assertEqual([],bad)
 def test_runtime_created_ui_is_registered(self):
  paths=list((ROOT/'Assets/Scripts/UI').glob('*.cs'))+[ROOT/'Assets/Scripts/Setup/GameSetupController.cs']
  missing=[]
  for p in paths:
   lines=p.read_text().splitlines()
   for i,line in enumerate(lines):
    if re.search(r'\bInstantiate\(',line) and 'BindCreatedRoot' not in '\n'.join(lines[i:i+3]):missing.append(f'{p.name}:{i+1}')
  self.assertEqual([],missing)
 def test_settings_menu_is_wired(self):
  scene=(ROOT/'Assets/Scenes/MainMenu.unity').read_text()
  for field in ['settingsPanel:', 'masterVolumeSlider:', 'musicEnabledToggle:', 'musicVolumeSlider:', 'OnSettingsClicked']:
   self.assertTrue(field in scene,field)
 def test_every_new_script_has_matching_meta(self):
  for directory in ['Audio']:
   for p in (ROOT/'Assets/Scripts'/directory).glob('*.cs'):
    self.assertTrue(Path(str(p)+'.meta').exists(),str(p))
 def test_existing_clip_guids_preserved(self):
  for name,guid in [('ui_click','38d9e1e3ce19f0a43980542fe9521ea9'),('ui_info_radio_hiss','9f3d9dfe88d1ec0429e48daca955c5c1')]:
   self.assertIn('guid: '+guid,(ROOT/f'Assets/Audio/{name}.wav.meta').read_text())
if __name__=='__main__':unittest.main()
