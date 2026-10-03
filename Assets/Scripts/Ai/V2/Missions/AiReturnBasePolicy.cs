using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Aviation;

namespace Game.Ai.V2
{
    internal static class AiReturnBasePolicy
    {
        // §11 — is the fixed return base still ours AND still structurally
        // reachable? "Structurally" is the key word: this reads the GENUINE, any-number-of-turns
        // route-existence fact WorldAnalysis froze onto the mover's ArmySnapshot
        // (SafeStepPathing.FindSafePath — the same oracle Provisioning uses live), never "reachable
        // THIS turn" — a merely-temporarily-blocked step must NOT trigger a retarget, only a base
        // with NO safe route at all. Shared by the primary's Return leg and the support's
        // SupportReturn leg — `moverArmyId` is whichever of the two is walking home.
        internal static bool ReturnBaseStillValid(WorldSnapshot snap, PlayerSetupData player,
            int? moverArmyId, HexCoord? hex)
        {
            if (!hex.HasValue || snap?.Self?.BaseHexes == null)
                return false;
            // ATK §20/§48 — "is this hex MY base right now" is answered by Self.BaseHexes, the
            // current-truth topology WorldAnalysis derives from live owned buildings, never by a
            // remembered KnownBuilding.Owner. The old read could keep walking an army home to a
            // base this player had already lost (memory still says we own it) and could refuse a
            // freshly built or freshly captured base until it happened to be re-observed.
            if (!snap.Self.BaseHexes.Contains(hex.Value))
                return false;

            ArmySnapshot mover = moverArmyId.HasValue
                ? snap.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == moverArmyId.Value)
                : null;
            if (mover != null && mover.IsStructuralRaidActor
                && mover.ReachableOwnBaseHexes.Count > 0
                && !mover.ReachableOwnBaseHexes.Contains(hex.Value))
                return false;
            return true;
        }

        // ---------------------------------------------------------------------------------------
        //  §11 — "the most active base", as a PURE lexicographic rule over existing snapshot data
        //  (KnownBuilding / Self.Armies / ThreatModel). Deliberately a small function, not a new
        //  manager. Order:
        //    1. max total collected amounts at that base this turn
        //    2. has working Research/Production/Barracks infrastructure
        //    3. max own field+garrison power standing there
        //    4. min current threat severity
        //    5. min ETA for the returning army
        //    6. starting Citadel first, then coordinates (stable tie-break only)
        // ---------------------------------------------------------------------------------------
        // The ONE walk-home destination rule for every lifecycle leg (Raid Return / SupportReturn,
        // Attack SupportReturn / RecoveryReturn / GatherReturn, ActiveDefence Return): keep the
        // fixed base while it is still ours and structurally reachable, otherwise re-pick through
        // SelectReturnBase. `reselected` tells the caller the leg moved (reset its stall clock);
        // null means no own base is left at all.
        internal static HexCoord? KeepOrReselectHome(WorldSnapshot snap, PlayerSetupData player,
            int? moverArmyId, HexCoord? current, out bool reselected)
        {
            reselected = false;
            if (current.HasValue && ReturnBaseStillValid(snap, player, moverArmyId, current))
                return current;
            reselected = true;
            return SelectReturnBase(snap, player, moverArmyId);
        }

        // The own base nearest to the closest known hostile base/Citadel — where an Attack's fist
        // would assemble. Null until an enemy structure is known and the strongest army already
        // carries stagingReturnMinFistShare of the strike pool (before that, "home" stays the
        // economically most active base). A pure snapshot rule.
        internal static HexCoord? StagingBase(WorldSnapshot snap, PlayerSetupData player)
        {
            if (snap?.Self?.BaseHexes == null || snap.Known?.Buildings == null || player == null
                || snap.Self.AttackPeak <= 0f
                || snap.Self.FistPower < AiConfigV2.stagingReturnMinFistShare * snap.Self.AttackPeak)
                return null;
            List<HexCoord> targets = snap.Known.Buildings
                .Where(b => AttackObjectiveEvaluator.IsHostileStrategicStructure(b, player))
                .Select(b => b.Hex).ToList();
            if (targets.Count == 0)
                return null;
            return snap.Self.BaseHexes
                .OrderBy(h => targets.Min(t => HexGridMath.Distance(h, t)))
                .ThenByDescending(h => h.Equals(snap.Self.Citadel) ? 1 : 0)
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .Select(h => (HexCoord?)h).FirstOrDefault();
        }

        // `preferStaging`: Raid return legs only — a returning raider walks to the staging base
        // (see StagingBase) instead of the most active one, so the fist assembles where it will
        // march from. Defence/Attack legs keep their own home rule.
        internal static HexCoord? SelectReturnBase(WorldSnapshot snap, PlayerSetupData player, int? moverArmyId,
            bool preferStaging = false)
        {
            if (snap?.Self?.BaseHexes == null || player == null)
                return null;
            // ATK §20/§48 — identity comes from Self.BaseHexes (current truth); Known.Buildings
            // stays the METADATA source the ranking below reads (stored resources, facilities).
            // Splitting the two is what lets a just-built or just-captured base be chosen on the
            // very turn it becomes ours, without waiting for a fresh structural observation.
            List<HexCoord> bases = snap.Self.BaseHexes.ToList();
            if (bases.Count == 0)
                return null;

            ArmySnapshot mover = moverArmyId.HasValue
                ? snap.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == moverArmyId.Value)
                : null;
            int moveBudget = System.Math.Max(1, mover?.MaxMovement ?? AiConfigV2.etaFallbackMoveBudget);

            // A structurally reachable base always outranks an unreachable one, ahead of every
            // other tie-break. Falls back to distance-only ordering among
            // bases with the SAME reachability, and — if genuinely none are reachable right now —
            // still returns the best-by-distance candidate rather than stranding the operation on a
            // signal that may only be a transient blockade.
            bool Reachable(HexCoord h) =>
                mover == null || !mover.IsStructuralRaidActor
                || mover.ReachableOwnBaseHexes.Contains(h);

            HexCoord citadel = snap.Self.Citadel;
            HexCoord? staging = preferStaging ? StagingBase(snap, player) : null;
            return bases
                .OrderByDescending(h => Reachable(h) ? 1 : 0)
                .ThenByDescending(h => staging.HasValue && h.Equals(staging.Value) ? 1 : 0)
                .ThenByDescending(h => BaseCollectedAmount(snap, h))
                .ThenByDescending(h => BaseHasDevelopmentInfrastructure(snap, h) ? 1 : 0)
                .ThenByDescending(h => BaseOwnPowerAt(snap, h))
                .ThenBy(h => BaseThreatSeverityAt(snap, h))
                .ThenBy(h => mover == null ? 0
                    : AiV2Util.CeilDiv(HexGridMath.Distance(mover.Hex, h), moveBudget))
                .ThenByDescending(h => h.Equals(citadel) ? 1 : 0)
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .Select(h => (HexCoord?)h)
                .FirstOrDefault();
        }

        private static float BaseCollectedAmount(WorldSnapshot snap, HexCoord hex)
        {
            float total = 0f;
            if (snap.Known?.Buildings == null)
                return total;
            foreach (Game.Ai.AiMapMemory.KnownBuilding b in snap.Known.Buildings)
            {
                if (!b.Hex.Equals(hex) || b.CollectedAmounts == null) continue;
                foreach (int amount in b.CollectedAmounts)
                    total += System.Math.Max(0, amount);
            }
            return total;
        }

        // Metadata read only — the caller has ALREADY established that `hex` is one of our own
        // bases (Self.BaseHexes). Matching on the hex alone is deliberate: a remembered
        // KnownBuilding.Owner can lag reality on a base we just built or captured, and gating this
        // on it would silently drop real facilities out of the ranking.
        private static bool BaseHasDevelopmentInfrastructure(WorldSnapshot snap, HexCoord hex) =>
            snap.Known?.Buildings != null && snap.Known.Buildings.Any(b => b.Hex.Equals(hex)
                && (b.HasFacilityWithAbility(UnitAbilities.Research)
                    || b.HasFacilityWithAbility(UnitAbilities.Production)
                    || b.HasFacilityWithAbility(UnitAbilities.Barracks)));

        private static float BaseOwnPowerAt(WorldSnapshot snap, HexCoord hex)
        {
            float power = 0f;
            foreach (ArmySnapshot a in snap.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                if (a != null && !a.IsPrison && !a.IsAir && a.Hex.Equals(hex))
                    power += a.EffectiveArmyPower;
            return power;
        }

        private static float BaseThreatSeverityAt(WorldSnapshot snap, HexCoord hex)
        {
            float worst = 0f;
            foreach (AssetThreatSnapshot t in snap.Threat?.Threats ?? System.Array.Empty<AssetThreatSnapshot>())
                if (t?.Asset != null && t.Asset.Hex.Equals(hex) && t.Severity > worst)
                    worst = t.Severity;
            return worst;
        }
    }
}
