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
    // Поведение (2026-08-19 redesign — project owner's own combat-reaction spec):
    //   - Нейтралы — от них НИКОГДА не убегаем, только жёсткая отмена задачи (HasNeutralThreat,
    //     see AiEconomyPlanner.AdvanceEconomyTask). Триггер новой задачи и отмена уже идущей теперь
    //     смотрят на ОДИН и тот же радиус=1 (neutralBuildTriggerRadius, HasAdjacentNeutralThreat) —
    //     2026-08-21 fix, project owner's own report: раньше отмена смотрела на более широкий
    //     radius=2, так что хекс, прошедший узкий триггер, но попадающий под широкий avoid, тут же
    //     отменялся после старта, оставляя после себя пустую, никогда не переиспользуемую армию.
    //     Мимо нейтрала можно свободно ХОДИТЬ — этот радиус только про то, где нельзя СТРОИТЬ.
    //   - Настоящие враги (не нейтралы) — реакция теперь зависит от состава и силы (ArmyBeats,
    //     WorthIt): герой-одиночка (IsSolo) по-прежнему не может драться, тот же жёсткий кэнсел,
    //     что и раньше. Армия с эскортом сравнивает силы: враг сильнее — убегаем в цитадель И
    //     отменяем задачу; враг слабее и достижим за текущий ход — атакуем его вместо похода на
    //     ресурсный хекс этот шаг (задача при этом не отменяется, следующий шаг снова оценивает
    //     обстановку); враг слабее, но вне радиуса хода — просто продолжаем идти к цели.
    //   - "Ничего больше строить" (HasAnythingToBuild) — герой, оставшийся без задачи,
    //     возвращается в цитадель (см. AiTurnController's own TryEconomyReturnHomeCandidates).
    public static class BuildFacilityTask
    {
        public static bool IsEligibleComposition(ArmyData army) => AiArmyRoles.IsHeroLed(army);

        // "герой в армии один" — the project owner's own 2026-08-19 combat-reaction spec: a bare
        // hero (no escort) still can't fight or hunt (see AdvanceEconomyTask's own FindNextVisitedStep
        // comment), so every threat-reaction branch below treats solo differently from escorted.
        public static bool IsSolo(ArmyData army) => army != null && army.Members.Count == 1;

        // FindNearestHeroAnywhere, not the plain FindNearestHero — a hero folded solo into the
        // Garrison stockpile still counts as "герой с армией или без" (see this class's own
        // comment above) even though it isn't its own led army yet; see
        // AiTurnController.TryStartEconomyCandidates for how a GarrisonHero pick gets detached
        // before it can actually lead the trip.
        public static AiEconomyPlanner.NearestHeroPick FindActor(PlayerSetupData player, HexCoord targetHex) =>
            AiEconomyPlanner.FindNearestHeroAnywhere(player, targetHex);

        // Кросс-категорийный скор — что AiDecision.Score actually carries into Decide, competing
        // against Разведка/Агрессия/Менеджмент. "+" — IncomeBehindBonus (отставание по доходу,
        // не зависит от конкретного хекса). "-" — дальность от ЦИТАДЕЛИ свыше
        // citadelPenaltyFreeRadius хексов (citadelDistancePenaltyPerHex — общая формула с
        // RaidWeakerArmyTask.ProximityScore, project owner's own 2026-08-19 rebalance). Дефицит/
        // отсутствие добычи (см. RankHex) сюда больше НЕ входит — это внутренний выбор ХЕКСА,
        // не должен утекать в кросс-категорийный скор (project owner's own call, тот же принцип,
        // что у Разведки — see AiConfig.scoutProximityWeight's own comment).
        public static float ScoreHex(PlayerSetupData player, PlayerRoot root, HexCoord hex, ResourceType resourceType)
        {
            return AiGoalScorer.IncomeBehindBonus(player) + CitadelDistanceScore(player, hex);
        }

        // Внутренний выбор — какой из известных свободных ресурсных хексов строить первым
        // (project owner's own 2026-08-19 call). Используется ТОЛЬКО AiEconomyPlanner.
        // TryStartEconomyCandidates' own pre-pass, чтобы выбрать один хекс до генерации кандидата
        // — сам этот скор никогда не попадает в AiDecision.Score.
        public static float RankHex(PlayerSetupData player, PlayerRoot root, HexCoord hex, ResourceType resourceType)
        {
            return ScarcityBonus(player, root, resourceType) + CitadelDistanceScore(player, hex);
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
                return AiConfig.buildNoIncomeBonus;
            return -root.GetResource(type) * AiConfig.buildScarcityWeight;
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
            int distance = HexGridMath.Distance(citadelHex, hex);
            int overage = System.Math.Max(0, distance - AiConfig.citadelPenaltyFreeRadius);
            return -overage * AiConfig.citadelDistancePenaltyPerHex;
        }

        // Same radius as HasAdjacentNeutralThreat below (2026-08-21 — see AiConfig.
        // neutralBuildTriggerRadius's own comment for why these two used to disagree and what that
        // broke) — kept as two separately-named methods purely for call-site clarity (this one
        // cancels an already-committed task, mid-AdvanceEconomyTask; the other gates whether a NEW
        // one may start at all, in TryStartEconomyCandidates' own hex pre-pass), not because they
        // check anything different any more.
        public static bool HasNeutralThreat(PlayerSetupData player, HexCoord targetHex) =>
            AiMapMemory.HasKnownNeutralWithin(player, targetHex, AiConfig.neutralBuildTriggerRadius);

        public static bool HasAdjacentNeutralThreat(PlayerSetupData player, HexCoord targetHex) =>
            AiMapMemory.HasKnownNeutralWithin(player, targetHex, AiConfig.neutralBuildTriggerRadius);

        public static bool HasEnemyThreat(PlayerSetupData player, HexCoord targetHex) =>
            AiMapMemory.HasKnownEnemyWithin(player, targetHex, AiConfig.economySafetyRadius);

        // Known REAL (non-neutral) enemy army within economySafetyRadius of `targetHex` — unlike
        // HasEnemyThreat's own plain boolean, this hands back the actual sighting so
        // AdvanceEconomyTask can weigh it with WorthIt (see ArmyBeats below) instead of hard-
        // cancelling on sight (project owner's own 2026-08-19 call: "убегаем если сильнее, атакуем
        // если слабее"). First match only, same shape as RaidWeakerArmyTask.NearbyThreat, just
        // scoped to Экономика's own economySafetyRadius rather than raidThreatRadius.
        public static AiMapMemory.KnownEnemySighting? NearbyRealEnemyThreat(PlayerSetupData player, HexCoord targetHex)
        {
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { targetHex }, AiConfig.economySafetyRadius))
                if (sighting.Owner != null && !sighting.Owner.IsNeutral)
                    return sighting;
            return null;
        }

        // Two-sided WorthIt read (our net edge vs `threat`, hex defense bonus folded onto the
        // defender's side same as WorthIt.Score/RequiredStrengthAt always do — and, 2026-08-20,
        // also handed separately into the per-unit CanDamageAll coverage check, not just the
        // aggregate edge) — whether `army` could actually win a fight against `threat` right now.
        // False for a solo hero too (see IsSolo) even though this method itself doesn't check
        // composition — a hero-only army has zero AttackSum/DefenseSum, so the two-sided edge
        // always comes out negative on its own.
        public static bool ArmyBeats(ArmyData army, AiMapMemory.KnownEnemySighting threat, HexMap map)
        {
            float hexBonus = WorthIt.HexDefenseBonus(threat.Hex, map);
            float enemyDefense = threat.DefenseSum + hexBonus;
            return WorthIt.IsWorthIt(army, enemyDefense, threat.AttackSum, threat.Defenders, hexBonus);
        }

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
