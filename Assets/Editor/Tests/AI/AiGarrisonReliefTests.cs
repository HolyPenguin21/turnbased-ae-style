#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-10-18 (project owner's decision) — GarrisonRelief: a safe base lends its spare garrison
    // bodies to a threatened own base whose garrison is below its defence floor.
    public class AiGarrisonReliefTests
    {
        private static readonly HexCoord Citadel = new HexCoord(0, 0);
        private static readonly HexCoord Front = new HexCoord(4, 0);
        private const int DonorId = 1, RecipientId = 2, ShellId = 30;

        [SetUp]
        public void SetUp() => MissionIntentRegistry.Clear();

        [TearDown]
        public void TearDown() => MissionIntentRegistry.Clear();

        private static WorthIt.DefenderProfile Body(float atk, float def, float hp) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, 2, null, hp);

        private static WorldSnapshot World(PlayerSetupData owner, float donorThreat = 0f,
            float recipientThreat = 10f)
        {
            var donorBodies = new[] { Body(6, 4, 10), Body(5, 3, 9), Body(2, 1, 4) };
            var donor = new ArmySnapshot
            {
                ArmyId = DonorId, Owner = owner, Hex = Citadel, IsGarrison = true,
                MemberCount = 3, Members = donorBodies,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(donorBodies),
                NonHeroSpareable = new[] { true, true, false },
                NonHeroRuntimeIds = new[] { 11, 12, 13 },
                NonHeroMoveMax = new[] { 2, 2, 2 }, NonHeroCurrentMovement = new[] { 2, 2, 2 },
                NonHeroActivationApCosts = new[] { 1, 1, 1 },
                NonHeroIsAviation = new[] { false, false, false },
                GarrisonThreatFloor = donorThreat,
            };
            var frontBody = new[] { Body(1, 1, 3) };
            var recipient = new ArmySnapshot
            {
                ArmyId = RecipientId, Owner = owner, Hex = Front, IsGarrison = true,
                MemberCount = 1, Members = frontBody,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(frontBody),
                GarrisonThreatFloor = recipientThreat,
            };
            var shell = new ArmySnapshot { ArmyId = ShellId, Owner = owner, Hex = Citadel, Capacity = 2 };
            return new WorldSnapshot
            {
                Observer = owner, TurnNumber = 9,
                Self = new SelfSnapshot
                {
                    Citadel = Citadel, BaseHexes = new[] { Citadel, Front },
                    Armies = new List<ArmySnapshot> { donor, recipient, shell },
                    // The Base share floor (garrisonDefenceShareBase) of this force is far above the
                    // front garrison: it is short.
                    AvailablePower = 1000f, DeployedPower = 100f,
                },
                Known = new KnownSnapshot(),
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
            };
        }

        [Test]
        public void SafeDonorLendsItsStrongestSpareBodies_ThroughAFreeShell()
        {
            var owner = new PlayerSetupData { Nickname = "Us" };
            List<GarrisonReliefObjective> reliefs = GarrisonReliefObjectiveEvaluator.Enumerate(World(owner), new HashSet<int>());

            Assert.That(reliefs, Has.Count.EqualTo(1));
            GarrisonReliefObjective relief = reliefs[0];
            Assert.That(relief.RecipientHex, Is.EqualTo(Front));
            Assert.That(relief.DonorGarrisonId, Is.EqualTo(DonorId));
            Assert.That(relief.Detachment.ArmyId, Is.EqualTo(ShellId));
            Assert.That(relief.UnitRuntimeIds, Is.EquivalentTo(new[] { 11, 12 }),
                "the spare bodies, strongest first, up to the shell's capacity; the kept body stays");
            Assert.That(relief.Detachment.ActivationApCost, Is.EqualTo(2));
            Assert.That(relief.EtaTurns, Is.EqualTo(2));
        }

        [Test]
        public void NoRelief_FromAThreatenedDonor_ToAnUnthreatenedBase_OrWithoutAFreeShell()
        {
            var owner = new PlayerSetupData { Nickname = "Us" };
            Assert.That(GarrisonReliefObjectiveEvaluator.Enumerate(World(owner, donorThreat: 1f),
                new HashSet<int>()), Is.Empty, "a threatened base never lends: no ping-pong between fronts");
            Assert.That(GarrisonReliefObjectiveEvaluator.Enumerate(World(owner, recipientThreat: 0f),
                new HashSet<int>()), Is.Empty, "a base no known enemy threatens is not a relief target");
            Assert.That(GarrisonReliefObjectiveEvaluator.Enumerate(World(owner), new HashSet<int> { ShellId }),
                Is.Empty, "a shell another operation holds is not free");
        }

        [Test]
        public void ADetachmentAlreadyWalking_CoversItsShareOfTheShortfall()
        {
            var owner = new PlayerSetupData { Nickname = "Us" };
            WorldSnapshot snap = World(owner);
            var walking = new ArmySnapshot { ArmyId = 31, Owner = owner, Hex = new HexCoord(2, 0),
                MemberCount = 2, EffectiveArmyPower = 1000f };
            snap.Self.Armies = snap.Self.Armies.Concat(new[] { walking }).ToList();
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(owner);
            var intent = new MissionIntent
            {
                Kind = MissionKind.ActiveDefence, Status = IntentStatus.Active, Funding = CommitmentTier.Hard,
                Objective = new ActiveDefenceIntent
                {
                    Phase = ActiveDefencePhase.Return, ReturnPurpose = ActiveDefenceReturnPurpose.GarrisonRelief,
                    EnemyArmyId = -1, PrimaryArmyId = 31, ReturnHex = Front,
                    ProtectedAssetHex = Front, ProtectedAssetKind = AssetKind.Base,
                },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            state.Put(intent);
            snap.Intents = state;

            Assert.That(GarrisonReliefObjectiveEvaluator.InFlightReliefPower(snap, Front), Is.EqualTo(1000f));
            Assert.That(GarrisonReliefObjectiveEvaluator.Enumerate(snap, new HashSet<int> { 31 }), Is.Empty);
        }

        [Test]
        public void Planner_ProposesOneGarrisonReliefReturnLegOnTheShell()
        {
            var owner = new PlayerSetupData { Nickname = "Us" };
            WorldSnapshot snap = World(owner);
            List<MissionProposal> proposals = AggressionMissionLayer.Propose(snap, new DesireBreakdown(),
                Array.Empty<MissionIntent>(), Array.Empty<RaidObjective>(), MissionIntentRegistry.GetOrCreate(owner))
                .Where(p => p.Target is ActiveDefenceMissionTarget t
                    && t.ReturnPurpose == ActiveDefenceReturnPurpose.GarrisonRelief).ToList();

            Assert.That(proposals, Has.Count.EqualTo(1));
            var target = (ActiveDefenceMissionTarget)proposals[0].Target;
            Assert.That(target.Phase, Is.EqualTo(ActiveDefencePhase.Return));
            Assert.That(target.PrimaryArmyId, Is.EqualTo(ShellId));
            Assert.That(target.ReturnHex, Is.EqualTo(Front));
            Assert.That(target.ReliefDonorGarrisonId, Is.EqualTo(DonorId));
            Assert.That(proposals[0].PreferredMoverArmyId, Is.EqualTo(ShellId));
            Assert.That(proposals[0].Requirements.ApMinimum, Is.EqualTo(2f), "the detachment's own activation");
            Assert.That(MissionIntentKey.For(proposals[0]),
                Is.EqualTo(MissionIntentKey.ForActiveDefence(ActiveDefencePhase.Return, -1, ShellId, Front)),
                "a Return leg: identified by its mover and destination");
        }
    }
}
#endif
