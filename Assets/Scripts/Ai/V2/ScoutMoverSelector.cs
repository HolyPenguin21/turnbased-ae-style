using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT MOVER SELECTOR  (Strategy V2 build-order step 6 — the single mover-selection algorithm)
    // ===========================================================================================
    //  "ONE ESTIMATOR, MANY STAGES" applied to WHO executes a Scout mission, not just what it
    //  costs. Two callers, one ranking:
    //    · ScoutCostModel.Estimate (step 4 estimate stage) takes candidate[0] to size the AP /
    //      ETA / distance envelope.
    //    · ProvisioningManager.PreparePass (step 6 provision stage) takes the whole ranked list
    //      for every funded mission and runs a capability-preserving assignment across them, then
    //      Provision() consumes the assigned mover.
    //  Neither re-derives the eligibility rule or the sort — a divergence here is the "allocator
    //  approves, provisioning can't deliver" thrash V2 is built to prevent.
    //
    //  ELIGIBILITY (own armies only — WorldSnapshot.Self.Armies)
    //    A fielded solo Recce (AiArmyRoles.IsSoloRecce), not a prison, not air, with members, that
    //    can still act THIS turn (CurrentMovement > 0 — a spent scout is not fundable work for the
    //    current allocation cycle, whatever its ETA), and is not in `excludeArmyIds` (movers
    //    locked by an earlier provisioning pass this turn). For a stealth-Required mission it must
    //    also be already hidden OR still able to slip into stealth before its first move
    //    (CanEnterStealth && !HasActivatedThisTurn) — a visible, already-activated scout is not a
    //    valid executor at all (parity with V1's hard exclusion).
    //
    //  RANK (deterministic — same order ScoutCostModel used inline before step 6 split it out)
    //    effective activation AP  ->  ETA turns  ->  hex distance to focus  ->  ArmyId
    // ===========================================================================================
    public readonly struct ScoutMoverCandidate
    {
        public readonly ArmySnapshot Army;
        public readonly int EffActivationAp;   // 0 if already activated this turn, else ActivationApCost
        public readonly int EtaTurns;          // 1 if reachable this turn, else 1 + ceil(remaining / move budget)
        public readonly int Distance;          // plain hex distance army.Hex -> target.FocusHex
        public readonly bool AlreadyHidden;

        public ScoutMoverCandidate(ArmySnapshot army, int effActivationAp, int etaTurns, int distance, bool alreadyHidden)
        {
            Army = army;
            EffActivationAp = effActivationAp;
            EtaTurns = etaTurns;
            Distance = distance;
            AlreadyHidden = alreadyHidden;
        }
    }

    public static class ScoutMoverSelector
    {
        public static List<ScoutMoverCandidate> Rank(WorldSnapshot snap, ScoutMissionTarget target,
            ISet<int> excludeArmyIds)
        {
            var result = new List<ScoutMoverCandidate>();
            if (snap?.Self?.Armies == null)
                return result;

            bool needStealth = target.Stealth == StealthRequirement.Required;

            int fleetBudget = snap.Self.Armies.Select(a => a.MaxMovement).DefaultIfEmpty(0).Max();
            if (fleetBudget <= 0)
                fleetBudget = 1;

            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                    continue;
                if (a.CurrentMovement <= 0)
                    continue;
                if (excludeArmyIds != null && excludeArmyIds.Contains(a.ArmyId))
                    continue;
                if (needStealth && !(a.IsHidden || (a.CanEnterStealth && !a.HasActivatedThisTurn)))
                    continue;

                int dist = HexGridMath.Distance(a.Hex, target.FocusHex);
                int effAp = a.HasActivatedThisTurn ? 0 : a.ActivationApCost;
                int budget = a.MaxMovement > 0 ? a.MaxMovement : fleetBudget;
                int eta = a.CurrentMovement >= dist ? 1 : 1 + CeilDiv(dist - a.CurrentMovement, budget);

                result.Add(new ScoutMoverCandidate(a, effAp, eta, dist, a.IsHidden));
            }

            result.Sort((x, y) =>
            {
                int c = x.EffActivationAp.CompareTo(y.EffActivationAp); if (c != 0) return c;
                c = x.EtaTurns.CompareTo(y.EtaTurns); if (c != 0) return c;
                c = x.Distance.CompareTo(y.Distance); if (c != 0) return c;
                return x.Army.ArmyId.CompareTo(y.Army.ArmyId);
            });
            return result;
        }

        // STRUCTURAL capability probe — does the player own ANY solo Recce that could, in
        // principle, serve a mission of this stealth requirement? Deliberately ignores the
        // turn-transient filters Rank applies (CurrentMovement > 0, and for a Required mission the
        // "visible + already activated" exclusion): those change next turn, so their absence is
        // "spent / contended THIS turn" (MoverContended, no cooldown), NOT "no such executor
        // exists" (NoMoverExists, cooldown). Stealth capability IS structural: a Required mission
        // needs a Recce that is hidden or carries a stealth ability at all.
        public static bool HasStructuralCandidate(WorldSnapshot snap, ScoutMissionTarget target)
        {
            if (snap?.Self?.Armies == null)
                return false;
            bool needStealth = target.Stealth == StealthRequirement.Required;
            foreach (ArmySnapshot a in snap.Self.Armies)
            {
                if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                    continue;
                if (needStealth && !(a.IsHidden || a.StealthLevel > 0))
                    continue;
                return true;
            }
            return false;
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
