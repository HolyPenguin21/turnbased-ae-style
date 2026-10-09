#if UNITY_INCLUDE_TESTS
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Ai;
using Game.Ai.V2;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Stage E2 of the decoupling task: the bank learns the moments of the turn
    // (StrategicManager.ObserveInitialForce / AfterMissionSettlement / BeforeFirstTempo /
    // BeforeTempoSpend) instead of the orchestrator calling its stages one by one, and the owner
    // of the aviation obligations records a stalled step. The bank's own formulas are covered by
    // AiEconomyReservationLifecycleTests / AiReservationInvariantsTests and are not repeated here.
    public class AiStrategicTurnLifecycleTests
    {
        private static SelfSnapshot Force(bool open) => new SelfSnapshot
        {
            DeployedPower = open ? 80f : 10f, AvailablePower = 100f,
            TotalMilitaryPotential = 60f, FieldStrikePotential = 0f,
        };

        [Test]
        public void OnlyTheClosingOfTheOrdinaryPassesSettlesTheContinuationWindow()
        {
            var player = new PlayerSetupData();
            var ctx = new AiTurnContext { TurnNumber = 8 };
            try
            {
                OperationContinuationWindow.EndTurn(player, 8);
                Assert.That(OperationContinuationWindow.IsSettled(player, 8), Is.False);

                StrategicManager.AfterMissionSettlement(player, null, null, ctx);
                StrategicManager.BeforeTempoSpend(player, null, null, ctx);
                Assert.That(OperationContinuationWindow.IsSettled(player, 8), Is.False,
                    "a settled mission step or a later round must not end the Phase-A protection");

                StrategicManager.BeforeFirstTempo(player, null, null, ctx);
                Assert.That(OperationContinuationWindow.IsSettled(player, 8), Is.True);
                Assert.That(OperationContinuationWindow.IsSettled(player, 9), Is.False,
                    "the window belongs to its turn");
            }
            finally { OperationContinuationWindow.EndTurn(player, 8); }
        }

        [Test]
        public void TheInitialScanStampsTheMobilizationGateOfThatTurnOnly()
        {
            var player = new PlayerSetupData { Nickname = "Us" };
            var ctx = new AiTurnContext { TurnNumber = 12 };
            try
            {
                StrategicManager.ObserveInitialForce(new WorldSnapshot { Self = Force(true) }, player, ctx);
                Assert.That(OperationContinuationWindow.IsMobilizationOpen(player, 12), Is.True);
                Assert.That(OperationContinuationWindow.IsMobilizationOpen(player, 13), Is.False);

                // the gate is re-stamped by every scan: a closed measurement clears it
                StrategicManager.ObserveInitialForce(new WorldSnapshot { Self = Force(false) }, player, ctx);
                Assert.That(OperationContinuationWindow.IsMobilizationOpen(player, 12), Is.False);
            }
            finally { OperationContinuationWindow.EndTurn(player, 12); }
        }

        [Test]
        public void AnObligationThatDidNotProgressIsStalledForItsTurnOnly()
        {
            var player = new PlayerSetupData();
            var ctx = new AiTurnContext { TurnNumber = 20 };
            try
            {
                Assert.That(AviationObligations.RecordSettledStep(player, ctx, 5, progressed: true), Is.False);
                Assert.That(AviationObligationStallRegistry.IsStalled(player, 20, 5), Is.False);

                Assert.That(AviationObligations.RecordSettledStep(player, ctx, 5, progressed: false), Is.True);
                Assert.That(AviationObligationStallRegistry.IsStalled(player, 20, 5), Is.True);
                Assert.That(AviationObligationStallRegistry.IsStalled(player, 20, 6), Is.False);
                Assert.That(AviationObligationStallRegistry.IsStalled(player, 21, 5), Is.False,
                    "the next turn tries the wing again");
            }
            finally { AviationObligationStallRegistry.Clear(); }
        }

        // The order of the stages inside a moment is the order the orchestrator used before:
        // reconcile -> release the income cover -> settle the window.
        [Test]
        public void TheFirstTempoMomentKeepsTheStageOrder()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string file = Directory.GetFiles(root, "StrategicTurnLifecycle.cs", SearchOption.AllDirectories).Single();
            string text = File.ReadAllText(file);
            int start = text.IndexOf("internal static void BeforeFirstTempo", System.StringComparison.Ordinal);
            string body = text.Substring(start, text.IndexOf("internal static void BeforeTempoSpend",
                System.StringComparison.Ordinal) - start);
            int reconcile = body.IndexOf("ReconcileEconomyCompletionReservations(", System.StringComparison.Ordinal);
            int release = body.IndexOf("ReleaseDeferredEconomyIncomeCover(", System.StringComparison.Ordinal);
            int settle = body.IndexOf("OperationContinuationWindow.Settle(", System.StringComparison.Ordinal);
            Assert.That(reconcile, Is.GreaterThan(-1));
            Assert.That(release, Is.GreaterThan(reconcile));
            Assert.That(settle, Is.GreaterThan(release));
            Assert.That(Regex.Matches(body, @"ReleaseDeferredEconomyIncomeCover\(").Count, Is.EqualTo(1),
                "the income cover is released exactly once per turn");
        }

        // Acceptance of the stage: no executable code of the whole Orchestration folder names the
        // bank stage owners any more (comments are ignored).
        [Test]
        public void OrchestrationDoesNotNameTheBankStageOwners()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string dir = Directory.GetDirectories(root, "Orchestration", SearchOption.AllDirectories).First();
            var banned = new Regex(@"\b(EconomyReservationLifecycle|OperationContinuationWindow|AviationObligationStallRegistry)\b");
            var offenders = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, n: i + 1, l)))
                .Where(x => banned.IsMatch(x.l.Split(new[] { "//" }, 2, System.StringSplitOptions.None)[0]))
                .Select(x => Path.GetFileName(x.f) + ":" + x.n + ": " + x.l.Trim()).ToList();
            Assert.That(offenders, Is.Empty, string.Join(System.Environment.NewLine, offenders));
        }
    }
}
#endif
