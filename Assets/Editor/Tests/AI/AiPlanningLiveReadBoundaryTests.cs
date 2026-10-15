#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-10-15 — the planning layers (Strategy, Missions, Evaluation) reason on the WorldSnapshot
    // and reach a live object only through its observer-scoped door, Game.Ai.V2.OwnWorld
    // (`snap.Own` / `OwnWorld.Of(player)`): own armies and buildings, the observer's own vision.
    // The registries themselves are read by Analysis and the live tiers (Continuity, Provisioning,
    // Execution, Housekeeping, Reaction). A direct read here fails: route it through OwnWorld, or
    // move the code to the live tier it belongs to (ObjectiveLiveStatus, AviationRebaseExecutor).
    public class AiPlanningLiveReadBoundaryTests
    {
        private static readonly Regex LiveRead = new Regex(
            @"\b(ArmyRegistry|BuildingRegistry|VisionSystem|StealthSystem|PlayerRootRegistry)\.|\bAiV2Util\.ResolveArmy\(");

        private static readonly string[] PlanningLayers = { "Strategy", "Missions", "Evaluation" };

        [Test]
        public void Pattern_MatchesRegistryReadsButNotTheirNamesInText()
        {
            Assert.That(LiveRead.IsMatch("ArmyRegistry.AllForOwner(player)"), Is.True);
            Assert.That(LiveRead.IsMatch("AiV2Util.ResolveArmy(player, id)"), Is.True);
            Assert.That(LiveRead.IsMatch("OwnWorld.Of(player).Armies"), Is.False);
            Assert.That(LiveRead.IsMatch("MissionIntentRegistry.GetOrCreate(player)"), Is.False);
        }

        [Test]
        public void PlanningLayers_ReadLiveObjectsOnlyThroughOwnWorld()
        {
            string root = new[] { "Assets", Path.Combine("src", "Assets") }
                .Select(a => Path.Combine(Directory.GetCurrentDirectory(), a, "Scripts", "Ai", "V2"))
                .FirstOrDefault(Directory.Exists);
            if (root == null)
                Assert.Ignore("AI V2 sources not found from the working directory");

            var reads = new List<string>();
            foreach (string layer in PlanningLayers)
                foreach (string file in Directory.GetFiles(Path.Combine(root, layer), "*.cs",
                             SearchOption.AllDirectories))
                {
                    int n = 0;
                    foreach (string line in File.ReadLines(file))
                    {
                        n++;
                        string code = line.Split(new[] { "//" }, 2, System.StringSplitOptions.None)[0];
                        if (LiveRead.IsMatch(code))
                            reads.Add($"{file.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/')}:{n}: {code.Trim()}");
                    }
                }

            Assert.That(reads, Is.Empty, "Planning layers read live state directly; use OwnWorld "
                + "or move the code to a live tier:\n" + string.Join("\n", reads));
        }

        // 2026-10-15 — the durable intent store reaches the planning layers and Analysis explicitly:
        // the decision frame's MissionIntentState for the owners that write (Phase A), the read-only
        // IMissionIntentView (a parameter, or WorldSnapshot.Intents bound by WorldAnalysis.Scan) for
        // everything else. The global registry is the lookup of the live tiers and the turn session.
        private static readonly Regex RegistryRead = new Regex(@"\bMissionIntentRegistry\.");
        private static readonly string[] IntentReceivingLayers = { "Strategy", "Missions", "Evaluation", "Analysis" };
        // Force measures computed outside the AI turn (no decision frame exists there).
        private static readonly string[] RegistryReadAllowed = { "Analysis/PlayerForceAnalysis.cs" };

        [Test]
        public void PlanningLayersAndAnalysis_ReceiveTheIntentStoreExplicitly()
        {
            string root = new[] { "Assets", Path.Combine("src", "Assets") }
                .Select(a => Path.Combine(Directory.GetCurrentDirectory(), a, "Scripts", "Ai", "V2"))
                .FirstOrDefault(Directory.Exists);
            if (root == null)
                Assert.Ignore("AI V2 sources not found from the working directory");

            var reads = new List<string>();
            foreach (string layer in IntentReceivingLayers)
                foreach (string file in Directory.GetFiles(Path.Combine(root, layer), "*.cs",
                             SearchOption.AllDirectories))
                {
                    string rel = file.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                    if (RegistryReadAllowed.Contains(rel))
                        continue;
                    int n = 0;
                    foreach (string line in File.ReadLines(file))
                    {
                        n++;
                        string code = line.Split(new[] { "//" }, 2, System.StringSplitOptions.None)[0];
                        if (RegistryRead.IsMatch(code))
                            reads.Add($"{rel}:{n}: {code.Trim()}");
                    }
                }

            Assert.That(reads, Is.Empty, "Planning layers / Analysis look the intent store up globally; "
                + "take it from the caller (IMissionIntentView, WorldSnapshot.Intents):\n" + string.Join("\n", reads));
        }

        // 2026-10-15 — planning never mutates the world itself: the canonical gameplay mutations run
        // in Execution (InfrastructureBuildExecutor, NonCombatPlayExecutor, CardPlayExecutor, ...).
        // A Phase A / Phase B coordinator may call an executor's entry point; it never calls the
        // primitive directly.
        private static readonly Regex GameplayMutation = new Regex(
            @"\b(CardPlayExecutor\.Play|BuildingPlayExecutor\.(PlayBaseCard|PlayFacilityCard|BuildExtractionFacility)"
            + @"|EquipmentSystem\.TryAttach|InfrastructureActions\.TryUpgradeBase|UnitRepair\.TryRepair"
            + @"|AviationActions\.TryDeploy\w*)\(");

        [Test]
        public void PlanningLayers_NeverCallAGameplayMutationDirectly()
        {
            Assert.That(GameplayMutation.IsMatch("CardPlayExecutor.Play(player, root"), Is.True);
            Assert.That(GameplayMutation.IsMatch("CardPlayExecutor.Preflight(player, root"), Is.False);
            Assert.That(GameplayMutation.IsMatch("EquipmentSystem.CanAttach(card"), Is.False);

            string root = new[] { "Assets", Path.Combine("src", "Assets") }
                .Select(a => Path.Combine(Directory.GetCurrentDirectory(), a, "Scripts", "Ai", "V2"))
                .FirstOrDefault(Directory.Exists);
            if (root == null)
                Assert.Ignore("AI V2 sources not found from the working directory");

            var calls = new List<string>();
            foreach (string layer in PlanningLayers)
                foreach (string file in Directory.GetFiles(Path.Combine(root, layer), "*.cs",
                             SearchOption.AllDirectories))
                {
                    int n = 0;
                    foreach (string line in File.ReadLines(file))
                    {
                        n++;
                        string code = line.Split(new[] { "//" }, 2, System.StringSplitOptions.None)[0];
                        if (GameplayMutation.IsMatch(code))
                            calls.Add($"{file.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/')}:{n}: {code.Trim()}");
                    }
                }

            Assert.That(calls, Is.Empty, "Planning layers mutate the world directly; move the action "
                + "to an executor in Execution/:\n" + string.Join("\n", calls));
        }
    }
}
#endif
