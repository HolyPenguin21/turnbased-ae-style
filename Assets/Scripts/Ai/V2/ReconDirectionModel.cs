using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Coarse, sanitized strategic direction buckets. This is the ONLY shape Recon planners should
    // receive from the explicitly-sanctioned TrueWorld enemy-presence cheat: no army id, no exact
    // hidden hex, no strength/composition, no hidden Recce/AA/stealth survives this boundary.
    public enum ReconSector { E, NE, NW, W, SW, SE }

    public sealed class ReconDirectionSnapshot
    {
        public IReadOnlyDictionary<ReconSector, float> EnemyDirectionSectors;
        public float EnemyPresenceWeight;
        public ReconSector? KnownEnemyCitadelDirection;
        public IReadOnlyCollection<ReconSector> OwnAssetWatchDirections;
    }

    public static class ReconDirectionModel
    {
        public static ReconDirectionSnapshot Build(WorldSnapshot snapshot)
        {
            var weights = new Dictionary<ReconSector, float>();
            foreach (ReconSector s in System.Enum.GetValues(typeof(ReconSector)))
                weights[s] = 0f;

            if (snapshot?.Self == null)
                return Empty(weights);

            HexCoord origin = snapshot.Self.Citadel;
            IReadOnlyList<ArmySnapshot> enemies = snapshot.TrueWorld?.EnemyArmies;
            int enemyCount = 0;
            if (enemies != null)
            {
                foreach (ArmySnapshot enemy in enemies)
                {
                    if (enemy == null)
                        continue;
                    // Every true-world army contributes EXACTLY one base unit. Hidden strength,
                    // roster, AA, Recce and stealth are deliberately ignored.
                    weights[Sector(origin, enemy.Hex)] += 1f;
                    enemyCount++;
                }
            }

            if (enemyCount > 0)
                foreach (ReconSector s in weights.Keys.ToList())
                    weights[s] /= enemyCount;

            PlayerSetupData self = ResolveSelf(snapshot);
            ReconSector? knownCitadel = null;
            if (snapshot.Known?.Buildings != null)
            {
                AiMapMemory.KnownBuilding? citadel = snapshot.Known.Buildings
                    .Where(b => b.IsStartingCitadel && b.Owner != null && b.Owner != self)
                    .Select(b => (AiMapMemory.KnownBuilding?)b)
                    .FirstOrDefault();
                if (citadel.HasValue)
                    knownCitadel = Sector(origin, citadel.Value.Hex);
            }

            var watch = new HashSet<ReconSector>();
            foreach (KeyValuePair<ReconSector, float> kv in weights)
                if (kv.Value > 0f)
                    watch.Add(kv.Key);
            if (knownCitadel.HasValue)
                watch.Add(knownCitadel.Value);

            return new ReconDirectionSnapshot
            {
                EnemyDirectionSectors = weights,
                EnemyPresenceWeight = enemyCount,
                KnownEnemyCitadelDirection = knownCitadel,
                OwnAssetWatchDirections = watch,
            };
        }

        public static ReconSector Sector(HexCoord from, HexCoord to)
        {
            // Axial -> cartesian for a stable six-way heading bucket. The output is categorical;
            // callers never receive the source `to` coordinate through ReconDirectionSnapshot.
            float dq = to.Q - from.Q;
            float dr = to.R - from.R;
            float x = dq + 0.5f * dr;
            float y = 0.8660254f * dr;
            float deg = Mathf.Atan2(y, x) * Mathf.Rad2Deg;
            if (deg < 0f) deg += 360f;

            if (deg < 30f || deg >= 330f) return ReconSector.E;
            if (deg < 90f) return ReconSector.NE;
            if (deg < 150f) return ReconSector.NW;
            if (deg < 210f) return ReconSector.W;
            if (deg < 270f) return ReconSector.SW;
            return ReconSector.SE;
        }

        private static PlayerSetupData ResolveSelf(WorldSnapshot snapshot)
        {
            ArmySnapshot own = snapshot.Self?.Armies?.FirstOrDefault(a => a?.Owner != null);
            if (own != null)
                return own.Owner;
            if (snapshot.Known?.Buildings != null)
            {
                AiMapMemory.KnownBuilding? home = snapshot.Known.Buildings
                    .Where(b => b.Hex.Equals(snapshot.Self.Citadel) && b.Owner != null)
                    .Select(b => (AiMapMemory.KnownBuilding?)b)
                    .FirstOrDefault();
                if (home.HasValue)
                    return home.Value.Owner;
            }
            return null;
        }

        private static ReconDirectionSnapshot Empty(Dictionary<ReconSector, float> weights) =>
            new ReconDirectionSnapshot
            {
                EnemyDirectionSectors = weights,
                EnemyPresenceWeight = 0f,
                KnownEnemyCitadelDirection = null,
                OwnAssetWatchDirections = new ReconSector[0],
            };
    }
}
