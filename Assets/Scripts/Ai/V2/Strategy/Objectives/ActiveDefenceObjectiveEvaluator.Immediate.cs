using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ImmediateOpportunity — a weak field army a free defender can reach and START a fight with in
    // this very own turn: its enumeration, the strike proof, exposure after the fight, its trace.
    public static partial class ActiveDefenceObjectiveEvaluator
    {
        // ---- IMMEDIATE OPPORTUNITY ---------------------------------------------------------------
        // A weak field army the strategic threat model does not list (severity cutoff, no potential
        // damage, power below activeDefenceMinEnemyPower) is still a legitimate fight when a defender
        // can reach it and START the battle in this very own turn. This is deliberately a separate
        // enumeration, never a loosened Enumerate: Enumerate keeps its strategic meaning for Attack's
        // local reaction, Demand, aviation rebase and air support. Candidates come from the current
        // honest contacts (visible NOW, or last seen on an own structure lost since our last turn —
        // OnFreshOwnRuin; also a canonical Known.EnemySightings entry) — never from
        // Threat.Threats, which the severity cutoff already thinned, and never from a hidden registry.
        // The protected asset only anchors scoring (nearest own Base/Citadel): no damage is invented.
        public static List<ActiveDefenceObjective> EnumerateImmediate(WorldSnapshot snap)
        {
            var result = new List<ActiveDefenceObjective>();
            if (snap?.Threat?.Contacts == null || snap.Self?.BaseHexes == null || snap.Observer == null)
                return result;
            var known = AiV2Util.KnownArmyIds(snap.Known?.EnemySightings
                ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>());
            var seen = new HashSet<int>();
            foreach (EnemyContactSnapshot c in snap.Threat.Contacts.Where(c => c?.Army != null)
                .OrderBy(c => c.Army.ArmyId))
            {
                int id = c.Army.ArmyId;
                if (!ImmediatePositionHonest(snap, c) || id < 0
                    || c.Army.IsAir || c.Army.Owner == null || c.Army.Owner.IsNeutral
                    || c.Army.Owner == snap.Observer || c.Army.Members == null
                    || c.Army.Members.Count == 0 || !known.Contains(id) || !seen.Add(id))
                    continue;
                HexCoord position = c.Position.Value;
                if (BlocksImmediateIntercept(snap, position))
                    continue;
                StrategicAssetSnapshot asset = NearestOwnBaseAsset(snap, position);
                if (asset == null)
                    continue;
                // 2026-11-07 (owner, playtest Mordak/Ysolde) — the damage kept off our home is the
                // force this fight takes off the map: the enemy's power on the scale of our own
                // strike force (SelfSnapshot.AttackPeak). Without it a 17-power army beside a
                // captured Base and a lone scout were worth the same WinChance, and the larger
                // target lost to a mover price (Ysolde T24-T25). The win chance stays its own slot.
                float enemyPower = Mathf.Max(0f, c.Army.EffectiveArmyPower);
                float strikeScale = Mathf.Max(1f, Mathf.Max(snap.Self.AttackPeak, enemyPower));
                float preventedDamage = TaskScoreEvaluator.PreventedDamage(enemyPower / strikeScale);
                // 2026-11-19 (owner, log 2026-11-19 Halden T9) — a weak saboteur beside an own
                // structure it can take before our next turn is worth that structure too: the
                // strategic Intercept never lists it (power bar, Base/Citadel only), so without it
                // a 4.7-power scout next to a fresh extractor was priced as a stray contact. The
                // asset terms are the strategic score's own (AssetDefenceTerms); the damage slot
                // keeps the larger of the two readings, never their sum.
                float relevance = 0f, threatDirection = 0f;
                AssetThreatSnapshot structure = ThreatenedOwnStructure(snap, id);
                if (structure != null)
                {
                    AssetDefenceTerms(structure, out relevance, out threatDirection,
                        out float structureDamage);
                    preventedDamage = Mathf.Max(preventedDamage, structureDamage);
                }
                TaskScore score = new TaskScore(
                    strategicRelevance: relevance,
                    threatDirection: threatDirection,
                    economicHexBenefit: ExtractorDenial(snap, position),
                    ownTerritoryProximity: TaskScoreEvaluator.LeashedProximity(
                        TaskScoreEvaluator.NearestOwnedHomeDistance(snap, position),
                        AiConfigV2.activeDefenceLeashHexes, AiConfigV2.taskScoreActiveDefenceLeashPerHex),
                    preventedDamage: preventedDamage);
                result.Add(new ActiveDefenceObjective
                {
                    TaskScore = score,
                    Target = new ActiveDefenceMissionTarget
                    {
                        Phase = ActiveDefencePhase.Intercept,
                        InterceptPurpose = ActiveDefenceInterceptPurpose.ImmediateOpportunity,
                        ImmediateTurn = snap.TurnNumber,
                        EnemyArmyId = id,
                        LastKnownHex = position,
                        LastObservedTurn = c.LastObservedTurn,
                        Confidence = c.Confidence,
                        ProtectedAssetHex = asset.Hex,
                        ProtectedAssetKind = asset.Kind,
                        ProtectedAssetValue = asset.Value,
                    },
                });
            }
            return result.OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.Target.EnemyArmyId).ToList();
        }

        // The own structure (Citadel, Base or Facility — never an army) this contact can take
        // before our next turn (the threat model's own pair: PotentialDamage > 0, EnemyEta within
        // immediateStructureThreatMaxEnemyEta) whose loss is the largest; null when none.
        private static AssetThreatSnapshot ThreatenedOwnStructure(WorldSnapshot snap, int enemyArmyId)
        {
            AssetThreatSnapshot best = null;
            float bestLoss = 0f;
            foreach (AssetThreatSnapshot t in snap?.Threat?.Threats
                ?? (IReadOnlyList<AssetThreatSnapshot>)Array.Empty<AssetThreatSnapshot>())
            {
                if (t?.Asset == null || t.Contact?.Army == null || t.Contact.Army.ArmyId != enemyArmyId
                    || t.Asset.Kind == AssetKind.Army || t.PotentialDamage <= 0f
                    || !t.EnemyEta.HasValue || t.EnemyEta.Value > AiConfigV2.immediateStructureThreatMaxEnemyEta)
                    continue;
                float loss = t.Asset.Value * Mathf.Clamp01(t.PotentialDamage);
                if (best == null || loss > bestLoss
                    || loss == bestLoss && (t.Asset.Hex.Q < best.Asset.Hex.Q
                        || t.Asset.Hex.Q == best.Asset.Hex.Q && t.Asset.Hex.R < best.Asset.Hex.R))
                {
                    best = t;
                    bestLoss = loss;
                }
            }
            return best;
        }

        // 2026-11-05 (owner) — the saboteur of a structure we lost since our last turn, last seen on
        // that ruin. Destroying ends its movement and it cannot re-enter stealth before its next
        // turn, so its last-known hex is honest now even unseen (fog): our own loss record
        // (AiMapMemory.LostStructureSince) plus its own sighting, never a TrueWorld read.
        // Ashen T14 (2026-11-05): Steel Locusts razed two extractors and stood on (3,-3) while a
        // free army waited at the Citadel; only a visible contact was a candidate.
        // The ONE rule, read by planning (here, beside ContactKnowledge.Exact) and by the live tiers
        // that re-prove an immediate intercept (ActiveDefenceProvisioner, ActiveDefenceExecutor,
        // beside VisionSystem.IsVisible): memory only, so it is legal in every layer.
        internal static bool OnFreshOwnRuin(PlayerSetupData observer, int turn, HexCoord hex,
            int lastObservedTurn) =>
            observer != null && lastObservedTurn >= turn - 1
            && AiMapMemory.LostStructureSince(observer, hex, turn - 1);

        private static bool OnFreshOwnRuin(WorldSnapshot snap, EnemyContactSnapshot c) =>
            c.Position.HasValue
            && OnFreshOwnRuin(snap.Observer, snap.TurnNumber, c.Position.Value, c.LastObservedTurn);

        // The ONE snapshot-tier honesty rule of an immediate target (field intercept AND garrison
        // sortie): in sight now, or OnFreshOwnRuin. Playtest 2026-11-06: the assessment and both
        // sortie readers still demanded Exact, so five saboteurs on fresh ruins were deferred.
        internal static bool ImmediatePositionHonest(WorldSnapshot snap, EnemyContactSnapshot c) =>
            c?.Position != null && (c.Knowledge == ContactKnowledge.Exact || OnFreshOwnRuin(snap, c));

        private static StrategicAssetSnapshot NearestOwnBaseAsset(WorldSnapshot snap, HexCoord from)
        {
            StrategicAssetSnapshot best = null;
            foreach (StrategicAssetSnapshot a in snap.Threat?.Assets ?? (IReadOnlyList<StrategicAssetSnapshot>)
                Array.Empty<StrategicAssetSnapshot>())
                if (a != null && IsDefendableAsset(snap, a.Kind, a.Hex)
                    && (best == null || CompareAssetDistance(from, a.Hex, best.Hex) < 0))
                    best = a;
            if (best != null)
                return best;
            // Fixtures / snapshots without an asset list: the own base hex alone anchors the score.
            foreach (HexCoord h in snap.Self.BaseHexes)
                if (best == null || CompareAssetDistance(from, h, best.Hex) < 0)
                    best = new StrategicAssetSnapshot { Hex = h, Kind = AssetKind.Base };
            return best;
        }

        private static int CompareAssetDistance(HexCoord from, HexCoord a, HexCoord b)
        {
            int byDistance = HexGridMath.Distance(from, a).CompareTo(HexGridMath.Distance(from, b));
            if (byDistance != 0) return byDistance;
            return a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R);
        }

        // 2026-11-19 (owner) — THE proof Continuity reads before it lends an army walking a routine
        // return leg (or an outbound build convoy whose site an enemy holds) to an immediate strike
        // (MissionContinuityLayer.StrikeLoan): the first of `objectives` (EnumerateImmediate,
        // best first) that passes `accept`, is not already answered by a live Intercept, and that
        // this one army can START now — AssessImmediate with the army pinned, so its reach, the
        // shared win gate, the pinned-stronghold rule and LeavesForceExposed are the planner's own.
        // `committed` is the planner's view of the other owners (the lending intent left out).
        internal static ActiveDefenceObjective ImmediateStrikeWith(WorldSnapshot snap, int armyId,
            IReadOnlyList<ActiveDefenceObjective> objectives, ISet<int> committed,
            ICollection<int> withdrawing, IEnumerable<MissionIntent> activeIntents,
            Func<ActiveDefenceObjective, bool> accept = null)
        {
            foreach (ActiveDefenceObjective objective in objectives
                ?? (IReadOnlyList<ActiveDefenceObjective>)Array.Empty<ActiveDefenceObjective>())
            {
                if (objective == null || accept != null && !accept(objective)
                    || IncumbentIntercept(activeIntents, objective.Target.EnemyArmyId) != null)
                    continue;
                ActiveDefenceResponse response = AssessImmediate(snap, objective, committed,
                    withdrawing, armyId);
                if (response?.Kind == ActiveDefenceResponseKind.Intercept
                    && response.Plan?.BaseArmyId == armyId)
                    return objective;
            }
            return null;
        }

        // The immediate answer: an Intercept the chosen roster can START now, or a Defer. Never
        // Regroup / Shortage / Return. asset_holds, EnemyEta and the strategic arrival deadline are
        // deliberately not consulted — they judge protecting an asset, not whether to strike now.
        // Everything that owns the army stays: committed claims, withdrawals, the pinned stronghold
        // defender and the shared WorthIt admission of GroundCombatAssemblyPlanner.
        private static ActiveDefenceResponse AssessImmediate(WorldSnapshot snap,
            ActiveDefenceObjective objective, ISet<int> committed, ICollection<int> withdrawing,
            int? pinnedActor)
        {
            if (snap?.Self?.Armies == null || objective == null
                || !IsDefendableAsset(snap, objective.Target.ProtectedAssetKind,
                    objective.Target.ProtectedAssetHex))
                return null;
            int enemyId = objective.Target.EnemyArmyId;
            IReadOnlyList<WorthIt.DefendingArmy> opposition = KnownOpposition.OfEnemyArmy(snap, enemyId);
            if (opposition == null || BlocksImmediateIntercept(snap, objective.Target.LastKnownHex))
                return null;
            var response = new ActiveDefenceResponse { Opposition = opposition };
            EnemyContactSnapshot contact = snap.Threat?.Contacts?.FirstOrDefault(c => c?.Army != null
                && c.Army.ArmyId == enemyId && c.Position.HasValue);
            HexCoord targetHex = objective.Target.LastKnownHex;
            // The admission rule of EnumerateImmediate (ImmediatePositionHonest).
            if (!ImmediatePositionHonest(snap, contact) || !contact.Position.Value.Equals(targetHex))
                return TraceImmediate(snap, objective, pinnedActor, response, ActiveDefenceResponseKind.Defer,
                    "immediate_contact_not_visible", -1, 0, 0);

            var planExcluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            if (withdrawing != null) planExcluded.UnionWith(withdrawing);
            if (pinnedActor.HasValue) planExcluded.Remove(pinnedActor.Value);
            // 2026-11-15 — a defender held home only by this enemy may strike it now.
            foreach (ArmySnapshot a in snap.Self.Armies.Where(GroundCombatActorEligibility.IsStructuralActor))
                if (IsPinnedStrongholdDefender(snap, a, committed, ignoreEnemyArmyId: enemyId)
                    || !CanReachNow(snap, a, targetHex, out _))
                    planExcluded.Add(a.ArmyId);

            // The planner may pick an assembly that drags the formation below the needed movement:
            // the chosen roster is re-proved and a failing host is excluded for the next try.
            // The same for a formation the fight would leave exposed (LeavesForceExposed).
            string exposed = null;
            // 2026-11-20 (match Halden vs Orlan P4) - why the last try failed, for the trace: the
            // single "not reachable or not capable" hid whether no roster cleared the fight or the
            // roster that did could not reach (Orlan T23-T26 vs saboteur #28, root unprovable).
            string lastWhy = null;
            int unreachableNow = 0;
            foreach (int id in planExcluded)
                if (committed == null || !committed.Contains(id)) unreachableNow++;
            for (int attempt = 0; attempt <= snap.Self.Armies.Count; attempt++)
            {
                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                    new GroundCombatAssemblyRequest
                    {
                        Opposition = opposition,
                        AllowHeroDisplacement = true,
                        WinChanceGate = GroundCombatAdmissionPolicy.PinnedOrFreshGate(pinnedActor.HasValue),
                        PreferredPrimaryArmyId = pinnedActor,
                        PinToPreferred = pinnedActor.HasValue,
                        ExcludedArmyIds = planExcluded,
                    });
                if (!plan.Feasible)
                {
                    lastWhy = "no_roster_clears:" + (plan.Reason ?? "-");
                    break;
                }
                ArmySnapshot host = snap.Self.Armies.FirstOrDefault(a => a != null
                    && a.ArmyId == plan.BaseArmyId);
                if (host == null)
                    break;
                int projectedMax = GroundCombatAssemblyPlanner.ProjectedMaxMovement(snap, plan)
                    ?? host.MaxMovement;
                int projectedNow = GroundCombatAssemblyPlanner.ProjectedCurrentMovement(snap, plan)
                    ?? host.CurrentMovement;
                if (CanReachNow(snap, host, targetHex, out int routeCost, projectedNow, projectedMax))
                {
                    ArmySnapshot formation = GroundCombatAssemblyPlanner.ProjectedDepartureArmies(snap, plan)
                        .TryGetValue(plan.BaseArmyId, out ArmySnapshot projected) ? projected : host;
                    if (LeavesForceExposed(snap, formation.Members, formation.Commander, targetHex,
                            enemyId, out int exposerId, out float exposerWin))
                    {
                        exposed = $"#{host.ArmyId} vs #{exposerId} win {exposerWin:0.00}";
                        if (pinnedActor.HasValue && plan.BaseArmyId == pinnedActor.Value)
                            break;
                        planExcluded.Add(plan.BaseArmyId);
                        continue;
                    }
                    response.Plan = plan;
                    response.ExcludedArmyIds = planExcluded;
                    return TraceImmediate(snap, objective, pinnedActor, response, ActiveDefenceResponseKind.Intercept,
                        "immediate_response", host.ArmyId, projectedNow, routeCost, plan.ProjectedWinChance);
                }
                lastWhy = $"host_#{host.ArmyId}_cannot_reach(mp {projectedNow}/{projectedMax})";
                if (pinnedActor.HasValue && plan.BaseArmyId == pinnedActor.Value)
                    break;
                planExcluded.Add(plan.BaseArmyId);
            }
            // 2026-11-19 (owner) — a sortie is a field army formed from the garrison, so it may
            // displace a hero-only contact like any other field army (CanDisplaceHeroes).
            if (!pinnedActor.HasValue)
            {
                response.Sortie = PlanGarrisonSortie(snap, objective, opposition, planExcluded);
                ArmySnapshot detachment = response.Sortie?.Detachment;
                if (detachment != null && LeavesForceExposed(snap, detachment.Members,
                        detachment.Commander, targetHex, enemyId, out int exposerId, out float exposerWin))
                {
                    exposed = $"sortie #{detachment.ArmyId} vs #{exposerId} win {exposerWin:0.00}";
                    response.Sortie = null;
                }
                if (response.Sortie != null)
                    return TraceImmediate(snap, objective, pinnedActor, response, ActiveDefenceResponseKind.Intercept,
                        "immediate_garrison_sortie", response.Sortie.Detachment.ArmyId,
                        response.Sortie.Detachment.CurrentMovement, response.Sortie.RouteCost, response.Sortie.WinChance);
            }
            return TraceImmediate(snap, objective, pinnedActor, response, ActiveDefenceResponseKind.Defer,
                exposed != null ? "immediate_force_exposed_after_fight:" + exposed
                    : "immediate_not_reachable_or_not_capable:" + (lastWhy ?? "no_candidate")
                        + $" excludedPinnedOrOutOfReach={unreachableNow}", -1, 0, 0);
        }

        // 2026-11-07 (owner, playtest Mordak T19) — a voluntary one-turn fight must not leave the
        // force where it at once needs the safe withdrawal WithdrawalThreats orders: a known hostile
        // player army other than the struck one that reaches the battle hex within `maxEnemyEta`
        // turns (activeDefenceWithdrawMaxEnemyEta; Raid passes its own horizon, RaidPostFightPolicy), can damage the formation and beats it above
        // 1 - activeDefenceHoldWinChance. The same threat facts the Army asset is measured with
        // (WorldAnalysis.ContactApproachCost, the shared estimator, no hex bonus in the open);
        // wounds of the won fight are not subtracted, so the read is never more cautious than the
        // fight itself. Mordak's Dust Dogs chased a lone scout onto (-2,3), beside Ysolde's Base
        // and her 21.8 Ironfang, and were destroyed with their hero on her next move.
        internal static bool LeavesForceExposed(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefenderProfile> roster, WorthIt.SideCommander commander,
            HexCoord at, int struckEnemyId, out int exposerId, out float exposerWin,
            int maxEnemyEta = AiConfigV2.activeDefenceWithdrawMaxEnemyEta)
        {
            exposerId = -1;
            exposerWin = 0f;
            if (snap?.Threat?.Contacts == null || roster == null || roster.Count == 0
                || ReferenceEquals(snap.Map, null))
                return false;
            var defending = new[] { new WorthIt.DefendingArmy(roster, commander,
                environment: KnownOpposition.WeatherAt(snap, at)) };
            foreach (EnemyContactSnapshot c in snap.Threat.Contacts)
            {
                if (c?.Army == null || c.Army.ArmyId < 0 || c.Army.ArmyId == struckEnemyId
                    || !c.Position.HasValue || c.Army.Owner == null || c.Army.Owner.IsNeutral
                    || c.Army.Owner == snap.Observer || c.Army.Members == null || c.Army.Members.Count == 0)
                    continue;
                int? approach = WorldAnalysis.ContactApproachCost(snap.Map, c, at, snap.TurnNumber,
                    observer: snap.Observer);
                if (!approach.HasValue || AiV2Util.CeilDiv(approach.Value, Math.Max(1, c.Army.MaxMovement))
                        > maxEnemyEta)
                    continue;
                if (!WorthIt.CanDamageAll(c.Army.Members, defending, 0f, attackerCommander: c.Army.Commander))
                    continue;
                float win = WorthIt.EstimateHexControl(c.Army.Members, c.Army.Commander, defending, 0f).WinChance;
                if (win <= 1f - AiConfigV2.activeDefenceHoldWinChance || win <= exposerWin)
                    continue;
                exposerId = c.Army.ArmyId;
                exposerWin = win;
            }
            return exposerId >= 0;
        }

        // `pinnedActor` — the one army asked (an incumbent's primary, or the returning army
        // Continuity proves before lending it, MissionContinuityLayer.StrikeLoan): its answer is
        // logged apart from the free choice of the same enemy in the same turn.
        private static ActiveDefenceResponse TraceImmediate(WorldSnapshot snap,
            ActiveDefenceObjective objective, int? pinnedActor, ActiveDefenceResponse response,
            ActiveDefenceResponseKind kind, string reason, int actorId, int mp, int routeCost,
            float win = 0f)
        {
            response.Kind = kind;
            response.Reason = reason;
            string pin = pinnedActor.HasValue
                ? " pinned=" + pinnedActor.Value.ToString(CultureInfo.InvariantCulture) : "";
            AiDebugLog.WriteDeduped($"immediate:{snap.Observer?.Nickname}:{snap.TurnNumber}:{objective.Target.EnemyArmyId}{pin}",
                $"[AI][V2][ActiveDefence][Immediate] turn={snap.TurnNumber} player={snap.Observer?.Nickname}{pin} "
                + $"enemy={objective.Target.EnemyArmyId}@{objective.Target.LastKnownHex} actor={(actorId < 0 ? "none" : actorId.ToString(CultureInfo.InvariantCulture))} "
                + $"mp={mp} routeCost={(routeCost == int.MaxValue ? "none" : routeCost.ToString(CultureInfo.InvariantCulture))} "
                + $"win={win:0.00} decision={kind} reason={reason}");
            return response;
        }

        // An enemy standing on a structure this player remembers as someone else's is an
        // ImmediateOpportunity only on a live opponent's extractor that was seen collecting
        // (DeniedIncome > 0): winning there razes it and ends that income (2026-11-12). Every
        // other foreign structure blocks: Bases/Citadels are Attack's
        // (AttackObjectiveEvaluator.IsHostileStrategicStructure), and a neutral, eliminated or
        // idle structure has no income to deny. The strategic Intercept keeps the plain rule
        // (OnKnownForeignStructure). Deciding it here, at admission, keeps the planner, Demand
        // and Continuity on one answer: no objective, no pursuit.
        internal static bool BlocksImmediateIntercept(WorldSnapshot snap, HexCoord hex) =>
            snap?.Known?.Buildings?.Any(b => b.Hex.Equals(hex) && b.Owner != null
                && b.Owner != snap.Observer && DeniedIncome(snap, b) <= 0f) == true;

        // Observed per-turn collection of a live opponent's extractor; 0 for anything else.
        private static float DeniedIncome(WorldSnapshot snap, AiMapMemory.KnownBuilding b)
        {
            if (b.Owner == null || b.Owner == snap?.Observer || b.Owner.IsNeutral || b.Owner.IsEliminated
                || b.IsBase || b.IsStartingCitadel)
                return 0f;
            float income = 0f;
            foreach (int amount in b.CollectedAmounts ?? Array.Empty<int>())
                income += Math.Max(0, amount);
            return income;
        }

        private static float ExtractorDenial(WorldSnapshot snap, HexCoord hex)
        {
            float income = 0f;
            foreach (AiMapMemory.KnownBuilding b in snap.Known?.Buildings
                ?? (IReadOnlyList<AiMapMemory.KnownBuilding>)Array.Empty<AiMapMemory.KnownBuilding>())
                if (b.Hex.Equals(hex))
                    income = Math.Max(income, DeniedIncome(snap, b));
            return income <= 0f ? 0f : TaskScoreEvaluator.EconomicHexBenefit(income, 0f);
        }
    }
}
