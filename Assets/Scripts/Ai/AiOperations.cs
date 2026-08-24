using System.Collections;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Generic army-to-army reinforcement mechanics (2026-08-24, project owner's own SecureBase
    // architecture spec) — create/reuse a courier, move it, hand its cargo over, clean up the empty
    // shell afterward. Deliberately holds NO strategic knowledge of its own: not which base needed
    // securing, not how many defenders it needs, not why this particular donor/unit was picked —
    // every one of those calls already happened on the calling task class (SecureBaseTask today)
    // before it ever built the AiDecision this file's routines execute. Same "planner/task decide,
    // this just does it" split AiAggressionPlanner's own DispatchReinforcementRoutine/
    // ReinforceSwapRoutine already follow for Агрессия's RaidReinforce — those stay right where
    // they are (Агрессия-scoped, Агрессия-flavored logging); this file exists so Оборона's own
    // SecureBase gets the identical mechanic without borrowing Агрессия's class or its log text.
    public static class AiOperations
    {
        // Whether a courier could actually be produced at `hex` right now — either a disposable
        // empty shell already sitting there (see GarrisonReorgTask.FindDisposableEmptyArmyAt, free
        // to reuse) or, failing that, enough AP left to spend on a brand-new one
        // (ArmyActions.CreateArmyApCost). 2026-08-24 P1 fix (project owner's own report) — the
        // calling task class used to pre-check ONLY the AP cost, which incorrectly blocked a
        // dispatch decision even when a free reusable shell existed and DispatchBaseReinforcement
        // Routine below was about to find and use it for free — the pre-check now mirrors exactly
        // what that routine itself is about to try, kept here (not duplicated on the task class)
        // since it's the same "does the mechanic have what it needs" question CanAffordTransferInto
        // already answers for the deposit side.
        public static bool CanDispatchCourier(PlayerSetupData player, PlayerRoot root, HexCoord hex) =>
            GarrisonReorgTask.FindDisposableEmptyArmyAt(player, hex) != null
            || (root != null && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost));

        // SecureBase's own dispatch step — mirrors AiAggressionPlanner.DispatchReinforcementRoutine
        // exactly (reuse a disposable empty shell at the donor's hex before spending AP on a new
        // one, same as every other courier-spawn in this codebase), just under its own name/log
        // prefix so debug output reads as SecureBase, not Агрессия. Fills in decision.Task.Army once
        // the courier is real — the task itself was already registered (or already existed) by the
        // time this runs, see AiTurnController.Commit's own registration rule.
        public static IEnumerator DispatchBaseReinforcementRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData source = decision.ExistingArmy;
            UnitData recruit = decision.CollectorUnit;
            yield return AiTurnController.PanTo(ctx, source.Hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;

            ArmyData reusedCourier = GarrisonReorgTask.FindDisposableEmptyArmyAt(player, source.Hex);
            ArmyData courier = reusedCourier ?? ArmyActions.CreateArmy(player, source.Hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            if (courier == null)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — not enough AP for a courier toward "
                    + $"({decision.Task.HomeHex.Q},{decision.Task.HomeHex.R}).");
                yield break; // task stays registered, Army still null — retried fresh next step
            }

            if (ArmyActions.TransferMember(recruit, source, courier, ctx.HexSelection, out string failReason))
            {
                decision.Task.Army = courier;
                string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — {decision.Reason}.{delta}");
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — couldn't dispatch reinforcement from "
                    + $"\"{source.Name}\" — {failReason}");
                // The courier was already created above but never got its recruit — never leave an
                // empty shell army behind just because the transfer itself was rejected (same fix
                // DispatchReinforcementRoutine's own comment describes for RaidReinforce).
                ctx.HexSelection?.DeleteArmyIfEmptied(courier);
            }

            yield return AiTurnController.WaitStep(ctx);
        }

        // SecureBase's own delivery step — courier has arrived at the base's own hex
        // (SecureBaseTask.BuildDecision already verified that before building this decision).
        // Mechanical only: moves decision.ConsolidationMove.Unit from the courier into the base's
        // own garrison, deletes the courier shell once it's empty, and clears task.Army back to
        // null so the very next continuation naturally re-enters SecureBaseTask's own
        // AcquireReinforcement phase. Does NOT decide whether the task is done — that's
        // SecureBaseTask.IsComplete's own call, made by AiDefencePlanner BEFORE this routine (or
        // any other continuation) ever runs again.
        public static IEnumerator DepositReinforcementRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            GarrisonReorgTask.ConsolidationMove move = decision.ConsolidationMove;
            AiTask task = decision.Task;
            yield return AiTurnController.PanTo(ctx, move.Source.Hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;

            bool moved = ArmyActions.TransferMember(move.Unit, move.Source, move.Target, ctx.HexSelection, out string failReason);
            if (moved)
            {
                string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                int nonHero = move.Target.Members.Count(m => !m.IsHero);
                AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — {decision.Reason}, garrison now has "
                    + $"{nonHero} non-hero defender(s) (needs {AiConfig.secureBaseMinNonHeroUnits}).{delta}");
                ctx.RecordArmyVisit(move.Unit, move.Source, move.Target);
                if (task != null)
                    task.Army = null;
                ctx.HexSelection?.DeleteArmyIfEmptied(move.Source);
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — couldn't deliver {move.Unit.Name} — {failReason}");
                // Task/courier left exactly as they are — retried fresh next step.
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(move.Target);
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
