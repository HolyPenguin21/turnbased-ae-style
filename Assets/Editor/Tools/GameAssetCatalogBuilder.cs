#if UNITY_EDITOR
using Game.Presentation;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Core;
using Game.UI;
using UnityEditor;
using UnityEngine;

public static class GameAssetCatalogBuilder
{
    public const string CatalogPath = "Assets/Resources/GameAssetCatalog.asset";

    // (project folder, key prefix). Keys mirror the former Assets/Resources paths, extension-less.
    private static readonly (string Folder, string Prefix)[] Sources =
    {
        ("Assets/Fonts", "Fonts"),
        ("Assets/Shaders", "Shaders"),
        ("Assets/Prefabs/TutorialHints", "UI/TutorialHints"),
        ("Assets/Prefabs/Audio", "Audio"),
        ("Assets/Textures/Campaign", "Campaign"),
        ("Assets/Textures/UI/Backgrounds/Collection", "UI/Backgrounds/Collection"),
        ("Assets/Textures/UI/Buttons/Collection", "UI/Buttons/Collection"),
        ("Assets/Textures/UI/Controls", "UI/Controls"),
        ("Assets/Textures/UI/Decorations/Campaign", "UI/Decorations/Campaign"),
        ("Assets/Textures/UI/Decorations/Collection", "UI/Decorations/Collection"),
        ("Assets/Textures/UI/Effects", "UI/Effects"),
        ("Assets/Textures/UI/Panels/Campaign", "UI/Panels/Campaign"),
        ("Assets/Textures/UI/Panels/Collection", "UI/Panels/Collection"),
        ("Assets/Textures/UI/Panels/Rewards", "UI/Panels/Rewards"),
    };

    private static readonly (string File, string Key)[] SingleFiles =
    {
        ("Assets/Textures/Game/Effects/CardPlacementDissolve.shader", CardDissolveEffect.ShaderKey),
        ("Assets/Textures/Game/Effects/Overlay.png", "Effects/Overlay"),
        ("Assets/Textures/Game/Effects/FogOfWar.mat", "Materials/FogOfWar"),
        ("Assets/Shaders/ScreenWear.shader", "Effects/ScreenWear"),
        // Reward prefabs live with the other UI prefabs; keys keep their old Prefabs/Rewards names.
        ("Assets/Prefabs/UI/Cards/Card_Reward.prefab", CollectionRewardUI.RewardCardKey),
        ("Assets/Prefabs/UI/Panels/CollectionRewards.prefab", CollectionRewardUI.RewardPanelKey),
        ("Assets/Prefabs/UI/Panels/CollectionMessageDialog.prefab", CollectionMessageUI.MessageDialogKey),
        ("Assets/Prefabs/UI/Panels/RewardDialog.prefab", CollectionMessageUI.RewardDialogKey),
    };

    [MenuItem("Tools/Rebuild Game Asset Catalog")]
    public static void Rebuild()
    {
        var catalog = AssetDatabase.LoadAssetAtPath<GameAssetCatalog>(CatalogPath);
        if (catalog == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
            catalog = ScriptableObject.CreateInstance<GameAssetCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
        }

        var entries = new List<GameAssetCatalog.Entry>();
        foreach (var (folder, prefix) in Sources)
        {
            if (!AssetDatabase.IsValidFolder(folder)) { Debug.LogWarning($"GameAssetCatalog: missing folder {folder}"); continue; }
            foreach (var guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;
                string relative = path.Substring(folder.Length + 1);
                AddEntry(entries, prefix + "/" + Path.ChangeExtension(relative, null).Replace('\\', '/'), path);
            }
        }
        foreach (var (file, key) in SingleFiles) AddEntry(entries, key, file);

        entries.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        catalog.ReplaceEntries(entries);
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        Debug.Log($"GameAssetCatalog: {entries.Count} entries.");
    }

    private static void AddEntry(List<GameAssetCatalog.Entry> entries, string key, string path)
    {
        var assets = new List<Object>();
        var main = AssetDatabase.LoadMainAssetAtPath(path);
        if (main == null) return;
        assets.Add(main);
        // A sprite texture's main asset is the Texture2D; GameAssetCatalog.Load<Sprite> also found its sprites.
        if (main is Texture2D)
            assets.AddRange(AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<Sprite>());
        entries.Add(new GameAssetCatalog.Entry { key = key, assets = assets.ToArray() });
    }
}
#endif
