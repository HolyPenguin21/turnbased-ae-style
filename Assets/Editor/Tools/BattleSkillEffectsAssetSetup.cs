#if UNITY_EDITOR
using System.Linq;
using Game.Core;
using UnityEditor;
using UnityEngine;

// Register the owner's single local atlas without checking the PNG into Git.
// The stable .meta and GameAssetCatalog entry work on fresh clones; this hook
// also repairs catalog references if Unity assigned a different local GUID.
[InitializeOnLoad]
public sealed class BattleSkillEffectsAssetSetup : AssetPostprocessor
{
    private const string AtlasPath = "Assets/Textures/UI/Effects/Effects.png";
    private const string CatalogKey = "UI/Effects/Effects";
    private static bool _queued;
    private static bool _registering;

    static BattleSkillEffectsAssetSetup() => Schedule();

    private static void Schedule()
    {
        if (_queued || _registering) return;
        _queued = true;
        EditorApplication.delayCall += Register;
    }

    private void OnPreprocessTexture()
    {
        if (assetPath != AtlasPath) return;
        var importer = (TextureImporter)assetImporter;
        importer.textureType = TextureImporterType.Default;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.sRGBTexture = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.maxTextureSize = 2048;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.npotScale = TextureImporterNPOTScale.None;
    }

    private static void OnPostprocessAllAssets(string[] imported, string[] deleted,
        string[] moved, string[] oldPaths)
    {
        if (imported.Contains(AtlasPath) || moved.Contains(AtlasPath)) Schedule();
    }

    [MenuItem("Tools/Register Battle Skill Atlas")]
    public static void Register()
    {
        _queued = false;
        if (_registering) return;
        _registering = true;
        try
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);
            if (texture == null) return;
            if (texture.width != BattleSkillEffect.AtlasWidth ||
                texture.height != BattleSkillEffect.AtlasHeight)
            {
                Debug.LogWarning("Effects.png must be 1536x1024 (8 columns, 5 rows).");
            }

            var catalog = AssetDatabase.LoadAssetAtPath<GameAssetCatalog>(
                GameAssetCatalogBuilder.CatalogPath);
            if (catalog == null) return;

            var entries = catalog.Entries.ToList();
            int index = entries.FindIndex(e => e.key == CatalogKey);
            if (index >= 0 && entries[index].assets != null
                && entries[index].assets.Length == 1
                && entries[index].assets[0] == texture) return;

            var entry = new GameAssetCatalog.Entry
            {
                key = CatalogKey,
                assets = new Object[] { texture }
            };
            if (index < 0) entries.Add(entry);
            else entries[index] = entry;

            catalog.ReplaceEntries(entries);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
        }
        finally { _registering = false; }
    }
}
#endif
