using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Агрессия · Задача 1 (AI architecture doc, section 02 · «Агрессия») — full redesign, 2026:
    // no longer a scouting-adjacent "attack whatever weaker army happens to be visible along a
    // recon route" side-task. Composition eligibility, target scoring, threat reaction, and
    // "nothing left worth it" all live here, same split as every other task class — see
    // VisitHexTask's own class comment for why. Replaces AiAggressionPlanner entirely (deleted).
    //
    // Цель — атаковать известную нейтральную армию и/или Hex Event она может охранять
    // (AiMapMemory.AllKnownNeutralSightings/KnownEventGuardHexes — "collision hex" case: a
    // physical neutral army and a card-guarded event can share one hex as two SEPARATE fights,
    // see HexEventRegistry.Entry's own comment). Разведка убрана целиком — эта задача больше не
    // открывает туман попутно, только целится в уже известное. Раздел 5 (см. AiConfig's own
    // "рейд экономики" header) временно добавляет известные вражеские экономические постройки в
    // тот же пул целей для тестов — планируется переехать в свою собственную задачу позже.
    //
    // Композиция — НЕ фиксированный предикат состава, как у любой другой задачи (см.
    // AiArmyRoles's старую IsMakeshiftScoutCapable — та композиция удалена вместе с этим
    // редизайном). Вместо этого: против выбранной цели требуется армия, которая проходит
    // WorthIt.Score (двусторонний разбор — и наша сила против их защиты, и их сила против нашей
    // защиты) относительно самой сильной из присутствующих защит на её хексе — RequiredStrengthAt
    // уже берёт максимум из нейтрала/охраны эвента/охраны постройки, НЕ их сумму (два отдельных
    // боя, не один общий).
    // Два пути набрать такую силу — оба в AiTurnController, не здесь (см. его же "Композиция"
    // комментарий на TryRaidAssembleCandidates):
    //   1) уже есть подходящая ПРОСТАИВАЮЩАЯ армия целиком (FindReadyIdleArmy) — используется как
    //      есть, без сборки.
    //   2) иначе — сборка: обязателен герой (см. NeedsHero), плюс не-геройские юниты подтягиваются
    //      по одному из гарнизона и из любых других простаивающих армий на карте (простаивающие
    //      армии НЕ на хексе гарнизона сначала должны туда дойти, это отдельный "recall"-кандидат
    //      оркестратора, не эта задача).
    //
    // Поведение:
    //   - Атакует и по ПАМЯТИ, не только по видимости — цель выбирается из AiMapMemory, хекс не
    //     обязан быть видимым прямо сейчас (в отличие от старой AiAggressionPlanner.
    //     FindRaidTargetHex, которая требовала CURRENTLY VISIBLE).
    //   - «Слабее» больше не грубый локальный плейсхолдер — WorthIt.Score на весь модуль.
    //   - Реакция на угрозу — НЕ временный уход на 1 ход (в отличие от Разведки): известная
    //     НЕ-нейтральная (настоящая вражеская) армия в raidThreatRadius от текущего хекса — если
    //     наша сила её не бьёт, задача переходит в AiTask.Retreating и армия идёт домой (см.
    //     AiTurnController.TryContinueRaidTask); если бьёт — атакуем ЕЁ вместо исходной цели этот
    //     ход (raidCounterAttackBonus), после чего обычная переоценка на следующий ход сама решит,
    //     возвращаться ли к нейтралу/эвенту.
    //   - До maxConcurrentRaid задач одновременно (без изменений).
    //   - Нет отдельного дедлайн-чека "больше некого бить" (MaxPossibleAttack/
    //     CanEventuallyDamageToughest, удалены 2026-08-22, project owner's own call): армия просто
    //     собирается, пока не станет равной нужному коэффициенту, либо пока рекрутов взять
    //     неоткуда (FindRecruitAt возвращает null) — тот же естественный стоп, который раньше
    //     давал этот отдельный чек, только на шаг позже.
    public static class RaidWeakerArmyTask
    {
        public readonly struct RaidTarget
        {
            public readonly HexCoord Hex;
            public readonly ThreatStrength Threat;
            public readonly float Score;
            public readonly string Reason;

            public RaidTarget(HexCoord hex, ThreatStrength threat, float score, string reason)
            {
                Hex = hex;
                Threat = threat;
                Score = score;
                Reason = reason;
            }
        }

        // Whichever of `hex`'s known guards (physical army sighting vs Hex Event card-guard) is
        // the STRONGER one — see this class's own "Цель" comment for why the max, not the sum, of
        // the two: a physical army and a card-guarded event on the same hex fight separately,
        // never together, so only whichever one `army` would actually face matters here. Defense
        // and Attack are always read off that SAME source, never mixed across the two (an event
        // guard's Defense paired with a physical army's Attack would describe a fight nobody
        // could ever actually have).
        public readonly struct ThreatStrength
        {
            public readonly float Defense;
            public readonly float Attack;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> Defenders;
            // The hex's own terrain/Base-building contribution already folded into Defense above
            // (see WorthIt.HexDefenseBonus) — kept separately too so a per-unit CanDamage read
            // (each DefenderProfile's own raw stat, no hex bonus baked in) can apply the SAME
            // bonus every real defender fighting here would actually get (2026-08-20 fix, project
            // owner's own report — see WorthIt.CanDamage's own comment).
            public readonly float HexBonus;
            // Whichever source RequiredStrengthAt's own max() picked — the sighted army's own
            // ArmyData.Name, or the Hex Event guard's own GuardArmyName (see AiMapMemory.
            // GuardStrength's own comment). Null for an undefended hex, same as Defenders. Display
            // only — never read by any WorthIt comparison.
            public readonly string Name;

            // True exactly when NEITHER a physical army sighting NOR a Hex Event guard is known
            // at this hex at all (see RequiredStrengthAt) — a CONFIRMED zero, not "we don't
            // happen to have a per-unit roster memorized" (a real sighting/guard with Defenders
            // null but Defense/Attack > 0 is NOT this — see WinChanceAgainst's own aggregate-sum
            // fallback for that case). Matters because a hex with literally no known defender
            // never actually triggers a dice-based fight in this game at all — an undefended
            // building just changes hands for free the instant a mover arrives (see
            // BuildingRegistry.CaptureOrDestroyIfUndefended) — so WinChanceAgainst/IsReady treat
            // it as a certain win regardless of any residual HexBonus/Attack number this struct
            // still carries (a Base-tagged building's own passive Defense stat, folded into
            // Defense by RequiredStrengthAt for the DEFENDED case, would never actually get
            // rolled against with nobody there to fight for it). 2026-08-24 fix (project owner's
            // own report): the aggregate two-sided Monte Carlo below used to score a genuinely
            // empty raid-economy building target a flat 50/50 coin flip whenever the raiding
            // army's own AttackSum happened to be 0 (SimulateExchangeMargin ties every trial when
            // both sides roll zero dice), reading as "waits for reinforcement against nothing".
            public readonly bool IsUndefended;

            public ThreatStrength(float defense, float attack, IReadOnlyList<WorthIt.DefenderProfile> defenders, float hexBonus,
                string name = null, bool isUndefended = false)
            {
                Defense = defense;
                Attack = attack;
                Defenders = defenders;
                HexBonus = hexBonus;
                Name = name;
                IsUndefended = isUndefended;
            }
        }

        // Everything known to defend `hex` — see ThreatStrength's own comment for the "strongest
        // source, not the sum" rule. Defense includes the hex's own terrain/Base-building bonus
        // once on top (see WorthIt.HexDefenseBonus — the same bonus applies to whichever of the
        // two ends up actually fighting there for real); Attack never gets a hex bonus, same as a
        // real defender's Attack stat never does either. Defenders is likewise read off whichever
        // source won the max (never merged across both — see this struct's own comment). Zero-
        // Defense/zero-Attack/no Defenders (plus hex bonus) for a hex with nothing known guarding
        // it at all — a legitimate "undefended" answer for a raid-economy building target, not a
        // sentinel for "unknown" (callers only ever ask this for a hex a known-target enumeration
        // already produced).
        public static ThreatStrength RequiredStrengthAt(PlayerSetupData actor, HexCoord hex, HexMap map)
        {
            AiMapMemory.KnownEnemySighting? sighting = AiMapMemory.KnownEnemySightingAt(actor, hex);
            AiMapMemory.GuardStrength? guard = AiMapMemory.KnownEventGuardStrengthAt(actor, hex);
            float armyDefense = sighting.HasValue ? sighting.Value.DefenseSum : 0f;
            float armyAttack = sighting.HasValue ? sighting.Value.AttackSum : 0f;
            float eventDefense = guard.HasValue ? guard.Value.Defense : 0f;
            float eventAttack = guard.HasValue ? guard.Value.Attack : 0f;
            bool eventIsStronger = eventDefense > armyDefense;
            float hexBonus = WorthIt.HexDefenseBonus(hex, map);
            float defense = System.Math.Max(armyDefense, eventDefense) + hexBonus;
            float attack = eventIsStronger ? eventAttack : armyAttack;
            IReadOnlyList<WorthIt.DefenderProfile> defenders = eventIsStronger
                ? (guard?.Defenders)
                : (sighting?.Defenders);
            string name = eventIsStronger ? guard?.Name : sighting?.Name;
            bool isUndefended = !sighting.HasValue && !guard.HasValue;
            return new ThreatStrength(defense, attack, defenders, hexBonus, name, isUndefended);
        }

        // Trigger for the post-combat regroup tier (see AiAggressionPlanner.TryRaidRegroupCandidates)
        // — a standalone ≤50%-HP predicate now (2026-08-22, project owner's own call: "нужно
        // использовать worth it" — the old EffectiveAttackSum/EffectiveAttackerProfiles zero-Attack
        // override this used to share a threshold with is gone, see IsReady's own comment below for
        // why; this predicate no longer describes an existing readiness-math discount, it's purely
        // "should this force stop and heal" now).
        public static bool IsCriticallyWounded(ArmyData army) =>
            army != null && army.Members.Any(m => !m.IsHero && m.HitPointsCurrent <= m.HitPointsMax / 2);

        // IsUndefended short-circuits to a certain win (see ThreatStrength.IsUndefended's own
        // comment) rather than falling into the raw-float IsReady overload below — that overload
        // is also called directly (bypassing ThreatStrength entirely) by AiAggressionPlanner/
        // AiDefencePlanner's own nearby-threat reactions, which always describe a REAL sighted
        // enemy army and must never get this "confirmed empty" treatment.
        public static bool IsReady(ArmyData army, ThreatStrength threat) =>
            threat.IsUndefended || IsReady(army, threat.Defense, threat.Attack, threat.Defenders, threat.HexBonus);

        // Full-roster win chance when known, aggregate-sum fallback otherwise — extracted out of
        // IsReady below (2026-08-23, project owner's own call) so FindTarget's own ranking can read
        // the SAME honest win-chance number IsReady itself decides readiness against, rather than
        // ranking candidates by raw required.Defense (which says nothing about how strong `army` —
        // or the garrison it'll be assembled from — actually is right now).
        private static float WinChanceAgainst(ArmyData army, float threatDefense, float threatAttack,
            IReadOnlyCollection<WorthIt.DefenderProfile> defenders, float hexBonus)
        {
            return defenders != null && defenders.Count > 0
                ? WorthIt.WinChance(army, defenders, hexBonus)
                : WorthIt.WinChance(WorthIt.AttackSum(army), WorthIt.DefenseSum(army), threatAttack, threatDefense);
        }

        // Same IsUndefended short-circuit as IsReady above — ProximityScore/ScoreTarget (the only
        // other callers of this ThreatStrength overload) rank a confirmed-empty target as a sure
        // thing too, instead of the aggregate fallback's coin-flip reading it as a toss-up.
        private static float WinChanceAgainst(ArmyData army, ThreatStrength threat) =>
            threat.IsUndefended ? 1f : WinChanceAgainst(army, threat.Defense, threat.Attack, threat.Defenders, threat.HexBonus);

        // Routes through WorthIt.WinChance now (2026-08-22, project owner's own call: every army
        // comparison on the map goes through WorthIt, no second copy of the same math anywhere
        // else). Used to build its own per-unit snapshot here with a manual "wounded unit reads
        // zero Attack" override before handing it to WinChance — removed 2026-08-22 (project owner's
        // own follow-up call, "уже нет, нужно использовать worth it"): now that WinChance plays a
        // full round-by-round Monte Carlo battle against real per-unit HP (see WorthIt.cs's own
        // "Full round-by-round Monte Carlo" section), a wounded unit already contributes less on its
        // own — it dies (and stops attacking) earlier in a simulated fight than a healthy one would,
        // the same way a real player's wounded unit would. Manually zeroing its Attack ahead of time
        // on top of that double-counted the same penalty and could make IsReady read a wounded-but-
        // still-useful army as hopeless when the real sim would actually favor it. `army`'s own live
        // roster is never behind fog of war, so this just calls the plain ArmyData overload of
        // WinChance (WorthIt.cs — builds its snapshot via FromLiveUnit, real Attack AND real
        // HitPointsCurrent, no discount). Falls back to the aggregate-sum path only when `defenders`
        // carries no real per-unit roster at all (see WorthIt.MeetsWinChance's own comment for why
        // that's now the rare case, not the default one). Defense side and CanDamageAll are
        // untouched. `hexBonus` — see WorthIt.CanDamage's own comment; defaults to 0f for a caller
        // with no hex to check against (source-compatible with every call site before this
        // parameter existed).
        public static bool IsReady(ArmyData army, float threatDefense, float threatAttack,
            IReadOnlyCollection<WorthIt.DefenderProfile> defenders, float hexBonus = 0f)
        {
            float chance = WinChanceAgainst(army, threatDefense, threatAttack, defenders, hexBonus);
            return chance > 0.5f && WorthIt.CanDamageAll(army, defenders, hexBonus);
        }

        // Whether `hex` is still a legitimate target at all — a known neutral sighting, a known
        // event guard, or a still-enemy-owned building sits there. False means the objective
        // already resolved somehow (someone else cleared it, memory got corrected, ownership
        // changed) — AiTurnController.TryContinueRaidTask's own "already done, nothing to chase
        // any more" check, distinct from "still a target but we're not strong enough for it"
        // (that's IsReady's own job).
        public static bool IsStillValidTarget(PlayerSetupData actor, HexCoord hex)
        {
            if (AiMapMemory.KnownEnemySightingAt(actor, hex).HasValue)
                return true;
            if (AiMapMemory.KnownEventGuardDefenseAt(actor, hex).HasValue)
                return true;
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.Owner != null && building.Owner != actor && !building.Owner.IsNeutral;
        }

        // Best known neutral/event/(temporary) enemy-building hex — proximity to `army` plus
        // citadel-distance penalty (visitRingBand/freshNeighborWeight are NOT reused here — no
        // wavefront restriction at all, see this class's own "Цель" comment: the whole known map
        // is fair game, not a frontier band), PLUS (2026-08-23, project owner's own call) how good
        // a fight this actually is for `army` right now (ProximityScore's own WinChanceAgainst
        // term) — raw required.Defense alone said nothing about how strong `army` (or the garrison
        // it'll be assembled from) actually is, so an expensive-but-close target used to outrank a
        // cheap-but-slightly-farther one purely on distance. Never FILTERS by whether `army` could
        // currently win, though — that's still IsReady's own job (a low win chance only lowers this
        // candidate's rank, it doesn't disqualify it outright; the best-known target still wins
        // ranking even if nothing beats it yet).
        //
        // `excludeHexes` — hexes an already-active Агрессия task (this player's own, whether still
        // assembling or already travelling) is targeting right now. AiTurnController.
        // TryRaidAssembleCandidates passes its own currently-registered tasks' TargetHex set so a
        // second idle army never gets handed the SAME target a first one is already committed to
        // (the project owner's own report: two separate non-hero-led armies both raiding one
        // neutral). FindReadyIdleArmy deliberately still allows a lone strong-enough army to raid
        // without a hero — this only stops that army from getting DUPLICATED onto an already-
        // claimed target, not from existing at all.
        public static RaidTarget? FindTarget(PlayerSetupData actor, ArmyData army, HexMap map, IReadOnlyCollection<HexCoord> excludeHexes = null)
        {
            if (actor == null || army == null || map == null)
                return null;

            HexCoord? citadelHex = actor.CitadelHexQ.HasValue && actor.CitadelHexR.HasValue
                ? new HexCoord(actor.CitadelHexQ.Value, actor.CitadelHexR.Value)
                : (HexCoord?)null;

            RaidTarget? best = null;

            var neutralOrEventHexes = new HashSet<HexCoord>();
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownNeutralSightings(actor))
                neutralOrEventHexes.Add(sighting.Hex);
            foreach (HexCoord hex in AiMapMemory.KnownEventGuardHexes(actor))
                neutralOrEventHexes.Add(hex);

            foreach (HexCoord candidate in neutralOrEventHexes)
            {
                if (excludeHexes != null && excludeHexes.Contains(candidate))
                    continue;
                ThreatStrength required = RequiredStrengthAt(actor, candidate, map);
                float score = ProximityScore(army, candidate, citadelHex, required);
                if (best == null || score > best.Value.Score)
                    best = new RaidTarget(candidate, required,
                        score, $"known target at ({candidate.Q},{candidate.R}), needs strength {required.Defense:0}");
            }

            // Раздел 5 — временный "рейд экономики", см. этого класса собственный class comment.
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner == null || building.Owner == actor || building.Owner.IsNeutral || building.IsStartingCitadel)
                    continue;
                if (excludeHexes != null && excludeHexes.Contains(building.Hex))
                    continue;
                if (!VisionSystem.IsVisited(actor, building.Hex))
                    continue; // "known" — тот же принцип видимости с памятью, что и всюду

                ThreatStrength required = RequiredStrengthAt(actor, building.Hex, map);
                // No dedicated bonus any more (raidBuildingUndefendedBonus/GuardedWeakerBonus
                // removed 2026-08-19, project owner's own call) — scored on ProximityScore alone,
                // same as any neutral/event target; raidCounterAttackBonus already covers "attack a
                // known target we're stronger than" when one sits near the raiding army itself.
                bool guarded = AiMapMemory.KnownEnemySightingAt(actor, building.Hex).HasValue;
                float score = ProximityScore(army, building.Hex, citadelHex, required);

                if (best == null || score > best.Value.Score)
                    best = new RaidTarget(building.Hex, required, score,
                        guarded ? $"enemy building at ({building.Hex.Q},{building.Hex.R}), guard is weaker"
                                : $"enemy building at ({building.Hex.Q},{building.Hex.R}), unguarded");
            }

            return best;
        }

        // "Больше рейдить нечего" — same coarse, reachability/strength-blind existence check
        // BuildFacilityTask.HasAnythingToBuild uses for Экономика's own return-home fallback (see
        // its own comment): does ANY known neutral/event/temporarily-enemy-owned-building target
        // still exist anywhere on the map at all, regardless of whether any ONE particular idle
        // army could currently reach or beat it. Same three sources FindTarget itself scans, just
        // stopping at the first hit instead of scoring every candidate — AiTurnController.
        // TryRaidReturnHomeCandidates is the only reader (the project owner's own report,
        // 2026-08-16: a raid army that won its fight and had nothing left to chase just sat there
        // forever instead of walking home).
        public static bool HasAnythingToRaid(PlayerSetupData actor)
        {
            if (actor == null)
                return false;
            if (AiMapMemory.AllKnownNeutralSightings(actor).Any())
                return true;
            if (AiMapMemory.KnownEventGuardHexes(actor).Any())
                return true;
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner == null || building.Owner == actor || building.Owner.IsNeutral || building.IsStartingCitadel)
                    continue;
                if (VisionSystem.IsVisited(actor, building.Hex))
                    return true;
            }
            return false;
        }

        // Условия "+" к скору — ближе к текущей позиции армии (scoutProximityWeight, тот же
        // общий вес, что и у Разведки); выше win chance против `threat` (raidWinChanceRankWeight,
        // см. WinChanceAgainst выше — 2026-08-23, project owner's own call: цель, которую `army`
        // прямо сейчас может уверенно взять, должна перевешивать чуть более близкую, но требующую
        // долгой сборки/недостижимую вообще).
        // Условия "-" к скору — дальше от цитадели свыше citadelPenaltyFreeRadius хексов
        // (citadelDistancePenaltyPerHex — та же кросс-категорийная формула, что и у
        // BuildFacilityTask.ScoreHex, project owner's own 2026-08-19 rebalance: "влияет на все
        // задачи 1 уровня").
        private static float ProximityScore(ArmyData army, HexCoord candidate, HexCoord? citadelHex, ThreatStrength threat)
        {
            float score = -HexGridMath.Distance(army.Hex, candidate) * AiConfig.scoutProximityWeight;
            if (citadelHex.HasValue)
            {
                int distance = HexGridMath.Distance(citadelHex.Value, candidate);
                int overage = System.Math.Max(0, distance - AiConfig.citadelPenaltyFreeRadius);
                score -= overage * AiConfig.citadelDistancePenaltyPerHex;
            }
            score += WinChanceAgainst(army, threat) * AiConfig.raidWinChanceRankWeight;
            return score;
        }

        // Same ProximityScore FindTarget's own scan already computes, exposed for a caller that
        // needs to score ONE specific already-known hex (2026-08-24, project owner's own report —
        // AiAggressionPlanner.TryRaidAssembleCandidates' own retarget hysteresis needs the CURRENT
        // TargetHex scored the same honest way a candidate replacing it gets scored, rather than
        // switching on any marginal edge at all) without a second copy of the formula. `threat` —
        // the caller already has this from its own RequiredStrengthAt read, no need to fetch it
        // again here.
        public static float ScoreTarget(PlayerSetupData actor, ArmyData army, HexCoord hex, ThreatStrength threat)
        {
            HexCoord? citadelHex = actor.CitadelHexQ.HasValue && actor.CitadelHexR.HasValue
                ? new HexCoord(actor.CitadelHexQ.Value, actor.CitadelHexR.Value)
                : (HexCoord?)null;
            return ProximityScore(army, hex, citadelHex, threat);
        }

        // A whole existing idle army (doesn't need to be hero-led) that already clears IsReady's
        // own worth-it read against `threat`. Strongest-first so a smaller army is left free for
        // other work when a bigger one would already do. No separate HP filter here — IsReady's own
        // real-HP full-battle simulation already discounts a critically wounded army on its own
        // (see IsReady's own comment: a wounded unit dies, and stops attacking, earlier in the sim
        // than a healthy one would), no need for a second, cruder gate on top of it.
        // A lone Recce scout or an escort-less lone hero are excluded even when IsReady would
        // otherwise pass (2026-08-21, project owner's own report — a solo scout was picking fights
        // with neutrals): both compositions exist specifically to stay out of combat (see
        // AiArmyRoles.IsSoloRecce/IsSoloHeroAwaitingEscort's own comments), so a weak-enough target
        // clearing the raw IsReady math must never actually commit them to a fight. Shared by
        // AiDefencePlanner too (same method) — a fragile solo shouldn't get drafted as a defender
        // either.
        public static ArmyData FindReadyIdleArmy(PlayerSetupData player, ThreatStrength threat, AiResourcePool pool)
        {
            return pool.AvailableArmies()
                .Where(a => !a.IsGarrison && !a.IsPrison && a.Members.Count > 0
                    && !AiArmyRoles.IsSoloRecce(a) && !AiArmyRoles.IsSoloHeroAwaitingEscort(a)
                    && IsReady(a, threat))
                .OrderByDescending(a => WorthIt.AttackSum(a))
                .FirstOrDefault();
        }

        public static bool NeedsHero(ArmyData army) => !AiArmyRoles.IsHeroLed(army);

        // Opportunistic pre-attack top-up (2026-08-20 fix, project owner's own report: the AI used
        // to send two separate combat armies sitting on the exact same hex to attack one after
        // another instead of combining them first — a hero-led raid/defense force that's already
        // strong enough alone (FindReadyIdleArmy) never checked whether a co-located sibling army
        // could just be folded in for a stronger single strike). Garrison itself is deliberately
        // excluded — this only cannibalizes an otherwise-idle FIELD army bystander, never dips into
        // home-defense stock the way the real assembly pipeline (FindRecruitAt) is allowed to for a
        // composition that still actually NEEDS the help. One unit per call, same "one recruit per
        // step, re-evaluate fresh" shape every other assembly/consolidation move in this codebase
        // already follows (AssembleRaidForce/ConsolidateUnits) — see AiTurnController.Decide's own
        // class comment on why nothing here ever moves more than one member at once.
        public static UnitData FindCoLocatedMergeRecruit(ArmyData readyArmy, AiResourcePool pool, out ArmyData source)
        {
            source = null;
            foreach (ArmyData candidate in pool.AvailableArmies())
            {
                if (candidate == readyArmy || candidate.IsGarrison || candidate.IsPrison || !candidate.Hex.Equals(readyArmy.Hex))
                    continue;
                UnitData unit = candidate.Members.FirstOrDefault(m => !m.IsHero && !m.HasAbility(UnitAbilities.Recce));
                if (unit != null)
                {
                    source = candidate;
                    return unit;
                }
            }
            return null;
        }

        // Next recruit sitting at `hex` (garrison or any other idle army already parked there)
        // not yet part of `army` itself — hero first if `army` still needs one (see NeedsHero),
        // otherwise the strongest available non-hero unit, same "converge fastest" reasoning
        // FindReadyIdleArmy's own ordering uses. `source` is which army it's currently sitting in
        // (needed by the caller to actually issue the same-hex ArmyActions.TransferMember).
        //
        // HP-aware since 2026-08-23 (project owner's own report) — a healthy recruit is always
        // preferred over a critically wounded one (IsCriticallyWounded's own <=50%HP threshold),
        // even when the wounded one has higher Attack: without this, a half-dead unit that
        // happened to be the strongest by raw stat got pulled into a brand-new raid army ahead of
        // AiManagementPlanner's own Repair task ever getting to it (repairUnitBaseWeight sits
        // below both raidAssembleBonus and a plain raid move on purpose — see repairUnitBaseWeight's
        // own comment — so Менеджмент alone can never outrace this pick). A wounded unit is only
        // ever offered here as a last resort, when nothing healthy is available anywhere at `hex`
        // — still better than leaving `army` short a body outright, and IsReady's own real-HP
        // full-battle simulation continues to discount it fairly once it's actually in the roster
        // (see IsReady's own comment).
        //
        // Recce-tagged units are never offered as recruits — this exclusion is this method's own,
        // independent of GarrisonReorgTask (which dropped its OWN Recce carve-out 2026-08-20, see
        // that class's own class comment point 1.2 — a solo Recce can now get folded into garrison
        // by consolidation the same as any other lone army). Without THIS exclusion, a Recce unit
        // sitting solo at the garrison hex (exactly what AiScoutPlanner keeps it there for) reads
        // as ordinary raid fodder here, and AiScoutPlanner.AssembleRecceScoutRoutine pulls it right
        // back out the moment it lands buried in a raid army — an endless recruit/reclaim ping-pong
        // between Агрессия and Разведка that burned a whole turn's step budget without either task
        // ever finishing (see AiDebug.log 2026-08-17, turn 8).
        public static UnitData FindRecruitAt(PlayerSetupData player, HexCoord hex, ArmyData army, AiResourcePool pool, out ArmyData source)
        {
            UnitData best = FindRecruitAt(player, hex, army, pool, allowCriticallyWounded: false, out source);
            return best ?? FindRecruitAt(player, hex, army, pool, allowCriticallyWounded: true, out source);
        }

        private static UnitData FindRecruitAt(PlayerSetupData player, HexCoord hex, ArmyData army, AiResourcePool pool,
            bool allowCriticallyWounded, out ArmyData source)
        {
            source = null;
            bool wantHero = NeedsHero(army);

            UnitData best = null;
            float bestAttack = -1f;
            foreach (ArmyData candidate in pool.AvailableArmies())
            {
                if (candidate == army || candidate.IsPrison || !candidate.Hex.Equals(hex))
                    continue;
                foreach (UnitData unit in candidate.Members)
                {
                    if (unit.IsHero != wantHero || unit.HasAbility(UnitAbilities.Recce))
                        continue;
                    if (!wantHero && !allowCriticallyWounded && unit.HitPointsCurrent <= unit.HitPointsMax / 2)
                        continue;
                    // Would pulling `unit` out leave `candidate` (often the Garrison) unable to
                    // hold its own remaining roster? A hero can be the only thing propping the
                    // Garrison's capacity above its base — see ArmyData.CanLeaveWithoutOvercrowding's
                    // own comment. Checked here, not just at execution time, so this method never
                    // proposes a recruit ArmyActions.TransferMember is guaranteed to reject —
                    // without this, TryRaidAssembleCandidates re-offers the exact same doomed
                    // recruit every step for the rest of the turn, since nothing about the roster
                    // changes between retries (project owner's own report, 2026-08-22).
                    if (!candidate.CanLeaveWithoutOvercrowding(unit))
                        continue;
                    if (wantHero || unit.Attack > bestAttack)
                    {
                        best = unit;
                        bestAttack = unit.Attack;
                        source = candidate;
                        if (wantHero)
                            return best; // first hero found — never a choice between several
                    }
                }
            }
            return best;
        }

        // TryRaidRegroupCandidates' own courier pick (and TryContinueRaidTask's in-flight
        // reinforcement branch) — always non-hero (a hero has no business being spent as a
        // logistics run to fetch a wounded army home), otherwise the same "any idle army sitting
        // at `hex`" scan FindRecruitAt uses, just without the wantHero branch. Same Recce exclusion
        // as FindRecruitAt above and for the same reason — ReinforceSwapRoutine folds this pick
        // permanently into the wounded army, which would strip a scout composition exactly as
        // durably as recruiting it into a raid force would.
        //
        // `preferTypeMatchFor` — the wounded army this recruit is headed to replace someone in, if
        // known (both current callers pass their own `army`). When given, a candidate whose
        // TypeTags overlaps one of that army's own non-hero members' wins immediately over a
        // same-hex candidate found earlier that doesn't (same "does this unit's own type fit the
        // gap" read AiManagementPlanner.UnitCompositionFitBonus already uses for card placement,
        // just applied to a courier pick instead) — a Ranged replacement for a fallen Ranged unit,
        // not whichever body happened to be sitting there first. Falls back to the first available
        // candidate regardless of type if nothing matches (or `preferTypeMatchFor` is null) — same
        // behavior as before this parameter existed.
        public static UnitData FindNonHeroRecruitAt(HexCoord hex, AiResourcePool pool, ArmyData excludeArmy,
            out ArmyData source, ArmyData preferTypeMatchFor = null)
        {
            HashSet<UnitTypeTag> preferredTypes = preferTypeMatchFor != null
                ? new HashSet<UnitTypeTag>(preferTypeMatchFor.Members.Where(m => !m.IsHero).SelectMany(m => m.TypeTags))
                : null;

            UnitData fallback = null;
            ArmyData fallbackSource = null;
            foreach (ArmyData candidate in pool.AvailableArmies())
            {
                if (candidate == excludeArmy || candidate.IsPrison || !candidate.Hex.Equals(hex))
                    continue;
                foreach (UnitData unit in candidate.Members)
                {
                    if (unit.IsHero || unit.HasAbility(UnitAbilities.Recce))
                        continue;
                    if (preferredTypes != null && preferredTypes.Count > 0 && unit.TypeTags.Overlaps(preferredTypes))
                    {
                        source = candidate;
                        return unit;
                    }
                    if (fallback == null)
                    {
                        fallback = unit;
                        fallbackSource = candidate;
                    }
                }
            }
            source = fallbackSource;
            return fallback;
        }

        // Feature 3 (2026-08-24, project owner's own report) — the opportunity-capture mechanism
        // itself already exists (see FindTarget's own Section 5 "unguarded"/"guard is weaker" case
        // above) and already gets an army moving toward such a target as its own real TargetHex once
        // it wins FindTarget's ranking outright. The gap this method closes is narrower: an army
        // ALREADY travelling toward some OTHER destination (its own real raid target, or simply
        // walking home) that happens to pass close by a DIFFERENT known unguarded/beatable enemy
        // building doesn't currently deviate for it at all — FindTarget only ever gets consulted
        // when picking a brand-new target, never mid-route.
        //
        // Purely an internal "which hex does THIS STEP's move actually aim at" nudge (same "never
        // leaks into AiDecision.Score" scoping ProximityScore/WinChanceAgainst already keep to
        // themselves in this class) — the task's own real TargetHex/HomeHex is left completely
        // untouched; only the destination THIS ONE MoveArmy decision passes to IssueMoveOrder
        // changes, so a later step's fresh re-evaluation can freely pick a different detour (or none
        // at all) without the task ever having "forgotten" its actual goal.
        //
        // A candidate building qualifies the same way FindTarget's own Section 5 does — actually
        // enemy-owned, not neutral, not the starting citadel, and "known" via the same visited-hex
        // memory (VisionSystem.IsVisited) — AND either confirmed undefended (ThreatStrength.
        // IsUndefended) or already beatable by `army` right now (IsReady, the same readiness math
        // every other real engagement decision in this class already trusts). Only a detour that
        // doesn't add more than AiConfig.captureStepDetourTolerance extra hexes over the direct
        // route wins; otherwise the real destination stays this step's own target, unchanged — and
        // the building sitting exactly ON `realDestination` itself is skipped (nothing to "detour"
        // toward, ordinary continuation already covers it).
        internal static HexCoord? FindCaptureStepDestination(PlayerSetupData actor, ArmyData army, HexCoord realDestination, HexMap map)
        {
            if (actor == null || army == null || map == null || realDestination.Equals(army.Hex))
                return null;

            int direct = HexGridMath.Distance(army.Hex, realDestination);
            HexCoord? best = null;
            int bestExtra = int.MaxValue;

            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner == null || building.Owner == actor || building.Owner.IsNeutral || building.IsStartingCitadel)
                    continue;
                if (building.Hex.Equals(realDestination))
                    continue;
                if (!VisionSystem.IsVisited(actor, building.Hex))
                    continue;

                ThreatStrength required = RequiredStrengthAt(actor, building.Hex, map);
                if (!required.IsUndefended && !IsReady(army, required))
                    continue; // not a confirmed-safe or currently-winnable capture — not worth deviating for

                int viaDetour = HexGridMath.Distance(army.Hex, building.Hex) + HexGridMath.Distance(building.Hex, realDestination);
                int extra = viaDetour - direct;
                if (extra < 0 || extra > AiConfig.captureStepDetourTolerance || extra >= bestExtra)
                    continue;
                bestExtra = extra;
                best = building.Hex;
            }
            return best;
        }

        // Known non-neutral army within raidThreatRadius of `hex` — the threat-reaction trigger
        // (see this class's own "Поведение" comment). Neutrals never trigger this (they're this
        // task's own PREY, not a threat to react to).
        //
        // Returns the STRONGEST such sighting (by DefenseSum — the same axis RequiredStrengthAt
        // itself ranks by), not just the first one found (2026-08-20 fix, project owner's own
        // report). With two enemy armies both within radius, the old first-found behavior could
        // read IsReady against the weaker one, decide "we beat it", and counter-attack — while a
        // second, stronger army sat right there unaccounted for the whole time. Every caller
        // (TryContinueRaidTask's own counter-attack/retreat branch, AiDefencePlanner's citadel
        // threat detection) already treats "beats NearbyThreat" as "safe to engage nearby", so
        // making this the worst case in range makes that read honest.
        public static AiMapMemory.KnownEnemySighting? NearbyThreat(PlayerSetupData player, HexCoord hex)
        {
            AiMapMemory.KnownEnemySighting? strongest = null;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { hex }, AiConfig.raidThreatRadius))
            {
                if (sighting.Owner == null || sighting.Owner.IsNeutral)
                    continue;
                if (!strongest.HasValue || sighting.DefenseSum > strongest.Value.DefenseSum)
                    strongest = sighting;
            }
            return strongest;
        }
    }
}
