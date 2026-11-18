using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON OBJECTIVE EVALUATOR
    // ===========================================================================================
    // One frozen turn produces three explicit Recon opportunity classes:
    // Explore — never/ground-unvisited frontier information;
    // Refresh — stale previously-observed information;
    // AirSweep — aviation-only observation pass toward the strategic sweep anchor.
    // ===========================================================================================
    public enum ReconObjectiveKind { Explore = 0, Refresh = 1, AirSweep = 3, CaptureStructure = 4, Hunt = 5, DisruptStructure = 6 }

    public sealed class ReconObjective
    {
        public ReconObjectiveKind Kind;
        public HexCoord FocusHex;

        // Legacy transport kept during migration. Intrinsic value is now owned exclusively by
        // TaskScore; every migrated consumer must observe BaseValue == TaskScore.Value.
        public float BaseValue;
        public TaskScore TaskScore;
        public float DetectionRisk;             // raw fact, not the converted TaskScore contribution
        public StealthRequirement Stealth;

        public int FreshNeighbors;
        public int DistanceFromBase;
        public int AgeTurns;
        public float Severity;
        public float StrategicRelevance;
        public float DirectionPressure;

        public MissionIntentKey IntentKey
        {
            get
            {
                // One durable sweep identity: the anchor follows the enemy, the operation does not
                // become a new intent every time the concentration moves.
                if (Kind == ReconObjectiveKind.AirSweep)
                    return new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.AirSweep, 0, 0, 0);
                if (Kind == ReconObjectiveKind.Hunt || Kind == ReconObjectiveKind.DisruptStructure)
                    return MissionIntentKey.ForScoutTarget(ToTarget());
                ScoutTargetKind sub = Kind == ReconObjectiveKind.Refresh
                    ? ScoutTargetKind.Refresh
                    : ScoutTargetKind.Explore;
                return new MissionIntentKey(MissionKind.Scout, (int)sub, 0, FocusHex.Q, FocusHex.R);
            }
        }

        public bool NeedsStealth => ReconScoutKinds.NeedsStealth(Stealth, DetectionRisk);

        // A one-shot local action (capture / hunt): never a Recon lane, never a scout to buy.
        public bool IsDisruption => Kind == ReconObjectiveKind.DisruptStructure;

        public bool IsLocalStrike => Kind == ReconObjectiveKind.CaptureStructure || Kind == ReconObjectiveKind.Hunt;

        public ScoutMissionTarget ToTarget() => new ScoutMissionTarget
        {
            FocusHex = FocusHex,
            Kind = Kind == ReconObjectiveKind.CaptureStructure ? ScoutTargetKind.CaptureStructure
                : Kind == ReconObjectiveKind.Hunt ? ScoutTargetKind.Hunt
                : Kind == ReconObjectiveKind.DisruptStructure ? ScoutTargetKind.DisruptStructure
                : Kind == ReconObjectiveKind.AirSweep ? ScoutTargetKind.AirSweep
                : Kind == ReconObjectiveKind.Refresh ? ScoutTargetKind.Refresh : ScoutTargetKind.Explore,
            Stealth = Stealth,
            DetectionRisk = DetectionRisk,
        };
    }

    public static class ReconObjectiveEvaluator
    {
        // The decision frame's ONE Recon enumeration of a snapshot (DecisionFrame.Recon), kept
        // beside that snapshot instance. Every refresh builds a new snapshot, so a later snapshot
        // never sees an older frame's list. A read-only consumer inside the frame's decision
        // (the operator aviation forecast priced in Development verdicts) reuses it instead of
        // re-enumerating: late game one Enumerate is ~200 ms (2026-11-14 profile).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot,
            List<ReconObjective>> s_frameRecon =
                new System.Runtime.CompilerServices.ConditionalWeakTable<WorldSnapshot, List<ReconObjective>>();

        internal static List<ReconObjective> EnumerateForFrame(WorldSnapshot snap)
        {
            List<ReconObjective> list = Enumerate(snap);
            if (snap != null)
            {
                s_frameRecon.Remove(snap);
                s_frameRecon.Add(snap, list);
            }
            return list;
        }

        // The frame's list for this snapshot (read-only), else a fresh enumeration.
        internal static IReadOnlyList<ReconObjective> FrameOrEnumerate(WorldSnapshot snap) =>
            snap != null && s_frameRecon.TryGetValue(snap, out List<ReconObjective> list)
                ? list : Enumerate(snap);

        // Snapshot-wide facts of ONE synchronous Enumerate, computed once instead of once per
        // candidate hex (2026-11-14 late-game timing: ObservationNeeds re-ran Attack's objective
        // enumeration and an ActorCommitments build for every Explore focus - 1511 builds, 4.3 s
        // in one turn; LocalExplorationGap scanned the whole map per focus). Nothing changes the
        // snapshot or the intents inside one Enumerate, so each fact equals the per-hex read.
        // Outside an Enumerate (ExploreAt / RefreshAt / BuildExploreScore called directly) the
        // facts are read live, as before.
        private sealed class EnumerationFacts
        {
            internal WorldSnapshot Snap;
            internal HashSet<HexCoord> ObservationNeeds;
            internal float? LocalExplorationGap;
            internal ReconDirectionSnapshot Direction;
        }

        [System.ThreadStatic] private static EnumerationFacts s_enumeration;

        private static EnumerationFacts FactsFor(WorldSnapshot snap)
        {
            EnumerationFacts facts = s_enumeration;
            return facts != null && ReferenceEquals(facts.Snap, snap) ? facts : null;
        }

        // A single question: the running Enumerate's frozen needs, else a live read
        // (ObservationNeedLookup owns that choice).
        private static bool IsObservationNeed(WorldSnapshot snap, HexCoord hex)
        {
            using var needs = new ObservationNeedLookup(snap);
            return needs.Contains(hex);
        }

        // ONE AttackObjectiveEvaluator.ObservationNeeds read shared by many IsObservationNeed
        // questions about the same snapshot and an unchanged intent set (one ActorCommitments
        // build). Pulled lazily: a hex found early stops the enumeration exactly as a single
        // Contains does, a later question continues it, so the answers equal the per-call read.
        // 2026-11-18 late game: every commitments build re-ran ObservationNeeds (a nested
        // commitments build, Attack's target enumeration and readiness forecasts) per exposed
        // Refresh intent - 0.4-2.8 s per turn.
        internal sealed class ObservationNeedLookup : System.IDisposable
        {
            private readonly WorldSnapshot _snap;
            private HashSet<HexCoord> _seen;
            private IEnumerator<HexCoord> _pending;

            internal ObservationNeedLookup(WorldSnapshot snap) => _snap = snap;

            internal bool Contains(HexCoord hex)
            {
                HashSet<HexCoord> frozen = FactsFor(_snap)?.ObservationNeeds;
                if (frozen != null)
                    return frozen.Contains(hex);
                if (_seen == null)
                {
                    _seen = new HashSet<HexCoord>();
                    _pending = AttackObjectiveEvaluator.ObservationNeeds(_snap).GetEnumerator();
                }
                if (_seen.Contains(hex))
                    return true;
                while (_pending != null && _pending.MoveNext())
                {
                    HexCoord need = _pending.Current;
                    _seen.Add(need);
                    if (need.Equals(hex))
                        return true;
                }
                Dispose();
                return false;
            }

            public void Dispose()
            {
                _pending?.Dispose();
                _pending = null;
            }
        }

        private static float LocalExplorationGap(WorldSnapshot snap)
        {
            EnumerationFacts facts = FactsFor(snap);
            if (facts == null)
                return ScoutObjectiveEvaluator.LocalExplorationGap(snap);
            return facts.LocalExplorationGap ??= ScoutObjectiveEvaluator.LocalExplorationGap(snap);
        }

        // The coarse enemy-direction buckets (ReconDirectionModel), once per Enumerate.
        private static ReconDirectionSnapshot DirectionFor(WorldSnapshot snap)
        {
            EnumerationFacts facts = FactsFor(snap);
            if (facts == null)
                return ReconDirectionModel.Build(snap);
            return facts.Direction ??= ReconDirectionModel.Build(snap);
        }

        // THE "toward the enemy" fact of a Recon focus [0..1], for Refresh and Explore alike: the
        // sanitized enemy-presence pressure of the hex's sector seen from our Citadel, at least
        // 0.75 toward the known enemy Citadel. 2026-11-15 (match Brynn vs Grimm, P3, owner option
        // a) — Explore had no such term while Refresh did, so the unvisited ground beside the enemy
        // (Concord Base, never found in 46 turns) paid detection risk and earned nothing for it.
        internal static float DirectionalPressure(WorldSnapshot snap, HexCoord hex,
            ReconDirectionSnapshot direction)
        {
            if (snap?.Self == null || direction == null)
                return 0f;
            ReconSector sector = ReconDirectionModel.Sector(snap.Self.Citadel, hex);
            float raw = direction.EnemyDirectionSectors != null
                && direction.EnemyDirectionSectors.TryGetValue(sector, out float pressure)
                    ? Mathf.Clamp01(pressure) : 0f;
            if (direction.KnownEnemyCitadelDirection == sector)
                raw = Mathf.Max(raw, 0.75f);
            return raw;
        }

        // While a known enemy threatens home, the direction it comes from weighs up to twice.
        private static float HomeThreat(WorldSnapshot snap) =>
            Mathf.Max(snap?.Threat?.CitadelThreatSeverity ?? 0f, snap?.Threat?.BaseThreatSeverity ?? 0f);

        public static List<ReconObjective> Enumerate(WorldSnapshot snap)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Objectives.Recon");
            if (snap?.Self == null || snap.MapKnowledge == null)
                return new List<ReconObjective>();
            List<HexCoord> observationNeeds = AttackObjectiveEvaluator.ObservationNeeds(snap).Distinct().ToList();
            EnumerationFacts outer = s_enumeration;
            s_enumeration = new EnumerationFacts
            {
                Snap = snap,
                ObservationNeeds = new HashSet<HexCoord>(observationNeeds),
            };
            try { return EnumerateWithFacts(snap, observationNeeds); }
            finally { s_enumeration = outer; }
        }

        private static List<ReconObjective> EnumerateWithFacts(WorldSnapshot snap,
            IReadOnlyList<HexCoord> observationNeeds)
        {
            var list = new List<ReconObjective>();

            IReadOnlyList<FrontierHexSnapshot> frontier = snap.MapKnowledge.Frontier;
            if (frontier != null)
                foreach (FrontierHexSnapshot f in frontier)
                {
                    if (!ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, f.Hex))
                        continue;
                    list.Add(BuildExplore(snap, f.Hex, f.FreshNeighbors, f.DistanceFromNearestBase,
                        f.EnemyExposure, f.StealthDetectionRisk));
                }

            // 2026-11-15 (match Brynn vs Grimm, P3) — Explore vanished from the portfolio at T19 with
            // 39% of the map dark; the frontier's own prices were never logged. One line per turn.
            if (list.Count > 0)
                AiDebugLog.WriteDeduped($"recon-explore-frontier#{snap.Observer?.ColorIndex}#{snap.TurnNumber}",
                    $"[AI][V2][Recon][Explore] {snap.Observer?.Nickname} T{snap.TurnNumber} frontier={list.Count} "
                    + $"positive={list.Count(o => o.BaseValue > 0f)} best: "
                    + string.Join(" || ", list.OrderByDescending(o => o.BaseValue).Take(3)
                        .Select(o => $"({o.FocusHex.Q},{o.FocusHex.R}) fresh {o.FreshNeighbors} d{o.DistanceFromBase} "
                            + $"stealth={o.Stealth} {TaskScoreEvaluator.Describe(o.TaskScore)}")));

            // An unvisited City-ruins hex is a known event-reward site, so it is an Explore focus
            // worth walking to even before it becomes part of the frontier wave band. Same
            // Explore shape and validity rule as a frontier focus; distance is priced by the
            // ordinary Delivery / OwnTerritoryProximity slots, never filtered here.
            ISet<HexCoord> ruins = snap.MapKnowledge.UnvisitedRuinsHexes;
            if (ruins != null && ruins.Count > 0)
            {
                var listed = new HashSet<HexCoord>(list.Select(o => o.FocusHex));
                foreach (HexCoord r in ruins.OrderBy(h => h.Q).ThenBy(h => h.R))
                {
                    if (listed.Contains(r))
                        continue;
                    ReconObjective o = ExploreAt(snap, r);
                    if (o != null)
                        list.Add(o);
                }
            }

            foreach (HexCoord hex in snap.Known?.ActiveEventHexes ?? System.Array.Empty<HexCoord>())
            {
                if (!ScoutObjectiveEvaluator.IsKnownUnguardedEvent(snap, hex)
                    || list.Any(o => o.FocusHex.Equals(hex)))
                    continue;
                ReconObjective objective = ExploreAt(snap, hex);
                if (objective != null)
                    list.Add(objective);
                else
                    AiDebugLog.WriteDeduped($"event-explore-{snap.Observer?.ColorIndex}-{hex}",
                        $"[AI][V2][Recon] {snap.Observer?.Nickname} unguarded event at {hex} deferred: blocked focus");
            }

            // Generic Refresh revisits map information the
            // player genuinely observed in an earlier turn. The frozen sidecar excludes never-seen
            // hexes by construction and current-visible hexes naturally have age 0.
            ReconDirectionSnapshot direction = null;
            List<ReconObjective> refresh = BuildRefreshObjectives(snap, ref direction);
            // Strike force step 6 — Attack's observation needs join as Refresh objectives of the
            // same identity, replacing a generic Refresh of the same hex.
            // A need never observed at all (the enemy citadel's cheat anchor, step 7) is an
            // ordinary Explore focus instead.
            foreach (HexCoord need in observationNeeds)
            {
                ReconObjective o = AttackNeedRefresh(snap, need, null, ref direction);
                if (o != null)
                {
                    refresh.RemoveAll(r => r.FocusHex.Equals(need));
                    refresh.Add(o);
                    continue;
                }
                if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, need, out _)
                    && !list.Any(x => x.Kind == ReconObjectiveKind.Explore && x.FocusHex.Equals(need)))
                {
                    o = ExploreAt(snap, need);
                    if (o != null)
                        list.Add(o);
                }
            }
            list.AddRange(refresh);
            foreach (var building in snap.Known?.Buildings ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
            {
                ReconObjective disruption = DisruptAt(snap, building.Hex);
                if (disruption != null) list.Add(disruption);
            }

            ReconObjective sweep = AirSweepOf(snap);
            if (sweep != null)
                list.Add(sweep);

            var auditPlayer = snap.Self.Armies?.FirstOrDefault(a => a?.Owner != null)?.Owner;
            if (auditPlayer != null)
            {
                if (direction == null)
                    direction = DirectionFor(snap);
                ReconAcceptanceAudit.RecordDirectionBoundary(auditPlayer, snap.TurnNumber,
                    direction);

                ReconObjective topRefresh = refresh
                    .Where(o => o != null)
                    .OrderByDescending(o => o.BaseValue)
                    .FirstOrDefault();
                if (topRefresh != null)
                    ReconAcceptanceAudit.RecordDirectionInfluence(auditPlayer, snap.TurnNumber,
                        topRefresh.FocusHex, topRefresh.DirectionPressure, topRefresh.BaseValue);
            }
            foreach (HexCoord hex in (snap.Self?.ReconCaptureOpportunities
                ?? System.Array.Empty<(int ArmyId, HexCoord Hex)>()).Select(x => x.Hex).Distinct())
            {
                ReconObjective capture = CaptureAt(snap, hex);
                if (capture != null) list.Add(capture);
            }
            foreach (HexCoord hex in (snap.Self?.ReconHuntOpportunities
                ?? System.Array.Empty<(int ArmyId, HexCoord Hex)>()).Select(x => x.Hex).Distinct())
            {
                ReconObjective hunt = HuntAt(snap, hex);
                if (hunt != null) list.Add(hunt);
            }
            return list;
        }

        // A durable operation, or a nearby battle-ready fist, is a concrete support customer.
        // No enemy registry is read and no new strategic axis is introduced.
        // A fist that already carries its own carrier removes the defence itself: the scout
        // delivery (and its production) is then redundant.
        internal static bool AssaultNeedsDisruption(WorldSnapshot snap, HexCoord hex)
        {
            if (HostileStructureDefense(snap, hex) <= 0f) return false;
            ArmySnapshot OwnArmy(int? id) => id.HasValue
                ? snap.Self?.Armies?.FirstOrDefault(a => a != null && a.ArmyId == id.Value) : null;
            bool CarriesOwn(ArmySnapshot a) => a != null
                && Game.Combat.DisruptionRules.HasCarrier(a.MembersWithHeroes);
            MissionIntent assault = LiveAssaultAt(snap, hex);
            if (assault != null)
                return !CarriesOwn(OwnArmy(assault.Attack.PrimaryArmyId ?? assault.PreferredMoverArmyId));
            // A nearby field fist may need the carrier BEFORE its assault clears the defence gate.
            return (snap.Self?.Armies ?? System.Array.Empty<ArmySnapshot>()).Any(a => a != null
                && !a.IsAir && !a.IsPrison && !a.IsGarrison && !a.IsSoloRecce && a.MemberCount > 1
                && a.IsStructuralRaidActor && a.MaxMovement > 0
                && HexGridMath.Distance(a.Hex, hex) <= 2 * a.MaxMovement
                && !CarriesOwn(a)
                && FistMayAssault(snap, a, hex));
        }

        // 2026-11-16 — the fist the Attack lane itself would send: past the 80% floor, or by
        // opportunity (AttackForceReadiness.ForceReady with the win chance) against the site once a
        // carrier removes its structure defence (AttackSupportProjection.WithoutStructure). Match
        // 2026-11-16, Korrin T27-T33: #18 (52.8 < 74.9) was admitted by opportunity (win 0.92) and
        // held every turn awaiting a carrier, while this rule read the power floor alone and never
        // asked for one; with no operation started there was no timeout either.
        private static bool FistMayAssault(WorldSnapshot snap, ArmySnapshot fist, HexCoord hex)
        {
            if (AttackForceReadiness.ForceReady(fist.EffectiveArmyPower, snap.Self.AttackPeak))
                return true;
            List<Game.Combat.WorthIt.DefendingArmy> known = KnownOpposition.OnHex(snap, hex);
            float hexBonus = AiMapMemory.KnownHexDefenseBonus(snap.Observer, snap.Map, hex);
            List<Game.Combat.WorthIt.DefendingArmy> disrupted = AttackSupportProjection.WithoutStructure(known, hexBonus,
                out float disruptedBonus);
            GroundCombatFeasibility.Clears(fist.Members ?? (IReadOnlyList<Game.Combat.WorthIt.DefenderProfile>)System.Array.Empty<Game.Combat.WorthIt.DefenderProfile>(),
                fist.Commander, disrupted, GroundCombatAdmissionPolicy.AttackWinChanceGate, disruptedBonus,
                out float win, out _);
            return AttackForceReadiness.ForceReady(fist.EffectiveArmyPower, snap.Self.AttackPeak, win, disrupted);
        }

        // The live Attack operation (Gather, Assault or Reinforcement) whose target is `hex`.
        internal static MissionIntent LiveAssaultAt(WorldSnapshot snap, HexCoord hex) =>
            (snap?.AllIntents ?? System.Array.Empty<MissionIntent>()).FirstOrDefault(i =>
                i?.Status == IntentStatus.Active && i.Attack != null
                && i.Attack.Target.Hex.Equals(hex)
                && (i.Attack.Phase == AttackMissionPhase.Assault || i.Attack.Phase == AttackMissionPhase.Gather
                    || i.Attack.Phase == AttackMissionPhase.Reinforcement));

        // THE known structure defence a Disruption carrier removes at `hex`: a known hostile
        // (non-neutral) Base or starting Citadel's positive Defense, else 0. Read by the need rule
        // above and by the BuildingDisruption card valuation (EffectEvaluationContext).
        internal static float HostileStructureDefense(WorldSnapshot snap, HexCoord hex)
        {
            if (snap?.Observer == null || snap.Known?.Buildings == null) return 0f;
            var site = snap.Known.Buildings.FirstOrDefault(b => b.Hex.Equals(hex));
            if (site.Owner == null || site.Owner == snap.Observer || site.Owner.IsNeutral
                || (!site.IsBase && !site.IsStartingCitadel)) return 0f;
            return Mathf.Max(0f, site.Defense);
        }

        // The target of the active Attack an own army serves — its primary/commander, a support or
        // gather support, or its bound mover. Null when the army serves no Attack.
        internal static HexCoord? AttackSiteOf(WorldSnapshot snap, int armyId)
        {
            MissionIntent attack = (snap?.AllIntents ?? System.Array.Empty<MissionIntent>()).FirstOrDefault(i =>
                i?.Status == IntentStatus.Active && i.Attack != null
                && (i.PreferredMoverArmyId == armyId || i.Attack.PrimaryArmyId == armyId
                    || i.Attack.CommanderArmyId == armyId || i.Attack.SupportArmyId == armyId
                    || i.Attack.GatherSupportArmyIds?.Contains(armyId) == true));
            return attack?.Attack.Target.Hex;
        }

        // requireMover false — the Recon demand's own read (DemandLayer.Recon): the same objective
        // priced with a notional mover, so a missing stealth carrier can be asked for.
        internal static ReconObjective DisruptAt(WorldSnapshot snap, HexCoord hex,
            int? preferredMoverArmyId = null, bool requireMover = true)
        {
            if (!AssaultNeedsDisruption(snap, hex)) return null;
            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap, DisruptionTarget(hex), preferredMoverArmyId);
            if (requireMover && !cost.MoverKnown) return null;
            // 2026-11-07 (owner) — the carrier a live Attack operation counts on
            // (AttackSupportProjection.FreeCarrier) is a step of that operation: it keeps Attack's
            // priority the way every preparation step does (AttackReadiness full), so it can take a
            // scout off a lower Explore/Refresh lane (DisplaceableReconRole) instead of losing to it.
            MissionIntent liveAttack = LiveAssaultAt(snap, hex);
            bool servesLiveAttack = liveAttack != null;
            // 2026-11-16 (owner decision) — an enabler is worth what it unblocks: when that live
            // Attack's entry is held for want of this carrier (AttackObjectiveEvaluator.
            // HasAssaultDisruption fails for its primary), the operation's own value
            // (MissionIntent.DisplacementValue, what its idle army loses) joins the readiness term.
            // Match 2026-11-16: Rurik's Attack on (2,-1) held six turns beside the target while
            // the only carrier kept an Explore worth 17.2 against this task's flat 13.1.
            float unblocked = UnblockedAttackValue(snap, liveAttack, disruption: true);
            TaskScore score = new TaskScore(
                attackReadiness: servesLiveAttack ? TaskScoreEvaluator.AttackReadiness(1f) + unblocked : 0f,
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(1f),
                cardPrice: TaskScoreEvaluator.Price(cost.ActivationApNow),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(
                    TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex)));
            return new ReconObjective { Kind = ReconObjectiveKind.DisruptStructure, FocusHex = hex,
                Stealth = StealthRequirement.Required, TaskScore = score, BaseValue = score.Value,
                StrategicRelevance = 1f };
        }

        private static ScoutMissionTarget DisruptionTarget(HexCoord hex) => new ScoutMissionTarget
        {
            Kind = ScoutTargetKind.DisruptStructure, FocusHex = hex, Stealth = StealthRequirement.Required,
        };

        // Does DisruptAt(snap, hex) exist (default mover gate)? Its own gates without the score
        // (UnblockedAttackValue, prices) - the read StealthForIntent needs per commitments build.
        private static bool DisruptionExists(WorldSnapshot snap, HexCoord hex) =>
            AssaultNeedsDisruption(snap, hex)
            && ScoutCostModel.Estimate(snap, DisruptionTarget(hex)).MoverKnown;

        // 2026-11-16 (owner decision) — the value a Recon step unblocks: the live `attack`'s own
        // value (MissionIntent.DisplacementValue, what its idle army loses) while the entry rule the
        // operation itself applies fails for its primary on exactly what this step delivers —
        // Disruption (HasAssaultDisruption) or a current observation (HasAssaultObservation).
        // 0 when the operation is not waiting on it. Added to the step's readiness term.
        private static float UnblockedAttackValue(WorldSnapshot snap, MissionIntent attack, bool disruption)
        {
            AttackIntent a = attack?.Attack;
            if (a == null || !a.PrimaryArmyId.HasValue || snap?.Self?.Armies == null)
                return 0f;
            ArmySnapshot fist = snap.Self.Armies.FirstOrDefault(x => x != null && x.ArmyId == a.PrimaryArmyId.Value);
            if (fist == null)
                return 0f;
            bool blocked = disruption
                ? !AttackObjectiveEvaluator.HasAssaultDisruption(snap, a.Target, fist, out _)
                : !AttackObjectiveEvaluator.HasAssaultObservation(snap, a.Target, out _);
            return blocked ? attack.DisplacementValue : 0f;
        }

        internal static ReconObjective HuntAt(WorldSnapshot snap, HexCoord hex)
        {
            if (snap?.Self?.ReconHuntOpportunities?.Any(x => x.Hex.Equals(hex)) != true) return null;
            var target = new ScoutMissionTarget { Kind = ScoutTargetKind.Hunt, FocusHex = hex };
            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap, target);
            if (!cost.MoverKnown) return null;
            float relevance = AiConfigV2.reconHuntStrategicRelevance;
            // The same executable Hunt also removes a known saboteur beside our actual income.
            // This is task relevance, not a new guard mission or an override of fight/route safety.
            if (snap.Observer != null && (snap.Known?.Buildings ?? System.Array.Empty<AiMapMemory.KnownBuilding>()).Any(b =>
                b.Owner == snap.Observer && !b.IsBase && !b.IsStartingCitadel
                && HexGridMath.Distance(b.Hex, hex) <= AiConfigV2.economySiteThreatRadius
                && ResourceBundle.All.Any(t => b.CollectedAmount(t) > 0)))
                relevance = 1f;
            TaskScore score = new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(relevance),
                cardPrice: TaskScoreEvaluator.Price(cost.ActivationApNow),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(
                    TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex)));
            return new ReconObjective { Kind = ReconObjectiveKind.Hunt, FocusHex = hex,
                TaskScore = score, BaseValue = score.Value, StrategicRelevance = relevance };
        }

        internal static ReconObjective CaptureAt(WorldSnapshot snap, HexCoord hex)
        {
            if (snap?.Self?.ReconCaptureOpportunities?.Any(x => x.Hex.Equals(hex)) != true) return null;
            AiMapMemory.KnownBuilding building = snap.Known.Buildings.First(b => b.Hex.Equals(hex));
            var target = new ScoutMissionTarget { Kind = ScoutTargetKind.CaptureStructure, FocusHex = hex };
            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap, target);
            if (!cost.MoverKnown) return null;
            float relevance = building.IsStartingCitadel ? 1f : building.IsBase ? 0.85f : 0.5f;
            TaskScore score = new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(relevance),
                cardPrice: TaskScoreEvaluator.Price(cost.ActivationApNow),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(
                    TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex)));
            return new ReconObjective { Kind = ReconObjectiveKind.CaptureStructure, FocusHex = hex,
                TaskScore = score, BaseValue = score.Value, StrategicRelevance = relevance };
        }

        // Task 5 (Problem B) — preferredMoverArmyId lets a caller re-materialising a durable
        // incumbent intent (ReconMissionPlanner.TryMaterializeIntent) price BaseValue against the
        // SAME actor Requirements will later be priced against in BuildProposal(), instead of
        // BaseValue silently reflecting whichever ground actor happens to be cheapest right now.
        // The fresh-objective path (Enumerate) always omits it — a brand-new candidate has no
        // actor to prefer yet.
        public static ReconObjective ExploreAt(WorldSnapshot snap, HexCoord hex,
            int? preferredMoverArmyId = null)
        {
            if (!ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, hex))
                return null;
            int fresh = ScoutObjectiveEvaluator.ExploreStillOpen(snap, hex);
            int distBase = snap?.Self?.BaseHexes != null && snap.Self.BaseHexes.Count > 0
                ? MinDist(snap.Self.BaseHexes, hex) : 0;
            bool exposed = EnemyExposedAt(snap, hex);
            bool stealthRisk = exposed && DetectorsAt(snap, hex) > 0;
            return BuildExplore(snap, hex, fresh, distBase, exposed, stealthRisk, preferredMoverArmyId);
        }

        public static ReconObjective RefreshAt(WorldSnapshot snap, HexCoord hex,
            int? preferredMoverArmyId = null)
        {
            switch (RefreshKindAt(snap, hex, null, exactKind: true, out int age))
            {
                case RefreshKind.AttackNeed:
                    ReconDirectionSnapshot direction = null;
                    return AttackNeedRefresh(snap, hex, preferredMoverArmyId, ref direction);
                case RefreshKind.Stale:
                    return BuildRefresh(snap, hex, age, preferredMoverArmyId);
                default:
                    return null;
            }
        }

        // Does a Refresh objective exist at `hex` right now (RefreshAt non-null)? The one validity
        // rule of a durable Refresh intent (ScoutObjectiveEvaluator.IsIntentStillValid) and of its
        // stealth requirement (StealthForIntent). `needs` shares one observation-need read.
        internal static bool RefreshExists(WorldSnapshot snap, HexCoord hex,
            ObservationNeedLookup needs = null) =>
            RefreshKindAt(snap, hex, needs, exactKind: false, out _) != RefreshKind.None;

        private enum RefreshKind { None, AttackNeed, Stale }

        // THE branch rule of a Refresh at `hex`: an Attack observation need (AttackNeedRefresh),
        // else an ordinary stale Refresh. Both need a known age past their own gates; the
        // observation need (Attack's whole target enumeration outside an Enumerate) is read only
        // when the answer depends on it. `exactKind` false = existence only: when both branches
        // pass, the kind is not asked and Stale stands for "exists".
        private static RefreshKind RefreshKindAt(WorldSnapshot snap, HexCoord hex,
            ObservationNeedLookup needs, bool exactKind, out int age)
        {
            if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, hex, out age))
                return RefreshKind.None;
            bool asNeed = RefreshNeedGatesPass(snap, hex, age);
            bool asStale = RefreshStaleGatesPass(snap, hex, age);
            if (!asNeed && !asStale)
                return RefreshKind.None;
            if (!exactKind && asNeed && asStale)
                return RefreshKind.Stale;
            bool need = needs?.Contains(hex) ?? IsObservationNeed(snap, hex);
            if (need)
                return asNeed ? RefreshKind.AttackNeed : RefreshKind.None;
            return asStale ? RefreshKind.Stale : RefreshKind.None;
        }

        // The age and runnable gates of an Attack observation-need Refresh (AttackNeedRefresh).
        private static bool RefreshNeedGatesPass(WorldSnapshot snap, HexCoord hex, int age) =>
            ReconIntelSnapshotRegistry.IsAttackIntelStale(age)
            && ScoutObjectiveEvaluator.IsAttackObservationFocusRunnable(snap, hex);

        // The age and runnable gates of an ordinary stale Refresh (RefreshAt, BuildRefreshObjectives,
        // ReconContinuity's re-focus waypoints).
        internal static bool RefreshStaleGatesPass(WorldSnapshot snap, HexCoord hex, int age) =>
            ReconIntelSnapshotRegistry.IsStaleAge(age)
            && ScoutObjectiveEvaluator.IsRefreshFocusRunnable(snap, hex);

        // The aviation-only observation pass (ScoutTargetKind.AirSweep). FocusHex = the sweep
        // anchor (WorldAnalysis.TryAirSweepAnchor: true enemy army concentration, else enemy
        // citadel). Its intrinsic value is what one deep pass along the corridor from our nearest
        // base toward the anchor would observe, on the existing Recon TaskScore slots: never-
        // observed hexes (InfoGain), relevance-weighted staleness (Staleness), what the anchor is
        // (StrategicRelevance) and that it is where the enemy is (ThreatDirection). Like every
        // other Recon task it also carries its own price: one sortie (activation AP + launch
        // Energy) from ScoutCostModel — the incumbent wing's own figure when known, else notional.
        public static ReconObjective AirSweepOf(WorldSnapshot snap, int? preferredMoverArmyId = null)
        {
            if (!WorldAnalysis.TryAirSweepAnchor(snap, out HexCoord anchor, out bool armyConcentration))
                return null;
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;
            HexCoord origin = bases != null && bases.Count > 0
                ? bases.OrderBy(b => HexGridMath.Distance(b, anchor)).ThenBy(b => b.Q).ThenBy(b => b.R).First()
                : snap.Self.Citadel;

            int samples = 0, neverObserved = 0;
            float staleWeighted = 0f, weight = 0f;
            HexCoord cur = origin;
            for (int i = 0; i < AiConfigV2.airSweepValueDepth && !cur.Equals(anchor); i++)
            {
                cur = ReconAirCapacityPolicy.SweepEndpoint(cur, anchor, 1);
                samples++;
                float w = AiConfigV2.reconRefreshPressureFloorWeight
                    + ReconIntelSnapshotRegistry.RefreshRelevance(snap, cur);
                weight += w;
                if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, cur, out int age))
                {
                    neverObserved++;
                    // Never observed belongs to InfoGain; there is no old intel to refresh.
                    continue;
                }
                staleWeighted += w * ReconIntelSnapshotRegistry.Staleness(age);
            }
            if (samples == 0)
                return null;

            float anchorRelevance = Mathf.Max(armyConcentration ? 1f : 0.85f,
                ReconIntelSnapshotRegistry.RefreshRelevance(snap, anchor));
            float infoGainRaw = neverObserved / (float)samples;
            float stalenessRaw = weight > 0f ? staleWeighted / weight : 0f;
            float threatDirectionRaw = armyConcentration ? 1f : 0.75f;
            ScoutCostEstimate cost = MissionCost(snap, anchor, ScoutTargetKind.AirSweep,
                StealthRequirement.None, 0f, preferredMoverArmyId);
            TaskScore score = BuildAirSweepScore(snap, infoGainRaw, stalenessRaw,
                anchorRelevance, threatDirectionRaw, cost);

            return new ReconObjective
            {
                Kind = ReconObjectiveKind.AirSweep,
                FocusHex = anchor,
                TaskScore = score,
                BaseValue = score.Value,
                DetectionRisk = 0f,
                Stealth = StealthRequirement.None,
                DistanceFromBase = HexGridMath.Distance(origin, anchor),
                StrategicRelevance = anchorRelevance,
                DirectionPressure = threatDirectionRaw,
            };
        }

        // THE objective a durable Scout intent stands for right now, re-materialised from the
        // snapshot (null when it no longer exists, or for an unknown kind). Mission planning prices
        // it against the incumbent's own actor; ActorCommitments reads its stealth requirement.
        public static ReconObjective ForIntent(WorldSnapshot snap, ScoutIntent si,
            int? preferredMoverArmyId = null)
        {
            if (si == null)
                return null;
            switch (si.Kind)
            {
                case ScoutTargetKind.CaptureStructure: return CaptureAt(snap, si.FocusHex);
                case ScoutTargetKind.Hunt: return HuntAt(snap, si.FocusHex);
                case ScoutTargetKind.DisruptStructure: return DisruptAt(snap, si.FocusHex, preferredMoverArmyId);
                case ScoutTargetKind.Explore: return ExploreAt(snap, si.FocusHex, preferredMoverArmyId);
                case ScoutTargetKind.Refresh: return RefreshAt(snap, si.FocusHex, preferredMoverArmyId);
                case ScoutTargetKind.AirSweep: return AirSweepOf(snap, preferredMoverArmyId);
                default: return null;
            }
        }

        // Exactly `ForIntent(snap, si)?.Stealth ?? StealthRequirement.None` without pricing the
        // objective (ActorCommitments reads only the requirement; 2026-11-15 late game: one
        // commitments build re-priced every incumbent Scout objective, ~5 ms x 1880 per turn).
        // Each branch repeats ForIntent's existence gates of its kind and the requirement its
        // builder assigns; BuildExplore/BuildRefresh never return null once their gates pass.
        // Explore and Refresh require stealth exactly when the focus is exposed, so exposure is
        // read first: an unexposed focus skips the gates (Refresh's observation-need gate runs
        // Attack's target enumeration). Keep in step with ExploreAt / RefreshAt /
        // AttackNeedRefresh / DisruptAt.
        // `needs` (optional) shares one ObservationNeeds read across the intents of one
        // commitments build; without it each Refresh question reads the needs live.
        internal static StealthRequirement StealthForIntent(WorldSnapshot snap, ScoutIntent si,
            ObservationNeedLookup needs = null)
        {
            if (si == null)
                return StealthRequirement.None;
            HexCoord hex = si.FocusHex;
            switch (si.Kind)
            {
                // Capture, Hunt and AirSweep objectives never carry a stealth requirement.
                case ScoutTargetKind.CaptureStructure:
                case ScoutTargetKind.Hunt:
                case ScoutTargetKind.AirSweep:
                    return StealthRequirement.None;
                // A Disruption objective always requires stealth; existence reads DisruptAt's own
                // gates (the mover estimate included), not its score.
                case ScoutTargetKind.DisruptStructure:
                    return DisruptionExists(snap, hex) ? StealthRequirement.Required : StealthRequirement.None;
                case ScoutTargetKind.Explore:
                    return EnemyExposedAt(snap, hex) && ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, hex)
                        ? StealthRequirement.Required : StealthRequirement.None;
                case ScoutTargetKind.Refresh:
                {
                    // Both Refresh branches require stealth exactly when the focus is exposed; the
                    // observation need is read only when the branch gates disagree (today: a
                    // visible-arrival-blocked known site, age past both thresholds).
                    return EnemyExposedAt(snap, hex) && RefreshExists(snap, hex, needs)
                        ? StealthRequirement.Required : StealthRequirement.None;
                }
                default:
                    return StealthRequirement.None;
            }
        }

        // Strike force step 6 — the one rule for an Attack observation need: a site last seen more
        // than attackIntelMaxAgeTurns ago is refreshed as fully stale and maximally relevant.
        private static ReconObjective AttackNeedRefresh(WorldSnapshot snap, HexCoord hex,
            int? preferredMoverArmyId, ref ReconDirectionSnapshot direction)
        {
            // Recon audit B2 — only a HARD block (off-map / impassable terrain) stops the look. A known
            // hostile site is always a visible-arrival block (its garrison would fight, an
            // undefended one would be taken over), so testing that block here dropped EVERY Attack
            // need; the job is observed from a vantage instead (ObservationVantageSelector.UsesVantage).
            if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, hex, out int age)
                || !RefreshNeedGatesPass(snap, hex, age))
                return null;
            if (direction == null)
                direction = DirectionFor(snap);
            return BuildRefresh(snap, hex, age, preferredMoverArmyId, direction, attackNeed: true);
        }

        private static List<ReconObjective> BuildRefreshObjectives(WorldSnapshot snap,
            ref ReconDirectionSnapshot direction)
        {
            var stale = new List<(HexCoord Hex, int Age)>();
            foreach (KeyValuePair<HexCoord, int> kv in ReconIntelSnapshotRegistry.LastObservedFor(snap))
            {
                int age = Mathf.Max(0, snap.TurnNumber - kv.Value);
                // Visibility footprints may extend beyond the generated playfield. Use the same
                // actual-map gate as durable Refresh validity, before pricing or beam selection.
                if (!RefreshStaleGatesPass(snap, kv.Key, age))
                    continue;
                stale.Add((kv.Key, age));
            }
            if (stale.Count > 0 && direction == null)
                direction = DirectionFor(snap);

            int cap = Mathf.Max(AiConfigV2.scoutCandidateBeamWidth * 3,
                ReconConcurrencyPolicy.HardCap * 3);
            int pricedPool = cap * Mathf.Max(1, AiConfigV2.reconRefreshPricedPoolFactor);
            IEnumerable<(HexCoord Hex, int Age)> priced = stale;
            if (stale.Count > pricedPool)
            {
                // Approximate pre-selection (AiConfigV2.reconRefreshPricedPoolFactor): the same
                // score with the optimistic ground price, movers taken once per stealth need.
                // An incumbent Refresh left outside is re-materialised by RefreshAt, as for a hex
                // past the cap.
                var refreshTarget = new ScoutMissionTarget { Kind = ScoutTargetKind.Refresh };
                List<ArmySnapshot> open = ScoutMoverSelector.Eligible(snap, refreshTarget, null);
                refreshTarget.Stealth = StealthRequirement.Required;
                List<ArmySnapshot> hidden = ScoutMoverSelector.Eligible(snap, refreshTarget, null);
                ReconDirectionSnapshot dir = direction;
                priced = stale
                    .Select(s => (Stale: s, Rough: BuildRefresh(snap, s.Hex, s.Age, direction: dir,
                        optimisticMovers: (open, hidden))))
                    .OrderByDescending(x => x.Rough.BaseValue)
                    .ThenByDescending(x => x.Stale.Age)
                    .ThenBy(x => x.Stale.Hex.Q)
                    .ThenBy(x => x.Stale.Hex.R)
                    .Take(pricedPool)
                    .Select(x => x.Stale)
                    .ToList();
            }

            var candidates = new List<ReconObjective>();
            foreach ((HexCoord hex, int age) in priced)
            {
                ReconObjective o = BuildRefresh(snap, hex, age, direction: direction);
                if (o != null)
                    candidates.Add(o);
            }

            List<ReconObjective> kept = candidates
                .OrderByDescending(o => o.BaseValue)
                .ThenByDescending(o => o.AgeTurns)
                .ThenBy(o => o.FocusHex.Q)
                .ThenBy(o => o.FocusHex.R)
                .Take(cap)
                .ToList();
            // 2026-11-15 (P3) — which stale map the cap keeps: east of Brynn's map (Concord Base)
            // left the Refresh list at T20 and never returned. One line per turn.
            if (stale.Count > 0)
                AiDebugLog.WriteDeduped($"recon-refresh-pool#{snap.Observer?.ColorIndex}#{snap.TurnNumber}",
                    $"[AI][V2][Recon][Refresh] {snap.Observer?.Nickname} T{snap.TurnNumber} stale={stale.Count} "
                    + $"priced={candidates.Count} kept={kept.Count}/{cap} "
                    + $"cut={(kept.Count > 0 ? kept[kept.Count - 1].BaseValue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "-")} "
                    + $"kept=[{string.Join(" ", kept.Select(o => $"({o.FocusHex.Q},{o.FocusHex.R})"))}]");
            return kept;
        }

        private static ScoutCostEstimate MissionCost(WorldSnapshot snap, HexCoord hex,
            ScoutTargetKind kind, StealthRequirement stealth, float detectionRisk,
            int? preferredMoverArmyId = null) =>
            ScoutCostModel.Estimate(snap, new ScoutMissionTarget
            {
                Kind = kind,
                FocusHex = hex,
                Stealth = stealth,
                DetectionRisk = detectionRisk,
            }, preferredMoverArmyId);

        // Each Recon task has one visible external-score assembly point. Helpers above/below provide
        // raw world/route facts only; conversion to score units happens here through TaskScoreEvaluator.
        // The value is one pass (airSweepValueDepth hexes), so the price is one sortie: the
        // activation AP it spends now plus its launch Energy — no multi-turn Delivery.
        private static TaskScore BuildAirSweepScore(WorldSnapshot snap, float infoGainRaw,
            float stalenessRaw, float strategicRelevanceRaw, float threatDirectionRaw,
            ScoutCostEstimate cost)
        {
            float launchEnergy = Mathf.Max(0f, cost.EnergyDesired);
            return new TaskScore(
                infoGain: TaskScoreEvaluator.InfoGain(infoGainRaw),
                staleness: TaskScoreEvaluator.PositiveStaleness(stalenessRaw),
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(strategicRelevanceRaw),
                threatDirection: TaskScoreEvaluator.ThreatDirection(threatDirectionRaw),
                cardPrice: TaskScoreEvaluator.Price(Mathf.Max(0f, cost.ActivationApNow)
                    + ActionPrice.Resources(t => t == Game.Economy.ResourceType.Energy
                        ? launchEnergy : 0f, snap)));
        }

        internal static TaskScore BuildExploreScore(WorldSnapshot snap, HexCoord hex,
            float infoGainRaw, int homeDistance, ScoutCostEstimate cost, float detectionRiskRaw,
            bool? observationNeed = null)
        {
            float activationNow = Mathf.Max(0f, cost.ActivationApNow);
            float stealthEntryNow = Mathf.Max(0f, cost.ApDesired - activationNow);
            float homeWeight = Mathf.Lerp(1f, AiConfigV2.scoutExploreHomeProximityWeight,
                LocalExplorationGap(snap));
            float relevance = (observationNeed ?? IsObservationNeed(snap, hex))
                ? 1f : RuinsRelevance(snap, hex);
            bool eventVisit = ScoutObjectiveEvaluator.IsKnownUnguardedEvent(snap, hex);
            return new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(relevance),
                threatDirection: TaskScoreEvaluator.ThreatDirection(
                    DirectionalPressure(snap, hex, DirectionFor(snap))) * (1f + Mathf.Clamp01(HomeThreat(snap))),
                infoGain: TaskScoreEvaluator.InfoGain(infoGainRaw),
                eventReward: eventVisit ? TaskScoreEvaluator.EventReward(-1) : 0f,
                citadelThreatRisk: eventVisit ? TaskScoreEvaluator.CitadelThreatRisk(snap) : 0f,
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance) * homeWeight,
                cardPrice: TaskScoreEvaluator.Price(activationNow + stealthEntryNow),
                delivery: TaskScoreEvaluator.Price(ActionPrice.RecurringAp(
                    cost.RecurringActivationAp, cost.EtaTurns)),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(detectionRiskRaw));
        }

        // `homeThreat` (0..1, ThreatModel Citadel/Base severity): 2026-10-15 (user decision) — while a
        // known enemy threatens home, watching the direction it comes from weighs up to twice as much
        // as in peace (playtest 2026-10-15: a far ruins Explore outscored the Refresh toward Sentinels).
        private static TaskScore BuildRefreshScore(float stalenessRaw, float strategicRelevanceRaw,
            float threatDirectionRaw, int homeDistance, ScoutCostEstimate cost,
            float detectionRiskRaw, float homeThreat, float attackReadinessRaw = 0f,
            float unblockedAttackValue = 0f)
        {
            float activationNow = Mathf.Max(0f, cost.ActivationApNow);
            float stealthEntryNow = Mathf.Max(0f, cost.ApDesired - activationNow);
            return new TaskScore(
                attackReadiness: attackReadinessRaw > 0f
                    ? TaskScoreEvaluator.AttackReadiness(attackReadinessRaw) + Mathf.Max(0f, unblockedAttackValue) : 0f,
                staleness: TaskScoreEvaluator.PositiveStaleness(stalenessRaw),
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(strategicRelevanceRaw),
                threatDirection: TaskScoreEvaluator.ThreatDirection(threatDirectionRaw)
                    * (1f + Mathf.Clamp01(homeThreat)),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                cardPrice: TaskScoreEvaluator.Price(activationNow + stealthEntryNow),
                delivery: TaskScoreEvaluator.Price(ActionPrice.RecurringAp(
                    cost.RecurringActivationAp, cost.EtaTurns)),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(detectionRiskRaw));
        }

        internal static ReconObjective BuildExplore(WorldSnapshot snap, HexCoord hex, int freshNeighbors,
            int distFromBase, bool enemyExposure, bool stealthDetectionRisk,
            int? preferredMoverArmyId = null)
        {
            float infoGainRaw = Mathf.Clamp01(
                freshNeighbors / Mathf.Max(0.0001f, AiConfigV2.scoutInfoGainNorm));
            infoGainRaw *= ExploreObservationFreshnessFactor(snap, hex);
            // 2026-11-09 (match P1) — a live Attack's observation need that was never seen is
            // maximally unknown: full InfoGain, the same rule as staleRaw = 1 for a need seen
            // long ago (AttackNeedRefresh). Without it the enemy citadel scored below home Refreshes.
            bool observationNeed = IsObservationNeed(snap, hex);
            if (observationNeed)
                infoGainRaw = 1f;

            int homeDist = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex, distFromBase);
            StealthRequirement req = enemyExposure ? StealthRequirement.Required : StealthRequirement.None;
            float riskRaw = enemyExposure
                ? Mathf.Max(stealthDetectionRisk
                        ? 1f / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm) : 0f,
                    ScoutRiskModel.DetectorRisk(snap, hex))
                : 0f;
            ScoutCostEstimate cost = MissionCost(snap, hex, ScoutTargetKind.Explore, req, riskRaw,
                preferredMoverArmyId);

            TaskScore score = BuildExploreScore(snap, hex, infoGainRaw, homeDist, cost, riskRaw,
                observationNeed);

            return new ReconObjective
            {
                Kind = ReconObjectiveKind.Explore,
                FocusHex = hex,
                TaskScore = score,
                BaseValue = score.Value,
                DetectionRisk = riskRaw,
                Stealth = req,
                FreshNeighbors = freshNeighbors,
                DistanceFromBase = distFromBase,
            };
        }

        // An unvisited City-ruins focus signals a Hex Event (a public map rule). Discovery is
        // priced through StrategicRelevance. Known unguarded rewards are priced separately in
        // BuildExploreScore, and a known guard's reward is priced by Raid.
        private static float RuinsRelevance(WorldSnapshot snap, HexCoord hex)
        {
            ISet<HexCoord> ruins = snap?.MapKnowledge?.UnvisitedRuinsHexes;
            return ruins != null && ruins.Contains(hex) ? AiConfigV2.reconRuinsRelevance : 0f;
        }

        // Average [floor..1] information-retention factor over the Explore focus and the unvisited,
        // on-map, non-blocked neighbours that make up its FreshNeighbors count.
        private static float ExploreObservationFreshnessFactor(WorldSnapshot snap, HexCoord focus)
        {
            float floor = Mathf.Clamp01(AiConfigV2.scoutExploreObservedInfoDiscountFloor);
            float sum = HexObservationRetention(snap, focus, floor);
            int count = 1;

            MapKnowledgeSnapshot mk = snap?.MapKnowledge;
            foreach (HexCoord n in HexGridMath.Neighbors(focus))
            {
                if (mk?.AllHexes != null && !mk.IsOnMap(n)) continue;
                if (mk?.VisitedHexSet != null && mk.VisitedHexSet.Contains(n)) continue;
                if (mk != null && mk.IsBlockedForScout(n, stealthCapable: false)) continue;
                sum += HexObservationRetention(snap, n, floor);
                count++;
            }
            return count > 0 ? sum / count : 1f;
        }

        private static float HexObservationRetention(WorldSnapshot snap, HexCoord hex, float floor)
        {
            if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, hex, out int age))
                return 1f;
            return Mathf.Lerp(floor, 1f, ReconIntelSnapshotRegistry.Staleness(age));
        }

        // `attackNeed` — a live Attack operation's target (AttackNeedRefresh): stale and relevant
        // for its purpose regardless of the generic staleness ramp.
        private static ReconObjective BuildRefresh(WorldSnapshot snap, HexCoord hex, int age,
            int? preferredMoverArmyId = null, ReconDirectionSnapshot direction = null,
            bool attackNeed = false,
            (IReadOnlyList<ArmySnapshot> Open, IReadOnlyList<ArmySnapshot> Hidden)? optimisticMovers = null)
        {
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;
            int distBase = bases != null && bases.Count > 0 ? MinDist(bases, hex) : 0;
            int homeDist = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex, distBase);
            float staleRaw = attackNeed ? 1f : ReconIntelSnapshotRegistry.Staleness(age);

            float strategicRaw = attackNeed ? 1f : ReconIntelSnapshotRegistry.RefreshRelevance(snap, hex);
            direction = direction ?? DirectionFor(snap);
            float directionalRaw = DirectionalPressure(snap, hex, direction);

            bool exposed = EnemyExposedAt(snap, hex);
            float riskRaw = exposed ? ScoutRiskModel.DetectorRisk(snap, hex) : 0f;
            StealthRequirement req = exposed ? StealthRequirement.Required : StealthRequirement.None;
            // `optimisticMovers` — the pool's approximate pre-selection only (BuildRefreshObjectives):
            // the cheap optimistic price against movers taken once; the kept hexes are re-priced exactly.
            ScoutCostEstimate cost = optimisticMovers.HasValue
                ? ScoutCostModel.OptimisticGroundEstimate(snap, new ScoutMissionTarget
                    {
                        Kind = ScoutTargetKind.Refresh, FocusHex = hex, Stealth = req, DetectionRisk = riskRaw,
                    },
                    req == StealthRequirement.Required ? optimisticMovers.Value.Hidden : optimisticMovers.Value.Open)
                : MissionCost(snap, hex, ScoutTargetKind.Refresh, req, riskRaw, preferredMoverArmyId);
            // 2026-11-13 (P1, owner) — the observation a LIVE Attack waits for is a step of that
            // operation: it keeps Attack's priority (AttackReadiness full), the DisruptAt
            // precedent, so it can take a scout off a routine lane. Korrin T29: Refresh(1,-6)
            // scored 7.1 against the routine Refresh(2,-4) at 12.3 and lost the scout one hex away.
            MissionIntent liveAttack = attackNeed ? LiveAssaultAt(snap, hex) : null;
            bool liveAttackNeed = liveAttack != null;
            TaskScore score = BuildRefreshScore(staleRaw, strategicRaw, directionalRaw,
                homeDist, cost, riskRaw, HomeThreat(snap), liveAttackNeed ? 1f : 0f,
                UnblockedAttackValue(snap, liveAttack, disruption: false));

            var objective = new ReconObjective
            {
                Kind = ReconObjectiveKind.Refresh,
                FocusHex = hex,
                TaskScore = score,
                BaseValue = score.Value,
                DetectionRisk = riskRaw,
                Stealth = req,
                DistanceFromBase = distBase,
                AgeTurns = age,
                StrategicRelevance = strategicRaw,
                DirectionPressure = directionalRaw,
            };

            if (strategicRaw > 0f && !optimisticMovers.HasValue)
            {
                var auditPlayer = snap.Self.Armies?.FirstOrDefault(a => a?.Owner != null)?.Owner;
                if (auditPlayer != null)
                    ReconAcceptanceAudit.RecordStaleStrategicRefresh(auditPlayer, snap.TurnNumber,
                        hex, age, strategicRaw);
            }
            return objective;
        }

        private static int MinDist(IReadOnlyList<HexCoord> hexes, HexCoord to) => AiV2Util.MinDist(hexes, to);

        private static bool EnemyExposedAt(WorldSnapshot snap, HexCoord hex) =>
            ScoutRiskModel.IsExposed(snap?.Known?.EnemySightings, hex);

        private static int DetectorsAt(WorldSnapshot snap, HexCoord hex) =>
            ScoutRiskModel.CountDetectors(snap?.Known?.EnemySightings, hex);
    }
}
