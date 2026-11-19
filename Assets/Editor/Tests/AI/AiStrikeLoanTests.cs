#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-11-19 (owner; log 2026-11-19 Halden T7-T12) — an army walking a routine return leg is
    // lent to an ActiveDefence immediate strike it can start now (MissionContinuityLayer
    // .StrikeLoan); an outbound build whose own site an enemy holds strikes first instead of being
    // abandoned; the immediate strike is worth the own structure the contact would take.
    public class AiStrikeLoanTests
    {
        private const int Actor = 21;
        private const int Enemy = 77;
        private const int Turn = 8;
        private static readonly HexCoord Home = new HexCoord(3, 1);
        private static readonly HexCoord ArmyHex = new HexCoord(0, 0);
        private static readonly HexCoord Near = new HexCoord(-2, 0);
        private static readonly HexCoord Far = new HexCoord(-6, 0);

        [TearDown]
        public void TearDown()
        {
            MissionIntentRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
            StrategicResourceReservationLedger.ClearAll();
            LifecycleReturnPolicy.ClearAll();
        }

        // The escort of a finished build: a hero with one combat body, free to walk home.
        private static ArmySnapshot Escort(PlayerSetupData owner, HexCoord hex, int mp = 3) =>
            new ArmySnapshot { Owner = owner, ArmyId = Actor, Hex = hex, MemberCount = 2,
                HasHero = true, IsStructuralRaidActor = true,
                CurrentMovement = mp, MaxMovement = 3, ActivationApCost = 1,
                EffectiveArmyPower = 24f, ReachableOwnBaseHexes = new[] { Home },
                EconomyHomeRouteCosts = new Dictionary<HexCoord, int> { [Home] = 3 },
                Members = new[] { new WorthIt.DefenderProfile(7f, false, null, 7f, 10f, 3) } };

        // A weak visible scout (one ground body) at `enemyHex`, our only base at Home.
        private static WorldSnapshot World(PlayerSetupData owner, HexCoord enemyHex, params ArmySnapshot[] armies)
        {
            var enemy = new PlayerSetupData { Nickname = "scout-owner" };
            var body = new WorthIt.DefenderProfile(1f, false, null, 1f, 2f, 3);
            var contact = new EnemyContactSnapshot
            {
                Army = new ArmySnapshot { ArmyId = Enemy, Owner = enemy, EffectiveArmyPower = 3f,
                    Members = new[] { body }, MemberCount = 1, MaxMovement = 3 },
                Knowledge = ContactKnowledge.Exact, Position = enemyHex, LastObservedTurn = Turn,
                Confidence = 1f,
            };
            return new WorldSnapshot
            {
                Observer = owner, TurnNumber = Turn,
                Self = new SelfSnapshot { Citadel = Home, BaseHexes = new[] { Home }, Armies = armies },
                Known = new KnownSnapshot { EnemySightings = new[] { new AiMapMemory.KnownEnemySighting(
                    enemyHex, enemy, "scout", 1, 1f, 1f, new List<WorthIt.DefenderProfile> { body },
                    false, 0, 0, Turn, Enemy) } },
                Threat = new ThreatModel { Contacts = new[] { contact }, Threats = new AssetThreatSnapshot[0] },
            };
        }

        private static MissionIntent ReturnBuilder(PlayerSetupData player)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy, Funding = CommitmentTier.Hard, Status = IntentStatus.Active,
                PreferredMoverArmyId = Actor, CreatedTurn = Turn - 1, TurnsActive = 1,
                LastReconciledTurn = Turn - 1, LastProgressTurn = Turn - 1,
                Objective = new EconomyIntent { Kind = EconomyTaskKind.ReturnBuilder, TargetHex = Home,
                    BuilderArmyId = Actor },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            MissionIntentRegistry.GetOrCreate(player).Put(intent);
            return intent;
        }

        private static MissionIntent ImmediateStrike(int actorId)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.ActiveDefence, Status = IntentStatus.Active, Funding = CommitmentTier.Hard,
                PreferredMoverArmyId = actorId,
                Objective = new ActiveDefenceIntent { Phase = ActiveDefencePhase.Intercept,
                    InterceptPurpose = ActiveDefenceInterceptPurpose.ImmediateOpportunity,
                    ImmediateTurn = Turn, EnemyArmyId = Enemy, PrimaryArmyId = actorId, LastKnownHex = Near },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            return intent;
        }

        private static MissionContinuityLayer.ActiveResolution Pass(PlayerSetupData player, WorldSnapshot snap,
            params MissionIntent[] active)
        {
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            var pass = new MissionContinuityLayer.ActiveResolution(state, null, null);
            pass.ActorClaims = ActorCommitments.FromIntents(state.All.Where(i => i.Status == IntentStatus.Active),
                snap, null).ClaimedArmyIdSet;
            pass.Active.AddRange(active);
            return pass;
        }

        [Test]
        public void RoutineReturnLeg_LendsItsArmy_ToAStrikeItCanStartNow()
        {
            var player = new PlayerSetupData { Nickname = "loan" };
            WorldSnapshot snap = World(player, Near, Escort(player, ArmyHex));
            MissionIntent leg = ReturnBuilder(player);

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);

            Assert.That(leg.Status, Is.EqualTo(IntentStatus.Suspended));
            Assert.That(leg.Suspended, Is.EqualTo(SuspendReason.StrikeLoan));
            Assert.That(active, Has.No.Member(leg));
            ISet<int> committed = ActorCommitments.FromIntents(active, snap, null).ClaimedArmyIdSet;
            Assert.That(committed, Has.No.Member(Actor), "the planner sees a free army");
            ActiveDefenceResponse strike = ActiveDefenceObjectiveEvaluator.AssessResponse(snap,
                ActiveDefenceObjectiveEvaluator.EnumerateImmediate(snap).Single(), committed, null, null);
            Assert.That(strike.Kind, Is.EqualTo(ActiveDefenceResponseKind.Intercept));
            Assert.That(strike.Plan.BaseArmyId, Is.EqualTo(Actor));
        }

        [TestCase(false, 3, TestName = "RoutineReturnLeg_KeepsWalking_WhenTheScoutIsOutOfReach")]
        [TestCase(true, 0, TestName = "RoutineReturnLeg_KeepsWalking_WithNoMovementLeft")]
        public void RoutineReturnLeg_KeepsWalking(bool near, int mp)
        {
            var player = new PlayerSetupData { Nickname = "walk" };
            WorldSnapshot snap = World(player, near ? Near : Far, Escort(player, ArmyHex, mp));
            MissionIntent leg = ReturnBuilder(player);

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);

            Assert.That(leg.Status, Is.EqualTo(IntentStatus.Active));
            Assert.That(active, Does.Contain(leg));
        }

        [Test]
        public void LentLeg_WaitsForTheStrike_ThenResumes_AndIsNotLentTwiceInOneTurn()
        {
            var player = new PlayerSetupData { Nickname = "settle" };
            WorldSnapshot snap = World(player, Near, Escort(player, ArmyHex));
            MissionIntent leg = ReturnBuilder(player);
            MissionContinuityLayer.ResolveActive(player, snap);
            Assert.That(MissionContinuityLayer.IsStrikeLoan(leg), Is.True, "precondition: lent");

            var holding = Pass(player, snap, ImmediateStrike(Actor));
            MissionContinuityLayer.SettleStrikeLoans(player, snap, holding);
            Assert.That(MissionContinuityLayer.IsStrikeLoan(leg), Is.True, "the strike still owns the army");
            Assert.That(holding.Active, Has.No.Member(leg));

            // The planner chose another defender (or the strike is over): the walk goes on, and the
            // same turn never lends it again.
            var free = Pass(player, snap);
            MissionContinuityLayer.SettleStrikeLoans(player, snap, free);
            Assert.That(leg.Status, Is.EqualTo(IntentStatus.Active));
            Assert.That(free.Active, Does.Contain(leg));
        }

        [Test]
        public void LentLeg_IsRetired_WhenAnotherOperationTookItsArmy()
        {
            var player = new PlayerSetupData { Nickname = "taken" };
            WorldSnapshot snap = World(player, Near, Escort(player, ArmyHex));
            MissionIntent leg = ReturnBuilder(player);
            MissionContinuityLayer.ResolveActive(player, snap);
            Assert.That(MissionContinuityLayer.IsStrikeLoan(leg), Is.True, "precondition: lent");

            MissionIntent strategic = ImmediateStrike(Actor);
            strategic.ActiveDefence.InterceptPurpose = ActiveDefenceInterceptPurpose.StrategicDefence;
            var pass = Pass(player, snap, strategic);
            MissionContinuityLayer.SettleStrikeLoans(player, snap, pass);

            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(leg.IntentKey, out _), Is.False,
                "a longer task owns the army now; the walk home is over, never suspended for good");
        }

        [Test]
        public void LentLeg_DoesNotAgeWhileTheStrikeHoldsItsArmy()
        {
            var player = new PlayerSetupData { Nickname = "age" };
            WorldSnapshot snap = World(player, Near, Escort(player, ArmyHex));
            MissionIntent leg = ReturnBuilder(player);
            MissionContinuityLayer.ResolveActive(player, snap);
            int stall = leg.StallTurns;

            MissionContinuityLayer.ReconcileAfterTurn(player, Turn, new MissionStepResult[0]);

            Assert.That(leg.StallTurns, Is.EqualTo(stall));
        }

        [Test]
        public void OutboundBuild_WithAnEnemyOnItsSite_LendsItsArmyInsteadOfEnding()
        {
            var player = new PlayerSetupData { Nickname = "site" };
            WorldSnapshot snap = World(player, Near, Escort(player, ArmyHex));
            snap.Economy = new EconomyStanding();
            var build = new MissionIntent
            {
                Kind = MissionKind.Economy, Funding = CommitmentTier.Soft, Status = IntentStatus.Active,
                PreferredMoverArmyId = Actor, CreatedTurn = Turn - 1, TurnsActive = 1,
                LastReconciledTurn = Turn - 1, LastProgressTurn = Turn - 1,
                Objective = new EconomyIntent { Kind = EconomyTaskKind.BuildExtraction, TargetHex = Near,
                    ResourceType = ResourceType.Materials, BuilderArmyId = Actor },
            };
            build.IntentKey = MissionIntentKey.For(build);
            MissionIntentRegistry.GetOrCreate(player).Put(build);

            MissionContinuityLayer.ResolveActive(player, snap);

            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(build.IntentKey, out _), Is.True,
                "the build keeps its site while its army strikes the enemy standing on it");
            Assert.That(MissionContinuityLayer.IsStrikeLoan(build), Is.True);
        }

        [TestCase(1, true, TestName = "Immediate_SaboteurBesideAnOwnFacility_IsWorthTheFacility")]
        [TestCase(2, false, TestName = "Immediate_ContactTwoTurnsFromTheFacility_IsAStrayContact")]
        public void Immediate_ThreatenedOwnStructure_RaisesTheStrikeValue(int enemyEta, bool counted)
        {
            var player = new PlayerSetupData { Nickname = "value" };
            WorldSnapshot snap = World(player, Near, Escort(player, ArmyHex));
            TaskScore stray = ActiveDefenceObjectiveEvaluator.EnumerateImmediate(snap).Single().TaskScore;

            var facility = new StrategicAssetSnapshot { Hex = new HexCoord(-3, 1), Kind = AssetKind.Facility,
                Value = AiConfigV2.assetValueFacilityBase + AiConfigV2.assetValueFacilityCollectorBonus };
            snap.Threat.Threats = new[] { new AssetThreatSnapshot { Asset = facility,
                Contact = snap.Threat.Contacts[0], CanDamage = true, EnemyEta = enemyEta,
                PotentialDamage = 1f, AttackWinChance = 1f, Confidence = 1f, Severity = 1f } };
            TaskScore score = ActiveDefenceObjectiveEvaluator.EnumerateImmediate(snap).Single().TaskScore;

            if (!counted)
            {
                Assert.That(score.Value, Is.EqualTo(stray.Value));
                return;
            }
            ActiveDefenceObjectiveEvaluator.AssetDefenceTerms(snap.Threat.Threats[0],
                out float relevance, out _, out float prevented);
            Assert.That(score.StrategicRelevance, Is.EqualTo(relevance));
            Assert.That(score.PreventedDamage, Is.EqualTo(System.Math.Max(prevented, stray.PreventedDamage)));
            Assert.That(score.Value, Is.GreaterThan(stray.Value));
        }
    }
}
#endif
