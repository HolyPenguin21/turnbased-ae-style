using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // Level-1 category orchestration for Агрессия (AiTaskCategory.Aggression) — the
    // continue/start/recall/return-home tiers AiTurnController.Decide gathers candidates from
    // each step, same role AiScoutPlanner/AiEconomyPlanner/AiManagementPlanner already play for
    // their own categories, plus this category's own execution routines
    // (RequestRaidArmyRoutine/AssembleRaidForceRoutine — see AiTurnController's own class comment
    // on the execution split). Behavioral specifics (target selection, composition eligibility,
    // threat reaction) live one level down, on RaidWeakerArmyTask itself — see its own class
    // comment; this class only sequences calls into it and turns the results into AiDecision/
    // AiTask, the same split every other category already follows.
    //
    // Every non-hero recruit AssembleRaidForce pulls still comes from already-deployed units
    // (garrison stock or an idle army), never straight off a card — GarrisonReorgTask's own job
    // to have sorted plain units into the Garrison already. The one exception is the hero a
    // forming raid army still needs (see TryHeroCardForRaid): its own direct card-hand pipeline,
    // the way AiScoutPlanner already has for Recce (its own TryStartReconAssemblyCandidatesFor
    // placing/scoring a Recce card entirely on its own, never through AiManagementPlanner.
    // TryPlayCardCandidates — see that method's own comment) — scored as this category's own
    // candidate, never routed through Менеджмент's PlayCard, and no special-casing needed for the
    // two to compete — removing a card from hand.Hand already lowers TryPlayCardCandidates' own
    // backlog count for whoever else reads it next step (project owner's own 2026-08-19 note).
    public static class AiAggressionPlanner
    {
        // ---- Агрессия · Задача 1 (Зачистка нейтралов/эвентов) ----
        // Full redesign, 2026 — see RaidWeakerArmyTask's own class comment for what changed and
        // why (target/composition/threat reaction all live there now). Replaces AiAggressionPlanner
        // (the old, pre-redesign class of the same name) entirely.

        // Advances an ALREADY-committed task — threat reaction first (own copy, unlike Разведка's
        // one-turn flee this is a ONE-WAY retreat, see AiTask.Retreating's own comment), then
        // "is the target still even real" (AiTaskRegistry stays honest the moment memory says the
        // objective is gone, no forced march home needed for that case), then the actual
        // travel/attack move. Never handles an UNREADY (still-assembling) task — that's
        // TryRaidAssembleCandidates' own job; this returns null for one so the assemble tier gets
        // a clear turn instead of silently competing with it.
        public static AiDecision TryContinueRaidTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task,
            AiResourcePool pool)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            // Two distinct "home" concepts now (2026-08-21, project owner's own call — "пусть
            // утекает в новую базу, она тоже даёт бонус к защите, а армии защиты могут
            // подтянуться из цитадели"): citadelHex is ONLY for the "still assembling" gate below
            // (recruiting stays citadel-scoped this phase — TryRaidAssembleCandidates/
            // TryRaidRecallCandidates are unchanged) and for a siege-triggered recall specifically
            // (see retreatDestination below — a siege response must mass at the CITADEL, not
            // wherever's merely nearest). homeHex is everywhere else "home" means "safety/support
            // for THIS army right now" — ordinary local retreat, regroup, and mid-travel
            // reinforcement all target whichever of the player's own garrisoned hexes is nearest to
            // the army's CURRENT position, recomputed fresh every call like everything else here
            // (no stored anchor the way AiTask.HomeHex pins DefendCitadel's own patrol — a fleeing
            // raid should always head for whichever base is genuinely closest right now, not
            // whichever one it happened to start near).
            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            HexCoord homeHex = AiTurnController.NearestOwnGarrisonHex(player, task.Army.Hex);

            // Оборона's own alarm (see AiDefencePlanner.IsUnderSiege) forces every active raid home
            // — the project owner's own explicit scope: raid armies only, Экономика/Разведка already
            // flee on their own terms and are left alone. Recomputed fresh every call (not just the
            // turn Retreating first gets set) so a raid ALREADY retreating for its own local reason
            // still picks up the wider siege buffer the moment a siege separately starts — 2026-08-21
            // fix, own report: the two retreat triggers used to diverge (an already-retreating task
            // kept its own narrow single-hex avoidance for the rest of the siege, never upgrading to
            // the wide one), and this also drops the moment the siege itself lifts mid-retreat.
            bool underSiege = AiDefencePlanner.IsUnderSiege(player, ctx);
            if (!task.Retreating && underSiege)
            {
                task.Retreating = true;
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — citadel under siege, Aggression task recalled.");
            }

            if (task.Retreating)
            {
                // Siege-triggered retreat masses at the CITADEL specifically (that's the whole
                // point of recalling raids during a siege — reinforcing the citadel, not merely
                // reaching safety); an ordinary local retreat (outmatched by something nearby, not
                // a citadel siege) falls back to whichever own base is nearest instead. Re-read
                // fresh every call, same as avoidCenter/avoidRadius below — a retreat that started
                // for a local reason automatically upgrades to citadel-massing the moment a siege
                // separately starts mid-retreat.
                HexCoord retreatDestination = underSiege ? citadelHex : homeHex;
                if (task.Army.Hex.Equals(retreatDestination))
                {
                    AiTaskRegistry.Remove(player, task);
                    return null;
                }
                if (task.Army.CurrentMovement <= 0 || (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost)))
                    return null;

                // Never path back onto/through whatever we're fleeing FROM (2026-08-20 fix,
                // project owner's own report: a retreating army used to route straight back
                // through the very enemy it fled, attacking it mid-retreat). A plain MoveArmy
                // decision just hands MoveArmyRoutine a final destination and lets the engine's
                // own pathfinding pick the route — its own soft-avoidance (HexSelectionController.
                // Movement.AvoidEnemyHex) only steers around a CURRENTLY VISIBLE enemy, but
                // NearbyThreat is memory-based (AiMapMemory) and can still "see" a threat that's
                // fallen back out of vision by the time this step actually runs, so the engine has
                // no idea to avoid it. Compute just the next step ourselves instead, hard-blocking
                // whatever's actually relevant right now — the wider siege buffer while underSiege
                // (same primitive AiDefencePlanner's own Turtle march-home uses, see
                // AiTurnController.FindPathStepAvoidingZone), otherwise just this raid's own locally
                // known threat, single-hex, same as before.
                HexCoord? avoidCenter = underSiege ? AiDefencePlanner.SiegeThreatHex(player) : RaidWeakerArmyTask.NearbyThreat(player, task.Army.Hex)?.Hex;
                int avoidRadius = underSiege ? AiConfig.defenceRetreatAvoidRadius : 0;
                HexCoord? nextStep = AiTurnController.FindPathStepAvoidingZone(ctx.Map, task.Army, retreatDestination, avoidCenter, avoidRadius);
                if (nextStep == null)
                    return null; // boxed in avoiding the threat — wait rather than walk into it

                string reason = underSiege ? "citadel under siege — recalled to the garrison" : "regrouping — returns to base";
                float score = underSiege ? AiConfig.defencePreemptScore : AiConfig.aggressionBaseWeight;
                return AiDecision.Move(task.Army, nextStep.Value, reason, task, score, AiTaskCategory.Aggression);
            }

            AiMapMemory.KnownEnemySighting? threat = RaidWeakerArmyTask.NearbyThreat(player, task.Army.Hex);
            if (threat.HasValue)
            {
                float threatHexBonus = WorthIt.HexDefenseBonus(threat.Value.Hex, ctx.Map);
                float threatDefense = threat.Value.DefenseSum + threatHexBonus;
                if (task.Army.CurrentMovement <= 0 || (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost)))
                    return null;

                if (!RaidWeakerArmyTask.IsReady(task.Army, threatDefense, threat.Value.AttackSum, threat.Value.Defenders, threatHexBonus))
                {
                    // Already home and still outmatched — nothing to retreat TO, and defending the
                    // citadel is no longer this task's own job (see AiTask.cs's own AiTaskKind.
                    // DefendCitadel comment: split into its own Оборона category 2026-08-20,
                    // AiDefencePlanner detects this exact same threat independently). Just give up
                    // on this raid outright rather than the old move-to-self no-op that used to
                    // strand it (the project owner's own "Bastion Guard" report, 2026-08-17).
                    if (task.Army.Hex.Equals(homeHex))
                    {
                        AiTaskRegistry.Remove(player, task);
                        return null;
                    }
                    task.Retreating = true;
                    AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — a known enemy army is stronger, "
                        + "Aggression task switches to regrouping.");
                    // Same safe-step logic the top-of-method Retreating branch uses on every
                    // following turn — this is the FIRST retreat step, right next to the threat
                    // that triggered it, so it's the highest-risk moment to get this wrong. Not
                    // reachable while underSiege (that branch already returned above, via
                    // task.Retreating), so homeHex — not a citadel/homeHex split — is always right
                    // here.
                    HexCoord? firstStep = FindNextRetreatStep(ctx.Map, task.Army, homeHex, threat.Value.Hex);
                    if (firstStep == null)
                        return null;
                    return AiDecision.Move(task.Army, firstStep.Value, "enemy is stronger — retreating",
                        task, AiConfig.aggressionBaseWeight, AiTaskCategory.Aggression);
                }

                if (!AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, threat.Value.Hex))
                    return null;
                task.TargetHex = threat.Value.Hex;
                return AiDecision.Move(task.Army, threat.Value.Hex, "counter-attacks a known nearby army",
                    task, AiConfig.aggressionBaseWeight + AiConfig.raidCounterAttackBonus, AiTaskCategory.Aggression);
            }

            if (!RaidWeakerArmyTask.IsStillValidTarget(player, task.TargetHex))
            {
                // Objective already resolved (someone else got it, memory corrected) — done, no
                // need to force a march home for this one.
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            RaidWeakerArmyTask.ThreatStrength required = RaidWeakerArmyTask.RequiredStrengthAt(player, task.TargetHex, ctx.Map);
            if (!RaidWeakerArmyTask.IsReady(task.Army, required))
            {
                if (task.Army.Hex.Equals(citadelHex))
                    return null; // still assembling — TryRaidAssembleCandidates' own turn to act (citadel-scoped)

                // Got weaker mid-travel (combat losses against the neutral/event target itself —
                // NOT an enemy threat, that's the branch above) with no re-assembly support away
                // from the citadel. Same "is it cheaper to march home ourselves, or wait here for
                // a single courier" AP comparison TryRaidRegroupCandidates already uses for an
                // idle critically-wounded army (ApRoundTrip/ApOneWay/TravelTurns below) — applied
                // here too since a still-registered, mid-travel task never shows up in
                // pool.AvailableArmies() for that method to find on its own. Uses homeHex (nearest
                // own base), not citadelHex — 2026-08-21, project owner's own call: a courier can
                // just as well come from a closer forward base as from the citadel.
                UnitData recruit = RaidWeakerArmyTask.FindNonHeroRecruitAt(homeHex, pool, task.Army, out ArmyData recruitSource, task.Army);
                bool canDispatch = recruit != null && recruitSource != null && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost);
                bool goHome = !canDispatch;
                if (!goHome)
                {
                    int toHome = HexGridMath.Distance(task.Army.Hex, homeHex);
                    float goHomeApCost = ApRoundTrip(task.Army.ActivationApCost, task.Army.MaxMovement, toHome,
                        HexGridMath.Distance(homeHex, task.TargetHex));
                    float waitApCost = ApOneWay(recruit.ActivationApCost, recruit.MoveMax, toHome);
                    goHome = goHomeApCost <= waitApCost;
                }

                if (goHome)
                {
                    task.Retreating = true;
                    return null;
                }

                var reinforceTask = new AiTask { Kind = AiTaskKind.RaidReinforce, TargetArmy = task.Army, TargetHex = task.Army.Hex };
                return AiDecision.DispatchReinforcement(recruitSource, recruit, reinforceTask,
                    AiConfig.raidReinforceDispatchScore);
            }

            if (!AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, task.TargetHex))
                return null;

            // No raidCommittedBonus top-up any more (removed 2026-08-19) — Разведка's own
            // reconAggressionSuppressionPenalty now handles "a committed raid must reliably keep
            // moving ahead of routine scouting" from the other side of the arbiter instead.
            // ScoreForContinuation's own proximity term removed 2026-08-20 (project owner's own
            // call) — internal to target SELECTION only now, same as target.Value.Score at
            // assembly time (see TryRaidAssembleCandidates); ordinary continuation is a flat
            // aggressionBaseWeight regardless of how far the target sits.
            //
            // Feature 3 (2026-08-24) — capture-step nudge, see RaidWeakerArmyTask.
            // FindCaptureStepDestination's own comment: this ordinary continuation may detour onto a
            // DIFFERENT known unguarded/beatable enemy building it happens to pass close by, without
            // ever changing task.TargetHex itself — a later step's fresh continuation re-targets the
            // real destination the normal way (or re-detours again if another opportunity is still
            // closer). Deliberately only wired into this ordinary branch — the counter-attack/retreat
            // branches above already pick their own destination for their own reasons and must not be
            // second-guessed here.
            HexCoord moveDestination = task.TargetHex;
            string moveReason = $"attacks the target at ({task.TargetHex.Q},{task.TargetHex.R})";
            HexCoord? captureStep = RaidWeakerArmyTask.FindCaptureStepDestination(player, task.Army, task.TargetHex, ctx.Map);
            if (captureStep.HasValue && AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, captureStep.Value))
            {
                moveDestination = captureStep.Value;
                moveReason = $"detours to capture an unguarded building at ({captureStep.Value.Q},{captureStep.Value.R}) on the way";
            }
            return AiDecision.Move(task.Army, moveDestination, moveReason,
                task, AiConfig.aggressionBaseWeight, AiTaskCategory.Aggression);
        }

        // Debug-log detail for Агрессия's own "still assembling" step — the actual unit-by-unit
        // breakdown behind IsReady's verdict, so a log reader doesn't have to trust it blind. Our
        // own side reads straight off `army.Members`, EVERY member including hero(es) (2026-08-22,
        // project owner's own call — "все юниты, не только атакующих"; the win chance below still
        // only counts non-hero power, same WorthIt.AttackSum/DefenseSum convention as every other
        // real comparison in this codebase, heroes are just listed here for a complete picture).
        // The enemy side shows whatever AiMapMemory actually remembers about a fogged target — an
        // aggregate Defense/Attack total plus a per-unit DefenderProfile list that, since
        // 2026-08-22's full-roster Monte Carlo, carries a real per-unit Attack/HitPoints snapshot
        // too (not just Defense — see DefenderProfile's own comment). No per-unit NAME — nothing
        // in AiMapMemory ever remembers which unit was which, only the army/guard's own Name
        // (`required.Name`) and its roster's stats — so each entry prints as bare
        // Attack/Defense/HitPoints stats. Our own side deliberately matches that same bare-stats
        // shape now (2026-08-22, project owner's own follow-up call — no unit names on either
        // side) even though `army.Members` does carry real names.
        //
        // Win chance (2026-08-22, project owner's own follow-up call — replaces the old flat
        // AttackSum+DefenseSum ratio coefficient this used to print, which never matched the
        // actual readiness verdict above it): routes through the SAME WorthIt.WinChance call
        // RaidWeakerArmyTask.IsReady itself uses to decide readiness (full round-by-round Monte
        // Carlo against real per-unit HP where a per-unit enemy roster is known, aggregate-sum
        // fallback otherwise — see IsReady's own comment for why), so the percentage shown here
        // always matches the verdict it's explaining instead of drifting from it.
        private static string FormatNotEnoughForceLog(PlayerSetupData player, ArmyData army, RaidWeakerArmyTask.ThreatStrength required)
        {
            string ourList = army.Members.Count > 0
                ? string.Join(", ", army.Members.Select(m => $"{m.Attack}/{m.Defense}/{m.HitPointsCurrent}"))
                : "none";

            IReadOnlyList<WorthIt.DefenderProfile> enemyDefenders = required.Defenders;
            string enemyList = enemyDefenders != null && enemyDefenders.Count > 0
                ? string.Join(", ", enemyDefenders.Select(d => $"{d.Attack:0.#}/{d.Defense:0.#}/{d.HitPoints:0.#}"))
                : "none";
            string enemyName = string.IsNullOrEmpty(required.Name) ? "the target" : $"\"{required.Name}\"";

            float ourChance = enemyDefenders != null && enemyDefenders.Count > 0
                ? WorthIt.WinChance(army, enemyDefenders, required.HexBonus)
                : WorthIt.WinChance(WorthIt.AttackSum(army), WorthIt.DefenseSum(army), required.Attack, required.Defense);
            float enemyChance = 1f - ourChance;

            // Readiness diagnostic (2026-08-23, project owner's own report): IsReady is
            // `winChance > 50% AND CanDamageAll` (see RaidWeakerArmyTask.IsReady) — two
            // independent gates — but this log used to only ever print winChance, so a high
            // winChance next to "not enough force" read as contradictory/misleading when the REAL
            // reason was the coverage gate (some defender none of our units can actually scratch,
            // e.g. heavy Defense/CeramicArmor with nothing in the roster strong enough), not raw
            // power. Spelled out explicitly here so a log reader doesn't have to guess which gate
            // failed — and, notably, a composition failure the garrison genuinely has nothing left
            // to fix (no counter-unit anywhere) would otherwise wait for a "reinforcement" that can
            // never actually satisfy this task.
            bool winChanceOk = ourChance > 0.5f;
            bool coverageOk = WorthIt.CanDamageAll(army, enemyDefenders, required.HexBonus);
            string readyDiag;
            bool actuallyReady;
            if (!winChanceOk && !coverageOk)
            {
                readyDiag = $"winChance {ourChance:P0} <= 50% AND composition can't cover every defender";
                actuallyReady = false;
            }
            else if (!winChanceOk)
            {
                readyDiag = $"winChance {ourChance:P0} <= 50%";
                actuallyReady = false;
            }
            else
            {
                var uncovered = enemyDefenders?.Where(d => !army.Members.Where(m => !m.IsHero)
                    .Any(u => WorthIt.CanDamage(u.Attack, d, required.HexBonus))).ToList();
                actuallyReady = uncovered == null || uncovered.Count == 0;
                readyDiag = actuallyReady
                    ? "ready"
                    : $"winChance {ourChance:P0} OK but composition FAIL — {uncovered.Count} defender(s) nothing in the roster can damage: "
                        + string.Join(", ", uncovered.Select(d => $"{d.Attack:0.#}/{d.Defense:0.#}/{d.HitPoints:0.#}"));
            }

            // Log-vs-verdict mismatch fix (2026-08-24, project owner's own report): WorthIt.WinChance
            // runs a fresh Monte Carlo simulation every call (see its own "Full round-by-round Monte
            // Carlo" section), so THIS method's own re-roll above can land on a different result than
            // the IsReady() roll that decided to call this method at all — a real case, "55% win
            // chance ... ready, waits for reinforcement" logged in the very same line. Not a lifecycle
            // bug (the actual decision loop re-rolls fresh next step regardless and attacks the moment
            // ITS OWN roll says ready) — purely this log contradicting itself. `actuallyReady` (this
            // call's own roll) decides the WORDING here, independent of whatever IsReady() decided a
            // moment ago; never both "ready" and "waits for reinforcement" in the same line again.
            if (actuallyReady)
                return $"[AI] {player.Nickname}: \"{army.Name}\" ({army.Members.Count} units: {ourList} = {ourChance:P0} win chance) "
                    + $"vs {enemyName} ({enemyDefenders?.Count ?? 0} units: {enemyList} = {enemyChance:P0} win chance) "
                    + $"— ready to strike, winChance {ourChance:P0}, coverage OK.";

            return $"[AI] {player.Nickname}: \"{army.Name}\" ({army.Members.Count} units: {ourList} = {ourChance:P0} win chance) "
                + $"— not enough force vs {enemyName} ({enemyDefenders?.Count ?? 0} units: {enemyList} = {enemyChance:P0} win chance) "
                + $"— {readyDiag}, waits for reinforcement.";
        }

        // Both "start a brand new raid" and "recruit the next member into an already-forming one"
        // — same decision shape (AiDecision.AssembleRaidForce), same execution routine, only
        // difference is whether `task` is a fresh, not-yet-registered AiTask or an existing one
        // (AiTurnController.Commit's own generic `!Contains` check handles either transparently,
        // same as every other task kind). Композиция — see RaidWeakerArmyTask's own class
        // comment: either an already-idle army that alone already beats the target (dispatched
        // directly, no assembly at all — see FindReadyIdleArmy), or a hero-led force built up one
        // recruit at a time from whatever's currently sitting at the garrison hex (garrison stock
        // itself, or an idle army recalled there — see TryRaidRecallCandidates below).
        public static List<AiDecision> TryRaidAssembleCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiHandData hand, AiResourcePool pool)
        {
            var results = new List<AiDecision>();

            // Citadel under siege — no new raids start while Оборона's own alarm is active (see
            // AiDefencePlanner.IsUnderSiege); any already active gets force-recalled instead, see
            // TryContinueRaidTask's own siege branch above.
            if (AiDefencePlanner.IsUnderSiege(player, ctx))
                return results;

            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);

            // Every hex an active (non-retreating) Агрессия task is already targeting, ready or
            // still assembling alike — collected UP FRONT (not inside the loop below) so a task's
            // own re-target check further down sees every OTHER task's claim regardless of
            // iteration order, and can still exclude only ITS OWN current hex rather than being
            // artificially blocked from re-confirming it (see AiTask.StillAssembling's own comment
            // for why the "start a new one" tier below also needs this same full set).
            List<AiTask> assemblingOrTravelling = AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.RaidWeakerArmy && !t.Retreating).ToList();
            var activeTargets = new HashSet<HexCoord>(assemblingOrTravelling.Select(t => t.TargetHex));

            // Continue every already-registered, not-yet-ready task.
            foreach (AiTask task in assemblingOrTravelling)
            {
                if (task.Army == null || !task.Army.Hex.Equals(garrisonHex))
                    continue; // travelling or retreating — TryContinueRaidTask's own turn, not this tier's
                RaidWeakerArmyTask.ThreatStrength required = RaidWeakerArmyTask.RequiredStrengthAt(player, task.TargetHex, ctx.Map);
                if (RaidWeakerArmyTask.IsReady(task.Army, required))
                {
                    task.StillAssembling = false; // see AiTask.StillAssembling's own comment
                    continue; // ready — TryContinueRaidTask picks it up from here
                }
                task.StillAssembling = true;

                // Re-check available targets every step while still assembling AT THE GARRISON
                // (2026-08-23, project owner's own call: "нужно почаще перепроверять доступные
                // цели") — free to do here specifically because nothing has been spent toward the
                // old target yet: the army hasn't moved an inch, so every recruit gathered so far is
                // exactly as useful against a new target as the old one. Once travelling (task.Army
                // no longer at garrisonHex) or ready, this tier doesn't run at all any more, so the
                // target freezes again exactly as before — re-shopping only ever makes sense while
                // the trip itself hasn't started costing anything. Excludes every OTHER active
                // task's hex but not this task's own, so FindTarget can freely re-pick the same hex
                // right back (the common case — nothing changed) without being forced off it.
                var otherTargets = new HashSet<HexCoord>(activeTargets);
                otherTargets.Remove(task.TargetHex);
                RaidWeakerArmyTask.RaidTarget? retarget = RaidWeakerArmyTask.FindTarget(player, task.Army, ctx.Map, otherTargets);
                if (retarget.HasValue && !retarget.Value.Hex.Equals(task.TargetHex))
                {
                    // Retarget hysteresis (2026-08-24, project owner's own report — see AiConfig.
                    // raidRetargetMinImprovement's own comment). `required` at this point is always
                    // the CURRENT target's threat, and this whole loop iteration already skipped
                    // above (the `continue` right after computing it) the moment IsReady(task.Army,
                    // required) was true — so the old target is always "not ready" here, there's no
                    // "already ready, don't downgrade" case to protect against; a new target that's
                    // ready right now still always deserves to win outright.
                    bool currentStillValid = RaidWeakerArmyTask.IsStillValidTarget(player, task.TargetHex);
                    bool newReady = RaidWeakerArmyTask.IsReady(task.Army, retarget.Value.Threat);
                    float currentScore = currentStillValid
                        ? RaidWeakerArmyTask.ScoreTarget(player, task.Army, task.TargetHex, required)
                        : float.NegativeInfinity;
                    bool shouldSwitch = !currentStillValid || newReady
                        || retarget.Value.Score > currentScore + AiConfig.raidRetargetMinImprovement;

                    if (shouldSwitch)
                    {
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — retargets Agression raid from "
                            + $"({task.TargetHex.Q},{task.TargetHex.R}) to ({retarget.Value.Hex.Q},{retarget.Value.Hex.R}), "
                            + "a better prospect for the force gathered so far.");
                        activeTargets.Remove(task.TargetHex);
                        task.TargetHex = retarget.Value.Hex;
                        activeTargets.Add(task.TargetHex);
                        required = retarget.Value.Threat;
                        if (newReady)
                        {
                            task.StillAssembling = false;
                            continue; // the new target is already within reach — no need to recruit further
                        }
                    }
                }

                // No separate dead-end pre-check any more (2026-08-22, project owner's own call —
                // "армия будет собираться пока не станет равной необходимому кэфициенту, либо
                // закончится по условию"): MaxPossibleAttack/CanEventuallyDamageToughest used to
                // short-circuit here on a theoretical "can never win" read, but the branch never
                // actually cancelled the task either way (see the removed comment's own history —
                // 2026-08-16 fix already stopped it from discarding progress), so recruiting was the
                // only real difference a step made. Just log where the current numbers stand and let
                // FindRecruitAt below decide for itself whether there's anyone left to add — a null
                // result already `continue`s on its own, the same natural stop this pre-check used to
                // reach a step later anyway.
                AiDebugLog.Write(FormatNotEnoughForceLog(player, task.Army, required));

                AiDecision heroCardDecision = TryHeroCardForRaid(player, root, hand, task.Army, task,
                    AiConfig.aggressionBaseWeight + AiConfig.raidAssembleBonus);
                if (heroCardDecision != null)
                {
                    results.Add(heroCardDecision);
                    continue;
                }

                UnitData recruit = RaidWeakerArmyTask.FindRecruitAt(player, garrisonHex, task.Army, pool, out ArmyData source);
                if (recruit == null || source == null || !task.Army.HasRoom || ctx.WouldRevisitArmy(recruit, task.Army))
                    continue; // nothing to recruit (or full) this step — waits for a recall/next card
                results.Add(AiDecision.AssembleRaidForce(source, recruit, task.Army, task,
                    AiConfig.aggressionBaseWeight + AiConfig.raidAssembleBonus));
            }

            // Оборона цитадели moved out entirely 2026-08-20 — see AiDefencePlanner.
            // TryStartDefenceCandidates, its own first-class Оборона category now instead of a
            // sub-tier bolted onto Агрессия (see AiTask.cs's own AiTaskKind.DefendCitadel comment).

            // Start a new ORDINARY raid, up to the concurrency cap.
            if (AiTaskRegistry.TasksFor(player).Count(t => t.Kind == AiTaskKind.RaidWeakerArmy) >= AiConfig.maxConcurrentRaid)
                return results;

            ArmyData garrison = AiTurnController.GarrisonArmyFor(player);
            if (garrison == null)
                return results;
            RaidWeakerArmyTask.RaidTarget? target = RaidWeakerArmyTask.FindTarget(player, garrison, ctx.Map, activeTargets);
            if (!target.HasValue)
                return results;

            ArmyData readyArmy = RaidWeakerArmyTask.FindReadyIdleArmy(player, target.Value.Threat, pool);
            if (readyArmy != null)
            {
                var readyTask = new AiTask { Kind = AiTaskKind.RaidWeakerArmy, Army = readyArmy, TargetHex = target.Value.Hex };

                // Fold in a co-located sibling combat army first, one member at a time, before
                // actually attacking (see RaidWeakerArmyTask.FindCoLocatedMergeRecruit's own
                // comment) — readyArmy already wins alone, so this is purely opportunistic; if
                // something else outscores it before the merge finishes, readyArmy just attacks
                // on its own next time regardless, nothing here blocks that.
                UnitData mergeRecruit = RaidWeakerArmyTask.FindCoLocatedMergeRecruit(readyArmy, pool, out ArmyData mergeSource);
                if (mergeRecruit != null && mergeSource != null && readyArmy.HasRoom && !ctx.WouldRevisitArmy(mergeRecruit, readyArmy))
                {
                    results.Add(AiDecision.AssembleRaidForce(mergeSource, mergeRecruit, readyArmy, readyTask,
                        AiConfig.aggressionBaseWeight + AiConfig.raidAssembleBonus));
                    return results;
                }

                // target.Value.Score is FindTarget's own internal ranking term (which known target
                // to commit to) — never added to the cross-category score any more (2026-08-20,
                // project owner's own call), same "внутренний скоринг" treatment Разведка's own
                // scoutProximityWeight already gets.
                //
                // Feasibility check (2026-08-23 fix, project owner's own report): FindReadyIdleArmy
                // only ever tests combat readiness (see RaidWeakerArmyTask.IsReady), never whether
                // `readyArmy` can actually afford the trip THIS step — a real case, Halden's
                // "Swarm" freshly freed up by a ReinforceSwap at 0 AP, kept getting proposed here
                // and rejected outright at IssueMoveOrder. `readyArmy` isn't lost by skipping it
                // here — it stays exactly as ready next step, ArmyRegistry never forgets it, so no
                // task/registration is needed to remember the attempt.
                if (!AiTurnController.CanIssueMoveNow(root, readyArmy, ctx.Map, target.Value.Hex))
                    return results;
                results.Add(AiDecision.Move(readyArmy, target.Value, readyTask,
                    AiConfig.aggressionBaseWeight, AiTaskCategory.Aggression));
                return results;
            }

            ArmyData forming = pool.AvailableArmies().FirstOrDefault(a => AiArmyRoles.IsEmptyDeployableArmy(a) && a.Hex.Equals(garrisonHex));
            if (forming == null)
            {
                // Don't spend AP on a brand new empty army if an idle one already exists ANYWHERE
                // on the map (2026-08-20 fix, project owner's own report: this used to request a
                // fresh army even with spare reserve armies just sitting idle elsewhere) —
                // TryRaidRecallCandidates below is the one that walks such an army home; this tier
                // just needs to stay out of its way rather than spend AP redundantly.
                bool idleEmptyArmyExistsElsewhere = pool.AvailableArmies().Any(a => AiArmyRoles.IsEmptyDeployableArmy(a));
                if (!idleEmptyArmyExistsElsewhere && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                    results.Add(AiDecision.RequestRaidArmy(AiConfig.aggressionBaseWeight + AiConfig.raidRequestArmyPenalty));
                return results;
            }

            var newTask = new AiTask { Kind = AiTaskKind.RaidWeakerArmy, Army = forming, TargetHex = target.Value.Hex, StillAssembling = true };

            AiDecision newTaskHeroCardDecision = TryHeroCardForRaid(player, root, hand, forming, newTask,
                AiConfig.aggressionBaseWeight + AiConfig.raidAssembleBonus);
            if (newTaskHeroCardDecision != null)
            {
                results.Add(newTaskHeroCardDecision);
                return results;
            }

            UnitData firstRecruit = RaidWeakerArmyTask.FindRecruitAt(player, garrisonHex, forming, pool, out ArmyData firstSource);
            if (firstRecruit != null && firstSource != null && !ctx.WouldRevisitArmy(firstRecruit, forming))
                results.Add(AiDecision.AssembleRaidForce(firstSource, firstRecruit, forming, newTask,
                    AiConfig.aggressionBaseWeight + AiConfig.raidAssembleBonus));
            return results;
        }

        // Агрессия's own direct card-hand pipeline for the hero a forming raid army still needs —
        // mirrors AiScoutPlanner.TryStartReconAssemblyCandidatesFor's own Recce pipeline (see this
        // class's own comment for why a category-owned card pipeline scores itself directly
        // instead of routing through AiManagementPlanner.PlayCard). Checked BEFORE FindRecruitAt's
        // garrison-stock pull (project owner's own call, 2026-08-22): a spare Hero card in hand is
        // a strictly better source than pulling a hero bodily out of the Garrison — it can never
        // trip the Garrison's own capacity guard (see ArmyData.CanLeaveWithoutOvercrowding) and
        // never disturbs stock GarrisonReorgTask already sorted. Returns null if `formingArmy`
        // doesn't need a hero, has no room, hand holds no matching card, or the one it holds isn't
        // affordable right now — checked here, not after, same "never re-propose a doomed
        // candidate" principle AiManagementPlanner.FindPlacement's own comment documents.
        //
        // Hero/Unit alternation gate (2026-08-23, project owner's own report): this method's own
        // fixed score (aggressionBaseWeight+raidAssembleBonus, well above anything
        // AiManagementPlanner.TryPlayCardCandidates itself ever proposes) used to always win
        // arbitration outright whenever a raid needed a hero — the DECISION to play never
        // consulted AiManagementPlanner.IsCardRoleCoolingDown at all, only the shared
        // NotifyCardRolePlayed afterward ever touched the alternation state. That's correct for
        // keeping Менеджмент's OWN next-step scoring honest, but it let a raid grab a Hero card
        // the very step right after Менеджмент had just played one itself — two Hero cards back
        // to back, exactly the alternation is meant to prevent. Skipping outright while Hero is
        // cooling down (rather than damping the score) is the safe direction here: this method is
        // already only ever the FALLBACK once FindRecruitAt's own garrison-stock pull comes up
        // empty (see this method's own comment above), so a raid that skips one step here still
        // has next step's fresh re-evaluation, and the cooldown itself clears the moment any Unit
        // card gets played anywhere (by Менеджмент or otherwise) — it doesn't stall the raid for
        // more than a step in the ordinary case.
        private static AiDecision TryHeroCardForRaid(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            ArmyData formingArmy, AiTask task, float score)
        {
            if (hand == null || !formingArmy.HasRoom || !RaidWeakerArmyTask.NeedsHero(formingArmy)
                || AiManagementPlanner.IsCardRoleCoolingDown(player, AiManagementPlanner.CardRole.Hero))
                return null;

            foreach (CardData card in hand.Hand)
            {
                if (!AiManagementPlanner.IsUnitOrHeroCard(card) || AiManagementPlanner.IsRecceCard(card)
                    || AiManagementPlanner.RoleOf(card) != AiManagementPlanner.CardRole.Hero)
                    continue;
                CardDefinition definition = card.Definition;
                if (!AiManagementPlanner.IsAtRequiredBuilding(formingArmy, player, definition))
                    continue;
                int deployApCost = ArmyActions.EffectiveDeployApCost(definition);
                if (!root.CanSpendActionPoints(deployApCost) || !AiResourceReservation.CanAfford(root, player, definition.resourceCost))
                    continue;

                AiDecision decision = AiDecision.PlayCard(formingArmy, card, AiManagementPlanner.CardRole.Hero, score, task.Category);
                decision.Task = task;
                return decision;
            }
            return null;
        }

        // An idle army anywhere else on the map, while at least one Агрессия task is currently
        // assembling (needs recruits) — walks toward the garrison so TryRaidAssembleCandidates can
        // fold its members in once it arrives (same-hex only, like every reorg move in this
        // codebase). No Task of its own (same shape as AiScoutPlanner.TryReturnHomeCandidates) —
        // it's prep work for whichever raid task happens to still need bodies once it gets there,
        // not committed to one in particular.
        //
        // Also covers an EMPTY deployable army sitting away from the garrison (2026-08-20 fix,
        // project owner's own report — see TryRaidAssembleCandidates' own idle-empty-army guard):
        // that guard skips RequestRaidArmy whenever one exists anywhere, so without this, an empty
        // reserve army stranded off-hex would never actually reach the garrison to become the
        // `forming` army TryRaidAssembleCandidates looks for — it would just block the request AND
        // never itself get used, stalling the raid. Walking it home the same way a manned army
        // gets recalled closes that gap; once it lands on garrisonHex, the very next step's
        // `forming` lookup picks it up automatically, no special-casing needed here beyond letting
        // it through.
        public static List<AiDecision> TryRaidRecallCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            bool anyAssembling = AiTaskRegistry.TasksFor(player).Any(t => t.Kind == AiTaskKind.RaidWeakerArmy && !t.Retreating
                && t.Army != null && !RaidWeakerArmyTask.IsReady(t.Army, RaidWeakerArmyTask.RequiredStrengthAt(player, t.TargetHex, ctx.Map)));
            // Broadened 2026-08-20 (project owner's own fix) — an idle army now also drifts home
            // whenever there's ANY raid-worthy target on the map at all, not just once a task has
            // already started assembling. Keeps a spare reserve army from sitting idle while a
            // fresh one gets requested (and paid for in AP) for a raid that hasn't even started yet.
            if (!anyAssembling && !RaidWeakerArmyTask.HasAnythingToRaid(player))
                return results;

            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || army.Hex.Equals(garrisonHex)
                    || army.Controller == null || stuckScouts.Contains(army))
                    continue;
                // Feature 3 (2026-08-24) — same capture-step nudge TryContinueRaidTask's own ordinary
                // continuation gets (see RaidWeakerArmyTask.FindCaptureStepDestination's own comment):
                // an untasked army walking home to assemble may as well grab a known unguarded/
                // beatable enemy building it happens to pass close by first, without changing where
                // it's actually headed (still the garrison) for next step's own fresh re-evaluation.
                HexCoord destination = garrisonHex;
                string reason = "returns to the garrison to assemble the force";
                HexCoord? captureStep = RaidWeakerArmyTask.FindCaptureStepDestination(player, army, garrisonHex, ctx.Map);
                if (captureStep.HasValue && AiTurnController.CanIssueMoveNow(root, army, ctx.Map, captureStep.Value))
                {
                    destination = captureStep.Value;
                    reason = $"detours to capture an unguarded building at ({captureStep.Value.Q},{captureStep.Value.R}) on the way home";
                }
                else if (!AiTurnController.CanIssueMoveNow(root, army, ctx.Map, garrisonHex))
                    continue;

                var target = new AiScoutPlanner.ScoutTarget(destination, 0f, reason);
                results.Add(AiDecision.Move(army, target, null, AiConfig.raidRecallScore, AiTaskCategory.Aggression));
            }
            return results;
        }

        // Оборона цитадели's own emergency reinforcement moved out entirely 2026-08-20 — see
        // AiDefencePlanner.TryDefencePreemptCandidates.

        // RaidWeakerArmyTask's own "nothing left to raid" fallback — a combat-capable field army
        // with no active task, sitting away from the garrison, once RaidWeakerArmyTask.
        // HasAnythingToRaid says there's no known neutral/event/enemy-building target left
        // anywhere on the map (own dedicated gate, same shape as AiEconomyPlanner.
        // TryEconomyReturnHomeCandidates' own HasAnythingToBuild check — see that method's own
        // comment for why this is a coarse, reachability-blind existence check rather than a
        // per-army one: an army that simply wasn't THIS step's pick for a target that DOES still
        // exist elsewhere is left alone rather than sent home prematurely, same reasoning
        // TryRaidRecallCandidates' own anyAssembling gate follows). A lone Recce carrier
        // (AiArmyRoles.IsSoloRecce) is excluded — its own VisitHex task already owns that
        // lifecycle (TryFlee, their own goal-met handling), and it has no combat value alone
        // anyway; a Recce member riding inside a bigger combat army is NOT excluded here, that
        // army is a normal raid participant. This gate is only for a raid army left jobless once
        // its own target is gone (the project owner's own report, 2026-08-16: it just sat there
        // forever instead).
        public static List<AiDecision> TryRaidReturnHomeCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            if (RaidWeakerArmyTask.HasAnythingToRaid(player))
                return results;

            // Per-army nearest own base, not always the citadel (2026-08-21, project owner's own
            // call — same reasoning as TryContinueRaidTask's own homeHex) — different idle armies
            // scattered across the map can each have a different nearest base. Exclusion check is
            // now "already at ANY own garrison", not just the citadel specifically — an army idle
            // at a later-founded base is already home and shouldn't be walked all the way to the
            // citadel just because the old single-hex check didn't know about it.
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || AiTurnController.OwnGarrisonHexes(player).Contains(army.Hex)
                    || army.Controller == null || stuckScouts.Contains(army)
                    || AiArmyRoles.IsSoloRecce(army) || !BattleInitiator.IsCombatCapable(army))
                    continue;

                HexCoord homeHex = AiTurnController.NearestOwnGarrisonHex(player, army.Hex);

                // Feature 3 (2026-08-24) — same capture-step nudge as TryRaidRecallCandidates above;
                // this is Feature 3's own "untasked/returning combat army" case too — a jobless raid
                // army heading home may as well grab a known unguarded/beatable enemy building it
                // passes close by first (see RaidWeakerArmyTask.FindCaptureStepDestination's own
                // comment), without changing where it's actually headed for next step's own re-eval.
                HexCoord destination = homeHex;
                string reason = "nothing left to raid — returns to base";
                HexCoord? captureStep = RaidWeakerArmyTask.FindCaptureStepDestination(player, army, homeHex, ctx.Map);
                if (captureStep.HasValue && AiTurnController.CanIssueMoveNow(root, army, ctx.Map, captureStep.Value))
                {
                    destination = captureStep.Value;
                    reason = $"detours to capture an unguarded building at ({captureStep.Value.Q},{captureStep.Value.R}) on the way home";
                }
                else if (!AiTurnController.CanIssueMoveNow(root, army, ctx.Map, homeHex))
                    continue;

                var target = new AiScoutPlanner.ScoutTarget(destination, 0f, reason);
                results.Add(AiDecision.Move(army, target, null, AiConfig.raidRecallScore, AiTaskCategory.Aggression));
            }
            return results;
        }

        // ---- Постбоевая переоценка (раненая армия ждёт подкрепление или идёт домой чиниться) ----

        // A critically wounded (see RaidWeakerArmyTask.IsCriticallyWounded — ≤50% HP on some
        // non-hero member) idle field army, regardless of whether other raid targets still exist
        // anywhere (deliberately NOT gated on HasAnythingToRaid, unlike TryRaidReturnHomeCandidates
        // right above — that gate is exactly why a wounded army used to sit forever: nothing routed
        // it home while raiding elsewhere was still possible). Decides, per the project owner's own
        // spec, whether it's cheaper (in AP × turns-to-travel) to march the whole army home itself,
        // or to wait in place for a single non-hero courier from the garrison — see GoHomeApCost/
        // WaitApCost below. Skips an army a RaidReinforce task already has a courier en route to
        // (TargetArmy match, not Army — that field is the courier, see AiTask.TargetArmy's own
        // comment).
        public static List<AiDecision> TryRaidRegroupCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();

            // Per-army nearest own base, not one shared citadel hex (2026-08-21, project owner's
            // own call, same as TryRaidReturnHomeCandidates above) — computed inside the loop since
            // different wounded armies scattered across the map can each have a different nearest
            // base. Exclusion check widened the same way — already at ANY own garrison counts as
            // home, not just the citadel specifically.
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || AiTurnController.OwnGarrisonHexes(player).Contains(army.Hex)
                    || army.Controller == null || stuckScouts.Contains(army) || AiArmyRoles.IsSoloRecce(army)
                    || !BattleInitiator.IsCombatCapable(army))
                    continue;
                if (!RaidWeakerArmyTask.IsCriticallyWounded(army))
                    continue;
                if (AiTaskRegistry.TasksFor(player).Any(t => t.Kind == AiTaskKind.RaidReinforce && t.TargetArmy == army))
                    continue; // already has a courier dispatched or en route

                HexCoord homeHex = AiTurnController.NearestOwnGarrisonHex(player, army.Hex);
                RaidWeakerArmyTask.RaidTarget? target = RaidWeakerArmyTask.FindTarget(player, army, ctx.Map);
                ArmyData recruitSource = null;
                UnitData recruit = target.HasValue
                    ? RaidWeakerArmyTask.FindNonHeroRecruitAt(homeHex, pool, army, out recruitSource, army)
                    : null;
                bool canDispatch = recruit != null && recruitSource != null && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost);

                bool goHome = !target.HasValue || !canDispatch;
                if (!goHome)
                {
                    int toHome = HexGridMath.Distance(army.Hex, homeHex);
                    float goHomeApCost = ApRoundTrip(army.ActivationApCost, army.MaxMovement, toHome,
                        HexGridMath.Distance(homeHex, target.Value.Hex));
                    float waitApCost = ApOneWay(recruit.ActivationApCost, recruit.MoveMax, toHome);
                    goHome = goHomeApCost <= waitApCost;
                }

                if (goHome)
                {
                    if (!AiTurnController.CanIssueMoveNow(root, army, ctx.Map, homeHex))
                        continue;
                    var homeTarget = new AiScoutPlanner.ScoutTarget(homeHex, 0f,
                        "critically wounded — returns to base to repair");
                    results.Add(AiDecision.Move(army, homeTarget, task: null, AiConfig.aggressionBaseWeight, AiTaskCategory.Aggression));
                }
                else
                {
                    var reinforceTask = new AiTask { Kind = AiTaskKind.RaidReinforce, TargetArmy = army, TargetHex = army.Hex };
                    results.Add(AiDecision.DispatchReinforcement(recruitSource, recruit, reinforceTask,
                        AiConfig.raidReinforceDispatchScore));
                }
            }
            return results;
        }

        // Retreating's own safe-step pick (2026-08-20 fix — see TryContinueRaidTask's own two
        // call sites for why). `avoidHex` is HARD-blocked, never entered even if that means a
        // longer detour or no route at all — same semantics HexPathfinder.FindPath's own blockHex
        // already documents, reused here rather than the softer avoidHex (which only steers
        // around a hex, doesn't rule it out, and is already applied a second time regardless by
        // the actual move executor — see HexSelectionController.Movement's own AvoidEnemyHex).
        // Same "just the next hex, let the next Decide call re-evaluate" shape AiEconomyPlanner.
        // FindNextVisitedStep already uses for its own blockHex case.
        // Thin wrapper over the shared primitive (see AiTurnController.FindPathStepAvoidingZone's
        // own comment) — radius 0 is exactly the single-hex block this method always used before
        // that helper existed. TryContinueRaidTask's own siege-forced-retreat branch below calls the
        // shared helper directly instead, with AiConfig.defenceRetreatAvoidRadius.
        private static HexCoord? FindNextRetreatStep(HexMap map, ArmyData army, HexCoord destinationHex, HexCoord? avoidHex) =>
            AiTurnController.FindPathStepAvoidingZone(map, army, destinationHex, avoidHex, 0);

        // AP cost of `mover` travelling `d1` hexes then `d2` more (there-and-back through the
        // garrison, for the "go home itself" option) — one activation's worth of AP per turn the
        // trip takes, MoveMax capping how far one turn covers. Same rough "hex distance, not real
        // pathfinding" precision every other candidate-scoring helper in this codebase already
        // uses (see RaidWeakerArmyTask.ProximityScore) — good enough to compare two options, not
        // meant to predict the exact real AP spend.
        private static float ApRoundTrip(int activationApCost, int moveMax, int d1, int d2) =>
            activationApCost * (TravelTurns(d1, moveMax) + TravelTurns(d2, moveMax));

        private static float ApOneWay(int activationApCost, int moveMax, int distance) =>
            activationApCost * TravelTurns(distance, moveMax);

        private static int TravelTurns(int distance, int moveMax) =>
            moveMax > 0 ? (int)System.Math.Ceiling((double)distance / moveMax) : distance;

        // Drives an active RaidReinforce task — Army is the courier, left null by
        // TryRaidRegroupCandidates until DispatchReinforcementRoutine actually spawns it (nothing
        // reads this task in between those two steps, see that routine's own comment). Travels
        // toward TargetHex (the wounded army's own hex, fixed at dispatch — it deliberately never
        // moves while a courier is inbound, see TryRaidRegroupCandidates' own skip check) same as
        // any other travel-stage task, then hands off to the swap once arrived.
        public static AiDecision AdvanceReinforceTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army == null)
                return null; // just dispatched this same turn — DispatchReinforcementRoutine hasn't run yet

            if (task.Army.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army)
                || task.TargetArmy?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.TargetArmy))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (!task.Army.Hex.Equals(task.TargetHex))
            {
                if (!AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, task.TargetHex))
                    return null;
                var moveTarget = new AiScoutPlanner.ScoutTarget(task.TargetHex, 0f,
                    $"reinforcement heads to \"{task.TargetArmy.Name}\"");
                return AiDecision.Move(task.Army, moveTarget, task, AiConfig.aggressionBaseWeight, AiTaskCategory.Aggression);
            }

            // 2026-08-24 P0 fix (project owner's own report): the courier arriving used to be a
            // blank cheque straight into ReinforceSwap, regardless of whether either side could
            // actually AFFORD what ReinforceSwapRoutine was about to attempt — ArmyActions.
            // SwapMembers/TransferMember only reject per-call once inside the routine, which by
            // then has already committed to logging "0 wounded swapped out" and unconditionally
            // deleting the task. Preflight with the exact same pairing BuildReinforceSwapPairs
            // uses, so this prediction can't drift from what the routine actually attempts. No
            // wounded left is a legitimate (not starved) reason to still hand off to the routine —
            // that's ReinforceSwapRoutine's own "just deliver the courier's cargo" path.
            ReinforceSwapPlan plan = BuildReinforceSwapPlan(task.Army, task.TargetArmy);
            if (plan.HasWounded && !root.CanSpendActionPoints(plan.ApCost))
                return null; // AP-starved this turn — task stays put, retried once AP resets

            return AiDecision.ReinforceSwap(task, AiConfig.aggressionBaseWeight);
        }

        // Pure pairing helper shared by BuildReinforceSwapPlan's own AP preflight and
        // ReinforceSwapRoutine's actual execution, so the two can never disagree about which
        // recruit replaces which wounded member (2026-08-24, project owner's own report). Mirrors
        // ReinforceSwapRoutine's original inline loop exactly: each recruit prefers a wounded
        // member sharing a TypeTags overlap, falling back to the first still-unmatched wounded
        // member otherwise. Read-only — never mutates either army.
        private static (List<UnitData> recruitsMatched, List<UnitData> woundedMatched,
            List<UnitData> unswappedRecruits, List<UnitData> remainingWoundedPool) BuildReinforceSwapPairs(
            ArmyData courier, ArmyData wounded)
        {
            List<UnitData> remainingWounded = wounded.Members.Where(m => !m.IsHero && m.HitPointsCurrent <= m.HitPointsMax / 2).ToList();
            List<UnitData> recruits = courier.Members.Where(m => !m.IsHero).ToList();
            var recruitsMatched = new List<UnitData>();
            var woundedMatched = new List<UnitData>();
            var unswappedRecruits = new List<UnitData>();

            foreach (UnitData recruit in recruits)
            {
                UnitData replaced = remainingWounded.Count > 0
                    ? (remainingWounded.FirstOrDefault(w => w.TypeTags.Overlaps(recruit.TypeTags)) ?? remainingWounded[0])
                    : null;
                if (replaced == null)
                {
                    unswappedRecruits.Add(recruit);
                    continue;
                }
                remainingWounded.Remove(replaced);
                recruitsMatched.Add(recruit);
                woundedMatched.Add(replaced);
            }

            return (recruitsMatched, woundedMatched, unswappedRecruits, remainingWounded);
        }

        // AP cost prediction for the whole ReinforceSwapRoutine run — see ArmyActions.SwapMembers/
        // TransferMember's own comments for the underlying rule this mirrors: a side only pays
        // (from the shared PlayerRoot.ActionPoints pool, same pool for both armies since they
        // share an owner) for what it RECEIVES, and only if that side has already
        // HasActivatedThisTurn. The extra-evacuation/extra-fold-in tails are bounded by each
        // army's own HasRoom in the routine, which this preflight doesn't replicate — it costs
        // the FULL remaining pool on each tail instead, a deliberate upper bound (same "good
        // enough to compare, not exact" precision AiAggressionPlanner.ApRoundTrip's own comment
        // already accepts elsewhere): overcounting here only ever makes the AI wait one extra
        // turn it didn't strictly need to, never lets it start a swap it can't fully pay for.
        private readonly struct ReinforceSwapPlan
        {
            public readonly bool HasWounded;
            public readonly int ApCost;
            public ReinforceSwapPlan(bool hasWounded, int apCost)
            {
                HasWounded = hasWounded;
                ApCost = apCost;
            }
        }

        private static ReinforceSwapPlan BuildReinforceSwapPlan(ArmyData courier, ArmyData wounded)
        {
            bool hasWounded = wounded.Members.Any(m => !m.IsHero && m.HitPointsCurrent <= m.HitPointsMax / 2);
            (List<UnitData> recruitsMatched, List<UnitData> woundedMatched, List<UnitData> unswappedRecruits,
                List<UnitData> remainingWoundedPool) = BuildReinforceSwapPairs(courier, wounded);

            int apCost = 0;
            for (int i = 0; i < recruitsMatched.Count; i++)
            {
                if (courier.HasActivatedThisTurn)
                    apCost += woundedMatched[i].ActivationApCost;
                if (wounded.HasActivatedThisTurn)
                    apCost += recruitsMatched[i].ActivationApCost;
            }
            if (courier.HasActivatedThisTurn)
                apCost += remainingWoundedPool.Sum(w => w.ActivationApCost);
            if (wounded.HasActivatedThisTurn)
                apCost += unswappedRecruits.Sum(r => r.ActivationApCost);

            return new ReinforceSwapPlan(hasWounded, apCost);
        }

        // ---- Execution ----

        // Агрессия · сборка состава с нуля, шаг 1 — see AiDecision.RequestRaidArmy's own comment.
        public static IEnumerator RequestRaidArmyRoutine(PlayerSetupData player, AiTurnContext ctx)
        {
            HexCoord hex = AiTurnController.GarrisonHexFor(player);
            yield return AiTurnController.PanTo(ctx, hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            // Feature 4A (2026-08-24) — reuse a disposable empty shell already sitting here before
            // spending AP on a brand-new one (see GarrisonReorgTask.FindDisposableEmptyArmyAt's own
            // comment) — same AP-avoidance intent as everywhere else in this codebase that already
            // prefers reusing an existing empty army over creating one from scratch.
            ArmyData reused = GarrisonReorgTask.FindDisposableEmptyArmyAt(player, hex);
            ArmyData army = reused ?? ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
            AiDebugLog.Write(reused != null
                ? $"[AI] {player.Nickname}: Aggression task — reuses empty army \"{reused.Name}\" to assemble a raid force instead of spending AP on a new one."
                : army != null
                    ? $"[AI] {player.Nickname}: Aggression task — creates empty army \"{army.Name}\" to assemble a raid force.{delta}"
                    : $"[AI] {player.Nickname}: Aggression task — not enough AP for a new army to assemble into.");

            yield return AiTurnController.WaitStep(ctx);
        }

        // Агрессия · сборка состава с нуля, шаг 2 — see AiDecision.AssembleRaidForce's own
        // comment. `source` may itself go empty once its recruit leaves (a solo recalled idle
        // army, unlike garrison stock which never runs out this easily) — cleaned up the same way
        // AiManagementPlanner.ConsolidateUnitsRoutine already does, rather than leaving an empty
        // husk army behind.
        public static IEnumerator AssembleRaidForceRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData source = decision.ExistingArmy;
            ArmyData formingArmy = decision.MergeTarget;
            UnitData unit = decision.CollectorUnit;
            yield return AiTurnController.PanTo(ctx, source.Hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            if (ArmyActions.TransferMember(unit, source, formingArmy, ctx.HexSelection, out string failReason))
            {
                string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.{delta}");
                if (!source.IsGarrison)
                    ctx.HexSelection?.DeleteArmyIfEmptied(source);
                // DefendCitadel's own stall clock (see AiTask.AssemblyProgressTurn's own comment) —
                // a no-op for RaidWeakerArmy, which never reads this field, so setting it
                // unconditionally here is simpler than branching on decision.Task.Kind.
                if (decision.Task != null)
                    decision.Task.AssemblyProgressTurn = ctx.TurnNumber;
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't assemble the Aggression force — {failReason}");
            }

            // Feeds the cross-category oscillation guard (see AiTurnContext.WouldRevisitArmy's own
            // comment) — recorded on BOTH outcomes now (2026-08-23 fix, project owner's own report),
            // not just a landed move. A failed transfer leaves `unit`/source/formingArmy exactly as
            // they were, so every candidate-generation tier that fed this exact (unit, source,
            // target) triple would just re-offer the identical doomed transfer again next step —
            // AiDefencePlanner's own CanAffordSwapInto pre-check (added the same fix) already stops
            // the one known repeat cause (an already-activated target with no AP left), but this is
            // the general backstop for whatever OTHER reason TransferMember could still reject a
            // transfer nothing about the candidate-generation side predicts.
            ctx.RecordArmyVisit(unit, source, formingArmy);

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(formingArmy);
            yield return AiTurnController.WaitStep(ctx);
        }

        // Постбоевая переоценка, шаг 1 — single-step create+populate, same shape as
        // AiEconomyPlanner.DetachCollectorRoutine's own from-scratch branch (no separate "spawn an
        // empty army, recruit into it next step" dance needed here — this task never needs the
        // fresh army to exist before this exact step). Fills in decision.Task.Army once the courier
        // is real — the task was already registered by AiTurnController.Commit's own generic code
        // with Army still null; AiTask is a plain mutable class, so setting it here afterward is
        // safe (nothing reads task.Army between registration and this routine actually running).
        public static IEnumerator DispatchReinforcementRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
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

            // Feature 4A (2026-08-24) — same disposable-empty-shell reuse RequestRaidArmyRoutine's
            // own comment describes, applied to the courier here too.
            ArmyData reusedCourier = GarrisonReorgTask.FindDisposableEmptyArmyAt(player, source.Hex);
            ArmyData courier = reusedCourier ?? ArmyActions.CreateArmy(player, source.Hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            if (courier == null)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: not enough AP for a courier for \"{decision.Task.TargetArmy.Name}\".");
                AiTaskRegistry.Remove(player, decision.Task);
                yield break;
            }
            if (reusedCourier != null)
                AiDebugLog.Write($"[AI] {player.Nickname}: reuses empty army \"{reusedCourier.Name}\" as the courier for "
                    + $"\"{decision.Task.TargetArmy.Name}\" instead of spending AP on a new one.");

            if (ArmyActions.TransferMember(recruit, source, courier, ctx.HexSelection, out string failReason))
            {
                decision.Task.Army = courier;
                string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.{delta}");
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't dispatch the courier — {failReason}");
                AiTaskRegistry.Remove(player, decision.Task);
                // The courier was already created above but never got its recruit — never leave an
                // empty shell army behind just because the transfer itself was rejected (2026-08-24
                // P0 fix, project owner's own report: this exact rejection path used to grow the
                // army count by one empty army every time it fired).
                ctx.HexSelection?.DeleteArmyIfEmptied(courier);
            }

            yield return AiTurnController.WaitStep(ctx);
        }

        // Постбоевая переоценка, шаг 2 — courier just arrived at the wounded army's own hex.
        // Rewritten 2026-08-20 (project owner's own call) to a direct pairwise
        // ArmyActions.SwapMembers per recruit/wounded pair, rather than the old two-phase
        // transfer-everyone-out-then-everyone-in — a true "replace", not "add then remove", and
        // never transiently exceeds either army's own Capacity the way the old two-phase version
        // theoretically could (same 1-for-1-no-free-slot-needed method GarrisonReorgTask.
        // FindReorgSwap already relies on for the exact same reason). Each recruit is paired with
        // whichever still-unreplaced wounded member shares a TypeTags overlap (falls back to the
        // first remaining one if no type matches) — same "does this unit's own type fit the gap"
        // read FindNonHeroRecruitAt's own preferTypeMatchFor already applies at dispatch time,
        // just re-applied here since a courier can in principle carry more than the one recruit
        // it does today. TargetArmy (now stronger, still fielded) is left exactly where it stands
        // — the next step's TryRaidAssembleCandidates/TryRaidRegroupCandidates re-evaluate it
        // fresh like any other idle army. Army (courier, now carrying only the rescued wounded, if
        // any) is left idle too — TryRaidRegroupCandidates picks it up again next step, most
        // likely choosing "go home" now that it's sitting right where the rendezvous was.
        public static IEnumerator ReinforceSwapRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            AiTask task = decision.Task;
            ArmyData courier = task.Army;
            ArmyData wounded = task.TargetArmy;
            yield return AiTurnController.PanTo(ctx, courier.Hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;

            // Pairing itself comes from the same helper AdvanceReinforceTask's own AP preflight
            // used to decide this swap was worth issuing in the first place (2026-08-24 fix) — the
            // routine no longer works out its own pairing separately, so it can never disagree
            // with the plan that greenlit it.
            (List<UnitData> recruitsToSwap, List<UnitData> woundedToSwap, List<UnitData> unswappedRecruits,
                List<UnitData> remainingWounded) = BuildReinforceSwapPairs(courier, wounded);
            bool hadWoundedToStartWith = woundedToSwap.Count > 0 || remainingWounded.Count > 0;

            int swapped = 0;
            for (int i = 0; i < recruitsToSwap.Count; i++)
            {
                UnitData recruit = recruitsToSwap[i];
                UnitData replaced = woundedToSwap[i];
                if (ArmyActions.SwapMembers(recruit, courier, replaced, wounded, ctx.HexSelection, out string failReason))
                {
                    swapped++;
                }
                else
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: couldn't swap {recruit.Name} for {replaced.Name} — {failReason}");
                    unswappedRecruits.Add(recruit);
                }
            }

            // More wounded than recruits (2026-08-20 fix, project owner's own report: two wounded
            // at once, one single-unit courier — only one got swapped out, the other was left
            // behind for no real reason). A straight swap already keeps `wounded`'s own headcount
            // exactly even, so pulling ANOTHER wounded member out on top only ever DECREASES
            // wounded's own headcount further — never a capacity problem on that side. The only
            // real limit is the courier's own room to receive them, so take as many of the
            // remaining wounded home as actually fit; whatever's left (if the courier itself is
            // full) simply waits for the next regroup evaluation to send another courier.
            int extraEvacuated = 0;
            foreach (UnitData leftoverWounded in remainingWounded.ToList())
            {
                if (!courier.HasRoom)
                    break;
                if (ArmyActions.TransferMember(leftoverWounded, wounded, courier, ctx.HexSelection, out string failReason))
                {
                    remainingWounded.Remove(leftoverWounded);
                    extraEvacuated++;
                }
                else
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: couldn't evacuate {leftoverWounded.Name} — {failReason}");
                    break;
                }
            }

            // More recruits than wounded to replace — not reachable with today's single-recruit
            // dispatch, but this loop is written generically per the project owner's own ask (a
            // courier growing to carry several at once later shouldn't need this rewritten again).
            // Anything left over just folds straight in as extra reinforcement, if there's room.
            int extraFolded = 0;
            foreach (UnitData leftover in unswappedRecruits)
            {
                if (wounded.HasRoom && ArmyActions.TransferMember(leftover, courier, wounded, ctx.HexSelection, out string failReason))
                    extraFolded++;
                else if (!wounded.HasRoom)
                    AiDebugLog.Write($"[AI] {player.Nickname}: no room left in \"{wounded.Name}\" for {leftover.Name} — stays with the courier.");
            }

            string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
            AiDebugLog.Write($"[AI] {player.Nickname}: \"{courier.Name}\" met up with \"{wounded.Name}\" — "
                + $"{swapped} wounded swapped out"
                + (extraEvacuated > 0 ? $", {extraEvacuated} more wounded evacuated" : "")
                + (extraFolded > 0 ? $", {extraFolded} extra reinforcement(s) folded in" : "") + $".{delta}");

            // 2026-08-24 P0 fix (project owner's own report): AdvanceReinforceTask's own AP
            // preflight should already keep this from firing on an outright AP-starved rendezvous,
            // but a genuine total no-op is still possible in principle (every SwapMembers/
            // TransferMember call above rejected for some OTHER reason, e.g. a capacity edge case)
            // — the task used to be deleted unconditionally regardless, silently abandoning
            // wounded units that were never actually rescued. Only close the task out when there
            // was nothing left to rescue to begin with, or when at least one wounded member
            // actually got swapped/evacuated home; otherwise leave it for the next Decide pass to
            // retry against (same courier, same target — nothing about either army changed).
            if (!hadWoundedToStartWith || swapped > 0 || extraEvacuated > 0)
            {
                AiTaskRegistry.Remove(player, task);
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{courier.Name}\" reinforcement swap made no progress at "
                    + $"\"{wounded.Name}\" — task stays active for a retry.");
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return AiTurnController.WaitStep(ctx);
        }

        // ---- Агрессия · Задача 2 (Постройка дополнительной базы) ----
        // Trigger (all checked fresh every step, per the project owner's own spec, 2026-08-21):
        // a Base card in hand + a hero-led combat army whose own strength is at least
        // buildBaseStrengthToleranceRatio of the single strongest REAL enemy army anywhere on the
        // map (RequiredBuildBaseStrength — a deliberate cheat, see that method's own comment, not
        // fog-of-war-honest AiMapMemory) + citadel not under siege + turn ≥ buildBaseMinTurn + at
        // most maxConcurrentBuildBase such tasks already running. Composition — see
        // FindBuildBaseArmy: prefers an idle hero-led army, but an
        // army already running an active (non-retreating) RaidWeakerArmy task may also be
        // redirected (the project owner's own explicit "да активный рейд может пойти поставить
        // новую базу" call) by preempting that task, same PreemptedTask mechanism AiEconomyPlanner.
        // TryStartEconomyCandidates already established.
        public static List<AiDecision> TryStartBuildBaseCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiHandData hand, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            if (hand == null || ctx.TurnNumber < AiConfig.buildBaseMinTurn)
                return results;
            if (AiDefencePlanner.IsUnderSiege(player, ctx))
                return results;
            if (AiTaskRegistry.TasksFor(player).Count(t => t.Kind == AiTaskKind.BuildBase) >= AiConfig.maxConcurrentBuildBase)
                return results;
            if (!hand.Hand.Any(c => c.Definition.cardType == CardType.Base))
                return results;

            float required = RequiredBuildBaseStrength(player);
            if (required <= 0f)
                return results; // nothing known about any enemy army yet — no real comparison possible

            ArmyData army = FindBuildBaseArmy(player, pool, required, out AiTask preempted);
            if (army == null)
                return results;

            // Feasibility before proposing (2026-08-23 fix, project owner's own report): every
            // other "start a new task" tier already checks the candidate army can actually take
            // its first step THIS turn before ever building a Move decision (see e.g.
            // AiEconomyPlanner.TryStartEconomyCandidates' own army.CurrentMovement/
            // CanSpendActionPoints check) — this tier was the one exception, so a high-scoring
            // BuildBase candidate could win arbitration, get Commit()ed (destroying `preempted`'s
            // own in-progress Raid task in the process), and only THEN discover at
            // HexSelectionController.Movement's own IssueMoveOrder check that the army had no AP/
            // movement left this step — a real Raid force lost for nothing. Checked here, before
            // the task/decision is even built, so an infeasible pick simply produces no candidate
            // this step (the army — and any task it's still running — is untouched, tried again
            // fresh next step) instead of winning arbitration and failing execution.
            if (army.CurrentMovement <= 0 || (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost)))
                return results;

            HexCoord? targetHex = BuildBaseTask.FindTargetHex(player, army, ctx.Map);
            if (!targetHex.HasValue)
                return results;

            // The generic army.CurrentMovement<=0/AP check above only ever caught "can't move
            // AT ALL this step" — never "the specific first hex toward THIS targetHex costs more
            // than CurrentMovement" (see AiTurnController.CanIssueMoveNow's own comment on the
            // FindAffordableStep gap this closes). Re-checked here, now that targetHex is actually
            // known, rather than folded into the early filter above — that one still runs first and
            // cheaply, before the real (pricier) FindTargetHex search above even happens.
            if (!AiTurnController.CanIssueMoveNow(root, army, ctx.Map, targetHex.Value))
                return results;

            var task = new AiTask { Kind = AiTaskKind.BuildBase, Army = army, TargetHex = targetHex.Value };
            AiDecision decision = AiDecision.Move(army, targetHex.Value,
                $"heads out to found a new base at ({targetHex.Value.Q},{targetHex.Value.R})",
                task, AiConfig.aggressionBaseWeight + AiConfig.buildBaseTravelBonus, AiTaskCategory.Aggression);
            decision.PreemptedTask = preempted;
            results.Add(decision);
            return results;
        }

        // "Примерно равна силе активных армий противника" — the single STRONGEST real army found
        // anywhere on the map among every enemy player, scaled down by buildBaseStrengthToleranceRatio.
        // Same number regardless of whether 1, 2, or 3+ opponents exist — deliberately NOT a sum
        // across per-player maxes any more (2026-08-22, project owner's own reversal of the earlier
        // 2026-08-21 "посмотреть на всех противников" correction).
        // A deliberate cheat (project owner's own explicit call, 2026-08-22 — "ии-игрок может
        // читерить в этом случае, не обязательно опираться только на то что он видел") reading real
        // ArmyData directly across every player, same sanctioned exception
        // AiDefencePlanner.CheatEstimateRaiderThreat already takes for its own composition sizing —
        // fixes the earlier honest-AiMapMemory version's own "phantom threat" bug (2026-08-21
        // simulation report): a sighting recorded once and never refreshed (AiMapMemory only
        // corrects a hex once it's actually re-observed, see that class's own "видимость с
        // памятью" comment) could permanently inflate the requirement long after the real threat
        // was gone. Reading live ArmyData every call has no such staleness — it's always exactly
        // today's actual strongest enemy army. Excludes garrison/prison/empty armies, same
        // "field force, not the standing home defence" filter FindBuildBaseArmy already applies to
        // this player's own candidate armies (see below) — comparing like for like.
        private static float RequiredBuildBaseStrength(PlayerSetupData player)
        {
            float strongest = 0f;
            foreach (PlayerSetupData other in GameSession.Players ?? Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other == player || other.IsNeutral)
                    continue;
                foreach (ArmyData army in ArmyRegistry.AllForOwner(other))
                {
                    if (army.IsGarrison || army.IsPrison || army.Members.Count == 0)
                        continue;
                    float strength = WorthIt.AttackSum(army) + WorthIt.DefenseSum(army);
                    if (strength > strongest)
                        strongest = strength;
                }
            }
            return strongest * AiConfig.buildBaseStrengthToleranceRatio;
        }

        // Strongest hero-led combat army meeting `requiredStrength` — first among idle armies
        // (pool.AvailableArmies(), which already excludes anything claimed by another task this
        // step), then among armies currently running an active RaidWeakerArmy task (`preempted`
        // set only for that second group — redirecting one of those means giving up its raid).
        //
        // Task lock (2026-08-23, project owner's own report/spec): only a raid still in
        // task.StillAssembling (still recruiting, composition not yet reading as ready — see
        // AiTask.StillAssembling's own comment) is eligible to be preempted here. The moment a
        // raid's own composition reads as ready (StillAssembling flips false in
        // TryRaidAssembleCandidates) it's already moving toward — or engaging — its target, and
        // BuildBase must never grab it out from under that: a raid this far along represents real
        // sunk cost (every recruit gathered, every step already walked toward the target) that a
        // same-turn "build a base instead" opportunity shouldn't be allowed to erase. Only genuine
        // Citadel-emergency logic (AiDefencePlanner.TryDefencePreemptCandidates, IsUnderSiege) may
        // still pull a ready/en-route/engaged raid off its own task — that path is untouched by
        // this filter, it doesn't go through FindBuildBaseArmy at all.
        private static ArmyData FindBuildBaseArmy(PlayerSetupData player, AiResourcePool pool, float requiredStrength, out AiTask preempted)
        {
            preempted = null;
            ArmyData best = null;
            float bestStrength = float.NegativeInfinity;

            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || !AiArmyRoles.IsHeroLed(army) || !BattleInitiator.IsCombatCapable(army))
                    continue;
                float strength = WorthIt.AttackSum(army) + WorthIt.DefenseSum(army);
                if (strength < requiredStrength || strength <= bestStrength)
                    continue;
                bestStrength = strength;
                best = army;
            }

            foreach (AiTask task in AiTaskRegistry.TasksFor(player))
            {
                if (task.Kind != AiTaskKind.RaidWeakerArmy || task.Retreating || !task.StillAssembling || task.Army == null
                    || !AiArmyRoles.IsHeroLed(task.Army) || !BattleInitiator.IsCombatCapable(task.Army))
                    continue;
                float strength = WorthIt.AttackSum(task.Army) + WorthIt.DefenseSum(task.Army);
                if (strength < requiredStrength || strength <= bestStrength)
                    continue;
                bestStrength = strength;
                best = task.Army;
                preempted = task;
            }
            return best;
        }

        // Advances an already-committed BuildBase task — validity/siege/cancel checks first, then
        // travel, then (once arrived) the execution step. Отмена — a known enemy army within
        // buildBaseCancelRadius of the TARGET hex (not the army's own current hex — see
        // BuildBaseTask.IsLegalHex's own matching pre-filter) cancels the task outright, but only if
        // `task.Army`'s OWN win chance against it actually drops below buildBaseMinWinChance
        // (2026-08-22, project owner's own follow-up call — no longer a bare presence check; see
        // BuildBaseTask.HasThreateningEnemyNear's own comment). That method stays honest about which
        // hexes it even looks at (only ones AiMapMemory already has a sighting for near the target —
        // never learns where an enemy went), but cheats narrowly to re-verify whether a REMEMBERED
        // sighting is still physically there right now — see that method's own comment for why. No
        // forced retreat (the army is simply freed — RaidWeakerArmy's own
        // return-home/recall tiers, or AiDefencePlanner's emergency preempt, naturally pick a
        // jobless combat army back up next step if there's anywhere useful for it to go). A weak
        // enemy encountered en route may be fought first without cancelling ("на слабую армию врага
        // можем отвлечься (победить) и пойти дальше строить базу") — strength was already checked
        // before this task ever entered the orchestrator, so an unbeatable en-route threat is an
        // unexpected edge case, handled the same way (cancel, don't force a retreat) rather than
        // assumed impossible.
        public static AiDecision TryContinueBuildBaseTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiHandData hand, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army) || !AiArmyRoles.IsHeroLed(task.Army))
            {
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            // Feature 2 (2026-08-24) — the building itself is already up; see
            // AiTask.AwaitingGarrisonSeed's own comment. Checked before the siege/threat/travel logic
            // below, none of which applies any more once the army has actually arrived and built —
            // AdvanceGarrisonSeed has its own, narrower siege bail-out instead (free the army for
            // Оборона immediately rather than finish seeding first).
            if (task.AwaitingGarrisonSeed)
                return AdvanceGarrisonSeed(player, root, ctx, task);

            if (AiDefencePlanner.IsUnderSiege(player, ctx))
            {
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — citadel under siege, base-building task cancelled.");
                return null;
            }

            if (BuildBaseTask.HasThreateningEnemyNear(player, task.TargetHex, task.Army, AiConfig.buildBaseCancelRadius))
            {
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — a known enemy near the target could likely beat it, base-building task cancelled.");
                return null;
            }

            if (task.Army.CurrentMovement <= 0 || (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost)))
                return null;

            AiMapMemory.KnownEnemySighting? threat = RaidWeakerArmyTask.NearbyThreat(player, task.Army.Hex);
            if (threat.HasValue)
            {
                float threatHexBonus = WorthIt.HexDefenseBonus(threat.Value.Hex, ctx.Map);
                float threatDefense = threat.Value.DefenseSum + threatHexBonus;
                if (RaidWeakerArmyTask.IsReady(task.Army, threatDefense, threat.Value.AttackSum, threat.Value.Defenders, threatHexBonus))
                    return AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, threat.Value.Hex)
                        ? AiDecision.Move(task.Army, threat.Value.Hex, "counter-attacks a known nearby army on the way to found the new base",
                            task, AiConfig.aggressionBaseWeight + AiConfig.raidCounterAttackBonus, AiTaskCategory.Aggression)
                        : null;

                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — unexpectedly outmatched on the way to found the new base, task cancelled.");
                return null;
            }

            // Resource reservation (2026-08-23, project owner's own report/spec — see
            // AiResourceReservation.TotalReservedExcluding's own comment on why BuildBase now
            // shares BuildFacility's own pool): resolved here, before the travel/arrived branch,
            // same "starts once this turn's own movement guarantees arrival" rule
            // AiEconomyPlanner.AdvanceEconomyTask already documents for BuildFacility — starting any
            // earlier locks the resource type out of every OTHER AI spend for the whole multi-turn
            // trip; any later leaves the stockpile fully exposed right up to the one turn arrival is
            // actually guaranteed.
            CardData card = hand?.Hand.FirstOrDefault(c => c.Definition.cardType == CardType.Base);
            CardDefinition definition = card?.Definition;
            bool willArriveThisTurn = HexGridMath.Distance(task.Army.Hex, task.TargetHex) <= task.Army.MaxMovement;
            if (definition != null && willArriveThisTurn)
                AiResourceReservation.TopUp(root, player, task, definition.resourceCost);

            if (!task.Army.Hex.Equals(task.TargetHex))
                return AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, task.TargetHex)
                    ? AiDecision.Move(task.Army, task.TargetHex, $"heads to found a new base at ({task.TargetHex.Q},{task.TargetHex.R})",
                        task, AiConfig.aggressionBaseWeight + AiConfig.buildBaseTravelBonus, AiTaskCategory.Aggression)
                    : null;

            // Arrived — re-validate the hex is still actually buildable (something else may have
            // claimed it since it was picked) before proposing the execution step at all.
            BuildingData existingBuilding = BuildingRegistry.FindAt(task.TargetHex);
            if (existingBuilding != null && !BuildBaseTask.CanMergeIntoResourceSite(existingBuilding))
            {
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — target hex no longer buildable, base-building task cancelled.");
                return null;
            }

            if (card == null)
            {
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — no Base card left in hand, base-building task cancelled.");
                return null;
            }

            // Belt-and-suspenders on top of IsFullyReserved's own virtual ledger — same reasoning
            // AiEconomyPlanner.AdvanceEconomyTask's own matching check documents.
            bool shortOnResources = !AiResourceReservation.IsFullyReserved(task, definition.resourceCost) || !definition.resourceCost.CanAfford(root);
            bool shortOnAp = !root.CanSpendActionPoints(definition.apCost);
            if (shortOnResources || shortOnAp)
            {
                // Stale-plan timeout (2026-08-23, project owner's own report/spec) — see
                // AiTask.BuildBaseWaitTurns's own comment. A hero-led combat army sitting here
                // forever, unable to ever actually pay for the base while other AI spending keeps
                // outcompeting it, is a worse outcome than giving up and freeing it back to
                // Raid/Defence.
                task.BuildBaseWaitTurns++;
                if (task.BuildBaseWaitTurns > AiConfig.buildBaseMaxWaitTurns)
                {
                    AiResourceReservation.Release(task);
                    AiTaskRegistry.Remove(player, task);
                    AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — stuck unable to afford the new base for "
                        + $"{task.BuildBaseWaitTurns} turns, base-building task abandoned, army freed.");
                    return null;
                }
                string reason = shortOnResources && shortOnAp
                    ? "saving up resources and short on AP" : shortOnResources ? "saving up resources" : "short on AP";
                return AiDecision.Wait(task, $"\"{task.Army.Name}\" is on-site, {reason} to found the new base "
                    + $"at ({task.TargetHex.Q},{task.TargetHex.R}) — waiting ({task.BuildBaseWaitTurns}/{AiConfig.buildBaseMaxWaitTurns})");
            }

            return AiDecision.BuildBase(task, AiConfig.buildBaseExecuteScore);
        }

        // Агрессия · Задача 2's own execution — the AI-side equivalent of CardHandUI.TryBuildBase
        // (same underlying HexSelectionController.SpawnBuilding call, same facility-carryover from
        // a merged resource site), just reading from AiHandData instead of a dragged CardUI.
        // Affordability/legality/card-presence were already re-checked immediately before this
        // decision was proposed (TryContinueBuildBaseTask, same turn/step) — no second guard here,
        // same "trust the precheck" rule AiManagementPlanner.RepairUnitRoutine's own comment
        // documents for the identical reason.
        public static IEnumerator BuildBaseRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            AiTask task = decision.Task;
            ArmyData army = task.Army;
            yield return AiTurnController.PanTo(ctx, army.Hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
            CardData card = hand?.Hand.FirstOrDefault(c => c.Definition.cardType == CardType.Base);
            if (root == null || card == null)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" — no Base card left, couldn't found the new base.");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                yield break;
            }
            CardDefinition definition = card.Definition;

            int ap0 = root.ActionPoints;
            int human0 = root.GetResource(ResourceType.Human);
            int energy0 = root.GetResource(ResourceType.Energy);
            int materials0 = root.GetResource(ResourceType.Materials);
            int tech0 = root.GetResource(ResourceType.Tech);

            root.SpendActionPoints(definition.apCost);
            definition.resourceCost.PayFrom(root);

            // Absorb whatever was already built on a bare resource site into the new Base's own
            // slots (see BuildBaseTask.CanMergeIntoResourceSite) before its old marker is replaced
            // — same carry-over CardHandUI.TryBuildBase already does for a human's own drag-drop.
            BuildingData existing = BuildingRegistry.FindAt(task.TargetHex);
            FacilityData[] carriedOver = existing?.FacilitySlots;
            if (existing != null && existing.Visual != null)
                Object.Destroy(existing.Visual.gameObject);

            BuildingData building = ctx.HexSelection?.SpawnBuilding(definition, task.TargetHex, player);
            if (building != null && carriedOver != null)
            {
                int slot = 0;
                foreach (FacilityData facility in carriedOver)
                {
                    if (facility == null)
                        continue;
                    while (slot < building.FacilitySlots.Length && building.FacilitySlots[slot] != null)
                        slot++;
                    if (slot >= building.FacilitySlots.Length)
                        break;
                    building.FacilitySlots[slot] = facility;
                    slot++;
                }
            }

            hand.Hand.Remove(card);
            string delta = AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0);
            AiResourceReservation.Release(task);
            if (building != null)
            {
                // Feature 2 (2026-08-24, project owner's own report — see AiTask.AwaitingGarrisonSeed's
                // own comment): the task deliberately does NOT complete here any more, even though the
                // building itself is now up. The instant hand-off to "task complete, army free" this
                // used to do left a brand-new empty Garrison for exactly one step — long enough for
                // Raid/Defence to reclaim the builder army and for the enemy's own opportunity-capture
                // scan (RaidWeakerArmyTask.FindTarget's own "unguarded" case) to flag it right back.
                // TryContinueBuildBaseTask's own AwaitingGarrisonSeed branch drives the actual seed
                // transfer (or the stale-task timeout) from here on.
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" founds a new base at "
                    + $"({task.TargetHex.Q},{task.TargetHex.R}) — building complete, seeding its garrison next.{delta}");
                task.AwaitingGarrisonSeed = true;
                // Turn-boundary timeout fix (2026-08-24 P1) — see AiTask.GarrisonSeedStartedTurn's
                // own comment. Stamped once, right here, the same moment AwaitingGarrisonSeed
                // itself first flips true — AdvanceGarrisonSeed below computes elapsed turns off
                // this rather than incrementing a counter once per Decide() step.
                task.GarrisonSeedStartedTurn = ctx.TurnNumber;
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" couldn't found the new base — spawn failed.{delta}");
                AiTaskRegistry.Remove(player, task);
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(army);
            yield return AiTurnController.WaitStep(ctx);
        }

        // Feature 2's own continuation for the AwaitingGarrisonSeed phase (see
        // AiTask.AwaitingGarrisonSeed's own comment) — runs once per step (from
        // TryContinueBuildBaseTask's own branch above) until either a seed transfer actually lands,
        // or GarrisonSeedStartedTurn's own stale-task escape hatch fires. Under siege, the army is freed
        // immediately rather than finishing the seed first (2026-08-24, same priority
        // AiDefencePlanner.TryDefencePreemptCandidates already gives a genuine citadel emergency over
        // everything else in this codebase) — a still-hero-led combat army standing right next to a
        // fresh base is exactly the kind of body Оборона would want back regardless of whether the
        // garrison itself ends up seeded this turn or the next.
        private static AiDecision AdvanceGarrisonSeed(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (AiDefencePlanner.IsUnderSiege(player, ctx))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — citadel under siege, "
                    + "new-base garrison seeding abandoned, army freed.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            ArmyData garrison = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison && a.Hex.Equals(task.TargetHex));
            if (garrison == null)
            {
                // The building/garrison itself never actually materialized (or was lost since) —
                // nothing left here to seed.
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            if (garrison.Members.Count > 0)
            {
                // Something already reached this garrison on its own — a card routed here by
                // AiManagementPlanner.FindPlacement's own temporary priority nudge, a reinforcement,
                // or an earlier seed transfer that landed. Either way the "unguarded" gap this whole
                // phase exists to close is already shut.
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — new base garrison at "
                    + $"({task.TargetHex.Q},{task.TargetHex.R}) is no longer empty, BuildBase task complete.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            UnitData seed = FindGarrisonSeedUnit(task.Army);
            if (seed == null)
            {
                // Builder army is hero-only, or every remaining non-hero member is the hero's own
                // last escort (see FindGarrisonSeedUnit's own comment) — don't hold the army hostage
                // forever; hand off to the reservation-routing nudge (AiManagementPlanner.
                // FindPlacement/OwnGarrisonHexesByActivity) instead and time out on our own clock.
                // Turn-boundary fix (2026-08-24 P1, project owner's own code-review report) — elapsed
                // REAL turns, not Decide()-step calls (see AiTask.GarrisonSeedStartedTurn's own
                // comment for why the old per-call counter could time out within a single turn).
                int waitedTurns = ctx.TurnNumber - task.GarrisonSeedStartedTurn;
                if (waitedTurns > AiConfig.garrisonSeedMaxWaitTurns)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — no unit to spare for the new "
                        + $"garrison at ({task.TargetHex.Q},{task.TargetHex.R}) after {waitedTurns} turn(s), "
                        + "leaves it to the next card/reinforcement instead, BuildBase task complete.");
                    AiTaskRegistry.Remove(player, task);
                }
                return null;
            }

            if (!GarrisonReorgTask.CanAffordTransferInto(garrison, seed))
                return null; // AP-short this step — retried next step, same army/unit, nothing else changed

            var move = new GarrisonReorgTask.ConsolidationMove(task.Army, seed, garrison,
                $"\"{task.Army.Name}\" leaves {seed.Name} to seed the new garrison at ({task.TargetHex.Q},{task.TargetHex.R})");
            return AiDecision.SeedNewBaseGarrison(move, task, AiConfig.buildBaseExecuteScore);
        }

        // Feature 2's own candidate pick (2026-08-24) — see AiTask.AwaitingGarrisonSeed's own
        // comment. Preference order matches the project owner's own spec: never the hero; not a
        // Recce unit (Recce operates solo by design — see AiScoutPlanner/AiManagementPlanner.
        // IsRecceCard's own identification elsewhere — stripping it into a garrison defeats its
        // whole purpose); not critically wounded (RaidWeakerArmyTask.IsCriticallyWounded is
        // ArmyData-scoped, not the per-unit read this pick needs, so the same ≤50%HP threshold is
        // applied directly per candidate instead — see UnitCompositionFitBonus's own criterion 6 for
        // the identical threshold used the same way elsewhere in this codebase); lowest strategic
        // value to the builder army itself (Defense+Attack ascending); a defensive/ranged unit
        // preferred on a tie (Range > 1 — cheap insurance sitting behind the new base's own defense
        // bonus, same intuition GarrisonReorgTask.FindHexBalanceMove's own 2.1.3 exception already
        // uses for "which unit stays behind"). Never strips a hero-led builder army down to the hero
        // standing alone — leaving exactly the hero's last non-hero escort behind would immediately
        // re-trigger AiArmyRoles.IsSoloHeroAwaitingEscort next step, the same "hero alone and exposed"
        // outcome AiScoutPlanner's own return-home fallback already exists to avoid, not something
        // this pick should ever cause on purpose.
        private static UnitData FindGarrisonSeedUnit(ArmyData builderArmy)
        {
            if (builderArmy == null)
                return null;
            List<UnitData> nonHero = builderArmy.Members.Where(m => !m.IsHero && !m.HasAbility(UnitAbilities.Recce)).ToList();
            if (nonHero.Count == 0)
                return null;

            bool heroLed = builderArmy.Members.Any(m => m.IsHero);
            if (heroLed && nonHero.Count == 1)
                return null; // would leave the hero's own last escort behind — never worth it

            List<UnitData> healthy = nonHero.Where(m => m.HitPointsCurrent > m.HitPointsMax / 2).ToList();
            List<UnitData> pool = healthy.Count > 0 ? healthy : nonHero; // no healthy candidate — a wounded one still beats an undefended base

            return pool.OrderBy(m => m.Defense + m.Attack).ThenByDescending(m => m.Range > 1 ? 1 : 0).First();
        }

        // Feature 2's own execution step (2026-08-24) — see AiTask.AwaitingGarrisonSeed's own
        // comment and AdvanceGarrisonSeed above. Mirrors AiManagementPlanner.ConsolidateUnitsRoutine's
        // own single-transfer shape (same ArmyActions.TransferMember call, same failure handling,
        // same oscillation-guard bookkeeping) — not a call INTO that routine directly since this one
        // also has to close the BuildBase task out once the transfer lands, which
        // ConsolidateUnitsRoutine has no notion of a task to do.
        public static IEnumerator SeedNewBaseGarrisonRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
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
                AiDebugLog.Write($"[AI] {player.Nickname}: new base garrison seeded with {move.Unit.Name} — {decision.Reason}, "
                    + $"BuildBase task complete.{delta}");
                ctx.RecordArmyVisit(move.Unit, move.Source, move.Target);
                if (task != null)
                    AiTaskRegistry.Remove(player, task);
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't seed the new garrison with {move.Unit.Name} — {failReason}");
                // Task left registered — AdvanceGarrisonSeed retries fresh next step (same builder
                // army, same pick, unless something about the roster changed meanwhile).
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(move.Target);
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
