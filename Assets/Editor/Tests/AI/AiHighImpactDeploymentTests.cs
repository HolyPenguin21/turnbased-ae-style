#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.Ai;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiHighImpactDeploymentTests
    {
        private static MaterializationPlan Heavy(int cost = 7) => new MaterializationPlan
        {
            Kind = MaterializationChainKind.Direct, FinalCapability = CapabilityKind.FieldCombatPower,
            BaseCardInHand = new CardData(new CardDefinition { cardType = CardType.Unit,
                displayName = "Heavy", authoredKey = "heavy", apCost = cost,
                attack = 12, defenseRating = 8, hitPoints = 12, moveMax = 3 }),
            ApCost = cost,
            Deploy = new PlacementOption(new HexCoord(0, 0), DeploymentKind.NewArmy, null),
        };
        private static WorldSnapshot World() => new WorldSnapshot { Self = new SelfSnapshot {
            Armies = Array.Empty<ArmySnapshot>(), StrikeRoster = new[] {
                new StrikeRosterSlot("heavy", false, 1, ForceSource.Hand) } } };

        [Test]
        public void OpeningRequiresRoomForBothDeploymentAndTheFirstActivation()
        {
            var plan = Heavy();
            Assert.That(HighImpactDeployment.IsCandidate(World(), plan, 1f, 8f, 2f), Is.False);
            Assert.That(HighImpactDeployment.IsCandidate(World(), plan, 1f, 9f, 2f), Is.True,
                "an initiative bonus may make the full action affordable");
        }
        [Test]
        public void ExpensiveWeakOrAlreadyMatchedBodyDoesNotGetOpeningPriority()
        {
            var snap = World(); var plan = Heavy();
            snap.Self.Armies = new[] { new ArmySnapshot { Members = new[] {
                AiPower.ToDefenderProfile(plan.BaseCardInHand.Definition) } } };
            Assert.That(HighImpactDeployment.IsCandidate(snap, plan, 1f, 20, 0), Is.False);
        }
        [Test]
        public void OpeningUsesOnlyPositiveDirectHeavyFieldPlays()
        {
            var snap = World(); var plan = Heavy(6);
            Assert.That(HighImpactDeployment.IsCandidate(snap, plan, 2f, 20, 0), Is.False);
            plan = Heavy();
            Assert.That(HighImpactDeployment.IsCandidate(snap, plan, 0f, 20, 0), Is.False);
            plan.Deploy = new PlacementOption(new HexCoord(0, 0), DeploymentKind.Garrison, null);
            Assert.That(HighImpactDeployment.IsCandidate(snap, plan, 2f, 20, 0), Is.False);
            plan = Heavy(); plan.Kind = MaterializationChainKind.GenerateDeploy;
            Assert.That(HighImpactDeployment.IsCandidate(snap, plan, 2f, 20, 0), Is.False);
        }
        [Test]
        public void FirstActivationIsNotReservedTwiceWhenTheRecipientAlreadyHasProtection()
        {
            Assert.That(HighImpactDeployment.UnprotectedFollowupAp(3, 2), Is.EqualTo(1));
            Assert.That(HighImpactDeployment.UnprotectedFollowupAp(3, 0, 2), Is.EqualTo(1));
            Assert.That(HighImpactDeployment.UnprotectedFollowupAp(3, 3), Is.Zero);
            Assert.That(HighImpactDeployment.UnprotectedFollowupAp(3, 0), Is.EqualTo(3));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void OpeningProtectsANewOnSiteConstruction_ButDoesNotInventZeroRadarReservations(bool zeroRadar)
        {
            var player = new PlayerSetupData();
            var site = new HexCoord(0, 0);
            var build = new AxisDemand { RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicInfrastructure, DesiredAmount = 1,
                TargetHex = site, EconomyResourceType = ResourceType.Materials,
                EconomyBuildResourceCost = new ResourceCost(materials: 3),
                EconomyBuilderRoutes = new[] { new EconomyBuilderRouteSnapshot {
                    ArmyId = 7, IsOnTarget = true, MaxMovement = 3 } } };
            var snapshot = new WorldSnapshot { Observer = player, TurnNumber = 10,
                Self = new SelfSnapshot { Armies = new[] { new ArmySnapshot { ArmyId = 7,
                    Owner = player, Hex = site, HasHero = true, IsMobileEconomyBuilder = true } } } };
            var radar = Radar.Even();
            if (zeroRadar) radar.Weight[DesireAxis.Economy] = 0;
            var result = new StrategicPhaseResult { Reservation = new MaterializationReservation() };
            var pass = new PhaseAPass(snapshot, player, null, new AiHandData(null, Faction.None, 0),
                new AiTurnContext { TurnNumber = 10 }, PhaseAApBudget.Create(null), new[] { build },
                new ActorCommitments(), MissionIntentRegistry.GetOrCreate(player),
                Array.Empty<MissionIntent>(), null, null, radar, true, result);
            StrategicResourceReservationLedger.BeginTurn(player, 10);
            try
            {
                HighImpactDeployment.PrepareConstructionClaims(pass);
                Assert.That(StrategicResourceReservationLedger.Active(player, 10,
                    StrategicReservedResource.Materials), Is.EqualTo(zeroRadar ? 0f : 3f));
            }
            finally { StrategicResourceReservationLedger.ClearAll(); MissionIntentRegistry.Clear(); }
        }
        [Test]
        public void CriticalDefenceKeepsPriorityOverAnOpeningHeavyUnit()
        {
            var snap = World(); snap.Threat = new ThreatModel { UnderSiege = true };
            Assert.That(HighImpactDeployment.HasCriticalDefence(snap, Array.Empty<AxisDemand>()), Is.True);
            snap.Threat.UnderSiege = false;
            Assert.That(HighImpactDeployment.HasCriticalDefence(snap, new[] { new AxisDemand {
                ConsumerMissionKind = MissionKind.ActiveDefence, ActiveDefenceEnemyEta = 1 } }), Is.True);
            Assert.That(HighImpactDeployment.HasCriticalDefence(snap, Array.Empty<AxisDemand>()), Is.False);
        }
    }
}
#endif
