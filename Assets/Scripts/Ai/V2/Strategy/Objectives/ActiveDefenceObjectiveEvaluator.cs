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
    // Intercept — one capable army (or a same-hex assembly around it) meets the enemy.
    // Return — one army withdraws: to the Citadel to regroup when the defensive power exists but is
    // spread over several field armies, or to its own base when the power does not exist at all.
    public enum ActiveDefencePhase { Intercept, Return }

    public struct ActiveDefenceMissionTarget
    {
        public ActiveDefencePhase Phase;
        public int EnemyArmyId;
        public HexCoord LastKnownHex;
        public int LastObservedTurn;
        public float Confidence;
        public HexCoord ProtectedAssetHex;
        public AssetKind ProtectedAssetKind;
        public float ProtectedAssetValue;
        public float ThreatSeverity;
        public int? PrimaryArmyId;
        public HexCoord? ReturnHex;
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public int EstimatedEta;
    }

    // The ONE answer to "how does this player respond to this threat", shared by the Mission
    // planner (what to propose) and Demand (whether to buy). See AssessResponse.
    public enum ActiveDefenceResponseKind
    {
        Intercept,  // a concrete army / same-hex assembly clears the combat gate now
        Defer,      // a capable force exists but cannot act this pass (spent MP, claimed, pinned)
        Regroup,    // enough usable power, but spread over field armies: they gather at the Citadel
        Shortage,   // not enough usable power (or regroup exhausted): withdraw home and buy power
    }

    public sealed class ActiveDefenceResponse
    {
        public ActiveDefenceResponseKind Kind;
        public IReadOnlyList<WorthIt.DefendingArmy> Opposition;
        // Intercept only: the plan and the exclusion set it was solved under (the same set the
        // proposal's admission fingerprint must use).
        public GroundCombatAssemblyPlan Plan;
        public ISet<int> ExcludedArmyIds;
        public float RequiredPower;
        public float AvailablePower;
        // Power of the strongest single usable army (sizes a composition shortage).
        public float StrongestPower;
        // Regroup / Shortage: usable field armies that still have to walk; each gets its own
        // one-actor Return leg. Armies already withdrawing are never listed again.
        public readonly List<ArmySnapshot> Movers = new List<ArmySnapshot>();
        // Regroup only: the canonical Citadel hex.
        public HexCoord? RegroupHex;
        public string Reason;
    }

    public sealed class ActiveDefenceObjective
    {
        public ActiveDefenceMissionTarget Target;
        public TaskScore TaskScore;
        public float BaseValue => TaskScore.Value;
        public MissionIntentKey IntentKey => MissionIntentKey.ForActiveDefence(Target.EnemyArmyId);
    }

    // Threat -> objective is deliberately snapshot-only. One hostile army yields at most one
    // objective: the protected asset is selected by the canonical TaskScore, never by a private
    // defence-family multiplier.
    public static class ActiveDefenceObjectiveEvaluator
    {
        public static List<ActiveDefenceObjective> Enumerate(WorldSnapshot snap)
        {
            var result = new List<ActiveDefenceObjective>();
            IEnumerable<AssetThreatSnapshot> threats = snap?.Threat?.Threats
                ?? Array.Empty<AssetThreatSnapshot>();

            // Analysis includes historical AiReconMemory contacts in Threat. They are honest
            // observations, but are not necessarily present in AiMapMemory's canonical enemy
            // sightings. ActiveDefenceProvisioner requires that canonical sighting to resolve
            // an actual intercept. Filter at objective admission, BEFORE AP allocation; retain
            // historical threats in Analysis for strategic pressure and Recon information needs.
            // AiReconMemory.Historical excludes IDs already in Known.EnemySightings, so an ID
            // membership check identifies precisely the source contract the provisioner uses.
            var interceptableIds = new HashSet<int>(snap?.Known?.EnemySightings?
                .Select(s => s.ArmyId) ?? Enumerable.Empty<int>());

            foreach (IGrouping<int, AssetThreatSnapshot> group in threats
                .Where(t => IsHonestPositionedHostile(t)
                    && interceptableIds.Contains(t.Contact.Army.ArmyId))
                .GroupBy(t => t.Contact.Army.ArmyId))
            {
                AssetThreatSnapshot chosen = group
                    .Select(t => new { Threat = t, Score = ScoreThreat(snap, t) })
                    .OrderByDescending(x => x.Score.Value)
                    .ThenByDescending(x => x.Threat.Asset.Value * x.Threat.Severity)
                    .ThenBy(x => x.Threat.EnemyEta ?? int.MaxValue)
                    .ThenBy(x => x.Threat.Asset.Hex.Q).ThenBy(x => x.Threat.Asset.Hex.R)
                    .Select(x => x.Threat).First();
                if (OnKnownForeignStructure(snap, chosen.Contact.Position.Value))
                {
                    AiDebugLog.WriteDeduped(group.Key.ToString(CultureInfo.InvariantCulture),
                        $"[AI][V2][ActiveDefence][Objective] decision=DEFER enemy={group.Key} "
                        + "reason=enemy_on_known_foreign_structure attack_owner_required");
                    continue;
                }
                TaskScore score = ScoreThreat(snap, chosen);
                var target = new ActiveDefenceMissionTarget
                {
                    Phase = ActiveDefencePhase.Intercept,
                    EnemyArmyId = group.Key,
                    LastKnownHex = chosen.Contact.Position.Value,
                    LastObservedTurn = chosen.Contact.LastObservedTurn,
                    Confidence = chosen.Confidence,
                    ProtectedAssetHex = chosen.Asset.Hex,
                    ProtectedAssetKind = chosen.Asset.Kind,
                    ProtectedAssetValue = chosen.Asset.Value,
                    ThreatSeverity = chosen.Severity,
                    EstimatedEta = chosen.EnemyEta.GetValueOrDefault(),
                };
                result.Add(new ActiveDefenceObjective { Target = target, TaskScore = score });
                AiDebugLog.WriteDeduped(group.Key.ToString(CultureInfo.InvariantCulture),
                    $"[AI][V2][ActiveDefence][Objective] decision=ACCEPT enemy={group.Key} "
                    + $"asset={chosen.Asset.Kind}@({chosen.Asset.Hex.Q},{chosen.Asset.Hex.R}) "
                    + $"task={score.Value:0.00} severity={chosen.Severity:0.00}");
            }

            return result.OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.Target.EnemyArmyId).ToList();
        }

        // An enemy standing on a structure this player remembers as someone else's is never an
        // intercept. Winning a fight on a hostile structure captures/destroys it — that is the
        // Attack objective for the site (AttackObjectiveEvaluator.IsHostileAttackStructure, which
        // takes the enemy as part of the site's one defender package, §31). Any other foreign
        // owner is refused by the ground-move gate for a Combat step anyway (the structure reads
        // as an undefended foreign takeover). Deciding it here, at admission, keeps the planner,
        // Demand and Continuity on one answer: no objective, no shortage, no pursuit.
        internal static bool OnKnownForeignStructure(WorldSnapshot snap, HexCoord hex)
        {
            IReadOnlyList<AiMapMemory.KnownBuilding> buildings = snap?.Known?.Buildings;
            if (buildings == null || snap.Observer == null)
                return false;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i].Hex.Equals(hex) && buildings[i].Owner != null
                    && buildings[i].Owner != snap.Observer)
                    return true;
            return false;
        }

        public static ActiveDefenceObjective ForTrackedEnemy(WorldSnapshot snap, int enemyArmyId) =>
            Enumerate(snap).FirstOrDefault(o => o.Target.EnemyArmyId == enemyArmyId);

        // The fight an intercept of this enemy is: its honestly-known roster and commander.
        // Null when the contact carries no position (nothing to intercept).
        internal static IReadOnlyList<WorthIt.DefendingArmy> Opposition(WorldSnapshot snap,
            int enemyArmyId)
        {
            EnemyContactSnapshot contact = snap?.Threat?.Contacts?.FirstOrDefault(c =>
                c?.Army != null && c.Army.ArmyId == enemyArmyId && c.Position.HasValue);
            return contact == null ? null
                : new[] { new WorthIt.DefendingArmy(contact.Army.Members, contact.Army.Commander) };
        }

        // Armies already walking an ActiveDefence Return leg (a regroup or a withdrawal).
        internal static HashSet<int> WithdrawingArmyIds(IEnumerable<MissionIntent> intents) =>
            new HashSet<int>((intents ?? Enumerable.Empty<MissionIntent>())
                .Where(i => i != null && i.Status == IntentStatus.Active
                    && i.ActiveDefence?.Phase == ActiveDefencePhase.Return
                    && i.ActiveDefence.PrimaryArmyId.HasValue)
                .Select(i => i.ActiveDefence.PrimaryArmyId.Value));

        // The live Intercept intent answering this enemy, if any.
        internal static MissionIntent IncumbentIntercept(IEnumerable<MissionIntent> intents,
            int enemyArmyId) =>
            intents?.FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                && i.Kind == MissionKind.ActiveDefence
                && i.ActiveDefence?.Phase == ActiveDefencePhase.Intercept
                && i.ActiveDefence.EnemyArmyId == enemyArmyId);

        // THE ActiveDefence response decision (Mission planner and Demand read the same answer):
        //  1. a concrete army or same-hex assembly clears the gate  -> Intercept;
        //  2. a capable army exists but cannot act this pass (spent MP, owned by another operation,
        //     or this objective's pinned incumbent still waiting)    -> Defer (no retreat, no buy);
        //  3. the usable field armies together reach RequiredPower   -> Regroup at the Citadel,
        //     where the existing same-hex owners (GroundCombatAssemblyPlanner, Housekeeping) form
        //     the force; once every usable army already stands there the regroup is exhausted;
        //  4. otherwise (or regroup exhausted)                        -> Shortage: withdraw home,
        //     Demand publishes FieldCombatPower.
        // `committed` — armies other operations own; `withdrawing` — armies already on an
        // ActiveDefence Return leg: they count as usable power but are never proposed again, and
        // never pulled off their withdrawal into an intercept. Usable power is summed over
        // GroundCombatActorEligibility's structural set through GroundCombatFeasibility.
        internal static ActiveDefenceResponse AssessResponse(WorldSnapshot snap,
            ActiveDefenceObjective objective, ISet<int> committed, ICollection<int> withdrawing,
            int? pinnedActor)
        {
            IReadOnlyList<WorthIt.DefendingArmy> opposition = objective == null ? null
                : Opposition(snap, objective.Target.EnemyArmyId);
            if (opposition == null || snap?.Self?.Armies == null)
                return null;
            // An enemy standing on a known foreign structure is the Attack owner's site, never a
            // defence response (see OnKnownForeignStructure): no intercept, no withdrawal, no buy.
            if (OnKnownForeignStructure(snap, objective.Target.LastKnownHex))
                return null;
            var response = new ActiveDefenceResponse { Opposition = opposition };

            var planExcluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            if (withdrawing != null) planExcluded.UnionWith(withdrawing);
            if (pinnedActor.HasValue) planExcluded.Remove(pinnedActor.Value);
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                new GroundCombatAssemblyRequest
                {
                    Opposition = opposition,
                    WinChanceGate = GroundCombatAdmissionPolicy.PinnedOrFreshGate(pinnedActor.HasValue),
                    PreferredPrimaryArmyId = pinnedActor,
                    PinToPreferred = pinnedActor.HasValue,
                    ExcludedArmyIds = planExcluded,
                });
            if (plan.Feasible)
            {
                response.Kind = ActiveDefenceResponseKind.Intercept;
                response.Plan = plan;
                response.ExcludedArmyIds = planExcluded;
                response.Reason = "direct_response";
                return response;
            }
            if (pinnedActor.HasValue)
            {
                // Continuity already re-tested the incumbent's capability this pass; failing the
                // ready plan here only means it cannot move right now.
                response.Kind = ActiveDefenceResponseKind.Defer;
                response.Reason = "incumbent_waits";
                return response;
            }

            // Capable but temporarily unavailable: one physical army clears the gate on its own
            // once its MP returns or its current operation releases it. Buying or retreating
            // against that would be phantom.
            float freshGate = GroundCombatAdmissionPolicy.FreshStartWinChanceGate;
            ArmySnapshot capable = snap.Self.Armies.FirstOrDefault(a => a != null
                && a.IsStructuralRaidActor
                && (withdrawing == null || !withdrawing.Contains(a.ArmyId))
                && GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(snap, opposition,
                    a.ArmyId, freshGate).Feasible);
            if (capable != null)
            {
                response.Kind = ActiveDefenceResponseKind.Defer;
                response.Reason = $"capable_actor_unavailable actor=#{capable.ArmyId}";
                return response;
            }

            var powerExcluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            if (withdrawing != null) powerExcluded.ExceptWith(withdrawing);
            List<ArmySnapshot> usable = GroundCombatActorEligibility.EligibleArmies(snap,
                powerExcluded, requireMovementNow: false);
            response.RequiredPower = GroundCombatFeasibility.RequiredPower(
                WorthIt.UnitsOf(opposition), 0f);
            response.AvailablePower = GroundCombatFeasibility.AggregatePower(usable);
            response.StrongestPower = usable.Count == 0 ? 0f
                : usable.Max(a => Mathf.Max(0f, a.EffectiveArmyPower));
            IEnumerable<ArmySnapshot> walkers = usable.Where(a =>
                withdrawing == null || !withdrawing.Contains(a.ArmyId));

            // The regroup point is the canonical Citadel, and only while it is still an own base.
            // Only armies that stand there or have a route to it can join the regroup: power that
            // can never arrive would keep the regroup from ever being exhausted.
            IEnumerable<HexCoord> bases = snap.Self.BaseHexes ?? Enumerable.Empty<HexCoord>();
            HexCoord? citadel = snap.Observer == null ? (HexCoord?)null
                : AiTurnController.GarrisonHexFor(snap.Observer);
            if (citadel.HasValue && !bases.Contains(citadel.Value))
                citadel = null;
            List<ArmySnapshot> gatherable = !citadel.HasValue ? new List<ArmySnapshot>()
                : usable.Where(a => a.Hex.Equals(citadel.Value)
                    || a.ReachableOwnBaseHexes == null || a.ReachableOwnBaseHexes.Count == 0
                    || a.ReachableOwnBaseHexes.Contains(citadel.Value)).ToList();

            if (GroundCombatFeasibility.AggregatePower(gatherable) + AiConfigV2.allocatorSliceEpsilon
                >= response.RequiredPower)
            {
                if (gatherable.Any(a => !a.Hex.Equals(citadel.Value)))
                {
                    response.Kind = ActiveDefenceResponseKind.Regroup;
                    response.RegroupHex = citadel;
                    response.Movers.AddRange(walkers.Where(a => gatherable.Contains(a)
                        && !a.Hex.Equals(citadel.Value)));
                    response.Reason = "regroup_required";
                    return response;
                }
                // Every army that can gather already stands in the Citadel and the same-hex
                // assembly still misses the gate: the gap is composition, not distribution.
                response.Kind = ActiveDefenceResponseKind.Shortage;
                response.Reason = "regroup_exhausted";
                return response;
            }

            response.Kind = ActiveDefenceResponseKind.Shortage;
            response.Movers.AddRange(walkers.Where(a => !bases.Contains(a.Hex)));
            response.Reason = !citadel.HasValue && response.AvailablePower
                    + AiConfigV2.allocatorSliceEpsilon >= response.RequiredPower
                ? "no_regroup_point" : "insufficient_power";
            return response;
        }

        // ---- LIVE (revalidation / post-execution ledger pass) --------------------------------

        // The ONE honest completion check for an Intercept objective, and the live counterpart of
        // Enumerate above. MissionRevalidator, MissionOutcomeLedger, Enumerate and
        // ActiveDefenceProvisioner must all apply the same knowledge rule: strategic knowledge of
        // an enemy army is AiMapMemory's (ARCH-02 canonical seams), and "absent from the world" is
        // never objective completion — a global ArmyRegistry sweep would let an intercept retire
        // because the army moved/died somewhere we cannot see, while the other stages still
        // consider it a live target, recreating and instantly "completing" the mission every pass.
        // Mirrors RaidObjectiveEvaluator.IsObjectiveSatisfiedLive:
        //  1) the tracked id is fielded by US now — positive confirmation of the outcome of an
        //     action we ourselves completed (the one ArmyRegistry read the seam allows);
        //  2) honest memory still tracks it as a hostile, non-neutral army — NOT satisfied. It is
        //     exactly as real as our last observation says, fog included;
        //  3) honest memory now tracks it as a NEUTRAL army — this hostile-threat objective is
        //     over (Raid, not ActiveDefence, owns neutrals);
        //  4) honest memory no longer tracks it at all — either the hex was genuinely re-observed
        //     empty (AiMapMemory's own "corrected (gone on re-observation)" write) or a
        //     player-owned sighting aged out under the existing enemySightingMemoryTurns
        //     uncertainty policy. Both are "gone as far as we may honestly know"; neither invents
        //     a new lost-contact state, and Enumerate cannot re-admit an objective for an id that
        //     is no longer in Known.EnemySightings, so this can never oscillate.
        public static bool IsObjectiveSatisfiedLive(PlayerSetupData player, int enemyArmyId)
        {
            if (player == null)
                return false;
            if (ArmyRegistry.AllForOwner(player)
                .Any(a => a != null && a.Id == enemyArmyId && a.Members.Count > 0))
                return true;
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == enemyArmyId)
                    return false;
            return true;
        }

        internal static bool ShouldStopPursuit(bool hasListedThreat, bool movingAway,
            bool homeDistanceAtFullNegative, bool activeStillBeatsAlternative) =>
            !hasListedThreat && movingAway && homeDistanceAtFullNegative
            && !activeStillBeatsAlternative;

        // `projectedActivationAp` — the activation AP of the roster the assembly plan will really
        // field, when the caller has that projection (GroundCombatAssemblyPlanner is its one
        // owner). Null keeps the actor's own snapshot figure. The score must be folded from the
        // same force the envelope is priced from.
        public static TaskScore WithResponse(ActiveDefenceObjective objective, ArmySnapshot actor,
            float winChance, int eta, float moverOpportunityCost = 0f,
            int? projectedActivationAp = null) =>
            TaskScoreEvaluator.WithActorResponse(objective.TaskScore, actor, winChance, eta,
                moverOpportunityCost, projectedActivationAp);

        private static bool IsHonestPositionedHostile(AssetThreatSnapshot t) =>
            t?.Asset != null && t.Contact?.Army != null
            && t.Contact.Army.ArmyId >= 0
            && t.Contact.Position.HasValue
            && t.Contact.Army.Owner != null && !t.Contact.Army.Owner.IsNeutral;

        private static TaskScore ScoreThreat(WorldSnapshot snap, AssetThreatSnapshot t)
        {
            int age = Math.Max(0, (snap?.TurnNumber ?? 0) - t.Contact.LastObservedTurn);
            int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(snap,
                t.Contact.Position.Value);
            float assetNorm = t.Asset.Value / Mathf.Max(1f, AiConfigV2.assetValueCitadel);
            return new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(assetNorm),
                threatDirection: TaskScoreEvaluator.ThreatDirection(
                    1f / (1f + t.EnemyEta.GetValueOrDefault())),
                militaryTargetRelevance: TaskScoreEvaluator.MilitaryTargetRelevance(t.PotentialDamage),
                staleness: TaskScoreEvaluator.StaleIntelPenalty(
                    age / (float)Mathf.Max(1, AiConfigV2.scoutSurveilStaleTurnsHi)),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance));
        }
    }
}
