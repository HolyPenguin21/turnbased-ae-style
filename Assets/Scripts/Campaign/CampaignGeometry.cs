using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Game.Campaign
{
    public static class CampaignGeometry
    {
        public static double Cross(Vector2 a, Vector2 b, Vector2 c) => (double)(b.x - a.x) * (c.y - a.y) - (double)(b.y - a.y) * (c.x - a.x);
        public static double Area(IReadOnlyList<Vector2> p)
        { double a = 0; for (int i = 0; i < p.Count; i++) { var b = p[(i + 1) % p.Count]; a += (double)p[i].x * b.y - (double)b.x * p[i].y; } return a / 2; }
        public static bool Contains(IReadOnlyList<Vector2> p, Vector2 v)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                if ((p[i].y > v.y) != (p[j].y > v.y) && v.x < (p[j].x - p[i].x) * (v.y - p[i].y) / (p[j].y - p[i].y) + p[i].x) inside = !inside;
            return inside;
        }
        public static Vector2 Center(RegionState r) => r.PolygonVertices.Aggregate(Vector2.zero, (a, b) => a + b) / r.PolygonVertices.Count;
        // Ear clipping supports the concave shared-boundary deformation; no center fan overlap.
        public static List<int> Triangulate(IReadOnlyList<Vector2> p)
        {
            var ids = Enumerable.Range(0, p.Count).ToList(); var result = new List<int>(); int guard = p.Count * p.Count;
            while (ids.Count > 3 && guard-- > 0)
            {
                bool found = false;
                for (int i = 0; i < ids.Count; i++)
                {
                    int a = ids[(i + ids.Count - 1) % ids.Count], b = ids[i], c = ids[(i + 1) % ids.Count];
                    if (CampaignGeometry.Cross(p[a], p[b], p[c]) <= 1e-10) continue;
                    bool contains = ids.Any(v => v != a && v != b && v != c && CampaignGeometry.Cross(p[a], p[b], p[v]) >= -1e-10
                        && CampaignGeometry.Cross(p[b], p[c], p[v]) >= -1e-10 && CampaignGeometry.Cross(p[c], p[a], p[v]) >= -1e-10);
                    if (contains) continue;
                    result.AddRange(new[] { a, b, c }); ids.RemoveAt(i); found = true; break;
                }
                if (!found) throw new System.IO.InvalidDataException("Cannot triangulate campaign region.");
            }
            if (ids.Count == 3) result.AddRange(ids); return result;
        }
        private static Vector2 InteriorPoint(RegionState region)
        {
            var triangles = Triangulate(region.PolygonVertices);
            return (region.PolygonVertices[triangles[0]] + region.PolygonVertices[triangles[1]] + region.PolygonVertices[triangles[2]]) / 3;
        }
        public static bool Connected(IEnumerable<int> regionIds, IReadOnlyList<RegionState> regions)
        {
            var ids = new HashSet<int>(regionIds); if (ids.Count == 0) return false;
            var found = new HashSet<int>(); var queue = new Queue<int>(); queue.Enqueue(ids.First());
            var lookup = regions.ToDictionary(r => r.RegionId);
            while (queue.Count > 0) { int id = queue.Dequeue(); if (!found.Add(id)) continue; foreach (int n in lookup[id].NeighborIds) if (ids.Contains(n) && !found.Contains(n)) queue.Enqueue(n); }
            return found.Count == ids.Count;
        }
        internal static string VertexKey(Vector2 p) => Math.Round(p.x * 100000) + ":" + Math.Round(p.y * 100000);
        internal static string EdgeKey(Vector2 a, Vector2 b)
        { string x = VertexKey(a), y = VertexKey(b); return string.CompareOrdinal(x, y) < 0 ? x + "/" + y : y + "/" + x; }
        public static bool ProperIntersection(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
            => Cross(a, b, c) * Cross(a, b, d) < -1e-12 && Cross(c, d, a) * Cross(c, d, b) < -1e-12;
        public static void Validate(IReadOnlyList<RegionState> regions, bool initialOwnership = false)
        {
            if (regions == null || regions.Count < 3 || regions.Count > 256 || regions.Any(r => r == null)) throw new InvalidDataException("Invalid regions.");
            var ids = new HashSet<int>(regions.Select(r => r.RegionId));
            if (ids.Count != regions.Count || ids.Any(id => id < 0)) throw new InvalidDataException("Invalid region identities.");
            var edges = new Dictionary<string, List<int>>();
            var directions = new Dictionary<string, string>();
            foreach (var r in regions)
            {
                var p = r.PolygonVertices;
                if (string.IsNullOrWhiteSpace(r.Name) || !CampaignRules.IsPlayable(r.OwnerFaction) || p == null || p.Count < 3 || p.Count > 512
                    || p.Any(v => float.IsNaN(v.x) || float.IsInfinity(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.y) || Math.Abs(v.x) > 2 || Math.Abs(v.y) > 2)
                    || Area(p) <= 1e-6 || p.Select(VertexKey).Distinct().Count() != p.Count || r.NeighborIds == null || r.NeighborIds.Count != r.NeighborIds.Distinct().Count()) throw new InvalidDataException("Invalid region polygon.");
                for (int i = 0; i < p.Count; i++)
                {
                    var b = p[(i + 1) % p.Count];
                    if ((p[i] - b).sqrMagnitude < 1e-12) throw new InvalidDataException("Zero length edge.");
                    for (int j = i + 1; j < p.Count; j++)
                        if (ProperIntersection(p[i], b, p[j], p[(j + 1) % p.Count])) throw new InvalidDataException("Self intersection.");
                    string key = EdgeKey(p[i], b);
                    string direction = VertexKey(p[i]) + "/" + VertexKey(b);
                    if (!edges.TryGetValue(key, out var owners)) { edges[key] = owners = new List<int>(); directions[key] = direction; }
                    else if (directions[key] == direction) throw new InvalidDataException("Shared edge has the same orientation on both regions.");
                    owners.Add(r.RegionId);
                }
            }
            foreach (var edge in edges.Values) if (edge.Count > 2 || edge.Distinct().Count() != edge.Count) throw new InvalidDataException("Overlapping boundaries.");
            foreach (var r in regions)
            {
                var actual = edges.Values.Where(e => e.Count == 2 && e.Contains(r.RegionId)).SelectMany(e => e).Where(id => id != r.RegionId).Distinct().OrderBy(id => id);
                if (!actual.SequenceEqual(r.NeighborIds.OrderBy(id => id)) || r.NeighborIds.Contains(r.RegionId) || r.NeighborIds.Any(n => !ids.Contains(n))) throw new InvalidDataException("Adjacency mismatch.");
            }
            var interiors = regions.Select(InteriorPoint).ToList();
            for (int i = 0; i < regions.Count; i++) for (int j = i + 1; j < regions.Count; j++)
            {
                var a = regions[i].PolygonVertices; var b = regions[j].PolygonVertices;
                for (int x = 0; x < a.Count; x++) for (int y = 0; y < b.Count; y++)
                    if (ProperIntersection(a[x], a[(x + 1) % a.Count], b[y], b[(y + 1) % b.Count])) throw new InvalidDataException("Region intersection.");
                // Boundary vertices do not count; an interior point detects containment.
                if (Contains(a, interiors[j]) || Contains(b, interiors[i])) throw new InvalidDataException("Overlapping regions.");
            }
            if (!Connected(ids, regions)) throw new InvalidDataException("Disconnected map.");
            // A single closed outer boundary and shared interior edges ensure no holes/gaps.
            var outer = edges.Where(e => e.Value.Count == 1).Select(e => e.Key.Split('/')).ToList();
            var degree = new Dictionary<string, List<string>>();
            foreach (var e in outer) for (int i = 0; i < 2; i++) { if (!degree.TryGetValue(e[i], out var n)) degree[e[i]] = n = new List<string>(); n.Add(e[1 - i]); }
            if (degree.Count < 3 || degree.Values.Any(n => n.Count != 2)) throw new InvalidDataException("Open boundary.");
            var visited = new HashSet<string>(); var q = new Queue<string>(); q.Enqueue(degree.Keys.First());
            while (q.Count > 0) { var v = q.Dequeue(); if (visited.Add(v)) foreach (var n in degree[v]) q.Enqueue(n); }
            if (visited.Count != degree.Count) throw new InvalidDataException("Map has holes.");
            if (initialOwnership)
                foreach (var f in Game.Cards.DeckRules.PlayableFactions)
                {
                    var owned = regions.Where(r => r.OwnerFaction == f).Select(r => r.RegionId).ToList();
                    if (!Connected(owned, regions) || owned.Count < regions.Count / 3 || owned.Count > (regions.Count + 2) / 3) throw new InvalidDataException("Invalid initial ownership.");
                }
        }
    }
}
