#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiV2ArchitecturalRegressionTests
    {
        [SetUp]
        public void SetUp()
        {
            ResourceStarvationRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
        }

        [Test]
        public void ReconConcurrency_HasZeroLaneAndFrontierCannotBypassCanonicalValue()
        {
            var snap = new WorldSnapshot { MapKnowledge = new MapKnowledgeSnapshot
            {
                ExplorableUnknownFrac = 1f,
                Frontier = new[]
                {
                    new FrontierHexSnapshot { Hex = new HexCoord(0, 0) },
                    new FrontierHexSnapshot { Hex = new HexCoord(20, 20) },
                },
            } };
            Assert.That(ReconConcurrencyPolicy.DesiredTotal(snap, new[] { Recon(0f) }), Is.Zero);
            Assert.That(ReconConcurrencyPolicy.DesiredTotal(snap,
                new[] { Recon(12f), Recon(AiConfigV2.taskScoreUrgencyRampLo) }), Is.EqualTo(1));
        }

        [Test]
        public void ReconConcurrency_ValuableLateRefreshCanReactivateOneLane()
        {
            var snap = new WorldSnapshot { MapKnowledge = new MapKnowledgeSnapshot
            {
                ExplorableUnknownFrac = 0f,
                Frontier = new List<FrontierHexSnapshot>(),
            } };
            ReconObjective refresh = Recon(12f);
            refresh.Kind = ReconObjectiveKind.Refresh;
            Assert.That(ReconConcurrencyPolicy.DesiredTotal(snap, new[] { refresh }), Is.EqualTo(1));
        }

        [Test]
        public void MissionDeferralContract_EconomyPlannerPublishesCanonicalReason()
        {
            var player = new PlayerSetupData { Nickname = "DeferralContract" };
            var actor = new ArmySnapshot
            {
                ArmyId = 7,
                Owner = player,
                Hex = new HexCoord(0, 0),
                CurrentMovement = 0,
                MaxMovement = 2,
            };
            var snap = new WorldSnapshot
            {
                Observer = player,
                Self = new SelfSnapshot
                {
                    Armies = new[] { actor },
                    BaseHexes = new List<HexCoord>(),
                },
            };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy,
                Funding = CommitmentTier.Hard,
                Status = IntentStatus.Active,
                PreferredMoverArmyId = 7,
                Objective = new EconomyIntent
                {
                    Kind = EconomyTaskKind.ReturnBuilder,
                    TargetHex = new HexCoord(2, 0),
                    BuilderArmyId = 7,
                },
            };
            intent.IntentKey = MissionIntentKey.For(intent);

            var deferrals = new Dictionary<MissionIntentKey, string>();
            List<MissionProposal> missions = EconomyMissionPlanner.Propose(
                snap, new DesireBreakdown(), new[] { intent }, new List<AxisDemand>(), deferrals);

            Assert.That(missions.Exists(m => MissionIntentKey.For(m).Equals(intent.IntentKey)), Is.False);
            Assert.That(deferrals.TryGetValue(intent.IntentKey, out string reason), Is.True);
            Assert.That(reason, Is.EqualTo("actor_no_movement_this_cycle"));
            Assert.That(MissionContinuityLayer.BindFunding(
                new[] { intent }, missions, snap, deferrals), Is.Empty);
        }

        [Test]
        public void StarvationPressure_OneResourceTurnAddsOneHitAndKeepsStrongestEvidence()
        {
            var player = new PlayerSetupData { Nickname = "StarvationIdempotence" };
            ResourceStarvationRegistry.DecayOncePerTurn(player, 7);
            ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Energy,
                3f, 1f, 0f, 4f, 7);
            ResourceStarvationRegistry.DecayOncePerTurn(player, 8);
            float before = ResourceStarvationRegistry.Pressure(player, ResourceType.Energy);
            ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Energy,
                5f, 1f, 0f, 6f, 8);
            ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Energy,
                9f, 1f, 0f, 11f, 8);
            ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Energy,
                7f, 1f, 0f, 8f, 8);

            Assert.That(ResourceStarvationRegistry.Pressure(player, ResourceType.Energy),
                Is.EqualTo(before + AiConfigV2.starvationHitGain).Within(0.0001f));
            Assert.That(ResourceStarvationRegistry.TryGetCurrentBlock(player, ResourceType.Energy,
                8, out ResourceBlockEvidence evidence), Is.True);
            Assert.That(evidence.DemandValue, Is.EqualTo(11f));
            Assert.That(evidence.Required, Is.EqualTo(9f));
        }

        [Test]
        public void GroundCombatAdmission_DistinctAssignmentOverridesSharedPlanningWitness()
        {
            var owner = new PlayerSetupData { Nickname = "Enemy", ColorIndex = 2 };
            MissionProposal raid = Raid(101, 1);
            MissionProposal attack = Attack(owner, new HexCoord(5, 0), 1);
            GroundCombatAdmissionRegistry.RecordEligibleForTest(raid, new[] { 1, 2 });
            GroundCombatAdmissionRegistry.RecordEligibleForTest(attack, new[] { 1, 2 });
            Assert.That(MissionAdmissionPolicy.Conflicts(raid, attack), Is.False);
        }

        [Test]
        public void GroundCombatAdmission_TwoRaidsUseProvenAlternativeActors()
        {
            MissionProposal a = Raid(101, 1);
            MissionProposal b = Raid(102, 1);
            GroundCombatAdmissionRegistry.RecordEligibleForTest(a, new[] { 1, 2 });
            GroundCombatAdmissionRegistry.RecordEligibleForTest(b, new[] { 1, 2 });

            Assert.That(MissionAdmissionPolicy.Conflicts(a, b), Is.False);
        }

        [Test]
        public void GroundCombatAdmission_TwoAttacksUseProvenAlternativeActors()
        {
            var owner = new PlayerSetupData { Nickname = "Enemy", ColorIndex = 2 };
            MissionProposal a = Attack(owner, new HexCoord(5, 0), 1);
            MissionProposal b = Attack(owner, new HexCoord(6, 0), 1);
            GroundCombatAdmissionRegistry.RecordEligibleForTest(a, new[] { 1, 2 });
            GroundCombatAdmissionRegistry.RecordEligibleForTest(b, new[] { 1, 2 });

            Assert.That(MissionAdmissionPolicy.Conflicts(a, b), Is.False);
        }

        [Test]
        public void GroundCombatAdmission_StillConflictsWithoutDistinctAssignment()
        {
            var owner = new PlayerSetupData { Nickname = "Enemy", ColorIndex = 2 };
            MissionProposal a = Attack(owner, new HexCoord(5, 0), 1);
            MissionProposal b = Attack(owner, new HexCoord(6, 0), 1);
            GroundCombatAdmissionRegistry.RecordEligibleForTest(a, new[] { 1 });
            GroundCombatAdmissionRegistry.RecordEligibleForTest(b, new[] { 1 });
            Assert.That(MissionAdmissionPolicy.Conflicts(a, b), Is.True);
        }

        [TestCase(11f, 20f, MissionKind.Attack)]
        [TestCase(20f, 11f, MissionKind.Raid)]
        public void CompletedRaidFallback_DoesNotPreemptFreshGlobalAggressionChoice(
            float nextRaidValue, float attackValue, MissionKind expected)
        {
            var player = new PlayerSetupData { Nickname = "FreshAggressionChoice", ColorIndex = 1 };
            var enemy = new PlayerSetupData { Nickname = "Enemy", ColorIndex = 2 };
            MissionProposal fallback = Raid(100, 1);
            fallback.Target = new RaidMissionTarget
            {
                Phase = RaidMissionPhase.Return,
                Target = RaidTargetRef.ForNeutralArmy(100),
                DestinationHex = new HexCoord(0, 0),
            };
            SetValueAndCost(fallback, 0f);

            MissionProposal nextRaid = Raid(101, 1);
            MissionProposal attack = Attack(enemy, new HexCoord(5, 0), 1);
            SetValueAndCost(nextRaid, nextRaidValue);
            SetValueAndCost(attack, attackValue);
            GroundCombatAdmissionRegistry.RecordEligibleForTest(nextRaid, new[] { 1 });
            GroundCombatAdmissionRegistry.RecordEligibleForTest(attack, new[] { 1 });

            var snap = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot { ActionPoints = 1 },
            };
            TentativeAllocation allocation = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { fallback, nextRaid, attack },
                new List<Commitment>(), player).Pack();

            Assert.That(allocation.Funded, Has.Count.EqualTo(1));
            Assert.That(allocation.Funded[0].Mission.Kind, Is.EqualTo(expected));
        }

        [Test]
        public void TacticalAttackSignificance_ExcludesRecceAndCountsMixedCombatBodiesOnly()
        {
            var scout = new WorthIt.DefenderProfile(40f, false, null, 40f, 5f, 1,
                new[] { "r1s4" }, 5f);
            var combat = new WorthIt.DefenderProfile(4f, false,
                new[] { UnitTypeTag.Infantry }, 6f, 10f, 2);
            Assert.That(AttackTacticalOpportunity.RawCombatBodyStrength(new[] { scout }), Is.Zero);
            Assert.That(AttackTacticalOpportunity.RawCombatBodyStrength(new[] { scout, combat }),
                Is.EqualTo(10f));

            var ownScout = new UnitData { Attack = 40, Defense = 40 };
            ownScout.Abilities.Add("r1s4");
            var ownCombat = new UnitData { Attack = 6, Defense = 4 };
            Assert.That(AiArmyRoles.IsGroundCombatBody(ownScout), Is.False);
            Assert.That(AiArmyRoles.IsGroundCombatBody(ownCombat), Is.True);
        }

        [Test]
        public void ExactResourceEvidenceIsReplacedWhenTheDemandDisappears()
        {
            var player = new PlayerSetupData();
            ResourceStarvationRegistry.BeginVerifiedPass(player);
            ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Human, 2f, 1f, 1f, 8f, 4);
            Assert.That(ResourceStarvationRegistry.TryGetCurrentBlock(player, ResourceType.Human, 4, out _), Is.True);
            float pressure = ResourceStarvationRegistry.Pressure(player, ResourceType.Human);
            ResourceStarvationRegistry.BeginVerifiedPass(player);
            Assert.That(ResourceStarvationRegistry.TryGetCurrentBlock(player, ResourceType.Human, 4, out _), Is.False);
            Assert.That(ResourceStarvationRegistry.Pressure(player, ResourceType.Human), Is.EqualTo(pressure),
                "replacing exact evidence must not erase the bounded economic pressure history");
        }

        [TestCase(false, false, false, true)]
        [TestCase(true, false, false, false)]
        [TestCase(false, true, false, false)]
        [TestCase(false, false, true, false)]
        public void ProjectedEconomyBuilderUsesTheSameCandidateContract(
            bool combatFist, bool operatorDuty, bool claimed, bool allowed)
        {
            var player = new PlayerSetupData();
            var source = new ArmySnapshot { ArmyId = 19, Owner = player };
            var projected = new ArmySnapshot
            {
                ArmyId = 19, Owner = player, HasHero = true, IsMobileEconomyBuilder = true,
                IsStructuralRaidActor = combatFist, EffectiveArmyPower = 40f,
                OperatorDutyBlocksDeparture = operatorDuty,
            };
            var snap = new WorldSnapshot { Observer = player, Self = new SelfSnapshot
                { Armies = new[] { source }, FieldStrikePotential = 100f } };
            var commitments = new ActorCommitments();
            if (claimed) commitments.Claim(19);
            var routes = new[] { new EconomyBuilderRouteSnapshot { ArmyId = 19, IsOnTarget = false } };
            Assert.That(System.Linq.Enumerable.Any(EconomyBuilderSelection.EconomyBuilderCandidates(snap,
                new HexCoord(2, 0), routes, System.Array.Empty<MissionIntent>(), commitments,
                projected, 100f)), Is.EqualTo(allowed));
        }

        [Test]
        public void ResourceGainFacilityUsesActualWorkloadAndCountsItsEffectOnce()
        {
            var def = new CardDefinition
            {
                cardType = CardType.Facility,
                grantedAbilities = new List<string> { UnitAbilities.ApBonus },
            };
            var snap = new WorldSnapshot { TurnNumber = 3, Self = new SelfSnapshot
            {
                ActionPoints = 12, ApEconomy = new ApActionEconomySnapshot { BaseActionPoints = 12 },
            } };
            var card = new CardData(def);
            var low = StrategicCardEvaluator.ScoreNonCombat(NonCombatRole.Facility, card,
                snap, null, null, 0f, witnessedUsefulApDemand: 0f);
            var high = StrategicCardEvaluator.ScoreNonCombat(NonCombatRole.Facility, card,
                snap, null, null, 0f, witnessedUsefulApDemand: 30f);
            var effect = StrategicEffectRegistry.Contributions(IntendedRole.ResourceGain,
                def.grantedAbilities, def.moveMax, new EffectEvaluationContext(snap, 30f));
            Assert.That(high.Breakdown.RoleFit, Is.EqualTo(effect.GlobalRoleFit).Within(0.0001f));
            Assert.That(high.Breakdown.RoleFit, Is.GreaterThan(low.Breakdown.RoleFit));
        }

        [TestCase(2f, 7.3f, 0.492857f)]
        [TestCase(8f, 12f, 0.140625f)]
        [TestCase(2f, 5f, 0f)]
        public void ResourcePreservationProtectsNearPurchaseWithoutFreezingRemoteOrLowValueNeeds(
            float required, float value, float expectedPremium)
        {
            var player = new PlayerSetupData();
            var snap = new WorldSnapshot { Observer = player, TurnNumber = 4,
                Self = new SelfSnapshot { Stockpile = new ResourceBundle { Human = 1f },
                    PerTurnIncome = new ResourceBundle { Human = 1f } } };
            var cost = new ResourceCost(human: 1);
            float ordinaryPrice = StrategicCardEvaluator.StrategicResourceCostValue(cost, snap);
            ResourceStarvationRegistry.RecordVerifiedBlock(player, ResourceType.Human,
                required, 1f, 1f, value, 4);
            float protectedPrice = StrategicCardEvaluator.StrategicResourceCostValue(cost, snap, player: player);
            Assert.That(protectedPrice - ordinaryPrice, Is.EqualTo(expectedPremium).Within(0.0001f));
            ResourceStarvationRegistry.BeginVerifiedPass(player);
            Assert.That(StrategicCardEvaluator.StrategicResourceCostValue(cost, snap, player: player),
                Is.EqualTo(ordinaryPrice).Within(0.0001f), "a vanished need must release its opportunity cost");
        }

        private static ReconObjective Recon(float value) => new ReconObjective
        {
            Kind = ReconObjectiveKind.Explore, FocusHex = new HexCoord(1, 1), BaseValue = value,
        };

        private static MissionProposal Raid(int targetId, int preferred) => new MissionProposal
        {
            Kind = MissionKind.Raid, PreferredMoverArmyId = preferred,
            Target = new RaidMissionTarget
            {
                Phase = RaidMissionPhase.Assault,
                Target = RaidTargetRef.ForNeutralArmy(targetId),
            },
        };

        private static MissionProposal Attack(PlayerSetupData owner, HexCoord hex, int preferred) =>
            new MissionProposal
            {
                Kind = MissionKind.Attack, PreferredMoverArmyId = preferred,
                Target = new AttackMissionTarget
                {
                    Phase = AttackMissionPhase.Assault,
                    Target = AttackTargetRef.For(hex, owner, AttackTargetKind.Base),
                },
            };

        private static void SetValueAndCost(MissionProposal proposal, float value)
        {
            proposal.BaseValue = value;
            proposal.LocalAdmissionScore = value;
            proposal.EffectiveValue = value;
            proposal.Requirements = new MissionRequirements
            {
                ApMinimum = 1f,
                ApDesired = 1f,
                ApMaximum = 1f,
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
        }
    }
}
#endif
