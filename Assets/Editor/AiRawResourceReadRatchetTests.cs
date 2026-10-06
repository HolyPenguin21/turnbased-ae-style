#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Ratchet on raw resource reads in the AI V2 code. "Can I afford this" decisions go through
    // TurnResourceBook / StrategicSpendability, which subtract the holds the spender may not draw
    // on; a raw read (root.ActionPoints, GetResource, Stockpile, CanAfford) sees the whole stock
    // and silently eats other owners' holds. The reads that exist today are legitimate: executors
    // measuring AP before/after an action, Provisioning's physical check of an already funded
    // mission, valuations, telemetry, the book itself. This test freezes their per-file count so
    // a NEW raw read fails here and gets a deliberate decision:
    //   · a decision about spending        -> use TurnResourceBook.Free / StrategicSpendability;
    //   · a genuine physical/telemetry read -> raise that file's number below, in the same change.
    // Lower a number whenever a read goes away.
    public class AiRawResourceReadRatchetTests
    {
        private static readonly Regex RawRead =
            new Regex(@"(?<!StrategicReservedResource)\.ActionPoints\b|\.GetResource\(|\.Stockpile\b|\bCanAfford\(");

        private static readonly Dictionary<string, int> Allowed = new Dictionary<string, int>
        {
            ["Allocation/ResourceAllocator.cs"] = 4,
            ["Analysis/WorldAnalysis.Knowledge.cs"] = 2,
            ["Analysis/WorldAnalysis.Observation.cs"] = 5,
            ["Analysis/WorldAnalysis.Self.cs"] = 5,
            ["Continuity/MissionRevalidator.cs"] = 2,
            ["Diagnostics/AiFrameLog.cs"] = 2,
            ["Diagnostics/AiV2Trace.cs"] = 2,
            ["Diagnostics/MaterializationDiagnostics.cs"] = 3,
            ["Diagnostics/TurnResourceTelemetry.cs"] = 15,
            ["Evaluation/ActionPrice.cs"] = 1,
            ["Evaluation/Cards/NonCombatCardPlayer.cs"] = 5,
            ["Evaluation/Cards/StrategicCardEvaluator.cs"] = 1,
            ["Evaluation/Effects/StrategicEffectRegistry.cs"] = 2,
            ["Execution/CardPlayExecutor.cs"] = 3,
            ["Execution/ReconAirExecutor.cs"] = 34,
            ["Execution/ReconGroundExecutor.cs"] = 2,
            // +1 (T01): the preparation step measures its physical AP delta like every lane.
            ["Execution/ActiveDefenceExecutor.cs"] = 1,
            ["Execution/RaidExecutor.cs"] = 1,
            ["Execution/TaskExecutor.cs"] = 10,
            ["Housekeeping/HousekeepingManager.cs"] = 6,
            ["Initiative/PreTurnCapacityAnalysis.cs"] = 1,
            // Physical before/after accounting, including AP spent by the local-refit handoff.
            ["Materialization/MaterializationExecutor.cs"] = 26,
            ["Missions/Raid/RaidRecoveryPlanner.cs"] = 1,
            ["Orchestration/AiStrategyV2Pipeline.cs"] = 6,
            // T01: the preparation step's physical turn-AP-left read, as every provisioning lane.
            ["Provisioning/AttackProvisioner.cs"] = 1,
            ["Provisioning/GroundCombatAssaultTransaction.cs"] = 3,
            ["Provisioning/ProvisioningManager.Air.cs"] = 1,
            ["Provisioning/ProvisioningManager.Development.cs"] = 1,
            ["Provisioning/ProvisioningManager.Economy.cs"] = 4,
            ["Provisioning/ProvisioningManager.cs"] = 1,
            ["Reaction/ReactionRoundExecutor.cs"] = 2,
            ["Reaction/StrategicReactionPass.cs"] = 1,
            ["Recon/AiAirSortiePlanner.cs"] = 1,
            ["Recon/AviationSortieReservationEvaluator.cs"] = 1,
            ["Recon/ReconAirCapacityPolicy.cs"] = 2,
            ["Recon/ReconAirEnergyPolicy.cs"] = 1,
            ["State/ApTurnPressure.cs"] = 1,
            ["State/PhaseAApBudget.cs"] = 1,
            ["State/ReservationInvariants.cs"] = 2,
            ["State/StrategicResourceReservation.cs"] = 2,
            ["State/StrategicSpendability.cs"] = 3,
            ["State/TurnResourceBook.cs"] = 8,
            ["Strategy/Demand/DevelopmentUpgradeFulfillment.cs"] = 3,
            ["Strategy/Demand/InfrastructureFulfillment.cs"] = 16,
            ["Strategy/PhaseA/MaterializationPortfolioSolver.cs"] = 1,
            ["Strategy/PhaseB/HoldEvaluator.cs"] = 1,
            ["Strategy/PhaseB/TempoCandidateProvider.cs"] = 1,
            ["Strategy/StrategicMaintenancePolicy.cs"] = 3,
            ["Strategy/StrategicPhaseA.cs"] = 4,
            ["Strategy/StrategicPhaseB.cs"] = 16,
        };

        [Test]
        public void ResourceEnumMembersAreNotPhysicalPoolReads()
        {
            Assert.That(RawRead.Matches("StrategicReservedResource.ActionPoints").Count, Is.Zero);
            Assert.That(RawRead.Matches("root.ActionPoints").Count, Is.EqualTo(1));
            Assert.That(RawRead.Matches("root.GetResource(type)").Count, Is.EqualTo(1));
        }

        [Test]
        public void RawResourceReads_DoNotGrowOutsideTheirApprovedCount()
        {
            string root = new[] { "Assets", Path.Combine("src", "Assets") }
                .Select(a => Path.Combine(Directory.GetCurrentDirectory(), a, "Scripts", "Ai", "V2"))
                .FirstOrDefault(Directory.Exists);
            if (root == null)
                Assert.Ignore("AI V2 sources not found from the working directory");

            var grown = new List<string>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                int count = File.ReadLines(file)
                    .Select(line => line.Split(new[] { "//" }, 2, System.StringSplitOptions.None)[0])
                    .Sum(code => RawRead.Matches(code).Count);
                string key = file.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                Allowed.TryGetValue(key, out int allowed);
                if (count > allowed)
                    grown.Add($"{key}: {count} raw reads (approved {allowed})");
            }

            Assert.That(grown, Is.Empty,
                "New raw resource reads. Spending decisions must use TurnResourceBook / "
                + "StrategicSpendability; raise the approved count only for a physical or "
                + "telemetry read:\n" + string.Join("\n", grown));
        }
    }
}
#endif
