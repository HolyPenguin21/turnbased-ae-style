using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    public sealed class CollectionContentValidation : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report) => Validate();

        [MenuItem("Game/Collection/Validate Content")]
        public static void Validate()
        {
            var graph = Discover();
            if (!graph.Assets<GameAssetCatalog>().Any())
                throw new BuildFailedException($"{GameAssetCatalogBuilder.CatalogPath}: Missing runtime asset catalog.");
            var issues = AuthoredContentValidator.Validate(graph);
            if (issues.Count != 0)
                throw new BuildFailedException(string.Join("\n", issues.Select(issue => issue.ToString())));
        }

        internal static AuthoredContentGraph Discover()
        {
            var sources = new Dictionary<Object, string>();
            foreach (Type type in new[] { typeof(FactionCardCatalog), typeof(StartingDeckCatalog), typeof(ResearchProductionCatalog),
                typeof(EventCatalog), typeof(NeutralArmyCatalog), typeof(UnitAbilityCatalog), typeof(GameConfig) })
            {
                var paths = AssetDatabase.FindAssets("t:" + type.Name).Select(AssetDatabase.GUIDToAssetPath)
                    .Distinct().OrderBy(path => path, StringComparer.Ordinal);
                foreach (string path in paths)
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (asset != null && type.IsInstanceOfType(asset)) sources[asset] = path;
            }

            // Validate the catalog used by the player even if the project has other catalog assets.
            var runtimeCatalog = UnityEngine.Resources.Load<GameAssetCatalog>(GameAssetCatalog.ResourceName);
            if (runtimeCatalog != null) sources[runtimeCatalog] = AssetDatabase.GetAssetPath(runtimeCatalog);

            var guards = new Dictionary<EventCatalog, IReadOnlyList<NeutralArmyCatalog>>();
            var configurations = new Dictionary<Object, IReadOnlyList<GameConfig>>();
            foreach (var scene in EditorBuildSettings.scenes.Where(scene => scene.enabled))
            {
                var dependencies = new HashSet<string>(AssetDatabase.GetDependencies(scene.path, true), StringComparer.Ordinal);
                var sceneAssets = sources.Where(item => dependencies.Contains(item.Value)).Select(item => item.Key).ToArray();
                var armies = sceneAssets.OfType<NeutralArmyCatalog>().DefaultIfEmpty().ToArray();
                var configs = sceneAssets.OfType<GameConfig>().ToArray();
                foreach (var catalog in sceneAssets.OfType<EventCatalog>())
                    guards[catalog] = guards.TryGetValue(catalog, out var previous)
                        ? previous.Concat(armies).Distinct().ToArray() : armies;
                foreach (var catalog in sceneAssets.Where(asset => asset is NeutralArmyCatalog || asset is EventCatalog))
                    configurations[catalog] = configurations.TryGetValue(catalog, out var previous)
                        ? previous.Concat(configs).Distinct().ToArray() : configs;
            }
            return new AuthoredContentGraph(sources, guards, configurations);
        }
    }
}
