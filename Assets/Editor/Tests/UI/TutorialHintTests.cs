#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class TutorialHintPrefabTests
    {
        [Test]
        public void EveryTopicHasAnEditablePrefabWithCompleteReferencesAndNoFullscreenRaycast()
        {
            foreach (TutorialHintId id in Enum.GetValues(typeof(TutorialHintId)))
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Prefabs/TutorialHints/" + id + "Hint.prefab");
                Assert.That(root, Is.Not.Null, id.ToString());
                var hint = root.GetComponent<TutorialHintView>();
                Assert.That(hint, Is.Not.Null);
                var serialized = new SerializedObject(hint);
                foreach (string field in new[] { "titleText", "bodyText", "dismissButton", "bubble", "pointer", "highlight" })
                    Assert.That(serialized.FindProperty(field).objectReferenceValue, Is.Not.Null, id + ": " + field);
                Assert.That(root.GetComponent<Graphic>(), Is.Null, "The overlay must not consume map clicks.");
                Assert.That(root.GetComponentInChildren<Button>(true), Is.Not.Null);
                foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
                {
                    Assert.That(text.text, Is.Not.Empty);
                    Assert.That(text.font, Is.Not.Null);
                    Assert.That(text.raycastTarget, Is.False);
                }
                var body = root.transform.Find("Bubble/Body").GetComponent<TMP_Text>();
                Assert.That(new SerializedObject(body).FindProperty("m_TextWrappingMode").intValue,
                    Is.EqualTo(1), "Long hint text must wrap inside the bubble.");
                foreach (Graphic graphic in root.transform.Find("TargetHighlight").GetComponentsInChildren<Graphic>(true))
                    Assert.That(graphic.raycastTarget, Is.False, "The highlighted control must remain clickable.");
            }
        }

        [Test]
        public void SharedOptionsPrefabWiresTheTutorialCheckbox()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/Panels/AudioOptionsPanel.prefab");
            var serialized = new SerializedObject(prefab.GetComponent<GameSettingsPanelUI>());
            var toggle = serialized.FindProperty("tutorialHintsToggle").objectReferenceValue as Toggle;
            Assert.That(toggle, Is.Not.Null);
            Assert.That(prefab.transform.Find("TutorialHintsLabel").GetComponent<TMP_Text>().text,
                Is.EqualTo("Show tutorial hints"));
        }

        [Test]
        public void CompletedTopicsPersistIndependentlyOfTheEnabledOption()
        {
            const string enabledKey = "Rustmarch.Tutorial.Enabled", seenKey = "Rustmarch.Tutorial.Seen.v1";
            bool hadEnabled = PlayerPrefs.HasKey(enabledKey), hadSeen = PlayerPrefs.HasKey(seenKey);
            int oldEnabled = PlayerPrefs.GetInt(enabledKey), oldSeen = PlayerPrefs.GetInt(seenKey);
            try
            {
                PlayerPrefs.DeleteKey(enabledKey);
                PlayerPrefs.DeleteKey(seenKey);
                Assert.That(TutorialHintProgress.Enabled, Is.True);
                TutorialHintProgress.Complete(TutorialHintId.Fate);
                TutorialHintProgress.Enabled = false;
                Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.Fate), Is.True);
                Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.Draw), Is.False);
                TutorialHintProgress.Enabled = true;
                Assert.That(PlayerPrefs.GetInt(seenKey), Is.EqualTo(1 << (int)TutorialHintId.Fate));
                Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.Fate), Is.True);
            }
            finally
            {
                if (hadEnabled) PlayerPrefs.SetInt(enabledKey, oldEnabled); else PlayerPrefs.DeleteKey(enabledKey);
                if (hadSeen) PlayerPrefs.SetInt(seenKey, oldSeen); else PlayerPrefs.DeleteKey(seenKey);
                PlayerPrefs.Save();
            }
        }
    }

    public sealed class TutorialDeploymentPreviewTests
    {
        private GameObject scene;
        private PlayerRoot root;
        private PlayerSetupData owner;
        private HexSelectionController selector;
        private ArmyData army;
        private CardDefinition definition;

        [SetUp]
        public void SetUp()
        {
            PlayerRootRegistry.Clear();
            BuildingRegistry.Clear();
            ArmyRegistry.Clear();
            owner = new PlayerSetupData();
            root = PlayerRoot.Create(owner, "tutorial preview resources");
            PlayerRootRegistry.Register(owner, root);
            root.ActionPoints = 8;
            root.AddResource(ResourceType.Materials, 8);
            scene = new GameObject("tutorial preview map");
            scene.SetActive(false);
            var map = scene.AddComponent<HexMap>();
            var hex = new HexCoord(0, 0);
            map.SetData(1, 1f, new Dictionary<HexCoord, TerrainTypeEntry> { [hex] = new TerrainTypeEntry() });
            selector = scene.AddComponent<HexSelectionController>();
            typeof(HexSelectionController).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(selector, map);
            var building = new BuildingData { Owner = owner, Hex = hex };
            building.Abilities.Add(UnitAbilities.Barracks);
            BuildingRegistry.Register(hex, building);
            army = new ArmyData { Owner = owner, Hex = hex, IsGarrison = true };
            ArmyRegistry.Register(army);
            definition = new CardDefinition { cardType = CardType.Unit, displayName = "preview unit", apCost = 3,
                activationApCost = 1, requiredBuildingAbility = UnitAbilities.Barracks,
                resourceCost = new ResourceCost { materials = 4 } };
        }
        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear();
            if (scene != null) Object.DestroyImmediate(scene);
            if (root != null) Object.DestroyImmediate(root.gameObject);
        }

        [Test]
        public void RepeatedLegalQueriesDoNotSpendResourcesPublishUnitsOrAllocateLiveIdentities()
        {
            int ap = root.ActionPoints, materials = root.GetResource(ResourceType.Materials);
            int previousIdentity = new UnitData().RuntimeId;
            int notifications = 0;
            Action onChanged = () => notifications++;
            root.ResourcesChanged += onChanged;
            try
            {
                for (int i = 0; i < 20; i++)
                    Assert.That(ArmyActions.CanDeployUnitFromCard(new CardData(definition), owner, army, root, selector), Is.True);
                Assert.That(new UnitData().RuntimeId, Is.EqualTo(previousIdentity + 1));
                Assert.That(root.ActionPoints, Is.EqualTo(ap));
                Assert.That(root.GetResource(ResourceType.Materials), Is.EqualTo(materials));
                Assert.That(notifications, Is.Zero);
                Assert.That(army.Members, Is.Empty);
                Assert.That(ArmyRegistry.AllAt(army.Hex), Has.Count.EqualTo(1));
            }
            finally { root.ResourcesChanged -= onChanged; }
        }

        [Test]
        public void PreviewUsesProducedCardAndRapidReactionCostsInsteadOfPrintedCosts()
        {
            root.ActionPoints = 1;
            root.AddResource(ResourceType.Materials, -root.GetResource(ResourceType.Materials));
            var card = new CardData(definition);
            Assert.That(ArmyActions.CanDeployUnitFromCard(card, owner, army, root, selector), Is.False);
            card.ResearchProductionCreated = true;
            Assert.That(ArmyActions.CanDeployUnitFromCard(card, owner, army, root, selector), Is.True);
            root.ActionPoints = 0;
            definition.grantedAbilities.Add(UnitAbilities.RapidReaction);
            Assert.That(ArmyActions.CanDeployUnitFromCard(card, owner, army, root, selector), Is.True);
            Assert.That(root.ActionPoints, Is.Zero);
        }
    }
}
#endif
