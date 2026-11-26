using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HOUSEKEEPING NIGHT FOLD  (owner 2026-11-25, housekeeping stand E13 / F06)
    // ===========================================================================================
    //  "Housekeeping must always be ready for a strong attack: the armies and the garrison are
    //  joined, and separated again for the tasks." The day pass (ArmyReorganizationPlanner) shapes
    //  the TASK layout: the garrison keeps its floor, its surplus goes to the armies with a task.
    //  At the end of the owner's turn this fold joins every fighting formation of a held hex (a
    //  hex whose own building keeps emptied armies: Barracks) into the strongest one formation
    //  the commands allow — one hard fight for whoever attacks before the owner's next turn.
    //  Tasks are not consulted: nothing moves until then. Every member of a touched army is
    //  recorded (army, roster index); at the start of the owner's next turn Restore puts the
    //  survivors back exactly — task armies with their id, roster and commander, the garrison
    //  with its bodies — before any planning reads the world. The record is persisted
    //  (AiPersistentState.NightFold): a human-turn save lies between the two.
    //  Zero AP: a body never enters an army that would charge its activation tonight.
    // ===========================================================================================
    public sealed class NightFoldPlan
    {
        public int Q;
        public int R;
        public int DestinationArmyId = -1;
        // The hero that leads the destination (a field army), or -1 (its order stays; a garrison
        // is ordered by CommandRating, ArmyData.NormalizeRoster).
        public int CommanderKey = -1;
        // Every move: a body or a hero into the destination, or (ToArmyId != the destination) a
        // Support hero / operator stepping out of it to a hero-only bench on the hex.
        public readonly List<(int UnitKey, int FromArmyId, int ToArmyId)> Moves = new List<(int, int, int)>();
        public readonly Dictionary<int, List<int>> ExpectedMembership = new Dictionary<int, List<int>>();
        // The whole-hex hold against the worst known enemy (WorthIt.EstimateHexControl through
        // ThreatContactRow, ETA-weighted); -1 without a known enemy.
        public float HoldBefore = -1f;
        public float HoldAfter = -1f;
        public string Before = "";
        public string After = "";

        public bool IsEmpty => Moves.Count == 0 && CommanderKey < 0;
        public string HexKey => Q + "," + R;
    }

    public static partial class ArmyReorganizationPlanner
    {
        private readonly struct NightScore
        {
            public readonly int Formations;
            public readonly float Strongest;
            // The strongest formation's commander (its first hero): Fate, then initiative.
            public readonly int Fate;
            public readonly int Initiative;
            public readonly int Moves;
            private readonly VState _state;

            public NightScore(VState state, int moves)
            {
                _state = state;
                Moves = moves;
                Formations = 0;
                Strongest = 0f;
                Fate = Initiative = -1;
                foreach (KeyValuePair<int, ReorgContainer> kv in state.Meta)
                {
                    if (!IsNightFighting(kv.Value))
                        continue;
                    List<ReorgUnit> units = state.Roster[kv.Key];
                    if (!units.Any(u => u != null && u.IsGroundCombatant))
                        continue;
                    Formations++;
                    float power = ReorgViability.EffectivePower(units);
                    if (power <= Strongest)
                        continue;
                    Strongest = power;
                    ReorgUnit lead = units.FirstOrDefault(u => u != null && u.IsHero);
                    Fate = lead?.AsCommander.Fate ?? -1;
                    Initiative = lead?.AsCommander.Initiative ?? -1;
                }
            }

            public IReadOnlyList<ThreatContactRow> Rows =>
                _state.ThreatBenchmarks.Count == 0 ? Array.Empty<ThreatContactRow>()
                    : (IReadOnlyList<ThreatContactRow>)ThreatRowsOf(_state);

            // Better first: fewer fighting formations (one fight — owner), then the whole-hex
            // hold against every known enemy (worst first, Monte Carlo tolerance), then the
            // strongest formation, then its commander's Fate and initiative (owner 2026-11-25:
            // command capacity beyond the seated bodies decides nothing), then fewer moves.
            public int CompareTo(in NightScore o)
            {
                int c = Formations.CompareTo(o.Formations);
                if (c != 0) return c;
                if (_state.ThreatBenchmarks.Count > 0)
                {
                    List<ThreatContactRow> a = Rows.OrderBy(r => r.HexReadiness).ThenBy(r => r.ThreatArmyId).ToList();
                    List<ThreatContactRow> b = o.Rows.OrderBy(r => r.HexReadiness).ThenBy(r => r.ThreatArmyId).ToList();
                    for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
                    {
                        float eps = Math.Max(FloatEps, Math.Max(a[i].HexTolerance, b[i].HexTolerance));
                        if (a[i].HexReadiness > b[i].HexReadiness + eps) return -1;
                        if (a[i].HexReadiness < b[i].HexReadiness - eps) return 1;
                    }
                }
                if (Strongest > o.Strongest + FloatEps) return -1;
                if (Strongest < o.Strongest - FloatEps) return 1;
                c = o.Fate.CompareTo(Fate);
                if (c != 0) return c;
                c = o.Initiative.CompareTo(Initiative);
                if (c != 0) return c;
                return Moves.CompareTo(o.Moves);
            }
        }

        // A container that fights for the hex at night and may give or take members: the garrison
        // and every ground army (free, with a task, or an empty shell). Aircraft, carriers, the
        // prison, a working scout and a collector keep their own semantics.
        private static bool IsNightFighting(ReorgContainer c) =>
            c != null && (c.Role == ReorgPhysicalRole.Garrison || c.Role == ReorgPhysicalRole.NormalFieldArmy
                || c.Role == ReorgPhysicalRole.ProtectedMissionArmy || c.Role == ReorgPhysicalRole.EmptyReusableArmy);

        private static bool NightMovableBody(ReorgUnit u) =>
            u != null && !u.IsHero && u.IsGroundBattleBody && !u.IsAviation && !u.IsCommitted
            && !u.IsDevelopmentOperator;

        // A hero that may lead or ride in the night formation: never a Research/Production operator
        // or a Support (garrison) hero — they stay where they serve.
        private static bool NightMovableHero(ReorgUnit u) =>
            u != null && u.IsHero && !u.IsAviation && !u.IsCommitted && !u.IsDevelopmentOperator
            && !u.IsGarrisonHero;

        internal static NightFoldPlan PlanNightFold(LocalForceGroup group)
        {
            var plan = new NightFoldPlan { Q = group?.Q ?? 0, R = group?.R ?? 0 };
            if (group?.Containers == null || group.Garrison == null)
                return plan;
            var state = new VState { HexDefenseBonus = group.HexDefenseBonus,
                BuildingDefenseBonus = group.BuildingDefenseBonus, Environment = group.Environment };
            state.ThreatBenchmarks.AddRange(group.ThreatBenchmarks);
            foreach (ReorgContainer c in group.Containers)
            {
                state.Meta[c.ArmyId] = c;
                state.Roster[c.ArmyId] = new List<ReorgUnit>(c.Units);
            }
            List<int> parts = group.Containers.Where(IsNightFighting).Select(c => c.ArmyId).OrderBy(x => x).ToList();
            foreach (KeyValuePair<int, List<ReorgUnit>> kv in state.Roster)
                plan.ExpectedMembership[kv.Key] = kv.Value.Select(u => u.Key).ToList();
            plan.HoldBefore = WorstHold(state);
            plan.HoldAfter = plan.HoldBefore;
            plan.Before = FormatNight(state, parts);
            if (parts.Count(id => state.Roster[id].Any(u => u != null && u.IsGroundCombatant)) < 2)
                return plan;

            var baseline = new NightScore(state, 0);
            NightScore bestScore = baseline;
            VState best = null;
            int bestDst = -1, bestLead = -1;
            List<(int, int, int)> bestMoves = null;
            foreach (int dstId in parts)
            {
                var leads = new List<ReorgUnit> { null };
                foreach (int id in parts)
                    leads.AddRange(state.Roster[id].Where(NightMovableHero)
                        .Where(h => id != dstId || !state.Meta[dstId].IsGarrison));
                foreach (ReorgUnit lead in leads)
                {
                    VState layout = BuildNightLayout(state, parts, dstId, lead, out List<(int, int, int)> moves,
                        out bool reordered);
                    if (layout == null || moves.Count == 0 && !reordered)
                        continue;
                    var score = new NightScore(layout, moves.Count);
                    if (score.CompareTo(bestScore) < 0)
                    {
                        best = layout;
                        bestScore = score;
                        bestDst = dstId;
                        bestLead = reordered || lead != null && moves.Any(m => m.Item1 == lead.Key) ? lead.Key : -1;
                        bestMoves = moves;
                    }
                }
            }

            if (best == null)
                return plan;
            plan.DestinationArmyId = bestDst;
            plan.CommanderKey = state.Meta[bestDst].IsGarrison ? -1 : bestLead;
            plan.Moves.AddRange(bestMoves);
            foreach (KeyValuePair<int, List<ReorgUnit>> kv in best.Roster)
                plan.ExpectedMembership[kv.Key] = kv.Value.Select(u => u.Key).ToList();
            plan.HoldAfter = WorstHold(best);
            plan.After = FormatNight(best, parts);
            return plan;
        }

        private static float WorstHold(VState state) =>
            state.ThreatBenchmarks.Count == 0 ? -1f
                : ThreatRowsOf(state).Select(r => r.HexReadiness).DefaultIfEmpty(-1f).Min();

        private static string FormatNight(VState state, List<int> parts) =>
            string.Join(" | ", parts.Select(id => $"#{id}:{state.Roster[id].Count}"
                + $"/P{ReorgViability.EffectivePower(state.Roster[id]):0.#}"));

        // `dstId` led by `lead` (null: as it is) takes the strongest movable bodies of the other
        // night containers while its command seats them, then their army support carriers
        // (Heal/Drill/Transport fight under any commander). Null when a source would be left
        // over its own capacity (its commander left) or a step would cost activation AP.
        private static VState BuildNightLayout(VState state, List<int> parts, int dstId, ReorgUnit lead,
            out List<(int, int, int)> moves, out bool reordered)
        {
            moves = new List<(int, int, int)>();
            reordered = false;
            VState v = state.Clone();
            ReorgContainer meta = v.Meta[dstId];
            List<ReorgUnit> dst = v.Roster[dstId];
            int OwnerOf(ReorgUnit u) => parts.First(id => v.Roster[id].Contains(u));

            if (lead != null)
            {
                int from = OwnerOf(lead);
                if (from != dstId)
                {
                    if (meta.ChargesActivationFor(lead))
                        return null;
                    v.Roster[from].Remove(lead);
                    ReorgViability.AddMemberSorted(dst, lead, meta.IsGarrison);
                    moves.Add((lead.Key, from, dstId));
                }
                if (!meta.IsGarrison && !ReferenceEquals(dst.FirstOrDefault(u => u.IsHero), lead))
                {
                    dst.Remove(lead);
                    dst.Insert(0, lead);
                    reordered = from == dstId;
                }
                if (ReorgViability.Capacity(dst, meta.IsGarrison) < dst.Count)
                    return null;
            }

            List<(int, int, int)> taken = moves;
            // 2026-11-26 (owner) — a garrison is never left without a body while it can keep one
            // (a weak scout must not walk into an empty Base): its last fighter stays, the
            // weakest one, since the strongest leave first.
            bool Takeable(ReorgUnit u, int from) =>
                NightMovableBody(u) && !meta.ChargesActivationFor(u)
                && !(v.Meta[from].IsGarrison && v.Roster[from].Count(x => x.IsGroundCombatant) <= 1);
            void FillBodies()
            {
                var bodies = parts.Where(id => id != dstId)
                    .SelectMany(id => v.Roster[id].Where(NightMovableBody).Select(u => (u, id)))
                    .OrderByDescending(x => AiPower.SkilledCombatValue(x.u.CombatProfile))
                    .ThenBy(x => x.u.Key).ToList();
                foreach ((ReorgUnit u, int from) in bodies)
                {
                    if (ReorgViability.Capacity(dst, meta.IsGarrison) < dst.Count + 1)
                        break;
                    if (!Takeable(u, from))
                        continue;
                    v.Roster[from].Remove(u);
                    ReorgViability.AddMemberSorted(dst, u, meta.IsGarrison);
                    taken.Add((u.Key, from, dstId));
                }
            }
            FillBodies();

            // 2026-11-18 (owner) — a Support hero or an operator that does not command the formation
            // steps out to a hero-only bench on the hex (it keeps serving that hex) while bodies wait
            // for its slot; the bench is a night container left without fighters.
            while (parts.Any(id => id != dstId && v.Roster[id].Any(u => Takeable(u, id)))
                   && ReorgViability.Capacity(dst, meta.IsGarrison) <= dst.Count)
            {
                ReorgUnit commander = dst.FirstOrDefault(u => u.IsHero);
                ReorgUnit idle = dst.Where(u => u.IsHero && !ReferenceEquals(u, commander) && !u.IsCommitted
                        && !u.IsAviation && (u.IsGarrisonHero || u.IsDevelopmentOperator))
                    .OrderBy(u => u.CommandRating).ThenBy(u => u.Key).FirstOrDefault();
                if (idle == null)
                    break;
                int bench = parts.Where(id => id != dstId && !v.Roster[id].Any(u => u.IsGroundCombatant)
                        && !v.Meta[id].ChargesActivationFor(idle))
                    .Where(id =>
                    {
                        var after = new List<ReorgUnit>(v.Roster[id]);
                        ReorgViability.AddMemberSorted(after, idle, v.Meta[id].IsGarrison);
                        return ReorgViability.Capacity(after, v.Meta[id].IsGarrison) >= after.Count;
                    })
                    .DefaultIfEmpty(-1).First();
                if (bench < 0)
                    break;
                dst.Remove(idle);
                ReorgViability.AddMemberSorted(v.Roster[bench], idle, v.Meta[bench].IsGarrison);
                moves.Add((idle.Key, dstId, bench));
                FillBodies();
            }

            var carriers = parts.Where(id => id != dstId)
                .SelectMany(id => v.Roster[id].Where(h => NightMovableHero(h) && h.IsArmySupportCarrier)
                    .Select(h => (h, id)))
                .OrderBy(x => x.h.Key).ToList();
            foreach ((ReorgUnit h, int from) in carriers)
            {
                if (meta.ChargesActivationFor(h))
                    continue;
                var after = new List<ReorgUnit>(dst);
                ReorgViability.AddMemberSorted(after, h, meta.IsGarrison);
                if (ReorgViability.Capacity(after, meta.IsGarrison) < after.Count
                    || !ReorgViability.CanLeaveWithoutOvercrowding(v.Roster[from], h, v.Meta[from].IsGarrison))
                    continue;
                v.Roster[from].Remove(h);
                dst.Clear();
                dst.AddRange(after);
                moves.Add((h.Key, from, dstId));
            }

            foreach (int id in parts)
            {
                List<ReorgUnit> roster = v.Roster[id];
                if (v.Meta[id].IsGarrison && id != dstId)
                {
                    List<ReorgUnit> ordered = ReorgViability.NormalizeRoster(roster);
                    roster.Clear();
                    roster.AddRange(ordered);
                }
                if (ReorgViability.Capacity(roster, v.Meta[id].IsGarrison) < roster.Count)
                    return null;
            }
            return v;
        }
    }

    internal static class HousekeepingNightFold
    {
        // Per player: unit runtime id -> (the army it stood in by day, its roster index there).
        private static readonly Dictionary<PlayerSetupData, Dictionary<int, (int ArmyId, int Index)>> Records =
            new Dictionary<PlayerSetupData, Dictionary<int, (int ArmyId, int Index)>>();

        internal static bool HasRecord(PlayerSetupData player) =>
            player != null && Records.TryGetValue(player, out var r) && r.Count > 0;

        internal static void ClearAll() => Records.Clear();

        // A held hex keeps an emptied army alive until the owner's next turn (ArmyActions.
        // DeleteArmyIfEmptied: the owner's building with Barracks): only there may an army be
        // emptied for the night and refilled in the morning.
        private static bool KeepsEmptiedArmies(PlayerSetupData player, HexCoord hex)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.Owner == player && building.HasAbility(UnitAbilities.Barracks);
        }

        // End of the owner's turn, after the day pass. Returns the number of units moved.
        internal static int Fold(PlayerSetupData player, AiTurnContext ctx, ActorCommitments commitments,
            WorldSnapshot snapshot)
        {
            if (player == null || ctx == null)
                return 0;
            if (HasRecord(player))
                Restore(player, ctx, "a night record still open at the next fold");
            ArmyReorgAnalysis analysis = ArmyReorgAnalyzer.Analyze(player, commitments, snapshot, ctx,
                includeEveryGroup: true);
            int moved = 0;
            foreach (LocalForceGroup group in analysis.Groups)
            {
                var hex = new HexCoord(group.Q, group.R);
                if (group.Garrison == null || !KeepsEmptiedArmies(player, hex))
                    continue;
                if (group.Containers.Any(c => analysis.ArmyById.TryGetValue(c.ArmyId, out ArmyData a)
                        && (a.IsMovementBusy || a.Controller != null && a.Controller.IsMoving)))
                    continue;
                NightFoldPlan plan = ArmyReorganizationPlanner.PlanNightFold(group);
                if (plan.IsEmpty)
                    continue;
                moved += Execute(plan, analysis, player, ctx);
            }
            return moved;
        }

        private static int Execute(NightFoldPlan plan, ArmyReorgAnalysis analysis, PlayerSetupData player,
            AiTurnContext ctx)
        {
            if (!analysis.ArmyById.TryGetValue(plan.DestinationArmyId, out ArmyData dst) || dst == null)
                return 0;
            UnitData lead = plan.CommanderKey >= 0 && analysis.UnitByKey.TryGetValue(plan.CommanderKey, out UnitData l)
                ? l : null;
            // Benches first (they free the destination's slots), then the commander's batch (its
            // Command seats the rest), then every other source.
            var batches = plan.Moves.GroupBy(m => (m.FromArmyId, m.ToArmyId))
                .Select(g => (from: analysis.ArmyById[g.Key.FromArmyId], to: analysis.ArmyById[g.Key.ToArmyId],
                    units: g.Select(m => analysis.UnitByKey[m.UnitKey]).ToList()))
                .OrderByDescending(b => b.to != dst)
                .ThenByDescending(b => lead != null && b.units.Contains(lead))
                .ToList();

            if (!Records.TryGetValue(player, out var record))
                Records[player] = record = new Dictionary<int, (int, int)>();
            foreach (ArmyData army in new[] { dst }.Concat(batches.Select(b => b.from)).Concat(batches.Select(b => b.to)))
                for (int i = 0; i < army.Members.Count; i++)
                    if (!record.ContainsKey(army.Members[i].RuntimeId))
                        record[army.Members[i].RuntimeId] = (army.Id, i);

            int moved = 0;
            if (lead != null && dst.Members.Contains(lead) && dst.Members.IndexOf(lead) > 0)
            {
                if (dst.TryReorderCommander(lead, out string reorderFail))
                    WorldDeltaLifecycle.CommitMutation();
                else
                    AiDebugLog.Write($"[AI][V2][NIGHT] ({plan.HexKey}) reorder {lead.Name} #{dst.Id} failed: {reorderFail}");
            }
            foreach ((ArmyData from, ArmyData to, List<UnitData> units) in batches)
            {
                UnitData promote = to == dst && !dst.IsGarrison && lead != null && units.Contains(lead) ? lead : null;
                if (ArmyActions.TransferMembersAtomic(units, from, to, ctx.HexSelection, out string fail, promote))
                {
                    WorldDeltaLifecycle.CommitMutation();
                    moved += units.Count;
                }
                else
                    AiDebugLog.Write($"[AI][V2][NIGHT] ({plan.HexKey}) #{from.Id}->#{to.Id} "
                        + $"[{string.Join(",", units.Select(u => u.Name))}] failed: {fail}");
            }
            AiDebugLog.Write($"[AI][V2][NIGHT] ({plan.HexKey}) fold into #{dst.Id}"
                + (lead != null ? $" led by {lead.Name}" : "")
                + $": {plan.Before} -> {plan.After}"
                + (plan.HoldBefore >= 0f ? $", hold {plan.HoldBefore:0.00} -> {plan.HoldAfter:0.00}" : "")
                + $"; moved {moved}");
            return moved;
        }

        // Start of the owner's turn, before anything reads the world: every recorded survivor
        // goes back to the army and roster place it held by day. Returns the units moved back.
        internal static int Restore(PlayerSetupData player, AiTurnContext ctx, string why = "turn start")
        {
            if (player == null || !Records.TryGetValue(player, out var record))
                return 0;
            Records.Remove(player);
            var where = new Dictionary<int, (UnitData unit, ArmyData army)>();
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player).Where(a => a != null))
                foreach (UnitData u in a.Members)
                    where[u.RuntimeId] = (u, a);

            var homes = record.GroupBy(kv => kv.Value.ArmyId)
                .Select(g => (army: ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null && a.Id == g.Key),
                    order: g.OrderBy(kv => kv.Value.Index)
                        .Where(kv => where.ContainsKey(kv.Key)).Select(kv => where[kv.Key].unit).ToList()))
                .Where(h => h.army != null && h.order.Count > 0)
                .ToList();

            // Batches (one source -> one home), retried until no progress: a merged formation
            // fits its remaining members only once the other guests have left.
            var pending = new List<(ArmyData from, ArmyData home, List<UnitData> units, UnitData lead)>();
            foreach ((ArmyData home, List<UnitData> order) in homes)
            {
                UnitData homeLead = order.FirstOrDefault(u => u.IsHero);
                foreach (var g in order.Where(u => ArmyRegistry.FindArmyContaining(u) != home)
                             .GroupBy(u => ArmyRegistry.FindArmyContaining(u)))
                    if (g.Key != null && g.Key.Hex.Equals(home.Hex))
                        pending.Add((g.Key, home, g.ToList(),
                            !home.IsGarrison && homeLead != null && g.Contains(homeLead) ? homeLead : null));
            }
            int moved = 0;
            bool progress = true;
            string lastFail = null;
            while (pending.Count > 0 && progress)
            {
                progress = false;
                foreach (var b in pending.OrderByDescending(b => b.lead != null).ToList())
                {
                    if (!ArmyActions.TransferMembersAtomic(b.units, b.from, b.home, ctx?.HexSelection,
                            out string fail, b.lead))
                    {
                        lastFail = $"#{b.from.Id}->#{b.home.Id}: {fail}";
                        continue;
                    }
                    WorldDeltaLifecycle.CommitMutation();
                    moved += b.units.Count;
                    pending.Remove(b);
                    progress = true;
                }
            }
            if (pending.Count > 0)
                AiDebugLog.Write($"[AI][V2][NIGHT] restore left {pending.Sum(b => b.units.Count)} unit(s) "
                    + $"where they spent the night ({lastFail})");

            // The day order (commander first) of every home army.
            foreach ((ArmyData home, List<UnitData> order) in homes)
            {
                if (home.IsGarrison)
                {
                    if (home.NormalizeGarrisonOrder())
                        WorldDeltaLifecycle.CommitMutation();
                    continue;
                }
                List<UnitData> final = order.Where(home.Members.Contains)
                    .Concat(home.Members.Where(u => !order.Contains(u))).ToList();
                final = final.Where(u => u.IsHero).Concat(final.Where(u => !u.IsHero)).ToList();
                if (!home.Members.SequenceEqual(final)
                    && ArmyActions.ReorderMembers(home, final, out _))
                    WorldDeltaLifecycle.CommitMutation();
            }
            AiDebugLog.Write($"[AI][V2][NIGHT] {player.Nickname} restore ({why}): {moved} unit(s) back "
                + $"to {homes.Count} army(ies)");
            return moved;
        }

        // ---- persistence (AiPersistentState.NightFold) --------------------------------------
        internal static IReadOnlyDictionary<string, IReadOnlyDictionary<int, (int ArmyId, int Index)>> CapturePersistentState(
            Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(Records, playerId,
                r => AiPersistenceCollections.Freeze(r));

        internal static void RestorePersistentState(
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, (int ArmyId, int Index)>> state,
            Func<string, PlayerSetupData> player) =>
            AiPersistenceCollections.Restore(Records, state, player,
                r => r.ToDictionary(kv => kv.Key, kv => kv.Value));
    }
}
