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
    //  the V1 AiStrategyDirector style — NOT fuzzy logic. Reads the snapshot (+ AiConfigV2,
    //  + WorthIt/AiPower which are pure) and no live game state — that is what lets
    //  Tools/radar-sim drive it against hand-built snapshots. Two read-only owners are consulted
    //  through their snapshot-keyed APIs: the frozen per-turn IntelAge (ReconIntelSnapshotRegistry)
    //  and the Attack observation need (AttackObjectiveEvaluator.ObservationNeeds, which reads the
    //  live intent registry); an empty registry simply contributes nothing.
    //
    //  AGGRESSION (2026-10-04): raw = readiness while a war witness exists
    //  (ForceNeedModel.HasAggressionWitness: a known fight, a sanctioned enemy starting Citadel or
    //  an open mobilization gate); its surplus term ignores the threat reserve (see ComputeSurplus).
    //
    //  RECON is one axis with exploration, map refresh and enemy blindness contributions:
    //    exploration   — DECAYS as the reachable map opens (state-driven, no turn term).
    //    enemyBlindness— an opponent is fielded (honest opponent list) but we have zero honest
    //                    sightings of it. Magnitude only.
    //
    //  Two continuous strategic lanes:
    //    ExplorePressure — frontier pressure from unexplored reachable ground.
    //    RefreshPressure — frozen per-hex IntelAge and strategic-site pressure.
    //  The frozen IntelAge snapshot is captured during WorldAnalysis and therefore cannot change
    //  retroactively while the operational phase is moving scouts one hex at a time.
    //
    //  AGGRESSION is one strategic axis. Its desire reads broad military readiness when combat
    //  activity is known; the general threat scalar remains a separate world fact. It never reads
    //  the merit of a Raid, ActiveDefence or Attack task.
    //  Their concrete values are compared only by TaskScore after the common Radar scale.
    //  Home threat therefore moves this axis in NEITHER direction: raising it would lift every
    //  offensive peer, damping it (the former siege damp) would starve ActiveDefence, which lives
    //  on the same axis. Offensive restraint while home is threatened is the Raid/Attack
    //  TaskScore's CitadelThreatRisk slot; ActiveDefence carries its own threat severity.
    //
    //  DesireBreakdown supplies the lane pressures to MissionLayer without re-deriving them.
    //
    //  Smoothing: symmetric low-pass on Recon + Aggression only (AiConfigV2.desireSmoothing).
    //  ECO/DEV placeholders and the two out-of-simplex scalars are unsmoothed — a threat scalar
    //  that means "existential" must react the turn it becomes true.
    // ===========================================================================================

    public sealed class DesireBreakdown
    {
        public float ReconExploration;
        public float ReconEnemyBlindness;
        public float ReconExplorePressure;
        public float ReconRefreshPressure;

        public float AggSurplus;
        public float AggRelativeEdge;

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
        public float DevOfferingQuality;    // [0..1] feasibility: max(ready best success chance, latent path)
        public float DevBestSuccessChance;  // raw p of the best affordable offering
        public int   DevUpgradeTargets;
        public bool  DevPathViable;         // a facility exists / can be built — else latent appetite is 0
        public float DevJustifiedNeed;      // [0..1] Development need, including useful reserve investment
        public string DevNeedDetail = "";
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
        public int LastTurn = -1;
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
            using var __profile = new Game.Core.ProfileScope("AI/Strategy.Evaluate");
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
            float exploration = ReconExploration(snapshot);
            float blindness = ReconEnemyBlindness(snapshot);
            float explorePressure = exploration;
            float refreshPressure = ReconRefreshPressure(snapshot);
            float rawRecon = Mathf.Clamp01(
                AiConfigV2.reconWeightExploration * explorePressure
                + AiConfigV2.reconWeightRefresh * refreshPressure
                + AiConfigV2.reconWeightBlindness * blindness);

            breakdown.ReconExploration = exploration;
            breakdown.ReconEnemyBlindness = blindness;
            breakdown.ReconExplorePressure = explorePressure;
            breakdown.ReconRefreshPressure = refreshPressure;

            CombatOpportunityReport opp = CombatOpportunityAnalyzer.Analyze(snapshot);

            ComputeSurplus(snapshot, out float requiredReserve, out float freePower);
            float surplus = Curves.Ramp(freePower / Mathf.Max(1f, snapshot.Self.TotalPower),
                AiConfigV2.aggSurplusRampLo, AiConfigV2.aggSurplusRampHi);

            float relativeEdge = ForceNeedModel.RelativeEdge(snapshot);

            float ecoSecurity = snapshot.Economy != null ? snapshot.Economy.EconomicSecurity : 0.5f;
            float ecoGate = Mathf.Lerp(AiConfigV2.aggEcoGateLo, 1f, Mathf.Clamp01(ecoSecurity));

            float readiness = (surplus + ecoGate + relativeEdge) / 3f;
            float strategicThreat = MilitaryThreat(snapshot, underSiege);
            float rawAggression = Mathf.Clamp01(
                ForceNeedModel.HasAggressionWitness(snapshot) ? readiness : 0f);

            breakdown.AggSurplus = surplus;
            breakdown.AggRelativeEdge = relativeEdge;
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

            desires.MilitaryThreat = strategicThreat;
            desires.EconomicRunway = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ecoSecurity));

            if (firstEvaluationThisTurn)
            {
                state.LastTurn = snapshot.TurnNumber;
            }

            Radar radar = Radar.Normalize(desires);
            LogDesires(desires, breakdown, radar, rawRecon, rawAggression, rawEconomy, rawDev,
                opp);

            return new RadarAssessment { Desires = desires, Breakdown = breakdown, Radar = radar };
        }

        // Recon lane pressures are the only sub-terms MissionLayer needs on every settled step
        // (ReconMissionPlanner.Propose runs mid-turn against a LIVE snapshot while the rest of
        // DesireBreakdown/Radar stays frozen from the turn's single Evaluate() — recomputing the
        // whole radar mid-turn would re-introduce the oscillation that decision explicitly
        // avoided). This mutates ONLY the Explore/Refresh/Blindness fields in place
        // on the already-frozen breakdown, from the current snapshot, so a frontier completion
        // mid-turn is reflected before the next mission is proposed. No smoothing, no radar
        // renormalization, no other axis touched.
        public static void RefreshReconLanePressures(WorldSnapshot snapshot, DesireBreakdown breakdown)
        {
            if (snapshot?.Self == null || snapshot.MapKnowledge == null || breakdown == null)
                return;
            float exploration = ReconExploration(snapshot);
            float blindness = ReconEnemyBlindness(snapshot);
            float refreshPressure = ReconRefreshPressure(snapshot);
            breakdown.ReconExploration = exploration;
            breakdown.ReconEnemyBlindness = blindness;
            breakdown.ReconExplorePressure = exploration;
            breakdown.ReconRefreshPressure = refreshPressure;
        }

        // Refresh perishable opportunity and force facts without advancing the turn's Radar.
        public static void RefreshAggressionOperationalFacts(WorldSnapshot snapshot, DesireBreakdown breakdown)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Strategy.RefreshAggressionFacts");
            if (snapshot?.Self == null || breakdown == null)
                return;

            CombatOpportunityReport opp = CombatOpportunityAnalyzer.Analyze(snapshot);

            ComputeSurplus(snapshot, out float requiredReserve, out float freePower);
            float surplus = Curves.Ramp(freePower / Mathf.Max(1f, snapshot.Self.TotalPower),
                AiConfigV2.aggSurplusRampLo, AiConfigV2.aggSurplusRampHi);

            float relativeEdge = ForceNeedModel.RelativeEdge(snapshot);

            breakdown.OpportunityReport = opp;
            breakdown.BestOpportunity = opp.Best;
            breakdown.AggSurplus = surplus;
            breakdown.AggRelativeEdge = relativeEdge;
            breakdown.RequiredDefensiveReserve = requiredReserve;
            breakdown.OffensiveFreePower = freePower;
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

        // Development (Research/Production) desire — the last link of the axis chain:
        //   Recon -> knowledge -> Economy -> budget -> Attack/Defence -> need -> Production amplifies.
        //   rawDev = surplus x need x feasibility, and never above need.
        //     surplus     — Economy's budget (resource headroom above the reservation floors). Hard
        //                   multiplier: a Challenge stakes real H/E/M/T with a random return.
        //     need        — ForceNeedModel.JustifiedForceNeed: the part of the known war (fights we
        //                   cannot take, enemy edge, uncovered defensive reserve) Attack/Defence
        //                   cannot meet with the force they have. Zero without a military witness.
        //     feasibility — max(ready, latent): a staffed facility with an affordable offering can
        //                   run a Challenge now (its best success chance); otherwise a path to a
        //                   facility keeps the axis warm (devLatentPotential) so Demand can STAGE the
        //                   prerequisite. No facility+hero hard gate: that made the axis dormant
        //                   whenever the operator never showed up on its own.
        // Reads only snapshot facts — no live game-state read.
        private static float DevelopmentDesire(WorldSnapshot snap, DesireBreakdown b)
        {
            DevelopmentReadiness rd = snap?.Development;
            if (rd == null)
                return 0f;

            float surplus = Curves.Ramp(rd.SurplusFraction,
                AiConfigV2.devSurplusRampLo, AiConfigV2.devSurplusRampHi);
            ForceNeed need = ForceNeedModel.JustifiedForceNeed(snap);

            float developmentNeed = ForceNeedModel.DevelopmentNeed(snap);

            float readyFeasibility = rd.Offerings.Count == 0 ? 0f : Mathf.Clamp01(rd.BestSuccessChance);
            float latentFeasibility = rd.DevPathViable ? AiConfigV2.devLatentPotential : 0f;
            float feasibility = Mathf.Max(readyFeasibility, latentFeasibility);

            b.DevFacilityReady = rd.AnyFacilityWithHero ? 1f : 0f;
            b.DevSurplusFraction = rd.SurplusFraction;
            b.DevBestSuccessChance = rd.BestSuccessChance;
            b.DevOfferingQuality = feasibility;
            b.DevUpgradeTargets = rd.UpgradeTargetCount;
            b.DevPathViable = rd.DevPathViable;
            b.DevJustifiedNeed = developmentNeed;
            b.DevNeedDetail = $"force={need}; development={developmentNeed:0.###}";

            float outputDesire = Mathf.Min(developmentNeed, Mathf.Clamp01(surplus * developmentNeed * feasibility));
            // Prerequisite appetite is independent of future output/recipient and military need.
            // Reuse the existing latent feasibility; no new baseline/completion coefficient.
            float preparationSurplus = Curves.Ramp(rd.PreparationHeadroom,
                AiConfigV2.devSurplusRampLo, AiConfigV2.devSurplusRampHi);
            float preparationDesire = rd.HasPreparationStep
                ? preparationSurplus * AiConfigV2.devLatentPotential : 0f;
            b.DevNeedDetail += $"; preparation={preparationDesire:0.###}"
                + $" headroom={rd.PreparationHeadroom:0.###} hasStep={rd.HasPreparationStep}";
            return Mathf.Max(outputDesire, preparationDesire);
        }

        // Spec §4 — RefreshPressure is a composite: a baseline, whole-map strategic IntelAge,
        // own-asset perimeter staleness, an enemy-facing corridor staleness sample, and coarse
        // enemy-concentration direction pressure.
        private static float ReconRefreshPressure(WorldSnapshot snap)
        {
            if (snap?.Self == null)
                return Mathf.Clamp01(AiConfigV2.reconRefreshBaseline);

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

            float attackTarget = AttackObservationPressure(snap, observed, turn);

            float sum = AiConfigV2.reconRefreshBaseline
                + AiConfigV2.reconRefreshWeightIntelAge * intelAge
                + AiConfigV2.reconRefreshWeightPerimeter * perimeter
                + AiConfigV2.reconRefreshWeightCorridor * corridor
                + AiConfigV2.reconRefreshWeightConcentration * concentration
                + AiConfigV2.reconRefreshWeightAttackTarget * attackTarget;
            return Mathf.Clamp01(sum);
        }

        // 2026-10-04 — a site Attack asked Recon to observe (AttackObjectiveEvaluator.ObservationNeeds,
        // the one owner: the target of a live operation, or the enemy Citadel when no hostile
        // structure is known yet): 1 while one of them was never observed or is older than the
        // Attack intel limit (attackIntelMaxAgeTurns), else 0. One hex inside the whole-map IntelAge
        // average barely moved the axis, so the Refresh it needs could compete at a cold weight.
        private static float AttackObservationPressure(WorldSnapshot snap,
            IReadOnlyDictionary<HexCoord, int> observed, int turn)
        {
            foreach (HexCoord hex in AttackObjectiveEvaluator.ObservationNeeds(snap))
                if (observed == null || !observed.TryGetValue(hex, out int seen)
                    || turn - seen > AiConfigV2.attackIntelMaxAgeTurns)
                    return 1f;
            return 0f;
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
                && snap.Threat.Contacts.Any(c => c.Position.HasValue);
            return (opponentFielded && !hasConcreteHonestPosition) ? AiConfigV2.reconBlindnessMagnitude : 0f;
        }

        // 2026-10-04 — the Radar's surplus is the force above the fixed home guard only. The
        // defensive reserve known threats demand is still measured and logged, but it no longer
        // shrinks the surplus: that made a home threat damp the whole Aggression axis — and with
        // it ActiveDefence, the answer to that very threat (the hidden siege damp the file header
        // rules out). Offensive restraint under threat is the Raid/Attack CitadelThreatRisk slot.
        private static void ComputeSurplus(WorldSnapshot snap, out float requiredReserve, out float freePower)
        {
            float reserve = ForceNeedModel.DefensiveReserveForThreats(snap.Threat?.Threats, log: true);
            requiredReserve = Mathf.Max(reserve, AiConfigV2.aggHomeGuardFloor);
            freePower = Mathf.Max(0f, snap.Self.TotalPower - AiConfigV2.aggHomeGuardFloor);
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
            CombatOpportunityReport opp)
        {
            AiDebugLog.Write($"[AI][V2]   desires — RCN raw {F(rawRecon)} smoothed {F(d.Raw[DesireAxis.Recon])} "
                + $"(explRaw {F(b.ReconExploration)} exploreP {F(b.ReconExplorePressure)} "
                + $"refreshP {F(b.ReconRefreshPressure)} "
                + $"blind {F(b.ReconEnemyBlindness)})");
            AiDebugLog.Write($"[AI][V2]   desires — AGG raw {F(rawAggression)} smoothed {F(d.Raw[DesireAxis.Aggression])} "
                + $"radar {F(radar.Weight[DesireAxis.Aggression])} scale {F(RadarValueScale.For(radar, DesireAxis.Aggression))} "
                + $"[surp {F(b.AggSurplus)} edge {F(b.AggRelativeEdge)}]");
            AiDebugLog.Write($"[AI][V2]   world threat fact {F(d.MilitaryThreat)}");
            AiDebugLog.Write($"[AI][V2]   desires — reserve {F(b.RequiredDefensiveReserve)} free {F(b.OffensiveFreePower)}");
            AiDebugLog.Write($"[AI][V2][Economy][Desire] resource={b.EconomyPrimaryResource} "
                + $"max={F(b.EconomyMaxDeficit)} mean={F(b.EconomyMeanDeficit)} "
                + $"incomeGap={F(b.EconomyIncomeGap)} relativeGap={F(b.EconomyRelativeGap)} "
                + $"runwayGap={F(b.EconomyRunwayGap)} operational={F(b.EconomyOperationalPressure)} "
                + $"actionableGate={F(b.EconomyActionableGate)} raw={F(rawEconomy)} "
                + $"smoothed={F(d.Raw[DesireAxis.Economy])}");
            AiDebugLog.Write($"[AI][V2]   desires — DEV raw {F(rawDev)} smoothed {F(d.Raw[DesireAxis.Development])} "
                + $"= surplus {F(b.DevSurplusFraction)} x need {F(b.DevJustifiedNeed)} x feasibility {F(b.DevOfferingQuality)} "
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

