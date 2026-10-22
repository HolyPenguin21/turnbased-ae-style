using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  2026-10-21 (user decision, playtest Vex/Mordak) — the SECOND Attack operation.
    //
    //  One full army beats several weak ones, so the player keeps ONE live Attack operation
    //  (AggressionMissionLayer.LiveAttackOperation). Beside it a second, smaller operation may take
    //  a weaker target, built only from SURPLUS: bodies the strongest army the pool can form never
    //  uses. The peak stack (SelfSnapshot.StrikeRoster, composed over SelfSnapshot.StrikePool:
    //  field armies, the bodies garrisons may spare above their floors, hand and deck) is capped by
    //  one army's command, so whatever lies outside it the main fist can never hold. Mordak T17-T21:
    //  Base (2,1) next to his own Base (2,0) waited five turns behind a parked Assault on (1,-1)
    //  while Pale Horde stood free; on T22 that army alone took it.
    //
    //  The second operation is an ordinary Assault leg (same planner, same assembly kernel, same
    //  march policy) with three extra limits, all checked here:
    //    · its departing roster takes, per card key, no more than the pool holds beyond the peak
    //      roster's positions (UsesOnlySurplus);
    //    · it starts only on a decisive win against freshly observed defenders
    //      (AttackForceReadiness.IsOpportunity) — never the > 80% peak route of the main fist;
    //    · it asks nothing of Demand / Production, opens no gather or preparation and takes no
    //      reinforcement (AttackContinuity.ResolveCommittedAssault).
    //  At most one is live (AggressionMissionLayer.LiveSecondaryAttack).
    // ===========================================================================================
    internal static class AttackSecondaryPolicy
    {
        // THE departure test of a fresh Assault leg, read by the planner (AppendAttack), the
        // admission registry (GroundCombatAdmissionRegistry.RecordAttack) and the provisioning
        // transaction alike: the ordinary march start (AttackMarchPolicy.CanStart) and, for a
        // secondary leg, the surplus roster and the decisive start.
        internal static bool CanStart(WorldSnapshot snap, AttackMissionTarget leg,
            GroundCombatAssemblyPlan candidate, IMissionIntentView intents,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, out string why)
        {
            if (!AttackMarchPolicy.CanStart(snap, leg.Target, candidate, intents, out why))
                return false;
            if (!leg.Secondary)
                return true;
            if (!UsesOnlySurplus(snap, candidate, out why))
            { why = "secondary_" + why; return false; }
            if (!ClearsStart(snap, leg.Target, candidate, opposition, out why))
            { why = "secondary_" + why; return false; }
            return true;
        }

        // Does this plan's departing roster (host members + same-hex transfers − heroes sent home)
        // stay outside the peak strike roster? Keys the peak does not use are free; a key it uses
        // has KeyCounts[k] − peak[k] spare copies. Live units are read through OwnWorld (the plan
        // holds the concrete UnitData it transfers).
        internal static bool UsesOnlySurplus(WorldSnapshot snap, GroundCombatAssemblyPlan plan,
            out string why)
        {
            why = null;
            if (snap?.Self == null || plan == null || !plan.Feasible)
            { why = "no_plan"; return false; }
            ArmyData host = snap.Observer == null ? null : OwnWorld.Of(snap.Observer).Army(plan.BaseArmyId);
            if (host == null)
            { why = "host_gone"; return false; }
            var roster = host.Members.Where(u => u != null && !u.IsPrisoner && !u.IsSummoned)
                .Concat(plan.Transfers.Select(t => t.Unit).Where(u => u != null))
                .Except(plan.ReturnHeroes)
                .Distinct();
            Dictionary<string, int> spare = SurplusByKey(snap.Self);
            var used = new Dictionary<string, int>();
            foreach (UnitData u in roster)
            {
                string key = StrikeRoster.UnitKey(u);
                if (key == null || !spare.TryGetValue(key, out int free))
                    continue;
                used.TryGetValue(key, out int n);
                used[key] = ++n;
                if (n > free)
                {
                    why = $"peak_body:{key}";
                    return false;
                }
            }
            return true;
        }

        // Spare copies of every key the peak roster uses: the pool's count less its positions.
        internal static Dictionary<string, int> SurplusByKey(SelfSnapshot self)
        {
            var peak = new Dictionary<string, int>();
            foreach (StrikeRosterSlot slot in self?.StrikeRoster ?? System.Array.Empty<StrikeRosterSlot>())
                if (slot.Key != null)
                {
                    peak.TryGetValue(slot.Key, out int n);
                    peak[slot.Key] = n + 1;
                }
            var spare = new Dictionary<string, int>();
            foreach (KeyValuePair<string, int> kv in peak)
            {
                int pool = self.StrikePoolKeyCounts != null
                    && self.StrikePoolKeyCounts.TryGetValue(kv.Key, out int c) ? c : kv.Value;
                spare[kv.Key] = System.Math.Max(0, pool - kv.Value);
            }
            return spare;
        }

        // 2026-10-21 (user decision) — a second operation from a garrison that has no field host on
        // its Base: the user's case of a strong garrison next to a weak enemy Base. The same pinned
        // detachment ActiveDefence's immediate sortie uses (GarrisonSortiePlan, GarrisonDetachment):
        // the garrison's spare bodies (above its floor, never its pinned cover) that are also
        // surplus to the peak roster, strongest first, into an empty reusable shell on that hex or
        // one new container, as soon as they take the site decisively AND reach it this turn.
        // The nearest garrison that can do it wins; null when none can.
        internal static GarrisonSortiePlan PlanGarrisonSortie(WorldSnapshot snap, AttackObjective objective,
            float hexBonus, ISet<int> excluded)
        {
            if (snap?.Self?.Armies == null || snap.Observer == null || objective == null || objective.LocationOnly
                || !AttackObjectiveEvaluator.HasAssaultObservation(snap, objective.Target, out _))
                return null;
            Dictionary<string, int> spare = SurplusByKey(snap.Self);
            foreach (ArmySnapshot donor in snap.Self.Armies
                .Where(a => a != null && a.IsGarrison && a.Owner == snap.Observer
                    && (excluded == null || !excluded.Contains(a.ArmyId))
                    && snap.Self.BaseHexes != null && snap.Self.BaseHexes.Contains(a.Hex))
                .OrderBy(a => Game.HexGrid.HexGridMath.Distance(a.Hex, objective.Hex)).ThenBy(a => a.ArmyId))
            {
                ArmyData live = OwnWorld.Of(snap.Observer).Army(donor.ArmyId);
                int? cover = GarrisonDetachment.WeakestCover(donor);
                if (live == null || !cover.HasValue)
                    continue;
                ArmySnapshot shell = snap.Self.Armies.Where(a => a != null && a.Owner == snap.Observer
                    && ReusableArmySelector.IsReusableShell(a, donor.Hex, excluded))
                    .OrderBy(a => a.ArmyId).FirstOrDefault();
                int capacity = shell?.Capacity ?? ArmyData.ComputeCapacity(System.Array.Empty<UnitData>(), false);
                int actorId = shell?.ArmyId ?? -1;
                var left = new Dictionary<string, int>(spare);
                var chosen = new List<int>();
                foreach (int i in GarrisonDetachment.SpareBodyIndices(donor, requireSoloMovement: false)
                    .Where(i => AiArmyRoles.IsGroundBattleBody(donor.Members[i]) && donor.Members[i].HitPoints > 0
                        && donor.NonHeroRuntimeIds[i] != cover.Value))
                {
                    if (chosen.Count >= capacity)
                        break;
                    UnitData unit = live.Members.FirstOrDefault(u => u != null && u.RuntimeId == donor.NonHeroRuntimeIds[i]);
                    if (unit == null)
                        continue;
                    string key = StrikeRoster.UnitKey(unit);
                    if (key != null && left.TryGetValue(key, out int free))
                    {
                        if (free <= 0)
                            continue;   // the main fist's body
                        left[key] = free - 1;
                    }
                    chosen.Add(i);
                    ArmySnapshot projected = GarrisonDetachment.Project(donor, chosen, actorId, capacity, true);
                    if (projected.MemberCount == 1 && (projected.Members[0].Abilities?.Any(a =>
                            Game.Cards.AbilityParams.TryGetRecce(a, out _, out _)) ?? false))
                        continue;
                    if (!SortieTakesSite(projected.Members, objective.Opposition, hexBonus, out float win, out bool covers)
                        || !ActiveDefenceObjectiveEvaluator.CanReachNow(snap, projected, objective.Hex, out int cost)
                        || ActiveDefenceObjectiveEvaluator.DetachmentPinned(snap, donor, chosen, projected, excluded))
                        continue;
                    return new GarrisonSortiePlan(donor, cover.Value, shell, objective.Hex,
                        chosen.Select(c => donor.NonHeroRuntimeIds[c]).ToArray(), projected, cost, win, covers);
                }
            }
            return null;
        }

        // The sortie's fight: a site observed empty takes any body; a defended one the decisive
        // start (raidMinViableWinChance against the known defenders, the site's defence included).
        internal static bool SortieTakesSite(IReadOnlyList<WorthIt.DefenderProfile> members,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, out float win, out bool covers)
        {
            win = 0f; covers = false;
            if (members == null || members.Count == 0)
                return false;
            if (opposition == null || !opposition.Any(a => a.Units.Any(u => u.IsGroundCombatant)))
            { win = 1f; covers = true; return true; }
            // Coverage follows the main Base rule (GroundCombatAdmissionPolicy.RequiresCoverage).
            return GroundCombatFeasibility.Clears(members, default, opposition,
                    GroundCombatAdmissionPolicy.FreshStartWinChanceGate, hexBonus, out win, out covers)
                && AttackForceReadiness.IsOpportunity(win, opposition);
        }

        // Provisioning's and execution's re-proof of the exact pinned bodies before they leave the
        // garrison (GarrisonSortieProvisioning.TryResolve's `evaluate`): still a hostile
        // Base observed, still surplus, still a decisive fight reached this turn, cover intact.
        internal static bool SortieStillTakesSite(WorldSnapshot snap, PlayerSetupData player, AiTurnContext ctx,
            AttackMissionTarget leg, HexCoord from, IReadOnlyList<UnitData> units, out string why)
        {
            why = "attack sortie witness no longer valid";
            GarrisonSortiePlan p = leg.Sortie;
            if (p == null || !leg.Secondary || ctx?.Map == null || snap?.Self == null
                || ctx.HexSelection != null && ctx.HexSelection.IsBattleActive
                || units == null || units.Count == 0 || units.Any(u => !AiArmyRoles.IsGroundBattleBody(u)
                    || u.IsPrisoner || u.HitPointsCurrent <= 0 || u.Owner != player)
                || !p.MatchesRoster(units.Select(u => u.RuntimeId)))
                return false;
            if (!GarrisonSortieProvisioning.CoverHolds(player, p, units, out why))
                return false;
            if (AttackObjectiveEvaluator.EvaluateTarget(snap, leg.Target)
                    != AttackObjectiveEvaluator.AttackTargetStatus.Continue
                || !AttackObjectiveEvaluator.HasAssaultObservation(snap, leg.Target, out _))
            { why = "attack sortie target taken, gone or no longer observed"; return false; }
            Dictionary<string, int> spare = SurplusByKey(snap.Self);
            foreach (IGrouping<string, UnitData> g in units.GroupBy(StrikeRoster.UnitKey))
                if (g.Key != null && spare.TryGetValue(g.Key, out int free) && g.Count() > free)
                { why = $"attack sortie body {g.Key} now belongs to the main fist"; return false; }
            HexCoord site = leg.Target.Hex;
            if (!SortieTakesSite(units.Select(WorthIt.FromLiveUnit).ToList(),
                    KnownOpposition.OnHex(snap, site),
                    AiMapMemory.KnownHexDefenseBonus(snap?.Observer, ctx.Map, site), out _, out _)
                || !ActiveDefenceObjectiveEvaluator.CanReachNow(ctx.Map, player, from, site,
                    ArmyData.ComputeCurrentMovement(units), ArmyData.ComputeMaxMovement(units), out _))
            { why = "attack sortie fight or same-turn movement gate failed"; return false; }
            why = null;
            return true;
        }

        // The start bar of a second operation: a decisive win against defenders seen recently
        // enough to assault (HasAssaultObservation), never an estimate of an unknown site.
        internal static bool ClearsStart(WorldSnapshot snap, AttackTargetRef target,
            GroundCombatAssemblyPlan plan, IReadOnlyList<WorthIt.DefendingArmy> opposition, out string why)
        {
            why = null;
            if (!AttackObjectiveEvaluator.HasAssaultObservation(snap, target, out string intel))
            { why = intel; return false; }
            // A site observed empty right now is the plainest small target there is.
            bool defended = opposition != null && opposition.Any(a => a.Units.Any(u => u.IsGroundCombatant));
            if (defended && !AttackForceReadiness.IsOpportunity(plan?.ProjectedWinChance ?? 0f, opposition))
            { why = $"not_decisive:win {(plan?.ProjectedWinChance ?? 0f):0.00}"; return false; }
            return true;
        }
    }
}
