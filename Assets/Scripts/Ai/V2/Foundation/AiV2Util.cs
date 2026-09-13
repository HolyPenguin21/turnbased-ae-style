using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Small, decision-relevant helpers that were independently copy-pasted (byte-identical bodies)
    // across several V2 lanes. Consolidated here so a future fix/change lands once instead of N
    // times — the risk this collapses is silent behavioral drift between copies that are supposed
    // to answer the exact same question (see Docs/ai-duplicate-methods-analysis.md, groups A/D/F).
    // Deliberately NOT included: the many private N()/F() float-formatting helpers scattered across
    // diagnostics/logging code — those differ in precision on purpose ("0.##" vs "0.00" vs "0.0")
    // and only affect log text, never a decision, so merging them has no risk-reduction value and
    // was left alone.
    internal static class AiV2Util
    {
        // Lexicographic comparison of scoring keys — was byte-identical in ReconAssignmentPlanner
        // and ProvisioningManager (both drive an injective actor<->job assignment search).
        internal static int Lex(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0) return c;
            }
            return 0;
        }

        // Canonical "find this player's live army by id" lookup — was copy-pasted verbatim in
        // ReconAssignmentPlanner and twice in ProvisioningManager (Ground + RaidProvisioner).
        internal static ArmyData ResolveArmy(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        // Ceiling integer division, guarded against a non-positive divisor. Identical body in six
        // independent cost/threat-model files.
        internal static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;

        // Nearest hex-distance from a point to any hex in a set (0 for an empty/unmatched set).
        internal static int MinDist(IReadOnlyList<HexCoord> hexes, HexCoord to)
        {
            int best = int.MaxValue;
            foreach (HexCoord h in hexes)
            {
                int d = HexGridMath.Distance(h, to);
                if (d < best) best = d;
            }
            return best == int.MaxValue ? 0 : best;
        }

        // Known defenders of a sighted army id, from either the enemy or neutral sighting list.
        // Was copy-pasted (to the line) in AggressionMissionPlanner.KnownDefenders,
        // AggressionObjectiveEvaluator.DefendersOf and AggressionDemandEvaluator.RaidDefenders —
        // the exact kind of duplicate that can silently start giving different answers to "who
        // defends this army" if only one copy gets a future fix.
        internal static IReadOnlyList<WorthIt.DefenderProfile> KnownDefenders(WorldSnapshot snap, int armyId)
        {
            if (snap?.Known == null || armyId == 0)
                return System.Array.Empty<WorthIt.DefenderProfile>();
            IEnumerable<Game.Ai.AiMapMemory.KnownEnemySighting> all =
                (snap.Known.EnemySightings ?? Enumerable.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings ?? Enumerable.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>());
            foreach (Game.Ai.AiMapMemory.KnownEnemySighting s in all)
                if (s.ArmyId == armyId)
                    return s.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            return System.Array.Empty<WorthIt.DefenderProfile>();
        }
    }
}
