#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Saves;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class MatchSaveSlotsUiTests
    {
        private GameObject root;
        private GameConfig config;
        private string directory;
        private MatchSaveStore store;
        private MatchSaveSlotsUI window;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
        private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);

        // Edit Mode never calls OnEnable/OnDisable of a runtime component; the window wires its
        // buttons and releases its overlay there, so drive them exactly when Play Mode would.
        private bool Open()
        {
            bool shown = window.Show(config, null, false);
            if (shown) Call(window, "OnEnable");
            return shown;
        }
        private void Shut()
        {
            bool active = window.gameObject.activeSelf;
            window.Close();
            if (active && !window.gameObject.activeSelf) Call(window, "OnDisable");
        }
        [SetUp] public void SetUp()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/Panels/MatchSaveSlots.prefab");
            Assert.That(prefab, Is.Not.Null, "Save/Load must be an authored prefab.");
            root = Object.Instantiate(prefab); root.SetActive(false);
            window = root.GetComponent<MatchSaveSlotsUI>();
            config = ScriptableObject.CreateInstance<GameConfig>();
            directory = Path.Combine(Path.GetTempPath(), "slots-ui-" + Guid.NewGuid().ToString("N"));
            store = new MatchSaveStore(directory);
            typeof(MatchSaveSlotsUI).GetField("store", Private).SetValue(window, store);
        }
        [TearDown] public void TearDown()
        {
            if (window != null && window.IsShowing) Shut(); // releases the static UI overlay
            if (root != null) Object.DestroyImmediate(root);
            if (config != null) Object.DestroyImmediate(config);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        [Test] public void ClosingAndReopeningReusesAuthoredRowsAndResetsSelection()
        {
            var rows = Field<Button[]>(window, "rowButtons");
            Assert.That(rows.Length, Is.EqualTo(6));
            int count = root.GetComponentsInChildren<Button>(true).Length;
            for (int i = 0; i < 3; i++)
            {
                Assert.That(Open(), Is.True);
                Assert.That(UIFocusUtility.HasOverlay, Is.True);
                Shut();
                Assert.That(window.IsShowing, Is.False);
                Assert.That(UIFocusUtility.HasOverlay, Is.False);
                Assert.That(root.GetComponentsInChildren<Button>(true).Length, Is.EqualTo(count));
                Assert.That(Field<int>(window, "selected"), Is.EqualTo(-1));
            }
        }
        [Test] public void CancelDeletePreservesCorruptBackupAndConfirmRemovesItOnceAfterReopen()
        {
            Directory.CreateDirectory(directory);
            string backup = Path.Combine(directory, "slot-9.json.bak");
            File.WriteAllText(backup, "broken");
            int changes = 0; window.SlotsChanged += () => changes++;
            for (int i = 0; i < 3; i++) { Open(); Shut(); }
            Open(); Call(window, "Select", 9);
            Field<Button>(window, "deleteButton").onClick.Invoke();
            Assert.That(Field<GameObject>(window, "deleteConfirmation").activeSelf, Is.True);
            Assert.That(Field<CanvasGroup>(window, "contentGroup").interactable, Is.False);
            Field<Button>(window, "cancelDeleteButton").onClick.Invoke();
            Assert.That(Field<CanvasGroup>(window, "contentGroup").interactable, Is.True);
            Assert.That(File.Exists(backup), Is.True); Assert.That(changes, Is.Zero);
            Field<Button>(window, "deleteButton").onClick.Invoke();
            Field<Button>(window, "confirmDeleteButton").onClick.Invoke();
            Assert.That(store.Exists(9), Is.False); Assert.That(changes, Is.EqualTo(1));
            Assert.That(Field<int>(window, "selected"), Is.EqualTo(-1));
            Assert.That(Field<Button>(window, "action").interactable, Is.False);
        }
        [Test] public void DeletingOneNewPositionKeepsOtherUnsavedPositions()
        {
            Assert.That(Open(), Is.True);
            // Exercise slot management in save mode without preparing/capturing a live match.
            typeof(MatchSaveSlotsUI).GetField("saving", Private).SetValue(window, true);
            Call(window, "ReloadSlots");
            for (int i = 0; i < 3; i++) Call(window, "AddSlot");
            Call(window, "Select", 7);
            Field<Button>(window, "deleteButton").onClick.Invoke();
            Field<Button>(window, "confirmDeleteButton").onClick.Invoke();
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 8 }, Field<List<int>>(window, "slots"));
            Shut();
            Open();
            Assert.That(Field<List<int>>(window, "slots"), Is.Empty, "Unsaved positions belong only to the open save window.");
        }
        [TestCase(false, false, 0)]
        [TestCase(false, true, 1)]
        [TestCase(true, false, 2)]
        [TestCase(true, true, 2)]
        public void ContinueAlwaysPrefersMatchListOverCampaign(bool matches, bool campaign, int expected)
        {
            var method = typeof(MainMenuController).GetMethod("ContinueAction", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Assert.That((int)method.Invoke(null, new object[] { matches, campaign }), Is.EqualTo(expected));
        }
    }
}
#endif
