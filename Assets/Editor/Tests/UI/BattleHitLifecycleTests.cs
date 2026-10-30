#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.EditorTests
{
    // Enters Play Mode per test (~20 s each): full suite only (Tools/ai-verify/README.md, "Unity runs").
    [Category("Extended")]
    public sealed class BattleHitLifecycleTests
    {
        [UnitySetUp]
        public IEnumerator SetUp() { yield return new EnterPlayMode(); }
        [UnityTearDown]
        public IEnumerator TearDown() { yield return null; yield return new ExitPlayMode(); }
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object o, string name, object value) => o.GetType().GetField(name, Private).SetValue(o, value);
        private static object Get(object o, string name) => o.GetType().GetField(name, Private).GetValue(o);
        private static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Private).Invoke(o, args);
        private static UnitData Body(PlayerSetupData owner, string name) => new UnitData
            { Owner = owner, Name = name, HitPointsCurrent = 20, HitPointsMax = 20, Attack = 4, Defense = 1, Range = 1 };
        private static GameObject Root()
        {
            var root = new GameObject("Hit lifetime", typeof(RectTransform), typeof(Canvas));
            ((RectTransform)root.transform).sizeDelta = new Vector2(1000, 600);
            return root;
        }
        private static BattleScreenUI Screen(GameObject root, out BattleState state,
            out UnitData actor, out UnitData next, out UnitData victim)
        {
            var screen = root.AddComponent<BattleScreenUI>();
            var own = new PlayerSetupData { IsHuman = true };
            var foe = new PlayerSetupData { IsHuman = true };
            actor = Body(own, "Actor"); next = Body(own, "Next"); victim = Body(foe, "Victim");
            var army = new ArmyData { Owner = own }; army.Members.AddRange(new[] { actor, next });
            var enemy = new ArmyData { Owner = foe }; enemy.Members.Add(victim);
            var grid = new BattleGrid(); grid.Set(2, 1, actor); grid.Set(3, 1, next); grid.Set(1, 1, victim);
            state = new BattleState(default, army, enemy, grid, 42);
            state.TurnOrder = new List<UnitData> { actor, next, victim };
            Set(screen, "_localArmy", army); Set(screen, "_battleState", state);
            Set(screen, "_battleEngine", new BattleEngine(state, AbilityMagnitudes.Default));
            Set(screen, "_currentActingUnit", actor);
            var container = new GameObject("Grid", typeof(RectTransform));
            container.transform.SetParent(root.transform, false);
            Set(screen, "gridContainer", container.transform);
            Card(screen, actor, 2, 1); Card(screen, next, 3, 1); Card(screen, victim, 1, 1);
            return screen;
        }
        private static BattleGridCellUI Card(BattleScreenUI screen, UnitData unit, int row, int col)
        {
            var card = new GameObject(unit.Name, typeof(RectTransform), typeof(BattleGridCellUI));
            card.transform.SetParent((Transform)Get(screen, "gridContainer"), false);
            var cell = card.GetComponent<BattleGridCellUI>();
            cell.RectTransform.sizeDelta = new Vector2(100, 150);
            cell.RectTransform.anchoredPosition = new Vector2(col * 110, row * 160);
            cell.Setup(screen, unit, row, col, false);
            ((List<BattleGridCellUI>)Get(screen, "_cells")).Add(cell);
            return cell;
        }

        [TestCase(3)]
        [TestCase(0)]
        public void OrdinaryResultAdvancesInTheSameCallWithoutFloatingLabels(int damage)
        {
            var root = Root();
            try
            {
                var screen = Screen(root, out var state, out var actor, out var next, out var victim);
                int finished = 0;
                Call(screen, "StartAttackPresentation", actor,
                    new BattleAttackApplication { Victim = victim, Damage = damage },
                    (Action)(() => { finished++; Call(screen, "EndTurn"); }));
                Assert.That(finished, Is.EqualTo(1));
                Assert.That(state.TurnIndex, Is.EqualTo(1));
                Assert.That(Get(screen, "_currentActingUnit"), Is.SameAs(next));
                Assert.That((bool)Get(screen, "_isPresentingAttack"), Is.False);
                Assert.That(root.GetComponentsInChildren<BattleDamageFlipbook>(), Is.Empty);
                Assert.That(root.transform.Find("IncomingDamage"), Is.Null);
                Assert.That(((ICollection)Get(screen, "_attackEffectLayers")).Count, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void SecondaryResultsFinishBeforeEffectsAndImmediateTurnAdvance()
        {
            var root = Root();
            try
            {
                var screen = Screen(root, out var state, out var actor, out var next, out var victim);
                actor.Abilities.Add(UnitAbilities.Splash);
                foreach (int col in new[] { 0, 2 })
                {
                    var neighbour = Body(victim.Owner, "Neighbour " + col);
                    state.Defender.Members.Add(neighbour); state.Grid.Set(1, col, neighbour);
                    Card(screen, neighbour, 1, col);
                }
                var popupObject = new GameObject("Attack popup", typeof(RectTransform), typeof(BattleAttackPopupUI));
                popupObject.transform.SetParent(root.transform, false);
                var popup = popupObject.GetComponent<BattleAttackPopupUI>();
                Set(screen, "attackPopup", popup);
                Set(popup, "_attacker", actor); Set(popup, "_defender", victim);
                var roll = new BattleChallengeRollResult(new[] { true, true, true, true }, new bool[0], 0, 0);
                Call(screen, "OnAttackResolved", actor, victim, null, null, roll, false);
                Assert.That((bool)Get(screen, "_isPresentingAttack"), Is.True);
                Assert.That(root.GetComponentsInChildren<BattleDamageFlipbook>(), Is.Empty);
                Assert.That(state.TurnIndex, Is.Zero);
                Action firstResult = (Action)Get(popup, "_onAnnouncementAcknowledged");
                Assert.That(firstResult, Is.Not.Null);

                Call(popup, "OnOkClicked");
                Assert.That(Get(popup, "_onAnnouncementAcknowledged"), Is.Not.Null);
                Assert.That(root.GetComponentsInChildren<BattleDamageFlipbook>(), Is.Empty);
                Assert.That(state.TurnIndex, Is.Zero, "the second victim still needs acknowledgement");

                Call(popup, "OnOkClicked");
                Assert.That(Get(popup, "_onAnnouncementAcknowledged"), Is.Null);
                Assert.That(root.GetComponentsInChildren<BattleDamageFlipbook>().Length, Is.EqualTo(2));
                Assert.That(state.TurnIndex, Is.EqualTo(1));
                Assert.That(Get(screen, "_currentActingUnit"), Is.SameAs(next));
                Assert.That((bool)Get(screen, "_isPresentingAttack"), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void MixedSkillEffectsSurviveGridRefreshAndFollowingAttack()
        {
            var root = Root();
            try
            {
                var screen = Screen(root, out _, out var actor, out _, out var victim);
                var splashVictim = Body(victim.Owner, "Splash victim");
                var fireVictim = Body(victim.Owner, "Scorcher victim");
                Card(screen, splashVictim, 1, 0); Card(screen, fireVictim, 1, 2);
                var application = new BattleAttackApplication { Victim = victim };
                application.SecondaryHits.Add(new BattleSecondaryHit(splashVictim, 1, false, UnitAbilities.Splash));
                application.SecondaryHits.Add(new BattleSecondaryHit(fireVictim, 1, false, UnitAbilities.Scorcher));
                var oldCards = ((List<BattleGridCellUI>)Get(screen, "_cells")).ToArray();
                Call(screen, "PlayAttackEffects", actor, application);
                var effects = root.GetComponentsInChildren<BattleDamageFlipbook>();
                Assert.That(effects.Length, Is.EqualTo(2));
                var source = (RectTransform)Get(effects[0], "_sourceCard");
                Vector3 position = source.position;
                Call(screen, "RefreshGrid");
                foreach (var card in oldCards) UnityEngine.Object.DestroyImmediate(card.gameObject);
                Assert.That(source, Is.Not.Null);
                Assert.That(source.position, Is.EqualTo(position));
                foreach (var effect in effects)
                {
                    Assert.That(Get(effect, "_sourceCard"), Is.SameAs(source));
                    Assert.That((RectTransform)Get(effect, "_targetCard"), Is.Not.Null);
                    if ((bool)Get(effect, "_fire")) Assert.That((bool)Get(effect, "_sourcePulse"), Is.True);
                }
                Card(screen, victim, 1, 1); Card(screen, splashVictim, 1, 0); Card(screen, fireVictim, 1, 2);
                Call(screen, "PlayAttackEffects", actor, application);
                Assert.That(root.GetComponentsInChildren<BattleDamageFlipbook>().Length, Is.EqualTo(4),
                    "a following attack must not destroy the previous animation");
                var parent = (RectTransform)root.transform;
                var normalized = Rect.PointToNormalized(parent.rect, parent.InverseTransformPoint(source.position));
                parent.sizeDelta *= 1.5f;
                var resized = Rect.PointToNormalized(parent.rect, parent.InverseTransformPoint(source.position));
                Assert.That(Vector2.Distance(resized, normalized), Is.LessThan(.00001f),
                    "effect anchors scale with the canvas independently of cards");
                Call(screen, "CancelAttackPresentation");
                Assert.That(root.GetComponentsInChildren<BattleDamageFlipbook>(), Is.Empty);
                Assert.That(((ICollection)Get(screen, "_attackEffectLayers")).Count, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void LethalSkillOverlaysOutliveCardsAndExpireWithoutHoldingTheTurn()
        {
            var root = Root();
            var texture = new Texture2D(4, 4);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 4, 4), Vector2.one * .5f);
            var sprites = (Dictionary<string, Sprite>)typeof(BattleHealEffect)
                .GetField("_sprites", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var old = new Dictionary<string, Sprite>();
            var paths = new[] { BattleHealEffect.DrillSpritePath, BattleHealEffect.BodyguardSpritePath,
                BattleHealEffect.ShockSpritePath };
            foreach (string path in paths)
            {
                if (sprites.TryGetValue(path, out var previous)) old.Add(path, previous);
                sprites[path] = sprite;
            }
            try
            {
                var screen = Screen(root, out _, out var actor, out _, out var victim);
                var secondary = Body(victim.Owner, "Lethal neighbour");
                Card(screen, secondary, 1, 0);
                var oldCards = ((List<BattleGridCellUI>)Get(screen, "_cells")).ToArray();
                actor.HitPointsCurrent = victim.HitPointsCurrent = secondary.HitPointsCurrent = 0;
                var application = new BattleAttackApplication { Victim = victim, Damage = 3,
                    DefenderDied = true, BerserkTriggered = true, InterceptedBy = victim, ShockTriggered = true };
                application.SecondaryHits.Add(new BattleSecondaryHit(secondary, 1, true, UnitAbilities.Splash, true));
                int finished = 0;
                Call(screen, "StartAttackPresentation", actor, application, (Action)(() => finished++));
                Assert.That(finished, Is.EqualTo(1));
                foreach (var card in oldCards) UnityEngine.Object.DestroyImmediate(card.gameObject);
                Assert.That(root.GetComponentsInChildren<BattleHealEffect>().Length, Is.EqualTo(4),
                    "Berserk, Bodyguard and Shock survive lethal card removal");
                var layers = (List<(RectTransform Layer, float ExpiresAt)>)Get(screen, "_attackEffectLayers");
                Assert.That(layers.Count, Is.EqualTo(1));
                layers[0] = (layers[0].Layer, Time.unscaledTime - 1f);
                Call(screen, "RetireFinishedAttackEffects");
                Assert.That(layers, Is.Empty);
                Assert.That(root.GetComponentsInChildren<BattleHealEffect>(), Is.Empty);
                Assert.That(root.GetComponentsInChildren<BattleDamageFlipbook>(), Is.Empty);
            }
            finally
            {
                foreach (string path in paths)
                    if (old.TryGetValue(path, out var previous)) sprites[path] = previous;
                    else sprites.Remove(path);
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(sprite);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void CancelledSecondaryResultCannotAdvanceOrStartEffectsInAnotherBattle()
        {
            var root = Root();
            try
            {
                var screen = Screen(root, out var state, out var actor, out _, out var victim);
                actor.Abilities.Add(UnitAbilities.Splash);
                foreach (int col in new[] { 0, 2 })
                {
                    var neighbour = Body(victim.Owner, "Neighbour " + col);
                    state.Defender.Members.Add(neighbour); state.Grid.Set(1, col, neighbour);
                    Card(screen, neighbour, 1, col);
                }
                var popupObject = new GameObject("Attack popup", typeof(RectTransform), typeof(BattleAttackPopupUI));
                popupObject.transform.SetParent(root.transform, false);
                var popup = popupObject.GetComponent<BattleAttackPopupUI>();
                Set(screen, "attackPopup", popup);
                Set(popup, "_attacker", actor); Set(popup, "_defender", victim);
                Call(screen, "OnAttackResolved", actor, victim, null, null,
                    new BattleChallengeRollResult(new[] { true, true }, new bool[0], 0, 0), false);
                Action staleResult = (Action)Get(popup, "_onAnnouncementAcknowledged");
                Assert.That(staleResult, Is.Not.Null);
                Call(screen, "CancelAttackPresentation");
                Set(popup, "_onAnnouncementAcknowledged", null);
                staleResult();
                Assert.That(Get(popup, "_onAnnouncementAcknowledged"), Is.Null,
                    "a stale acknowledgement must not reopen another old secondary result");
                Assert.That(state.TurnIndex, Is.Zero);
                Assert.That(root.GetComponentsInChildren<BattleDamageFlipbook>(), Is.Empty);
                Assert.That((bool)Get(screen, "_isPresentingAttack"), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void ExplosionHitPresentationDoesNotYieldBeforeTheNextChallenge()
        {
            var root = Root();
            try
            {
                var screen = Screen(root, out _, out var actor, out _, out var victim);
                Set(screen, "_isPresentingAttack", true);
                var effects = (IEnumerator)Call(screen, "PresentExplosionHit", actor,
                    new BattleAttackApplication { Victim = victim, Damage = 3 });
                Assert.That(effects.MoveNext(), Is.False);
                Assert.That((bool)Get(screen, "_isPresentingAttack"), Is.True,
                    "only the remaining challenge sequence keeps the input lock");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
#endif
