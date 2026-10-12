#if UNITY_INCLUDE_TESTS
using System;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class CardActionHoverTextTests
    {
        private GameObject _card;
        private GameObject _title;
        private TMP_Text _skills;
        private object _visibility;
        private Type _type;

        [SetUp]
        public void SetUp()
        {
            _card = new GameObject("Card");
            _title = new GameObject("Title");
            _title.transform.SetParent(_card.transform);
            var label = new GameObject("Skills", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(_card.transform);
            _skills = label.GetComponent<TMP_Text>();
            _type = typeof(ArmyUnitCardUI).Assembly.GetType("Game.UI.CardActionHoverText", true);
            // The real Army prefab binds moveText and skillsText to the same label.
            _visibility = Activator.CreateInstance(_type, new object[] { _title, new[] { _skills, _skills, null } });
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_card);

        private void Hide(bool hidden) => _type.GetMethod("SetHidden").Invoke(_visibility, new object[] { hidden });
        private void Restore() => _type.GetMethod("Restore").Invoke(_visibility, null);

        [Test]
        public void VisibleActionsHideTitleAndSkillsAndExitRestoresAliases()
        {
            Hide(true);
            Assert.That(_title.activeSelf, Is.False);
            Assert.That(_skills.enabled, Is.False);
            Hide(true); // A repeated enter must not overwrite the saved visible state.
            Restore();
            Assert.That(_title.activeSelf, Is.True);
            Assert.That(_skills.enabled, Is.True);
            Restore();
            Assert.That(_title.activeSelf, Is.True);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public void RestorePreservesOriginallyHiddenElements(bool titleActive, bool skillsEnabled)
        {
            _title.SetActive(titleActive);
            _skills.enabled = skillsEnabled;
            Hide(true);
            Hide(false);
            Assert.That(_title.activeSelf, Is.EqualTo(titleActive));
            Assert.That(_skills.enabled, Is.EqualTo(skillsEnabled));
        }

        [Test]
        public void HoverWithoutActionsLeavesTextVisible()
        {
            Hide(false);
            Assert.That(_title.activeSelf, Is.True);
            Assert.That(_skills.enabled, Is.True);
        }

        [Test]
        public void EquipmentPreviewCannotRedisplaySkillsOverActionButtons()
        {
            _skills.gameObject.SetActive(false);
            Hide(true);
            // EquipmentArtToggle enables this GameObject to show its preview text.
            _skills.gameObject.SetActive(true);
            Assert.That(_skills.enabled, Is.False);
            Restore();
            Assert.That(_skills.enabled, Is.True);
        }
    }
}
#endif
