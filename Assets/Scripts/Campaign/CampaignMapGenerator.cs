using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Cards;
using Game.Players;
using UnityEngine;

namespace Game.Campaign
{
    [Serializable] public sealed class CampaignGenerationSettings
    {
        public int RegionCount = 24;
        public float MinimumRegionArea = .018f;
        public float MinimumRegionWidth = .07f;
        public float BorderIrregularity = .6f;
        public float PointSpacing = .19f;
    }
    public sealed class CampaignMapGenerator
    {
        public CampaignState Generate(int seed, Faction human, CampaignGenerationSettings settings = null)
        {
            settings = settings ?? new CampaignGenerationSettings();
            if (!CampaignRules.IsPlayable(human) || settings.RegionCount < 3 || settings.RegionCount > 128
                || settings.PointSpacing < 0 || settings.MinimumRegionArea <= 0 || settings.MinimumRegionWidth <= 0
                || settings.BorderIrregularity < 0 || settings.BorderIrregularity > 1
                || new[] { settings.PointSpacing, settings.MinimumRegionArea, settings.MinimumRegionWidth, settings.BorderIrregularity }.Any(v => float.IsNaN(v) || float.IsInfinity(v))) throw new ArgumentException("Invalid campaign settings.");
            Exception last = null;
            for (int attempt = 0; attempt < 64; attempt++)
            {
                try
                {
                    var rng = new CampaignRandom(CampaignRandom.Derive(seed, attempt));
                    var regions = Build(settings, rng);
                    Assign(regions, rng);
                    CampaignGeometry.Validate(regions, true);
                    var state = new CampaignState { CampaignId = Guid.NewGuid().ToString("N"), PlanetName = "Dustworld " + unchecked((uint)seed).ToString("X4"),
                        Seed = seed, HumanFaction = human, Regions = regions, Phase = CampaignPhase.AwaitingFactionAction };
                    state.Factions = DeckRules.PlayableFactions.Select(f => new CampaignFactionState { Faction = f }).ToList();
                    CampaignRules.SetRoundOrder(state);
                    return state;
                }
                catch (InvalidDataException ex) { last = ex; }
            }
            throw new InvalidOperationException("Could not generate a valid planet within 64 attempts.", last);
        }
        private static List<Vector2> Clip(List<Vector2> polygon, Vector2 normal, double offset)
        {
            var result = new List<Vector2>();
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                double da = (double)a.x * normal.x + (double)a.y * normal.y - offset;
                double db = (double)b.x * normal.x + (double)b.y * normal.y - offset;
                if (da <= 0) result.Add(a);
                if ((da < 0 && db > 0) || (da > 0 && db < 0)) result.Add(a + (b - a) * (float)(da / (da - db)));
            }
            return result;
        }
        private static List<RegionState> Build(CampaignGenerationSettings s, CampaignRandom rng)
        {
            var boundary = new List<Vector2>();
            for (int i = 0; i < 32; i++) { double a = i * Math.PI / 16; boundary.Add(new Vector2((float)Math.Cos(a), (float)Math.Sin(a))); }
            var points = new List<Vector2>();
            for (int tries = 0; points.Count < s.RegionCount && tries < 20000; tries++)
            {
                var p = new Vector2((float)(rng.Value() * 1.88 - .94), (float)(rng.Value() * 1.88 - .94));
                if (p.sqrMagnitude > .9f || points.Any(q => (p - q).sqrMagnitude < s.PointSpacing * s.PointSpacing)) continue;
                points.Add(p);
            }
            if (points.Count != s.RegionCount) throw new InvalidDataException("Point spacing cannot be satisfied.");
            var regions = new List<RegionState>();
            var canonical = new Dictionary<string, Vector2>();
            Vector2 Canon(Vector2 p)
            {
                string key = CampaignGeometry.VertexKey(p);
                if (!canonical.TryGetValue(key, out var v)) canonical[key] = v = new Vector2((float)Math.Round(p.x, 5), (float)Math.Round(p.y, 5));
                return v;
            }
            for (int i = 0; i < points.Count; i++)
            {
                var polygon = new List<Vector2>(boundary);
                for (int j = 0; j < points.Count && polygon.Count > 0; j++) if (j != i)
                    polygon = Clip(polygon, points[j] - points[i], (points[j].sqrMagnitude - points[i].sqrMagnitude) / 2.0);
                polygon = polygon.Select(Canon).ToList();
                for (int j = polygon.Count - 1; j >= 0; j--)
                    if (polygon.Count > 1 && (polygon[j] - polygon[(j + 1) % polygon.Count]).sqrMagnitude < 1e-10) polygon.RemoveAt(j);
                if (polygon.Count < 3 || CampaignGeometry.Area(polygon) < s.MinimumRegionArea) throw new InvalidDataException("Region too small.");
                // Width of a convex cell is minimum support distance over its edge normals.
                for (int j = 0; j < polygon.Count; j++)
                {
                    var edge = polygon[(j + 1) % polygon.Count] - polygon[j]; float length = edge.magnitude;
                    var n = new Vector2(-edge.y / length, edge.x / length);
                    float width = polygon.Max(p => Vector2.Dot(p, n)) - polygon.Min(p => Vector2.Dot(p, n));
                    if (width < s.MinimumRegionWidth) throw new InvalidDataException("Region too narrow.");
                }
                regions.Add(new RegionState { RegionId = i, Name = Names[i % Names.Length] + (i >= Names.Length ? " " + (i + 1) : ""), PolygonVertices = polygon });
            }
            var edges = new Dictionary<string, List<int>>();
            foreach (var r in regions) for (int i = 0; i < r.PolygonVertices.Count; i++)
            {
                var p = r.PolygonVertices; string key = CampaignGeometry.EdgeKey(p[i], p[(i + 1) % p.Count]);
                if (!edges.TryGetValue(key, out var owners)) edges[key] = owners = new List<int>(); owners.Add(r.RegionId);
            }
            foreach (var owners in edges.Values.Where(e => e.Count == 2))
            { regions[owners[0]].NeighborIds.Add(owners[1]); regions[owners[1]].NeighborIds.Add(owners[0]); }
            // Each shared edge is split/deformed once; both cells reuse exactly the same midpoint.
            var mids = new Dictionary<string, Vector2>();
            double phase = rng.Value() * Math.PI * 2;
            Vector2 Warp(Vector2 p) => p + new Vector2((float)Math.Sin(p.y * 7 + phase), (float)Math.Sin(p.x * 6 + phase)) * (.014f * s.BorderIrregularity);
            foreach (var r in regions)
            {
                var p = r.PolygonVertices; var shaped = new List<Vector2>();
                for (int i = 0; i < p.Count; i++)
                {
                    var b = p[(i + 1) % p.Count]; string key = CampaignGeometry.EdgeKey(p[i], b);
                    if (!mids.TryGetValue(key, out var mid)) mids[key] = mid = Warp((p[i] + b) / 2);
                    shaped.Add(Warp(p[i])); shaped.Add(mid);
                }
                if (CampaignGeometry.Area(shaped) < s.MinimumRegionArea) throw new InvalidDataException("Deformed region too small.");
                for (int i = 0; i < shaped.Count; i++)
                {
                    var e = shaped[(i + 1) % shaped.Count] - shaped[i];
                    var n = new Vector2(-e.y / e.magnitude, e.x / e.magnitude);
                    if (shaped.Max(v => Vector2.Dot(v, n)) - shaped.Min(v => Vector2.Dot(v, n)) < s.MinimumRegionWidth)
                        throw new InvalidDataException("Deformed region too narrow.");
                }
                r.PolygonVertices = shaped; r.NeighborIds.Sort();
            }
            return regions;
        }
        private static void Assign(List<RegionState> regions, CampaignRandom rng)
        {
            // Bounded connected growth; each accepted addition leaves the unassigned graph connected.
            // This maintains a connected final faction and exact quotas without a territorial AI.
            int n = regions.Count;
            for (int attempt = 0; attempt < 256; attempt++)
            {
                var remaining = new HashSet<int>(regions.Select(r => r.RegionId));
                bool ok = true;
                var factions = DeckRules.PlayableFactions.OrderBy(_ => rng.Next()).ToList();
                for (int f = 0; f < 2; f++)
                {
                    int quota = n / 3 + (f < n % 3 ? 1 : 0);
                    var chosen = new HashSet<int>();
                    var start = remaining.OrderBy(i => i).ToList(); chosen.Add(start[rng.Range(start.Count)]);
                    remaining.ExceptWith(chosen);
                    if (!CampaignGeometry.Connected(remaining, regions)) { ok = false; break; }
                    while (chosen.Count < quota)
                    {
                        var candidates = chosen.SelectMany(i => regions[i].NeighborIds).Where(remaining.Contains).Distinct().OrderBy(i => i).ToList();
                        for (int i = candidates.Count - 1; i > 0; i--) { int j = rng.Range(i + 1); int t = candidates[i]; candidates[i] = candidates[j]; candidates[j] = t; }
                        bool added = false;
                        foreach (int id in candidates)
                        {
                            remaining.Remove(id);
                            if (CampaignGeometry.Connected(remaining, regions)) { chosen.Add(id); added = true; break; }
                            remaining.Add(id);
                        }
                        if (!added) { ok = false; break; }
                    }
                    if (!ok) break;
                    foreach (int id in chosen) regions[id].OwnerFaction = factions[f];
                }
                if (!ok) continue;
                foreach (int id in remaining) regions[id].OwnerFaction = factions[2];
                return;
            }
            throw new InvalidDataException("Could not partition connected territories.");
        }
        private static readonly string[] Names = { "Dust Scar Basin", "Red Hollow", "Iron Mesa", "Ashfall Reach", "Salt Crown", "Broken Horizon", "Cinder Vale", "Silent Crater", "Rust Expanse", "Glass Wastes", "Pale Ridge", "Deadwater", "Black Dunes", "Copper Rift", "Storm Shelf", "Scorched Delta", "Bone Plateau", "Ember Coast", "Dry Meridian", "Wreck Fields", "Shattered Plain", "Grey Frontier", "Deep Scar", "Last Oasis" };
    }
}
