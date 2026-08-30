using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Экономика · Задача 1's own soft resource lock — the project owner's own spec: once a hero
    // is sent to build an extraction facility, the resources that build will eventually cost must
    // be set aside AS THEY TRICKLE IN (the task can start before the AI can actually afford the
    // facility yet — the hero may need several turns just to walk there), so no OTHER AI spend
    // (a different BuildFacility hex, a card off the hand — the owner's own "все траты ИИ") can
    // eat into them meanwhile. A reservation never removes anything from PlayerRoot's own
    // stockpile — it's purely an accounting claim every AI spend path checks (see CanAfford/
    // Available) before proposing a candidate; PlayerRoot only actually loses the resources once
    // BuildFacilityRoutine's own TryBuildExtractionFacility call succeeds for real.
    public static class AiResourceReservation
    {
        private static readonly ResourceType[] AllTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        private static readonly Dictionary<AiTask, int[]> ReservedByTask = new Dictionary<AiTask, int[]>();

        public static void Clear() => ReservedByTask.Clear();

        // Called once per turn `task` is still active (see AiTurnController.AdvanceEconomyTask,
        // both while the hero is still travelling and once arrived) — tops each resource type up
        // toward `cost`'s own amount by however much is currently free (not already claimed by
        // this or any other active BuildFacility task), never more than that. A task's own
        // reservation only ever grows, and only up to `cost` — it shrinks only via Release.
        public static void TopUp(PlayerRoot root, PlayerSetupData player, AiTask task, ResourceCost cost)
        {
            if (root == null || task == null || cost == null)
                return;
            if (!ReservedByTask.TryGetValue(task, out int[] reserved))
                ReservedByTask[task] = reserved = new int[AllTypes.Length];

            foreach (ResourceType type in AllTypes)
            {
                int index = (int)type;
                int needed = cost.Get(type);
                if (reserved[index] >= needed)
                    continue;
                int freeNow = Math.Max(0, root.GetResource(type) - TotalReservedExcluding(player, type, task));
                reserved[index] += Math.Min(needed - reserved[index], freeNow);
            }
        }

        // Whether `task`'s own reservation now fully covers `cost` in every resource type —
        // AdvanceEconomyTask's own "may it actually build this turn" gate once the hero has
        // arrived. True for a zero-cost `cost` even with no reservation on record at all.
        public static bool IsFullyReserved(AiTask task, ResourceCost cost)
        {
            if (cost == null)
                return false;
            ReservedByTask.TryGetValue(task, out int[] reserved);
            foreach (ResourceType type in AllTypes)
            {
                int have = reserved != null ? reserved[(int)type] : 0;
                if (have < cost.Get(type))
                    return false;
            }
            return true;
        }

        // Frees whatever `task` had claimed — called at every place a BuildFacility task ends,
        // win or lose (built, abandoned after MaxBuildAttempts, army lost, enemy showed up nearby
        // — see AiTurnController), so the resources return to the shared pool immediately rather
        // than staying locked behind a task that no longer exists.
        public static void Release(AiTask task)
        {
            if (task != null)
                ReservedByTask.Remove(task);
        }

        // Every AI spend path's own "what can I actually still touch" read — a hand card (see
        // AiTurnController.TryPlayCard) included, not just a rival BuildFacility task, per the
        // owner's own "все траты ИИ" call. `excluding` lets a task read what's free without
        // double-subtracting its OWN already-reserved amount (2026-08-26 P1 fix — see
        // AiTurnController.CanIssueMoveNow's own `reservationOwner` param for why a freshly
        // launched sortie needs this); every other caller passes null, unchanged.
        //
        // Strategy V2 is a mutually-exclusive owner of the whole AI turn and intentionally does
        // not execute V1 AiTaskRegistry work. Legacy task reservations can therefore survive only
        // as stale V1 bookkeeping while V2 is active. Subtracting them from V2 Phase A/Initiative
        // created a split-brain resource view: demand materialization saw less stock than Phase B
        // and the allocator. Under the V2 switch the real PlayerRoot stockpile is the authoritative
        // physical pool; V2's own atomic spending/claims are handled by its pipeline instead.
        public static int Available(PlayerRoot root, PlayerSetupData player, ResourceType type, AiTask excluding = null)
        {
            if (root == null)
                return 0;
            if (AiConfig.aiStrategyV2Enabled)
                return root.GetResource(type);
            return root.GetResource(type) - TotalReservedExcluding(player, type, excluding);
        }

        public static bool CanAfford(PlayerRoot root, PlayerSetupData player, ResourceCost cost)
        {
            if (root == null || cost == null)
                return false;
            foreach (ResourceType type in AllTypes)
                if (Available(root, player, type) < cost.Get(type))
                    return false;
            return true;
        }

        // Sum of every OTHER active BuildFacility/BuildBase task's own claim on `type` —
        // `excluding` lets a task's own continuation (TopUp/a future affordability check) read
        // what's free to grow INTO without double-subtracting its own already-reserved amount. A
        // brand-new candidate task (not yet in AiTaskRegistry) naturally excludes itself just by
        // not being found in the loop below, `excluding` only matters once a task is actually
        // registered. Widened 2026-08-23 (project owner's own report/spec) to also count BuildBase
        // — a founded-base card costs real resources same as a facility, and the two must share
        // the same "все траты ИИ" pool this class already enforces rather than being able to eat
        // into each other's reservation.
        // Widened 2026-08-26 (project owner's own report) to also count AirStrike/AirRecon — see
        // AiAviationSupport.LaunchRoutine's own single TopUp call for its own ActivationEnergyCost,
        // reserved the instant a launch decision commits so no OTHER AI spend can eat the Energy
        // this same army's own first move order needs a step or more later.
        private static int TotalReservedExcluding(PlayerSetupData player, ResourceType type, AiTask excluding)
        {
            int total = 0;
            foreach (AiTask task in AiTaskRegistry.TasksFor(player))
            {
                if (task == excluding || (task.Kind != AiTaskKind.BuildFacility && task.Kind != AiTaskKind.BuildBase
                    && task.Kind != AiTaskKind.AirStrike && task.Kind != AiTaskKind.AirRecon))
                    continue;
                if (ReservedByTask.TryGetValue(task, out int[] reserved))
                    total += reserved[(int)type];
            }
            return total;
        }
    }
}
