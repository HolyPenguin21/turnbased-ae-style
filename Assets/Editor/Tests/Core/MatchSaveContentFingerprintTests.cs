#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Game.Tests.Core
{
    public sealed class MatchSaveContentFingerprintTests
    {
        private string root;
        private readonly Dictionary<string, string> assets = new Dictionary<string, string>();
        [SetUp] public void SetUp() { root = Path.Combine(Path.GetTempPath(), "save-fingerprint-" + Guid.NewGuid().ToString("N")); assets.Clear(); }
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private void Write(string guid, string path, string value)
        {
            assets[guid] = path;
            string full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, value); File.WriteAllText(full + ".meta", "guid: " + guid);
        }
        private string Fingerprint(string version = "6000.5.4f1")
        {
            var type = typeof(MatchSaveContentFingerprintTests).Assembly.GetType("Game.EditorTools.MatchSaveContentFingerprint");
            Assert.That(type, Is.Not.Null, "Compatibility inputs must distinguish menu UI from match content.");
            return (string)type.GetMethod("Compute", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { root, version, assets, new HashSet<string>() });
        }
        [Test] public void MenuPrefabSceneAndTextureEditsKeepMatchCompatible()
        {
            Write("world", "Assets/Config/GameConfig.asset", "combat settings");
            Write("panel", "Assets/Prefabs/UI/Panels/MatchSaveSlots.prefab", "old layout");
            Write("scene", "Assets/Scenes/Game.unity", "old hierarchy");
            Write("skin", "Assets/Textures/UI/Panels/Save.png", "old skin");
            string before = Fingerprint();
            Write("panel", assets["panel"], "new layout"); Write("scene", assets["scene"], "new hierarchy"); Write("skin", assets["skin"], "new skin");
            Assert.That(Fingerprint(), Is.EqualTo(before));
            Write("settings", "Assets/Prefabs/UI/Panels/AudioOptionsPanel.prefab", "new settings layout");
            Assert.That(Fingerprint(), Is.EqualTo(before));
        }
        [Test] public void GeneratedUiCatalogAndScreenWearEditsDoNotChangeMatchCompatibility()
        {
            Write("world", "Assets/Cards/Catalog.asset", "card rules");
            Write("registry", "Assets/Resources/GameAssetCatalog.asset", "font list v1");
            Write("screen", "Assets/Shaders/ScreenWear.shader", "HUD wear v1");
            Write("overlay", "Assets/Textures/Game/Effects/Overlay.png", "HUD overlay v1");
            string before = Fingerprint();
            Write("registry", assets["registry"], "font list v2 with a new UI font");
            Write("screen", assets["screen"], "HUD wear v2");
            Write("overlay", assets["overlay"], "HUD overlay v2");
            Assert.That(Fingerprint(), Is.EqualTo(before));
        }
        [Test] public void GameplayAssetImporterAndEngineChangesRemainIncompatible()
        {
            Write("cards", "Assets/Cards/Catalog.asset", "unit strength 4");
            string before = Fingerprint();
            Write("cards", assets["cards"], "unit strength 5"); Assert.That(Fingerprint(), Is.Not.EqualTo(before));
            before = Fingerprint(); File.AppendAllText(Path.Combine(root, assets["cards"] + ".meta"), "\nimport setting");
            Assert.That(Fingerprint(), Is.Not.EqualTo(before));
            Assert.That(Fingerprint("different engine"), Is.Not.EqualTo(Fingerprint()));
        }
        [Test] public void GameplayMarkerDependencyIsProtectedEvenInsideUiDirectory()
        {
            Write("sprite", "Assets/Textures/UI/WorldMarker.png", "marker v1");
            var type = typeof(MatchSaveContentFingerprintTests).Assembly.GetType("Game.EditorTools.MatchSaveContentFingerprint");
            Assert.That(type, Is.Not.Null);
            var method = type.GetMethod("Compute", BindingFlags.Static | BindingFlags.NonPublic);
            var required = new HashSet<string> { assets["sprite"] };
            string first = (string)method.Invoke(null, new object[] { root, "6000.5.4f1", assets, required });
            Write("sprite", assets["sprite"], "marker v2");
            Assert.That((string)method.Invoke(null, new object[] { root, "6000.5.4f1", assets, required }), Is.Not.EqualTo(first));
        }
        [Test] public void RenameWithPreservedGuidAndSourceKeepsFingerprint()
        {
            Write("stable-guid", "Assets/Cards/Before.asset", "same card"); string before = Fingerprint();
            Write("stable-guid", "Assets/Cards/After.asset", "same card");
            Assert.That(Fingerprint(), Is.EqualTo(before));
        }
    }
}
#endif
