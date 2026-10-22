#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiGarrisonSortieTests
    {
        private static readonly HexCoord Home = new HexCoord(0, 0), EnemyHex = new HexCoord(1, 0);
        private PlayerSetupData owner;
        private static WorthIt.DefenderProfile Body(float attack, float defense, float hp) =>
            new WorthIt.DefenderProfile(defense, false, null, attack, hp, 3, null, hp);
        [SetUp] public void Setup()
        { owner = new PlayerSetupData { Nickname = "sortie" }; MissionIntentRegistry.Clear(); ArmyRegistry.Clear(); AiMapMemory.Clear(); }
        [TearDown] public void Cleanup() { MissionIntentRegistry.Clear(); ArmyRegistry.Clear(); AiMapMemory.Clear(); }
        private WorldSnapshot World(bool shell = true)
        {
            var roster = new[] { Body(12, 6, 18), Body(9, 4, 14), Body(1, 0, 2) };
            var donor = new ArmySnapshot { ArmyId = 1, Owner = owner, Hex = Home, IsGarrison = true,
                MemberCount = 3, Members = roster, EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(roster),
                NonHeroSpareable = new[] { true, true, false }, NonHeroRuntimeIds = new[] { 101, 102, 103 },
                NonHeroMoveMax = new[] { 3, 3, 3 }, NonHeroCurrentMovement = new[] { 3, 3, 3 },
                NonHeroActivationApCosts = new[] { 2, 1, 1 }, NonHeroIsAviation = new[] { false, false, false } };
            var enemy = new PlayerSetupData { Nickname = "enemy" }; var weak = new[] { Body(1, 0, 2) };
            var contact = new EnemyContactSnapshot { Army = new ArmySnapshot { ArmyId = 77, Owner = enemy,
                Members = weak, MemberCount = 1, EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(weak) },
                Position = EnemyHex, Knowledge = ContactKnowledge.Exact, Confidence = 1, LastObservedTurn = 9 };
            return new WorldSnapshot { Observer = owner, TurnNumber = 9,
                Self = new SelfSnapshot { Citadel = Home, BaseHexes = new[] { Home }, Armies = shell
                    ? new[] { donor, new ArmySnapshot { ArmyId = 30, Owner = owner, Hex = Home, Capacity = 2 } }
                    : new[] { donor } },
                Known = new KnownSnapshot { EnemySightings = new[] { new AiMapMemory.KnownEnemySighting(EnemyHex,
                    enemy, "weak", 1, 0, 1, weak, false, 0, 0, 9, 77) } },
                Threat = new ThreatModel { Contacts = new[] { contact }, Threats = Array.Empty<AssetThreatSnapshot>() } };
        }
        private static ActiveDefenceObjective Objective(WorldSnapshot s) =>
            ActiveDefenceObjectiveEvaluator.EnumerateImmediate(s).Single(o => o.Target.EnemyArmyId == 77);
        private static ActiveDefenceResponse Assess(WorldSnapshot s, ISet<int> excluded = null) =>
            ActiveDefenceObjectiveEvaluator.AssessResponse(s, Objective(s), excluded, null, null);
        private List<MissionProposal> Propose(WorldSnapshot s, IReadOnlyList<MissionIntent> intents = null) =>
            AggressionMissionLayer.Propose(s, new DesireBreakdown(), intents ?? Array.Empty<MissionIntent>(),
                Array.Empty<RaidObjective>(), MissionIntentRegistry.GetOrCreate(owner))
                .Where(p => p.Target is ActiveDefenceMissionTarget t && t.EnemyArmyId == 77).ToList();
        private MissionIntent MaterializeSnapshot(WorldSnapshot s, GarrisonSortiePlan p)
        {
            ArmySnapshot donor = s.Self.Armies[0]; int id = p.ShellArmyId ?? 90;
            var actor = new ArmySnapshot { ArmyId = id, Owner = owner, Hex = Home, IsStructuralRaidActor = true,
                Members = p.Detachment.Members, MemberCount = p.UnitRuntimeIds.Count, NonHeroRuntimeIds = p.UnitRuntimeIds,
                EffectiveArmyPower = p.Detachment.EffectiveArmyPower, Capacity = 2,
                MaxMovement = p.Detachment.MaxMovement, CurrentMovement = p.Detachment.CurrentMovement,
                ActivationApCost = p.Detachment.ActivationApCost };
            var remaining = donor.Members.Where((_, i) => !p.UnitRuntimeIds.Contains(donor.NonHeroRuntimeIds[i])).ToArray();
            donor.NonHeroRuntimeIds = donor.NonHeroRuntimeIds.Where(n => !p.UnitRuntimeIds.Contains(n)).ToArray();
            donor.Members = remaining; donor.MemberCount = remaining.Length;
            donor.EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(remaining);
            s.Self.Armies = new[] { donor, actor };
            var live = ArmyData.WithIdentity(id); live.Owner = owner; live.Hex = Home;
            foreach (int unitId in p.UnitRuntimeIds)
            {
                var unit = UnitData.CreateProjection(unitId); unit.Owner = owner; unit.HitPointsCurrent = 18;
                unit.Attack = 12; unit.Defense = 6; unit.MoveMax = unit.MoveCurrent = 3;
                live.Members.Add(unit);
            }
            ArmyRegistry.Register(live);
            var intent = new MissionIntent { Kind = MissionKind.ActiveDefence, Status = IntentStatus.Active,
                Funding = CommitmentTier.Hard, Objective = new ActiveDefenceIntent { Phase = ActiveDefencePhase.Intercept,
                    InterceptPurpose = ActiveDefenceInterceptPurpose.ImmediateOpportunity, ImmediateTurn = 9,
                    EnemyArmyId = 77, LastKnownHex = EnemyHex, Confidence = 1, ProtectedAssetHex = Home,
                    ProtectedAssetKind = AssetKind.Base, PrimaryArmyId = id, Sortie = p } };
            intent.IntentKey = MissionIntentKey.For(intent);
            var state = MissionIntentRegistry.GetOrCreate(owner); state.Put(intent); s.Intents = state;
            return intent;
        }

        [Test] public void WeakEnemy_UsesMinimalStrongestBody_AndKeepsWeakestCover()
        {
            WorldSnapshot s = World(); var r = Assess(s);
            Assert.That(r.Kind, Is.EqualTo(ActiveDefenceResponseKind.Intercept));
            Assert.That(r.Sortie.UnitRuntimeIds, Is.EqualTo(new[] { 101 }));
            Assert.That(r.Sortie.CoverRuntimeId, Is.EqualTo(103)); Assert.That(r.Sortie.CoversAllDefenders, Is.True);
            Assert.That(r.Sortie.WinChance, Is.GreaterThanOrEqualTo(GroundCombatAdmissionPolicy.FreshStartWinChanceGate));
            Assert.That(s.Self.Armies[0].MemberCount, Is.EqualTo(3), "planning must stay read-only");
        }
        [Test] public void ExistingFieldArmy_RemainsFirstChoice()
        {
            var s = World(); var profiles = new[] { Body(15, 8, 30) };
            s.Self.Armies = s.Self.Armies.Concat(new[] { new ArmySnapshot { ArmyId = 8, Owner = owner,
                Hex = new HexCoord(0, 1), Members = profiles, MemberCount = 1, IsStructuralRaidActor = true,
                CurrentMovement = 3, MaxMovement = 3, EffectiveArmyPower = 50 } }).ToArray();
            var r = Assess(s); Assert.That(r.Sortie, Is.Null); Assert.That(r.Plan.BaseArmyId, Is.EqualTo(8));
        }
        [Test] public void SignificantEnemy_IsNeverASortie()
        { var s = World(); s.Threat.Contacts[0].Army.EffectiveArmyPower = AiConfigV2.activeDefenceMinEnemyPower;
            Assert.That(Assess(s).Sortie, Is.Null); }
        [Test] public void PinnedEnemyPower_IsAnIndependentHardGate()
        { var s = World(); s.Self.Armies[0].Members = new[] { Body(2, 1, 5), Body(1, 0, 2), Body(0, 0, 1) };
            s.Self.Armies[0].NonHeroSpareable = new[] { true, false, false };
            s.Threat.Contacts[0].Army.EffectiveArmyPower = 9; Assert.That(Assess(s).Sortie, Is.Null); }
        [Test] public void MoreRawPower_DoesNotReplaceCombatEstimator()
        { var roster = new[] { Body(0, 20, 100) };
            Assert.That(ActiveDefenceObjectiveEvaluator.SortieClears(roster,
                new[] { new WorthIt.DefendingArmy(new[] { Body(5, 5, 5) }, default) }, out _, out bool cover), Is.False);
            Assert.That(cover, Is.False); }
        [Test] public void EqualPower_IsNotSufficient()
        { var body = new[] { Body(2, 1, 4) }; Assert.That(ActiveDefenceObjectiveEvaluator.SortieClears(body,
            new[] { new WorthIt.DefendingArmy(body, default) }, out _, out _), Is.False); }
        [Test] public void CoverAndFloorCannotBeTaken()
        { var s = World(); s.Self.Armies[0].NonHeroSpareable = new[] { false, false, true };
            Assert.That(Assess(s).Sortie, Is.Null);
            s.Self.Armies[0].NonHeroSpareable = new[] { true, false, false };
            Assert.That(Assess(s).Sortie.UnitRuntimeIds, Is.EqualTo(new[] { 101 })); }
        [Test] public void MinimalRoster_AddsSecondMemberOnlyWhenNeeded()
        { var s = World(); s.Self.Armies[0].Members = new[] { Body(2, 1, 5), Body(2, 1, 5), Body(0, 0, 1) };
            var opponent = new[] { Body(1, 1, 10) }; s.Threat.Contacts[0].Army.Members = opponent;
            s.Threat.Contacts[0].Army.EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(opponent);
            s.Known.EnemySightings = new[] { new AiMapMemory.KnownEnemySighting(EnemyHex,
                s.Threat.Contacts[0].Army.Owner, "durable", 1, 1, 1, opponent, false, 0, 0, 9, 77) };
            Assert.That(Assess(s).Sortie.UnitRuntimeIds, Is.EqualTo(new[] { 101, 102 })); }
        [Test] public void SlowestMember_PreventsSameTurnDeparture()
        { var s = World(); s.Threat.Contacts[0].Position = new HexCoord(2, 0);
            s.Self.Armies[0].NonHeroCurrentMovement = new[] { 1, 3, 3 };
            s.Self.Armies[0].NonHeroMoveMax = new[] { 1, 3, 3 }; Assert.That(Assess(s).Sortie, Is.Null); }
        [TestCase(true, 0)] [TestCase(false, ArmyActions.CreateArmyApCost)]
        public void ShellFirst_ElseCreation_PricesBothCreationAndActivation(bool reuse, int createAp)
        { var s = World(reuse); var proposal = Propose(s).Single(); var t = (ActiveDefenceMissionTarget)proposal.Target;
            Assert.That(t.Sortie.MaterializationAp, Is.EqualTo(createAp));
            Assert.That(proposal.Requirements.ApMinimum, Is.EqualTo(createAp + 2));
            Assert.That(t.RequiresSortieMaterialization, Is.True); Assert.That(GroundCombatLegs.JoinsAssignmentSolve(proposal), Is.False); }
        [Test] public void ClaimedDonorOrForeignShell_CannotBeUsed()
        { var s = World(); Assert.That(Assess(s, new HashSet<int> { 1 }).Sortie, Is.Null);
            Assert.That(Assess(s, new HashSet<int> { 30 }).Sortie.ShellArmyId, Is.Null);
            s.Self.Armies[1].Owner = new PlayerSetupData(); Assert.That(Assess(s).Sortie.ShellArmyId, Is.Null); }
        [TestCase("hex")] [TestCase("activated")] [TestCase("air")] [TestCase("prison")]
        public void UnavailableShell_IsNeverReused(string reason)
        { var s = World(); var shell = s.Self.Armies[1]; switch(reason) {
                case "hex": shell.Hex = new HexCoord(4, 0); break; case "activated": shell.HasActivatedThisTurn = true; break;
                case "air": shell.IsAir = true; break; case "prison": shell.IsPrison = true; break; }
            Assert.That(Assess(s).Sortie.ShellArmyId, Is.Null); }
        [Test] public void StrongCoOccupant_IsIncludedInTheFight()
        { var s = World(); s.Known.EnemySightings = s.Known.EnemySightings.Concat(new[] {
            new AiMapMemory.KnownEnemySighting(EnemyHex, s.Threat.Contacts[0].Army.Owner, "strong", 1, 50, 100,
                new[] { Body(100, 50, 100) }, false, 0, 0, 9, 78) }).ToArray(); Assert.That(Assess(s).Sortie, Is.Null); }
        [Test] public void GarrisonDoesNotBecomeARaidOrAttackHost()
        { var s = World(); Assert.That(GroundCombatActorEligibility.EligibleReadyArmies(s, null), Is.Empty);
            Assert.That(GroundCombatAssemblyPlanner.Plan(s, new GroundCombatAssemblyRequest {
                Opposition = KnownOpposition.OfEnemyArmy(s, 77),
                WinChanceGate = GroundCombatAdmissionPolicy.FreshStartWinChanceGate }).Feasible, Is.False); }
        [Test] public void OnlyTheProtectedStrongholdCanDonate()
        { var s = World(); s.Self.Armies[0].Hex = new HexCoord(4, 0); Assert.That(Assess(s).Sortie, Is.Null); }
        [Test] public void StrategicDefenceNeverUsesSortie()
        { var s = World(); var o = Objective(s); o.Target.InterceptPurpose = ActiveDefenceInterceptPurpose.StrategicDefence;
            Assert.That(ActiveDefenceObjectiveEvaluator.PlanGarrisonSortie(s, o,
                KnownOpposition.OfEnemyArmy(s, 77), null), Is.Null); }
        [Test] public void DedicatedScout_IsNotMaterializedAlone()
        { var s = World(); s.Self.Armies[0].Members = new[] {
            new WorthIt.DefenderProfile(6, false, null, 12, 18, 3, new[] { "r1s1" }, 18), Body(9, 4, 14), Body(1, 0, 2) };
            Assert.That(Assess(s).Sortie.UnitRuntimeIds, Is.EqualTo(new[] { 101, 102 })); }
        [Test] public void TransportProjection_PreservesSpentMovementAndDoesNotAllocateIdentities()
        { var s = World(); var donor = s.Self.Armies[0]; donor.Members = new[] {
            new WorthIt.DefenderProfile(2, false, null, 2, 5, 3, new[] { UnitAbilities.Transport }, 5),
            new WorthIt.DefenderProfile(1, false, new[] { UnitTypeTag.Infantry }, 2, 5, 3), Body(1, 0, 2) };
            donor.NonHeroMoveMax = new[] { 3, 1, 1 }; donor.NonHeroCurrentMovement = new[] { 3, 0, 1 };
            donor.NonHeroMovementSpent = new[] { 0, 2, 0 }; int first = new Game.Units.UnitData().RuntimeId;
            var projection = GarrisonDetachment.Project(donor, new[] { 0, 1 }, 30, 2, true);
            Assert.That(projection.MaxMovement, Is.EqualTo(3)); Assert.That(projection.CurrentMovement, Is.EqualTo(1));
            Assert.That(GarrisonDetachment.Project(donor, new[] { 0, 1 }, 30, 2).CurrentMovement, Is.Zero);
            Assert.That(new Game.Units.UnitData().RuntimeId, Is.EqualTo(first + 1)); }
        [Test] public void PendingExtraction_PinsBothSourcesAndReservesApOnce()
        { var s = World(); var proposal = Propose(s).Single(); var target = (ActiveDefenceMissionTarget)proposal.Target;
            Assert.That(GroundCombatLegs.PinsActors(proposal, out int? shell, out int? donor), Is.True);
            Assert.That(shell, Is.EqualTo(30)); Assert.That(donor, Is.EqualTo(1));
            using var session = new ProvisioningSession(s);
            session.SetGroundCombatConstraints(null, new HashSet<int> { 1, 30 }, new[] { proposal });
            Assert.That(session.ExcludedForGroundCombat(proposal), Has.No.Member(1));
            Assert.That(session.ExcludedForGroundCombat(new MissionProposal { Kind = MissionKind.Raid }), Is.SupersetOf(new[] { 1, 30 }));
            var bound = new ProvisionedMission { Kind = MissionKind.ActiveDefence, MoverArmyId = 30,
                Mission = proposal, ActiveDefenceTarget = target, ClaimedAp = target.Sortie.TotalAp };
            session.RegisterSuccess(StableMissionKey.For(proposal), bound); session.RegisterSuccess(StableMissionKey.For(proposal), bound);
            Assert.That(session.ApClaimed, Is.EqualTo(target.Sortie.TotalAp));
            Assert.That(session.ClaimedArmyIds, Is.SupersetOf(new[] { 1, 30 })); }

        // Bottom-up review regressions: the witness must survive every boundary after extraction.
        [TestCase(true)] [TestCase(false)]
        public void MaterializedWitness_SurvivesPlannerAdmissionAssignmentAndContinuity(bool reusedShell)
        {
            var s = World(reusedShell); var witness = Assess(s).Sortie; var intent = MaterializeSnapshot(s, witness);
            var proposal = Propose(s, new[] { intent }).Single(); var target = (ActiveDefenceMissionTarget)proposal.Target;
            Assert.That(target.Sortie, Is.SameAs(witness)); Assert.That(target.IsMaterializedSortie, Is.True);
            Assert.That(target.RequiresSortieMaterialization, Is.False);
            Assert.That(GroundCombatLegs.JoinsAssignmentSolve(proposal), Is.True);
            Assert.That(GroundCombatLegs.PinsActors(proposal, out _, out _), Is.False);
            Assert.That(GroundCombatAdmissionRegistry.TryGet(proposal, out HashSet<int> eligible), Is.True);
            Assert.That(eligible, Is.EqualTo(new[] { intent.PreferredMoverArmyId.Value }));
            using var session = new ProvisioningSession(s);
            session.SetGroundCombatConstraints(ActorCommitments.FromIntents(new[] { intent }, s, null), null);
            session.SetGroundCombatAssignment(new Dictionary<StableMissionKey, int> {
                [StableMissionKey.For(proposal)] = intent.PreferredMoverArmyId.Value });
            var plan = GroundCombatAssaultTransactionRunner.PlanAssignedAssault(session, proposal,
                KnownOpposition.OfEnemyArmy(s, 77), out _);
            Assert.That(plan.Feasible, Is.True); Assert.That(plan.NeedsAssembly, Is.False);
            Assert.That(plan.Transfers, Is.Empty); Assert.That(plan.ReturnHeroes, Is.Empty);
            Assert.That(plan.WinChanceGate, Is.EqualTo(GroundCombatAdmissionPolicy.FreshStartWinChanceGate));
            var rekeys = new List<(MissionIntentKey Old, MissionIntent Intent)>();
            Assert.That((bool)typeof(ActiveDefenceContinuity).GetMethod("ResolveActiveDefenceIntent",
                BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { owner, s, intent, rekeys }), Is.True);
        }
        [Test] public void DurableSortie_UsesFreshGateAndCannotAssembleOrShed()
        { var s = World(); var i = MaterializeSnapshot(s, Assess(s).Sortie); var p = Propose(s, new[] { i }).Single();
            Assert.That(GroundCombatAdmissionPolicy.AssaultGate(p, i.PreferredMoverArmyId.Value), Is.EqualTo(.8f));
            Assert.That(GroundCombatAdmissionPolicy.AllowsSameHexAssembly(p), Is.False);
            Assert.That(GroundCombatAdmissionPolicy.PreservesDepartureRoster(p), Is.True);
            p.Target = new ActiveDefenceMissionTarget { Phase = ActiveDefencePhase.Intercept };
            Assert.That(GroundCombatAdmissionPolicy.AssaultGate(p, i.PreferredMoverArmyId.Value),
                Is.EqualTo(GroundCombatAdmissionPolicy.ContinuationWinChanceFloor), "ordinary Hard intercept retains its own policy");
            Assert.That(GroundCombatAdmissionPolicy.AllowsSameHexAssembly(p), Is.True); }
        [TestCase("lost")] [TestCase("added")] [TestCase("moved")] [TestCase("hidden")] [TestCase("stronger")] [TestCase("turn")]
        public void InvalidMaterializedWitness_EndsWithoutReplacementOrReturn(string reason)
        { var s = World(); var i = MaterializeSnapshot(s, Assess(s).Sortie); var actor = s.Self.Armies[1];
            switch(reason) { case "lost": actor.NonHeroRuntimeIds = Array.Empty<int>(); break;
                case "added": actor.NonHeroRuntimeIds = actor.NonHeroRuntimeIds.Concat(new[] { 500 }).ToArray(); actor.MemberCount++; break;
                case "moved": s.Threat.Contacts[0].Position = new HexCoord(2, 0); break;
                case "hidden": s.Threat.Contacts[0].Knowledge = ContactKnowledge.LastKnown; break;
                case "stronger": s.Threat.Contacts[0].Army.EffectiveArmyPower = 10; break;
                case "turn": s.TurnNumber++; break; }
            var rekeys = new List<(MissionIntentKey Old, MissionIntent Intent)>();
            Assert.That((bool)typeof(ActiveDefenceContinuity).GetMethod("ResolveActiveDefenceIntent",
                BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { owner, s, i, rekeys }), Is.False);
            Assert.That(rekeys, Is.Empty);
        }
        [Test] public void HousekeepingCannotChangeThePinnedRoster_ButOrdinaryInterceptStillMayReceive()
        { var s = World(); var i = MaterializeSnapshot(s, Assess(s).Sortie);
            var c = ActorCommitments.FromIntents(new[] { i }, s, null);
            Assert.That(c.IsArmyClaimed(i.PreferredMoverArmyId.Value), Is.True);
            var contract = c.MutationContractOf(i.PreferredMoverArmyId.Value);
            Assert.That(contract.MayReceive, Is.False); Assert.That(contract.MayReorderCommander, Is.False);
            Assert.That(contract.MayReleaseExcessHeroes, Is.False); Assert.That(contract.KeepsMovement, Is.True);
            i.ActiveDefence.Sortie = null;
            Assert.That(ActorCommitments.FromIntents(new[] { i }, s, null)
                .MutationContractOf(i.PreferredMoverArmyId.Value).MayReceive, Is.True); }
        [Test] public void ActualProgressReceipt_CreatesADurableSortieWitness()
        { var s = World(); var witness = Assess(s).Sortie; var t = Objective(s).Target;
            t.Sortie = witness; t.SortieMaterialized = true; t.PrimaryArmyId = 30;
            var proposal = new MissionProposal { Kind = MissionKind.ActiveDefence, Target = t };
            var pm = new ProvisionedMission { Kind = MissionKind.ActiveDefence, Mission = proposal, MoverArmyId = 30, ActiveDefenceTarget = t };
            var step = new MissionStepResult { Proposal = proposal, MissionKind = MissionKind.ActiveDefence,
                MoverArmyId = 30, MadeProgress = true, IntentKey = MissionIntentKey.For(proposal) };
            ActiveDefenceContinuity.CaptureActiveDefenceProvisionFacts(pm, step);
            var state = MissionIntentRegistry.GetOrCreate(owner);
            Assert.That(ActiveDefenceContinuity.TryCreateActiveDefenceStep(state, null, null, step, 9), Is.True);
            Assert.That(state.All.Single().ActiveDefence.Sortie, Is.SameAs(witness));
            Assert.That(state.All.Single().PreferredMoverArmyId, Is.EqualTo(30)); }
        [Test] public void CacheFingerprintTracksShellSpendingSpareabilityAndWitness()
        { var s = World(); string a = AggressionAdmission.Fingerprint(s, owner);
            s.Self.Armies[0].NonHeroSpareable = new[] { false, false, false };
            string b = AggressionAdmission.Fingerprint(s, owner); Assert.That(b, Is.Not.EqualTo(a));
            s.Self.Armies[1].Hex = new HexCoord(4, 0); a = AggressionAdmission.Fingerprint(s, owner); Assert.That(a, Is.Not.EqualTo(b));
            s.Self.Armies[0].NonHeroMovementSpent = new[] { 1, 2, 3 };
            Assert.That(AggressionAdmission.Fingerprint(s, owner), Is.Not.EqualTo(a));
            s = World(); var i = MaterializeSnapshot(s, Assess(s).Sortie); a = AggressionAdmission.Fingerprint(s, owner);
            i.ActiveDefence.Sortie = null; Assert.That(AggressionAdmission.Fingerprint(s, owner), Is.Not.EqualTo(a)); }
        [Test] public void CacheFingerprintTracksEqualStatReplacementInMaterializedActor()
        {
            var s = World(); MaterializeSnapshot(s, Assess(s).Sortie);
            string before = AggressionAdmission.Fingerprint(s, owner);
            s.Self.Armies[1].NonHeroRuntimeIds = new[] { 999 };
            Assert.That(AggressionAdmission.Fingerprint(s, owner), Is.Not.EqualTo(before),
                "equal combat stats do not preserve the exact sortie roster");
        }
        [Test] public void ImminentOtherThreatPinsTheSortieBeforeMaterialization()
        { var s = World(); var contact = new EnemyContactSnapshot { Army = new ArmySnapshot { ArmyId = 88,
                Owner = s.Threat.Contacts[0].Army.Owner, Members = new[] { Body(100, 50, 100) }, EffectiveArmyPower = 200 },
                Position = new HexCoord(2, 0), Knowledge = ContactKnowledge.Exact, Confidence = 1 };
            s.Threat.Contacts = s.Threat.Contacts.Concat(new[] { contact }).ToArray();
            s.Threat.Threats = new[] { new AssetThreatSnapshot { Contact = contact, EnemyEta = 1, CanDamage = true,
                Asset = new StrategicAssetSnapshot { Hex = Home, Kind = AssetKind.Base } } };
            Assert.That(Assess(s).Sortie, Is.Null); }
    }
}
#endif
