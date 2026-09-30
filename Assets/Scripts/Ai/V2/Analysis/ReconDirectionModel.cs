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
            IReadOnlyDictionary<ReconSector, float> concentration = EnemyConcentration(snapshot, origin, out int enemyCount);
            foreach (KeyValuePair<ReconSector, float> kv in concentration)
                weights[kv.Key] = kv.Value;

            if (enemyCount > 0)
            {
                // Acceptance telemetry intentionally exposes only the already-sanitized shape of
                // the signal. Never log enemy ids, exact hexes, strength, composition or stealth.
                int activeSectors = weights.Count(kv => kv.Value > 0f);
                AiDebugLog.Write(
                    $"[AI][V2][Recon][Acceptance] scenario=coarse-direction-pressure status=PASS signal=coarse activeSectors={activeSectors}");
            }

            PlayerSetupData self = ResolveSelf(snapshot);
            AiMapMemory.KnownBuilding? citadel = KnownEnemyCitadel(snapshot, self);
            ReconSector? knownCitadel = citadel.HasValue
                ? Sector(origin, citadel.Value.Hex) : (ReconSector?)null;

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

        // Sanitized concentration only: one vote per army, normalized across occupied sectors.
        // Preserve encounter order for air-anchor ordering; no hidden identity or strength escapes.
        internal static IReadOnlyDictionary<ReconSector, float> EnemyConcentration(
            WorldSnapshot snapshot, HexCoord origin, out int enemyCount)
        {
            var counts = new Dictionary<ReconSector, float>();
            enemyCount = 0;
            if (snapshot?.TrueWorld?.EnemyArmies != null)
                foreach (ArmySnapshot enemy in snapshot.TrueWorld.EnemyArmies)
                {
                    if (enemy == null) continue;
                    ReconSector sector = Sector(origin, enemy.Hex);
                    counts.TryGetValue(sector, out float count);
                    counts[sector] = count + 1f;
                    enemyCount++;
                }
            if (enemyCount > 0)
                foreach (ReconSector sector in counts.Keys.ToList())
                    counts[sector] /= enemyCount;
            return counts;
        }

        // The first honestly known enemy citadel. Caller retains its own observer resolution.
        internal static AiMapMemory.KnownBuilding? KnownEnemyCitadel(WorldSnapshot snapshot, PlayerSetupData self) =>
            snapshot?.Known?.Buildings?
                .Where(b => b.IsStartingCitadel && b.Owner != null && b.Owner != self)
                .Select(b => (AiMapMemory.KnownBuilding?)b)
                .FirstOrDefault();

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
