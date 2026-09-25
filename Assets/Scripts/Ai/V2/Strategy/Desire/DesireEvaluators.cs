using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  DESIRE EVALUATORS  (Strategy V2 build-order step 3)
    // ===========================================================================================
    //  Turns one WorldSnapshot into the normalised Radar. Structure:
    //
    //    snapshot ─► ReconEvaluator     ─┐
    //             ─► AggressionEvaluator ─┤  raw independent intensities [0..1]  (DesireVector.Raw)
    //             ─► (DEF/ECO/DEV: flat placeholder until their own evaluators land)
    //                                     │  + out-of-simplex scalars MilitaryThreat / EconomicRunway
    //                                     ▼
    //                        Radar.Normalize  (THE single normalisation point, N-axis)
    //                                     ▼
    //                                   Radar   (weights sum to 1)
    //
    //  Each evaluator is response curves (one curve per input factor -> contribution, summed),
    //  the V1 AiStrategyDirector style — NOT fuzzy logic. Reads ONLY the snapshot (+ AiConfigV2,
    //  + WorthIt/AiPower which are pure). No registries, no live game state — that is what lets
    //  Tools/radar-sim drive it against hand-built snapshots.
    //
    //  RECON is ONE axis with THREE named contributions (see AiConfigV2):
    //    exploration   — DECAYS as the reachable map opens (state-driven, no turn term).
    //    surveillance  — SUSTAINED all game: a non-burning baseline + a stale-contact bump.
    //    enemyBlindness— an opponent is fielded (honest opponent list) but we have zero honest
    //                    sightings of it. Magnitude only.
    //
    //  The deep-rework keeps those raw diagnostics but derives two continuous strategic lanes:
    //    ExplorePressure — frontier pressure from unexplored reachable ground.
    //    RefreshPressure — frozen per-hex IntelAge pressure, never-observed cells excluded, with
    //                      contact surveillance as a floor so known stale enemies still matter.
    //  The frozen IntelAge snapshot is captured during WorldAnalysis and therefore cannot change
    //  retroactively while the operational phase is moving scouts one hex at a time.
    //
    //  AGGRESSION is ONE axis with TWO internal drivers, combined by max():
    //    raidOpportunity — "a profitable target I can take right now" (opportunity + surplus +
    //                      relativeEdge + momentum).  `opportunity` comes from the shared
    //                      CombatOpportunityAnalyzer — never a private aggression-only estimator.
    //    warPressure     — "economy secure and free force available against a KNOWN target"
    //                      (surplus + ecoGate + relativeEdge). Military-potential saturation is
    //                      Attack-only intrinsic value in AttackObjectiveEvaluator.
    //    raw = knownTargetGate * max(raidOpportunity, warPressure)
    //          * (UnderSiege ? aggSiegeDamp : 1).
    //    Military readiness without a known raid target is NOT aggression; in the blind opening it
    //    must leave budget to Recon so a neutral/enemy target can actually be discovered first.
    //
    //  The breakdown (DesireBreakdown) is returned alongside the vector so MissionLayer picks the
    //  RIGHT mission from it (exploration -> VisitHex, surveillance -> watch a stale zone,
    //  enemyBlindness -> AirRecon; BestOpportunity -> the raid target) instead of re-deriving the
    //  same analysis and drifting from it.
    //
    //  Smoothing: symmetric low-pass on Recon + Aggression only (AiConfigV2.desireSmoothing).
    //  ECO/DEV placeholders and the two out-of-simplex scalars are unsmoothed — a threat scalar
    //  that means "existential" must react the turn it becomes true.
    // ===========================================================================================

    internal static class Curves
    {
        public static float Ramp(float v, float lo, float hi) =>
            Mathf.Clamp01((v - lo) / Mathf.Max(0.0001f, hi - lo));

        public static float InvRamp(float v, float lo, float hi) => 1f - Ramp(v, lo, hi);
    }

    public sealed class DesireBreakdown
    {
        public float ReconExploration;
        public float ReconSurveillance;
        public float ReconEnemyBlindness;
        public float ReconExplorePressure;
        public float ReconRefreshPressure;

        public float AggRaidOpportunity;
        // ATK §37 — an OPERATIONAL Attack lane pressure inside the existing Aggression axis, for
        // diagnostics and for the offensive gate below. Explicitly not a new Radar axis: Radar
        // still normalises exactly the same four DesireAxis values it always has.
        public float AggAttackPressure;
        // The normalised canonical TaskScore of the best currently-known Attack objective, and how
        // many such objectives exist. Both are plain world facts carried for the gate and the log.
        public float AggBestAttackOpportunity;
        public int AggAttackTargetCount;
        public float AggActiveDefencePressure;
        public float AggWarPressure;
        public float AggOpportunity;
        public float AggSurplus;
        public float AggRelativeEdge;
        public float AggMomentum;

        public CombatOpportunity BestOpportunity = CombatOpportunity.None;
        public CombatOpportunityReport OpportunityReport = new CombatOpportunityReport();
        public float RequiredDefensiveReserve;
        public float OffensiveFreePower;

        public ResourceType EconomyPrimaryResource;
        public float EconomyMaxDeficit;
        public float EconomyMeanDeficit;
        public float EconomyIncomeGap;
        public float EconomyRelativeGap;
        public float EconomyRunwayGap;
        public float EconomyOperationalPressure;
        public float EconomyActionableGate;
        public float EconomyRaw;

        // Development — the desire factors, kept for the "why" log.
        public float DevFacilityReady;      // 0/1 — a facility with a qualifying hero exists (hint only, NOT a gate)
        public float DevSurplusFraction;    // [0..1] resource headroom above the reservation floors
        public float DevOfferingQuality;    // [0..1] max(ready-offering quality, latent target pressure)
        public float DevBestSuccessChance;  // raw p of the best affordable offering
        public int   DevUpgradeTargets;
        public bool  DevPathViable;         // a facility exists / can be built — else latent appetite is 0
    }

    public sealed class RadarAssessment
    {
        public DesireVector Desires;
        public DesireBreakdown Breakdown;
        public Radar Radar;
    }

    public sealed class AiRadarState
    {
        public readonly Dictionary<DesireAxis, float> Smoothed = new Dictionary<DesireAxis, float>();
        public float PrevOwnPower;
        public float EnemyLossPulse;
        public float OwnLossPulse;
        public int LastTurn = -1;
        public List<ObservedContact> PrevObservedEnemies = new List<ObservedContact>();

        public struct ObservedContact
        {
            public PlayerSetupData Owner;
            public HexCoord Hex;
            public float Power;
        }
    }

    public static class AiRadarStateRegistry
    {
        private static readonly Dictionary<PlayerSetupData, AiRadarState> ByPlayer =
            new Dictionary<PlayerSetupData, AiRadarState>();

        public static AiRadarState GetOrCreate(PlayerSetupData player)
        {
            if (player == null)
                return new AiRadarState();
            if (!ByPlayer.TryGetValue(player, out AiRadarState s))
                ByPlayer[player] = s = new AiRadarState();
            return s;
        }

        public static void Clear() => ByPlayer.Clear();
    }

    public static class StrategyLayer
    {
        public static RadarAssessment Evaluate(WorldSnapshot snapshot, AiRadarState state)
        {
            state = state ?? new AiRadarState();
            var breakdown = new DesireBreakdown();
            var desires = new DesireVector();

            if (snapshot?.Self == null)
            {
                foreach (DesireAxis a in DesireAxes.All)
                    desires.Raw[a] = 0.5f;
                return new RadarAssessment
                {
                    Desires = desires, Breakdown = breakdown, Radar = Radar.Normalize(desires),
                };
            }

            bool underSiege = snapshot.Threat != null && snapshot.Threat.UnderSiege;

            // Reaction refreshes objective facts but never advances Radar twice in one turn.
            bool firstEvaluationThisTurn = state.LastTurn != snapshot.TurnNumber;
            float enemyDropFrac, ownDropFrac;
            if (firstEvaluationThisTurn)
                UpdateLossPulses(snapshot, state, out enemyDropFrac, out ownDropFrac);
            else
                enemyDropFrac = ownDropFrac = 0f;
            float momentum = Mathf.Clamp01(0.5f + 0.5f * state.EnemyLossPulse - 0.5f * state.OwnLossPulse);

            float exploration = ReconExploration(snapshot);
            float surveillance = ReconSurveillance(snapshot);
            float blindness = ReconEnemyBlindness(snapshot);
            float explorePressure = exploration;
            float refreshPressure = ReconRefreshPressure(snapshot, surveillance);
            float rawRecon = Mathf.Clamp01(
                AiConfigV2.reconWeightExploration * explorePressure
                + AiConfigV2.reconWeightSurveillance * refreshPressure
                + AiConfigV2.reconWeightBlindness * blindness);

            breakdown.ReconExploration = exploration;
            breakdown.ReconSurveillance = surveillance;
            breakdown.ReconEnemyBlindness = blindness;
            breakdown.ReconExplorePressure = explorePressure;
            breakdown.ReconRefreshPressure = refreshPressure;

            CombatOpportunityReport opp = CombatOpportunityAnalyzer.Analyze(snapshot);
            // Raid opportunity is computed only from neutral targets. Hostile field armies do not
            // manufacture Raid pressure; their honest threat severity feeds ActiveDefence below,
            // within this same Aggression axis.
            float opportunity = opp.BestNeutralOpportunity.HasTarget
                ? opp.BestNeutralOpportunity.OpportunityScore : 0f;

            ComputeSurplus(snapshot, out float requiredReserve, out float freePower);
            float surplus = Curves.Ramp(freePower / Mathf.Max(1f, snapshot.Self.TotalPower),
                AiConfigV2.aggSurplusRampLo, AiConfigV2.aggSurplusRampHi);

            float ownPower = Mathf.Max(snapshot.Self.FieldPower, snapshot.Self.BestStackPotential);
            float enemyPower = snapshot.Known?.EnemyKnownStrength ?? 0f;
            float relativeEdge = enemyPower < 1f
                ? AiConfigV2.aggRelEdgeNoIntel
                : Curves.Ramp(ownPower / enemyPower, AiConfigV2.aggRelEdgeRampLo, AiConfigV2.aggRelEdgeRampHi);

            float ecoSecurity = snapshot.Economy != null ? snapshot.Economy.EconomicSecurity : 0.5f;
            float ecoGate = Mathf.Lerp(AiConfigV2.aggEcoGateLo, 1f, Mathf.Clamp01(ecoSecurity));

            float raidOpportunity =
                AiConfigV2.aggRaidOppWeightOpportunity * opportunity
                + AiConfigV2.aggRaidOppWeightSurplus * surplus
                + AiConfigV2.aggRaidOppWeightRelEdge * relativeEdge
                + AiConfigV2.aggRaidOppWeightMomentum * momentum;
            float warPressure =
                AiConfigV2.aggWarWeightSurplus * surplus
                + AiConfigV2.aggWarWeightEcoGate * ecoGate
                + AiConfigV2.aggWarWeightRelEdge * relativeEdge;

            // ATK §36 — the offensive gate is no longer "a neutral target exists". A known hostile
            // Base/Citadel is an equally real reason to want to be offensive, and while the gate
            // was neutral-only the whole war half of Aggression could never fire on a map whose
            // neutrals had all been cleared.
            List<AttackObjective> attackObjectives = AttackObjectiveEvaluator.Enumerate(snapshot);
            float attackOpportunity = BestAttackOpportunity(attackObjectives);
            float attackPressure =
                AiConfigV2.aggAttackWeightOpportunity * attackOpportunity
                + AiConfigV2.aggAttackWeightWarPressure * warPressure
                + AiConfigV2.aggAttackWeightSurplus * surplus
                + AiConfigV2.aggAttackWeightRelEdge * relativeEdge;

            bool hasKnownCombatTarget = HasOffensiveTarget(opp, attackObjectives);
            float offensivePressure = hasKnownCombatTarget
                ? Mathf.Clamp01(Mathf.Max(raidOpportunity,
                        Mathf.Max(warPressure, attackPressure)))
                    * (underSiege ? AiConfigV2.aggSiegeDamp : 1f)
                : 0f;
            float activeDefencePressure = snapshot.Threat?.Threats?
                .Where(t => t?.Contact?.Army != null
                    && t.Contact.Army.ArmyId >= 0
                    && t.Contact.Source == ContactSource.Honest
                    && t.Contact.Position.HasValue
                    && t.Contact.Army.Owner != null
                    && !t.Contact.Army.Owner.IsNeutral)
                .Select(t => t.Severity).DefaultIfEmpty(0f).Max() ?? 0f;
            float rawAggression = Mathf.Clamp01(Mathf.Max(offensivePressure,
                activeDefencePressure));

            breakdown.AggRaidOpportunity = Mathf.Clamp01(raidOpportunity);
            breakdown.AggAttackPressure = Mathf.Clamp01(attackPressure);
            breakdown.AggBestAttackOpportunity = attackOpportunity;
            breakdown.AggAttackTargetCount = attackObjectives.Count;
            breakdown.AggActiveDefencePressure = Mathf.Clamp01(activeDefencePressure);
            breakdown.AggWarPressure = Mathf.Clamp01(warPressure);
            breakdown.AggOpportunity = opportunity;
            breakdown.AggSurplus = surplus;
            breakdown.AggRelativeEdge = relativeEdge;
            breakdown.AggMomentum = momentum;
            breakdown.BestOpportunity = opp.Best;
            breakdown.OpportunityReport = opp;
            breakdown.RequiredDefensiveReserve = requiredReserve;
            breakdown.OffensiveFreePower = freePower;

            float recon = Smooth(state, DesireAxis.Recon, rawRecon, firstEvaluationThisTurn);
            float aggression = Smooth(state, DesireAxis.Aggression, rawAggression, firstEvaluationThisTurn);

            float rawEconomy = EconomyDesire(snapshot, breakdown);
            float rawDev = DevelopmentDesire(snapshot, breakdown);

            desires.Raw[DesireAxis.Recon] = recon;
            desires.Raw[DesireAxis.Aggression] = aggression;
            desires.Raw[DesireAxis.Economy] = Smooth(state, DesireAxis.Economy, rawEconomy, firstEvaluationThisTurn);
            desires.Raw[DesireAxis.Development] = Smooth(state, DesireAxis.Development, rawDev, firstEvaluationThisTurn);

            desires.MilitaryThreat = MilitaryThreat(snapshot, underSiege);
            desires.EconomicRunway = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ecoSecurity));

            if (firstEvaluationThisTurn)
            {
                state.PrevOwnPower = snapshot.Self.TotalPower;
                state.PrevObservedEnemies = CurrentObservedEnemies(snapshot);
                state.LastTurn = snapshot.TurnNumber;
            }

            Radar radar = Radar.Normalize(desires);
            LogDesires(desires, breakdown, radar, rawRecon, rawAggression, rawEconomy, rawDev,
                enemyDropFrac, ownDropFrac,
                state, opp);

            return new RadarAssessment { Desires = desires, Breakdown = breakdown, Radar = radar };
        }

        // Recon lane pressures are the only sub-terms MissionLayer needs on every settled step
        // (ReconMissionPlanner.Propose runs mid-turn against a LIVE snapshot while the rest of
        // DesireBreakdown/Radar stays frozen from the turn's single Evaluate() — recomputing the
        // whole radar mid-turn would re-introduce the oscillation that decision explicitly
        // avoided). This mutates ONLY the Explore/Refresh/Surveillance/Blindness fields in place
        // on the already-frozen breakdown, from the current snapshot, so a frontier completion
        // mid-turn is reflected before the next mission is proposed. No smoothing, no radar
        // renormalization, no other axis touched.
        public static void RefreshReconLanePressures(WorldSnapshot snapshot, DesireBreakdown breakdown)
        {
            if (snapshot?.Self == null || snapshot.MapKnowledge == null || breakdown == null)
                return;
            float exploration = ReconExploration(snapshot);
            float surveillance = ReconSurveillance(snapshot);
            float blindness = ReconEnemyBlindness(snapshot);
            float refreshPressure = ReconRefreshPressure(snapshot, surveillance);
            breakdown.ReconExploration = exploration;
            breakdown.ReconSurveillance = surveillance;
            breakdown.ReconEnemyBlindness = blindness;
            breakdown.ReconExplorePressure = exploration;
            breakdown.ReconRefreshPressure = refreshPressure;
        }

        // The Aggression counterpart of RefreshReconLanePressures. After a settled
        // combat/movement step, the frozen turn-start CombatOpportunityReport can describe a target
        // that is already dead (or a neutral that just became reachable). Rebuild ONLY the
        // operational Aggression facts from the fresh snapshot: the opportunity report, the
        // best/neutral reads and the raidOpportunity sub-driver. Radar is NOT renormalized
        // mid-turn — exactly the same discipline the Recon lane refresh follows.
        public static void RefreshAggressionLanePressures(WorldSnapshot snapshot, DesireBreakdown breakdown)
        {
            if (snapshot?.Self == null || breakdown == null)
                return;

            CombatOpportunityReport opp = CombatOpportunityAnalyzer.Analyze(snapshot);
            float opportunity = opp.BestNeutralOpportunity.HasTarget
                ? opp.BestNeutralOpportunity.OpportunityScore : 0f;

            ComputeSurplus(snapshot, out float requiredReserve, out float freePower);
            float surplus = Curves.Ramp(freePower / Mathf.Max(1f, snapshot.Self.TotalPower),
                AiConfigV2.aggSurplusRampLo, AiConfigV2.aggSurplusRampHi);

            float ownPower = Mathf.Max(snapshot.Self.FieldPower, snapshot.Self.BestStackPotential);
            float enemyPower = snapshot.Known?.EnemyKnownStrength ?? 0f;
            float relativeEdge = enemyPower < 1f
                ? AiConfigV2.aggRelEdgeNoIntel
                : Curves.Ramp(ownPower / enemyPower, AiConfigV2.aggRelEdgeRampLo, AiConfigV2.aggRelEdgeRampHi);

            // Momentum is a cross-turn smoothed signal owned by the once-per-turn Evaluate; reuse
            // the already-frozen value rather than re-pulsing it mid-turn.
            float raidOpportunity =
                AiConfigV2.aggRaidOppWeightOpportunity * opportunity
                + AiConfigV2.aggRaidOppWeightSurplus * surplus
                + AiConfigV2.aggRaidOppWeightRelEdge * relativeEdge
                + AiConfigV2.aggRaidOppWeightMomentum * breakdown.AggMomentum;

            breakdown.OpportunityReport = opp;
            breakdown.BestOpportunity = opp.Best;
            breakdown.AggOpportunity = opportunity;
            breakdown.AggSurplus = surplus;
            breakdown.AggRelativeEdge = relativeEdge;
            breakdown.AggRaidOpportunity = Mathf.Clamp01(raidOpportunity);
            // ATK §36/§67 — the Attack lane's operational facts are exactly as perishable as the
            // Raid lane's within one turn: a settled step can capture the target, reveal a fresh
            // one or change the defender package. Rebuild them from the fresh snapshot on the same
            // terms, and — like raidOpportunity above — reuse the turn-frozen cross-turn signals
            // (warPressure's force/eco terms) rather than re-pulsing Radar mid-turn.
            List<AttackObjective> attackObjectives = AttackObjectiveEvaluator.Enumerate(snapshot);
            float attackOpportunity = BestAttackOpportunity(attackObjectives);
            breakdown.AggBestAttackOpportunity = attackOpportunity;
            breakdown.AggAttackTargetCount = attackObjectives.Count;
            breakdown.AggAttackPressure = Mathf.Clamp01(
                AiConfigV2.aggAttackWeightOpportunity * attackOpportunity
                + AiConfigV2.aggAttackWeightWarPressure * breakdown.AggWarPressure
                + AiConfigV2.aggAttackWeightSurplus * surplus
                + AiConfigV2.aggAttackWeightRelEdge * relativeEdge);
            breakdown.RequiredDefensiveReserve = requiredReserve;
            breakdown.OffensiveFreePower = freePower;
        }

        // ATK §38 — an easy capture must be able to raise offensive pressure on its own merit, but
        // only through the SAME canonical world score every other task is measured on. There is no
        // Attack-local scale here: DemandUrgencyPolicy.NormalizedWorldValue is the existing owner
        // of "how urgent is a world value", already used by Development and StrategicCardEvaluator.
        // ATK §36 — the ONE offensive gate for the Aggression axis. Either family of offensive
        // target is sufficient on its own: a neutral army/event guard Raid can pursue, or a known
        // hostile Base/Citadel Attack can capture. While this asked only about neutrals, the whole
        // war half of Aggression was silently unreachable on a map whose neutrals were cleared.
        internal static bool HasOffensiveTarget(CombatOpportunityReport opp,
            List<AttackObjective> attackObjectives) =>
            (opp?.NeutralOpportunities != null && opp.NeutralOpportunities.Count > 0)
            || (attackObjectives != null && attackObjectives.Count > 0);

        internal static float BestAttackOpportunity(List<AttackObjective> objectives)
        {
            float best = 0f;
            if (objectives == null)
                return best;
            foreach (AttackObjective o in objectives)
            {
                float normalized = DemandUrgencyPolicy.NormalizedWorldValue(o.BaseValue);
                if (normalized > best)
                    best = normalized;
            }
            return best;
        }

        private static float ReconExploration(WorldSnapshot snap)
        {
            float explorable = snap.MapKnowledge != null ? snap.MapKnowledge.ExplorableUnknownFrac : 0f;
            return Curves.Ramp(explorable, AiConfigV2.reconExploreRampLo, AiConfigV2.reconExploreRampHi);
        }

        private static float EconomyDesire(WorldSnapshot snap, DesireBreakdown b)
        {
            IReadOnlyList<EconomyResourceStanding> resources = snap?.Economy?.PerType;
            if (resources == null || resources.Count == 0)
                return 0f;

            EconomyResourceStanding primary = resources
                .OrderByDescending(x => x.DeficitScore)
                .ThenBy(x => x.Type)
                .First();
            float max = Mathf.Clamp01(primary.DeficitScore);
            float mean = Mathf.Clamp01(resources.Average(x => x.DeficitScore));
            float raw = Mathf.Clamp01(AiConfigV2.economyDesireMaxWeight * max
                + AiConfigV2.economyDesireMeanWeight * mean);
            float gate = snap.Economy.HasActionableOpportunity
                ? 1f : AiConfigV2.economyLatentMultiplier;

            b.EconomyPrimaryResource = primary.Type;
            b.EconomyMaxDeficit = max;
            b.EconomyMeanDeficit = mean;
            b.EconomyIncomeGap = primary.IncomeGap;
            b.EconomyRelativeGap = primary.RelativeIncomeGap;
            b.EconomyRunwayGap = 1f - primary.RunwayCoverage;
            b.EconomyOperationalPressure = primary.OperationalPressure;
            b.EconomyActionableGate = gate;
            b.EconomyRaw = raw * gate;
            return b.EconomyRaw;
        }

        // Development desire over snapshot.Development (built once in the scan, shared with
        // DevelopmentOpportunityEvaluator). NO hard facility+hero gate — that made the axis a pure
        // execution-readiness signal and the vector went permanently dormant whenever the operator
        // hero never showed up on its own. Instead, like Recon's explore pressure:
        //   rawDev = surplus * quality * gain,  quality = max(readyQuality, latentQuality)
        //     readyQuality  — a facility + qualifying hero + affordable offered cards exist, so a
        //                     Challenge can run THIS turn (best success chance + target count).
        //     latentQuality — no live offering yet, but there is something worth developing and a
        //                     path to a facility exists. Keeps the axis warm so DemandLayer can
        //                     STAGE the prerequisite (build the facility / move an operator hero
        //                     onto it), capped at devLatentPotential.
        //   surplus stays a hard multiplier — a Challenge stakes real H/E/M/T with a random return.
        // Reads ONLY the shared readiness object — no live game-state read.
        private static float DevelopmentDesire(WorldSnapshot snap, DesireBreakdown b)
        {
            DevelopmentReadiness rd = snap?.Development;
            if (rd == null)
                return 0f;

            float surplus = Curves.Ramp(rd.SurplusFraction,
                AiConfigV2.devSurplusRampLo, AiConfigV2.devSurplusRampHi);
            float targetPressure = Curves.Ramp(rd.UpgradeTargetCount,
                AiConfigV2.devTargetRampLo, AiConfigV2.devTargetRampHi);

            float readyQuality = rd.Offerings.Count == 0 ? 0f : Mathf.Clamp01(
                AiConfigV2.devWeightSuccessChance * rd.BestSuccessChance
                + AiConfigV2.devWeightTargets * targetPressure);
            float latentQuality = rd.DevPathViable
                ? AiConfigV2.devLatentPotential * targetPressure
                : 0f;
            float quality = Mathf.Max(readyQuality, latentQuality);

            b.DevFacilityReady = rd.AnyFacilityWithHero ? 1f : 0f;
            b.DevSurplusFraction = rd.SurplusFraction;
            b.DevBestSuccessChance = rd.BestSuccessChance;
            b.DevOfferingQuality = quality;
            b.DevUpgradeTargets = rd.UpgradeTargetCount;
            b.DevPathViable = rd.DevPathViable;

            return Mathf.Clamp01(surplus * quality * AiConfigV2.devDesireGain);
        }

        private static float ReconSurveillance(WorldSnapshot snap)
        {
            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            float staleShare = 0f;
            if (contacts != null && contacts.Count > 0)
            {
                var targetable = contacts
                    .Where(c => c.Source == ContactSource.Honest && c.Position.HasValue)
                    .ToList();
                if (targetable.Count > 0)
                    staleShare = targetable.Count(c => c.Knowledge == ContactKnowledge.LastKnown)
                        / (float)targetable.Count;
            }
            return Mathf.Clamp01(AiConfigV2.reconSurveillanceBaseline
                + AiConfigV2.reconStaleShareWeight * staleShare);
        }

        // Spec §4 — RefreshPressure is a composite, not max(surveillance, avg IntelAge). It sums a
        // baseline, whole-map strategic IntelAge, the honest-contact stale share (extracted from
        // `surveillance`, which itself carries the baseline), own-asset perimeter staleness, an
        // enemy-facing corridor staleness sample, and coarse enemy-concentration direction pressure.
        private static float ReconRefreshPressure(WorldSnapshot snap, float surveillance)
        {
            if (snap?.Self == null)
                return Mathf.Clamp01(surveillance);

            float intelAge = ReconIntelSnapshotRegistry.StalePressure(snap);
            IReadOnlyDictionary<HexCoord, int> observed = ReconIntelSnapshotRegistry.LastObservedFor(snap);
            int turn = snap.TurnNumber;

            var assetHexes = new List<HexCoord>();
            if (snap.Self.BaseHexes != null)
                assetHexes.AddRange(snap.Self.BaseHexes);
            if (assetHexes.Count == 0)
                assetHexes.Add(snap.Self.Citadel);
            float perimeter = RegionStaleness(observed, turn, assetHexes,
                AiConfigV2.reconRefreshPerimeterRadius);

            float corridor = CorridorStaleness(observed, turn, snap.Self.Citadel,
                snap.Known?.EnemySightings);

            ReconDirectionSnapshot dir = ReconDirectionModel.Build(snap);
            float concentration = dir != null && dir.EnemyPresenceWeight > 0f
                ? Mathf.Clamp01(dir.EnemyPresenceWeight / Mathf.Max(1f, AiConfigV2.reconRefreshConcentrationNorm))
                : 0f;

            // `surveillance` already folds in reconSurveillanceBaseline; strip it back out so the
            // stale-contact term carries only the honest-contact stale share, not the baseline a
            // second time (spec §4 — the baseline is added once, explicitly, below).
            float staleContacts = AiConfigV2.reconStaleShareWeight > 0f
                ? Mathf.Clamp01((surveillance - AiConfigV2.reconSurveillanceBaseline)
                    / AiConfigV2.reconStaleShareWeight)
                : 0f;

            float sum = AiConfigV2.reconSurveillanceBaseline
                + AiConfigV2.reconRefreshWeightIntelAge * intelAge
                + AiConfigV2.reconRefreshWeightStaleContacts * staleContacts
                + AiConfigV2.reconRefreshWeightPerimeter * perimeter
                + AiConfigV2.reconRefreshWeightCorridor * corridor
                + AiConfigV2.reconRefreshWeightConcentration * concentration;
            return Mathf.Clamp01(sum);
        }

        private static float RegionStaleness(IReadOnlyDictionary<HexCoord, int> observed, int turn,
            IReadOnlyList<HexCoord> centers, int radius)
        {
            if (observed == null || observed.Count == 0 || centers == null || centers.Count == 0)
                return 0f;
            float sum = 0f;
            int n = 0;
            foreach (HexCoord c in centers)
                foreach (HexCoord h in HexGridMath.HexesInRange(c, Mathf.Max(1, radius)))
                    if (observed.TryGetValue(h, out int obs))
                    {
                        sum += ReconIntelSnapshotRegistry.Staleness(turn - obs);
                        n++;
                    }
            return n > 0 ? sum / n : 0f;
        }

        private static float CorridorStaleness(IReadOnlyDictionary<HexCoord, int> observed, int turn,
            HexCoord citadel, IReadOnlyList<AiMapMemory.KnownEnemySighting> sightings)
        {
            if (observed == null || sightings == null || sightings.Count == 0)
                return 0f;
            HexCoord target = sightings.OrderBy(s => HexGridMath.Distance(citadel, s.Hex)).First().Hex;
            var mid = new HexCoord((citadel.Q + target.Q) / 2, (citadel.R + target.R) / 2);
            return RegionStaleness(observed, turn, new[] { mid }, AiConfigV2.reconRefreshCorridorRadius);
        }

        private static float ReconEnemyBlindness(WorldSnapshot snap)
        {
            bool opponentFielded = snap.TrueWorld?.Opponents != null
                && snap.TrueWorld.Opponents.Any(o => o != null && o.ArmyCount > 0);
            bool hasConcreteHonestPosition = snap.Threat?.Contacts != null
                && snap.Threat.Contacts.Any(c => c.Source == ContactSource.Honest && c.Position.HasValue);
            return (opponentFielded && !hasConcreteHonestPosition) ? AiConfigV2.reconBlindnessMagnitude : 0f;
        }

        private static void ComputeSurplus(WorldSnapshot snap, out float requiredReserve, out float freePower)
        {
            float reserve = DefensiveReserveForThreats(snap.Threat?.Threats, log: true);
            reserve = Mathf.Max(reserve, AiConfigV2.aggHomeGuardFloor);
            requiredReserve = reserve;
            freePower = Mathf.Max(0f, snap.Self.TotalPower - reserve);
        }

        // One physical hostile force contributes once. The protected asset selects relevance and
        // diagnostics, exactly like ActiveDefenceObjectiveEvaluator; it must not clone the same
        // enemy power for every Citadel/Base/Facility lying inside its threat envelope.
        internal static float DefensiveReserveForThreats(
            IReadOnlyList<AssetThreatSnapshot> threats, bool log = false)
        {
            if (threats == null)
                return 0f;
            float reserve = 0f;
            foreach (IGrouping<object, AssetThreatSnapshot> group in threats
                .Where(t => t?.Contact?.Army != null
                    && t.Asset != null
                    && (t.Asset.Kind == AssetKind.Citadel || t.Asset.Kind == AssetKind.Base
                        || t.Asset.Kind == AssetKind.Facility))
                // Honest physical armies have a stable id. Region-only cheat alerts deliberately
                // carry -1/no identity, so keep each contact object independent rather than
                // either dropping them or incorrectly merging every hidden regional alert.
                .GroupBy(t => t.Contact.PhysicalArmyId.HasValue
                    ? (object)t.Contact.PhysicalArmyId.Value
                    : t.Contact.Army.ArmyId >= 0
                        ? (object)t.Contact.Army.ArmyId : t.Contact))
            {
                AssetThreatSnapshot best = group
                    .OrderByDescending(t => t.Severity)
                    .ThenByDescending(t => t.Asset.Value)
                    .ThenBy(t => t.EnemyEta ?? int.MaxValue)
                    .First();
                float contribution = Mathf.Max(0f,
                    best.Contact.Army.EffectiveArmyPower
                    * AiConfigV2.aggDefenceConfidenceMargin);
                reserve += contribution;
                if (log)
                {
                    int enemyId = best.Contact.Army.ArmyId;
                    string enemyLabel = enemyId >= 0 ? $"#{enemyId}" : "regional_contact";
                    AiDebugLog.WriteDeduped($"reserve:{enemyLabel}:{best.Asset.Hex.Q}:{best.Asset.Hex.R}",
                        $"[AI][V2][Defence][Reserve] enemy={enemyLabel} contributes={contribution:0.##} "
                        + $"asset={best.Asset.Kind}@({best.Asset.Hex.Q},{best.Asset.Hex.R}) "
                        + $"severity={best.Severity:0.00} pairCount={group.Count()}");
                }
            }
            return reserve;
        }

        private static float MilitaryThreat(WorldSnapshot snap, bool underSiege)
        {
            float top = 0f;
            IReadOnlyList<AssetThreatSnapshot> threats = snap.Threat?.Threats;
            if (threats != null)
                foreach (AssetThreatSnapshot t in threats)
                    if (t.Asset != null && (t.Asset.Kind == AssetKind.Citadel || t.Asset.Kind == AssetKind.Base))
                        top = Mathf.Max(top, t.Severity);
            if (underSiege)
                top = Mathf.Max(top, AiConfigV2.militaryThreatSiegeFloor);
            return Mathf.Clamp01(top);
        }

        private static void UpdateLossPulses(WorldSnapshot snap, AiRadarState state,
            out float enemyDropFrac, out float ownDropFrac)
        {
            float curOwn = snap.Self.TotalPower;
            ownDropFrac = (state.LastTurn >= 0 && state.PrevOwnPower > 1f)
                ? Mathf.Clamp01((state.PrevOwnPower - curOwn) / state.PrevOwnPower)
                : 0f;
            state.OwnLossPulse = Mathf.Max(state.OwnLossPulse * AiConfigV2.lossPulseDecay,
                Curves.Ramp(ownDropFrac, AiConfigV2.lossPulseRampLo, AiConfigV2.lossPulseRampHi));

            enemyDropFrac = 0f;
            List<AiRadarState.ObservedContact> current = CurrentObservedEnemies(snap);
            if (state.LastTurn >= 0 && state.PrevObservedEnemies != null && state.PrevObservedEnemies.Count > 0)
            {
                var pool = new List<AiRadarState.ObservedContact>(current);
                float drop = 0f, matchedPrevTotal = 0f;
                foreach (AiRadarState.ObservedContact prev in state.PrevObservedEnemies)
                {
                    int bestIdx = -1, bestDist = int.MaxValue;
                    for (int i = 0; i < pool.Count; i++)
                    {
                        if (!ReferenceEquals(pool[i].Owner, prev.Owner)) continue;
                        int dd = HexGridMath.Distance(pool[i].Hex, prev.Hex);
                        if (dd <= AiConfigV2.enemyLossMatchRadius && dd < bestDist)
                        {
                            bestDist = dd;
                            bestIdx = i;
                        }
                    }
                    if (bestIdx < 0) continue;
                    matchedPrevTotal += prev.Power;
                    drop += Mathf.Max(0f, prev.Power - pool[bestIdx].Power);
                    pool.RemoveAt(bestIdx);
                }
                if (matchedPrevTotal > 1f)
                    enemyDropFrac = Mathf.Clamp01(drop / matchedPrevTotal);
            }
            state.EnemyLossPulse = Mathf.Max(state.EnemyLossPulse * AiConfigV2.lossPulseDecay,
                Curves.Ramp(enemyDropFrac, AiConfigV2.lossPulseRampLo, AiConfigV2.lossPulseRampHi));
        }

        private static List<AiRadarState.ObservedContact> CurrentObservedEnemies(WorldSnapshot snap)
        {
            var list = new List<AiRadarState.ObservedContact>();
            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            if (contacts == null)
                return list;
            foreach (EnemyContactSnapshot c in contacts)
            {
                if (c.Source != ContactSource.Honest || !c.Position.HasValue || c.Army == null)
                    continue;
                list.Add(new AiRadarState.ObservedContact
                {
                    Owner = c.Army.Owner,
                    Hex = c.Position.Value,
                    Power = c.Army.EffectiveArmyPower,
                });
            }
            return list;
        }

        private static float Smooth(AiRadarState state, DesireAxis axis, float raw, bool advance)
        {
            if (!advance && state.Smoothed.TryGetValue(axis, out float frozen))
                return frozen;
            if (state.LastTurn < 0 || !state.Smoothed.TryGetValue(axis, out float prev))
            {
                state.Smoothed[axis] = raw;
                return raw;
            }
            float a = AiConfigV2.desireSmoothing;
            float smoothed = (1f - a) * raw + a * prev;
            state.Smoothed[axis] = smoothed;
            return smoothed;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static void LogDesires(DesireVector d, DesireBreakdown b, Radar radar,
            float rawRecon, float rawAggression, float rawEconomy, float rawDev,
            float enemyDropFrac, float ownDropFrac,
            AiRadarState state, CombatOpportunityReport opp)
        {
            AiDebugLog.Write($"[AI][V2]   desires — RCN raw {F(rawRecon)} smoothed {F(d.Raw[DesireAxis.Recon])} "
                + $"(explRaw {F(b.ReconExploration)} exploreP {F(b.ReconExplorePressure)} "
                + $"survRaw {F(b.ReconSurveillance)} refreshP {F(b.ReconRefreshPressure)} "
                + $"blind {F(b.ReconEnemyBlindness)})");
            AiDebugLog.Write($"[AI][V2]   desires — AGG attack lane targets={b.AggAttackTargetCount} "
                + $"bestOpportunity={F(b.AggBestAttackOpportunity)} pressure={F(b.AggAttackPressure)}");
            AiDebugLog.Write($"[AI][V2]   desires — AGG raw {F(rawAggression)} smoothed {F(d.Raw[DesireAxis.Aggression])} "
                + $"= max(offence=max(raid {F(b.AggRaidOpportunity)}, war {F(b.AggWarPressure)})*siegeDamp, "
                + $"activeDefence {F(b.AggActiveDefencePressure)}) "
                + $"[opp {F(b.AggOpportunity)} surp {F(b.AggSurplus)} edge {F(b.AggRelativeEdge)} "
                + $"mom {F(b.AggMomentum)}]");
            AiDebugLog.Write($"[AI][V2]   desires — reserve {F(b.RequiredDefensiveReserve)} free {F(b.OffensiveFreePower)} "
                + $"| lossPulse enemy {F(state.EnemyLossPulse)} (drop {F(enemyDropFrac)}) "
                + $"own {F(state.OwnLossPulse)} (drop {F(ownDropFrac)})");
            AiDebugLog.Write($"[AI][V2][Economy][Desire] resource={b.EconomyPrimaryResource} "
                + $"max={F(b.EconomyMaxDeficit)} mean={F(b.EconomyMeanDeficit)} "
                + $"incomeGap={F(b.EconomyIncomeGap)} relativeGap={F(b.EconomyRelativeGap)} "
                + $"runwayGap={F(b.EconomyRunwayGap)} operational={F(b.EconomyOperationalPressure)} "
                + $"actionableGate={F(b.EconomyActionableGate)} raw={F(rawEconomy)} "
                + $"smoothed={F(d.Raw[DesireAxis.Economy])}");
            AiDebugLog.Write($"[AI][V2]   desires — DEV raw {F(rawDev)} smoothed {F(d.Raw[DesireAxis.Development])} "
                + $"= surplus {F(b.DevSurplusFraction)} x quality {F(b.DevOfferingQuality)} "
                + $"(facReady {F(b.DevFacilityReady)} pathViable {(b.DevPathViable ? 1 : 0)} bestP {F(b.DevBestSuccessChance)} targets {b.DevUpgradeTargets})");
            string bestOpp = b.BestOpportunity.HasTarget
                ? $"@{b.BestOpportunity.TargetHex.Q},{b.BestOpportunity.TargetHex.R} "
                  + $"asmWin {F(b.BestOpportunity.AssemblableWinChance)} readyWin {F(b.BestOpportunity.ReadyWinChance)} "
                  + $"val {F(b.BestOpportunity.TargetValue)} eta {b.BestOpportunity.Eta} "
                  + $"gate {(b.BestOpportunity.GatePassed ? 1 : 0)}"
                : "none";
            AiDebugLog.Write($"[AI][V2]   desires — bestOpp {bestOpp} "
                + $"(targets {opp.All.Count}, heroAvail {(opp.HeroAvailable ? 1 : 0)}, cap {opp.AssemblableCap}) "
                + $"| threat {F(d.MilitaryThreat)} runway {F(d.EconomicRunway)}");
        }
    }

    // --- Radar model #2 (proportional). The radar's ONLY effect on decisions: it scales
    //     objective / mission VALUE. It does NOT slice AP (one shared pool) and is NOT part of
    //     within-lane ordering.
    //       scale(axis) = axisCount * weight
    //     This is a pure normalisation, not a tunable bonus: at the even split
    //     (weight == 1/axisCount for every axis) scale == 1 for every axis, so a uniform radar
    //     reproduces BaseValue exactly. Above or below the even split the scale keeps moving
    //     linearly — there is no ceiling and no floor. weight == 0 -> scale == 0 (a cold axis
    //     competes for AP with zero priority; it is NOT forbidden — ResourceAllocator still lets
    //     it spend leftover budget nobody else wants, see ResourceAllocator's remainder pass).
    //     axisCount is DesireAxes.All.Length (currently 4), read live so a future axis count still
    //     normalises correctly without a second constant to keep in sync.
    public static class RadarValueScale
    {
        public static float For(Radar radar, DesireAxis axis)
        {
            float w = radar?.Weight != null && radar.Weight.TryGetValue(axis, out float ww)
                ? UnityEngine.Mathf.Max(0f, ww) : 0f;
            return DesireAxes.All.Length * w;
        }

        // Contribution-weighted scale for a multi-axis mission — a normalised weighted sum of each
        // contributing axis's own proportional scale, weighted by how much the mission serves that
        // axis (AxisContribution). Every real proposal today names exactly one axis at 1.0, so this
        // collapses to For(radar, thatAxis).
        public static float For(Radar radar, MissionProposal m)
        {
            var contrib = m?.Axes?.Value;
            // Missing axis contributions are neutral, not an implicit Recon vote.
            // Explicit contributing axes (including Recon) still scale to zero if their Radar is zero.
            if (contrib == null || contrib.Count == 0)
                return 1f;
            float acc = 0f, wsum = 0f;
            foreach (DesireAxis a in DesireAxes.All)
                if (contrib.TryGetValue(a, out float c) && c > 0f)
                {
                    acc += c * For(radar, a);
                    wsum += c;
                }
            return wsum > 0f ? acc / wsum : 1f; // no valid declared contribution => neutral
        }
    }
}
