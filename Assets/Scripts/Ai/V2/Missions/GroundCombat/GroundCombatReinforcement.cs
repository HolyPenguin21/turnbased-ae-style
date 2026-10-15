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
            out string why, bool allowCommandHandover = false, bool allowCompleteTransfer = false)
        {
            if (allowCommandHandover && CommandHandover(primary, support, opposition,
                    defenderHexDefenseBonus, null) != null)
            {
                why = null;
                return true;
            }
            List<UnitData> sparable = SparableSupportBodies(support, allowCompleteTransfer);
            List<WorthIt.DefenderProfile> primaryBodies = primary.Members
                .Where(u => AiArmyRoles.IsGroundBattleBody(u))
                .Select(WorthIt.FromLiveUnit)
                .ToList();
            List<WorthIt.DefenderProfile> supportBodies = sparable
                .Select(WorthIt.FromLiveUnit)
                .ToList();
            int capacity = ArmyData.ComputeCapacity(primary.Members, primary.IsGarrison);
            return GroundCombatAssemblyPlanner.TryProjectReinforcement(
                primaryBodies, supportBodies, capacity, primary.Members.Count,
                // Body-only profiles: the roster form also carries secondary healing heroes.
                WorthIt.SideCommander.Of(primary.Members), opposition, out _, out why,
                defenderHexDefenseBonus);
        }

        // The Attack lane needs power delivered into the actual primary, not a new shell
        // elsewhere on the map or a Monte-Carlo win change that can be zero at saturation.
        internal static bool ImprovesAttackForce(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, out string why,
            bool requireChargeNow = false, bool capacityIsProgress = false) =>
            PlanAttackHandoff(primary, support, opposition, hexBonus, requireChargeNow, out why,
                capacityIsProgress) != null;

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
        // its larger Command is the progress, as for the same-hex capacity hero of a preparation;
        // the lone-hero container may be emptied by it.
        internal static HandoffPlan PlanAttackHandoff(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, bool requireChargeNow,
            out string why, bool capacityIsProgress = false)
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
                out string firstWhy, allowCompleteTransfer: true, requireChargeNow: requireChargeNow,
                allowEmptySupport: capacityIsProgress);
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
                allowCompleteTransfer: true, requireChargeNow: requireChargeNow);
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
        // damage before? The one rule of the Attack handoff and of the gather projection's
        // command handover.
        internal static bool RaisesPrimary(IReadOnlyList<UnitData> before, IReadOnlyList<UnitData> after,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus) =>
            AiPower.EffectiveArmyPower(after) > AiPower.EffectiveArmyPower(before)
            || !WorthIt.CanDamageAll(before.Select(WorthIt.FromLiveUnit).ToList(), opposition, hexBonus)
                && WorthIt.CanDamageAll(after.Select(WorthIt.FromLiveUnit).ToList(), opposition, hexBonus);

        // Strike force — THE "hand the support's hero over to lead the primary" rule, exchanges
        // included. A field hero of the support (never a SupportOperator) moves when, joined to the
        // primary, it is the best commander of the primary's fight (HeroRoleEvaluator.
        // BestCommanderFor, judged with `prospectiveBodies` — the bodies that would come along —
        // or the support's own bodies). When the primary has no room for the hero itself, the hero
        // is exchanged for the primary's most wounded, then weakest, body. Under the hero's
        // Command the free slots take the support's strongest bodies; every further support body
        // that beats the primary's weakest remaining body is exchanged for it (the same "fresh for
        // weakest" rule as the bodies-only swap). The support must stay a non-empty, legally sized
        // container with what it keeps and what it receives. Null when no hero should move. Used
        // by the gather projection, the gather plan's donor retention, the reinforcement gate and
        // the handoff — one answer for all four.
        internal static HandoffPlan CommandHandover(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float defenderHexDefenseBonus,
            IEnumerable<WorthIt.DefenderProfile> prospectiveBodies, bool allowEmptySupport = false,
            bool requireChargeNow = false)
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

                    var incoming = new List<UnitData> { hero };
                    var displaced = new List<UnitData>(returning);
                    if (heroFor != null)
                        displaced.Add(heroFor);
                    var led = new List<UnitData> { hero };
                    led.AddRange(retained.Where(u => u != heroFor));
                    int room = System.Math.Max(0, ArmyData.ComputeCapacity(led, false) - led.Count);
                    var fresh = new Queue<UnitData>(donorBodies);
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

                    // The support is never emptied: if everything would leave, its weakest moving body
                    // stays (the last fill, or else the last exchange, which is undone as a pair).
                    int fills = incoming.Count - 1
                        - displaced.Count(AiArmyRoles.IsGroundBattleBody) + (heroFor != null ? 1 : 0);
                    if (!support.Members.Except(incoming).Concat(displaced).Any() && incoming.Count > 1)
                    {
                        if (fills > 0)
                            incoming.RemoveAt(fills);
                        else
                        {
                            incoming.RemoveAt(incoming.Count - 1);
                            displaced.RemoveAt(displaced.Count - 1);
                        }
                    }
                    // Without its hero the support must still hold what it keeps and receives; a
                    // support that cannot does not give this hero away.
                    if (!SupportStaysLegal(support, incoming, displaced)
                        && !(allowEmptySupport && support.Members.All(u => incoming.Contains(u))
                            && displaced.Count == 0))
                        continue;
                    var final = new[] { hero }.Concat(primary.Members.Except(displaced))
                        .Concat(incoming.Where(u => u != hero)).ToList();
                    var withOldHeroes = final.Concat(returning).ToList();
                    if (ArmyData.ComputeCurrentMovement(final) < ArmyData.ComputeCurrentMovement(withOldHeroes)
                        || ArmyData.ComputeMaxMovement(final) < ArmyData.ComputeMaxMovement(withOldHeroes)
                        || !ArmyActions.CanExchangeMembers(incoming, support, primary, hero,
                            displaced, out _, requireChargeNow))
                        continue;
                    return new HandoffPlan(incoming, displaced, hero, $"hero {hero.Name} took command"
                        + (heroFor != null ? $" in exchange for {heroFor.Name}" : "")
                        + $"; {incoming.Count - 1} body(ies) in, {displaced.Count} out");
                }
            }
            return null;
        }

        // THE handoff decision, in order: a command handover (only when the lane passes its fight,
        // `commandOpposition` — Attack) when CommandHandover finds one the armies can take; else the
        // support's sparable bodies into the primary's free slots; else, the primary being full,
        // one fresh support body for the primary's most wounded / weakest body (the first fresh
        // body stronger than it that the armies can exchange). Null with `why` when nothing can go.
        internal static HandoffPlan PlanHandoff(ArmyData primary, ArmyData support,
            IReadOnlyList<WorthIt.DefendingArmy> commandOpposition, float commandHexBonus,
            out string why, bool allowCompleteTransfer = false, bool requireChargeNow = true,
            bool allowEmptySupport = false)
        {
            why = "";
            if (primary == null || support == null)
            {
                why = "primary or support is gone";
                return null;
            }
            // CommandHandover returns only an exchange the armies can make (same legality check).
            HandoffPlan command = commandOpposition == null ? null : CommandHandover(primary, support,
                commandOpposition, commandHexBonus, null, allowEmptySupport, requireChargeNow);
            if (command != null)
                return command;

            List<UnitData> sparable = SparableSupportBodies(support, allowCompleteTransfer);
            if (sparable.Count == 0)
            {
                why += "support has no sparable body";
                return null;
            }
            int freeSlots = System.Math.Max(0,
                ArmyData.ComputeCapacity(primary.Members, primary.IsGarrison) - primary.Members.Count);
            if (freeSlots > 0)
            {
                List<UnitData> batch = sparable.Take(freeSlots).ToList();
                if (ArmyActions.CanExchangeMembers(batch, support, primary, null, null, out string fWhy,
                        requireChargeNow))
                    return new HandoffPlan(batch, null, null, $"transferred {batch.Count} into free slot(s)");
                why += $"atomic transfer rejected: {fWhy}";
                return null;
            }

            // Primary is full — trade out its most critically wounded member for the best fresh
            // body the support can spare (a straight exchange needs no free slot on either side).
            // An exchange is the SupportReturn trigger: the displaced unit only exists in the
            // support now, so the whole support army walks itself home afterward.
            UnitData weakest = WeakestBodies(primary.Members).FirstOrDefault();
            if (weakest == null)
            {
                why += "primary is full and has no swappable non-hero body";
                return null;
            }
            foreach (UnitData fresh in sparable)
            {
                if (GroundCombatDonorPolicy.UnitCombatValue(fresh)
                    <= GroundCombatDonorPolicy.UnitCombatValue(weakest))
                    continue;
                if (ArmyActions.CanExchangeMembers(new[] { fresh }, support, primary, null,
                        new[] { weakest }, out string sWhy, requireChargeNow))
                    return new HandoffPlan(new[] { fresh }, new[] { weakest }, null,
                        $"swapped {weakest.Name} out for {fresh.Name}");
                why += $"swap rejected: {sWhy}; ";
            }
            why += "primary is full and no support body improves on its weakest member";
            return null;
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
        // order of every exchange).
        private static IEnumerable<UnitData> WeakestBodies(IEnumerable<UnitData> members) =>
            (members ?? Enumerable.Empty<UnitData>())
                .Where(u => AiArmyRoles.IsGroundBattleBody(u))
                .OrderBy(u => u.HitPointsMax > 0 ? (float)u.HitPointsCurrent / u.HitPointsMax : 1f)
                .ThenBy(u => GroundCombatDonorPolicy.UnitCombatValue(u))
                .ThenBy(u => u.Name);

        private static bool SupportStaysLegal(ArmyData support, List<UnitData> incoming,
            List<UnitData> displaced)
        {
            var remainder = support.Members.Where(u => !incoming.Contains(u)).Concat(displaced).ToList();
            return remainder.Count >= 1
                && ArmyData.RosterFits(remainder, support.IsGarrison);
        }

        // Attack may consume a singleton field support. All other transfers retain one member;
        // a hero only moves through CommandHandover.
        internal static List<UnitData> SparableSupportBodies(ArmyData support, bool allowCompleteTransfer = false)
        {
            var list = new List<UnitData>();
            if (support == null)
                return list;
            foreach (UnitData u in support.Members
                .Where(x => AiArmyRoles.IsGroundBattleBody(x))
                .OrderByDescending(GroundCombatDonorPolicy.UnitCombatValue)
                .ThenBy(x => x.Name))
            {
                if (!(allowCompleteTransfer && support.Members.Count == 1)
                    && support.Members.Count - list.Count <= 1)
                    break;  // Only Attack's singleton body may drain its donor; other donors retain one member
                if (!support.CanLeaveWithoutOvercrowding(u))
                    continue;
                list.Add(u);
            }
            return list;
        }
    }
}
