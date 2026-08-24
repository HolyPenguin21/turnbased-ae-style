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
    // (the citadel included as a candidate donor, but never below ITS OWN secure floor — see
    // below), never from a field army already carrying a task — a garrison ArmyData is never
    // itself claimed as any task's own Army, so restricting the donor pool to garrisons alone
    // satisfies that exclusion without needing a separate task-ownership check. Every donor pull
    // still goes through AiArmyRoles.CanSpareGarrisonMember with allowCitadelEmergency:false (2026-
    // 08-24 P0 fix — see FindReinforcementSource's own comment), so this can never strip the
    // citadel/another base back below its own secure floor, unlike the emergency-defence donor
    // pulls elsewhere in this codebase that deliberately CAN.
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
        // read backs. Callers must check task.Army first (see RedirectToNearestOwnBase's own
        // comment) — this alone says nothing about whether a courier is still out there needing
        // somewhere to go.
        public static bool ShouldCancel(PlayerSetupData player, AiTask task) =>
            !ArmyRegistry.AllForOwner(player).Any(a => a.IsGarrison && a.Hex.Equals(task.HomeHex));

        // 2026-08-24 P0 fix (project owner's own report): used to complete the instant
        // IsBaseGarrisonSecure went true, with NO regard for whether a courier (task.Army) was
        // still out in the field carrying a live unit — a card landing in the garrison, or a
        // SECOND SecureBase-adjacent event, could secure the base one step before an already-
        // dispatched courier finished delivering, and the old version would remove the task right
        // out from under it, leaving that courier a permanently untasked field army nobody would
        // ever route home again. Never completes while task.Army != null any more — the in-flight
        // courier is always let through to actually deliver first (a base ending up with one MORE
        // defender than strictly required is harmless, never an orphan), and this naturally
        // re-checks true right after AiOperations.DepositReinforcementRoutine clears task.Army back
        // to null.
        public static bool IsComplete(PlayerSetupData player, AiTask task, out string reason)
        {
            reason = null;
            if (task.Army != null || !IsSecure(player, task.HomeHex))
                return false;
            int nonHero = ArmyRegistry.AllForOwner(player)
                .Where(a => a.IsGarrison && a.Hex.Equals(task.HomeHex))
                .SelectMany(a => a.Members).Count(m => !m.IsHero);
            reason = $"garrison at ({task.HomeHex.Q},{task.HomeHex.R}) now has {nonHero} non-hero defender(s) — secure.";
            return true;
        }

        // 2026-08-24 P0 fix (project owner's own report) — ShouldCancel's own OTHER half: the base
        // is gone but task.Army (a courier, possibly already carrying a live unit) is still out
        // there. Re-points HomeHex/TargetHex at the nearest STILL-OWNED garrison instead of the
        // caller simply removing the task and abandoning the courier mid-field with nothing to do
        // — every other phase (travel/deposit/IsComplete) already reads HomeHex fresh each call, so
        // redirecting it here is enough for the existing lifecycle to carry the courier the rest of
        // the way on its own, no second "return home" mechanism needed. AiTurnController.
        // NearestOwnGarrisonHex always resolves to SOME hex (falls back to the citadel) as long as
        // this player has any garrison left at all — if even the citadel is gone the game is over
        // for this player already (BuildingRegistry.BuildingDestroyed's own win-condition hook), so
        // there's nothing left to redirect toward at that point either.
        public static void RedirectToNearestOwnBase(PlayerSetupData player, AiTask task)
        {
            task.HomeHex = AiTurnController.NearestOwnGarrisonHex(player, task.Army.Hex);
            task.TargetHex = task.HomeHex;
        }

        // Nearest OTHER own garrison that can spare a non-hero without dropping below its own
        // secure floor (see AiArmyRoles.CanSpareGarrisonMember) — never a Recce unit (solo-only by
        // design elsewhere in this codebase), lowest strategic value first so this never bleeds a
        // donor's own best defenders, same ordering FindGarrisonSeedUnit already uses for the
        // equivalent BuildBase pick. allowCitadelEmergency:false (2026-08-24 P0 fix, project
        // owner's own report) — unlike an occasional Raid/Reorg recruit, this search loops call
        // after call until a base is secure, and the citadel is very often the nearest donor, so it
        // must respect the SAME secureCitadelMinNonHeroUnits floor every other base already does
        // here rather than the method's own default unconditional citadel exemption.
        public static UnitData FindReinforcementSource(PlayerSetupData player, HexCoord targetHex, out ArmyData source)
        {
            source = null;
            UnitData best = null;
            int bestDistance = int.MaxValue;
            foreach (ArmyData donor in ArmyRegistry.AllForOwner(player).Where(a => a.IsGarrison && !a.Hex.Equals(targetHex)))
            {
                UnitData candidate = donor.Members
                    .Where(m => !m.IsHero && !m.HasAbility(UnitAbilities.Recce)
                        && AiArmyRoles.CanSpareGarrisonMember(player, donor, m, allowCitadelEmergency: false))
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
                // 2026-08-24 P1 fix (project owner's own report): used to gate on the raw AP cost
                // alone, which incorrectly blocked a dispatch even when DispatchBaseReinforcement
                // Routine was about to find and reuse a free disposable empty shell at the donor's
                // own hex instead of spending anything — see AiOperations.CanDispatchCourier's own
                // comment, which mirrors exactly what that routine is about to try.
                if (recruit == null || source == null || !AiOperations.CanDispatchCourier(player, root, source.Hex))
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
            if (garrison == null || cargo == null)
                return null;

            // 2026-08-24 P1 fix (project owner's own report): CanAffordTransferInto below only ever
            // checks AP, never capacity — a garrison that filled up from some OTHER source (a card,
            // GarrisonReorgTask) while this courier was already en route used to make this branch
            // propose the exact same doomed ArmyActions.TransferMember-bound decision every single
            // step forever (IsComplete's own P0 fix, above, deliberately keeps this task alive while
            // task.Army != null, so nothing else would ever free it either) — permanently occupying
            // this player's own maxConcurrentSecureBase slot with a courier that can never unload.
            if (!garrison.HasRoom)
                return BuildFullGarrisonFallback(player, root, ctx, task);

            if (!GarrisonReorgTask.CanAffordTransferInto(garrison, cargo))
                return null;

            var move = new GarrisonReorgTask.ConsolidationMove(task.Army, cargo, garrison,
                $"\"{task.Army.Name}\" delivers {cargo.Name} to secure the garrison at ({task.HomeHex.Q},{task.HomeHex.R})");
            return AiDecision.DepositReinforcement(move, task, AiConfig.secureBaseDeliverScore);
        }

        // See BuildDecision's own !garrison.HasRoom branch. Redirects the stuck courier to the
        // nearest OTHER own garrison that genuinely has a free slot right now — same "just repoint
        // HomeHex/TargetHex, let the existing travel/deposit machinery carry it the rest of the way"
        // trick RedirectToNearestOwnBase already established for a lost base (2026-08-24 P0 fix,
        // above), reused here for a full-instead-of-lost one. Two ways this can happen: the base is
        // already secure (something else filled the last slot first — a harmless race, the courier's
        // cargo is simply surplus now) or it isn't (a genuine composition anomaly — too many Hero/
        // Recce members occupying capacity without ever counting toward the non-hero secure floor;
        // logged as such so it's visible, but left for GarrisonReorgTask's own independent end-of-
        // turn drain to actually rebalance — this method's only job is getting the courier's cargo
        // to SOMEWHERE useful, not fixing that composition itself). Recurses into BuildDecision once
        // with the new HomeHex — safe from runaway recursion since the courier hasn't moved yet, so
        // that call always lands on the "not yet arrived, travel there" branch, never this same
        // full-garrison branch again. Null (task left registered, retried next step) only if truly
        // no own garrison anywhere has room right now.
        private static AiDecision BuildFullGarrisonFallback(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            HexCoord fullHex = task.HomeHex;
            string why = IsSecure(player, fullHex) ? "already secure" : "composition anomaly — likely too many Hero/Recce members";

            ArmyData nearestWithRoom = ArmyRegistry.AllForOwner(player)
                .Where(a => a.IsGarrison && a.HasRoom && !a.Hex.Equals(fullHex))
                .OrderBy(a => HexGridMath.Distance(a.Hex, task.Army.Hex))
                .FirstOrDefault();
            if (nearestWithRoom == null)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — garrison at ({fullHex.Q},{fullHex.R}) is full "
                    + $"({why}), no other own garrison has room right now, \"{task.Army.Name}\" waits.");
                return null;
            }

            task.HomeHex = nearestWithRoom.Hex;
            task.TargetHex = task.HomeHex;
            AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — garrison at ({fullHex.Q},{fullHex.R}) is full "
                + $"({why}), \"{task.Army.Name}\" redirected to ({task.HomeHex.Q},{task.HomeHex.R}) instead.");
            return BuildDecision(player, root, ctx, task);
        }
    }
}
