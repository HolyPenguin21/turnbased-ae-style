#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class GameAssetCatalogMutationTests
    {
        private static readonly FieldInfo Instance = typeof(GameAssetCatalog).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        private GameAssetCatalog _previous;
        private GameAssetCatalog _catalog;
        private Texture2D _oldAsset;
        private Texture2D _newAsset;

        [SetUp]
        public void SetUp()
        {
            _previous = (GameAssetCatalog)Instance.GetValue(null);
            _catalog = ScriptableObject.CreateInstance<GameAssetCatalog>();
            _oldAsset = new Texture2D(1, 1);
            _newAsset = new Texture2D(1, 1);
            Instance.SetValue(null, _catalog);
        }

        [TearDown]
        public void TearDown()
        {
            Instance.SetValue(null, _previous);
            Object.DestroyImmediate(_catalog);
            Object.DestroyImmediate(_oldAsset);
            Object.DestroyImmediate(_newAsset);
        }

        [Test]
        public void ProgrammaticReplacementRefreshesLoadedKeysOnSameInstance()
        {
            Replace(new[] { Entry("replaced", _oldAsset), Entry("removed", _oldAsset) });
            Assert.That(GameAssetCatalog.Load<Texture2D>("replaced"), Is.SameAs(_oldAsset));
            Assert.That(GameAssetCatalog.Load<Texture2D>("removed"), Is.SameAs(_oldAsset));

            Replace(new[] { Entry("replaced", _newAsset), Entry("added", _newAsset) });

            Assert.That(GameAssetCatalog.Load<Texture2D>("replaced"), Is.SameAs(_newAsset));
            Assert.That(GameAssetCatalog.Load<Texture2D>("added"), Is.SameAs(_newAsset));
            Assert.That(GameAssetCatalog.LoadAll<Object>("removed"), Is.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        [TestCase("stable")]
        public void InvalidKeyOrDuplicateRejectsWholeReplacement(string badKey)
        {
            Replace(new[] { Entry("stable", _oldAsset) });
            Assert.That(GameAssetCatalog.Load<Texture2D>("stable"), Is.SameAs(_oldAsset));
            var error = Assert.Throws<ArgumentException>(() => Replace(new[]
            {
                Entry("stable", _newAsset), Entry(badKey, _newAsset)
            }));
            Assert.That(error.Message, Does.Contain("key"));
            Assert.That(GameAssetCatalog.Load<Texture2D>("stable"), Is.SameAs(_oldAsset));
            Assert.That(_catalog.Entries.Select(e => e.key), Is.EqualTo(new[] { "stable" }));
        }

        [Test]
        public void NullOrThrowingInputLeavesLoadedAndSerializedStateIntact()
        {
            Replace(new[] { Entry("stable", _oldAsset) });
            Assert.That(GameAssetCatalog.Load<Texture2D>("stable"), Is.SameAs(_oldAsset));
            Assert.Throws<ArgumentNullException>(() => Replace(null));
            Assert.Throws<InvalidOperationException>(() => Replace(ThrowAfterFirstEntry()));
            Assert.That(GameAssetCatalog.Load<Texture2D>("stable"), Is.SameAs(_oldAsset));
            Assert.That(_catalog.Entries.Select(e => e.key), Is.EqualTo(new[] { "stable" }));
        }

        [Test]
        public void CallerOwnedListAndAssetArrayCannotMutateAcceptedEntries()
        {
            var assets = new Object[] { _oldAsset };
            var values = new List<GameAssetCatalog.Entry> { Entry("stable", assets) };
            Replace(values);
            assets[0] = _newAsset;
            values[0] = Entry("injected", _newAsset);
            values.Clear();
            // Check both before and after lookup creation.
            Assert.That(GameAssetCatalog.Load<Texture2D>("stable"), Is.SameAs(_oldAsset));
            assets[0] = null;
            Assert.That(GameAssetCatalog.Load<Texture2D>("stable"), Is.SameAs(_oldAsset));
            Assert.That(_catalog.Entries.Single().key, Is.EqualTo("stable"));
            Assert.That(_catalog.Entries.Single().assets[0], Is.SameAs(_oldAsset));
        }

        [Test]
        public void EntrySnapshotsProtectTheListAndNestedArraysAndRemainHistorical()
        {
            Replace(new[] { Entry("stable", _oldAsset) });
            var snapshot = _catalog.Entries;
            var list = snapshot as IList<GameAssetCatalog.Entry>;
            if (list != null)
                Assert.Throws<NotSupportedException>(() => list[0] = Entry("injected", _newAsset));
            snapshot[0].assets[0] = _newAsset;
            Assert.That(GameAssetCatalog.Load<Texture2D>("stable"), Is.SameAs(_oldAsset));
            Assert.That(_catalog.Entries[0].assets[0], Is.SameAs(_oldAsset));

            Replace(new[] { Entry("new key", _newAsset) });
            Assert.That(snapshot[0].key, Is.EqualTo("stable"));
            Assert.That(_catalog.Entries[0].key, Is.EqualTo("new key"));
        }

        [Test]
        public void ReplacementPreservesTypedResolutionNullAssetsAndExactKeyIdentity()
        {
            Replace(new[]
            {
                Entry("Key", _catalog, null, _oldAsset, _newAsset),
                Entry("key", _newAsset), Entry("null array", null)
            });
            Assert.That(GameAssetCatalog.LoadAll<Texture2D>("Key"), Is.EqualTo(new[] { _oldAsset, _newAsset }));
            Assert.That(GameAssetCatalog.Load<GameAssetCatalog>("Key"), Is.SameAs(_catalog));
            Assert.That(GameAssetCatalog.Load<Texture2D>("key"), Is.SameAs(_newAsset));
            Assert.That(GameAssetCatalog.LoadAll<Object>("null array"), Is.Empty);
            Assert.That(GameAssetCatalog.LoadAll<Object>(null), Is.Empty);
            var returned = GameAssetCatalog.LoadAll<Texture2D>("Key");
            returned[0] = _newAsset;
            Assert.That(GameAssetCatalog.Load<Texture2D>("Key"), Is.SameAs(_oldAsset));
        }

        [Test]
        public void RepeatedReplacementFromSnapshotRetainsAuthoredKeysAndEmptyInputClearsLookup()
        {
            Replace(new[] { Entry("Effects/Overlay", _oldAsset), Entry("Shaders/ScreenWear", _newAsset) });
            Assert.That(GameAssetCatalog.Load<Texture2D>("Effects/Overlay"), Is.SameAs(_oldAsset));
            Replace(_catalog.Entries);
            Replace(_catalog.Entries);
            Assert.That(_catalog.Entries.Select(e => e.key), Is.EqualTo(new[] { "Effects/Overlay", "Shaders/ScreenWear" }));
            Assert.That(GameAssetCatalog.Load<Texture2D>("Effects/Overlay"), Is.SameAs(_oldAsset));
            Replace(Array.Empty<GameAssetCatalog.Entry>());
            Assert.That(_catalog.Entries, Is.Empty);
            Assert.That(GameAssetCatalog.LoadAll<Object>("Effects/Overlay"), Is.Empty);
        }

        [Test]
        public void AuthoringValidationStillInvalidatesPreviouslyLoadedLookup()
        {
            Replace(new[] { Entry("old key", _oldAsset) });
            Assert.That(GameAssetCatalog.Load<Texture2D>("old key"), Is.SameAs(_oldAsset));
            // Unity's serialized authoring path writes the field directly, then calls OnValidate.
            typeof(GameAssetCatalog).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_catalog, new List<GameAssetCatalog.Entry> { Entry("new key", _newAsset) });
            typeof(GameAssetCatalog).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_catalog, null);
            Assert.That(GameAssetCatalog.LoadAll<Object>("old key"), Is.Empty);
            Assert.That(GameAssetCatalog.Load<Texture2D>("new key"), Is.SameAs(_newAsset));
        }

        private IEnumerable<GameAssetCatalog.Entry> ThrowAfterFirstEntry()
        {
            yield return Entry("partial", _newAsset);
            throw new InvalidOperationException("Input enumeration failed.");
        }

        private void Replace(IEnumerable<GameAssetCatalog.Entry> values) => _catalog.ReplaceEntries(values);

        private static GameAssetCatalog.Entry Entry(string key, params Object[] assets) =>
            new GameAssetCatalog.Entry { key = key, assets = assets };
    }
}
#endif
