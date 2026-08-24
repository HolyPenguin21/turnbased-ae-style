using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Оборона · SecureBase (2026-08-24, project owner's own spec) — initial-defence for a fresh,
    // captured, or later-weakened second base. Trigger, composition/donor selection, and the whole
    // phase lifecycle live here (same split every other task class in this codebase follows — see
    // RaidWeakerArmyTask/BuildBaseTask's own class comments); AiDefencePlanner only scans for which
    // bases need this, registers/removes the AiTask, and forwards whatever AiDecision this class
    // builds to the Arbiter — see AiDefencePlanner.TryStartSecureBaseCandidates/
    // TryContinueSecureBaseTask.
    //
    // Триггер — see NeedsSecuring: any of this player's own NON-CITADEL Base-tagged garrison hexes
    // (the citadel already has its own DefendCitadel Patrol/Active/Turtle coverage and starts with
    // a full garrison — see CitadelSetupController) whose garrison doesn't clear
    // AiArmyRoles.IsBaseGarrisonSecure. Works identically whether the base got there by being
    // freshly built (AiAggressionPlanner.BuildBaseRoutine), captured (BuildingRegistry.
    // CaptureOrDestroy/EnsureGarrisonForBuilding), or simply lost defenders since — this task never
    // asks HOW the base ended up short, only whether it currently is.
    //
    // Состав/источник — garrison-to-garrison only (2026-08-24, project owner's own scope call: "не
    // забирать армии и юнитов, уже занятых Raid, Recon, Economy или Defence"). FindReinforcementSource
    // only ever pulls a spare non-hero from another one of this player's own IsGarrison stockpiles
    // (the citadel included), never from a field army already carrying a task — a garrison ArmyData
    // is never itself claimed as any task's own Army, so restricting the donor pool to garrisons
    // alone satisfies that exclusion without needing a separate task-ownership check. Every donor
    // pull still goes through AiArmyRoles.CanSpareGarrisonMember, so this can never strip the
    // citadel/another base back below its own secure floor.
    //
    // Жизненный цикл — one courier at a time, phase read straight off AiTask state rather than a
    // separate enum (project owner's own "не добавлять отдельные поля под каждую промежуточную
    // деталь" call):
    //   task.Army == null           → AcquireReinforcement (BuildDecision picks a fresh donor/unit
    //                                  and returns a DispatchBaseReinforcement decision)
    //   task.Army.Hex != HomeHex    → TravelToBase (an ordinary MoveArmy decision)
    //   task.Army.Hex == HomeHex    → DepositIntoGarrison (a DepositReinforcement decision)
    // AiOperations.DepositReinforcementRoutine clears task.Army back to null once a delivery lands
    // (and the courier shell is gone), so the very next continuation call naturally re-enters
    // AcquireReinforcement — Recheck/Complete happen OUTSIDE this per-step branching entirely, in
    // IsComplete, checked by AiDefencePlanner before BuildDecision ever runs.
    public static class SecureBaseTask
    {
        public static bool IsSecure(PlayerSetupData player, HexCoord hex) => AiArmyRoles.IsBaseGarrisonSecure(player, hex);

        public static int RequiredDefenders(PlayerSetupData player, HexCoord hex) => AiConfig.secureBaseMinNonHeroUnits;

        // Every one of this player's own non-citadel garrison hexes whose garrison isn't secure
        // yet — see this class's own "Триггер" comment.
        public static IEnumerable<HexCoord> NeedsSecuring(PlayerSetupData player)
        {
            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            foreach (HexCoord hex in AiTurnController.OwnGarrisonHexes(player))
            {
                if (hex.Equals(citadelHex))
                    continue;
                if (!IsSecure(player, hex))
                    yield return hex;
            }
        }

        // The base itself is gone from under this task — captured back, or its Barracks building
        // destroyed outright — nothing left here to secure. Garrison OWNERSHIP specifically (not
        // just presence) is what matters — see AiTurnController.OwnGarrisonHexes, which this same
        // read backs.
        public static bool ShouldCancel(PlayerSetupData player, AiTask task) =>
            !ArmyRegistry.AllForOwner(player).Any(a => a.IsGarrison && a.Hex.Equals(task.HomeHex));

        public static bool IsComplete(PlayerSetupData player, AiTask task, out string reason)
        {
            if (!IsSecure(player, task.HomeHex))
            {
                reason = null;
                return false;
            }
            int nonHero = ArmyRegistry.AllForOwner(player)
                .Where(a => a.IsGarrison && a.Hex.Equals(task.HomeHex))
                .SelectMany(a => a.Members).Count(m => !m.IsHero);
            reason = $"garrison at ({task.HomeHex.Q},{task.HomeHex.R}) now has {nonHero} non-hero defender(s) — secure.";
            return true;
        }

        // Nearest OTHER own garrison (citadel included) that can spare a non-hero without dropping
        // below its own secure floor (see AiArmyRoles.CanSpareGarrisonMember) — never a Recce unit
        // (solo-only by design elsewhere in this codebase), lowest strategic value first so this
        // never bleeds a donor's own best defenders, same ordering FindGarrisonSeedUnit already
        // uses for the equivalent BuildBase pick.
        public static UnitData FindReinforcementSource(PlayerSetupData player, HexCoord targetHex, out ArmyData source)
        {
            source = null;
            UnitData best = null;
            int bestDistance = int.MaxValue;
            foreach (ArmyData donor in ArmyRegistry.AllForOwner(player).Where(a => a.IsGarrison && !a.Hex.Equals(targetHex)))
            {
                UnitData candidate = donor.Members
                    .Where(m => !m.IsHero && !m.HasAbility(UnitAbilities.Recce) && CanSpareSource(player, donor, m))
                    .OrderBy(m => m.Defense + m.Attack).ThenByDescending(m => m.Range > 1 ? 1 : 0)
                    .FirstOrDefault();
                if (candidate == null)
                    continue;

                int distance = HexGridMath.Distance(donor.Hex, targetHex);
                if (source == null || distance < bestDistance)
                {
                    source = donor;
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static bool CanSpareSource(PlayerSetupData player, ArmyData source, UnitData unit) =>
            AiArmyRoles.CanSpareGarrisonMember(player, source, unit);

        // The one decision this task ever proposes for an already-registered `task` — which of the
        // three lifecycle phases applies right now (see this class's own "Жизненный цикл" comment),
        // read fresh off task.Army every call, never cached. Null whenever nothing can be done this
        // step (no donor available yet, AP-short, army lost) — AiDefencePlanner's own caller treats
        // that as "try again next step", same as every other continuation in this codebase.
        public static AiDecision BuildDecision(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army == null)
            {
                UnitData recruit = FindReinforcementSource(player, task.HomeHex, out ArmyData source);
                if (recruit == null || source == null || root == null || !root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                    return null;
                return AiDecision.DispatchBaseReinforcement(source, recruit, task, AiConfig.secureBaseTravelScore);
            }

            if (task.Army.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (!task.Army.Hex.Equals(task.HomeHex))
            {
                if (!AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, task.HomeHex))
                    return null;
                var moveTarget = new AiScoutPlanner.ScoutTarget(task.HomeHex, 0f,
                    $"reinforcement heads to secure the base at ({task.HomeHex.Q},{task.HomeHex.R})");
                return AiDecision.Move(task.Army, moveTarget, task, AiConfig.secureBaseTravelScore, AiTaskCategory.Defence);
            }

            ArmyData garrison = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison && a.Hex.Equals(task.HomeHex));
            UnitData cargo = task.Army.Members.FirstOrDefault(m => !m.IsHero) ?? task.Army.Members.FirstOrDefault();
            if (garrison == null || cargo == null || !GarrisonReorgTask.CanAffordTransferInto(garrison, cargo))
                return null;

            var move = new GarrisonReorgTask.ConsolidationMove(task.Army, cargo, garrison,
                $"\"{task.Army.Name}\" delivers {cargo.Name} to secure the garrison at ({task.HomeHex.Q},{task.HomeHex.R})");
            return AiDecision.DepositReinforcement(move, task, AiConfig.secureBaseDeliverScore);
        }
    }
}
