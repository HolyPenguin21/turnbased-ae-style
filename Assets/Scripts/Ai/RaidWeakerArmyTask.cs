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
    //      по одному из гарнизона и из любых других простаивающих армий на карте (см.
    //      MaxPossibleAttack — простаивающие армии НЕ на хексе гарнизона сначала должны туда
    //      дойти, это отдельный "recall"-кандидат оркестратора, не эта задача).
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
    //   - "Больше некого бить" — если для выбранной цели даже теоретический потолок силы
    //     (MaxPossibleAttack) её не берёт, это тупик: задача отменяется, армия идёт домой
    //     переукомплектовываться (тот же путь, что и при угрозе сильнее нас).
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

            public ThreatStrength(float defense, float attack, IReadOnlyList<WorthIt.DefenderProfile> defenders)
            {
                Defense = defense;
                Attack = attack;
                Defenders = defenders;
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
            float defense = System.Math.Max(armyDefense, eventDefense) + WorthIt.HexDefenseBonus(hex, map);
            float attack = eventIsStronger ? eventAttack : armyAttack;
            IReadOnlyList<WorthIt.DefenderProfile> defenders = eventIsStronger
                ? (guard?.Defenders)
                : (sighting?.Defenders);
            return new ThreatStrength(defense, attack, defenders);
        }

        // Half-HP-or-worse non-hero members contribute zero Attack to this task's own worth-it
        // read — the project owner's own call: a unit that wounded won't survive to land a hit is
        // dead weight for THIS purpose specifically, even though it's still a real, recruitable
        // army member everywhere else (FindReadyIdleArmy/FindRecruitAt don't filter by HP at all
        // any more — see their own comments). WorthIt.AttackSum itself stays untouched; this
        // discount is scoped to Агрессия's own readiness math only, not the shared class other
        // planners/estimates also read.
        private static float EffectiveAttackSum(ArmyData army) =>
            army?.Members.Where(m => !m.IsHero && m.HitPointsCurrent > m.HitPointsMax / 2).Sum(m => m.Attack) ?? 0f;

        // Trigger for the post-combat regroup tier (see AiAggressionPlanner.TryRaidRegroupCandidates)
        // — same ≤50% threshold EffectiveAttackSum discounts to zero, kept as one shared predicate
        // so the two can never drift apart.
        public static bool IsCriticallyWounded(ArmyData army) =>
            army != null && army.Members.Any(m => !m.IsHero && m.HitPointsCurrent <= m.HitPointsMax / 2);

        public static bool IsReady(ArmyData army, ThreatStrength threat) =>
            IsReady(army, threat.Defense, threat.Attack, threat.Defenders);

        // Own copy of WorthIt.Score/IsWorthIt's two-sided net-edge formula, substituting
        // EffectiveAttackSum for WorthIt.AttackSum on the attacker side — see that method's own
        // comment for why this isn't just a call into WorthIt.IsWorthIt any more. Defense side and
        // CanDamageAll are untouched (only Attack contribution is what "won't survive to swing"
        // discounts).
        public static bool IsReady(ArmyData army, float threatDefense, float threatAttack,
            IReadOnlyCollection<WorthIt.DefenderProfile> defenders)
        {
            float ourEdge = EffectiveAttackSum(army) - threatDefense;
            float theirEdge = threatAttack - WorthIt.DefenseSum(army);
            return ourEdge - theirEdge > 0f && WorthIt.CanDamageAll(army, defenders);
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
        // is fair game, not a frontier band). Never filters by whether `army` could currently WIN
        // — that's the caller's own job (IsReady/MaxPossibleAttack decide whether to pursue or
        // treat as a dead end), this only ranks candidates by proximity/opportunity.
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
                float score = ProximityScore(army, candidate, citadelHex);
                if (best == null || score > best.Value.Score)
                    best = new RaidTarget(candidate, required,
                        score, $"известная цель на ({candidate.Q},{candidate.R}), нужна сила {required.Defense:0}");
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
                bool guarded = AiMapMemory.KnownEnemySightingAt(actor, building.Hex).HasValue;
                float bonus = guarded ? AiConfig.Current.raidBuildingGuardedWeakerBonus : AiConfig.Current.raidBuildingUndefendedBonus;
                float score = ProximityScore(army, building.Hex, citadelHex) + bonus;

                if (best == null || score > best.Value.Score)
                    best = new RaidTarget(building.Hex, required, score,
                        guarded ? $"вражеская постройка на ({building.Hex.Q},{building.Hex.R}), охрана слабее"
                                : $"вражеская постройка на ({building.Hex.Q},{building.Hex.R}) без охраны");
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
        // общий вес, что и у Разведки).
        // Условия "-" к скору — дальше от цитадели (citadelDistancePenalty, тот же общий вес).
        private static float ProximityScore(ArmyData army, HexCoord candidate, HexCoord? citadelHex)
        {
            float score = -HexGridMath.Distance(army.Hex, candidate) * AiConfig.Current.scoutProximityWeight;
            if (citadelHex.HasValue)
                score -= HexGridMath.Distance(citadelHex.Value, candidate) * AiConfig.Current.citadelDistancePenalty;
            return score;
        }

        // NOT the same formula FindTarget itself scores candidates with, despite sharing
        // ProximityScore's own mover-distance term — AiTurnController.TryContinueRaidTask's own
        // re-score each continuation, for ONE already-committed hex, deliberately drops the
        // citadel-distance penalty FindTarget still applies (passing citadelHex: null below). That
        // penalty exists to prefer a nearer-to-home target when CHOOSING among several candidates;
        // once a raid is already underway toward task.TargetHex, applying it again every single
        // step only starves exactly the campaign that most needs its own AP to finish covering
        // that distance — see AiTurnController's own AiConfig.raidCommittedBonus comment for the
        // full report (a committed raid army losing arbitration to routine Разведка movement,
        // AiDebug.log 2026-08-16).
        public static float ScoreForContinuation(PlayerSetupData actor, ArmyData army, HexCoord hex) =>
            ProximityScore(army, hex, citadelHex: null);

        // Условие "+" к скору (raidReadyArmyBonus, applied by the caller) — a whole existing idle
        // army (ANY composition, doesn't need to be hero-led) that already clears IsReady's own
        // ≤50%-HP-discounted worth-it read against `threat`. Strongest-first so a smaller army is
        // left free for other work when a bigger one would already do. No separate HP filter here
        // any more — IsReady's own EffectiveAttackSum discount already keeps a critically wounded
        // army from clearing a real target on its own; a lightly wounded one (>50% HP) is fully
        // eligible, same as a healthy one.
        public static ArmyData FindReadyIdleArmy(PlayerSetupData player, ThreatStrength threat, AiResourcePool pool)
        {
            return pool.AvailableArmies()
                .Where(a => !a.IsGarrison && !a.IsPrison && a.Members.Count > 0 && IsReady(a, threat))
                .OrderByDescending(a => WorthIt.AttackSum(a))
                .FirstOrDefault();
        }

        public static bool NeedsHero(ArmyData army) => !AiArmyRoles.IsHeroLed(army);

        // Theoretical ceiling if EVERY currently-idle non-hero unit the player owns (garrison
        // included) eventually joined `army` — the "больше некого бить" dead-end gate (see this
        // class's own "Поведение" comment). Deliberately rough: ignores Capacity, doesn't check
        // whether merging that many units is even reachable in practice — good enough to tell
        // "truly hopeless" apart from "just needs more turns of assembling."
        public static float MaxPossibleAttack(PlayerSetupData player, ArmyData excludeArmy, AiResourcePool pool)
        {
            float total = WorthIt.AttackSum(excludeArmy);
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army == excludeArmy || army.IsPrison)
                    continue;
                total += army.Members.Where(m => !m.IsHero).Sum(m => m.Attack);
            }
            return total;
        }

        // Next recruit sitting at `hex` (garrison or any other idle army already parked there)
        // not yet part of `army` itself — hero first if `army` still needs one (see NeedsHero),
        // otherwise the strongest available non-hero unit, same "converge fastest" reasoning
        // FindReadyIdleArmy's own ordering uses. `source` is which army it's currently sitting in
        // (needed by the caller to actually issue the same-hex ArmyActions.TransferMember). No HP
        // filter — a lightly wounded unit (>50% HP) still fights fine; IsReady's own
        // EffectiveAttackSum discount is what keeps a critically wounded composition from reading
        // as ready, not a recruiting-time ban.
        //
        // Recce-tagged units are never offered as recruits — every other planner keeps a Recce
        // member solo forever on purpose (see AiScoutPlanner.FindBuriedRecceUnit's own comment,
        // GarrisonReorgTask.IsLoneArmyAtBase's own !HasRecce exclusion). Without this exclusion, a
        // Recce unit sitting solo at the garrison hex (exactly what AiScoutPlanner keeps it there
        // for) reads as ordinary raid fodder here, and AiScoutPlanner.AssembleRecceScoutRoutine
        // pulls it right back out the moment it lands buried in a raid army — an endless
        // recruit/reclaim ping-pong between Агрессия and Разведка that burned a whole turn's step
        // budget without either task ever finishing (see AiDebug.log 2026-08-17, turn 8).
        public static UnitData FindRecruitAt(PlayerSetupData player, HexCoord hex, ArmyData army, AiResourcePool pool, out ArmyData source)
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

        // TryRaidRegroupCandidates' own courier pick — always non-hero (a hero has no business
        // being spent as a logistics run to fetch a wounded army home), otherwise the same "any
        // idle army sitting at `hex`" scan FindRecruitAt uses, just without the wantHero branch.
        // Same Recce exclusion as FindRecruitAt above and for the same reason — ReinforceSwapRoutine
        // folds this pick permanently into the wounded army, which would strip a scout composition
        // exactly as durably as recruiting it into a raid force would.
        public static UnitData FindNonHeroRecruitAt(HexCoord hex, AiResourcePool pool, ArmyData excludeArmy)
        {
            foreach (ArmyData candidate in pool.AvailableArmies())
            {
                if (candidate == excludeArmy || candidate.IsPrison || !candidate.Hex.Equals(hex))
                    continue;
                UnitData unit = candidate.Members.FirstOrDefault(m => !m.IsHero && !m.HasAbility(UnitAbilities.Recce));
                if (unit != null)
                    return unit;
            }
            return null;
        }

        // Known non-neutral army within raidThreatRadius of `hex` — the threat-reaction trigger
        // (see this class's own "Поведение" comment). Neutrals never trigger this (they're this
        // task's own PREY, not a threat to react to).
        public static AiMapMemory.KnownEnemySighting? NearbyThreat(PlayerSetupData player, HexCoord hex)
        {
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { hex }, AiConfig.Current.raidThreatRadius))
                if (sighting.Owner != null && !sighting.Owner.IsNeutral)
                    return sighting;
            return null;
        }
    }
}
