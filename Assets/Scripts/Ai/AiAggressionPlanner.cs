using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
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
                float score = underSiege ? AiConfig.defencePreemptScore : AiConfig.defenceRetreatScore;
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
                        task, AiConfig.defenceRetreatScore, AiTaskCategory.Aggression);
                }

                if (!AiTurnController.CanIssueMoveNow(root, player, task.Army, ctx.Map, threat.Value.Hex))
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
            if (!RaidWeakerArmyTask.IsReady(task.Army, required, AiConfig.raidMinimumWinChance))
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
                UnitData recruit = RaidWeakerArmyTask.FindNonHeroRecruitAt(player, homeHex, pool, task.Army, out ArmyData recruitSource, task.Army);
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

            // Cost-of-victory gate (2026-08-26 P1, "RaidWeakerArmy не оценивает цену победы") —
            // IsReady above only ever asked "can we win", never "is winning worth it". Computed
            // here (not just at log time below) so a too-costly-but-technically-winnable target
            // can actually turn this army around instead of always marching in — see AiConfig.
            // raidMaxAcceptableCriticalChance's own comment for the full gate/exception rundown.
            // Reused below for the unconditional log line too, so this never runs the same Monte
            // Carlo estimate twice for one call.
            WorthIt.BattleEstimate estimate = RaidWeakerArmyTask.EstimateAgainst(task.Army, required);
            bool costAcceptable = RaidWeakerArmyTask.IsCostOfVictoryAcceptable(player, task.TargetHex, homeHex, required, estimate);
            if (!costAcceptable)
            {
                if (task.Army.Hex.Equals(citadelHex))
                    return null; // still assembling — TryRaidAssembleCandidates' own turn to act (citadel-scoped)

                // Same "wait for a courier, or just walk home" AP comparison the outmatched-IsReady
                // branch above already applies — a winnable-but-too-costly target gets the same two
                // options a not-yet-winnable one does (retreat home doubles as "give up on this
                // target" here, same as it already does above).
                UnitData recruit = RaidWeakerArmyTask.FindNonHeroRecruitAt(player, homeHex, pool, task.Army, out ArmyData recruitSource, task.Army);
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

                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" raid vs ({task.TargetHex.Q},{task.TargetHex.R}) "
                    + $"clears win chance ({estimate.WinChance:P0}) but the cost is too high — expected survivor HP on win "
                    + $"{estimate.ExpectedSurvivingHpRatioOnWin:P0}, critical-after-win chance {estimate.CriticalAfterBattleChance:P0} — "
                    + (goHome ? "too costly, retreating to regroup." : "too costly, waiting for reinforcement."));

                if (goHome)
                {
                    task.Retreating = true;
                    return null;
                }

                var reinforceTask = new AiTask { Kind = AiTaskKind.RaidReinforce, TargetArmy = task.Army, TargetHex = task.Army.Hex };
                return AiDecision.DispatchReinforcement(recruitSource, recruit, reinforceTask,
                    AiConfig.raidReinforceDispatchScore);
            }

            if (!AiTurnController.CanIssueMoveNow(root, player, task.Army, ctx.Map, task.TargetHex))
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
            // Diagnostic (2026-08-24 P1 fix, project owner's own report — "50% слишком близко к
            // coin flip") — logs the actual committed win chance right alongside the decision
            // that acts on it, so a log reader can see real numbers instead of trusting IsReady's
            // bare pass/fail verdict blind. Only on the ordinary "still going" step, not every
            // retarget/assembly check above — those already log their own outcome.
            //
            // Expected survivor HP / critical-after-win chance (2026-08-24 P1 plan, "WorthIt не
            // оценивает цену победы") — a high WinChance alone can't say whether the win leaves the
            // army immediately critically wounded (RaidWeakerArmyTask.IsCriticallyWounded) and
            // straight back to base to repair. Gates the decision now (see costAcceptable above,
            // 2026-08-26 P1) — this log just narrates WHY: "attacks despite risk" when costAcceptable
            // only passed via one of IsCostOfVictoryAcceptable's own exceptions (strategic target /
            // safe retreat home), plain otherwise.
            // Logged at most once per task per real game turn — see AiTask.
            // LastBattleEstimateLoggedTurn's own comment (including its own caveat: this is a
            // coarse fingerprint, not a full change detector) — unless that fingerprint changed
            // since the last log, in which case it's worth a fresh line even within the same turn
            // (a retarget or a reinforcement landing mid-turn is real news, not just this method
            // being called again for the next movement step of the same unchanged trip).
            float armyPower = WorthIt.AttackSum(task.Army) + WorthIt.DefenseSum(task.Army);
            bool battleEstimateChanged = task.LastBattleEstimateLoggedTurn != ctx.TurnNumber
                || !task.LastBattleEstimateTargetHex.Equals(task.TargetHex)
                || task.LastBattleEstimateArmyMemberCount != task.Army.Members.Count
                || !Mathf.Approximately(task.LastBattleEstimateArmyPower, armyPower)
                || !Mathf.Approximately(task.LastBattleEstimateThreatDefense, required.Defense);
            if (battleEstimateChanged)
            {
                bool risky = !required.IsUndefended
                    && (estimate.CriticalAfterBattleChance > AiConfig.raidMaxAcceptableCriticalChance
                        || estimate.ExpectedSurvivingHpRatioOnWin < AiConfig.raidMinAcceptableSurvivorHpRatio);
                string riskNote = risky
                    ? (RaidWeakerArmyTask.IsStrategicallyImportant(player, task.TargetHex)
                        ? " Attacking despite the risk — target is strategically important."
                        : " Attacking despite the risk — close enough to retreat home and repair afterward.")
                    : "";
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" raid win chance vs "
                    + $"({task.TargetHex.Q},{task.TargetHex.R}) ~ {estimate.WinChance:P0} (min {AiConfig.raidMinimumWinChance:P0}), "
                    + $"expected survivor HP on win {estimate.ExpectedSurvivingHpRatioOnWin:P0}, "
                    + $"critical-after-win chance {estimate.CriticalAfterBattleChance:P0}.{riskNote}");
                task.LastBattleEstimateLoggedTurn = ctx.TurnNumber;
                task.LastBattleEstimateTargetHex = task.TargetHex;
                task.LastBattleEstimateArmyMemberCount = task.Army.Members.Count;
                task.LastBattleEstimateArmyPower = armyPower;
                task.LastBattleEstimateThreatDefense = required.Defense;
            }

            HexCoord moveDestination = task.TargetHex;
            string moveReason = $"attacks the target at ({task.TargetHex.Q},{task.TargetHex.R})";
            RaidWeakerArmyTask.CaptureStepOpportunity? captureStep =
                RaidWeakerArmyTask.FindCaptureStepDestination(player, task.Army, task.TargetHex, ctx.Map);
            if (captureStep.HasValue && AiTurnController.CanIssueMoveNow(root, player, task.Army, ctx.Map, captureStep.Value.NextHex))
            {
                moveDestination = captureStep.Value.NextHex;
                moveReason = FormatCaptureStepReason(captureStep.Value, "on the way");
            }
            return AiDecision.Move(task.Army, moveDestination, moveReason,
                task, AiConfig.aggressionBaseWeight, AiTaskCategory.Aggression);
        }

        // Shared reason text for all three FindCaptureStepDestination call sites (2026-08-24 P2,
        // project owner's own playtest report) — names the REAL building hex separately from the
        // next-step hex (an approach step's own next hex is a waypoint, not the building), and only
        // says "unguarded" for a confirmed-empty opportunity (CaptureStepOpportunity.IsUndefended);
        // a hero-only defended building is still a legitimate detour target (it's beatable, see
        // RaidWeakerArmyTask.FindCaptureStepDestination's own comment) but arriving there is a real
        // contact, not a free capture, so the log says so instead of promising one.
        private static string FormatCaptureStepReason(RaidWeakerArmyTask.CaptureStepOpportunity opportunity, string trailer)
        {
            string building = $"({opportunity.BuildingHex.Q},{opportunity.BuildingHex.R})";
            string nextStep = $"({opportunity.NextHex.Q},{opportunity.NextHex.R})";
            return opportunity.IsUndefended
                ? $"detours toward unguarded enemy building at {building}, next step {nextStep}, {trailer}"
                : $"detours toward enemy building at {building}, next step {nextStep}, defender is beatable, {trailer}";
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
        //
        // minimumWinChance (2026-08-24, project owner's own report): must be the SAME threshold
        // IsReady() itself was called with (AiConfig.raidMinimumWinChance, currently 0.65) — this
        // used to hardcode a stale 0.5 here after the real threshold was raised, so a log could
        // call an army "not enough force" at 55% right next to a min-65% decision, or (worse) mark
        // an army ready at, say, 60% when the real gate would keep assembling it further.
        private static string FormatNotEnoughForceLog(PlayerSetupData player, ArmyData army,
            RaidWeakerArmyTask.ThreatStrength required, float minimumWinChance)
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
            // `winChance >= minimumWinChance AND CanDamageAll` (see RaidWeakerArmyTask.IsReady) —
            // two independent gates — but this log used to only ever print winChance, so a high
            // winChance next to "not enough force" read as contradictory/misleading when the REAL
            // reason was the coverage gate (some defender none of our units can actually scratch,
            // e.g. heavy Defense/CeramicArmor with nothing in the roster strong enough), not raw
            // power. Spelled out explicitly here so a log reader doesn't have to guess which gate
            // failed — and, notably, a composition failure the garrison genuinely has nothing left
            // to fix (no counter-unit anywhere) would otherwise wait for a "reinforcement" that can
            // never actually satisfy this task.
            bool winChanceOk = ourChance >= minimumWinChance;
            bool coverageOk = WorthIt.CanDamageAll(army, enemyDefenders, required.HexBonus);
            string readyDiag;
            bool actuallyReady;
            if (!winChanceOk && !coverageOk)
            {
                readyDiag = $"winChance {ourChance:P0} < {minimumWinChance:P0} AND composition can't cover every defender";
                actuallyReady = false;
            }
            else if (!winChanceOk)
            {
                readyDiag = $"winChance {ourChance:P0} < {minimumWinChance:P0}";
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
                if (RaidWeakerArmyTask.IsReady(task.Army, required, AiConfig.raidMinimumWinChance))
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
                // An operation-owned raid (2026-08-27 operations layer) never re-shops its target
                // here — AiOperationPlanner pins it to the operation's strategic objective and owns
                // the decision to change or abandon it.
                var otherTargets = new HashSet<HexCoord>(activeTargets);
                otherTargets.Remove(task.TargetHex);
                RaidWeakerArmyTask.RaidTarget? retarget = task.OperationId >= 0
                    ? (RaidWeakerArmyTask.RaidTarget?)null
                    : RaidWeakerArmyTask.FindTarget(player, task.Army, ctx.Map, otherTargets);
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
                    bool newReady = RaidWeakerArmyTask.IsReady(task.Army, retarget.Value.Threat, AiConfig.raidMinimumWinChance);
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
                // only real difference a step made. FindRecruitAt below still decides for itself
                // whether there's anyone left to add — a null result naturally stops there, same as
                // before — but now feeds a real stall watchdog too (see below) instead of letting a
                // genuinely dead assembly wait for a reinforcement that can never come.
                // Progress-scaled assemble score (2026-08-27, project owner's own log audit — see
                // AiConfig.raidAssembleMinBonusFactor's own comment). Below raidMinimumWinChance the
                // raidAssembleBonus term tapers toward its floor so a far-from-ready assembly stops
                // out-competing every routine scout/economy move step after step; a raid already at
                // or past the bar keeps the full bonus.
                float assembleWinChance = RaidWeakerArmyTask.WinChanceAgainst(task.Army, required);
                float assembleBonusFactor = assembleWinChance >= AiConfig.raidMinimumWinChance
                    ? 1f
                    : Mathf.Clamp(assembleWinChance / AiConfig.raidMinimumWinChance,
                        AiConfig.raidAssembleMinBonusFactor, 1f);
                float assembleScore = AiConfig.aggressionBaseWeight + AiConfig.raidAssembleBonus * assembleBonusFactor;

                AiDecision heroCardDecision = TryHeroCardForRaid(player, root, hand, task.Army, task,
                    assembleScore, ctx);
                UnitData recruit = null;
                ArmyData source = null;
                bool recruitAvailable = heroCardDecision != null;
                if (!recruitAvailable)
                {
                    recruit = RaidWeakerArmyTask.FindRecruitAt(player, garrisonHex, task.Army, pool, out source);
                    recruitAvailable = recruit != null && source != null && task.Army.HasRoom && !ctx.WouldRevisitArmy(recruit, task.Army);
                }

                // Stall-detection / log-dedup snapshot (2026-08-26, project owner's own spec item 5
                // — see AiTask.RaidStallSinceTurn's own comment). Compares against the last snapshot
                // this exact task recorded; any real difference both resets the stall clock AND
                // earns a fresh "not enough force" log line even within the same turn, while an
                // identical repeat is logged at most once per turn (see AiConfig.raidStallTurns).
                float currentWinChance = assembleWinChance; // already computed above, same (task.Army, required)
                int memberCount = task.Army.Members.Count;
                bool signatureChanged = task.RaidStallSinceTurn < 0
                    || memberCount != task.RaidStallMemberCount
                    || !task.TargetHex.Equals(task.RaidStallTarget)
                    || recruitAvailable != task.RaidStallHadRecruit
                    || !Mathf.Approximately(currentWinChance, task.RaidStallWinChance);
                if (signatureChanged)
                {
                    task.RaidStallSinceTurn = ctx.TurnNumber;
                    task.RaidStallMemberCount = memberCount;
                    task.RaidStallTarget = task.TargetHex;
                    task.RaidStallHadRecruit = recruitAvailable;
                    task.RaidStallWinChance = currentWinChance;
                }
                if (signatureChanged || task.RaidLastLoggedTurn != ctx.TurnNumber)
                {
                    task.RaidLastLoggedTurn = ctx.TurnNumber;
                    AiDebugLog.Write(FormatNotEnoughForceLog(player, task.Army, required, AiConfig.raidMinimumWinChance));
                }

                // Wall-clock hard cap (2026-08-27, project owner's own log audit — see AiConfig.
                // raidAssembleMaxTurns / AiTask.RaidAssembleStartedTurn). Lazily stamped for any
                // task that predates this field (loaded mid-game at -1) so the cap counts from now,
                // never from turn 0.
                if (task.RaidAssembleStartedTurn < 0)
                    task.RaidAssembleStartedTurn = ctx.TurnNumber;
                bool hardCapReached = ctx.TurnNumber - task.RaidAssembleStartedTurn >= AiConfig.raidAssembleMaxTurns;

                // Abandon watchdog — either the dead-end case (nothing available to add for
                // raidStallTurns running) OR the wall-clock cap (a force that keeps growing one
                // body at a time but still can't clear an unwinnable target). Retarget to a
                // GENUINELY DIFFERENT known target if one exists, else cancel outright and free the
                // army back to the idle pool. The exclude set keeps this task's OWN current hex in
                // it (2026-08-27 fix — it used to Remove() it, so FindTarget was free to hand the
                // identical hex straight back, log "retargets from (X) to (X)", and reset the stall
                // clock forever: the cancel branch was structurally unreachable while any target
                // existed at all).
                // An operation-owned raid is exempt — AiOperationPlanner's own phase machine owns
                // its deadline and abort (2026-08-27 operations layer). It still gets the "not
                // enough force" diagnostic line above, just not the cancel.
                bool deadEnd = !recruitAvailable && ctx.TurnNumber - task.RaidStallSinceTurn >= AiConfig.raidStallTurns;
                if ((deadEnd || hardCapReached) && task.OperationId < 0)
                {
                    var otherTargetsForStall = new HashSet<HexCoord>(activeTargets) { task.TargetHex };
                    RaidWeakerArmyTask.RaidTarget? fallbackTarget = RaidWeakerArmyTask.FindTarget(player, task.Army, ctx.Map, otherTargetsForStall);
                    string why = hardCapReached
                        ? $"raid assembly hit the {AiConfig.raidAssembleMaxTurns}-turn cap still not ready"
                        : $"raid assembly stalled {AiConfig.raidStallTurns}+ turns with no progress";
                    if (fallbackTarget.HasValue && !fallbackTarget.Value.Hex.Equals(task.TargetHex))
                    {
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {why}, retargets from "
                            + $"({task.TargetHex.Q},{task.TargetHex.R}) to ({fallbackTarget.Value.Hex.Q},{fallbackTarget.Value.Hex.R}).");
                        activeTargets.Remove(task.TargetHex);
                        task.TargetHex = fallbackTarget.Value.Hex;
                        activeTargets.Add(task.TargetHex);
                        task.RaidStallSinceTurn = ctx.TurnNumber;
                        task.RaidStallTarget = task.TargetHex;
                        task.RaidStallWinChance = RaidWeakerArmyTask.WinChanceAgainst(task.Army, fallbackTarget.Value.Threat);
                        task.RaidAssembleStartedTurn = ctx.TurnNumber; // fresh wall-clock for the new target
                    }
                    else
                    {
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {why} and no other known "
                            + "target, cancels the raid (army returns to the idle pool).");
                        AiTaskRegistry.Remove(player, task);
                    }
                    continue;
                }

                if (heroCardDecision != null)
                {
                    results.Add(heroCardDecision);
                    continue;
                }
                if (!recruitAvailable)
                    continue; // nothing to recruit (or full) this step — waits for a recall/next card
                results.Add(AiDecision.AssembleRaidForce(source, recruit, task.Army, task, assembleScore));
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

            ArmyData readyArmy = RaidWeakerArmyTask.FindReadyIdleArmy(player, target.Value.Threat, pool, AiConfig.raidMinimumWinChance);
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
                if (!AiTurnController.CanIssueMoveNow(root, player, readyArmy, ctx.Map, target.Value.Hex))
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

            var newTask = new AiTask { Kind = AiTaskKind.RaidWeakerArmy, Army = forming, TargetHex = target.Value.Hex, StillAssembling = true, RaidAssembleStartedTurn = ctx.TurnNumber };

            AiDecision newTaskHeroCardDecision = TryHeroCardForRaid(player, root, hand, forming, newTask,
                AiConfig.aggressionBaseWeight + AiConfig.raidAssembleBonus, ctx);
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
            ArmyData formingArmy, AiTask task, float score, AiTurnContext ctx)
        {
            if (hand == null || !formingArmy.HasRoom || !RaidWeakerArmyTask.NeedsHero(formingArmy)
                || AiManagementPlanner.IsCardRoleCoolingDown(player, AiManagementPlanner.CardRole.Hero))
                return null;

            foreach (CardData card in hand.Hand)
            {
                if (!AiManagementPlanner.IsUnitOrHeroCard(card) || AiManagementPlanner.IsRecceCard(card)
                    || AiManagementPlanner.RoleOf(card) != AiManagementPlanner.CardRole.Hero
                    || ctx.FailedPlayCardsThisTurn.Contains(card))
                    continue;
                CardDefinition definition = card.Definition;
                if (!AiManagementPlanner.IsAtRequiredBuilding(formingArmy, player, definition))
                    continue;
                // Effective (instance) cost — a Research/Production-created Hero card plays at
                // activationApCost with its resources already paid (spec §5).
                int deployApCost = AiCardCost.PlayAp(card);
                if (!root.CanSpendActionPoints(deployApCost) || !AiCardCost.CanAffordPlayResources(root, player, card))
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
                && t.Army != null && !RaidWeakerArmyTask.IsReady(t.Army, RaidWeakerArmyTask.RequiredStrengthAt(player, t.TargetHex, ctx.Map),
                    AiConfig.raidMinimumWinChance));
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
                RaidWeakerArmyTask.CaptureStepOpportunity? captureStep =
                    RaidWeakerArmyTask.FindCaptureStepDestination(player, army, garrisonHex, ctx.Map);
                if (captureStep.HasValue && AiTurnController.CanIssueMoveNow(root, player, army, ctx.Map, captureStep.Value.NextHex))
                {
                    destination = captureStep.Value.NextHex;
                    reason = FormatCaptureStepReason(captureStep.Value, "on the way home");
                }
                else if (!AiTurnController.CanIssueMoveNow(root, player, army, ctx.Map, garrisonHex))
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
                RaidWeakerArmyTask.CaptureStepOpportunity? captureStep =
                    RaidWeakerArmyTask.FindCaptureStepDestination(player, army, homeHex, ctx.Map);
                if (captureStep.HasValue && AiTurnController.CanIssueMoveNow(root, player, army, ctx.Map, captureStep.Value.NextHex))
                {
                    destination = captureStep.Value.NextHex;
                    reason = FormatCaptureStepReason(captureStep.Value, "on the way home");
                }
                else if (!AiTurnController.CanIssueMoveNow(root, player, army, ctx.Map, homeHex))
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
                    ? RaidWeakerArmyTask.FindNonHeroRecruitAt(player, homeHex, pool, army, out recruitSource, army)
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
                    if (!AiTurnController.CanIssueMoveNow(root, player, army, ctx.Map, homeHex))
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
                if (!AiTurnController.CanIssueMoveNow(root, player, task.Army, ctx.Map, task.TargetHex))
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
        // a Base card in hand + a hero-led combat army + citadel not under siege + turn ≥
        // buildBaseMinTurn + at most maxConcurrentBuildBase such tasks already running. No global
        // relative-strength gate any more (2026-08-24 removal — see AiConfig's own comment where
        // buildBaseStrengthToleranceRatio used to live): FindBuildBaseArmy now picks the WEAKEST
        // eligible hero-led combat army rather than requiring one to already be strong, so BuildBase
        // stops competing with Raid/Defence for the best available force. Composition — see
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

            ArmyData army = FindBuildBaseArmy(player, pool, out AiTask preempted);
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

            // No diagnostic log here any more (2026-08-24 follow-up fix, project owner's own
            // report: "новые диагностические строки снова будут спамить лог") — this method just
            // GENERATES a candidate, most of which lose Decide's own arbitration and are discarded
            // (see AiTurnController.Commit's own class comment) — logging here printed a line for
            // every candidate BUILT, not every one that actually STARTED. AiTurnController.Commit
            // logs "BuildBase actor selected" instead, exactly once, only for the candidate that
            // actually wins and gets registered.

            // The generic army.CurrentMovement<=0/AP check above only ever caught "can't move
            // AT ALL this step" — never "the specific first hex toward THIS targetHex costs more
            // than CurrentMovement" (see AiTurnController.CanIssueMoveNow's own comment on the
            // FindAffordableStep gap this closes). Re-checked here, now that targetHex is actually
            // known, rather than folded into the early filter above — that one still runs first and
            // cheaply, before the real (pricier) FindTargetHex search above even happens.
            if (!AiTurnController.CanIssueMoveNow(root, player, army, ctx.Map, targetHex.Value))
                return results;

            var task = new AiTask { Kind = AiTaskKind.BuildBase, Army = army, TargetHex = targetHex.Value };
            AiDecision decision = AiDecision.Move(army, targetHex.Value,
                $"heads out to found a new base at ({targetHex.Value.Q},{targetHex.Value.R})",
                task, AiConfig.aggressionBaseWeight + AiConfig.buildBaseTravelBonus, AiTaskCategory.Aggression);
            decision.PreemptedTask = preempted;

            // "BuildBase и BuildFacility резервируют один хекс разными армиями" fix (2026-08-24,
            // project owner's own report) — BuildBase outranks a BuildFacility already headed for
            // the same hex (see AiDecision.PreemptedHexTask's own comment): the base can itself
            // absorb the hex's resource bonus once built (BuildBaseTask.CanMergeIntoResourceSite
            // already lets it merge into an EXISTING facility there — only a still-in-progress
            // claim on the same hex is the actual conflict this closes), so there's no reason two
            // separate hero-led armies should ever converge on one target hex to build mutually
            // exclusive things.
            decision.PreemptedHexTask = AiTaskRegistry.TasksFor(player)
                .FirstOrDefault(t => t.Kind == AiTaskKind.BuildFacility && t.TargetHex.Equals(targetHex.Value));
            results.Add(decision);
            return results;
        }

        // Weakest eligible hero-led combat army — first among idle armies (pool.AvailableArmies(),
        // which already excludes anything claimed by another task this step), then among armies
        // currently running an active RaidWeakerArmy task (`preempted` set only for that second
        // group — redirecting one of those means giving up its raid). 2026-08-24 flip (project
        // owner's own report — "BuildBase всё ещё требует слишком сильную армию"): used to pick
        // the STRONGEST eligible army above a global relative-strength floor (removed — see
        // AiConfig's own comment where buildBaseStrengthToleranceRatio used to live); now picks the
        // WEAKEST one that still clears the composition gates below, so a second base becomes an
        // investment for a spare/mid army instead of a task that outbids Raid/Defence for the best
        // one available. Safety isn't lost — BuildBaseTask.FindTargetHex/HasThreateningEnemyNear
        // still gate the target hex itself via buildBaseMinWinChance, and the per-step feasibility
        // check right after this call still applies.
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
        private static ArmyData FindBuildBaseArmy(PlayerSetupData player, AiResourcePool pool, out AiTask preempted)
        {
            preempted = null;
            ArmyData best = null;
            float bestStrength = float.PositiveInfinity;

            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || !AiArmyRoles.IsHeroLed(army) || !BattleInitiator.IsCombatCapable(army))
                    continue;
                float strength = WorthIt.AttackSum(army) + WorthIt.DefenseSum(army);
                if (strength >= bestStrength)
                    continue;
                bestStrength = strength;
                best = army;
            }

            foreach (AiTask task in AiTaskRegistry.TasksFor(player))
            {
                // An operation-owned raid is off-limits to BuildBase preemption (2026-08-27
                // operations layer) — the operation is the point of this turn's aggression.
                if (task.Kind != AiTaskKind.RaidWeakerArmy || task.Retreating || !task.StillAssembling || task.Army == null
                    || task.OperationId >= 0
                    || !AiArmyRoles.IsHeroLed(task.Army) || !BattleInitiator.IsCombatCapable(task.Army))
                    continue;
                float strength = WorthIt.AttackSum(task.Army) + WorthIt.DefenseSum(task.Army);
                if (strength >= bestStrength)
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
                    return AiTurnController.CanIssueMoveNow(root, player, task.Army, ctx.Map, threat.Value.Hex)
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
            bool willArriveThisTurn = HexGridMath.Distance(task.Army.Hex, task.TargetHex) <= task.Army.MaxMovement;
            // Effective (instance) resource cost — null for a Research/Production-created Base card,
            // in which case TopUp no-ops (its resources were paid at Create, spec §5).
            if (card != null && willArriveThisTurn)
                AiResourceReservation.TopUp(root, player, task, card.EffectivePlayResourceCost);

            if (!task.Army.Hex.Equals(task.TargetHex))
                return AiTurnController.CanIssueMoveNow(root, player, task.Army, ctx.Map, task.TargetHex)
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
            // AiEconomyPlanner.AdvanceEconomyTask's own matching check documents. Effective
            // (instance) cost — a Research/Production-created Base card has no resource cost left
            // to reserve or pay (spec §5), so it is never "short on resources".
            ResourceCost playCost = card.EffectivePlayResourceCost;
            bool shortOnResources = playCost != null
                && (!AiResourceReservation.IsFullyReserved(task, playCost) || !playCost.CanAfford(root));
            bool shortOnAp = !root.CanSpendActionPoints(card.EffectivePlayApCost);
            if (shortOnResources || shortOnAp)
            {
                // Stale-plan timeout (2026-08-23, project owner's own report/spec; turn-boundary fix
                // 2026-08-24 — see AiTask.BuildBaseWaitStartedTurn's own comment). A hero-led combat
                // army sitting here forever, unable to ever actually pay for the base while other AI
                // spending keeps outcompeting it, is a worse outcome than giving up and freeing it
                // back to Raid/Defence — but the wait must be counted in real game turns, not in how
                // many times this candidate happens to get re-evaluated within one turn.
                if (task.BuildBaseWaitStartedTurn < 0)
                    task.BuildBaseWaitStartedTurn = ctx.TurnNumber;
                int waitTurns = ctx.TurnNumber - task.BuildBaseWaitStartedTurn;
                if (waitTurns > AiConfig.buildBaseMaxWaitTurns)
                {
                    AiResourceReservation.Release(task);
                    AiTaskRegistry.Remove(player, task);
                    AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — stuck unable to afford the new base for "
                        + $"{waitTurns} turns, base-building task abandoned, army freed.");
                    return null;
                }
                string reason = shortOnResources && shortOnAp
                    ? "saving up resources and short on AP" : shortOnResources ? "saving up resources" : "short on AP";
                return AiDecision.Wait(task, $"\"{task.Army.Name}\" is on-site, {reason} to found the new base "
                    + $"at ({task.TargetHex.Q},{task.TargetHex.R}) — waiting ({waitTurns}/{AiConfig.buildBaseMaxWaitTurns} real turns)");
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

            // Effective (instance) cost — a Research/Production-created Base card pays
            // activationApCost and skips its already-paid ResourceCost (spec §5).
            root.SpendActionPoints(card.EffectivePlayApCost);
            card.EffectivePlayResourceCost?.PayFrom(root);

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

                // Diagnostic only (2026-08-24 P2, project owner's own playtest report) — Grimm's own
                // new base got an explicit SeedNewBaseGarrison candidate immediately next step, but
                // Vashti's didn't (garrison filled some other way and AdvanceGarrisonSeed found it
                // already non-empty next step instead). Logging the state right here, before
                // AdvanceGarrisonSeed runs at all, tells us on the NEXT report which of the two
                // actually happened instead of guessing after the fact.
                ArmyData newGarrison = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison && a.Hex.Equals(task.TargetHex));
                UnitData seedCandidate = FindGarrisonSeedUnit(army);
                AiDebugLog.Write($"[AI] {player.Nickname}: new base state at ({task.TargetHex.Q},{task.TargetHex.R}) — "
                    + $"garrison units={newGarrison?.Members.Count ?? -1}, builder non-heroes="
                    + $"{army.Members.Count(m => !m.IsHero)}, seed candidate={(seedCandidate != null ? seedCandidate.Name : "no")}.");
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
            List<UnitData> nonHero = builderArmy.Members.Where(m => !m.IsHero && !AbilityParams.UnitHasAnyRecce(m)).ToList();
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

        // ---- Агрессия · Авиация (AiTaskKind.AirStrike) ----
        // Behavioral specifics (target selection, sortie planning) live on AirStrikeTask/
        // AiAviationSupport — this tier only sequences calls into them and turns the results into
        // AiDecision/AiTask, same split every other category here already follows. Never reads or
        // plays a hand card, never recruits — see AirStrikeTask's own class comment.

        // AirStrike/Raid coordination (2026-08-26, project owner's own spec) — which active
        // RaidWeakerArmy task (if any) a given StrikeTarget would actually help, and the WorthIt
        // win-chance swing that raid gets from it. This decision lives entirely HERE, never on
        // AirStrikeTask (see that class's own header comment: composition/scoring only, no
        // coordination logic) — AirStrikeTask hands back raw candidates via FindTargets, this class
        // is the only one that ever reads AiTaskRegistry to relate them to an active raid.
        private readonly struct RaidSupportEvaluation
        {
            public readonly AiTask RaidTask;
            public readonly float WinChanceBefore;
            public readonly float WinChanceAfter;
            public readonly float CoordinationBonus;
            public readonly bool CrossesReadinessThreshold;
            // Repeat-strike spec (2026-08-26) — the estimated win chance if the launch candidate
            // ALSO gets its repeat strike (only ever populated when HasSecondStrike is true, see
            // EvaluateRaidCoordination's own comment); equals WinChanceAfter, and HasSecondStrike
            // stays false, for any candidate that isn't a fuel-margin-eligible multi-turn sortie.
            // Never assumed equal to WinChanceAfter automatically — chained onto the FIRST strike's
            // own expected post-strike roster, since a target can be thinned or wiped by then.
            public readonly float WinChanceAfterSecondStrike;
            public readonly bool HasSecondStrike;
            // Added 2026-08-26 (air-strike scoring rework, spec sections 3/8) — the raid's own
            // WorthIt.BattleEstimate improvement this strike buys, read via RaidWeakerArmyTask.
            // EstimateAgainst before vs after: how much ExpectedSurvivingHpRatioOnWin rises
            // (SurvivalGain) and CriticalAfterBattleChance falls (CriticalReduction). This is the
            // ONLY coordination credit a strike against an already-redundant (95%+ win chance) raid
            // can still earn — see CoordinationBonus's own composition in EvaluateRaidCoordination.
            public readonly float SurvivalGain;
            public readonly float CriticalReduction;

            public RaidSupportEvaluation(AiTask raidTask, float winChanceBefore, float winChanceAfter, float coordinationBonus,
                bool crossesReadinessThreshold, float winChanceAfterSecondStrike, bool hasSecondStrike,
                float survivalGain, float criticalReduction)
            {
                RaidTask = raidTask;
                WinChanceBefore = winChanceBefore;
                WinChanceAfter = winChanceAfter;
                CoordinationBonus = coordinationBonus;
                CrossesReadinessThreshold = crossesReadinessThreshold;
                WinChanceAfterSecondStrike = winChanceAfterSecondStrike;
                HasSecondStrike = hasSecondStrike;
                SurvivalGain = survivalGain;
                CriticalReduction = criticalReduction;
            }
        }

        public static List<AiDecision> TryStartAirStrikeCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            if (AiTaskRegistry.CountActive(player, AiTaskKind.AirStrike) >= AiConfig.maxConcurrentAirStrike)
                return results;

            // Gathered once per call, not per candidate/target — every LaunchCandidate this step
            // judges its own targets against the exact same set of active raids. Excludes: a
            // retreating raid (fleeing, not fighting — see RaidWeakerArmyTask's own "Поведение"
            // comment), a task whose Army was destroyed/never assigned, and a raid targeting a hex
            // that's no longer even a valid target per memory (RaidWeakerArmyTask.
            // IsStillValidTarget) — none of these can meaningfully be "supported" by a strike.
            // BuildBase/Defence/Reconnaissance tasks are excluded implicitly by the Kind filter
            // itself, even if their own army happens to be moving through the same hex.
            List<AiTask> activeRaids = AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.RaidWeakerArmy && t.Army != null && t.Army.Controller != null
                    && t.Army.Members.Count > 0 && !t.Retreating && RaidWeakerArmyTask.IsStillValidTarget(player, t.TargetHex))
                .ToList();

            foreach (AirStrikeTask.LaunchCandidate candidate in AirStrikeTask.FindLaunchCandidates(player, pool))
            {
                if (candidate.ExistingArmy == null && !AiAviationSupport.CanAffordLaunch(root, player, candidate.Aircraft))
                    continue;

                // Every reachable target is weighed here, not just the raw-BaseScore winner — a
                // target that's a few points behind on BaseScore alone can still win once its own
                // coordination bonus is added (2026-08-26 spec section 7: "не выбирать сначала
                // лучший BaseScore, а потом проверять координацию").
                AirStrikeTask.StrikeTarget? bestTarget = null;
                RaidSupportEvaluation bestSupport = default;
                float bestEffectiveBonus = 0f;
                float bestEffectiveSurvivalBonus = 0f;
                float bestFinalScore = 0f;
                foreach (AirStrikeTask.StrikeTarget candidateTarget in AirStrikeTask.FindTargets(player, root, candidate, ctx.Map, ctx.TurnNumber))
                {
                    RaidSupportEvaluation support = EvaluateRaidCoordination(player, candidate, candidateTarget, activeRaids, ctx.Map);
                    // A raid ready to attack NOW can't be credited the same coordination bonus for a
                    // strike that only lands several turns from now (2026-08-26 multi-turn aviation
                    // spec, point 10 — "наземный рейд не должен ждать вертолёт бесконечно"): the
                    // bonus is scaled down by how many extra turns the strike itself still needs,
                    // never zeroed outright (a genuinely decisive future strike can still matter).
                    // The survival-only slice is scaled the same way, tracked separately purely so
                    // BuildAirStrikeReason can print "+ raid ..." and "+ survival ..." as two
                    // distinct log terms instead of one opaque combined number.
                    int extraTurns = Mathf.Max(0, candidateTarget.RequiredTurns - 1);
                    float scale = extraTurns > 0 ? 1f / (1 + extraTurns) : 1f;
                    float survivalBonus = support.SurvivalGain * AiConfig.airStrikeRaidSurvivalWeight
                        + support.CriticalReduction * AiConfig.airStrikeRaidCriticalReductionWeight;
                    float effectiveBonus = support.CoordinationBonus * scale;
                    float effectiveSurvivalBonus = survivalBonus * scale;
                    float finalScore = Mathf.Min(candidateTarget.BaseScore + effectiveBonus, AiConfig.airStrikeScoreCap);
                    if (!bestTarget.HasValue || finalScore > bestFinalScore)
                    {
                        bestTarget = candidateTarget;
                        bestSupport = support;
                        bestEffectiveBonus = effectiveBonus;
                        bestEffectiveSurvivalBonus = effectiveSurvivalBonus;
                        bestFinalScore = finalScore;
                    }
                }

                // FindTargets itself already logs a deduped, reason-broken-down diagnostic
                // ("blocked by AA" / "no complete sortie" / "rejected as ineffective" / "no known
                // targets") whenever it yields nothing — see that method's own comment (2026-08-26
                // P2 fix, "разделить причины отсутствия кандидата AirStrike"); no separate blanket
                // line needed here any more.
                if (!bestTarget.HasValue)
                    continue;

                string reason = BuildAirStrikeReason(bestTarget.Value, bestSupport, bestFinalScore, bestEffectiveBonus,
                    bestEffectiveSurvivalBonus);

                if (candidate.ExistingArmy != null)
                {
                    // A formed-but-never-yet-activated air army (deployed straight onto the map
                    // this same step, or left over from a prior step that never got to move it)
                    // has never been through the ExistingArmy==null CanAffordLaunch check above —
                    // that branch only ever covers a still-STORED group. Without this, IssueMoveOrder
                    // would silently reject the real order later if AP/Energy ran out meanwhile
                    // (2026-08-26 P1 fix, project owner's own report), same gap TryContinueAirStrikeTask
                    // already closes for every step AFTER the first via ContinueSortie's own
                    // CanIssueMoveNow call.
                    if (!AiTurnController.CanIssueMoveNow(root, player, candidate.ExistingArmy, ctx.Map, bestTarget.Value.Hex))
                        continue;
                    var task = new AiTask
                    {
                        Kind = AiTaskKind.AirStrike, Army = candidate.ExistingArmy, TargetHex = bestTarget.Value.Hex,
                        LandingHex = bestTarget.Value.LandingHex, AirOutbound = true,
                        IsMultiTurnSortie = bestTarget.Value.RequiredTurns > 1,
                    };
                    results.Add(AiDecision.Move(candidate.ExistingArmy, bestTarget.Value.Hex, reason, task, bestFinalScore,
                        AiTaskCategory.Aggression));
                }
                else
                {
                    results.Add(AiDecision.LaunchAirStrike(candidate, bestTarget.Value, bestFinalScore, reason));
                }
            }
            return results;
        }

        // Whichever active raid (if any) targets the SAME hex as `target`, plus the honest WorthIt
        // BattleEstimate swing striking it would give that raid — before vs after
        // AviationCombatEstimator.EstimateAirStrike's own expected post-strike roster, both read
        // through RaidWeakerArmyTask.EstimateAgainst (never a second, simplified army-vs-army
        // formula of its own — spec section 3: "все army-vs-army сравнения должны по-прежнему
        // проходить через WorthIt"). Never mutates `raid`, its Army, or AiMapMemory — purely a read:
        // the real target composition only ever changes once the strike actually lands (see
        // AviationCombatPresenter.RunAirStrike) and AiMapMemory re-observes the hex.
        //
        // `target`'s own KnownDefense/KnownDefenders describe only the physical army sighting an
        // air strike can actually hit (AviationCombatPresenter.FindAirStrikeTargetsAt never touches
        // a Hex Event's own card-guard) — RaidWeakerArmyTask.RequiredStrengthAt's threatBefore.
        // Defense can instead be dominated by a stronger EVENT guard sharing the same hex (see its
        // own "two separate fights" comment). When that's the case, striking the army can't lower
        // what the raid actually has to beat, so this raid is skipped entirely rather than crediting
        // a strike for a threat it never touches.
        //
        // 2026-08-26 air-strike scoring rework (spec sections 3/8) — this used to return a flat
        // "already ready, no bonus" zero the instant WinChanceBefore cleared raidMinimumWinChance
        // (0.65); that gate is now airStrikeRaidRedundantWinChance (0.95, a coarser bar — a raid at
        // 70-90% is still genuinely helped by a coordinated strike even though it's already
        // "ready"), and even a strike against an already-redundant raid can still earn survival
        // credit (SurvivalGain/CriticalReduction) if it measurably lowers that raid's own cost of
        // victory, per spec's own "допускается бонус, если он заметно уменьшает риск тяжёлых потерь
        // после победы".
        private static RaidSupportEvaluation EvaluateRaidCoordination(PlayerSetupData player, AirStrikeTask.LaunchCandidate candidate,
            AirStrikeTask.StrikeTarget target, IReadOnlyList<AiTask> activeRaids, HexMap map)
        {
            AiTask bestRaid = null;
            float bestBefore = 0f, bestAfter = 0f, bestAfterSecond = 0f, bestImprovement = float.NegativeInfinity;
            bool bestHasSecondStrike = false;
            float bestSurvivalGain = 0f, bestCriticalReduction = 0f;

            foreach (AiTask raid in activeRaids)
            {
                if (!raid.TargetHex.Equals(target.Hex))
                    continue;

                RaidWeakerArmyTask.ThreatStrength threatBefore = RaidWeakerArmyTask.RequiredStrengthAt(player, target.Hex, map);
                float armyOnlyDefenseBefore = threatBefore.Defense - threatBefore.HexBonus;
                if (target.KnownDefense < armyOnlyDefenseBefore - 0.01f)
                    continue; // a known Hex Event guard outranks the army here — this strike can't touch the real threat

                WorthIt.BattleEstimate estimateBefore = RaidWeakerArmyTask.EstimateAgainst(raid.Army, threatBefore);
                float chanceBefore = estimateBefore.WinChance;

                // Reuses target.Estimate — the exact same first-strike Monte Carlo pass
                // AirStrikeTask.ScoreTarget already ran for its own damage/kill terms — instead of
                // re-simulating an identical strike here (2026-08-26 rework — StrikeTarget.Estimate
                // added specifically for this reuse).
                AviationCombatEstimator.AirStrikeEstimate firstEstimate = target.Estimate;
                bool wipedOutFirst = target.KnownDefenders != null && target.KnownDefenders.Count > 0
                    && firstEstimate.ExpectedDefendersAfter.Count == 0;
                var threatAfterFirst = new RaidWeakerArmyTask.ThreatStrength(firstEstimate.ExpectedDefenseAfter + threatBefore.HexBonus,
                    firstEstimate.ExpectedAttackAfter, firstEstimate.ExpectedDefendersAfter, threatBefore.HexBonus, threatBefore.Name, wipedOutFirst);
                WorthIt.BattleEstimate estimateAfterFirst = RaidWeakerArmyTask.EstimateAgainst(raid.Army, threatAfterFirst);
                float chanceAfterFirst = estimateAfterFirst.WinChance;

                // A repeat strike is a real possibility for ANY candidate — same-turn Sortie or
                // multi-turn — that still has fuel margin left AND could actually land safely next
                // turn from the target hex; never gated on target.RequiredTurns > 1 any more (a
                // same-turn sortie with spare TurnsWithoutRefuel margin can loiter exactly like a
                // multi-turn one — see TryEnterLoiterAtTarget, which never checks RequiredTurns
                // either). This mirrors that method's own three conditions so the estimate never
                // diverges from what actually gets offered once the army is sitting on the hex:
                // fuel margin (SafeUnlandedEndsRemaining), a target still standing after the first
                // strike (wipedOutFirst, already computed above), and a proven same-turn landing
                // next turn without spending this turn's movement (CanStrikeNextTurnAndLand). Real
                // eligibility is still re-verified live once the army is actually there
                // (TryEnterLoiterAtTarget/TryContinueLoiterAtTarget) — this is only ever an ESTIMATE
                // feeding the launch decision's own score, chained onto the FIRST strike's own
                // expected post-strike roster rather than assumed equal to it (spec: "не считать
                // второй удар равным первому автоматически" — units can die, the target can be
                // wiped, between the two).
                bool hasSecondStrike = !wipedOutFirst
                    && AiAviationSupport.SafeUnlandedEndsRemaining(candidate.Aircraft) >= 1
                    && AiAviationSupport.CanStrikeNextTurnAndLand(candidate.Aircraft, target.Hex, candidate.AirfieldHex, map, player, out _);
                float chanceAfterSecond = chanceAfterFirst;
                WorthIt.BattleEstimate estimateAfterSecond = estimateAfterFirst;
                if (hasSecondStrike)
                {
                    AviationCombatEstimator.AirStrikeEstimate secondEstimate = AviationCombatEstimator.EstimateAirStrike(
                        candidate.Aircraft, firstEstimate.ExpectedDefenseAfter, firstEstimate.ExpectedAttackAfter,
                        firstEstimate.ExpectedDefendersAfter);
                    bool wipedOutSecond = firstEstimate.ExpectedDefendersAfter.Count > 0 && secondEstimate.ExpectedDefendersAfter.Count == 0;
                    var threatAfterSecond = new RaidWeakerArmyTask.ThreatStrength(secondEstimate.ExpectedDefenseAfter + threatBefore.HexBonus,
                        secondEstimate.ExpectedAttackAfter, secondEstimate.ExpectedDefendersAfter, threatBefore.HexBonus, threatBefore.Name,
                        wipedOutSecond);
                    estimateAfterSecond = RaidWeakerArmyTask.EstimateAgainst(raid.Army, threatAfterSecond);
                    chanceAfterSecond = estimateAfterSecond.WinChance;
                }

                float improvement = chanceAfterSecond - chanceBefore;
                if (bestRaid == null || improvement > bestImprovement)
                {
                    bestRaid = raid;
                    bestImprovement = improvement;
                    bestBefore = chanceBefore;
                    bestAfter = chanceAfterFirst;
                    bestAfterSecond = chanceAfterSecond;
                    bestHasSecondStrike = hasSecondStrike;
                    bestSurvivalGain = Mathf.Max(0f, estimateAfterSecond.ExpectedSurvivingHpRatioOnWin - estimateBefore.ExpectedSurvivingHpRatioOnWin);
                    bestCriticalReduction = Mathf.Max(0f, estimateBefore.CriticalAfterBattleChance - estimateAfterSecond.CriticalAfterBattleChance);
                }
            }

            if (bestRaid == null)
                return new RaidSupportEvaluation(null, 0f, 0f, 0f, false, 0f, false, 0f, 0f);

            float survivalBonus = bestSurvivalGain * AiConfig.airStrikeRaidSurvivalWeight
                + bestCriticalReduction * AiConfig.airStrikeRaidCriticalReductionWeight;

            // Redundant support (spec section 8) — a raid this far past raidMinimumWinChance has
            // nothing left worth "unlocking"; only the survival credit above still applies, never a
            // bonus for merely sharing a target hex (spec item 5's own "не вытеснять более полезную
            // цель только из-за совпадения координаты").
            if (bestBefore >= AiConfig.airStrikeRaidRedundantWinChance)
                return new RaidSupportEvaluation(bestRaid, bestBefore, bestAfter, survivalBonus, false,
                    bestAfterSecond, bestHasSecondStrike, bestSurvivalGain, bestCriticalReduction);

            float clampedImprovement = Mathf.Max(0f, bestAfterSecond - bestBefore);
            float bonus = clampedImprovement > 0f
                ? AiConfig.airStrikeRaidSupportBaseBonus + clampedImprovement * AiConfig.airStrikeRaidSupportChanceWeight
                : 0f;
            bool crosses = bestBefore < AiConfig.raidMinimumWinChance && bestAfterSecond >= AiConfig.raidMinimumWinChance;
            if (crosses)
                bonus += AiConfig.airStrikeRaidThresholdCrossBonus;
            bonus += survivalBonus;

            return new RaidSupportEvaluation(bestRaid, bestBefore, bestAfter, bonus, crosses, bestAfterSecond, bestHasSecondStrike,
                bestSurvivalGain, bestCriticalReduction);
        }

        // The full diagnostic Reason for an AirStrike candidate — composed exactly once per
        // candidate, here, never as a separate AiDebugLog.Write per losing candidate (spec's own
        // "Лог не должен печатать отдельную строку для каждого отвергнутого кандидата"; the
        // standard per-step candidate dump and the "decided ..." line already surface this same
        // string for every candidate/the actual winner respectively — see
        // AiTurnController.DescribeCandidates/RunTurn).
        //
        // 2026-08-26 air-strike scoring rework (spec's own "Логирование" section) — replaces the
        // old prose sentence with an explicit additive breakdown (base/damage/kill/raid/urgency
        // minus route/resource costs) so a log reader can see EXACTLY why this strike outranked
        // (or lost to) everything else, not just the final number. Kept as one embeddable clause —
        // same single-line convention every other AiDecision.Reason in this codebase already
        // follows (AiTurnController appends ". " after it) — rather than the spec's own illustrative
        // multi-line example, which would break that shared rendering convention.
        //
        // effectiveBonus/effectiveSurvivalBonus — support's own CoordinationBonus/survival slice
        // already scaled down for a multi-turn strike's own delay (see
        // TryStartAirStrikeCandidates' own comment); reported here instead of the raw
        // support fields so the printed numbers always match what actually got added to BaseScore.
        private static string BuildAirStrikeReason(AirStrikeTask.StrikeTarget target, RaidSupportEvaluation support,
            float finalScore, float effectiveBonus, float effectiveSurvivalBonus)
        {
            AirStrikeTask.ScoreBreakdown b = target.Breakdown;
            string missionPrefix = target.RequiredTurns > 1
                ? $"{target.RequiredTurns}-turn helicopter sortie, action reached "
                    + (target.MultiTurn.HasValue && target.MultiTurn.Value.ReachesActionThisTurn ? "this turn, " : "later, ")
                    + $"{target.RequiredUnlandedEnds} safe unlanded end(s) required, landing "
                    + $"({target.LandingHex.Q},{target.LandingHex.R}) — "
                : $"same-turn sortie, landing ({target.LandingHex.Q},{target.LandingHex.R}) — ";

            string title = support.RaidTask == null
                ? $"air strike on {target.TargetName}"
                : $"air strike supports \"{support.RaidTask.Army.Name}\" raid on {target.TargetName}";

            var terms = new List<string>
            {
                $"base {b.Base:0}",
                $"+ damage {b.DamageValue:0} ({b.DamageFraction:P0} HP)",
                $"+ kill {b.KillValue:0} ({b.KillAnyProbability:P0} any kill)",
            };

            if (support.RaidTask == null)
            {
                terms.Add("+ raid 0 (no active raid on this hex)");
            }
            else
            {
                float raidDeltaBonus = effectiveBonus - effectiveSurvivalBonus;
                string thresholdPart = support.CrossesReadinessThreshold ? $", crosses {AiConfig.raidMinimumWinChance:P0}" : string.Empty;
                string afterPart = support.HasSecondStrike
                    ? $"{support.WinChanceBefore:P0}→{support.WinChanceAfter:P0}→{support.WinChanceAfterSecondStrike:P0}"
                    : $"{support.WinChanceBefore:P0}→{support.WinChanceAfter:P0}";
                terms.Add($"+ raid {raidDeltaBonus:0} ({afterPart}{thresholdPart})");
                if (effectiveSurvivalBonus > 0.01f)
                    terms.Add($"+ survival {effectiveSurvivalBonus:0}");
            }

            terms.Add($"+ urgency {b.UrgencyValue:0}{(b.IsCitadelUrgency ? " (citadel threat)" : string.Empty)}");
            terms.Add($"- route {b.RouteCost:0}");
            // Energy forecast detail (2026-08-26 P1 fix, "last Energy" planner/executor parity) —
            // shows Energy before/predicted cost/after so a "resources 0" line can be told apart
            // from a genuine "already activated, nothing left to spend" case instead of reading as
            // the same thing (the reported bug: a formed group's real 2→0 first-launch spend used
            // to print "resources 0" here with no forecast at all).
            string resourcesDetail = b.EnergyAlreadyPaid
                ? " (already activated this turn, no new Energy spend)"
                : b.PredictedEnergyCost > 0.01f
                    ? $" (energy {b.EnergyBefore:0.#}→{b.EnergyAfter:0.#}, cost {b.PredictedEnergyCost:0.#}"
                        + $"{(b.ResourceScarcityCost > 0.01f ? ", uses last Energy" : string.Empty)})"
                    : string.Empty;
            terms.Add($"- resources {b.ResourceScarcityCost:0}{resourcesDetail}");

            float uncappedTotal = b.Total + effectiveBonus;
            string totalPart = finalScore < uncappedTotal - 0.01f
                ? $"= {uncappedTotal:0}, capped to {finalScore:0}"
                : $"= {finalScore:0}";

            return $"{title} — {missionPrefix}{string.Join(" ", terms)} {totalPart}";
        }

        // Advances an already-committed AirStrike sortie — re-validates the sortie every step (per
        // spec: a task must recheck before it launches OR MOVES), advances whichever leg is active,
        // and flips AirOutbound/repoints TargetHex once the outbound leg is done. Completes the
        // moment the army sits on its own LandingHex — end-turn fuel/landing stays owned entirely by
        // AviationTurnLifecycle, this task never touches it.
        public static AiDecision TryContinueAirStrikeTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task, AiResourcePool pool)
        {
            if (task.Army != null)
            {
                // The outbound leg just landed the army on its own ActionHex — decide, exactly once
                // per sortie, whether this is worth holding position for a repeat strike, or an
                // ordinary sortie that turns for home immediately (repeat-strike spec, point 2).
                // Reaching ActionHex is NOT the same as having struck it: AviationCombatPresenter.
                // ResolveAirArmyStep resolves a strike at EVERY hex the army actually enters along
                // its path, not just the final one, so a target sighted on an earlier hex this same
                // turn can already have used up every aircraft's HasAirAttackedThisTurn before the
                // army ever reaches ActionHex — in which case nothing here was ever struck and there
                // is no "first strike" to hold position and repeat. ArmyData.LastAirStrikeHex/
                // LastAirStrikeAttacked (set by that same resolver, ground truth from the actual
                // combat step, not inferred from position) are what distinguish "arrived" from
                // "struck" — only the latter ever starts the loiter/repeat flow or counts toward
                // AiTask.AirStrikesCompleted.
                if (task.AirMissionPhase == AiAirMissionPhase.ToAction && task.Army.Hex.Equals(task.TargetHex))
                {
                    bool struckActionHex = task.Army.LastAirStrikeAttacked
                        && task.Army.LastAirStrikeHex.HasValue && task.Army.LastAirStrikeHex.Value.Equals(task.TargetHex);
                    if (struckActionHex)
                    {
                        task.AirStrikesCompleted = Mathf.Max(task.AirStrikesCompleted, 1);
                        if (TryEnterLoiterAtTarget(player, ctx, task))
                            return null; // logged inside; nothing to move this step
                    }
                    // Loiter denied, or nothing was ever struck here — falls through to
                    // ContinueSortie below, which still contains the ordinary "outbound leg
                    // finished -> Returning" flip (AirMissionPhase is still ToAction here, so that
                    // transition fires exactly as it always has). A sortie that already spent its
                    // attack on an earlier hex this turn is never described as loitering for a
                    // "repeat" strike it never actually landed once here.
                }
                else if (task.AirMissionPhase == AiAirMissionPhase.LoiterAtTarget)
                {
                    AiDecision repeat = TryContinueLoiterAtTarget(player, ctx, task);
                    if (task.AirMissionPhase == AiAirMissionPhase.LoiterAtTarget)
                        return repeat; // still safely waiting (a real repeat-strike candidate, or
                                       // null — nothing to do until next turn's own attack reset)
                    // else: TryContinueLoiterAtTarget itself just flipped the phase to Returning —
                    // fall through to ContinueSortie below to actually start the trip home THIS step.
                }
            }

            return AiAviationSupport.ContinueSortie(player, root, ctx, task, "AirStrike", "presses on toward the strike target",
                AiConfig.airStrikeContinuationScore, AiTaskCategory.Aggression);
        }

        // Called exactly once per sortie, the moment the outbound leg lands on ActionHex — decides
        // whether this AirStrike should hold position for a repeat strike next turn rather than
        // turn for home immediately (repeat-strike spec, point 2). Every condition is re-derived
        // live off the army's OWN current state/hex, never off the plan that got it here:
        //   - SafeUnlandedEndsRemaining margin left (a plane, margin 0, can never qualify — same
        //     "planes keep their existing single-turn model" rule the multi-turn spec itself set).
        //   - a real, still-standing target on the hex (ground truth via AviationCombatPresenter.
        //     FindAirStrikeTargetsAt, not fogged memory — the army is physically there right now).
        //   - a proven safe landing NEXT turn without moving this turn (CanStrikeNextTurnAndLand).
        //   - the sortie hasn't already used up its AiConfig.maxStrikesPerSortie strikes.
        private static bool TryEnterLoiterAtTarget(PlayerSetupData player, AiTurnContext ctx, AiTask task)
        {
            if (task.AirStrikesCompleted >= AiConfig.maxStrikesPerSortie)
                return false;
            if (AiAviationSupport.SafeUnlandedEndsRemaining(task.Army.Members) < 1)
                return false;

            List<ArmyData> remaining = AviationCombatPresenter.FindAirStrikeTargetsAt(task.Army.Hex, player);
            float remainingValue = remaining.Sum(a => a.Members.Sum(u => u.Defense + u.Attack));
            if (remainingValue < AiConfig.airStrikeRepeatMinTargetValue)
                return false; // first strike cleared the hex (or never found anyone) — nothing to repeat

            if (!AiAviationSupport.CanStrikeNextTurnAndLand(task.Army, task.Army.Hex, ctx.Map, player, out HexCoord landingHex))
                return false;

            task.AirMissionPhase = AiAirMissionPhase.LoiterAtTarget;
            task.LandingHex = landingHex;
            AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — first AirStrike completed at "
                + $"({task.Army.Hex.Q},{task.Army.Hex.R}) — holds over the target for one repeat strike next turn; "
                + $"{AiAviationSupport.SafeUnlandedEndsRemaining(task.Army.Members)} safe unlanded end(s) available, "
                + $"landing ({landingHex.Q},{landingHex.R}) reserved.");
            return true;
        }

        // Every step while LoiterAtTarget — re-validates all the same conditions TryEnterLoiterAtTarget
        // checked (spec point 7: "повторная проверка перед вторым ударом", never a one-time decision)
        // before ever proposing the repeat strike. The moment any of them fails, immediately flips to
        // Returning and returns null so the caller falls through to ContinueSortie's own home-bound
        // move THIS SAME step — never lingers a turn longer than it has to once repeating stops being
        // safe or worthwhile.
        private static AiDecision TryContinueLoiterAtTarget(PlayerSetupData player, AiTurnContext ctx, AiTask task)
        {
            List<ArmyData> remaining = AviationCombatPresenter.FindAirStrikeTargetsAt(task.Army.Hex, player);
            float remainingValue = remaining.Sum(a => a.Members.Sum(u => u.Defense + u.Attack));
            bool targetGone = remainingValue < AiConfig.airStrikeRepeatMinTargetValue;
            bool strikesExhausted = task.AirStrikesCompleted >= AiConfig.maxStrikesPerSortie;

            bool canReturn = AiAviationSupport.CanStrikeNextTurnAndLand(task.Army, task.Army.Hex, ctx.Map, player, out HexCoord landingHex);

            if (targetGone || strikesExhausted || !canReturn)
            {
                task.AirMissionPhase = AiAirMissionPhase.Returning;
                if (canReturn)
                    task.LandingHex = landingHex;
                task.TargetHex = task.LandingHex;
                string why = targetGone ? "target destroyed or left the hex"
                    : strikesExhausted ? "repeat-strike limit reached"
                    : "safe same-turn landing is no longer guaranteed";
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — repeat AirStrike cancelled — {why}; "
                    + $"returns to airfield ({task.LandingHex.Q},{task.LandingHex.R}).");
                return null;
            }

            if (!task.Army.Members.Any(u => !u.HasAirAttackedThisTurn))
                return null; // still the same turn as the last strike — wait for next turn's reset

            task.LandingHex = landingHex; // keep the reserved landing fresh even while still loitering

            // 2026-08-26 rework (spec section 7) — the repeat's own score now comes from the same
            // base+damage+kill(+urgency) formula ScoreTarget uses for a fresh candidate, evaluated
            // against the REAL roster still standing on the hex, instead of the old flat
            // airStrikeRepeatScore constant. A repeat against a real, still-dangerous remnant scores
            // in the normal AirStrike band or higher; one against an already-thinned remnant falls
            // toward airStrikeBaseWeight — the "natural falloff" the spec explicitly allows in place
            // of a new strike-history subsystem.
            List<WorthIt.DefenderProfile> defenders = remaining.SelectMany(a => a.Members).Select(WorthIt.FromLiveUnit).ToList();
            string targetName = string.Join(", ", remaining.Select(a => a.Name));
            var scored = AirStrikeTask.ScoreRepeatStrike(player, task.Army.Members, task.Army.Hex, defenders, targetName);
            if (!scored.HasValue)
            {
                // P1 gate (2026-08-26, "исключить авиаудары с нулевой ожидаемой эффективностью") —
                // the ground-truth remnant is too thinned/tough for this roster to meaningfully
                // damage or kill any more (ScoreSelfValue's own rejection — no separate log read
                // from it here, see AirStrikeTask.ScoreRepeatStrike's own comment on why: the
                // unconditional line right below is this path's only rejection notice, and this
                // task transitions out of the loiter phase this same step, so it never re-fires for
                // the same still-unchanged rejection). Falls through to Returning exactly like every
                // other repeat-cancelled condition above, never lingers loitering on a hex it can no
                // longer usefully hit.
                task.AirMissionPhase = AiAirMissionPhase.Returning;
                task.LandingHex = landingHex;
                task.TargetHex = task.LandingHex;
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — repeat AirStrike cancelled — "
                    + $"no meaningful expected effect left on the remnant; returns to airfield ({landingHex.Q},{landingHex.R}).");
                return null;
            }
            (AirStrikeTask.ScoreBreakdown breakdown, _) = scored.Value;
            float score = Mathf.Min(breakdown.Total, AiConfig.airStrikeScoreCap);

            AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — repeats AirStrike — air attack "
                + $"refreshed for the new turn; must return to airfield ({landingHex.Q},{landingHex.R}) this turn.");
            string reason = $"repeats the air strike at ({task.Army.Hex.Q},{task.Army.Hex.R}) — base {breakdown.Base:0} "
                + $"+ damage {breakdown.DamageValue:0} ({breakdown.DamageFraction:P0} HP) + kill {breakdown.KillValue:0} "
                + $"({breakdown.KillAnyProbability:P0} any kill) + urgency {breakdown.UrgencyValue:0} = {score:0}; "
                + $"before returning to ({landingHex.Q},{landingHex.R})";
            return AiDecision.RepeatAirStrike(task, score, reason);
        }

        // Executes AiActionKind.ExecuteAirStrikeAtCurrentHex — the army never moves; this only
        // invokes the shared Game.Aviation.AviationActions.ResolveStationaryStrike action (2026-08-26
        // consistency follow-up — the mechanic itself lives there now, not here, so a future human
        // control can call the exact same two methods; AiAggressionPlanner only ever decides WHEN to
        // use it, via TryContinueLoiterAtTarget above). Immediately advances the task to Returning
        // afterward — a repeat strike is never followed by yet another one in the same decision (the
        // max-strikes cap and the fresh safety recheck both already happen in
        // TryContinueLoiterAtTarget before this ever runs). AirStrikesCompleted only increments if
        // an aircraft actually fired — TryContinueLoiterAtTarget already re-verified a live target
        // and a fresh attack immediately before proposing this decision, but the count must still
        // reflect what really happened, never what was merely attempted.
        public static IEnumerator RepeatAirStrikeRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            AiTask task = decision.Task;
            AviationCombatPresenter presenter = ctx.HexSelection?.AviationCombatPresenter;
            if (task?.Army == null || presenter == null)
                yield break;

            var result = new AviationCombatPresenter.AirStrikeResult();
            yield return AviationActions.ResolveStationaryStrike(presenter, task.Army, result);

            if (result.Attacked)
                task.AirStrikesCompleted++;
            task.AirMissionPhase = AiAirMissionPhase.Returning;
            task.TargetHex = task.LandingHex;
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
