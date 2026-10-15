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
    }
}
#endif
