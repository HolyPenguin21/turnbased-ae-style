using System.Collections.Generic;
using System.Linq;
using Game.Combat;

namespace Game.Ai.V2
{
    // The day planner's candidate generators. Each yields legal zero-AP virtual operations; which
    // one wins is Evaluate's business. The order is the tie order (the first strictly better
    // candidate is kept), so the steps run in this fixed sequence.
    public static partial class ArmyReorganizationPlanner
    {
        private static IEnumerable<VState> EnumerateCandidates(VState state)
        {
            List<int> armyIds = state.Meta.Keys.OrderBy(id => id).ToList();
            IReadOnlyList<WorthIt.DefendingArmy> context = CommandContext(state);
            foreach (VState c in OperatorsHome(state, armyIds)) yield return c;
            foreach (VState c in CommanderReorders(state, armyIds, context)) yield return c;
            foreach (VState c in SupportLedCommanderSwaps(state, armyIds, context)) yield return c;
            foreach (VState c in HerolessFormationLeaders(state, armyIds, context)) yield return c;
            foreach (VState c in ExcessHeroReleases(state, armyIds, context)) yield return c;
            foreach (VState c in PreparationNonRosterReleases(state, armyIds)) yield return c;
            foreach (VState c in PreparationRosterIntake(state, armyIds)) yield return c;
            foreach (VState c in PreparationRosterExchanges(state, armyIds)) yield return c;
            foreach (VState c in GarrisonSurplusToTasks(state, armyIds)) yield return c;
            foreach (VState c in WholeFolds(state, armyIds)) yield return c;
            foreach (VState c in GarrisonDeposits(state, armyIds)) yield return c;
            foreach (VState c in EmptyGarrisonBody(state, armyIds)) yield return c;
            foreach (VState c in WeakArmySeeds(state, armyIds)) yield return c;
            foreach (VState c in FieldRedistribution(state, armyIds)) yield return c;
            foreach (VState c in FieldCompositionSwaps(state, armyIds)) yield return c;
        }

        private static ReorgContainer GarrisonOf(VState state, List<int> armyIds) =>
            armyIds.Select(id => state.Meta[id]).FirstOrDefault(c => c.IsGarrison);

        // A source a benched hero may come from: the garrison or a lone-hero field container.
        private static bool IsBench(VState state, int id)
        {
            ReorgContainer c = state.Meta[id];
            List<ReorgUnit> units = state.Roster[id];
            return c.IsGarrison || (IsFieldContainer(c) && units.Count == 1 && units[0].IsHero);
        }

        // Operators and garrison (Support) heroes in a free field container go to the local
        // garrison, one by one or all of them together: one at a time, only a whole fold of the
        // army cleared every exposed hero in one step and buried its fighters too. The order that
        // keeps every step within capacity (low command first, or the commander first) is the plan.
        private static IEnumerable<VState> OperatorsHome(VState state, List<int> armyIds)
        {
            ReorgContainer garrison = GarrisonOf(state, armyIds);
            if (garrison == null || !garrison.CanReceive)
                yield break;
            int garrisonId = garrison.ArmyId;
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (!IsFieldContainer(src) || !src.CanDonate)
                    continue;
                foreach (ReorgUnit u in state.Roster[srcId]
                             .Where(x => x.IsDevelopmentOperator || x.IsGarrisonHero)
                             .OrderBy(x => x.Key))
                {
                    VState moved = TryMoveOne(state, srcId, garrisonId, u, HomeReason(u),
                        allowDevelopmentOperator: true);
                    if (moved != null)
                        yield return moved;
                }

                List<ReorgUnit> home = state.Roster[srcId]
                    .Where(x => x.IsDevelopmentOperator || x.IsGarrisonHero).ToList();
                if (home.Count < 2)
                    continue;
                foreach (List<ReorgUnit> order in new[]
                         {
                             home.OrderBy(x => x.CommandRating).ThenBy(x => x.Key).ToList(),
                             home.OrderByDescending(x => x.CommandRating).ThenBy(x => x.Key).ToList(),
                         })
                {
                    VState all = state;
                    foreach (ReorgUnit u in order)
                    {
                        all = TryMoveOne(all, srcId, garrisonId, u, HomeReason(u), allowDevelopmentOperator: true);
                        if (all == null)
                            break;
                    }
                    if (all != null)
                    {
                        yield return all;
                        break;
                    }
                }
            }
        }

        private static string HomeReason(ReorgUnit u) => u.IsDevelopmentOperator
            ? "protect Research/Production operator in local garrison"
            : "return a garrison hero to the local garrison";

        // A container with two or more heroes is led by its best commander (BestCommander) so its
        // capacity, initiative and Fate follow it; a garrison's whole hero order is restored in one
        // reorder (a right first hero can hide a wrong tail).
        private static IEnumerable<VState> CommanderReorders(VState state, List<int> armyIds,
            IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            foreach (int armyId in armyIds)
            {
                ReorgContainer meta = state.Meta[armyId];
                if (!meta.CanReorderCommander)
                    continue;
                List<ReorgUnit> units = state.Roster[armyId];
                if (meta.IsGarrison)
                {
                    VState normalized = TryNormalizeGarrison(state, armyId);
                    if (normalized != null)
                        yield return normalized;
                    continue;
                }
                if (units.Count(u => u.IsHero) < 2)
                    continue;
                ReorgUnit current = units.First(u => u.IsHero);
                ReorgUnit best = BestCommander(units, meta, context);
                if (best == null || ReferenceEquals(best, current))
                    continue;
                VState c = TryReorderCommander(state, armyId, best);
                if (c != null)
                    yield return c;
            }
        }

        // A viable formation led by a SupportOperator while a combat-capable hero sits on a bench:
        // the two heroes swap, so the Support hero goes back to the bench and both headcounts stay.
        private static IEnumerable<VState> SupportLedCommanderSwaps(VState state, List<int> armyIds,
            IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            foreach (int dstId in armyIds)
            {
                ReorgContainer dst = state.Meta[dstId];
                if (!IsFieldContainer(dst) || !dst.CanChangeComposition
                    || !ReorgViability.IsViable(state.Roster[dstId]))
                    continue;
                List<ReorgUnit> dstUnits = state.Roster[dstId];
                ReorgUnit supportCommander = dstUnits.FirstOrDefault(u => u.IsHero);
                if (supportCommander == null || supportCommander.HeroRole != HeroOperationalRole.SupportOperator)
                    continue;

                foreach (int srcId in armyIds)
                {
                    if (srcId == dstId)
                        continue;
                    ReorgContainer src = state.Meta[srcId];
                    if (!src.CanDonate || !src.CanReceive || !src.CanChangeComposition || !IsBench(state, srcId))
                        continue;
                    List<ReorgUnit> srcUnits = state.Roster[srcId];
                    ReorgUnit combatHero = BestBenchedHeroForField(srcUnits, dstUnits, context);
                    if (combatHero == null)
                        continue;
                    if (src.IsGarrison && !GarrisonMayRelease(srcUnits, combatHero, src))
                        continue;

                    VState swapped = TrySwap(state, srcId, combatHero, dstId, supportCommander);
                    if (swapped != null)
                        yield return swapped;
                }
            }
        }

        // A heroless viable field formation gets a benched combat hero: a direct move into a free
        // slot, else a hero-for-weakest-body swap (the hero raises the destination's capacity). The
        // greedy loop leads one formation per iteration.
        private static IEnumerable<VState> HerolessFormationLeaders(VState state, List<int> armyIds,
            IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            foreach (int dstId in armyIds)
            {
                ReorgContainer dst = state.Meta[dstId];
                if (!dst.IsFieldReceiver)
                    continue;
                List<ReorgUnit> dstUnits = state.Roster[dstId];
                if (dstUnits.Any(u => u.IsHero) || dstUnits.Count(u => u.IsGroundCombatant) < 2
                    || !ReorgViability.IsViable(dstUnits))
                    continue;

                foreach (int srcId in armyIds)
                {
                    if (srcId == dstId)
                        continue;
                    ReorgContainer src = state.Meta[srcId];
                    if (!src.CanDonate || !IsBench(state, srcId))
                        continue;
                    List<ReorgUnit> srcUnits = state.Roster[srcId];
                    ReorgUnit hero = BestBenchedHeroForField(srcUnits, dstUnits, context);
                    if (hero == null)
                        continue;
                    if (src.IsGarrison && !GarrisonMayRelease(srcUnits, hero, src))
                        continue;

                    VState moved = TryMoveOne(state, srcId, dstId, hero,
                        "assign benched combat hero to heroless field formation");
                    if (moved != null)
                    {
                        yield return moved;
                        continue;
                    }

                    ReorgUnit weakestBody = state.Roster[dstId]
                        .Where(u => u.IsGroundBattleBody && !u.IsCommitted
                            && !state.MovedUnitKeys.Contains(u.Key))
                        .OrderBy(u => u.Power).ThenBy(u => u.Key)
                        .FirstOrDefault();
                    if (weakestBody == null)
                        continue;
                    VState swapped = TrySwap(state, srcId, hero, dstId, weakestBody);
                    if (swapped != null)
                        yield return swapped;
                }
            }
        }

        // A hero that only takes a fighter slot leaves for a free local container (the garrison
        // first, never another claimed receiver): in a free field army, and in an Attack
        // preparation host (its contract's MayReleaseExcessHeroes), where an operator may leave too
        // but only for the local garrison (it stays on its facility's hex). The former commander
        // that is not the best one leaves in the same candidate as the promotion of the best:
        // one step at a time, only a whole fold removed the slot waste and buried both heroes.
        private static IEnumerable<VState> ExcessHeroReleases(VState state, List<int> armyIds,
            IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                bool preparation = src.MayReleaseExcessHeroes;
                if (!preparation && !(IsFieldContainer(src) && src.CanDonate))
                    continue;
                foreach (ReorgUnit hero in ExcessHeroes(state.Roster[srcId], src, context,
                             includeOperators: preparation).Where(h => preparation && h.IsDevelopmentOperator
                                 || !h.IsArmySupportCarrier))
                    foreach (int dstId in OrderedDestinations(state, armyIds, srcId))
                    {
                        if (state.Meta[dstId].IsMissionReceiver)
                            continue;
                        if (hero.IsDevelopmentOperator && !state.Meta[dstId].IsGarrison)
                            continue;
                        VState released = TryMoveOne(state, srcId, dstId, hero,
                            hero.IsDevelopmentOperator
                                ? "keep a Research/Production operator at its facility's garrison"
                                : "release a secondary hero that only takes a fighter slot",
                            allowDevelopmentOperator: hero.IsDevelopmentOperator);
                        if (released != null)
                            yield return released;
                    }

                List<ReorgUnit> roster = state.Roster[srcId];
                if (roster.Count(u => u.IsHero) < 2 || !src.CanReorderCommander)
                    continue;
                ReorgUnit current = roster.First(u => u.IsHero);
                ReorgUnit lead = BestCommander(roster, src, context);
                if (lead == null || ReferenceEquals(lead, current) || current.IsCommitted
                    || current.IsArmySupportCarrier || (current.IsDevelopmentOperator && !preparation))
                    continue;
                VState promoted = TryReorderCommander(state, srcId, lead);
                if (promoted == null)
                    continue;
                foreach (int dstId in OrderedDestinations(promoted, armyIds, srcId))
                {
                    if (promoted.Meta[dstId].IsMissionReceiver)
                        continue;
                    if (current.IsDevelopmentOperator && !promoted.Meta[dstId].IsGarrison)
                        continue;
                    VState released = TryMoveOne(promoted, srcId, dstId, current,
                        "release the former commander after the best one took command",
                        allowDevelopmentOperator: current.IsDevelopmentOperator);
                    if (released != null)
                        yield return released;
                }
            }
        }

        // A preparation host releases a body its target roster does not need, weakest first, to a
        // free local container — only while a source of a missing position is actually blocked by
        // the slot (ReorgViability.PreparationBlockedSlots), never as a generic donation.
        private static IEnumerable<VState> PreparationNonRosterReleases(VState state, List<int> armyIds)
        {
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (src.PreparationTargetKeys == null
                    || ReorgViability.PreparationBlockedSlots(src, state.Roster[srcId], Rosters(state)) == 0)
                    continue;
                var nonRoster = new List<ReorgUnit>();
                ReorgViability.RosterNeed(src.PreparationTargetKeys, state.Roster[srcId], nonRoster);
                nonRoster.Reverse();
                foreach (ReorgUnit body in nonRoster.Where(u => !u.IsCommitted
                             && !state.MovedUnitKeys.Contains(u.Key)))
                    foreach (int dstId in OrderedDestinations(state, armyIds, srcId))
                    {
                        if (state.Meta[dstId].IsMissionReceiver)
                            continue;
                        VState released = TryMoveOne(state, srcId, dstId, body,
                            "release a non-roster body of the preparation host for a missing position");
                        if (released != null)
                            yield return released;
                    }
            }
        }

        // A preparation host takes a same-hex body of a missing roster position from a container
        // that may give (garrison floors and capacity are TryMoveOne's).
        private static IEnumerable<VState> PreparationRosterIntake(VState state, List<int> armyIds)
        {
            foreach (int hostId in armyIds)
            {
                ReorgContainer host = state.Meta[hostId];
                if (host.PreparationTargetKeys == null || !host.CanReceive)
                    continue;
                Dictionary<string, int> need = ReorgViability.RosterNeed(host.PreparationTargetKeys, state.Roster[hostId]);
                foreach (int srcId in armyIds)
                {
                    if (srcId == hostId || !state.Meta[srcId].CanDonate)
                        continue;
                    foreach (ReorgUnit u in state.Roster[srcId].Where(x => !x.IsHero
                                 && !x.IsAviation && !x.IsCommitted && x.StrikeKey != null
                                 && need.TryGetValue(x.StrikeKey, out int n) && n > 0)
                                 .OrderByDescending(x => x.Power).ThenBy(x => x.Key))
                    {
                        VState taken = TryMoveOne(state, srcId, hostId, u,
                            "a missing roster position joins the preparation host");
                        if (taken != null)
                            yield return taken;
                    }
                }
            }
        }

        // A preparation host exchanges a body its target roster does not contain for a garrison
        // body of a missing position, 1-for-1, the garrison keeping min(held, floor) (TrySwap
        // rosterExchange): a garrison at its floor can never release that body otherwise.
        private static IEnumerable<VState> PreparationRosterExchanges(VState state, List<int> armyIds)
        {
            foreach (int hostId in armyIds)
            {
                ReorgContainer host = state.Meta[hostId];
                if (host.PreparationTargetKeys == null || !host.CanReceive)
                    continue;
                var nonRoster = new List<ReorgUnit>();
                Dictionary<string, int> need = ReorgViability.RosterNeed(host.PreparationTargetKeys,
                    state.Roster[hostId], nonRoster);
                nonRoster = nonRoster.Where(u => !u.IsCommitted && !u.IsAviation
                    && !state.MovedUnitKeys.Contains(u.Key)).ToList();
                if (nonRoster.Count == 0)
                    continue;
                nonRoster.Reverse(); // the weakest non-roster body goes first
                foreach (int gId in armyIds)
                {
                    ReorgContainer g = state.Meta[gId];
                    if (gId == hostId || !g.IsGarrison || !g.CanChangeComposition)
                        continue;
                    foreach (ReorgUnit u in state.Roster[gId].Where(x => !x.IsHero
                                 && !x.IsAviation && !x.IsCommitted && x.StrikeKey != null
                                 && !state.MovedUnitKeys.Contains(x.Key)
                                 && need.TryGetValue(x.StrikeKey, out int n) && n > 0)
                                 .OrderByDescending(x => x.Power).ThenBy(x => x.Key))
                        foreach (ReorgUnit body in nonRoster)
                        {
                            VState exchanged = TrySwap(state, hostId, body, gId, u, rosterExchange: true);
                            if (exchanged != null)
                                yield return exchanged;
                        }
                }
            }
        }

        // The garrison keeps its release floor (GarrisonMayRelease: the defence power floor, one body
        // always stays) and gives the bodies above it to an army whose task takes units
        // (ArmyMutationContract.NeedsUnits: never a return leg) or to the hex's fist, which takes
        // only what keeps the garrison's own share (the muster Intake's rule). A free army without a
        // task gets nothing. Strongest first; one candidate per body and one with the whole surplus,
        // so the climb never stalls on a single step.
        private static IEnumerable<VState> GarrisonSurplusToTasks(VState state, List<int> armyIds)
        {
            foreach (int gId in armyIds)
            {
                ReorgContainer g = state.Meta[gId];
                if (!g.IsGarrison || !g.CanDonate)
                    continue;
                List<ReorgUnit> spare = state.Roster[gId]
                    .Where(u => !u.IsHero && u.IsGroundBattleBody && !u.IsAviation
                        && !u.IsCommitted && !state.MovedUnitKeys.Contains(u.Key))
                    .OrderByDescending(u => AiPower.SkilledCombatValue(u.CombatProfile))
                    .ThenBy(u => u.Key).ToList();
                if (spare.Count == 0)
                    continue;
                foreach (int hostId in armyIds)
                {
                    ReorgContainer host = state.Meta[hostId];
                    if (hostId == gId || host.IsGarrison || !(host.NeedsUnits || host.IsFist) || !host.CanReceive)
                        continue;
                    string why = host.IsFist ? "a garrison body above its own share joins the hex's fist"
                        : "a garrison body above its floor joins an army with a task";
                    bool KeepsShare(VState v) => !host.IsFist || ReorgViability.EffectivePower(
                        v.Roster[gId].Where(x => x.IsGroundBattleBody).ToList()) + 0.001f
                        >= g.GarrisonOwnShareFloor;
                    foreach (ReorgUnit u in spare)
                    {
                        VState one = TryMoveOne(state, gId, hostId, u, why);
                        if (one != null && KeepsShare(one))
                            yield return one;
                    }
                    VState all = state;
                    int moved = 0;
                    foreach (ReorgUnit u in spare)
                    {
                        VState next = TryMoveOne(all, gId, hostId, u, why);
                        if (next == null || !KeepsShare(next))
                            continue;
                        all = next;
                        moved++;
                    }
                    if (moved >= 2)
                        yield return all;
                }
            }
        }

        // Any occupied mutable field container may fold whole into another local container when the
        // transfer is legal (generation owns legality, Evaluate owns policy). The emptied army stays
        // as a reusable shell: Housekeeping never destroys containers.
        private static IEnumerable<VState> WholeFolds(VState state, List<int> armyIds)
        {
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (!IsFieldContainer(src) || !src.CanDonate)
                    continue;
                List<ReorgUnit> srcUnits = state.Roster[srcId];
                if (srcUnits.Count == 0 || srcUnits.Any(u =>
                    u.IsCommitted || u.IsAviation || state.MovedUnitKeys.Contains(u.Key)))
                    continue;

                foreach (int dstId in OrderedDestinations(state, armyIds, srcId))
                {
                    VState c = TryWholeFold(state, srcId, dstId);
                    if (c != null)
                        yield return c;
                }
            }
        }

        // A member of a singleton or non-viable field container goes into the local garrison.
        private static IEnumerable<VState> GarrisonDeposits(VState state, List<int> armyIds)
        {
            ReorgContainer garrison = GarrisonOf(state, armyIds);
            if (garrison == null || !garrison.CanReceive)
                yield break;
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (!IsFieldContainer(src) || !src.CanDonate)
                    continue;
                List<ReorgUnit> srcUnits = state.Roster[srcId];
                if (srcUnits.Count == 0)
                    continue;
                if (ReorgViability.IsViable(srcUnits) && !ReorgViability.IsSingletonShape(srcUnits))
                    continue;
                foreach (ReorgUnit u in srcUnits.OrderBy(x => x.Key))
                {
                    VState c = TryMoveOne(state, srcId, garrison.ArmyId, u,
                        "singleton/weak deposit into garrison");
                    if (c != null)
                        yield return c;
                }
            }
        }

        // A garrison below its body floor (a freshly taken base) takes ONE body from a viable
        // same-hex field army: the most wounded, then the weakest, never a hero, and only while the
        // army stays viable without it (GarrisonDeficit outranks every other term, so the guard
        // lives here).
        private static IEnumerable<VState> EmptyGarrisonBody(VState state, List<int> armyIds)
        {
            ReorgContainer garrison = GarrisonOf(state, armyIds);
            if (garrison == null || !garrison.CanReceive
                || state.Roster[garrison.ArmyId].Count(u => u.IsGroundCombatant) >= garrison.GarrisonNonHeroFloor)
                yield break;
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (!IsFieldContainer(src) || !src.CanDonate
                    || !ReorgViability.IsViable(state.Roster[srcId]))
                    continue;
                ReorgUnit body = state.Roster[srcId]
                    .Where(u => !u.IsHero && u.IsGroundBattleBody && !u.IsAviation && !u.IsCommitted)
                    .OrderBy(u => u.CombatProfile.MaxHitPoints > 0f
                        ? u.CombatProfile.HitPoints / u.CombatProfile.MaxHitPoints : 1f)
                    .ThenBy(u => AiPower.SkilledCombatValue(u.CombatProfile))
                    .ThenBy(u => u.Key)
                    .FirstOrDefault();
                if (body == null
                    || !ReorgViability.IsViable(state.Roster[srcId].Where(u => u != body).ToList()))
                    continue;
                VState c = TryMoveOne(state, srcId, garrison.ArmyId, body,
                    "garrison floor from a viable same-hex field army (most wounded / weakest)");
                if (c != null)
                    yield return c;
            }
        }

        // An occupied weak field container is seeded from a viable field donor. Empty shells are
        // useful future containers, not defects; and the garrison never lends bodies to make a
        // purposeless weak army viable (that force is folded instead).
        private static IEnumerable<VState> WeakArmySeeds(VState state, List<int> armyIds)
        {
            foreach (int weakId in armyIds)
            {
                ReorgContainer weak = state.Meta[weakId];
                if (!weak.IsFieldReceiver)
                    continue;
                List<ReorgUnit> weakUnits = state.Roster[weakId];
                if (weakUnits.Count == 0 || ReorgViability.IsViable(weakUnits) || weak.SingletonExempt)
                    continue;

                foreach (int donorId in armyIds)
                {
                    if (donorId == weakId)
                        continue;
                    ReorgContainer donor = state.Meta[donorId];
                    if (!IsFieldContainer(donor) || !donor.CanDonate
                        || !ReorgViability.IsViable(state.Roster[donorId]))
                        continue;
                    VState c = TrySeed(state, donorId, weakId);
                    if (c != null)
                        yield return c;
                }
            }
        }

        // One-way concentration / composition moves between viable field armies. Both rosters need
        // not stay viable: Evaluate rejects a bad intermediate, while a path that leaves an empty
        // shell can win through the strongest-first profile.
        private static IEnumerable<VState> FieldRedistribution(VState state, List<int> armyIds)
        {
            foreach (int srcId in armyIds)
            {
                ReorgContainer src = state.Meta[srcId];
                if (!IsFieldContainer(src) || !src.CanDonate
                    || !ReorgViability.IsViable(state.Roster[srcId]))
                    continue;

                foreach (int dstId in armyIds)
                {
                    if (dstId == srcId)
                        continue;
                    ReorgContainer dst = state.Meta[dstId];
                    if (!dst.IsFieldReceiver || !ReorgViability.IsViable(state.Roster[dstId]))
                        continue;

                    foreach (ReorgUnit u in state.Roster[srcId].OrderBy(x => x.Key))
                    {
                        VState c = TryMoveOne(state, srcId, dstId, u,
                            "combat concentration/composition redistribution");
                        if (c != null)
                            yield return c;
                    }
                }
            }
        }

        // 1-for-1 composition swaps between two viable full field armies (field only: a garrison's
        // exchanges have their own floor rule).
        private static IEnumerable<VState> FieldCompositionSwaps(VState state, List<int> armyIds)
        {
            for (int i = 0; i < armyIds.Count; i++)
            {
                int aId = armyIds[i];
                ReorgContainer a = state.Meta[aId];
                if (!IsFieldContainer(a) || !a.CanChangeComposition
                    || !ReorgViability.IsViable(state.Roster[aId]))
                    continue;

                for (int j = i + 1; j < armyIds.Count; j++)
                {
                    int bId = armyIds[j];
                    ReorgContainer b = state.Meta[bId];
                    if (!IsFieldContainer(b) || !b.CanChangeComposition
                        || !ReorgViability.IsViable(state.Roster[bId]))
                        continue;

                    foreach (ReorgUnit ua in state.Roster[aId].OrderBy(x => x.Key))
                        foreach (ReorgUnit ub in state.Roster[bId].OrderBy(x => x.Key))
                        {
                            VState c = TrySwap(state, aId, ua, bId, ub);
                            if (c != null)
                                yield return c;
                        }
                }
            }
        }

        // ---- commander choice ------------------------------------------------------------------

        // A benched (garrison / lone) hero that may lead `field`: never a SupportOperator, a
        // committed hero or an operator; among the rest, the commander evaluation's best for the
        // formation's own bodies.
        private static ReorgUnit BestBenchedHeroForField(List<ReorgUnit> roster,
            List<ReorgUnit> field, IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            List<WorthIt.DefenderProfile> bodies = CommandBodies(field);
            return roster
                .Where(u => u.IsHero && !u.IsCommitted && !u.IsDevelopmentOperator
                    && u.HeroRole != HeroOperationalRole.SupportOperator)
                .Select(u => (unit: u, candidate: CommandCandidateFor(u, bodies,
                    System.Array.Empty<ReorgUnit>(), context)))
                .OrderBy(x => x.candidate, Comparer<HeroRoleEvaluator.CommandCandidate>.Create(
                    HeroRoleEvaluator.CompareCandidates))
                .Select(x => x.unit)
                .FirstOrDefault();
        }

        // The fight a commander is judged against: HeroRoleEvaluator's strongest-enemy context
        // over this group's benchmarks.
        private static IReadOnlyList<WorthIt.DefendingArmy> CommandContext(VState state) =>
            HeroRoleEvaluator.CommandContext(state.ThreatBenchmarks
                .Where(t => t != null)
                .Select(t => (t.ArmyId, t.Members, t.Commander)));

        // The best commander among the heroes in `units` (null when none). Only a hero the whole
        // roster fits under may lead it; with none, the current commander stays. A garrison follows
        // its canonical order (ArmyData.NormalizeRoster). A free field army (nothing more joins it)
        // seats only the bodies it has — the fist also what the garrison can still give it — so
        // command capacity beyond them decides nothing (HeroRoleEvaluator.CompareCandidates with
        // needed body slots); a claimed receiver is still gathering and keeps the full order.
        private static ReorgUnit BestCommander(List<ReorgUnit> units, ReorgContainer meta,
            IReadOnlyList<WorthIt.DefendingArmy> context)
        {
            bool isGarrison = meta.IsGarrison;
            int neededBodySlots = !isGarrison && !meta.IsMissionReceiver && meta.CanDonate
                ? units.Count(u => u.IsGroundCombatant) + (meta.IsFist ? meta.FistGarrisonSpare : 0)
                : -1;
            List<ReorgUnit> heroes = units.Where(u => u.IsHero).ToList();
            if (heroes.Count == 0)
                return null;
            if (isGarrison)
                return ReorgViability.NormalizeRoster(heroes)[0];
            List<ReorgUnit> legal = heroes
                .Where(h => ReorgViability.Capacity(LedBy(units, h), isGarrison) >= units.Count)
                .ToList();
            if (legal.Count == 0)
                return heroes[0];
            List<WorthIt.DefenderProfile> bodies = CommandBodies(units);
            return legal
                .Select(h => (unit: h, candidate: CommandCandidateFor(h, bodies,
                    heroes.Where(x => !ReferenceEquals(x, h)).ToList(), context)))
                .OrderBy(x => x.candidate, Comparer<HeroRoleEvaluator.CommandCandidate>.Create(
                    (a, b) => neededBodySlots >= 0
                        ? HeroRoleEvaluator.CompareCandidates(a, b, neededBodySlots)
                        : HeroRoleEvaluator.CompareCandidates(a, b)))
                .First().unit;
        }

        // Heroes beyond the commander that only take a fighter slot (a hero has no power of its
        // own). `keepGarrisonHeroes` — a free field army does not count its Support heroes here:
        // their exposure is OperatorExposure's. Operators, committed units and army support
        // carriers never count.
        private static int HeroSlotWaste(List<ReorgUnit> units, bool keepGarrisonHeroes) =>
            units.Where(u => u.IsHero).Skip(1)
                .Count(u => !u.IsDevelopmentOperator && !u.IsCommitted && !u.IsArmySupportCarrier
                    && !(keepGarrisonHeroes && u.IsGarrisonHero));

        // The heroes that may leave: neither the current commander nor the best legal leader (the
        // old commander becomes releasable after the zero-AP reorder).
        private static List<ReorgUnit> ExcessHeroes(List<ReorgUnit> units, ReorgContainer meta,
            IReadOnlyList<WorthIt.DefendingArmy> context, bool includeOperators = false)
        {
            if (units.Count(u => u.IsHero) < 2)
                return new List<ReorgUnit>();
            ReorgUnit current = units.First(u => u.IsHero);
            ReorgUnit lead = BestCommander(units, meta, context);
            return units.Where(u => u.IsHero && !ReferenceEquals(u, lead)
                    && !ReferenceEquals(u, current) && (includeOperators || !u.IsDevelopmentOperator)
                    && !u.IsCommitted)
                .OrderBy(u => u.Key).ToList();
        }

        // A claimed receiver takes a hero only as its better commander, keeping at least the room
        // for bodies it had: a hero that would only occupy a fighter slot never enters it.
        private static bool MissionReceiverTakesHero(VState state, List<ReorgUnit> dest,
            ReorgUnit hero, ReorgContainer meta)
        {
            int BodyRoom(List<ReorgUnit> roster) =>
                ReorgViability.Capacity(roster, meta.IsGarrison) - roster.Count(x => x.IsHero);
            var after = new List<ReorgUnit>(dest);
            ReorgViability.AddMemberSorted(after, hero, meta.IsGarrison);
            ReorgUnit lead = meta.CanReorderCommander || !dest.Any(x => x.IsHero)
                ? BestCommander(after, meta, CommandContext(state))
                : after.First(x => x.IsHero);
            return ReferenceEquals(lead, hero) && BodyRoom(LedBy(after, hero)) >= BodyRoom(dest);
        }

        // `units` with `hero` moved to the commander slot (TryReorderCommander's roster order).
        private static List<ReorgUnit> LedBy(List<ReorgUnit> units, ReorgUnit hero)
        {
            var roster = new List<ReorgUnit>(units.Count) { hero };
            roster.AddRange(units.Where(u => !ReferenceEquals(u, hero)));
            return roster;
        }

        private static List<WorthIt.DefenderProfile> CommandBodies(List<ReorgUnit> units) =>
            units.Where(u => u.IsGroundCombatant).Select(u => u.CombatProfile).ToList();

        // `otherHeroes` stay in the formation under this commander: they take slots, and their
        // Heal/Drill act in its fight exactly as under any other commander.
        private static HeroRoleEvaluator.CommandCandidate CommandCandidateFor(ReorgUnit hero,
            IEnumerable<WorthIt.DefenderProfile> bodies, IReadOnlyList<ReorgUnit> otherHeroes,
            IReadOnlyList<WorthIt.DefendingArmy> context) =>
            new HeroRoleEvaluator.CommandCandidate(
                HeroRoleEvaluator.ProjectCommand(hero.CommandRating, otherHeroes.Count,
                    hero.AsCommander.WithSupport(otherHeroes.Select(u => u.CombatProfile)),
                    bodies, context, 0f),
                HeroRoleEvaluator.RolePreference(hero.HeroRole), hero.HeroCombatLeadership,
                hero.CommandRating, hero.AsCommander.Fate, hero.Key);

        // Destinations for a released member: viable field armies, then occupied weaker ones, then
        // the garrison, then empty shells.
        private static IEnumerable<int> OrderedDestinations(VState state, List<int> armyIds, int srcId)
        {
            var viable = new List<int>();
            var occupied = new List<int>();
            var shells = new List<int>();
            int garrison = -1;

            foreach (int id in armyIds)
            {
                if (id == srcId)
                    continue;
                ReorgContainer c = state.Meta[id];
                if (!c.CanReceive)
                    continue;
                if (c.IsGarrison) { garrison = id; continue; }
                if (!c.IsFieldReceiver)
                    continue;
                if (state.Roster[id].Count == 0) { shells.Add(id); continue; }
                if (ReorgViability.IsViable(state.Roster[id])) viable.Add(id);
                else occupied.Add(id);
            }

            foreach (int id in viable) yield return id;
            foreach (int id in occupied) yield return id;
            if (garrison >= 0) yield return garrison;
            foreach (int id in shells) yield return id;
        }

        // Every container's current virtual roster, the shape ReorgViability's roster rules read.
        private static IEnumerable<KeyValuePair<ReorgContainer, List<ReorgUnit>>> Rosters(VState state) =>
            state.Meta.Select(kv => new KeyValuePair<ReorgContainer, List<ReorgUnit>>(kv.Value, state.Roster[kv.Key]));
    }
}
