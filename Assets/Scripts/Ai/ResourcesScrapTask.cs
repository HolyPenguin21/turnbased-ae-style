using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Экономика · Задача 2 (AI architecture doc, section 02 · 2.2) — see BuildFacilityTask's own
    // class comment for why task-specific rules live here.
    //
    // Цель — известный ресурсный хекс без добывающей facility нужного типа; не проверяет занятость
    // BuildingRegistry как Задача 1 — может целиться в тот же хекс параллельно с Задачей 1.
    //
    // Композиция — один юнит с добывающей способностью (Game.Cards.UnitAbilities.CollectHuman/
    // Energy/Materials/Tech), уже соло в своей армии — сам поиск такого юнита и есть
    // композиционный фильтр (FindActor, delegates to AiEconomyPlanner.FindNearestSoloCollector;
    // не отдельный предикат, как VisitHexTask.IsEligibleComposition — Задача 2 никогда не
    // перебирает армии сама, только хексы). Если подходящий юнит погребён в чужой армии —
    // отдельная подготовительная подзадача на стороне оркестратора (AiTurnController.
    // TryStartCollectorDetachCandidates, через AiEconomyPlanner.FindCollectorDetachPlan).
    //
    // Поведение — известная вражеская армия в economySafetyRadius от цели отменяет задачу
    // насовсем (HasEnemyThreat) — нет отдельной проверки на нейтралов, в отличие от Задачи 1: сама
    // добыча никого не тревожит и не требует "хорошего места", только безопасного. Завершение —
    // не отдельный шаг, а проверка HasExtractionFacility каждый ход: как только на этом же хексе
    // достроена facility нужного типа, юнит просто освобождается — сама добыча идёт пассивно
    // каждый ход вне AI-цикла решений (GameTurnController.CollectArmyIncomeAt).
    public static class ResourcesScrapTask
    {
        public static ArmyData FindActor(PlayerSetupData player, HexCoord targetHex, ResourceType type, AiResourcePool pool) =>
            AiEconomyPlanner.FindNearestSoloCollector(player, targetHex, type, pool);

        // Условие "+" к скору — та же IncomeBehindBonus, что и у Задачи 1, но без дефицитного
        // бонуса (Задача 2 никогда не выбирает МЕЖДУ несколькими ресурсными типами — юнит уже
        // жёстко привязан к своему типу способностью) и БЕЗ дистанции вообще (project owner's own
        // call, 2026-08-17) — юнит-сборщик просто идёт куда есть подходящий известный хекс, сколько
        // бы шагов это ни заняло; в отличие от Задачи 1/Разведки/Агрессии, у которых удалённость от
        // цитадели намеренно снижает приоритет, здесь никакого штрафа/обнуления по дальности нет.
        public static float ScoreHex(PlayerSetupData player) => AiGoalScorer.IncomeBehindBonus(player);

        public static bool HasExtractionFacility(HexCoord hex, ResourceType type)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.HasFacilityWithAbility(UnitAbilities.CollectAbilityFor(type));
        }

        public static bool HasEnemyThreat(PlayerSetupData player, HexCoord targetHex) =>
            AiMapMemory.HasKnownEnemyWithin(player, targetHex, AiConfig.Current.economySafetyRadius);
    }
}
