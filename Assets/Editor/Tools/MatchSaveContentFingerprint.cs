using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Game.EditorTools
{
    // Hash direct authored files, not transitive Unity dependency hashes: GameConfig references
    // UI prefabs too, so a dependency hash would reintroduce layout changes through that root.
    internal static class MatchSaveContentFingerprint
    {
        private static readonly string[] PresentationDirectories =
        {
            "Assets/Prefabs/UI/", "Assets/Prefabs/Audio/", "Assets/Prefabs/Rewards/", "Assets/Prefabs/TutorialHints/",
            "Assets/Textures/MainMenu/", "Assets/Textures/UI/", "Assets/Textures/Game/", "Assets/Textures/Campaign/",
            "Assets/Audio/", "Assets/Fonts/", "Assets/TextMesh Pro/",
            "Assets/Shaders/UI/", "Assets/Materials/UI/",
        };
        private static bool Includes(string path) => path.StartsWith("Assets/", StringComparison.Ordinal)
            && !path.StartsWith("Assets/Editor/", StringComparison.Ordinal)
            && !path.StartsWith("Assets/Scripts/", StringComparison.Ordinal)
            && path != "Assets/Resources/MatchSaveAssetManifest.asset"
            && path != "Assets/Resources/GameAssetCatalog.asset"
            && !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
            && path != "Assets/Shaders/MainMenuAtmosphere.shader" && path != "Assets/Shaders/CampaignSurface.shader"
            && path != "Assets/Shaders/ScreenWear.shader" && path != "Assets/Textures/Effects/Overlay.png"
            && !PresentationDirectories.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));

        internal static string Compute(string rootDirectory, string unityVersion,
            IEnumerable<KeyValuePair<string, string>> assets, ISet<string> requiredWorldDependencies)
        {
            var identities = new List<string>();
            using (var hash = SHA256.Create())
            {
                foreach (var asset in assets.OrderBy(a => a.Key, StringComparer.Ordinal))
                {
                    if (!Includes(asset.Value) && !requiredWorldDependencies.Contains(asset.Value)) continue;
                    string path = Path.Combine(rootDirectory, asset.Value);
                    string source, importer;
                    using (var stream = File.OpenRead(path)) source = Hex(hash.ComputeHash(stream));
                    using (var stream = File.OpenRead(path + ".meta")) importer = Hex(hash.ComputeHash(stream));
                    identities.Add(asset.Key + ":" + source + ":" + importer);
                }
                return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(unityVersion + "\n" + string.Join("\n", identities))));
            }
        }
        private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
    }
}
