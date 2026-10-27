using System;
using System.Security.Cryptography;
using System.Text;

namespace Game.Terrain
{
    internal static class TerrainContentKeys
    {
        internal static string Resolve(string authored, string kind, string legacyName)
        {
            if (!string.IsNullOrWhiteSpace(authored)) return authored;
            if (string.IsNullOrWhiteSpace(legacyName))
                throw new InvalidOperationException("Terrain content needs an authored key or a legacy name.");
            // Stable initial identity for old, unmodified authoring data; never a runtime hash.
            using (SHA256 hash = SHA256.Create())
                return kind + ".legacy." + BitConverter.ToString(hash.ComputeHash(
                    Encoding.UTF8.GetBytes(legacyName.Trim().ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
