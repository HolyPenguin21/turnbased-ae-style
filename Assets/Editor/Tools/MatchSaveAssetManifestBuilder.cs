using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Cards;
using Game.EditorTools;
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
    // Per-machine stamp of the inputs of the last completed rebuild; Library is not versioned.
    private const string StampPath = "Library/MatchSaveAssetManifest.stamp";
    private static bool building;
    private static bool queued;
    public int callbackOrder => -1000;

    static MatchSaveAssetManifestBuilder()
    {
        Queue();
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode) RebuildIfInputsChanged();
        };
    }
    public void OnPreprocessBuild(BuildReport report) => Rebuild();
    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        if (building) return;
        // Editing UI/presentation prefabs or scenes cannot change save content; play/build still rebuild.
        if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(p => p != Output && p != Output + ".meta"
            && !MatchSaveContentFingerprint.IsPresentationAuthoring(p))) Queue();
    }
    private static void Queue()
    {
        if (queued) return;
        queued = true;
        EditorApplication.delayCall += () => { queued = false; if (!EditorApplication.isPlayingOrWillChangePlaymode) RebuildIfInputsChanged(); };
    }
    // Domain reloads and play mode skip the full asset scan while no content file, importer
    // setting, the manifest itself or the engine version changed since the last rebuild.
    private static void RebuildIfInputsChanged()
    {
        if (building) return;
        string stamp = InputStamp(ContentPaths());
        string previous = null;
        try { if (File.Exists(StampPath)) previous = File.ReadAllText(StampPath); }
        catch (IOException) { }
        if (previous == stamp) return;
        Rebuild();
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
            var requiredWorldDependencies = new HashSet<string>(StringComparer.Ordinal);
            var content = ContentPaths();
            foreach (string path in content)
            {
                // Scene objects cannot be loaded via LoadAllAssetsAtPath; scenes hold no manifest content.
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var asset in LoadCandidates(path))
                {
                    if (asset is Sprite sprite) sprites[Key(sprite)] = sprite;
                    if (asset is Material material) materials[Key(material)] = material;
                    if (asset is EventCatalog events && !eventCatalogs.Contains(events)) eventCatalogs.Add(events);
                    if (asset is GameObject prefab)
                    {
                        var markers = prefab.GetComponentsInChildren<MapObjectVisual>(true);
                        foreach (var marker in markers) prefabs[Key(marker)] = marker;
                        // A sprite/material shared with world markers remains gameplay content,
                        // even when its source happens to live in a presentation directory.
                        if (markers.Length > 0)
                            foreach (string dependency in AssetDatabase.GetDependencies(path, true))
                                requiredWorldDependencies.Add(dependency);
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
            var newSprites = sprites.Select(p => new MatchSaveSpriteAsset { Key = p.Key, Asset = p.Value }).ToList();
            var newMaterials = materials.Select(p => new MatchSaveMaterialAsset { Key = p.Key, Asset = p.Value }).ToList();
            var newPrefabs = prefabs.Select(p => new MatchSavePrefabAsset { Key = p.Key, Asset = p.Value }).ToList();
            string fingerprint = MatchSaveContentFingerprint.Compute(Directory.GetCurrentDirectory(), Application.unityVersion,
                content.Select(p => new KeyValuePair<string, string>(AssetDatabase.AssetPathToGUID(p), p)), requiredWorldDependencies);
            // Rewriting an identical manifest would re-import it and churn the working tree.
            if (manifest.ContentFingerprint == fingerprint
                && manifest.Sprites.Select(a => (a.Key, a.Asset)).SequenceEqual(newSprites.Select(a => (a.Key, a.Asset)))
                && manifest.Materials.Select(a => (a.Key, a.Asset)).SequenceEqual(newMaterials.Select(a => (a.Key, a.Asset)))
                && manifest.Prefabs.Select(a => (a.Key, a.Asset)).SequenceEqual(newPrefabs.Select(a => (a.Key, a.Asset)))
                && manifest.EventCatalogs.SequenceEqual(eventCatalogs)) { WriteStamp(content); return; }
            manifest.Sprites = newSprites;
            manifest.Materials = newMaterials;
            manifest.EventCatalogs = eventCatalogs;
            manifest.Prefabs = newPrefabs;
            manifest.ContentFingerprint = fingerprint;
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();
            WriteStamp(content);
        }
        finally { building = false; }
    }
    private static string[] ContentPaths() => AssetDatabase.GetAllAssetPaths()
        .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && !AssetDatabase.IsValidFolder(p)
            && p != Output && !p.StartsWith("Assets/Editor/", StringComparison.Ordinal)
            && !p.StartsWith("Assets/Scripts/", StringComparison.Ordinal))
        .OrderBy(p => p, StringComparer.Ordinal).ToArray();
    // Only sprites, materials, event catalogs and prefabs enter the manifest. Audio and text never
    // hold them, and a texture holds sprites only when imported as a Sprite; its
    // sub-assets are enough, so the texture itself is not loaded.
    private static UnityEngine.Object[] LoadCandidates(string path)
    {
        var type = AssetDatabase.GetMainAssetTypeAtPath(path);
        if (type == null) return AssetDatabase.LoadAllAssetsAtPath(path);
        if (typeof(Texture).IsAssignableFrom(type))
            return AssetImporter.GetAtPath(path) is TextureImporter texture && texture.textureType != TextureImporterType.Sprite
                ? Array.Empty<UnityEngine.Object>() : AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
        // Shaders stay: a Shader Graph asset carries its own Material sub-asset.
        if (typeof(AudioClip).IsAssignableFrom(type) || typeof(TextAsset).IsAssignableFrom(type))
            return Array.Empty<UnityEngine.Object>();
        return AssetDatabase.LoadAllAssetsAtPath(path);
    }
    // Size and write time of every content file and importer setting, plus the manifest file and
    // the engine version. A git checkout or an editor import changes the write time. Scenes are
    // listed without their contents: the rebuild never reads them.
    private static string InputStamp(string[] content)
    {
        var text = new System.Text.StringBuilder(Application.unityVersion).Append('\n');
        foreach (string path in content.Append(Output))
        {
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) { text.Append(path).Append('\n'); continue; }
            text.Append(path).Append('|').Append(AssetDatabase.AssetPathToGUID(path));
            AppendFile(text, path);
            AppendFile(text, path + ".meta");
            text.Append('\n');
        }
        using (var hash = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }
    private static void AppendFile(System.Text.StringBuilder text, string path)
    {
        var file = new FileInfo(path);
        text.Append('|');
        if (file.Exists) text.Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks);
    }
    private static void WriteStamp(string[] content)
    {
        try { File.WriteAllText(StampPath, InputStamp(content)); }
        catch (IOException exception) { Debug.LogWarning("Match save manifest stamp not written: " + exception.Message); }
    }
    private static string Key(UnityEngine.Object value)
    {
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId)
            || string.IsNullOrEmpty(guid)) throw new InvalidOperationException("Save content has no authored asset identity: " + value);
        return guid + ":" + localId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
