#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiDisruptionDeliveryTests
    {
        private static readonly HexCoord Site = new HexCoord(3, 0);
        private PlayerSetupData _player;
        private PlayerSetupData _enemy;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiMapMemory.Clear();
            _player = new PlayerSetupData();
            _enemy = new PlayerSetupData();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiMapMemory.Clear();
        }

        [Test]
        public void DeliverySelectsCarrierRatherThanOrdinaryRecce_AndNeverAnAircraft()
        {
            ArmySnapshot ordinary = Scout(1, false);
            ArmySnapshot carrier = Scout(2, true);
            ArmySnapshot aircraft = Scout(3, true); aircraft.IsAir = true;
            WorldSnapshot snap = Snapshot(ordinary, carrier, aircraft);
            var target = DeliveryTarget();
            Assert.That(ScoutMoverSelector.Eligible(snap, target, null).Select(a => a.ArmyId),
                Is.EqualTo(new[] { 2 }));
            Assert.That(ReconAssignmentPlanner.EvaluateCandidate(null, _player, snap, ordinary, target).Feasible,
                Is.False, "the generic no-map fallback cannot bypass the carrier requirement");
        }

        [Test]
        public void DeliveryEntersExactTargetInsteadOfObservationVantage_AndNeedsStealthBeforeActivation()
        {
            ArmySnapshot carrier = Scout(2, true);
            WorldSnapshot snap = Snapshot(carrier);
            var target = DeliveryTarget();
            Assert.That(ObservationVantageSelector.UsesVantage(snap, target), Is.False);
            var candidates = ReconAssignmentPlanner.BuildCandidates(snap, null, _player, target, null);
            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].ExecutionHex, Is.EqualTo(Site));
            Assert.That(candidates[0].StandOff, Is.Zero);
            carrier.IsHidden = false;
            carrier.HasActivatedThisTurn = true;
            Assert.That(ScoutMoverSelector.Eligible(snap, target, null), Is.Empty,
                "a visible activated scout must not walk into a defended building");
        }

        [Test]
        public void DeployedCarrierKeepsLease_WithoutRebuyingMovement_AndReleasesWhenSiteCaptured()
        {
            ArmySnapshot carrier = Scout(2, true); carrier.Hex = Site; carrier.CurrentMovement = 0;
            WorldSnapshot snap = Snapshot(carrier);
            var intent = new MissionIntent {
                Kind = MissionKind.Scout, Objective = new ScoutIntent {
                    Kind = ScoutTargetKind.DisruptStructure, FocusHex = Site, RequiresStealth = true },
                PreferredMoverArmyId = carrier.ArmyId, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.ForScout(ScoutTargetKind.DisruptStructure, Site),
            };
            MissionIntentRegistry.GetOrCreate(_player).Put(intent);
            Assert.That(ScoutMoverSelector.Eligible(snap, DeliveryTarget(), null), Has.Count.EqualTo(1));
            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(_player, snap,
                Array.Empty<ReconObjective>());
            Assert.That(active, Does.Contain(intent), "support is not trimmed as an idle patrol lane");
            Assert.That(ActorCommitments.FromIntents(active, snap, null).IsArmyClaimed(carrier.ArmyId), Is.True);
            var proposals = ReconMissionPlanner.Propose(snap, new DesireBreakdown(), active,
                Array.Empty<ReconObjective>());
            Assert.That(proposals, Is.Empty, "holding a passive source consumes no recurring movement AP");
            snap.Known.Buildings = new[] { new AiMapMemory.KnownBuilding(Site, _player, true,
                Array.Empty<string>(), isBase: true, defense: 10f) };
            Assert.That(MissionContinuityLayer.ResolveActive(_player, snap, Array.Empty<ReconObjective>()), Is.Empty);
            Assert.That(ActorCommitments.FromIntents(Array.Empty<MissionIntent>(), snap, null)
                .IsArmyClaimed(carrier.ArmyId), Is.False);
        }

        [Test]
        public void DeliveryHasNoPatrolQuotaOrPurchaseDemand_AndCarrierLossEndsRole()
        {
            ArmySnapshot carrier = Scout(2, true);
            WorldSnapshot snap = Snapshot(carrier);
            ReconObjective objective = ReconObjectiveEvaluator.DisruptAt(snap, Site);
            Assert.That(objective, Is.Not.Null);
            Assert.That(objective.IntentKey, Is.EqualTo(DeliveryTarget().IntentKey()));
            Assert.That(ReconConcurrencyPolicy.DesiredTotal(snap, new[] { objective }), Is.Zero);
            var intent = new MissionIntent { Kind = MissionKind.Scout,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.DisruptStructure, FocusHex = Site },
                PreferredMoverArmyId = 2 };
            Assert.That(MissionActorPolicy.HasCapableActor(intent, snap, StealthRequirement.Required), Is.True);
            carrier.MembersWithHeroes = new[] { Profile(false) };
            Assert.That(MissionActorPolicy.HasCapableActor(intent, snap, StealthRequirement.Required), Is.False);
        }

        [TestCase("owned_paid", true)]
        [TestCase("free_activation", true)]
        [TestCase("no_role", false)]
        [TestCase("missing_source", false)]
        [TestCase("spent_source", false)]
        [TestCase("unpaid_source", false)]
        [TestCase("visible_source", false)]
        [TestCase("arrived_source", false)]
        [TestCase("embedded_source", false)]
        [TestCase("hardblocked_target", false)]
        [TestCase("hardblocked_leg", false)]
        [TestCase("suspended_role", false)]
        public void TerminalAssaultWaitsOnlyForOwnedHiddenSourceThatCanFinishForFree(string scenario, bool expected)
        {
            ArmySnapshot carrier = Scout(2, true); carrier.HasActivatedThisTurn = true;
            WorldSnapshot snap = Snapshot(carrier);
            AiMapMemory.RememberBuildingForTest(_player, snap.Known.Buildings[0]);
            ArmySnapshot fist = snap.Self.Armies.First(a => a.ArmyId == 10);
            var intent = new MissionIntent { Kind = MissionKind.Scout, Status = IntentStatus.Active,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.DisruptStructure, FocusHex = Site,
                    RequiresStealth = true }, PreferredMoverArmyId = 2,
                IntentKey = MissionIntentKey.ForScout(ScoutTargetKind.DisruptStructure, Site) };
            if (scenario != "no_role") MissionIntentRegistry.GetOrCreate(_player).Put(intent);
            if (scenario == "missing_source") snap.Self.Armies = new[] { fist };
            if (scenario == "spent_source") carrier.CurrentMovement = 0;
            if (scenario == "unpaid_source") carrier.HasActivatedThisTurn = false;
            if (scenario == "free_activation") { carrier.HasActivatedThisTurn = false; carrier.ActivationApCost = 0; }
            if (scenario == "visible_source") carrier.IsHidden = false;
            if (scenario == "arrived_source") carrier.Hex = Site;
            if (scenario == "embedded_source") fist.MembersWithHeroes = new[] { Profile(true) };
            if (scenario == "hardblocked_target") snap.MapKnowledge.ScoutHardBlockedHexes.Add(Site);
            if (scenario == "hardblocked_leg") snap.MapKnowledge.ScoutHardBlockedHexes.Add(new HexCoord(2, 0));
            if (scenario == "suspended_role") intent.Status = IntentStatus.Suspended;
            Assert.That(AttackMarchPolicy.WaitForDisruption(snap, fist, Site, out _), Is.EqualTo(expected));
        }

        [Test]
        public void SameTurnDeliveryProofRejectsCertainDetectorsAndEveryHardblockedEndpoint()
        {
            ArmySnapshot carrier = Scout(2, true);
            WorldSnapshot snap = Snapshot(carrier);
            Assert.That(ReconAssignmentPlanner.CanDeliverDisruptionThisTurn(snap, carrier, Site), Is.True);
            snap.MapKnowledge.ScoutHardBlockedHexes.Add(Site);
            Assert.That(ReconAssignmentPlanner.CanDeliverDisruptionThisTurn(snap, carrier, Site), Is.False);
            snap.MapKnowledge.ScoutHardBlockedHexes.Clear();
            snap.Known.EnemySightings = Enumerable.Range(0, (int)Math.Ceiling(AiConfigV2.scoutDetectionRiskNorm))
                .Select(id => new AiMapMemory.KnownEnemySighting(Site, _enemy, "detector", 1, 1f, 1f,
                    new[] { Profile(false) }, armyId: 90 + id)).ToList();
            Assert.That(ScoutRiskModel.DetectorRisk(snap, Site), Is.EqualTo(1f));
            Assert.That(ReconAssignmentPlanner.CanDeliverDisruptionThisTurn(snap, carrier, Site), Is.False);
        }

        [Test]
        public void SuspendedAttackDoesNotKeepAStationaryDisruptionObligationAlive()
        {
            WorldSnapshot snap = Snapshot(Scout(2, true));
            snap.Self.Armies = snap.Self.Armies.Where(a => a.IsSoloRecce).ToList();
            var attack = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Suspended,
                Objective = new AttackIntent { Phase = AttackMissionPhase.Gather,
                    Target = AttackTargetRef.For(Site, _enemy, AttackTargetKind.Citadel) } };
            attack.IntentKey = MissionIntentKey.For(attack);
            MissionIntentRegistry.GetOrCreate(_player).Put(attack);
            Assert.That(ReconObjectiveEvaluator.AssaultNeedsDisruption(snap, Site), Is.False);
            attack.Status = IntentStatus.Active;
            Assert.That(ReconObjectiveEvaluator.AssaultNeedsDisruption(snap, Site), Is.True);
        }

        [Test]
        public void EmbeddedCarrierRemovesOnlyBuildingPartInAssaultEstimate()
        {
            var carrier = Profile(true);
            var attackers = new[] { new WorthIt.DefenderProfile(8f, false, attack: 12f, hitPoints: 20f), carrier };
            var plain = new[] { attackers[0], Profile(false) };
            var defender = new WorthIt.DefendingArmy(new[] {
                new WorthIt.DefenderProfile(8f, false, attack: 10f, hitPoints: 20f) },
                default, 13f, buildingDefenseBonus: 10f);
            Assert.That(defender.DefenseBonus(0f, attackers), Is.EqualTo(3f), "terrain remains");
            Assert.That(defender.DefenseBonus(0f, plain), Is.EqualTo(13f));
            float supported = WorthIt.EstimateSequential(attackers, default, new[] { defender }, 0f).WinChance;
            float unsupported = WorthIt.EstimateSequential(plain, default, new[] { defender }, 0f).WinChance;
            Assert.That(supported, Is.GreaterThan(unsupported),
                "the real estimate must value the low-power carrier through the building component");
        }

        // 2026-10-31 — a fist that carries its own carrier removes the defence itself: neither a
        // scout delivery nor its purchase is needed.
        [Test]
        public void FistCarryingItsOwnCarrier_NeedsNoDisruptionDelivery()
        {
            WorldSnapshot snap = Snapshot();
            ArmySnapshot fist = snap.Self.Armies.First(a => a.ArmyId == 10);
            Assert.That(ReconObjectiveEvaluator.AssaultNeedsDisruption(snap, Site), Is.True);
            fist.MembersWithHeroes = new[] { Profile(true) };
            Assert.That(ReconObjectiveEvaluator.AssaultNeedsDisruption(snap, Site), Is.False);
            var attack = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                Objective = new AttackIntent { Phase = AttackMissionPhase.Gather, PrimaryArmyId = 10,
                    Target = AttackTargetRef.For(Site, _enemy, AttackTargetKind.Citadel) } };
            attack.IntentKey = MissionIntentKey.For(attack);
            MissionIntentRegistry.GetOrCreate(_player).Put(attack);
            Assert.That(ReconObjectiveEvaluator.AssaultNeedsDisruption(snap, Site), Is.False);
            fist.MembersWithHeroes = Array.Empty<WorthIt.DefenderProfile>();
            Assert.That(ReconObjectiveEvaluator.AssaultNeedsDisruption(snap, Site), Is.True);
            Assert.That(ReconObjectiveEvaluator.AttackSiteOf(snap, 10), Is.EqualTo(Site));
            Assert.That(ReconObjectiveEvaluator.AttackSiteOf(snap, 11), Is.Null);
        }

        // 2026-10-31 — no stealth carrier anywhere: Recon asks for one (Stealth AND Disruption
        // required); a fielded carrier — even a busy one — is Assignment's to bind.
        [Test]
        public void MissingStealthCarrier_RaisesOneDisruptionScoutDemand()
        {
            WorldSnapshot ordinaryOnly = Snapshot(Scout(1, false));
            AxisDemand demand = DemandLayer.DisruptionCarrierDemand(ordinaryOnly,
                Array.Empty<MissionIntent>(), _player);
            Assert.That(demand, Is.Not.Null);
            Assert.That(demand.Capability, Is.EqualTo(CapabilityKind.ScoutCapability));
            Assert.That(demand.RequiredTraits, Is.EqualTo(TraitPreference.Stealth | TraitPreference.Disruption));
            Assert.That(demand.TargetHex, Is.EqualTo(Site));
            Assert.That(DemandLayer.DisruptionCarrierDemand(Snapshot(Scout(2, true)),
                Array.Empty<MissionIntent>(), _player), Is.Null);
            ordinaryOnly.Self.Armies.First(a => a.ArmyId == 10).MembersWithHeroes = new[] { Profile(true) };
            Assert.That(DemandLayer.DisruptionCarrierDemand(ordinaryOnly,
                Array.Empty<MissionIntent>(), _player), Is.Null, "the fist already carries one");
            Assert.That(MaterializationChainMatching.MeetsRequiredTraits(
                new[] { "r1s4", "Stealth4", UnitAbilities.Disruption }, demand.RequiredTraits), Is.True);
            Assert.That(MaterializationChainMatching.MeetsRequiredTraits(
                new[] { "r1s4", "Stealth4" }, demand.RequiredTraits), Is.False);
        }

        // 2026-10-31 — reinforcement never swaps the fist's carrier out for a stronger body, and a
        // first carrier may replace the weakest body although its own combat value is lower.
        [Test]
        public void ReinforcementKeepsAndBringsTheFirstCarrierAgainstASiteDefence()
        {
            var site = new[] { new WorthIt.DefendingArmy(new[] {
                new WorthIt.DefenderProfile(4f, false, attack: 6f, hitPoints: 12f) },
                default, 2f, buildingDefenseBonus: 2f) };
            var woundedCarrier = new WorthIt.DefenderProfile(1f, false, attack: 3f, hitPoints: 1f,
                abilities: new[] { UnitAbilities.Disruption }, maxHitPoints: 6f);
            var weakBody = new WorthIt.DefenderProfile(1f, false, attack: 2f, hitPoints: 4f, maxHitPoints: 4f);
            var strong = new WorthIt.DefenderProfile(4f, false, attack: 12f, hitPoints: 20f, maxHitPoints: 20f);
            Assert.That(GroundCombatAssemblyPlanner.TryProjectReinforcement(new[] { woundedCarrier, weakBody },
                new[] { strong }, 2, 2, default, site, out var kept, out _, out _, out _, out int displaced,
                0f, requireWinGain: false), Is.True);
            Assert.That(displaced, Is.EqualTo(1), "the wounded carrier never gives way");
            Assert.That(Game.Combat.DisruptionRules.HasCarrier(kept), Is.True);

            var carrier = new WorthIt.DefenderProfile(1f, false, attack: 1f, hitPoints: 3f,
                abilities: new[] { UnitAbilities.Disruption }, maxHitPoints: 3f);
            Assert.That(GroundCombatAssemblyPlanner.TryProjectReinforcement(new[] { weakBody, strong },
                new[] { strong, carrier }, 2, 2, default, site, out var brought, out _, out _, out _, out _,
                0f, requireWinGain: false), Is.True);
            Assert.That(Game.Combat.DisruptionRules.HasCarrier(brought), Is.True);
            Assert.That(GroundCombatDonorPolicy.DisruptionHelps(new[] { weakBody }, site), Is.True);
            Assert.That(GroundCombatDonorPolicy.DisruptionHelps(new[] { carrier }, site), Is.False);
            Assert.That(GroundCombatDonorPolicy.DisruptionHelps(new[] { weakBody },
                new[] { new WorthIt.DefendingArmy(site[0].Units, default, 2f) }), Is.False,
                "terrain alone is not a structure defence");
        }

        private ScoutMissionTarget DeliveryTarget() => new ScoutMissionTarget {
            Kind = ScoutTargetKind.DisruptStructure, FocusHex = Site, Stealth = StealthRequirement.Required };

        private ArmySnapshot Scout(int id, bool carrier) => new ArmySnapshot {
            ArmyId = id, Owner = _player, Hex = new HexCoord(1, 0), IsSoloRecce = true,
            MemberCount = 1, Members = new[] { Profile(carrier) }, MembersWithHeroes = new[] { Profile(carrier) },
            CurrentMovement = 3, MaxMovement = 3, IsHidden = true, CanEnterStealth = true,
            StealthLevel = 4, ActivationApCost = 1, EffectiveVisionRadius = 1,
        };

        private static WorthIt.DefenderProfile Profile(bool carrier) => new WorthIt.DefenderProfile(
            1f, false, attack: 1f, hitPoints: 4f,
            abilities: carrier ? new[] { UnitAbilities.Disruption, "r1s0", "Stealth4" }
                : new[] { "r1s0", "Stealth4" });

        private WorldSnapshot Snapshot(params ArmySnapshot[] scouts)
        {
            var armies = scouts.ToList();
            armies.Add(new ArmySnapshot { ArmyId = 10, Owner = _player, Hex = new HexCoord(2, 0),
                MemberCount = 3, MaxMovement = 3, EffectiveArmyPower = 40f, IsStructuralRaidActor = true });
            return new WorldSnapshot {
                Observer = _player, TurnNumber = 10, Intents = MissionIntentRegistry.GetOrCreate(_player),
                Self = new SelfSnapshot { Armies = armies,
                    AttackPeak = 40f, BaseHexes = new[] { new HexCoord(0, 0) }, Citadel = new HexCoord(0, 0) },
                Known = new KnownSnapshot { Buildings = new[] { new AiMapMemory.KnownBuilding(Site, _enemy,
                    true, Array.Empty<string>(), isBase: true, defense: 10f) } },
                MapKnowledge = new MapKnowledgeSnapshot {
                    AllHexes = HexGridMath.HexesInRange(default, 5).ToList(),
                    VisitedHexSet = new HashSet<HexCoord> { Site },
                    VisibleArrivalBlockedHexes = new HashSet<HexCoord> { Site },
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                },
            };
        }
    }
}
#endif
