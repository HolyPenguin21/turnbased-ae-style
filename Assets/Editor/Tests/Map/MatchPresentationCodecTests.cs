#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using System.Reflection;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Saves;
using Game.Setup;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class MatchPresentationCodecTests
    {
        private GameConfig config;
        private PlayerSetupData viewer, enemy;
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private static T Invoke<T>(string method, params object[] arguments)
        {
            try { return (T)typeof(MatchPresentationCodec).GetMethod(method, PrivateStatic).Invoke(null, arguments); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }
        [SetUp] public void SetUp()
        {
            VisionSystem.Clear(); HumanVisualMemory.Clear(); ArmyRegistry.Clear();
            config = ScriptableObject.CreateInstance<GameConfig>();
            viewer = new PlayerSetupData { PlayerId = "viewer", IsHuman = true };
            enemy = new PlayerSetupData { PlayerId = "enemy" };
        }
        [TearDown] public void TearDown()
        {
            VisionSystem.Clear(); HumanVisualMemory.Clear(); ArmyRegistry.Clear();
            if (config != null) Object.DestroyImmediate(config);
        }
        private PlayerSetupData Player(string id) => id == viewer.PlayerId ? viewer : id == enemy.PlayerId ? enemy : null;
        private static MatchVisualNodeData Node() => new MatchVisualNodeData
            { Parent = -1, Name = "frozen", Active = true, Rotation = Quaternion.identity, Scale = Vector3.one };
        private MatchPresentationData Remembered(out ArmyData live)
        {
            live = new ArmyData { Owner = enemy, Name = "Before capture", Hex = new HexCoord(1, 0), IsAirfield = true };
            live.Members.Add(new UnitData { Owner = enemy, Name = "Observed", Attack = 8, Defense = 3,
                HitPointsCurrent = 2, HitPointsMax = 7, MoveMax = 5, MoveCurrent = 1, IsAviation = true, Fate = 1, FateMax = 4 });
            HumanVisualMemory.ObserveArmy(viewer, live, live.Hex);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var memory), Is.True);
            var army = Invoke<MatchRememberedArmyData>("CaptureArmy", live.Id, live.Hex, memory.Army, config);
            var data = new MatchPresentationData { CurrentViewerId = viewer.PlayerId };
            data.Vision.Add(new MatchVisionData { PlayerId = viewer.PlayerId,
                EverSeen = { new MatchHexData { Q = 1 }, new MatchHexData { Q = 2 } },
                Visible = { new MatchHexData { Q = 2 } } }); // peripheral sight outside playable map is legitimate
            data.Memory.Add(new MatchHumanMemoryData { PlayerId = viewer.PlayerId, Armies = { army } });
            data.Visuals.Add(new MatchFrozenVisualData { ViewerId = viewer.PlayerId, IsArmy = true,
                SourceArmyId = live.Id, Q = 1, IsFrozen = true, ClickRadius = .7f, Nodes = { Node() } });
            return data;
        }
        private void Validate(MatchPresentationData data) => MatchPresentationCodec.Validate(data, config, Player,
            new HexMapLayout { Cells = new[] { new MapLayoutCell { Q = 1 } } }, MatchIdentityState.Capture());
        [Test] public void JsonRoundtrip_RetainsIndependentHistoricalRosterAfterHiddenCapture()
        {
            var saved = Remembered(out var live);
            string json = JsonUtility.ToJson(saved);
            MatchSaveJson.Validate(json, typeof(MatchPresentationData));
            saved = JsonUtility.FromJson<MatchPresentationData>(json);
            live.Owner = viewer; live.Name = "Captured behind fog"; live.Members[0].Attack = 99; live.Members.Clear();
            HumanVisualMemory.Clear();
            Validate(saved);
            MatchPresentationCodec.RestoreKnowledge(saved, config, Player);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var restored), Is.True);
            Assert.That(restored.Army.Id, Is.EqualTo(-1));
            Assert.That(ArmyRegistry.IsRegistered(restored.Army), Is.False);
            Assert.That(restored.Army.Owner, Is.SameAs(enemy));
            Assert.That(restored.Army.Name, Is.EqualTo("Before capture"));
            Assert.That(restored.Army.IsAirfield, Is.True);
            Assert.That(restored.Army.Members.Single().Attack, Is.EqualTo(8));
            Assert.That(restored.Army.Members.Single().MoveCurrent, Is.EqualTo(1));
            Assert.That(restored.Army.Members.Single().OriginalResourceCost, Is.Null);
            Assert.That(restored.Army.Members.Single().IsAviation, Is.True);
            Assert.That(VisionSystem.IsVisible(viewer, new HexCoord(2, 0)), Is.True);
        }
        [Test] public void Validate_RejectsMissingAndDuplicateAppearanceBeforeKnowledgeMutation()
        {
            var data = Remembered(out var live);
            var appearance = data.Visuals.Single(); data.Visuals.Clear();
            Assert.Throws<ArgumentException>(() => Validate(data));
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out _), Is.True);
            data.Visuals.Add(appearance); data.Visuals.Add(appearance);
            Assert.Throws<ArgumentException>(() => Validate(data));
        }
        [Test] public void Validate_RejectsNonfinitePoseAndUnissuedHistoricalIdentity()
        {
            var data = Remembered(out _);
            data.Visuals.Single().Nodes[0].Position = new Vector3(float.NaN, 0, 0);
            Assert.Throws<ArgumentException>(() => Validate(data));
            data.Visuals.Single().Nodes[0].Position = Vector3.zero;
            data.Memory.Single().Armies.Single().Members.Single().RuntimeId = int.MaxValue;
            Assert.Throws<ArgumentException>(() => Validate(data));
        }
        [Test] public void FrozenRendererRoundtrip_UsesClonePoseTintSortingAndFlagFrame()
        {
            var manifest = Resources.Load<MatchSaveAssetManifest>("MatchSaveAssetManifest");
            var sprite = manifest?.Sprites.FirstOrDefault(s => s?.Asset != null)?.Asset;
            if (sprite == null) Assert.Ignore("The source cache does not contain authored sprite binaries; run with complete Unity project assets.");
            GameObject live = new GameObject("live"), decoded = null; MapObjectVisual frozen = null;
            try
            {
                var marker = live.AddComponent<MapObjectVisual>();
                var child = new GameObject("historical flag"); child.transform.SetParent(live.transform, false);
                child.transform.localPosition = new Vector3(.3f, .4f, .5f); child.transform.localScale = new Vector3(2, 3, 4);
                var renderer = child.AddComponent<SpriteRenderer>(); renderer.sprite = sprite; renderer.color = Color.magenta;
                renderer.sortingOrder = 37; renderer.flipX = true;
                typeof(MapObjectVisual).GetField("innerCircle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(marker, renderer);
                live.transform.position = new Vector3(7, 2, 9); live.transform.localScale = new Vector3(2, 2, 2);
                live.AddComponent<MapFlagAnimator>(); frozen = marker.CreateFrozenSnapshot();
                renderer.color = Color.green; child.transform.localScale = Vector3.one;
                var data = Invoke<MatchFrozenVisualData>("CaptureVisual", frozen);
                data = JsonUtility.FromJson<MatchFrozenVisualData>(JsonUtility.ToJson(data));
                var restored = Invoke<MapObjectVisual>("RestoreVisual", data); decoded = restored.gameObject;
                var old = restored.GetComponentInChildren<SpriteRenderer>(true);
                Assert.That(old.sprite, Is.SameAs(sprite)); Assert.That(old.color, Is.EqualTo(Color.magenta));
                Assert.That(old.sortingOrder, Is.EqualTo(37)); Assert.That(old.flipX, Is.True);
                Assert.That(old.transform.localScale, Is.EqualTo(new Vector3(2, 3, 4)));
                Assert.That(decoded.transform.position, Is.EqualTo(new Vector3(7, 2, 9)));
                Assert.That(restored.IsFrozenRememberedAppearance, Is.True);
                Assert.That(restored.GetComponentsInChildren<MapFlagAnimator>(true), Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(live);
                if (frozen != null) Object.DestroyImmediate(frozen.gameObject);
                if (decoded != null) Object.DestroyImmediate(decoded);
            }
        }
        [Test] public void FrozenRendererRoundtrip_ReobservationRefreshesOriginalRendererRoles()
        {
            var live = new GameObject("observed army");
            MapObjectVisual restored = null;
            try
            {
                var marker = live.AddComponent<MapObjectVisual>();
                var renderers = new SpriteRenderer[4];
                for (int i = 0; i < renderers.Length; i++)
                {
                    var child = new GameObject("layer " + i); child.transform.SetParent(live.transform, false);
                    renderers[i] = child.AddComponent<SpriteRenderer>();
                    renderers[i].sharedMaterial = null; // This role regression requires no authored binary assets.
                }
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MapObjectVisual).GetField("innerCircle", fields).SetValue(marker, renderers[0]);
                typeof(MapObjectVisual).GetField("objectImage", fields).SetValue(marker, renderers[1]);
                typeof(MapObjectVisual).GetField("factionAccent", fields).SetValue(marker, renderers[2]);
                typeof(MapObjectVisual).GetField("auxiliaryRenderers", fields).SetValue(marker, new[] { null, renderers[3] });
                marker.FreezeRememberedAppearance();
                var data = Invoke<MatchFrozenVisualData>("CaptureVisual", marker);
                data = MatchSaveJson.Deserialize<MatchFrozenVisualData>(MatchSaveJson.Serialize(data));
                restored = Invoke<MapObjectVisual>("RestoreVisual", data);
                var colors = new[] { Color.red, Color.green, Color.blue, Color.magenta };
                for (int i = 0; i < renderers.Length; i++) renderers[i].color = colors[i];
                // UpdateRememberedArmyVisual calls this on the next real observation.
                restored.CopyAppearanceFrom(marker);
                var imported = restored.GetComponentsInChildren<SpriteRenderer>(true);
                Assert.That(imported.Select(r => r.color), Is.EqualTo(colors));
                restored.SetVisible(true);
                Assert.That(imported.All(r => r.enabled), Is.True);
                data.InnerCircle = data.Renderers.Count;
                Assert.Throws<ArgumentException>(() => Invoke<MapObjectVisual>("RestoreVisual", data));
            }
            finally
            {
                Object.DestroyImmediate(live);
                if (restored != null) Object.DestroyImmediate(restored.gameObject);
            }
        }
    }
}
#endif
