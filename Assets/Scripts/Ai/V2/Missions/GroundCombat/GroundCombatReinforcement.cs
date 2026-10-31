using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // What one reinforcement / gather handoff will do, decided once (GroundCombatReinforcement.
    // PlanHandoff, or CommandHandover for the hero exchange): who goes support -> primary (a
    // handed-over hero first), who goes primary -> support, and the hero that takes command, if
    // any. The leg's AP (HandoffApCost) and the executed transfer read the same plan.
    internal sealed class HandoffPlan
    {
        internal readonly IReadOnlyList<UnitData> Incoming;
        internal readonly IReadOnlyList<UnitData> Displaced;
        internal readonly UnitData Promote;
        internal readonly string Detail;

        internal HandoffPlan(IReadOnlyList<UnitData> incoming, IReadOnlyList<UnitData> displaced,
            UnitData promote, string detail)
        {
            Incoming = incoming;
            Displaced = displaced ?? System.Array.Empty<UnitData>();
            Promote = promote;
            Detail = detail;
        }
    }

    // ATK §28/§46 — reinforcement admission belongs to the GroundCombat kernel, not to a lane.
    // Both halves were private to the Raid provisioner; nothing in either is Raid-specific.
    internal static class GroundCombatReinforcement
    {
        // Would merging the support's transferable bodies into the primary raise its WorthIt win
        // chance against the current opposition, on the hex where the fight will happen — or, for
        // a lane that hands command over (Attack), would its hero take command of the primary
        // (CommandHandover)? A convoy that cannot help is never provisioned.
        internal static bool ImprovesOdds(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            out string why, bool allowCommandHandover = false,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null)
        {
            opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            HandoffPlan plan = PlanHandoff(primary, support,
                allowCommandHandover ? opposition : null, defenderHexDefenseBonus, out why,
                requireChargeNow: false, rosterAdmissible: rosterAdmissible);
            if (plan == null) return false;
            if (allowCommandHandover && plan.Promote != null) return true;
            var before = primary.Members.Where(AiArmyRoles.IsGroundBattleBody)
                .Select(WorthIt.FromLiveUnit).ToList();
            var afterRoster = FinalRoster(primary, plan);
            var after = afterRoster.Where(AiArmyRoles.IsGroundBattleBody)
                .Select(WorthIt.FromLiveUnit).ToList();
            bool improves = WorthIt.EstimateSequential(after, WorthIt.SideCommander.Of(afterRoster),
                    opposition, defenderHexDefenseBonus).WinChance
                > WorthIt.EstimateSequential(before, WorthIt.SideCommander.Of(primary.Members),
                    opposition, defenderHexDefenseBonus).WinChance + 0.001f;
            if (!improves) why = "projected legal handoff does not improve win chance";
            return improves;
        }

        // The Attack lane needs power delivered into the actual primary, not a new shell
        // elsewhere on the map or a Monte-Carlo win change that can be zero at saturation.
        internal static bool ImprovesAttackForce(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, out string why,
            bool requireChargeNow = false, bool capacityIsProgress = false,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null) =>
            PlanAttackHandoff(primary, support, opposition, hexBonus, requireChargeNow, out why,
                capacityIsProgress, rosterAdmissible) != null;

        // ATK-F04 — THE Attack handoff, one read-only plan for the gather projection
        // (SupportImprovesPrimary), Continuity's support drop, Provisioning's leg check and the
        // executed transfer. The legal command handover and the legal bodies-only transfer are
        // alternatives: the handover is kept when it raises the primary (power, or the defender
        // coverage it lacked), otherwise the bodies-only plan when that one does — a hero that does
        // not strengthen the fist never displaces a useful body. `requireChargeNow` false projects a
        // handoff that happens on the support's arrival (container/capacity legality only; its
        // activation charge is priced by ProjectedHandoffApCost, not paid from today's AP). Null
        // with the exact `why` (both alternatives, with power before/after) when neither helps.
        // `capacityIsProgress` — 2026-10-01 (variant B): the preparation's fetched commander. Its
        // lone hero takes command (joining, or exchanged for the weakest body of a full host) and
        // its larger Command is the progress, as for the same-hex capacity hero of a preparation.
        internal static HandoffPlan PlanAttackHandoff(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, bool requireChargeNow,
            out string why, bool capacityIsProgress = false,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null)
        {
            opposition = opposition ?? System.Array.Empty<WorthIt.DefendingArmy>();
            why = "";
            if (primary == null || support == null)
            {
                why = "primary or support is gone";
                return null;
            }
            float before = AiPower.EffectiveArmyPower(primary.Members);
            string Judge(HandoffPlan plan, out bool improves)
            {
                var roster = primary.Members.Except(plan.Displaced).Concat(plan.Incoming).ToList();
                float after = AiPower.EffectiveArmyPower(roster);
                improves = RaisesPrimary(primary.Members, roster, opposition, hexBonus);
                if (!improves && capacityIsProgress && plan.Promote != null)
                {
                    var led = new List<UnitData> { plan.Promote };
                    led.AddRange(roster.Where(u => u != plan.Promote));
                    improves = ArmyData.ComputeCapacity(led, false)
                        > ArmyData.ComputeCapacity(primary.Members, primary.IsGarrison);
                }
                return $"{plan.Detail} (in=[{string.Join(",", plan.Incoming.Select(u => u.Name))}] "
                    + $"out=[{string.Join(",", plan.Displaced.Select(u => u.Name))}] "
                    + $"power {before:0.#}->{after:0.#})";
            }

            HandoffPlan first = PlanHandoff(primary, support, opposition, hexBonus,
                out string firstWhy, requireChargeNow: requireChargeNow, rosterAdmissible: rosterAdmissible,
                siteOpposition: opposition);
            string commandJudged = null;
            if (first != null)
            {
                string judged = Judge(first, out bool improves);
                if (improves)
                    return first;
                if (first.Promote == null)
                {
                    // No hero moved: this already was the bodies-only plan.
                    why = $"bodies-only handoff does not raise power or coverage: {judged}";
                    return null;
                }
                commandJudged = judged;
            }
            HandoffPlan bodies = PlanHandoff(primary, support, null, 0f, out string bodiesWhy,
                requireChargeNow: requireChargeNow, rosterAdmissible: rosterAdmissible,
                siteOpposition: opposition);
            string bodiesJudged = null;
            if (bodies != null)
            {
                bodiesJudged = Judge(bodies, out bool bodiesImprove);
                if (bodiesImprove)
                    return bodies;
            }
            why = $"command handover: {commandJudged ?? firstWhy}; bodies-only: "
                + $"{bodiesJudged ?? bodiesWhy} - neither raises power or coverage";
            return null;
        }

        // Does `after` (the primary's roster once the exchange ran) raise the primary: more power,
        // or — a zero-power exchange — the only weapon able to hurt a known defender it could not
        // damage before, or the first Disruption carrier against a known structure defence
        // (GroundCombatDonorPolicy.DisruptionHelps)? The one rule of the Attack handoff and of the
        // gather projection's command handover.
        internal static bool RaisesPrimary(IReadOnlyList<UnitData> before, IReadOnlyList<UnitData> after,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus) =>
            AiPower.EffectiveArmyPower(after) > AiPower.EffectiveArmyPower(before)
            || !WorthIt.CanDamageAll(before.Select(WorthIt.FromLiveUnit).ToList(), opposition, hexBonus)
                && WorthIt.CanDamageAll(after.Select(WorthIt.FromLiveUnit).ToList(), opposition, hexBonus)
            || GroundCombatDonorPolicy.DisruptionHelps(before, opposition)
                && !GroundCombatDonorPolicy.DisruptionHelps(after, opposition);

        // Strike force — THE "hand the support's hero over to lead the primary" rule, exchanges
        // included. A field hero of the support (never a SupportOperator) moves when, joined to the
        // primary, it is the best commander of the primary's fight (HeroRoleEvaluator.
        // BestCommanderFor, judged with `prospectiveBodies` — the bodies that would come along —
        // or the support's own bodies). When the primary has no room for the hero itself, the hero
        // is exchanged for the primary's most wounded, then weakest, body. Under the hero's
        // Command the free slots take the support's strongest bodies; every further support body
        // that beats the primary's weakest remaining body is exchanged for it (the same "fresh for
        // weakest" rule as the bodies-only swap). The support must stay a legally sized container
        // with what it keeps and what it receives; it may be emptied (the game disbands an empty
        // army only at the end of its owner's turn). Null when no hero should move. Used
        // by the gather projection, the gather plan's donor retention, the reinforcement gate and
        // the handoff — one answer for all four.
        internal static HandoffPlan CommandHandover(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            IEnumerable<WorthIt.DefenderProfile> prospectiveBodies, bool requireChargeNow = false,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null)
        {
            if (primary == null || support == null || primary.IsGarrison)
                return null;
            List<UnitData> donorBodies = support.Members
                .Where(x => AiArmyRoles.IsGroundBattleBody(x))
                .OrderByDescending(GroundCombatDonorPolicy.UnitCombatValue)
                .ThenBy(x => x.Name)
                .ToList();
            List<WorthIt.DefenderProfile> prospects = (prospectiveBodies
                ?? donorBodies.Select(WorthIt.FromLiveUnit)).ToList();
            UnitData weakestHostBody = WeakestBodies(primary.Members).FirstOrDefault();

            foreach (UnitData hero in support.Members
                .Where(u => u != null && u.IsHero && !u.IsPrisoner
                    && HeroRoleEvaluator.Classify(u) != HeroOperationalRole.SupportOperator)
                .OrderByDescending(HeroRoleEvaluator.CombatLeadershipScore)
                .ThenBy(u => u.RuntimeId))
            {
                // First prove this incoming hero wins the existing command contest, including
                // the current commander. Removing old heroes must not manufacture an upgrade.
                var contest = new List<UnitData>(primary.Members) { hero };
                if (ArmyData.ComputeCapacity(new[] { hero }.Concat(primary.Members), false) < contest.Count)
                {
                    if (weakestHostBody == null) continue;
                    contest.Remove(weakestHostBody);
                }
                if (HeroRoleEvaluator.BestCommanderFor(contest, false, opposition,
                    defenderHexDefenseBonus, prospects) != hero) continue;
                // Command replacement is one complete exchange, not an accumulating hero join.
                // Keep passive carriers; return ordinary old heroes when the support can take
                // them. The retained variant is still available when that exchange is illegal.
                var ordinaryHeroes = primary.Members.Where(u => u != null && u.IsHero
                    && !StrategicEffectRegistry.IsArmySupportCarrier(u)).ToList();
                for (int variant = 0; variant < (ordinaryHeroes.Count > 0 ? 2 : 1); variant++)
                {
                    var returning = variant == 0 ? ordinaryHeroes : new List<UnitData>();
                    var retained = primary.Members.Except(returning).ToList();
                    // Joined without displacing anyone, or — no room for the hero itself — exchanged
                    // for the primary's weakest body.
                    UnitData heroFor = null;
                    var joined = new List<UnitData>(retained) { hero };
                    if (ArmyData.ComputeCapacity(new[] { hero }.Concat(retained), false) < joined.Count)
                    {
                        if (weakestHostBody == null)
                            continue;
                        heroFor = weakestHostBody;
                        joined.Remove(heroFor);
                    }
                    if (HeroRoleEvaluator.BestCommanderFor(joined, false, opposition,
                            defenderHexDefenseBonus, prospects) != hero)
                        continue;

                    foreach (var selectedBodies in BodyAlternatives(donorBodies, rosterAdmissible != null))
                    {
                        var incoming = new List<UnitData> { hero };
                        var displaced = new List<UnitData>(returning);
                        if (heroFor != null)
                            displaced.Add(heroFor);
                        var led = new List<UnitData> { hero };
                        led.AddRange(retained.Where(u => u != heroFor));
                        int room = System.Math.Max(0, ArmyData.ComputeCapacity(led, false) - led.Count);
                        var fresh = new Queue<UnitData>(selectedBodies);
                        while (room > 0 && fresh.Count > 0)
                        {
                            incoming.Add(fresh.Dequeue());
                            room--;
                        }
                        var weakest = new Queue<UnitData>(WeakestBodies(retained.Where(u => u != heroFor)));
                        while (fresh.Count > 0 && weakest.Count > 0
                            && GroundCombatDonorPolicy.UnitCombatValue(fresh.Peek())
                                > GroundCombatDonorPolicy.UnitCombatValue(weakest.Peek()))
                        {
                            incoming.Add(fresh.Dequeue());
                            displaced.Add(weakest.Dequeue());
                        }

                        // Without its hero the support must still hold what it keeps and receives; a
                        // support that cannot does not give this hero away.
                        if (!SupportStaysLegal(support, incoming, displaced))
                            continue;
                        var final = new[] { hero }.Concat(primary.Members.Except(displaced))
                            .Concat(incoming.Where(u => u != hero)).ToList();
                        if (rosterAdmissible != null && !rosterAdmissible(final)
                            || !ArmyActions.CanExchangeMembers(incoming, support, primary, hero,
                                displaced, out _, requireChargeNow))
                            continue;
                        return new HandoffPlan(incoming, displaced, hero, $"hero {hero.Name} took command"
                            + (heroFor != null ? $" in exchange for {heroFor.Name}" : "")
                            + $"; {incoming.Count - 1} body(ies) in, {displaced.Count} out");
                    }
                }
            }
            return null;
        }

        // THE handoff decision, in order: a command handover (only when the lane passes its fight,
        // `commandOpposition` — Attack) when CommandHandover finds one the armies can take; else the
        // support's sparable bodies into the primary's free slots; else, the primary being full,
        // one fresh support body for the primary's most wounded / weakest body (the first fresh
        // body stronger than it that the armies can exchange). Null with `why` when nothing can go.
        // `siteOpposition` (Attack lane only) — the fight the primary is heading for: when its
        // known structure defence makes a first Disruption carrier worth a slot
        // (GroundCombatDonorPolicy.DisruptionHelps), the support's carrier is offered first and may
        // replace the weakest body even though its own combat value is lower.
        internal static HandoffPlan PlanHandoff(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefendingArmy> commandOpposition, float commandHexBonus,
            out string why, bool requireChargeNow = true,
            System.Func<IReadOnlyList<UnitData>, bool> rosterAdmissible = null,
            IReadOnlyList<WorthIt.DefendingArmy> siteOpposition = null)
        {
            why = "";
            if (primary == null || support == null)
            {
                why = "primary or support is gone";
                return null;
            }
            // CommandHandover returns only an exchange the armies can make (same legality check).
            HandoffPlan command = commandOpposition == null ? null : CommandHandover(primary, support,
                commandOpposition, commandHexBonus, null, requireChargeNow, rosterAdmissible);
            if (command != null)
                return command;

            List<UnitData> sparable = SparableSupportBodies(support);
            if (sparable.Count == 0)
            {
                why += "support has no sparable body";
                return null;
            }
            UnitData carrier = GroundCombatDonorPolicy.DisruptionHelps(primary.Members, siteOpposition)
                ? sparable.FirstOrDefault(Game.Combat.DisruptionRules.IsCarrier) : null;
            if (carrier != null)
            {
                sparable.Remove(carrier);
                sparable.Insert(0, carrier);
            }
            int freeSlots = System.Math.Max(0,
                ArmyData.ComputeCapacity(primary.Members, primary.IsGarrison) - primary.Members.Count);
            if (freeSlots > 0)
            {
                foreach (var batch in BodyAlternatives(sparable, rosterAdmissible != null, freeSlots)
                    .Where(b => b.Count > 0 && b.Count <= freeSlots))
                {
                    var candidate = new HandoffPlan(batch, null, null,
                        $"transferred {batch.Count} into free slot(s)");
                    if (rosterAdmissible != null && !rosterAdmissible(FinalRoster(primary, candidate)))
                    { why = "handoff destroys the current task route"; continue; }
                    if (ArmyActions.CanExchangeMembers(batch, support, primary, null, null, out string fWhy,
                            requireChargeNow))
                        return candidate;
                    why = $"atomic transfer rejected: {fWhy}";
                }
                return null;
            }

            // Primary is full — trade out its most critically wounded member for the best fresh
            // body the support can spare (a straight exchange needs no free slot on either side).
            // An exchange is the SupportReturn trigger: the displaced unit only exists in the
            // support now, so the whole support army walks itself home afterward.
            foreach (UnitData weakest in WeakestBodies(primary.Members))
            foreach (UnitData fresh in sparable)
            {
                if (fresh != carrier && GroundCombatDonorPolicy.UnitCombatValue(fresh)
                    <= GroundCombatDonorPolicy.UnitCombatValue(weakest))
                    continue;
                var candidate = new HandoffPlan(new[] { fresh }, new[] { weakest }, null,
                    $"swapped {weakest.Name} out for {fresh.Name}");
                if (rosterAdmissible != null && !rosterAdmissible(FinalRoster(primary, candidate)))
                { why = "handoff destroys the current task route; "; continue; }
                if (ArmyActions.CanExchangeMembers(new[] { fresh }, support, primary, null,
                        new[] { weakest }, out string sWhy, requireChargeNow))
                    return candidate;
                why += $"swap rejected: {sWhy}; ";
            }
            why += "primary is full and no support body improves on its weakest member";
            return null;
        }

        // Mission owners supply their current destination and routing profile. Slower rosters
        // remain useful whenever they can still do that task; static or already-unreachable tasks
        // impose no new movement veto. Current MP is deliberately not a next-turn capability.
        internal static System.Func<IReadOnlyList<UnitData>, bool> PreserveTaskRoute(HexMap map,
            ArmyData primary, HexCoord destination, SafeRouteProfile profile)
        {
            if (map == null || primary == null || primary.Hex.Equals(destination)
                || SafeStepPathing.FindSafePathCost(map, primary.Owner, primary.Hex, destination,
                    primary.MaxMovement, profile) == int.MaxValue)
                return null;
            return roster => SafeStepPathing.FindSafePathCost(map, primary.Owner, primary.Hex,
                destination, ArmyData.ComputeMaxMovement(roster), profile) != int.MaxValue;
        }

        internal static List<UnitData> FinalRoster(ArmyData primary, HandoffPlan plan)
        {
            var roster = primary.Members.Except(plan.Displaced).Concat(plan.Incoming).ToList();
            if (plan.Promote != null)
            {
                roster.Remove(plan.Promote);
                roster.Insert(0, plan.Promote);
            }
            return roster;
        }

        // Strongest complete batches first, then legal alternatives. No subset work is needed
        // without a task constraint. This also allows a fast escort beside an inadmissible tank.
        private static IEnumerable<List<UnitData>> BodyAlternatives(List<UnitData> bodies,
            bool alternatives, int maxCount = int.MaxValue)
        {
            int limit = System.Math.Min(bodies.Count, maxCount);
            if (!alternatives) { yield return bodies.Take(limit).ToList(); yield break; }
            IEnumerable<List<UnitData>> Choose(int start, int left, List<UnitData> selected)
            {
                if (left == 0) { yield return new List<UnitData>(selected); yield break; }
                for (int i = start; i <= bodies.Count - left; i++)
                {
                    selected.Add(bodies[i]);
                    foreach (var batch in Choose(i + 1, left - 1, selected)) yield return batch;
                    selected.RemoveAt(selected.Count - 1);
                }
            }
            for (int count = limit; count >= 0; count--)
                foreach (var batch in Choose(0, count, new List<UnitData>())) yield return batch;
        }

        // The AP the handoff itself charges: every newcomer to an army that already acted this
        // turn pays its activation (ArmyActions.TransferMembersApCost), in both directions.
        // There is no cost of a handoff that has no plan — callers reject a null plan first.
        internal static int HandoffApCost(HandoffPlan plan, ArmyData primary, ArmyData support) =>
            ProjectedHandoffApCost(plan.Incoming, plan.Displaced, primary, support, supportWalks: false);

        // THE handoff charge, for the handoff happening now or on a support's arrival turn.
        // Now (`supportWalks` false — the armies already share the hex): the live rule
        // (ArmyActions.TransferMembersApCost). On arrival after a walk: the support activated to
        // walk there, so every body it receives pays its activation; the primary holds that turn
        // (a gather host, a reinforced primary waiting at the rendezvous), so its newcomers ride
        // its own first activation for free. PlanGather prices its supports with this, so a gather
        // plan's TotalAp carries the same charges the rendezvous legs will provision.
        internal static int ProjectedHandoffApCost(IEnumerable<UnitData> incoming,
            IEnumerable<UnitData> displaced, ArmyData primary, ArmyData support, bool supportWalks)
        {
            if (!supportWalks)
                return ArmyActions.TransferMembersApCost(incoming, primary)
                    + ArmyActions.TransferMembersApCost(displaced, support);
            return (displaced ?? Enumerable.Empty<UnitData>())
                .Where(u => u != null).Distinct().Sum(u => u.ActivationApCost);
        }

        // The primary's ground bodies, most wounded first, then weakest (the one "who gives way"
        // order of every exchange). An army support carrier (Disruption, Heal, Drill, Transport…)
        // never gives way: its value is not in its own power, which is all an exchange compares —
        // a wounded Disruption carrier swapped for a stronger body handed the target its whole
        // structure defence back (StrategicEffectRegistry.IsArmySupportCarrier's own contract).
        private static IEnumerable<UnitData> WeakestBodies(IEnumerable<UnitData> members) =>
            (members ?? Enumerable.Empty<UnitData>())
                .Where(u => AiArmyRoles.IsGroundBattleBody(u)
                    && !StrategicEffectRegistry.IsArmySupportCarrier(u))
                .OrderBy(u => u.HitPointsMax > 0 ? (float)u.HitPointsCurrent / u.HitPointsMax : 1f)
                .ThenBy(u => GroundCombatDonorPolicy.UnitCombatValue(u))
                .ThenBy(u => u.Name);

        private static bool SupportStaysLegal(ArmyData support, List<UnitData> incoming,
            List<UnitData> displaced)
        {
            var remainder = support.Members.Where(u => !incoming.Contains(u)).Concat(displaced).ToList();
            return ArmyData.RosterFits(remainder, support.IsGarrison);
        }

        // Every ground body the support can hand over, strongest first. A support may be emptied
        // (the game disbands an empty army only at the end of its owner's turn); a hero only moves
        // through CommandHandover.
        internal static List<UnitData> SparableSupportBodies(ArmyData support)
        {
            var list = new List<UnitData>();
            if (support == null)
                return list;
            foreach (UnitData u in support.Members
                .Where(x => AiArmyRoles.IsGroundBattleBody(x))
                .OrderByDescending(GroundCombatDonorPolicy.UnitCombatValue)
                .ThenBy(x => x.Name))
            {
                if (!support.CanLeaveWithoutOvercrowding(u))
                    continue;
                list.Add(u);
            }
            return list;
        }
    }
}
