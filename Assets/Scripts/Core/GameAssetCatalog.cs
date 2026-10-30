using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// Runtime lookup for assets that code loads by a string key (formerly Resources.Load paths).
    /// The assets live in ordinary project folders; this catalog (the only asset in Resources) holds
    /// direct references to them. Regenerate it via Tools/Rebuild Game Asset Catalog.
    /// </summary>
    public sealed class GameAssetCatalog : ScriptableObject
    {
        public const string ResourceName = "GameAssetCatalog";

        [Serializable]
        public struct Entry
        {
            public string key;
            public UnityEngine.Object[] assets;
        }

        [SerializeField] private List<Entry> entries = new List<Entry>();

        private static GameAssetCatalog _instance;
        private Dictionary<string, UnityEngine.Object[]> _lookup;

        // Copy the nested arrays as well: Entry is a value type, but its assets are mutable.
        // Object references themselves keep their authored identity and resolution order.
        public IReadOnlyList<Entry> Entries => Array.AsReadOnly(CopyEntries(entries).ToArray());

        public void ReplaceEntries(IEnumerable<Entry> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var replacement = new List<Entry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in values)
            {
                if (string.IsNullOrWhiteSpace(entry.key))
                    throw new ArgumentException("GameAssetCatalog entry key must not be empty or whitespace.", nameof(values));
                if (!keys.Add(entry.key))
                    throw new ArgumentException($"GameAssetCatalog duplicate entry key '{entry.key}'.", nameof(values));
                replacement.Add(CopyEntry(entry));
            }
            // A failed validation or enumeration leaves both the authored data and lookup intact.
            entries = replacement;
            _lookup = null;
        }

        private static List<Entry> CopyEntries(IEnumerable<Entry> values)
        {
            var copy = new List<Entry>();
            foreach (var entry in values) copy.Add(CopyEntry(entry));
            return copy;
        }

        private static Entry CopyEntry(Entry entry) => new Entry
        {
            key = entry.key,
            assets = entry.assets == null ? Array.Empty<UnityEngine.Object>() : (UnityEngine.Object[])entry.assets.Clone()
        };

        private static GameAssetCatalog Instance
        {
            get
            {
                if (_instance == null) _instance = Resources.Load<GameAssetCatalog>(ResourceName);
                return _instance;
            }
        }

        public static T Load<T>(string key) where T : UnityEngine.Object
        {
            var all = LoadAll<T>(key);
            return all.Length > 0 ? all[0] : null;
        }

        public static T[] LoadAll<T>(string key) where T : UnityEngine.Object
        {
            var catalog = Instance;
            if (catalog == null)
            {
                Debug.LogError("GameAssetCatalog is missing from Resources. Run Tools/Rebuild Game Asset Catalog.");
                return Array.Empty<T>();
            }
            return catalog.Find<T>(key);
        }

        private T[] Find<T>(string key) where T : UnityEngine.Object
        {
            if (_lookup == null)
            {
                _lookup = new Dictionary<string, UnityEngine.Object[]>(entries.Count);
                foreach (var entry in entries)
                    if (!string.IsNullOrEmpty(entry.key)) _lookup[entry.key] = entry.assets ?? Array.Empty<UnityEngine.Object>();
            }
            var result = new List<T>();
            if (key != null && _lookup.TryGetValue(key, out var assets))
                foreach (var asset in assets)
                    if (asset is T typed) result.Add(typed);
            return result.ToArray();
        }

        private void OnValidate() => _lookup = null;
    }
}
