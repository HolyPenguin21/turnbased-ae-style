#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class StealthBuildingContactTests
    {
        private readonly HexCoord _hex = new HexCoord(2, -1);
        private PlayerSetupData _owner;
        private PlayerSetupData _enemy;
        private UnitData _unit;
        private ArmyData _army;
        private BuildingData _building;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            StealthSystem.Clear();
            GameSession.Players = new List<PlayerSetupData>();
            _owner = new PlayerSetupData();
            _enemy = new PlayerSetupData();
            _unit = new UnitData { Owner = _owner, IsHidden = true, HitPointsCurrent = 5 };
            _army = new ArmyData { Owner = _owner, Hex = _hex };
            _army.Members.Add(_unit);
            ArmyRegistry.Register(_army);
            _building = new BuildingData { Owner = _enemy, Hex = _hex, Name = "Extraction" };
            BuildingRegistry.Register(_hex, _building);
        }

        [TearDown]
        public void TearDown()
        {
            Game.Ai.AiMapMemory.Clear();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            StealthSystem.Clear();
            GameSession.Players = new List<PlayerSetupData>();
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ResidentRevealResolvesUndefendedBuilding(bool hero, bool isBase)
        {
            _unit.IsHero = hero;
            _building.IsBase = isBase;
            BuildingRegistry.CaptureOrDestroyIfUndefended(_hex, _owner, null, _army);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building), "Hidden arrival must stay passive.");
            Assert.That(_building.Owner, Is.SameAs(_enemy));

            StealthSystem.ExitStealth(_unit);

            Assert.That(_unit.IsHidden, Is.False);
            if (isBase)
            {
                Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
                Assert.That(_building.Owner, Is.SameAs(_owner));
            }
            else
                Assert.That(BuildingRegistry.FindAt(_hex), Is.Null);
        }

        // Project owner, 2026-10-22: destroying a structure is the army's action for the turn — its
        // remaining movement ends on the ruin. A captured Base keeps the ordinary arrival rules.
        [TestCase(false)]
        [TestCase(true)]
        public void DestroyingAStructureEndsTheArmysMovement(bool isBase)
        {
            _building.IsBase = isBase;
            _unit.MoveMax = 3;
            _unit.MoveCurrent = 2;

            StealthSystem.ExitStealth(_unit);

            Assert.That(_unit.MoveCurrent, Is.EqualTo(isBase ? 2 : 0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void VisibleGuardPreventsTakeoverOnReveal(bool hero)
        {
            _unit.IsHero = hero;
            var guard = new ArmyData { Owner = _enemy, Hex = _hex };
            guard.Members.Add(new UnitData { Owner = _enemy, HitPointsCurrent = 5 });
            ArmyRegistry.Register(guard);
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
            Assert.That(_building.Owner, Is.SameAs(_enemy));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResidentExitRequestsArrivalContactOnceAfterReveal(bool hero)
        {
            _unit.IsHero = hero;
            var guard = new ArmyData { Owner = _enemy, Hex = _hex };
            guard.Members.Add(new UnitData { Owner = _enemy, HitPointsCurrent = 5 });
            ArmyRegistry.Register(guard);
            int requests = 0;
            void OnRevealed(ArmyData army)
            {
                requests++;
                Assert.That(army, Is.SameAs(_army));
                Assert.That(_unit.IsHidden, Is.False);
                Assert.That(Game.Combat.BattleInitiator.FindDefenderOnArrival(_hex, army), Is.SameAs(guard));
            }
            StealthSystem.ResidentRevealed += OnRevealed;
            try
            {
                StealthSystem.ExitStealth(_unit);
                StealthSystem.ExitStealth(_unit);
                Assert.That(requests, Is.EqualTo(1));
            }
            finally { StealthSystem.ResidentRevealed -= OnRevealed; }
        }

        [Test]
        public void BattleRevealDetectionAndRemovalDoNotRequestAnotherArrival()
        {
            int requests = 0;
            void OnRevealed(ArmyData _) => requests++;
            StealthSystem.ResidentRevealed += OnRevealed;
            try
            {
                StealthSystem.MarkDetected(_unit, _enemy);
                StealthSystem.RevealArmy(_army);
                _unit.IsHidden = true;
                StealthSystem.RevealForBattle(new[] { _army }, _hex);
                _unit.IsHidden = true;
                StealthSystem.OnUnitRemoved(_unit);
                Assert.That(requests, Is.Zero);
            }
            finally { StealthSystem.ResidentRevealed -= OnRevealed; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AviationExitDoesNotRequestGroundArrival(bool airfield)
        {
            _unit.IsAviation = true;
            _army.IsAirfield = airfield;
            int requests = 0;
            void OnRevealed(ArmyData _) => requests++;
            StealthSystem.ResidentRevealed += OnRevealed;
            try
            {
                StealthSystem.ExitStealth(_unit);
                Assert.That(requests, Is.Zero);
            }
            finally { StealthSystem.ResidentRevealed -= OnRevealed; }
        }

        // 2026-10-18 (project owner's decision) — a Base's own garrison defends it even while its
        // members are in stealth: the arriving visible army meets it (the reveal happens at the
        // battle's start) instead of taking the Base with the garrison still inside. Route planning
        // stays blind to it. An ordinary hidden resident still does not hold the building.
        [Test]
        public void HiddenGarrisonHoldsItsBase_OrdinaryHiddenResidentDoesNot()
        {
            _unit.IsHidden = false;
            _building.IsBase = true;
            var garrison = new ArmyData { Owner = _enemy, Hex = _hex, IsGarrison = true };
            garrison.Members.Add(new UnitData { Owner = _enemy, IsHidden = true, HitPointsCurrent = 5 });
            ArmyRegistry.Register(garrison);

            BuildingRegistry.CaptureOrDestroyIfUndefended(_hex, _owner, null, _army);
            Assert.That(_building.Owner, Is.SameAs(_enemy), "the hidden garrison holds the Base");
            Assert.That(Game.Combat.BattleInitiator.FindEnemyAt(_hex, _army), Is.Null,
                "planning never sees the hidden garrison");
            Assert.That(Game.Combat.BattleInitiator.FindDefenderOnArrival(_hex, _army), Is.SameAs(garrison),
                "the arrival meets it");

            garrison.IsGarrison = false;
            Assert.That(Game.Combat.BattleInitiator.FindDefenderOnArrival(_hex, _army), Is.Null);
            BuildingRegistry.CaptureOrDestroyIfUndefended(_hex, _owner, null, _army);
            Assert.That(_building.Owner, Is.SameAs(_owner), "an ordinary hidden resident does not hold it");
        }

        [Test]
        public void HiddenGarrisonKeepsTheBaseWhenAnotherDefenderFalls()
        {
            _unit.IsHidden = false;
            _building.IsBase = true;
            var garrison = new ArmyData { Owner = _enemy, Hex = _hex, IsGarrison = true };
            garrison.Members.Add(new UnitData { Owner = _enemy, IsHidden = true, HitPointsCurrent = 5 });
            ArmyRegistry.Register(garrison);
            var beaten = new ArmyData { Owner = _enemy, Hex = _hex };
            ArmyRegistry.Register(beaten);

            Game.Combat.BattleEngine.HandleBuildingOnArmyDefeat(_army, beaten, null);
            Assert.That(_building.Owner, Is.SameAs(_enemy));
        }

        // Project owner, 2026-11-06: a fully hidden army (scout, hero) detected by the army sharing
        // its hex must not lock that army in place as "in combat" — it is attacked only by an
        // explicit order. A visible member, or a building's own garrison, still holds it.
        [Test]
        public void DetectedFullyHiddenArmyDoesNotHoldTheDetector()
        {
            BuildingRegistry.Clear();
            var hero = new UnitData { Owner = _owner, IsHero = true, IsHidden = true, HitPointsCurrent = 5 };
            _army.Members.Add(hero);
            var detector = new ArmyData { Owner = _enemy, Hex = _hex };
            detector.Members.Add(new UnitData { Owner = _enemy, HitPointsCurrent = 5 });
            ArmyRegistry.Register(detector);

            Assert.That(Game.Combat.BattleInitiator.IsHeldByContact(detector), Is.False, "undetected");
            StealthSystem.MarkDetected(_unit, _enemy);
            StealthSystem.MarkDetected(hero, _enemy);
            Assert.That(Game.Combat.BattleInitiator.FindEnemyAt(_hex, _enemy), Is.SameAs(_army),
                "the detector does see it");
            Assert.That(Game.Combat.BattleInitiator.IsHeldByContact(detector), Is.False,
                "a detected fully hidden army is no contact");

            _army.IsGarrison = true;
            BuildingRegistry.Register(_hex, new BuildingData { Owner = _owner, Hex = _hex, IsBase = true });
            Assert.That(Game.Combat.BattleInitiator.IsHeldByContact(detector), Is.True,
                "a building's own garrison still holds");

            _army.IsGarrison = false;
            hero.IsHidden = false;
            Assert.That(Game.Combat.BattleInitiator.IsHeldByContact(detector), Is.True,
                "a visible member makes the army a contact");
        }

        // Project owner, 2026-11-09 (match P4): a detected enemy on the hex of the player's own
        // building is fought at the start of that player's turn; the strongest of the player's
        // armies there initiates. Undetected, nothing happens; off a building, nobody is held.
        // Scene: Grit Wolves (fully hidden) on Brynn's Citadel beside her garrison, T10-T14.
        [Test]
        public void DetectedEnemyOnOwnBuildingIsFoughtByTheStrongestArmyOfTheHex()
        {
            // _army (owner, fully hidden) stands on _enemy's building: _enemy is the building owner.
            UnitData Body(int attack) => new UnitData { Owner = _enemy, Attack = attack, Defense = 2,
                HitPointsCurrent = 8, HitPointsMax = 8, MoveMax = 3, MoveCurrent = 3 };
            var garrison = new ArmyData { Owner = _enemy, Hex = _hex, IsGarrison = true };
            garrison.Members.Add(Body(1));
            var field = new ArmyData { Owner = _enemy, Hex = _hex };
            field.Members.Add(Body(9)); field.Members.Add(Body(9)); field.Members.Add(Body(9));
            ArmyRegistry.Register(garrison);
            ArmyRegistry.Register(field);
            _unit.Attack = 2; _unit.Defense = 1; _unit.HitPointsMax = 5;

            Assert.That(Game.Combat.BattleInitiator.TryFindOwnBuildingContact(_enemy, null, out _, out _),
                Is.False, "an undetected hidden army is not a contact");

            StealthSystem.MarkDetected(_unit, _enemy);
            Assert.That(Game.Combat.BattleInitiator.TryFindOwnBuildingContact(_enemy, null,
                out HexCoord hex, out List<ArmyData> participants), Is.True);
            Assert.That(hex, Is.EqualTo(_hex));
            Assert.That(participants, Is.EqualTo(new[] { field, _army }),
                "the strongest army of the hex initiates against the detected enemy");
            Assert.That(Game.Combat.BattleInitiator.TryFindOwnBuildingContact(_enemy,
                new HashSet<(int, int)> { (field.Id, _army.Id) }, out _, out _), Is.False,
                "a pairing already fought this turn start is not repeated");
            Assert.That(Game.Combat.BattleInitiator.TryFindOwnBuildingContact(_owner, null, out _, out _),
                Is.False, "only the building owner's turn start fights it");

            BuildingRegistry.Clear();
            Assert.That(Game.Combat.BattleInitiator.TryFindOwnBuildingContact(_enemy, null, out _, out _),
                Is.False, "off a building nothing changes");
            Assert.That(Game.Combat.BattleInitiator.IsHeldByContact(field), Is.False,
                "and a detected fully hidden army still holds nobody");
        }

        [Test]
        public void FirstVisibleMemberActsWhileOtherMembersStayHidden()
        {
            var other = new UnitData { Owner = _owner, IsHidden = true };
            _army.Members.Add(other);
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.Null);
            Assert.That(other.IsHidden, Is.True);
        }

        [Test]
        public void PersonalDetectionDoesNotMakeHiddenResidentAct()
        {
            StealthSystem.MarkDetected(_unit, _enemy);
            BuildingRegistry.CaptureOrDestroyIfUndefended(_hex, _owner, null, _army);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
            Assert.That(_unit.IsHidden, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AviationRevealDoesNotTakeOver(bool airfield)
        {
            _unit.IsAviation = true;
            _army.IsAirfield = airfield;
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
        }

        [Test]
        public void OwnBuildingSurvivesReveal()
        {
            _building.Owner = _owner;
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
        }

        [Test]
        public void RemovedUnitDoesNotAttackBuilding()
        {
            StealthSystem.OnUnitRemoved(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
            Assert.That(_unit.IsHidden, Is.False);
        }

        [Test]
        public void UnregisteredArmyDoesNotTakeOverOnReveal()
        {
            ArmyRegistry.Unregister(_army);
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
        }

        [Test]
        public void RepeatedExitDoesNotDestroyReplacementBuilding()
        {
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.Null);
            BuildingRegistry.Register(_hex, _building);
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
        }

        // Playtest 2026-10-22 (Ysolde T17/T19): a Hooded scout hidden on the neutral Sand Fin's hex
        // revealed there to sabotage the next hex and was wiped on its own hex. A resident reveal
        // meets a known army sharing the hex; only that, never the structure, is the contact.
        [Test]
        public void ResidentRevealContact_IsAKnownArmySharingTheHex()
        {
            Game.Ai.AiMapMemory.Clear();
            Game.Ai.AiMapMemory.EnsureSubscribed();
            GameSession.Players = new List<PlayerSetupData> { _owner, _enemy };
            var neutral = new PlayerSetupData { IsNeutral = true };
            try
            {
                VisionSystem.NotifyContentChanged(_hex);
                Assert.That(Game.Ai.AiMapMemory.KnownResidentRevealContact(_owner, _army), Is.False,
                    "the undefended structure is the reveal's own action, not a contact");

                var sandFin = new ArmyData { Owner = neutral, Hex = _hex, Name = "Sand Fin" };
                sandFin.Members.Add(new UnitData { Owner = neutral, HitPointsCurrent = 5 });
                ArmyRegistry.Register(sandFin);
                VisionSystem.NotifyContentChanged(_hex);
                Assert.That(Game.Ai.AiMapMemory.KnownResidentRevealContact(_owner, _army), Is.True);

                _unit.IsHidden = false;
                Assert.That(Game.Ai.AiMapMemory.KnownResidentRevealContact(_owner, _army), Is.False,
                    "a visible army has no reveal left to make");
            }
            finally
            {
                Game.Ai.AiMapMemory.Clear();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RevealUpdatesBuildingMemoryAndRouteRevisionBeforeFormerOwnerLosesVision(bool isBase)
        {
            Game.Ai.AiMapMemory.Clear();
            Game.Ai.AiMapMemory.EnsureSubscribed();
            GameSession.Players = new List<PlayerSetupData> { _owner, _enemy };
            _building.IsBase = isBase;
            VisionSystem.NotifyContentChanged(_hex);
            try
            {
                Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_owner, _hex)?.Owner, Is.SameAs(_enemy));
                long routeBefore = Game.Ai.AiMapMemory.RouteMemoryVersionFor(_owner);
                int knowledgeBefore = Game.Ai.AiMapMemory.KnowledgeVersionFor(_owner);

                StealthSystem.ExitStealth(_unit);

                Assert.That(Game.Ai.AiMapMemory.RouteMemoryVersionFor(_owner), Is.GreaterThan(routeBefore));
                Assert.That(Game.Ai.AiMapMemory.KnowledgeVersionFor(_owner), Is.GreaterThan(knowledgeBefore));
                Assert.That(Game.Ai.AiMapMemory.KnownGroundArrival(_owner, _army, _hex).HasOutcome, Is.False);
                Assert.That(VisionSystem.IsVisible(_enemy, _hex), Is.False);
                if (isBase)
                {
                    Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_owner, _hex)?.Owner, Is.SameAs(_owner));
                    Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_enemy, _hex)?.Owner, Is.SameAs(_owner),
                        "Capture must be remembered before the former owner's vision disappears.");
                }
                else
                {
                    Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_owner, _hex).HasValue, Is.False);
                    Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_enemy, _hex).HasValue, Is.False,
                        "Observed destruction must not leave a stale structure under fog.");
                }
            }
            finally
            {
                Game.Ai.AiMapMemory.Clear();
            }
        }
    }
}
#endif
