using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Game.Cards;
using Game.Map;
using Game.Saves;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Automatic authoring infrastructure: no per-sprite manual keys, editor-only AssetDatabase
// lookup, or runtime name/index fallback is needed by a standalone player.
[InitializeOnLoad]
public sealed class MatchSaveAssetManifestBuilder : AssetPostprocessor, IPreprocessBuildWithReport
{
    private const string Output = "Assets/Resources/MatchSaveAssetManifest.asset";
    private static bool building;
    private static bool queued;
    public int callbackOrder => -1000;

    static MatchSaveAssetManifestBuilder()
    {
        Queue();
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode) Rebuild();
        };
    }
    public void OnPreprocessBuild(BuildReport report) => Rebuild();
    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        if (building) return;
        if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(p => p != Output && p != Output + ".meta")) Queue();
    }
    private static void Queue()
    {
        if (queued) return;
        queued = true;
        EditorApplication.delayCall += () => { queued = false; if (!EditorApplication.isPlayingOrWillChangePlaymode) Rebuild(); };
    }
    [MenuItem("Tools/Match saves/Rebuild content manifest")]
    public static void Rebuild()
    {
        if (building) return;
        building = true;
        try
        {
            Directory.CreateDirectory("Assets/Resources");
            var manifest = AssetDatabase.LoadAssetAtPath<MatchSaveAssetManifest>(Output);
            if (manifest == null)
            {
                manifest = ScriptableObject.CreateInstance<MatchSaveAssetManifest>();
                AssetDatabase.CreateAsset(manifest, Output);
            }
            var sprites = new SortedDictionary<string, Sprite>(StringComparer.Ordinal);
            var materials = new SortedDictionary<string, Material>(StringComparer.Ordinal);
            var eventCatalogs = new List<EventCatalog>();
            var prefabs = new SortedDictionary<string, MapObjectVisual>(StringComparer.Ordinal);
            var content = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && !AssetDatabase.IsValidFolder(p)
                    && p != Output && !p.StartsWith("Assets/Editor/", StringComparison.Ordinal)
                    && !p.StartsWith("Assets/Scripts/", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (string path in content)
            {
                // Scene objects cannot be loaded via LoadAllAssetsAtPath; scenes hold no manifest content.
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset is Sprite sprite) sprites[Key(sprite)] = sprite;
                    if (asset is Material material) materials[Key(material)] = material;
                    if (asset is EventCatalog events && !eventCatalogs.Contains(events)) eventCatalogs.Add(events);
                    if (asset is GameObject prefab)
                    {
                        foreach (var marker in prefab.GetComponentsInChildren<MapObjectVisual>(true)) prefabs[Key(marker)] = marker;
                        foreach (var renderer in prefab.GetComponentsInChildren<SpriteRenderer>(true))
                        {
                            if (renderer.sprite != null) sprites[Key(renderer.sprite)] = renderer.sprite;
                            if (renderer.sharedMaterial != null) materials[Key(renderer.sharedMaterial)] = renderer.sharedMaterial;
                        }
                    }
                }
            }
            // SpriteRenderer's built-in default material is authored engine content. Include it
            // even when no current prefab happens to use it (restored empty markers still may).
            var temporary = new GameObject("Match save material discovery");
            try
            {
                var material = temporary.AddComponent<SpriteRenderer>().sharedMaterial;
                if (material != null) materials[Key(material)] = material;
            }
            finally { UnityEngine.Object.DestroyImmediate(temporary); }
            manifest.Sprites = sprites.Select(p => new MatchSaveSpriteAsset { Key = p.Key, Asset = p.Value }).ToList();
            manifest.Materials = materials.Select(p => new MatchSaveMaterialAsset { Key = p.Key, Asset = p.Value }).ToList();
            manifest.EventCatalogs = eventCatalogs;
            manifest.Prefabs = prefabs.Select(p => new MatchSavePrefabAsset { Key = p.Key, Asset = p.Value }).ToList();
            // Unity dependency hashes include imported art/material/shader dependencies and
            // serialized authoring changes. GUID ordering makes a mere rename immaterial.
            var identities = content.Select(p => AssetDatabase.AssetPathToGUID(p) + ":" + AssetDatabase.GetAssetDependencyHash(p))
                .OrderBy(s => s, StringComparer.Ordinal);
            string input = Application.unityVersion + "\n" + string.Join("\n", identities);
            using (var sha = SHA256.Create())
                manifest.ContentFingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", "").ToLowerInvariant();
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();
        }
        finally { building = false; }
    }
    private static string Key(UnityEngine.Object value)
    {
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId)
            || string.IsNullOrEmpty(guid)) throw new InvalidOperationException("Save content has no authored asset identity: " + value);
        return guid + ":" + localId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
