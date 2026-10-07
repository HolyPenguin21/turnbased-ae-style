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
            var o = new MissionTurnOutcome { MissionKind = kind, IntentKey = key, Disposition = disposition,
                ObjectiveSatisfied = disposition == MissionStepDisposition.Completed, MadeProgress = true,
                HasRaidPayload = kind == MissionKind.Raid, RaidPhase = RaidMissionPhase.Assault,
                HasAttackPayload = kind == MissionKind.Attack,
                AttackTarget = new AttackMissionTarget { Phase = AttackMissionPhase.Assault } };
            session.Settle(o);
            Assert.That(session.PersistentState.TryGet(key, out _), Is.EqualTo(retained));
            Assert.That(lease.ActorClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(lease.ResourceClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(other.ActorClaims, Is.EqualTo(new[] { 1 }));
            Assert.That(other.ResourceClaims[0].Amount, Is.EqualTo(3));
        }

        // This source can be compiled separately against the frozen pre-extraction assembly.
        // Reflection is only the access seam for the existing internal Continuity entry point.
        public static string Fingerprint()
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
                var economyKind = leg == 4 ? EconomyTaskKind.ReturnBuilder : EconomyTaskKind.FoundBase;
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
                    state.Put(new MissionIntent { Kind = kind, IntentKey = key, Objective = objective,
                        CreatedTurn = 3, LastReconciledTurn = 3, LastProgressTurn = 3 });
                }
                var o = new MissionTurnOutcome { IntentKey = key, MissionKind = kind,
                    Disposition = disposition, MadeProgress = (flags & 2) != 0,
                    StepsMoved = (flags & 2) != 0 ? 1 : 0, ApSpent = 2,
                    ObjectiveSatisfied = (flags & 4) != 0, ObjectiveSatisfiedExternally = (flags & 8) != 0,
                    OperationStarted = (flags & 16) != 0, MoverArmyId = leg == 0 ? 0 : 1,
                    FinalHex = new HexCoord(2, 3),
                    ProvisionFailureKindValue = leg == 1 ? ProvisionFailureKind.AssemblyInfeasible : (ProvisionFailureKind?)null,
                    StopReason = leg == 4 ? ExecutionStopReason.NoSafeStep : (ExecutionStopReason?)null };
                switch (kind)
                {
                    case MissionKind.Scout: o.HasScoutPayload = true; o.FocusHex = new HexCoord(2, 3); break;
                    case MissionKind.Raid: o.HasRaidPayload = true; o.RaidPhase = raidPhase;
                        o.RaidPrimaryArmyId = 0; o.RaidSupportArmyId = 1; break;
                    case MissionKind.Attack: o.HasAttackPayload = true; o.AttackTarget = new AttackMissionTarget
                        { Phase = attackPhase, PrimaryArmyId = 0, SupportArmyId = 1 }; break;
                    case MissionKind.ActiveDefence: o.HasActiveDefencePayload = true; break;
                    case MissionKind.Economy: o.HasEconomyPayload = true; o.EconomyTarget = new EconomyMissionTarget
                        { Kind = economyKind, TargetHex = new HexCoord(2, 3) }; break;
                    case MissionKind.Development: o.HasDevelopmentPayload = true; break;
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
