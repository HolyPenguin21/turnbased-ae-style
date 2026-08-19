using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Экономика · Задача 1 (AI architecture doc, section 02 · 2.2) — composition eligibility,
    // target scoring, threat reaction, and "nothing left to do" all live here now, same split as
    // Разведка's own VisitHexTask (see its own class comment for why).
    // Actor lookup itself (AiEconomyPlanner.FindNearestHero) stays in AiEconomyPlanner since it's
    // genuinely shared with ResourcesScrapTask; what's specific to Задача 1 alone lives here.
    //
    // Цель — конкретный свободный известный ресурсный хекс (AiMapMemory.IsResourceHexKnown, ещё
    // без здания) — построить на нём добывающую facility.
    //
    // Композиция — любая ведомая героем армия (IsEligibleComposition delegates to
    // AiArmyRoles.IsHeroLed — с эскортом или без, даже бывший разведчик подходит, важен только
    // сам факт наличия героя). Берётся БЛИЖАЙШИЙ к хексу герой вообще (FindActor), независимо от
    // текущей занятости — если он занят другой задачей (кроме другого BuildFacility со скудным
    // ресурсом, см. ScoreHex), она снимается (см. AiTurnController.TryStartEconomyCandidates's
    // own PreemptedTask).
    //
    // Поведение — реакция на угрозу ЖЁСТКАЯ в обоих случаях (не временный уход, как у Разведки):
    // известная НЕЙТРАЛЬНАЯ армия рядом с целью — эта позиция просто плохая, задача отменяется
    // (HasNeutralThreat); известная ВРАЖЕСКАЯ армия рядом — тоже отменяется (HasEnemyThreat), а не
    // временное отступление в гарнизон, раз герой-одиночка без сопровождения всё равно не может
    // ничего противопоставить угрозе. "Ничего больше строить" (HasAnythingToBuild) — герой,
    // оставшийся без задачи, возвращается в цитадель (см. AiTurnController's own
    // TryEconomyReturnHomeCandidates).
    public static class BuildFacilityTask
    {
        public static bool IsEligibleComposition(ArmyData army) => AiArmyRoles.IsHeroLed(army);

        // FindNearestHeroAnywhere, not the plain FindNearestHero — a hero folded solo into the
        // Garrison stockpile still counts as "герой с армией или без" (see this class's own
        // comment above) even though it isn't its own led army yet; see
        // AiTurnController.TryStartEconomyCandidates for how a GarrisonHero pick gets detached
        // before it can actually lead the trip.
        public static AiEconomyPlanner.NearestHeroPick FindActor(PlayerSetupData player, HexCoord targetHex) =>
            AiEconomyPlanner.FindNearestHeroAnywhere(player, targetHex);

        // Условия "+" к скору — IncomeBehindBonus (отставание по доходу) + дефицитный бонус (см.
        // ScarcityBonus — по инкаму в первую очередь, по текущему запасу только во вторую).
        // Условие "-" к скору — дальность от ЦИТАДЕЛИ (citadelDistancePenalty, тот же общий вес и
        // та же "от цитадели, не от ближайшего своего хекса вообще" формула, что и у
        // VisitHexTask/RaidWeakerArmyTask — project owner's own call, 2026-08-17, replacing the
        // former AiEconomyPlanner.EconomyHexScore proximity-to-nearest-own-hex term).
        public static float ScoreHex(PlayerSetupData player, PlayerRoot root, HexCoord hex, ResourceType resourceType)
        {
            return AiGoalScorer.IncomeBehindBonus(player) + ScarcityBonus(player, root, resourceType)
                + CitadelDistanceScore(player, hex);
        }

        // Дефицит считается по ИНКАМУ в первую очередь, по текущему запасу — только во вторую
        // (project owner's own correction, 2026-08-17). Раньше это был чистый -GetResource(type):
        // богатый одноразовый эвент-бонус того типа, который ВООБЩЕ не добывается, читался как
        // "и так много" и терял приоритет на постройку, хотя реальной добычи нет никакой. Теперь
        // "нет добычи вообще" — отдельный, доминирующий флаг: даёт фиксированный buildNoIncomeBonus
        // и полностью ИГНОРИРУЕТ текущий запас (не просто перевешивает его — запас тут не в счёт
        // совсем, иначе достаточно большой эвент-бонус мог бы теоретически пересилить константу).
        // Только когда добыча уже идёт откуда-то (см. HasIncomeSource), в дело идёт прежняя
        // "строим то, чего меньше всего в закромах" эвристика (buildScarcityWeight) — та же, что
        // Разведка · Задача 2 использует для WantedResourceType.
        private static float ScarcityBonus(PlayerSetupData player, PlayerRoot root, ResourceType type)
        {
            if (root == null)
                return 0f;
            if (!HasIncomeSource(player, type))
                return AiConfig.Current.buildNoIncomeBonus;
            return -root.GetResource(type) * AiConfig.Current.buildScarcityWeight;
        }

        // Считает только здания (цитадель + размещённые Facility, через BuildingData.
        // CollectedAmount — то же число, что реально капает каждый ход в GameTurnController.
        // CollectResourceIncome); юнит-сборщик без facility (см. ResourcesScrapTask) сюда
        // намеренно не входит — это отдельный, более гибкий/временный источник, а не то, ради чего
        // существует именно Задача 1 (построить постоянную добычу).
        private static bool HasIncomeSource(PlayerSetupData player, ResourceType type) =>
            BuildingRegistry.AllBuildings().Any(b => b.Owner == player && b.CollectedAmount(type) > 0);

        private static float CitadelDistanceScore(PlayerSetupData player, HexCoord hex)
        {
            if (!player.CitadelHexQ.HasValue || !player.CitadelHexR.HasValue)
                return 0f;
            var citadelHex = new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value);
            return -HexGridMath.Distance(citadelHex, hex) * AiConfig.Current.citadelDistancePenalty;
        }

        public static bool HasNeutralThreat(PlayerSetupData player, HexCoord targetHex) =>
            AiMapMemory.HasKnownNeutralWithin(player, targetHex, AiConfig.Current.neutralBuildAvoidRadius);

        public static bool HasEnemyThreat(PlayerSetupData player, HexCoord targetHex) =>
            AiMapMemory.HasKnownEnemyWithin(player, targetHex, AiConfig.Current.economySafetyRadius);

        // "Больше строить нечего" — стадия, включающая AiTurnController's own
        // TryEconomyReturnHomeCandidates: любой свободный известный ресурсный хекс без здания
        // где-либо на карте всё ещё считается "есть что строить", даже если сейчас занят другой
        // BuildFacility-задачей — тот хекс просто уже застолблён, не "недоступен навсегда".
        public static bool HasAnythingToBuild(PlayerSetupData player)
        {
            foreach (HexCoord hex in HexResourceBonusRegistry.AllBonusHexes())
            {
                if (!AiMapMemory.IsResourceHexKnown(player, hex) || BuildingRegistry.FindAt(hex) != null)
                    continue;
                return true;
            }
            return false;
        }
    }
}
