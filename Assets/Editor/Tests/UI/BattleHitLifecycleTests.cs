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
    public sealed class BattleHitLifecycleTests
    {
        [UnitySetUp]
        public IEnumerator SetUp() { yield return new EnterPlayMode(); }
        [UnityTearDown]
        public IEnumerator TearDown() { yield return null; yield return new ExitPlayMode(); }
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object o,string name,object value) => o.GetType().GetField(name,Private).SetValue(o,value);
        private static object Get(object o,string name) => o.GetType().GetField(name,Private).GetValue(o);
        private static object Call(object o,string name,params object[] args) => o.GetType().GetMethod(name,Private).Invoke(o,args);
        [Test]
        public void LethalCellRemainsDuringPlaybackAndPassCannotAdvance()
        {
            var root = new GameObject("Hit lifetime",typeof(RectTransform),typeof(Canvas));
            try
            {
                var screen = root.AddComponent<BattleScreenUI>();
                var owner = new PlayerSetupData { IsHuman = true };
                var army = new ArmyData { Owner = owner };
                var victim = new UnitData { HitPointsCurrent = 0, HitPointsMax = 3 };
                var state = new BattleState(default,army,new ArmyData(),new BattleGrid(),42);
                state.TurnOrder = new List<UnitData> { victim, new UnitData() };
                Set(screen,"_localArmy",army); Set(screen,"_battleState",state);
                Set(screen,"_battleEngine",new BattleEngine(state,AbilityMagnitudes.Default));
                var card = new GameObject("Lethal victim",typeof(RectTransform),typeof(BattleGridCellUI));
                card.transform.SetParent(root.transform,false);
                var cell = card.GetComponent<BattleGridCellUI>();
                Set(cell,"rectTransform",(RectTransform)card.transform);
                cell.RectTransform.sizeDelta = new Vector2(100,150);
                cell.Setup(screen,victim,1,1,false);
                var cells = (List<BattleGridCellUI>)Get(screen,"_cells"); cells.Add(cell);
                int finished = 0;
                var playback = (IEnumerator)Call(screen,"PresentAttackThen",null,
                    new BattleAttackApplication { Victim=victim,Damage=3,DefenderDied=true },(Action)(()=>finished++));
                Assert.That(playback.MoveNext(),Is.True);
                var effects = (IEnumerator)playback.Current;
                Assert.That(effects.MoveNext(),Is.True);
                Call(screen,"RefreshGrid"); Call(screen,"OnPassClicked");
                Assert.That(cells.Count,Is.EqualTo(1)); Assert.That(state.TurnIndex,Is.Zero);
                Assert.That(card.GetComponentInChildren<BattleHitEffect>(),Is.Not.Null);
                Assert.That(finished,Is.Zero);
                while(effects.MoveNext()) { }
                Assert.That(playback.MoveNext(),Is.False);
                Assert.That(finished,Is.EqualTo(1)); Assert.That(cells.Count,Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        [Test]
        public void SplashDoesNotConsumeTheFollowingScorcherSourceFlare()
        {
            var root = new GameObject("Mixed skill hit",typeof(RectTransform),typeof(Canvas));
            try
            {
                var screen = root.AddComponent<BattleScreenUI>();
                Set(screen,"_localArmy",new ArmyData { Owner = new PlayerSetupData { IsHuman = true } });
                var grid = new GameObject("Grid",typeof(RectTransform));
                grid.transform.SetParent(root.transform,false);
                Set(screen,"gridContainer",grid.transform);
                var cells = (List<BattleGridCellUI>)Get(screen,"_cells");
                var units = new UnitData[3];
                for (int i=0;i<units.Length;i++)
                {
                    units[i] = new UnitData { HitPointsCurrent=3,HitPointsMax=3 };
                    var card = new GameObject("Victim "+i,typeof(RectTransform),typeof(BattleGridCellUI));
                    card.transform.SetParent(grid.transform,false);
                    var cell = card.GetComponent<BattleGridCellUI>();
                    Set(cell,"rectTransform",(RectTransform)card.transform);
                    cell.RectTransform.sizeDelta = new Vector2(100,150);
                    cell.Setup(screen,units[i],i,1,false); cells.Add(cell);
                }
                var application = new BattleAttackApplication { Victim=units[0] };
                application.SecondaryHits.Add(new BattleSecondaryHit(units[1],1,false,UnitAbilities.Splash));
                application.SecondaryHits.Add(new BattleSecondaryHit(units[2],1,false,UnitAbilities.Scorcher));
                var effects = (IEnumerator)Call(screen,"PlayAttackEffects",null,application);
                Assert.That(effects.MoveNext(),Is.True); // primary damage, before trails
                Assert.That(effects.MoveNext(),Is.True); // trails, before collateral arrival
                var flipbooks = root.GetComponentsInChildren<BattleDamageFlipbook>();
                Assert.That(flipbooks.Length,Is.EqualTo(2));
                foreach (var effect in flipbooks)
                    if ((bool)Get(effect,"_fire")) Assert.That((bool)Get(effect,"_sourcePulse"),Is.True);
                (effects as IDisposable)?.Dispose();
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        [Test]
        public void CancellationDiscardsThePreviousContinuation()
        {
            var root = new GameObject("Cancel hit",typeof(RectTransform));
            try
            {
                var screen = root.AddComponent<BattleScreenUI>(); int finished=0;
                var playback = (IEnumerator)Call(screen,"PresentAttackThen",null,new BattleAttackApplication(),(Action)(()=>finished++));
                Assert.That(playback.MoveNext(),Is.True);
                Call(screen,"CancelAttackPresentation");
                Assert.That(playback.MoveNext(),Is.False);
                Assert.That(finished,Is.Zero); Assert.That((bool)Get(screen,"_isPresentingAttack"),Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
#endif
