#if UNITY_EDITOR
using System.Linq;
using Game.Core;
using UnityEditor;
using UnityEngine;

// Keep externally supplied PNGs out of Git while registering the user's real local GUIDs.
[InitializeOnLoad]
public sealed class BattleSkillEffectsAssetSetup : AssetPostprocessor
{
    private static readonly string[] Paths =
    {
        "Assets/Textures/UI/Effects/BodyguardShield.png",
        "Assets/Textures/UI/Effects/ShockImpulse.png",
        "Assets/Textures/UI/Effects/Flipbooks/Scorcher_Flipbook.png",
        "Assets/Textures/UI/Effects/Flipbooks/Splash_Flipbook.png",
        "Assets/Shaders/BattleSkillFlipbook.shader"
    };
    private static bool _queued, _registering;
    static BattleSkillEffectsAssetSetup() => Schedule();
    private static void Schedule()
    {
        if (_queued || _registering) return;
        _queued = true;
        EditorApplication.delayCall += Register;
    }
    private void OnPreprocessTexture()
    {
        if (Paths.Contains(assetPath)) Configure((TextureImporter)assetImporter, assetPath.Contains("/Flipbooks/"));
    }
    private static bool Configure(TextureImporter importer, bool atlas)
    {
        var type = atlas ? TextureImporterType.Default : TextureImporterType.Sprite;
        int maxSize = atlas ? 2048 : 1024;
        bool changed = importer.textureType != type || importer.mipmapEnabled || !importer.alphaIsTransparency
            || !importer.sRGBTexture || importer.wrapMode != TextureWrapMode.Clamp
            || importer.filterMode != FilterMode.Bilinear || importer.maxTextureSize != maxSize
            || importer.textureCompression != TextureImporterCompression.Uncompressed
            || importer.npotScale != TextureImporterNPOTScale.None
            || (!atlas && importer.spriteImportMode != SpriteImportMode.Single);
        if (!changed) return false;
        importer.textureType = type;
        importer.mipmapEnabled = false; importer.alphaIsTransparency = true; importer.sRGBTexture = true;
        importer.wrapMode = TextureWrapMode.Clamp; importer.filterMode = FilterMode.Bilinear;
        importer.maxTextureSize = maxSize; importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.npotScale = TextureImporterNPOTScale.None;
        if (!atlas) importer.spriteImportMode = SpriteImportMode.Single;
        return true;
    }
    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
    {
        if (imported.Concat(deleted).Concat(moved).Any(p => Paths.Contains(p))) Schedule();
    }
    [MenuItem("Tools/Register Battle Skill Effects")]
    public static void Register()
    {
        _queued = false;
        if (_registering) return;
        _registering = true;
        try
        {
            var catalog = AssetDatabase.LoadAssetAtPath<GameAssetCatalog>(GameAssetCatalogBuilder.CatalogPath);
            if (catalog == null) return;
            var entries = catalog.Entries.ToList();
            bool changed = false;
            foreach (string path in Paths)
            {
                if (AssetImporter.GetAtPath(path) is TextureImporter importer
                    && Configure(importer,path.Contains("/Flipbooks/"))) importer.SaveAndReimport();
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null) continue;
                string key = path.StartsWith("Assets/Shaders/") ? "Shaders/BattleSkillFlipbook"
                    : "UI/Effects/" + System.IO.Path.ChangeExtension(path.Substring("Assets/Textures/UI/Effects/".Length),null);
                var assets = new[] { asset }.Concat(AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                    .OfType<Sprite>().Cast<Object>()).ToArray();
                int index = entries.FindIndex(e => e.key == key);
                if (index >= 0 && entries[index].assets != null
                    && entries[index].assets.SequenceEqual(assets)) continue;
                var entry = new GameAssetCatalog.Entry { key = key, assets = assets };
                if (index >= 0) entries[index] = entry; else entries.Add(entry);
                changed = true;
            }
            if (changed) { catalog.ReplaceEntries(entries); EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssets(); }
        }
        finally { _registering = false; }
    }
}
#endif
