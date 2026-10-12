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
    public class ArmyRadioArtTests
    {
        [Test]
        public void ActualPrefab_UsesAuthoredSpriteBackingWithoutStackedShadows()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/ArmyRadio/ArmyRadioMessage.prefab");
            Assert.That(prefab, Is.Not.Null);
            var host = Object.Instantiate(prefab);
            try
            {
                var view = host.GetComponent<ArmyRadioBubbleView>();
                var bubble = (ArmyRadioBubbleGraphic)typeof(ArmyRadioBubbleView)
                    .GetField("_bubble", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
                var shadow = (RectTransform)typeof(ArmyRadioBubbleView)
                    .GetField("_shadow", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
                Assert.That(shadow.gameObject.activeSelf, Is.False);
                var backing = bubble.transform.Find("Backing");
                Assert.That(backing, Is.Not.Null, "The agreed backing must be a real sprite in the prefab.");
                var image = backing.GetComponent<UnityEngine.UI.Image>();
                Assert.That(image, Is.Not.Null);
                Assert.That(image.sprite, Is.Not.Null);
                Assert.That(UnityEditor.AssetDatabase.GetAssetPath(image.sprite),
                    Is.EqualTo("Assets/Textures/UI/ArmyRadio_Background.png"));
                Assert.That(image.type, Is.EqualTo(UnityEngine.UI.Image.Type.Sliced));
                Assert.That(image.color.a, Is.InRange(.45f, .6f));
                Assert.That(image.raycastTarget, Is.False);
                Assert.That(backing.GetSiblingIndex(), Is.Zero, "Draw the backing beneath the text.");
                var rect = (RectTransform)backing;
                Assert.That(rect.anchorMin, Is.EqualTo(Vector2.zero));
                Assert.That(rect.anchorMax, Is.EqualTo(Vector2.one));
                Assert.That(rect.offsetMin, Is.EqualTo(Vector2.zero));
                Assert.That(rect.offsetMax, Is.EqualTo(Vector2.zero));
            }
            finally { Object.DestroyImmediate(host); }
        }

        [TestCase(0f, 39f)]
        [TestCase(54f, 39f)]
        [TestCase(27f, 0f)]
        [TestCase(27f, 78f)]
        [Category("ArmyRadioArtManaged")]
        public void ArtAlpha_AllOuterEdgesDisappear(float x, float y)
        {
            Assert.That(ArmyRadioArtFeather.AlphaAt(new Vector2(x, y), new Vector2(54f, 78f), 6f, 5f, 9f), Is.EqualTo(0f));
        }

        [Test, Category("ArmyRadioArtManaged")]
        public void ArtAlpha_CentreStaysClearAndEdgeIsSoftIrregularAndStable()
        {
            var size = new Vector2(54f, 78f);
            Assert.That(ArmyRadioArtFeather.AlphaAt(size * .5f, size, 6f, 5f, 9f), Is.EqualTo(1f));
            float minimum = 1f, maximum = 0f;
            for (int i = 12; i <= 66; i++)
            {
                var point = new Vector2(5f, i);
                float alpha = ArmyRadioArtFeather.AlphaAt(point, size, 6f, 5f, 9f);
                Assert.That(alpha, Is.InRange(0f, 1f));
                Assert.That(ArmyRadioArtFeather.AlphaAt(point, size, 6f, 5f, 9f), Is.EqualTo(alpha));
                minimum = Mathf.Min(minimum, alpha); maximum = Mathf.Max(maximum, alpha);
            }
            Assert.That(maximum - minimum, Is.GreaterThan(.25f), "Edge must be visibly irregular, not a straight uniform ramp.");
            Assert.That(maximum, Is.GreaterThan(0f).And.LessThan(1f), "Retain a soft partial-alpha transition.");
            Assert.That(ArmyRadioArtFeather.AlphaAt(Vector2.zero, size, 0f, 0f, 9f), Is.EqualTo(1f));
        }

        [Test]
        public void ArtMesh_PreservesAtlasUVsTintAndBoundsWithTransparentEdges()
        {
            var host = new GameObject("Radio feather mesh", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(ArmyRadioArtFeather));
            try
            {
                using (var mesh = new UnityEngine.UI.VertexHelper())
                {
                    var colour = new Color32(220, 210, 200, 214);
                    mesh.AddVert(new Vector3(0f, 0f), colour, new Vector2(.2f, .3f));
                    mesh.AddVert(new Vector3(0f, 78f), colour, new Vector2(.2f, .9f));
                    mesh.AddVert(new Vector3(54f, 78f), colour, new Vector2(.8f, .9f));
                    mesh.AddVert(new Vector3(54f, 0f), colour, new Vector2(.8f, .3f));
                    mesh.AddTriangle(0, 1, 2); mesh.AddTriangle(2, 3, 0);
                    host.GetComponent<ArmyRadioArtFeather>().ModifyMesh(mesh);
                    Assert.That(mesh.currentVertCount, Is.GreaterThan(4).And.LessThanOrEqualTo(6305));
                    var vertex = new UnityEngine.UIVertex(); bool foundCentre = false;
                    for (int i = 0; i < mesh.currentVertCount; i++)
                    {
                        mesh.PopulateUIVertex(ref vertex, i);
                        Assert.That(vertex.color.r, Is.EqualTo(220));
                        Assert.That(vertex.color.a, Is.LessThanOrEqualTo(214));
                        Assert.That(vertex.uv0.x, Is.EqualTo(.2f + vertex.position.x / 54f * .6f).Within(.00001f));
                        Assert.That(vertex.uv0.y, Is.EqualTo(.3f + vertex.position.y / 78f * .6f).Within(.00001f));
                        if (vertex.position.x == 0f || vertex.position.x == 54f || vertex.position.y == 0f || vertex.position.y == 78f)
                            Assert.That(vertex.color.a, Is.EqualTo(0));
                        if (Mathf.Abs(vertex.position.x - 27f) < 2f && Mathf.Abs(vertex.position.y - 39f) < 2f)
                        { foundCentre = true; Assert.That(vertex.color.a, Is.EqualTo(214)); }
                    }
                    Assert.That(foundCentre, Is.True);
                }
            }
            finally { Object.DestroyImmediate(host); }
        }
    }

    public class ArmyRadioLayoutTests
    {
        [TestCase(2.6f)]
        [TestCase(4f)]
        [TestCase(7f)]
        public void ActualOverlay_DoublesTheCompleteTransmissionAtEveryZoom(float orthographicSize)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/ArmyRadio/ArmyRadioOverlay.prefab");
            var host = Object.Instantiate(prefab);
            var cameraHost = new GameObject("Radio scale camera", typeof(Camera));
            try
            {
                var camera = cameraHost.GetComponent<Camera>();
                camera.orthographic = true; camera.orthographicSize = orthographicSize;
                var radio = host.GetComponent<ArmyRadioChatterController>();
                typeof(ArmyRadioChatterController).GetField("_camera", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(radio, camera);
                float scale = (float)typeof(ArmyRadioChatterController)
                    .GetMethod("MessageScale", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(radio, null);
                Assert.That(scale, Is.EqualTo(2f * ArmyRadioLayout.CameraScale(orthographicSize, 2.6f)).Within(.00001f));
                camera.orthographic = false;
                scale = (float)typeof(ArmyRadioChatterController)
                    .GetMethod("MessageScale", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(radio, null);
                Assert.That(scale, Is.EqualTo(2f));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(cameraHost); }
        }

        [TestCase(2.6f, 1f)]
        [TestCase(4f, .65f)]
        [TestCase(7f, .3714286f)]
        [Category("ArmyRadioManaged")]
        public void Zoom_ShrinksTheWholeTransmissionWithTheMap(float size, float expected)
        {
            Assert.That(ArmyRadioLayout.CameraScale(size, 2.6f), Is.EqualTo(expected).Within(.00001f));
        }

        [TestCase(2.6f)]
        [TestCase(4f)]
        [TestCase(7f)]
        [Category("ArmyRadioManaged")]
        public void Zoom_ScaledBodiesAndWiresRemainClearAtEveryScreenEdge(float cameraSize)
        {
            float scale = ArmyRadioLayout.CameraScale(cameraSize, 2.6f);
            var safe = new Rect(-640f, -180f, 1280f, 540f);
            var size = new Vector2(248f, 78f) * scale;
            foreach (var target in new[] { new Vector2(-600f, 0f), new Vector2(600f, 0f), new Vector2(0f, 330f), new Vector2(0f, -150f) })
            {
                var occupied = new System.Collections.Generic.List<Rect>();
                for (int i = 0; i < 3; i++)
                {
                    Vector2 anchor = target + new Vector2(i * 5f, i * 5f);
                    bool placed = ArmyRadioLayout.TryPlace(safe, anchor, size, 24f * scale, occupied, null,
                        out var centre, out bool left, 68f * scale, 56f * scale, 14f * scale, 9f * scale, 4f * scale, new Vector2(54f, 78f) * scale);
                    if (i == 0) Assert.That(placed, Is.True);
                    // Densely overlapping marker anchors need not fit all three slots;
                    // the presenter suppresses an unplaceable transmission.
                    if (!placed) continue;
                    Rect body = ArmyRadioLayout.Bounds(centre, size);
                    foreach (var other in occupied) Assert.That(ArmyRadioLayout.Inset(body, -7f).Overlaps(other), Is.False);
                    Rect panel = ArmyRadioLayout.PanelBounds(centre, size, 68f * scale, 56f * scale, left);
                    Assert.That(ArmyRadioLayout.Pointer(panel, anchor, 9f * scale, 14f * scale, out var a, out var b, out var tip, 4f * scale), Is.True);
                    Assert.That(Vector2.Distance(anchor, tip), Is.EqualTo(14f * scale).Within(.001f));
                    Vector2 artCentre = centre + new Vector2((left ? -1f : 1f) * (size.x - 54f * scale) * .5f, 0f);
                    Rect art = ArmyRadioLayout.Bounds(artCentre, new Vector2(54f, 78f) * scale);
                    for (int sample = 0; sample <= 50; sample++)
                        Assert.That(art.Contains(Vector2.Lerp((a + b) * .5f, tip, sample / 50f)), Is.False, "Wire must clear its own art.");
                    occupied.Add(body); ArmyRadioLayout.AddPointerObstacles((a + b) * .5f, tip, occupied);
                }
            }
        }

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
                    Assert.That(ArmyRadioLayout.TryPlace(safe, new Vector2(x, y), size, 24f, new Rect[0], null,
                        out Vector2 position, out bool left), Is.True);
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

        [TestCase(0f)]
        [TestCase(20f)]
        [Category("ArmyRadioManaged")]
        public void Pointer_RespectsThePrefabCornerRadius(float radius)
        {
            var panel = new Rect(-110f, -32f, 220f, 64f);
            Assert.That(ArmyRadioLayout.Pointer(panel, new Vector2(400f, 100f), radius, 14f, out var a, out var b, out _), Is.True);
            Vector2 centre = (a + b) * .5f;
            Assert.That(Mathf.Abs(centre.y), Is.LessThanOrEqualTo(32f - radius - 4f + .001f));
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
        public void OngoingContact_OverridesWaitingDamageAndIdleFlavor()
        {
            _army.Members[0].HitPointsMax = 3;
            Assert.That(ArmyRadioChatter.Situation(_army, true, false, 60f), Is.EqualTo(ArmyRadioSituation.Contact));
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
            ArmyRadioChatter.Phrase(situation, 2);
            Assert.That(UnityEngine.Random.state, Is.EqualTo(before));
            Assert.That(_army.HasActivatedThisTurn, Is.False);
            Assert.That(_army.Members.Count, Is.EqualTo(1));
        }
    }

    public class ArmyRadioPhraseTests
    {
        [Test, Category("ArmyRadioManaged")]
        public void Phrases_CoverEverySituationWithOneHundredDistinctEnglishLines()
        {
            var unique = new System.Collections.Generic.HashSet<string>();
            foreach (ArmyRadioSituation situation in Enum.GetValues(typeof(ArmyRadioSituation)))
            {
                int count = ArmyRadioChatter.PhraseCount(situation);
                Assert.That(count, Is.GreaterThanOrEqualTo(12));
                for (int i = 0; i < count; i++) Assert.That(unique.Add(ArmyRadioChatter.Phrase(situation, i)), Is.True);
                Assert.That(ArmyRadioChatter.Phrase(situation, count), Is.EqualTo(ArmyRadioChatter.Phrase(situation, 0)));
                Assert.That(ArmyRadioChatter.Phrase(situation, int.MinValue), Is.Not.Empty);
            }
            Assert.That(unique.Count, Is.EqualTo(100));
            Assert.That(ArmyRadioChatter.JokeCount, Is.EqualTo(25));
            for (int i = 0; i < ArmyRadioChatter.JokeCount; i++) Assert.That(unique.Add(ArmyRadioChatter.Joke(i)), Is.True);
            for (int i = 0; i < ArmyRadioChatter.DialogueCount; i++)
            {
                Assert.That(ArmyRadioChatter.Dialogue(i, false), Is.Not.Empty);
                Assert.That(ArmyRadioChatter.Dialogue(i, true), Is.Not.EqualTo(ArmyRadioChatter.Dialogue(i, false)));
            }
        }
    }

    public class ArmyRadioManagerTests
    {
        [Test, Category("ArmyRadioManaged")]
        public void Cadence_EnforcesGlobalAndPerArmyGapsAndReservesTheReply()
        {
            var clock = new ArmyRadioCadence(0f);
            Assert.That(clock.CanSpeak(1, 1f, 45f), Is.False);
            Assert.That(clock.CanSpeak(1, 2f, 45f), Is.True);
            clock.Spoke(1, 2f, 3f); clock.Reserve(2, 2f);
            Assert.That(clock.CanSpeak(3, 4.99f, 45f), Is.False);
            Assert.That(clock.CanSpeak(3, 5f, 45f), Is.True);
            Assert.That(clock.CanSpeak(1, 46.99f, 45f), Is.False);
            Assert.That(clock.ArmyReady(2, 46.99f, 45f), Is.False);
            Assert.That(clock.CanSpeak(1, 47f, 45f), Is.True);
            Assert.That(clock.ArmyReady(2, 47f, 45f), Is.True);
        }

        [Test, Category("ArmyRadioManaged")]
        public void Timing_FadesQuicklyAndDismissDuringEntryNeverBrightensOrRestarts()
        {
            var clock = new ArmyRadioTiming(10f, 5f, .18f, .28f);
            Assert.That(clock.Alpha(10f), Is.EqualTo(0f));
            Assert.That(clock.Alpha(10.09f), Is.EqualTo(.5f).Within(.001f));
            clock.Dismiss(10.09f);
            float alpha = clock.Alpha(10.2f);
            Assert.That(alpha, Is.GreaterThan(0f).And.LessThan(.5f));
            clock.Dismiss(10.25f);
            Assert.That(clock.Finished(10.38f), Is.True);
            Assert.That(clock.Alpha(10.38f), Is.EqualTo(0f));
            var natural = new ArmyRadioTiming(0f, 5f, .18f, .28f);
            Assert.That(natural.Alpha(4f), Is.EqualTo(1f));
            Assert.That(natural.Alpha(4.86f), Is.EqualTo(.5f).Within(.001f));
            Assert.That(natural.Finished(5f), Is.True);
        }

        [TestCase(1024f, 576f)]
        [TestCase(1280f, 720f)]
        [TestCase(1920f, 1080f)]
        [Category("ArmyRadioManaged")]
        public void Layout_ReflowsThreeNearbySpeakersWithoutOverlapAfterCameraMoves(float width, float height)
        {
            var safe = new Rect(-width / 2f, -height / 2f + 170f, width, height - 170f);
            var size = new Vector2(278f, 110f);
            var offsets = new Vector2?[3];
            for (int frame = 0; frame < 10; frame++)
            {
                var occupied = new System.Collections.Generic.List<Rect>();
                for (int i = 0; i < 3; i++)
                {
                    var anchor = new Vector2(i * 22f + frame * 12f, frame * 8f);
                    bool placed = ArmyRadioLayout.TryPlace(safe, anchor, size, 24f, occupied, offsets[i], out var centre, out bool left);
                    Assert.That(placed, Is.True);
                    var bounds = ArmyRadioLayout.Bounds(centre, size);
                    foreach (var other in occupied) Assert.That(ArmyRadioLayout.Inset(bounds, -7f).Overlaps(other), Is.False);
                    Assert.That(left, Is.EqualTo(centre.x > anchor.x));
                    occupied.Add(bounds); offsets[i] = centre - anchor;
                }
            }
        }

        [TestCase(-450f, -90f)]
        [TestCase(450f, -90f)]
        [TestCase(-450f, 260f)]
        [TestCase(450f, 260f)]
        [Category("ArmyRadioManaged")]
        public void Layout_AdjacentDialogueReservesFullArtBodiesAndActualPointers(float x, float y)
        {
            var safe = new Rect(-512f, -118f, 1024f, 406f);
            var occupied = new System.Collections.Generic.List<Rect>();
            var size = new Vector2(278f, 78f);
            for (int i = 0; i < 2; i++)
            {
                var target = new Vector2(x + (x < 0f ? 1f : -1f) * i * 90f, y);
                Assert.That(ArmyRadioLayout.TryPlace(safe, target, size, 24f, occupied, null,
                    out var centre, out bool left, 58f, 52f, 14f), Is.True);
                var body = ArmyRadioLayout.Bounds(centre, size);
                foreach (var other in occupied) Assert.That(ArmyRadioLayout.Inset(body, -7f).Overlaps(other), Is.False);
                var panel = ArmyRadioLayout.PanelBounds(centre, size, 58f, 52f, left);
                Assert.That(ArmyRadioLayout.Pointer(panel, target, 9f, 14f, out var a, out var b, out var tip), Is.True);
                Assert.That(ArmyRadioLayout.PointerPathClear((a + b) * .5f, tip, occupied), Is.True);
                occupied.Add(body); ArmyRadioLayout.AddPointerObstacles((a + b) * .5f, tip, occupied);
            }
        }

        [Test, Category("ArmyRadioManaged")]
        public void Layout_AvoidsConnectorsAndPreservesAnUnblockedCameraRelativeOffset()
        {
            var obstacles = new System.Collections.Generic.List<Rect>();
            ArmyRadioLayout.AddPointerObstacles(new Vector2(-40f, 0f), new Vector2(40f, 100f), obstacles);
            Assert.That(ArmyRadioLayout.PointerPathClear(new Vector2(-40f, 100f), new Vector2(40f, 0f), obstacles), Is.False);
            Assert.That(ArmyRadioLayout.PointerPathClear(new Vector2(200f, 100f), new Vector2(200f, 0f), obstacles), Is.True);
            var safe = new Rect(-600f, -250f, 1200f, 600f);
            var size = new Vector2(278f, 100f);
            Assert.That(ArmyRadioLayout.TryPlace(safe, Vector2.zero, size, 24f, obstacles, null,
                out var centre, out bool left, 58f, 70f, 14f), Is.True);
            obstacles.Clear();
            var anchor = new Vector2(10f, 10f);
            Assert.That(ArmyRadioLayout.TryPlace(safe, anchor, size, 24f, obstacles, centre,
                out var moved, out _, 58f, 70f, 14f), Is.True);
            Assert.That(moved.x, Is.EqualTo(centre.x + 10f).Within(.001f));
            Assert.That(moved.y, Is.EqualTo(centre.y + 10f).Within(.001f));
        }

        [Test, Category("ArmyRadioManaged")]
        public void Layout_DeclinesAnOccupiedOrTooSmallViewportInsteadOfOverlapping()
        {
            var safe = new Rect(-200f, -100f, 400f, 200f);
            var occupied = new[] { safe };
            Assert.That(ArmyRadioLayout.TryPlace(safe, Vector2.zero, new Vector2(278f, 78f), 24f,
                occupied, null, out _, out _), Is.False);
            Assert.That(ArmyRadioLayout.TryPlace(safe, Vector2.zero, new Vector2(500f, 78f), 24f,
                new Rect[0], null, out _, out _), Is.False);
        }
    }

    public class ArmyRadioLifecycleTests
    {
        [TestCase(1f, "Shadows make good neighbors.")]
        [TestCase(.5f, "Shadows make good neighbors.")]
        [TestCase(1f, "Radio check. Holding position and watching the perimeter. All eyes open. Reporting in.")]
        [TestCase(.5f, "Radio check. Holding position and watching the perimeter. All eyes open. Reporting in.")]
        public void ActualPrefab_RenderedPanelMatchesPlacementGeometry(float scale, string text)
        {
            var host = CreateRadioPrefab();
            var texture = new Texture2D(772, 1124);
            var art = Sprite.Create(texture, new Rect(0, 0, 772, 1124), new Vector2(.5f, .5f));
            try
            {
                var view = host.GetComponentInChildren<ArmyRadioBubbleView>(true);
                var army = new ArmyData(); army.Members.Add(new UnitData { DetailArt = art });
                view.Show(army, text, 0f, new ArmyRadioChatterSettings());
                Assert.That(view.Follow(new Rect(-640f, -180f, 1280f, 540f), Vector2.zero, 12f * scale, new Rect[0], scale), Is.True);
                var panel = (RectTransform)Field(view, "_panel");
                var shadow = (RectTransform)Field(view, "_shadow");
                var portrait = (UnityEngine.UI.Image)Field(view, "_art");
                var label = view.GetComponentInChildren<TMPro.TMP_Text>(true);
                float width = (float)Field(view, "messageWidth");
                Vector2 padding = label.rectTransform.offsetMin - label.rectTransform.offsetMax;
                float height = Mathf.Max((float)Field(view, "minimumPanelHeight"),
                    label.GetPreferredValues(text, width - padding.x, 1000f).y + padding.y);
                Assert.That(panel.rect.width, Is.EqualTo(width).Within(.01f));
                Assert.That(panel.rect.height, Is.EqualTo(height).Within(.01f));
                Assert.That(shadow.rect.size.x, Is.EqualTo(panel.rect.size.x).Within(.01f));
                Assert.That(shadow.rect.size.y, Is.EqualTo(panel.rect.size.y).Within(.01f));
                float gap = Mathf.Abs(panel.anchoredPosition.x - portrait.rectTransform.anchoredPosition.x)
                    - (panel.rect.width + portrait.rectTransform.rect.width) * .5f;
                Assert.That(gap, Is.EqualTo((float)Field(view, "cardGap")).Within(.01f), "Background must not cover the portrait.");
                var expected = ArmyRadioLayout.PanelBounds(view.Bounds.center, view.Bounds.size,
                    (portrait.rectTransform.rect.width + gap) * scale, height * scale, view.Bounds.center.x > 0f);
                var corners = new Vector3[4]; panel.GetWorldCorners(corners);
                Vector3 min = host.transform.InverseTransformPoint(corners[0]);
                Vector3 max = host.transform.InverseTransformPoint(corners[2]);
                Assert.That(min.x, Is.EqualTo(expected.xMin).Within(.01f));
                Assert.That(min.y, Is.EqualTo(expected.yMin).Within(.01f));
                Assert.That(max.x, Is.EqualTo(expected.xMax).Within(.01f));
                Assert.That(max.y, Is.EqualTo(expected.yMax).Within(.01f));
            }
            finally { Object.DestroyImmediate(host.transform.root.gameObject); Object.DestroyImmediate(art); Object.DestroyImmediate(texture); }
        }

        [Test]
        public void ActualPrefab_ZoomScalesArtTextAndBoundsWithoutChangingTheInspectorFont()
        {
            var host = CreateRadioPrefab();
            var texture = new Texture2D(772, 1124);
            var art = Sprite.Create(texture, new Rect(0, 0, 772, 1124), new Vector2(.5f, .5f));
            try
            {
                var view = host.GetComponentInChildren<ArmyRadioBubbleView>(true);
                var label = view.GetComponentInChildren<TMPro.TMP_Text>(true);
                float fontSize = label.fontSize;
                var army = new ArmyData(); army.Members.Add(new UnitData { DetailArt = art });
                view.Show(army, "All eyes open. Reporting in.", 0f, new ArmyRadioChatterSettings());
                var safe = new Rect(-640f, -180f, 1280f, 540f);
                var empty = new Rect[0];
                Assert.That(view.Follow(safe, Vector2.zero, 12f, empty, 1f), Is.True);
                Vector2 full = view.Bounds.size;
                Vector2 offset = ((RectTransform)view.transform).anchoredPosition;
                Assert.That(view.Follow(safe, Vector2.zero, 6f, empty, .5f), Is.True);
                Assert.That(view.Bounds.width, Is.EqualTo(full.x * .5f).Within(.01f));
                Assert.That(view.Bounds.height, Is.EqualTo(full.y * .5f).Within(.01f));
                Assert.That(view.transform.localScale, Is.EqualTo(Vector3.one * .5f));
                Assert.That(((RectTransform)view.transform).anchoredPosition.x, Is.EqualTo(offset.x * .5f).Within(.01f));
                Assert.That(label.fontSize, Is.EqualTo(fontSize));
                Assert.That(view.GetComponentInChildren<UnityEngine.UI.Image>(true).preserveAspect, Is.True);
            }
            finally { Object.DestroyImmediate(host.transform.root.gameObject); Object.DestroyImmediate(art); Object.DestroyImmediate(texture); }
        }

        [Test]
        public void ArmyInteraction_HidesItsMessageCancelsTheReplyAndUnsubscribes()
        {
            var host = new GameObject("Inactive interaction host"); host.SetActive(false);
            var radioObject = CreateRadioPrefab();
            try
            {
                var turns = host.AddComponent<Game.Turns.GameTurnController>();
                var selection = host.AddComponent<HexSelectionController>();
                var radio = radioObject.GetComponent<ArmyRadioChatterController>();
                radio.Configure(turns, null, null, new ArmyRadioChatterSettings(), selection);
                var army = new ArmyData { Owner = new PlayerSetupData() };
                army.Members.Add(new UnitData());
                typeof(ArmyRadioChatterController).GetField("_viewer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(radio, army.Owner);
                var views = (System.Collections.IList)Field(radio, "_views");
                Assert.That(views.Count, Is.EqualTo(3));
                object view = views[0];
                view.GetType().GetMethod("Show").Invoke(view, new object[] { army, "Radio check.", Time.unscaledTime, new ArmyRadioChatterSettings() });
                Type exchangeType = typeof(ArmyRadioChatterController).GetNestedType("Exchange", BindingFlags.NonPublic);
                object exchange = Activator.CreateInstance(exchangeType);
                exchangeType.GetField("First").SetValue(exchange, army);
                exchangeType.GetField("Second").SetValue(exchange, new ArmyData());
                typeof(ArmyRadioChatterController).GetField("_exchange", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(radio, exchange);
                typeof(HexSelectionController).GetMethod("ShowArmyModal", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(selection, new object[] { army });
                Assert.That(view.GetType().GetProperty("Active").GetValue(view), Is.False);
                Assert.That(view.GetType().GetProperty("Timing").GetValue(view), Is.Null);
                Assert.That(Field(radio, "_exchange"), Is.Null);
                Assert.That(((ArmyRadioCadence)Field(radio, "_cadence")).ArmyReady(army.Id, Time.unscaledTime, 45f), Is.False);
                radio.enabled = false;
                var listeners = (Delegate)typeof(HexSelectionController).GetField("ArmyInteracted", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(selection);
                Assert.That(listeners == null || !Array.Exists(listeners.GetInvocationList(), d => ReferenceEquals(d.Target, radio)), Is.True);
                listeners = (Delegate)typeof(HexSelectionController).GetField("HexInteracted", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(selection);
                Assert.That(listeners == null || !Array.Exists(listeners.GetInvocationList(), d => ReferenceEquals(d.Target, radio)), Is.True);
            }
            finally { Object.DestroyImmediate(radioObject.transform.root.gameObject); Object.DestroyImmediate(host); }
        }

        [TestCase(0, true, false)]
        [TestCase(1, false, false)]
        [TestCase(3, false, true)]
        public void HexInteraction_HidesOnlyLocalSpeakersAndCancelsAnInteractedReply(int q, bool hidden, bool keepsReply)
        {
            var host = new GameObject("Inactive hex interaction host"); host.SetActive(false);
            var radioObject = CreateRadioPrefab();
            try
            {
                var selection = host.AddComponent<HexSelectionController>();
                var radio = radioObject.GetComponent<ArmyRadioChatterController>();
                var settings = new ArmyRadioChatterSettings();
                radio.Configure(host.AddComponent<Game.Turns.GameTurnController>(), null, null, settings, selection);
                var owner = new PlayerSetupData();
                typeof(ArmyRadioChatterController).GetField("_viewer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(radio, owner);
                var first = new ArmyData { Owner = owner, Hex = new HexCoord(0, 0) };
                var sameHex = new ArmyData { Owner = owner, Hex = first.Hex };
                var unrelated = new ArmyData { Owner = owner, Hex = new HexCoord(2, 0) };
                foreach (var army in new[] { first, sameHex, unrelated }) army.Members.Add(new UnitData());
                var views = (ArmyRadioBubbleView[])Field(radio, "_views");
                views[0].Show(first, "Radio check.", Time.unscaledTime, settings);
                views[1].Show(sameHex, "Copy that.", Time.unscaledTime, settings);
                views[2].Show(unrelated, "Standing by.", Time.unscaledTime, settings);
                Type exchangeType = typeof(ArmyRadioChatterController).GetNestedType("Exchange", BindingFlags.NonPublic);
                object exchange = Activator.CreateInstance(exchangeType);
                exchangeType.GetField("First").SetValue(exchange, first);
                exchangeType.GetField("Second").SetValue(exchange, new ArmyData { Owner = owner, Hex = new HexCoord(1, 0) });
                typeof(ArmyRadioChatterController).GetField("_exchange", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(radio, exchange);
                var clicked = (Action<HexCoord>)typeof(HexSelectionController)
                    .GetField("HexInteracted", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(selection);
                Assert.That(clicked, Is.Not.Null);
                clicked(new HexCoord(q, 0));
                Assert.That(views[0].Active, Is.EqualTo(!hidden));
                Assert.That(views[1].Active, Is.EqualTo(!hidden));
                Assert.That(views[2].Active, Is.True, "A click on another hex must leave this transmission alone.");
                Assert.That(Field(radio, "_exchange") != null, Is.EqualTo(keepsReply));
                if (hidden)
                    Assert.That(((ArmyRadioCadence)Field(radio, "_cadence")).ArmyReady(first.Id, Time.unscaledTime, settings.armyCooldown), Is.False);
            }
            finally { Object.DestroyImmediate(radioObject.transform.root.gameObject); Object.DestroyImmediate(host); }
        }
        [Test]
        public void MissingFirstMessage_CancelsThePendingReply()
        {
            var radioObject = CreateRadioPrefab();
            var host = new GameObject("Inactive reply host"); host.SetActive(false);
            try
            {
                var radio = radioObject.GetComponent<ArmyRadioChatterController>();
                radio.Configure(host.AddComponent<Game.Turns.GameTurnController>(), null, null, new ArmyRadioChatterSettings());
                Type exchangeType = typeof(ArmyRadioChatterController).GetNestedType("Exchange", BindingFlags.NonPublic);
                object exchange = Activator.CreateInstance(exchangeType);
                exchangeType.GetField("First").SetValue(exchange, new ArmyData());
                exchangeType.GetField("Second").SetValue(exchange, new ArmyData());
                typeof(ArmyRadioChatterController).GetField("_exchange", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(radio, exchange);
                typeof(ArmyRadioChatterController).GetMethod("PresentReply", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(radio, new object[] { Time.unscaledTime });
                Assert.That(Field(radio, "_exchange"), Is.Null);
                foreach (var view in radioObject.GetComponentsInChildren<ArmyRadioBubbleView>(true)) Assert.That(view.Active, Is.False);
            }
            finally { Object.DestroyImmediate(radioObject.transform.root.gameObject); Object.DestroyImmediate(host); }
        }

        [Test]
        public void ViewerSwitch_ClearsCandidatesButKeepsTheGlobalCadence()
        {
            var host = new GameObject("Inactive radio viewer host"); host.SetActive(false);
            var radioObject = CreateRadioPrefab(); var previousViewer = VisionSystem.CurrentViewer;
            try
            {
                var radio = radioObject.GetComponent<ArmyRadioChatterController>();
                radio.Configure(host.AddComponent<Game.Turns.GameTurnController>(), null, null, new ArmyRadioChatterSettings());
                var old = new PlayerSetupData(); var next = new PlayerSetupData(); var army = new ArmyData { Owner = old };
                typeof(ArmyRadioChatterController).GetField("_viewer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(radio, old);
                var clock = (ArmyRadioCadence)Field(radio, "_cadence"); float now = Time.unscaledTime;
                clock.Spoke(army.Id, now, 3f);
                var dispatch = (ArmyRadioDispatch)Field(radio, "_dispatch");
                dispatch.Add(new ArmyRadioCandidate(ArmyRadioCategory.BattleWon, army, old, new object(), default, now, 10f), now, 24);
                VisionSystem.CurrentViewer = next;
                typeof(ArmyRadioChatterController).GetMethod("OnTurnStateChanged", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(radio, null);
                Assert.That(Field(radio, "_cadence"), Is.SameAs(clock));
                Assert.That(dispatch.PendingCount, Is.Zero);
                Assert.That(clock.RecentCount(now), Is.EqualTo(1));
                Assert.That(clock.ChannelReady(now + 1f, 3), Is.False);
            }
            finally
            { Object.DestroyImmediate(radioObject.transform.root.gameObject); Object.DestroyImmediate(host); VisionSystem.CurrentViewer = previousViewer; }
        }

        private static GameObject CreateRadioPrefab()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/ArmyRadio/ArmyRadioOverlay.prefab");
            Assert.That(prefab, Is.Not.Null);
            var shared = new GameObject("Shared scene Canvas test host", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler));
            shared.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = shared.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f); scaler.matchWidthOrHeight = .5f;
            return Object.Instantiate(prefab, shared.transform, false);
        }
        private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        [Test]
        public void ConfigureTwice_ReusesAuthoredPrefabWithoutCreatingObjectsAndBalancesSubscriptions()
        {
            var turnsObject = new GameObject("Inactive radio turn host"); turnsObject.SetActive(false);
            var radioObject = CreateRadioPrefab();
            try
            {
                var turns = turnsObject.AddComponent<Game.Turns.GameTurnController>();
                var radio = radioObject.GetComponent<ArmyRadioChatterController>();
                int transformsBefore = radioObject.GetComponentsInChildren<Transform>(true).Length;
                var canvasBefore = radioObject.GetComponent<Canvas>();
                var label = radioObject.GetComponentInChildren<TMPro.TMP_Text>(true);
                label.fontSize = 19f; label.color = Color.red;
                var panel = radioObject.GetComponentInChildren<ArmyRadioBubbleGraphic>(true);
                panel.color = Color.blue; panel.BorderColor = Color.green;
                radio.Configure(turns, null, null, new ArmyRadioChatterSettings());
                radio.Configure(turns, null, null, new ArmyRadioChatterSettings());
                Assert.That(radioObject.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(transformsBefore));
                Assert.That(radioObject.GetComponent<Canvas>(), Is.SameAs(canvasBefore));
                Assert.That(radioObject.GetComponentsInChildren<ArmyRadioBubbleView>(true).Length, Is.EqualTo(3));
                var view = radioObject.GetComponentInChildren<ArmyRadioBubbleView>(true);
                var army = new ArmyData(); army.Members.Add(new UnitData());
                view.Show(army, "Inspector style check.", Time.unscaledTime, new ArmyRadioChatterSettings());
                Assert.That(label.fontSize, Is.EqualTo(19f)); Assert.That(label.color, Is.EqualTo(Color.red));
                view.Hide();
                Assert.That(panel.color, Is.EqualTo(Color.blue)); Assert.That(panel.BorderColor, Is.EqualTo(Color.green));
                Assert.That(radio.GetComponentsInChildren<Canvas>(true).Length, Is.EqualTo(1));
                var fade = radio.GetComponentInChildren<CanvasGroup>(true);
                Assert.That(fade.blocksRaycasts, Is.False); Assert.That(fade.interactable, Is.False);
                foreach (var graphic in radio.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                    Assert.That(graphic.raycastTarget, Is.False);
                var image = radio.GetComponentInChildren<UnityEngine.UI.Image>(true);
                Assert.That(image.preserveAspect, Is.True);
                Assert.That(radio.GetComponent<UnityEngine.UI.CanvasScaler>(), Is.Null);
                Assert.That(canvasBefore.rootCanvas, Is.SameAs(radioObject.transform.parent.GetComponent<Canvas>()));
                var scaler = radio.GetComponentInParent<UnityEngine.UI.CanvasScaler>();
                Assert.That(scaler.referenceResolution, Is.EqualTo(new Vector2(1280f, 720f)));
                var eventField = typeof(VisionSystem).GetField("VisibilityChanged", BindingFlags.Static | BindingFlags.NonPublic);
                var listeners = (Delegate)eventField.GetValue(null);
                Assert.That(Array.Exists(listeners.GetInvocationList(), d => ReferenceEquals(d.Target, radio)), Is.True);
                radio.enabled = false;
                listeners = (Delegate)eventField.GetValue(null);
                Assert.That(listeners == null || !Array.Exists(listeners.GetInvocationList(), d => ReferenceEquals(d.Target, radio)), Is.True);
                Assert.That(fade.gameObject.activeSelf, Is.False);
            }
            finally { Object.DestroyImmediate(radioObject.transform.root.gameObject); Object.DestroyImmediate(turnsObject); }
        }
    }
}
#endif
