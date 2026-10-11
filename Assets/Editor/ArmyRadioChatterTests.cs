#if UNITY_INCLUDE_TESTS
using System;
using System.Reflection;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public class ArmyRadioLayoutTests
    {
        [TestCase(400f, 0f, 1, 0)]
        [TestCase(-400f, 0f, -1, 0)]
        [TestCase(0f, 400f, 0, 1)]
        [TestCase(0f, -400f, 0, -1)]
        [Category("ArmyRadioManaged")]
        public void Pointer_CanLeaveEverySide(float x, float y, int sideX, int sideY)
        {
            var rect = new Rect(-110f, -32f, 220f, 64f);
            var target = new Vector2(x, y);
            Assert.That(ArmyRadioLayout.Pointer(rect, target, 9f, 14f, out var a, out var b, out var tip), Is.True);
            Vector2 baseCentre = (a + b) * .5f;
            if (sideX != 0) Assert.That(baseCentre.x, Is.EqualTo(sideX * 110f).Within(.001f));
            else Assert.That(baseCentre.y, Is.EqualTo(sideY * 32f).Within(.001f));
            Assert.That(Vector2.Distance(target, tip), Is.EqualTo(14f).Within(.001f));
            Assert.That(Vector2.Distance(a, b), Is.EqualTo(8f).Within(.001f));
        }

        [TestCase(400f, 150f)]
        [TestCase(-400f, 150f)]
        [TestCase(400f, -150f)]
        [TestCase(-400f, -150f)]
        [Category("ArmyRadioManaged")]
        public void Pointer_DiagonalsStayAwayFromRoundedCorners(float x, float y)
        {
            var rect = new Rect(-110f, -32f, 220f, 64f);
            Assert.That(ArmyRadioLayout.Pointer(rect, new Vector2(x, y), 9f, 10f, out var a, out var b, out _), Is.True);
            Vector2 centre = (a + b) * .5f;
            bool side = Mathf.Abs(centre.x) == 110f;
            Assert.That(side ? Mathf.Abs(centre.y) : Mathf.Abs(centre.x), Is.LessThanOrEqualTo(side ? 19f : 97f));
        }

        [TestCase(1280, 720)]
        [TestCase(1920, 1080)]
        [TestCase(2560, 1440)]
        [TestCase(1024, 576)]
        [Category("ArmyRadioManaged")]
        public void Placement_ClampsAllCornersAndKeepsCardOnTheArmySide(int width, int height)
        {
            // Screen/canvas scale is handled by RectTransformUtility in the presenter.
            var safe = new Rect(-width * .5f + 12f, -height * .5f + 170f, width - 24f, height - 182f);
            var size = new Vector2(278f, 78f);
            foreach (float x in new[] { safe.xMin + 10f, 0f, safe.xMax - 10f })
                foreach (float y in new[] { safe.yMin + 10f, safe.center.y, safe.yMax - 10f })
                {
                    Vector2 position = ArmyRadioLayout.Place(safe, new Vector2(x, y), size, 24f, out bool left);
                    Rect placed = new Rect(position - size * .5f, size);
                    Assert.That(placed.xMin, Is.GreaterThanOrEqualTo(safe.xMin - .01f));
                    Assert.That(placed.xMax, Is.LessThanOrEqualTo(safe.xMax + .01f));
                    Assert.That(placed.yMin, Is.GreaterThanOrEqualTo(safe.yMin - .01f));
                    Assert.That(placed.yMax, Is.LessThanOrEqualTo(safe.yMax + .01f));
                    Assert.That(left, Is.EqualTo(position.x > x));
                    Assert.That(placed.Contains(new Vector2(x, y)), Is.False);
                }
        }

        [Test, Category("ArmyRadioManaged")]
        public void Pointer_DoesNotCrossAContainedMarker()
        {
            Assert.That(ArmyRadioLayout.Pointer(new Rect(-110f, -32f, 220f, 64f), Vector2.zero,
                9f, 12f, out _, out _, out _), Is.False);
        }

        [Test]
        public void Art_KeepsTheRealFullImageAspectWithoutCropping()
        {
            var texture = new Texture2D(772, 1124);
            var art = Sprite.Create(texture, new Rect(0, 0, 772, 1124), new Vector2(.5f, .5f));
            try
            {
                Vector2 size = ArmyRadioLayout.ArtSize(art, 78f);
                Assert.That(size.x / size.y, Is.EqualTo(772f / 1124f).Within(.00001f));
                Assert.That(size.y, Is.EqualTo(78f));
                var unit = new UnitData { Art = art, DetailArt = null };
                Assert.That(ArmyRadioChatter.DetailArt(unit), Is.Null, "Do not silently use the compact stat-slot art.");
                unit.OriginatingCard = new CardDefinition { detailArt = art };
                Assert.That(ArmyRadioChatter.DetailArt(unit), Is.SameAs(art));
            }
            finally { Object.DestroyImmediate(art); Object.DestroyImmediate(texture); }
        }

        [TestCase(0f, -10f)]
        [TestCase(-10f, 0f)]
        [TestCase(10f, 0f)]
        [TestCase(0f, 10f)]
        public void MarkerAnchor_FollowsTheActualCameraFromEveryDirection(float x, float z)
        {
            var cameraObject = new GameObject("Radio camera");
            var marker = new GameObject("Radio marker");
            var texture = new Texture2D(16, 16);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 16, 16), new Vector2(.5f, .5f), 16f);
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true; camera.orthographicSize = 5f;
                camera.pixelRect = new Rect(0f, 0f, 1920f, 1080f);
                camera.transform.position = new Vector3(x, 10f, z); camera.transform.LookAt(Vector3.zero);
                SpriteRenderer renderer = marker.AddComponent<SpriteRenderer>(); renderer.sprite = sprite;
                marker.transform.position = new Vector3(1f, 0f, 1f);
                MapObjectVisual visual = marker.AddComponent<MapObjectVisual>();
                typeof(MapObjectVisual).GetField("innerCircle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(visual, renderer);
                Assert.That(visual.TryGetScreenAnchor(camera, out var anchor, out float radius), Is.True);
                Vector3 expected = camera.WorldToScreenPoint(renderer.bounds.center);
                Assert.That(anchor.x, Is.EqualTo(expected.x).Within(.01f));
                Assert.That(anchor.y, Is.EqualTo(expected.y).Within(.01f));
                camera.orthographicSize = 10f;
                visual.TryGetScreenAnchor(camera, out _, out float zoomRadius);
                Assert.That(zoomRadius, Is.EqualTo(radius * .5f).Within(.01f));
                visual.SetVisible(false);
                Assert.That(visual.TryGetScreenAnchor(camera, out _, out _), Is.False);
            }
            finally { Object.DestroyImmediate(marker); Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(sprite); Object.DestroyImmediate(texture); }
        }
    }

    public class ArmyRadioChatterTests
    {
        private GameConfig _config;
        private PlayerSetupData _owner, _enemyOwner;
        private ArmyData _army;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); StealthSystem.Clear();
            _config = ScriptableObject.CreateInstance<GameConfig>(); _config.armyVisionRadius = 1;
            VisionSystem.Configure(_config);
            _owner = new PlayerSetupData(); _enemyOwner = new PlayerSetupData();
            _army = Army(_owner, new HexCoord(0, 0)); ArmyRegistry.Register(_army);
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); StealthSystem.Clear();
            VisionSystem.Configure(null); Object.DestroyImmediate(_config);
        }

        private static ArmyData Army(PlayerSetupData owner, HexCoord hex)
        {
            var army = new ArmyData { Owner = owner, Hex = hex };
            army.Members.Add(new UnitData { Owner = owner }); return army;
        }

        [Test]
        public void Contact_UsesOwnArmySightAndNeverFogMemoryOrDebugReveal()
        {
            var enemy = Army(_enemyOwner, new HexCoord(3, 0)); ArmyRegistry.Register(enemy);
            var distantScout = Army(_owner, new HexCoord(3, -1)); ArmyRegistry.Register(distantScout);
            Assert.That(VisionSystem.IsVisible(_owner, enemy.Hex), Is.True, "Another army sees it.");
            Assert.That(ArmyRadioChatter.SeesEnemy(_army), Is.False, "This army cannot claim the other scout's contact.");
            ArmyRegistry.MoveArmy(enemy, new HexCoord(1, 0));
            Assert.That(ArmyRadioChatter.SeesEnemy(_army), Is.True);
            ArmyRegistry.MoveArmy(enemy, new HexCoord(5, 0));
            VisionSystem.DebugRevealAll = true;
            try { Assert.That(ArmyRadioChatter.SeesEnemy(_army), Is.False); }
            finally { VisionSystem.DebugRevealAll = false; }
        }

        [Test]
        public void Contact_DoesNotRevealUndetectedStealth()
        {
            var enemy = Army(_enemyOwner, new HexCoord(1, 0)); enemy.Members[0].IsHidden = true;
            ArmyRegistry.Register(enemy);
            Assert.That(ArmyRadioChatter.SeesEnemy(_army), Is.False);
            enemy.Members[0].IsHidden = false;
            Assert.That(ArmyRadioChatter.SeesEnemy(_army), Is.True);
        }

        [Test]
        public void Speaker_IsFirstLivingRosterUnitAndOnlyTheViewersOwnArmy()
        {
            UnitData first = _army.Members[0]; var second = new UnitData { Owner = _owner };
            _army.Members.Add(second);
            Assert.That(ArmyRadioChatter.FirstUnit(_army), Is.SameAs(first));
            Assert.That(ArmyRadioChatter.CanSpeak(_army, _enemyOwner), Is.False);
            Assert.That(ArmyRadioChatter.CanSpeak(_army, _owner), Is.True);
            _army.Members.Reverse(); Assert.That(ArmyRadioChatter.FirstUnit(_army), Is.SameAs(second));
            second.IsPrisoner = true; Assert.That(ArmyRadioChatter.FirstUnit(_army), Is.SameAs(first));
            first.HitPointsCurrent = 0; Assert.That(ArmyRadioChatter.CanSpeak(_army, _owner), Is.False);
        }

        [Test]
        public void Waiting_DoesNotUseActivationAsEvidenceOfNotMoving()
        {
            Assert.That(ArmyRadioChatter.Situation(_army, false, false, 1f), Is.EqualTo(ArmyRadioSituation.Waiting));
            Assert.That(ArmyRadioChatter.Situation(_army, false, true, 1f), Is.EqualTo(ArmyRadioSituation.Moving));
            _army.MarkActivated();
            Assert.That(ArmyRadioChatter.Situation(_army, false, false, 1f), Is.EqualTo(ArmyRadioSituation.Moving));
            Assert.That(ArmyRadioChatter.Situation(_army, true, true, 1f), Is.EqualTo(ArmyRadioSituation.Contact));
        }

        [Test]
        public void Situations_UseExistingGarrisonAirDamageAndStealthFlags()
        {
            _army.IsGarrison = true;
            Assert.That(ArmyRadioChatter.Situation(_army, false, false, 1f), Is.EqualTo(ArmyRadioSituation.Garrison));
            _army.IsGarrison = false; _army.IsAirArmy = true;
            Assert.That(ArmyRadioChatter.Situation(_army, false, false, 1f), Is.EqualTo(ArmyRadioSituation.Airborne));
            _army.Members[0].IsHidden = true;
            Assert.That(ArmyRadioChatter.Situation(_army, false, false, 1f), Is.EqualTo(ArmyRadioSituation.Stealth));
            _army.Members[0].HitPointsMax = 3;
            Assert.That(ArmyRadioChatter.Situation(_army, false, false, 1f), Is.EqualTo(ArmyRadioSituation.Damaged));
        }

        [Test]
        public void Radio_DoesNotConsumeGameplayRandomOrChangeArmyState()
        {
            UnityEngine.Random.State before = UnityEngine.Random.state;
            var situation = ArmyRadioChatter.Situation(_army, false, false, 1f);
            ArmyRadioChatter.Phrase(situation, ArmyRadioLanguage.English, 2);
            Assert.That(UnityEngine.Random.state, Is.EqualTo(before));
            Assert.That(_army.HasActivatedThisTurn, Is.False);
            Assert.That(_army.Members.Count, Is.EqualTo(1));
        }
    }

    public class ArmyRadioPhraseTests
    {
        [Test, Category("ArmyRadioManaged")]
        public void Phrases_CoverEverySituationAndLanguageAndWrapWithoutImmediateRepeats()
        {
            foreach (ArmyRadioSituation situation in Enum.GetValues(typeof(ArmyRadioSituation)))
                foreach (ArmyRadioLanguage language in Enum.GetValues(typeof(ArmyRadioLanguage)))
                {
                    string first = ArmyRadioChatter.Phrase(situation, language, 0);
                    Assert.That(first, Is.Not.Empty);
                    Assert.That(ArmyRadioChatter.Phrase(situation, language, 1), Is.Not.EqualTo(first));
                    Assert.That(ArmyRadioChatter.Phrase(situation, language, 5), Is.EqualTo(first));
                    Assert.That(ArmyRadioChatter.Phrase(situation, language, int.MaxValue), Is.Not.Empty);
                }
        }
    }

    public class ArmyRadioLifecycleTests
    {
        [Test]
        public void ConfigureTwice_UsesOneOverlayAndDisableUnsubscribesWithoutBlockingMapInput()
        {
            var turnsObject = new GameObject("Inactive radio turn host"); turnsObject.SetActive(false);
            var radioObject = new GameObject("Radio presenter");
            try
            {
                var turns = turnsObject.AddComponent<Game.Turns.GameTurnController>();
                var radio = radioObject.AddComponent<ArmyRadioChatterController>();
                radio.Configure(turns, null, null, new ArmyRadioChatterSettings());
                radio.Configure(turns, null, null, new ArmyRadioChatterSettings());
                Assert.That(radio.GetComponentsInChildren<Canvas>(true).Length, Is.EqualTo(1));
                var fade = radio.GetComponentInChildren<CanvasGroup>(true);
                Assert.That(fade.blocksRaycasts, Is.False); Assert.That(fade.interactable, Is.False);
                foreach (var graphic in radio.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                    Assert.That(graphic.raycastTarget, Is.False);
                var image = radio.GetComponentInChildren<UnityEngine.UI.Image>(true);
                Assert.That(image.preserveAspect, Is.True);
                var scaler = radio.GetComponentInChildren<UnityEngine.UI.CanvasScaler>(true);
                Assert.That(scaler.referenceResolution, Is.EqualTo(new Vector2(1280f, 720f)));
                var eventField = typeof(VisionSystem).GetField("VisibilityChanged", BindingFlags.Static | BindingFlags.NonPublic);
                var listeners = (Delegate)eventField.GetValue(null);
                Assert.That(Array.Exists(listeners.GetInvocationList(), d => ReferenceEquals(d.Target, radio)), Is.True);
                radio.enabled = false;
                listeners = (Delegate)eventField.GetValue(null);
                Assert.That(listeners == null || !Array.Exists(listeners.GetInvocationList(), d => ReferenceEquals(d.Target, radio)), Is.True);
                Assert.That(fade.gameObject.activeSelf, Is.False);
            }
            finally { Object.DestroyImmediate(radioObject); Object.DestroyImmediate(turnsObject); }
        }
    }
}
#endif
