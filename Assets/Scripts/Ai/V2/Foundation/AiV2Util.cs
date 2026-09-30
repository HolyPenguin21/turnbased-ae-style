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
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null && a.Id == armyId);

        // Sighting identity for before/after discovery queries (enemy and neutral alike).
        // Army id 0 is valid; repeated sightings collapse to one identity.
        internal static HashSet<int> KnownArmyIds(IEnumerable<Game.Ai.AiMapMemory.KnownEnemySighting> sightings)
        {
            var ids = new HashSet<int>();
            foreach (Game.Ai.AiMapMemory.KnownEnemySighting sighting in sightings)
                ids.Add(sighting.ArmyId);
            return ids;
        }

        // Turns an army needs to cover `distance`: this turn's remaining movement first, then its
        // full movement per turn (a distance within reach is one turn).
        internal static int TurnsToCover(ArmySnapshot army, int distance)
        {
            int remaining = System.Math.Max(0, army.CurrentMovement);
            int move = System.Math.Max(1, army.MaxMovement);
            return distance <= remaining ? 1 : 1 + (distance - remaining + move - 1) / move;
        }

        // Route cost for a snapshot actor. Geometric distances are suitable for vision/risk,
        // never as a replacement for an unreachable route on a real map.
        internal static int TravelCost(WorldSnapshot snap, ArmySnapshot actor, HexCoord target,
            bool arrivesHidden = false, int? maxMovement = null)
        {
            if (actor == null) return int.MaxValue;
            // Synthetic analysis fixtures have no map; live Scan always supplies it.
            if (snap?.Map == null) return HexGridMath.Distance(actor.Hex, target);
            int budget = maxMovement ?? actor.MaxMovement;
            bool Block(HexCoord h) => !actor.IsAir &&
                ((!h.Equals(target) && snap.MapKnowledge?.IsBlockedForScout(h, arrivesHidden) == true)
                || (snap.Map.TryGetTerrainAt(h, out var entry) && entry.moveCost > budget));
            return HexPathfinder.FindPath(snap.Map, actor.Hex, target, blockHex: Block,
                flatCost: actor.IsAir)?.TotalCost ?? int.MaxValue;
        }

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

        // THE single resolver every Raid consumer (objective, demand, admission, assembly,
        // provisioning, reinforcement projection, continuity WorthIt checks) must use to find a
        // Raid target's defenders — no event/army switch duplicated elsewhere. Note armyId 0 is a
        // legitimate army id (see ArmyData identity sequencing), so absence is expressed only by
        // RaidTargetRef.HasValue == false or "not found in the sighting/guard list", never by a
        // numeric sentinel.
        internal static IReadOnlyList<WorthIt.DefenderProfile> KnownDefenders(WorldSnapshot snap, RaidTargetRef target) =>
            WorthIt.UnitsOf(KnownOpposition(snap, target));

        // The same target as the fight it is: one defending army (or event guard) with its
        // observed commander. Empty when nothing is known.
        internal static IReadOnlyList<WorthIt.DefendingArmy> KnownOpposition(WorldSnapshot snap, RaidTargetRef target)
        {
            if (snap?.Known == null || !target.HasValue)
                return System.Array.Empty<WorthIt.DefendingArmy>();

            if (target.Kind == RaidTargetKind.EventGuard)
            {
                if (snap.Known.EventGuards != null)
                    foreach (KnownEventGuardSnapshot g in snap.Known.EventGuards)
                        if (g.Hex.Equals(target.Hex))
                            return new[] { new WorthIt.DefendingArmy(g.Defenders, g.Commander,
                                Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(
                                    snap.Observer, g.Hex, defendingOwner: null)) };
                return System.Array.Empty<WorthIt.DefendingArmy>();
            }

            IEnumerable<Game.Ai.AiMapMemory.KnownEnemySighting> all =
                (snap.Known.EnemySightings ?? Enumerable.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings ?? Enumerable.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>());
            foreach (Game.Ai.AiMapMemory.KnownEnemySighting s in all)
                if (s.ArmyId == target.ArmyId)
                    return new[] { new WorthIt.DefendingArmy(s.Defenders, s.Commander,
                        Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(
                            snap.Observer, s.Hex, s.Owner)) };
            return System.Array.Empty<WorthIt.DefendingArmy>();
        }

        // THE hex defence a Raid target fights with — the companion of KnownOpposition, resolved
        // the same way (event guard: its stable hex; neutral army: its last observed hex) and read
        // through the one fog-honest owner. A field battle is still a battle on a hex: terrain
        // defends the target exactly as WorthIt folds it into the live fight.
        internal static float KnownRaidDefenceBonus(WorldSnapshot snap, RaidTargetRef target)
        {
            if (snap?.Known == null || !target.HasValue)
                return 0f;
            if (target.Kind == RaidTargetKind.EventGuard)
                return Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(
                    snap.Observer, target.Hex, defendingOwner: null);
            IEnumerable<Game.Ai.AiMapMemory.KnownEnemySighting> all =
                (snap.Known.EnemySightings ?? Enumerable.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings ?? Enumerable.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>());
            foreach (Game.Ai.AiMapMemory.KnownEnemySighting s in all)
                if (s.ArmyId == target.ArmyId)
                    return Game.Ai.AiMapMemory.KnownHexDefenseBonusFor(
                        snap.Observer, s.Hex, s.Owner);
            return 0f;
        }

        // Legacy overload for non-Raid callers that only ever deal with a physical army. Raid
        // consumers must call the RaidTargetRef overload above instead of duplicating this switch.
        internal static IReadOnlyList<WorthIt.DefenderProfile> KnownDefenders(WorldSnapshot snap, int armyId) =>
            KnownDefenders(snap, RaidTargetRef.ForNeutralArmy(armyId));
    }
}
