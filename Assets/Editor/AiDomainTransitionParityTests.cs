#if UNITY_INCLUDE_TESTS
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiDomainTransitionParityTests
    {
        [Test]
        public void TransitionMatrixMatchesFrozenPreExtractionRules()
        {
            // 5,760 transitions evaluated against pre-terminal.dll before policy extraction.
            Assert.That(Fingerprint(), Is.EqualTo("C521F075668400B3A1CBE227EB370F298ADF2F5714D78E01761F77A07DDE5335"));
        }

        [Test]
        public void CapabilityFailureAgingMatchesFrozenPreExtractionRules()
        {
            // Another 5,760 transitions: no-mover/contention, pool exhaustion, age/stall edges.
            Assert.That(Fingerprint(true), Is.EqualTo("FF74620661454EAA6C80DAF01394D39EB1E5ED37F9B3C9FA875F92FAA5DFBECF"));
        }

        [TestCase(MissionKind.Economy)]
        [TestCase(MissionKind.Development)]
        public void PinnedDeliveryActorIsNotReplacedByARetry(MissionKind kind)
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey(kind, 0, 17, 2, 3);
            var intent = new MissionIntent { Kind = kind, IntentKey = key, CreatedTurn = 3,
                LastProgressTurn = 3, PreferredMoverArmyId = 0 };
            session.PersistentState.Put(intent); session.Leases.For(key).Claim(0);
            session.Settle(new MissionStepResult { IntentKey = key, MissionKind = kind,
                Disposition = MissionStepDisposition.Progress, MadeProgress = true, MoverArmyId = 1 });
            Assert.That(intent.PreferredMoverArmyId, Is.EqualTo(0));
            Assert.That(session.Leases.For(key).ActorClaims, Is.EqualTo(new[] { 0 }));
        }

        [TestCase(RaidMissionPhase.Reinforcement, false)]
        [TestCase(RaidMissionPhase.Reinforcement, true)]
        [TestCase(RaidMissionPhase.SupportReturn, false)]
        [TestCase(RaidMissionPhase.SupportReturn, true)]
        [TestCase(RaidMissionPhase.AirSupport, false)]
        [TestCase(RaidMissionPhase.AirSupport, true)]
        public void RaidSideActorFactsNeverReplacePrimaryAfterLivePhaseChanged(RaidMissionPhase phase,
            bool handedOff)
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey(MissionKind.Raid, 0, 17, 2, 3);
            // Execution may already have cleared support and returned the live intent to Assault.
            var raid = new RaidIntent { PrimaryArmyId = 0, Phase = RaidMissionPhase.Assault };
            var intent = new MissionIntent { Kind = MissionKind.Raid, IntentKey = key, Objective = raid,
                CreatedTurn = 3, LastProgressTurn = 3 };
            session.PersistentState.Put(intent); session.Leases.For(key).Claim(0);
            session.Settle(new MissionStepResult { IntentKey = key, MissionKind = MissionKind.Raid,
                Disposition = MissionStepDisposition.Progress, MadeProgress = true,
                MoverArmyId = phase == RaidMissionPhase.AirSupport ? 2 : 1 }
                .WithPayload(new RaidStepPayload { HasRaidPayload = true, RaidPhase = phase, RaidPrimaryArmyId = 0,
                    RaidSupportArmyId = 1, RaidAirSupportArmyId = 2 })
                .WithPayload(new GroundCombatStepPayload { ReinforcementHandoffAttempted = handedOff }));
            Assert.That(raid.PrimaryArmyId, Is.EqualTo(0));
            if (phase == RaidMissionPhase.Reinforcement)
                Assert.That(raid.SupportArmyId, Is.EqualTo(handedOff ? (int?)null : 1));
            if (phase == RaidMissionPhase.AirSupport)
                Assert.That(raid.AirSupportArmyId, Is.EqualTo(2));
            Assert.That(session.Leases.For(key).ActorClaims, Is.EqualTo(new[] { 0 }));
        }

        [TestCase(AttackMissionPhase.Reinforcement)]
        [TestCase(AttackMissionPhase.Gather)]
        [TestCase(AttackMissionPhase.SupportReturn)]
        public void AttackSupportFactsNeverReplacePrimary(AttackMissionPhase phase)
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey(MissionKind.Attack, 0, 17, 2, 3);
            var attack = new AttackIntent { PrimaryArmyId = 0, SupportArmyId = 1 };
            var intent = new MissionIntent { Kind = MissionKind.Attack, IntentKey = key, Objective = attack,
                CreatedTurn = 3, LastProgressTurn = 3 };
            session.PersistentState.Put(intent); session.Leases.For(key).Claim(0);
            session.Settle(new MissionStepResult { IntentKey = key, MissionKind = MissionKind.Attack,
                Disposition = MissionStepDisposition.Progress, MadeProgress = true, MoverArmyId = 1,
                }.WithPayload(new AttackStepPayload { HasAttackPayload = true, AttackTarget = new AttackMissionTarget
                    { Phase = phase, PrimaryArmyId = 0, SupportArmyId = 1 } }));
            Assert.That(attack.PrimaryArmyId, Is.EqualTo(0));
            Assert.That(session.Leases.For(key).ActorClaims, Is.EqualTo(new[] { 0 }));
        }

        [TearDown] public void Reset()
        {
            MissionIntentRegistry.Clear(); AiAllocatorStateRegistry.Clear();
        }

        [TestCase(MissionKind.Scout, false)]
        [TestCase(MissionKind.Raid, true)]
        [TestCase(MissionKind.Attack, true)]
        [TestCase(MissionKind.ActiveDefence, false)]
        [TestCase(MissionKind.Economy, false)]
        [TestCase(MissionKind.Development, false)]
        public void CompletedStepUsesExistingDomainRetirementAndLeasePolicy(MissionKind kind, bool retained)
        {
            CheckLease(kind, MissionStepDisposition.Completed, retained);
        }

        [TestCase(MissionKind.Scout, MissionStepDisposition.Invalidated)]
        [TestCase(MissionKind.Raid, MissionStepDisposition.Invalidated)]
        [TestCase(MissionKind.Attack, MissionStepDisposition.Invalidated)]
        [TestCase(MissionKind.ActiveDefence, MissionStepDisposition.Invalidated)]
        [TestCase(MissionKind.Economy, MissionStepDisposition.Invalidated)]
        [TestCase(MissionKind.Development, MissionStepDisposition.Invalidated)]
        [TestCase(MissionKind.Scout, MissionStepDisposition.PermanentFailure)]
        [TestCase(MissionKind.Raid, MissionStepDisposition.PermanentFailure)]
        [TestCase(MissionKind.Attack, MissionStepDisposition.PermanentFailure)]
        [TestCase(MissionKind.ActiveDefence, MissionStepDisposition.PermanentFailure)]
        [TestCase(MissionKind.Economy, MissionStepDisposition.PermanentFailure)]
        [TestCase(MissionKind.Development, MissionStepDisposition.PermanentFailure)]
        public void TerminalMainOperationReleasesOnlyItsOwnActorAndResources(MissionKind kind,
            MissionStepDisposition disposition)
        {
            CheckLease(kind, disposition, false);
        }

        private static void CheckLease(MissionKind kind, MissionStepDisposition disposition, bool retained)
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey(kind, 0, 17, 2, 3);
            var otherKey = new MissionIntentKey(MissionKind.Development, 0, 18, 7, 3);
            session.PersistentState.Put(new MissionIntent { Kind = kind, IntentKey = key,
                CreatedTurn = 3, LastProgressTurn = 3 });
            var lease = session.Leases.For(key); var other = session.Leases.For(otherKey);
            lease.Claim(0); other.Claim(1);
            lease.Reserve(StrategicReservationReason.EconomyBuildCompletion,
                StrategicReservedResource.ActionPoints, 2);
            other.Reserve(StrategicReservationReason.EconomyBuildCompletion,
                StrategicReservedResource.ActionPoints, 3);
            var o = new MissionStepResult { MissionKind = kind, IntentKey = key, Disposition = disposition,
                ObjectiveSatisfied = disposition == MissionStepDisposition.Completed, MadeProgress = true }
                .WithPayload(new RaidStepPayload { HasRaidPayload = kind == MissionKind.Raid, RaidPhase = RaidMissionPhase.Assault })
                .WithPayload(new AttackStepPayload { HasAttackPayload = kind == MissionKind.Attack,
                    AttackTarget = new AttackMissionTarget { Phase = AttackMissionPhase.Assault } });
            session.Settle(o);
            Assert.That(session.PersistentState.TryGet(key, out _), Is.EqualTo(retained));
            Assert.That(lease.ActorClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(lease.ResourceClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(other.ActorClaims, Is.EqualTo(new[] { 1 }));
            Assert.That(other.ResourceClaims[0].Amount, Is.EqualTo(3));
        }

        // This source can be compiled separately against the frozen pre-extraction assembly.
        // Reflection is only the access seam for the existing internal Continuity entry point.
        public static string Fingerprint(bool capabilityFailures = false)
        {
            var reconcile = typeof(MissionIntent).Assembly.GetType("Game.Ai.V2.MissionContinuityLayer")
                .GetMethod("ReconcileStep", BindingFlags.Static | BindingFlags.Public);
            var rows = new StringBuilder();
            foreach (MissionKind kind in new[] { MissionKind.Scout, MissionKind.Raid, MissionKind.Attack,
                MissionKind.ActiveDefence, MissionKind.Economy, MissionKind.Development })
            foreach (MissionStepDisposition disposition in Enum.GetValues(typeof(MissionStepDisposition)))
            for (int flags = 0; flags < 32; flags++)
            for (int leg = 0; leg < 5; leg++)
            {
                MissionIntentRegistry.Clear(); AiAllocatorStateRegistry.Clear();
                var player = new PlayerSetupData();
                var key = new MissionIntentKey(kind, 0, 17, 2, 3);
                var state = MissionIntentRegistry.GetOrCreate(player);
                var raidPhase = new[] { RaidMissionPhase.Assault, RaidMissionPhase.Reinforcement,
                    RaidMissionPhase.SupportReturn, RaidMissionPhase.AirSupport, RaidMissionPhase.Return }[leg];
                var attackPhase = new[] { AttackMissionPhase.Assault, AttackMissionPhase.Reinforcement,
                    AttackMissionPhase.GatherReturn, AttackMissionPhase.AirSupport, AttackMissionPhase.Gather }[leg];
                var economyKind = leg == 4 ? EconomyTaskKind.ReturnBuilder
                    : capabilityFailures && leg == 3 ? EconomyTaskKind.MobileCollection : EconomyTaskKind.FoundBase;
                if ((flags & 1) != 0)
                {
                    object objective = kind == MissionKind.Scout ? (object)new ScoutIntent()
                        : kind == MissionKind.Raid ? new RaidIntent { Phase = raidPhase,
                            PrimaryArmyId = 0, SupportArmyId = 1 }
                        : kind == MissionKind.Attack ? new AttackIntent { Phase = attackPhase,
                            PrimaryArmyId = 0, SupportArmyId = 1 }
                        : kind == MissionKind.ActiveDefence ? new ActiveDefenceIntent { PrimaryArmyId = 0 }
                        : kind == MissionKind.Economy ? new EconomyIntent { Kind = economyKind }
                        : (object)new DevelopmentIntent();
                    var intent = new MissionIntent { Kind = kind, IntentKey = key, Objective = objective,
                        CreatedTurn = 3, LastReconciledTurn = 3, LastProgressTurn = 3,
                        TurnsActive = capabilityFailures ? (kind == MissionKind.Raid
                            ? AiConfigV2.raidIntentMaxTurns - 1 : AiConfigV2.commitmentMaxTurns - 1) : 0,
                        StallTurns = capabilityFailures ? AiConfigV2.commitmentStallTurns - 1 : 0 };
                    if (capabilityFailures && leg == 0) intent.PreferredMoverArmyId = 0;
                    state.Put(intent);
                }
                var o = new MissionStepResult { IntentKey = key, MissionKind = kind,
                    Disposition = disposition, MadeProgress = (flags & 2) != 0,
                    StepsMoved = (flags & 2) != 0 ? 1 : 0, ApSpent = 2,
                    ObjectiveSatisfied = (flags & 4) != 0, ObjectiveSatisfiedExternally = (flags & 8) != 0,
                    MoverArmyId = leg == 0 ? 0 : 1,
                    FinalHex = new HexCoord(2, 3),
                    ProvisionFailureKindValue = leg == 1 ? ProvisionFailureKind.AssemblyInfeasible : (ProvisionFailureKind?)null,
                    StopReason = leg == 4 ? ExecutionStopReason.NoSafeStep : (ExecutionStopReason?)null };
                if ((flags & 16) != 0) o.GroundFactsForWrite().OperationStarted = true;
                switch (kind)
                {
                    case MissionKind.Scout: o.ReconFactsForWrite().HasScoutPayload = true; o.ReconFactsForWrite().FocusHex = new HexCoord(2, 3); break;
                    case MissionKind.Raid: o.RaidFactsForWrite().HasRaidPayload = true; o.RaidFactsForWrite().RaidPhase = raidPhase;
                        o.RaidFactsForWrite().RaidPrimaryArmyId = 0; o.RaidFactsForWrite().RaidSupportArmyId = 1; break;
                    case MissionKind.Attack: o.AttackFactsForWrite().HasAttackPayload = true; o.AttackFactsForWrite().AttackTarget = new AttackMissionTarget
                        { Phase = attackPhase, PrimaryArmyId = 0, SupportArmyId = 1 }; break;
                    case MissionKind.ActiveDefence: o.DefenceFactsForWrite().HasActiveDefencePayload = true; break;
                    case MissionKind.Economy: o.EconomyFactsForWrite().HasEconomyPayload = true; o.EconomyFactsForWrite().EconomyTarget = new EconomyMissionTarget
                        { Kind = economyKind, TargetHex = new HexCoord(2, 3) }; break;
                    case MissionKind.Development: o.DevelopmentFactsForWrite().HasDevelopmentPayload = true; break;
                }
                if (capabilityFailures)
                {
                    o.MoverArmyId = null;
                    o.ProvisionFailureKindValue = leg % 2 == 0
                        ? ProvisionFailureKind.NoMoverExists : ProvisionFailureKind.MoverContended;
                    o.AllocationDeferReason = leg == 2 ? DeferReason.CommitmentPoolExhausted : (DeferReason?)null;
                }
                reconcile.Invoke(null, new object[] { player, 4, o });
                rows.Append(kind).Append('|').Append(disposition).Append('|').Append(flags).Append('|')
                    .Append(leg).Append('|').Append(state.Count).Append('|').Append(o.MadeProgress);
                foreach (var i in state.All.OrderBy(x => x.IntentKey))
                    rows.Append('|').Append(i.IntentKey).Append('|').Append(i.Kind).Append('|').Append(i.Status)
                        .Append('|').Append(i.Suspended).Append('|').Append(i.Funding).Append('|').Append(i.TurnsActive)
                        .Append('|').Append(i.LastReconciledTurn).Append('|').Append(i.LastProgressTurn)
                        .Append('|').Append(i.StallTurns).Append('|').Append(i.StepsMovedTotal)
                        .Append('|').Append(i.CumulativeApSpent.ToString(CultureInfo.InvariantCulture))
                        .Append('|').Append(i.PreferredMoverArmyId).Append('|').Append(i.Raid?.Phase)
                        .Append('|').Append(i.Raid?.SupportArmyId).Append('|').Append(i.Attack?.Phase)
                        .Append('|').Append(i.Attack?.SupportArmyId).Append('|').Append(i.Scout?.FocusHex)
                        .Append('|').Append(i.Economy?.Kind);
                rows.Append('|').Append(AiAllocatorStateRegistry.GetOrCreate(player).CooldownDigest(4)).Append('\n');
            }
            MissionIntentRegistry.Clear(); AiAllocatorStateRegistry.Clear();
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(rows.ToString()))).Replace("-", "");
        }
    }
}
#endif
