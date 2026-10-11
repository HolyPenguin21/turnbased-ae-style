#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Level 1 of the pipeline simplification: the shared post-step effects now live in
    // AiTurnSession.SettleStep (domain settlement of a cycle's attempted outcomes) and
    // LifecycleAudit (read-only lifecycle diagnostics split out of AiTurnSession).
    public class AiStepCompletionProtocolTests
    {
        private static MissionIntentKey Key(int id) => new MissionIntentKey(MissionKind.Economy, 0, id, 2, 3);

        private static StableMissionKey Attempt(int id) =>
            new StableMissionKey(MissionKind.Economy, 0, id, 2, 3);

        private static MissionStepResult Result(MissionIntentKey key, StableMissionKey attempt) =>
            new MissionStepResult
            {
                IntentKey = key, MissionKind = MissionKind.Economy, AttemptKey = attempt,
                Disposition = MissionStepDisposition.Progress, MadeProgress = true,
            };

        private static MissionIntent Intent(AiTurnSession session, MissionIntentKey key)
        {
            var intent = new MissionIntent { Kind = MissionKind.Economy, IntentKey = key, CreatedTurn = 3,
                LastProgressTurn = 3, PreferredMoverArmyId = 0 };
            session.PersistentState.Put(intent);
            return intent;
        }

        [Test]
        public void SettleStepSettlesOnlyTheAttemptsThisCycleMade()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            MissionIntentKey a = Key(1), b = Key(2);
            Intent(session, a); Intent(session, b);
            var outcomes = new[] { Result(a, Attempt(1)), Result(b, Attempt(2)) };

            session.SettleStep(outcomes, new System.Collections.Generic.HashSet<StableMissionKey> { Attempt(1) },
                null, null);

            Assert.That(session.LastLifecycleLine, Does.Contain($"operation={a}"));
            Assert.That(session.LastLifecycleLine, Does.Not.Contain($"operation={b}"),
                "the unattempted ledger row was not a step");
        }

        [Test]
        public void SettleStepKeepsLedgerOrderAndSkipsNullRows()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            MissionIntentKey a = Key(1), b = Key(2);
            Intent(session, a); Intent(session, b);
            var both = new System.Collections.Generic.HashSet<StableMissionKey> { Attempt(1), Attempt(2) };

            session.SettleStep(new[] { Result(a, Attempt(1)), null, Result(b, Attempt(2)) }, both, null, null);

            Assert.That(session.LastLifecycleLine, Does.Contain($"operation={b}"), "the last attempted row settles last");
        }

        [Test]
        public void SettleStepWithNoAttemptsSettlesNothing()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            MissionIntentKey a = Key(1);
            Intent(session, a);

            session.SettleStep(new[] { Result(a, Attempt(1)) },
                new System.Collections.Generic.HashSet<StableMissionKey>(), null, null);

            Assert.That(session.LastLifecycleLine, Is.Null);
        }

        [Test]
        public void AuditReportsAClaimOwnerThatHasNoDurableIntent()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            session.Leases.For(Key(9)).Claim(5);

            var violations = session.AuditTurnEnd(null, null);

            Assert.That(violations.Any(v => v.Contains("has no durable intent")), Is.True);
        }

        [Test]
        public void AuditOfAQuietSessionIsClean()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            MissionIntentKey a = Key(1);
            Intent(session, a);
            session.Leases.For(a).Claim(0);

            Assert.That(session.AuditTurnEnd(null, null), Is.Empty);
        }
    }
}
#endif
