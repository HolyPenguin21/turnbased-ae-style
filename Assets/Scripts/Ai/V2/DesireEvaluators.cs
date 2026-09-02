using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    //    warPressure     — "built out, economy fine, time to break a KNOWN opponent/neutral target"
    //                      (potentialSaturation + surplus + ecoGate + relativeEdge).
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
    //  DEF/ECO/DEV placeholders and the two out-of-simplex scalars are unsmoothed — a threat
    //  scalar that means "existential" must react the turn it becomes true. Asymmetric
    //  rise/fall handling belongs with the Defence evaluator, a later step.
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
        public float AggWarPressure;
        public float AggOpportunity;
        public float AggSurplus;
        public float AggRelativeEdge;
        public float AggPotentialSaturation;
        public float AggMomentum;

        public CombatOpportunity BestOpportunity = CombatOpportunity.None;
        public CombatOpportunityReport OpportunityReport = new CombatOpportunityReport();
        public float RequiredDefensiveReserve;
        public float OffensiveFreePower;
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

            UpdateLossPulses(snapshot, state, out float enemyDropFrac, out float ownDropFrac);
            float momentum = Mathf.Clamp01(0.5f + 0.5f * state.EnemyLossPulse - 0.5f * state.OwnLossPulse);

            float exploration = ReconExploration(snapshot);
            float surveillance = ReconSurveillance(snapshot);
            float blindness = ReconEnemyBlindness(snapshot);
            float explorePressure = exploration;
            float refreshPressure = Mathf.Clamp01(Mathf.Max(
                surveillance,
                ReconIntelSnapshotRegistry.StalePressure(snapshot)));
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
            float opportunity = opp.Best.HasTarget ? opp.Best.OpportunityScore : 0f;

            ComputeSurplus(snapshot, out float requiredReserve, out float freePower);
            float surplus = Curves.Ramp(freePower / Mathf.Max(1f, snapshot.Self.TotalPower),
                AiConfigV2.aggSurplusRampLo, AiConfigV2.aggSurplusRampHi);

            float ownPower = Mathf.Max(snapshot.Self.FieldPower, snapshot.Self.BestStackPotential);
            float enemyPower = snapshot.Known?.EnemyKnownStrength ?? 0f;
            float relativeEdge = enemyPower < 1f
                ? AiConfigV2.aggRelEdgeNoIntel
                : Curves.Ramp(ownPower / enemyPower, AiConfigV2.aggRelEdgeRampLo, AiConfigV2.aggRelEdgeRampHi);

            float potentialSaturation = Curves.Ramp(
                snapshot.Self.BestStackPotential / Mathf.Max(1f, snapshot.Self.TotalMilitaryPotential),
                AiConfigV2.aggPotentialSatRampLo, AiConfigV2.aggPotentialSatRampHi);

            float ecoSecurity = snapshot.Economy != null ? snapshot.Economy.EconomicSecurity : 0.5f;
            float ecoGate = Mathf.Lerp(AiConfigV2.aggEcoGateLo, 1f, Mathf.Clamp01(ecoSecurity));

            float raidOpportunity =
                AiConfigV2.aggRaidOppWeightOpportunity * opportunity
                + AiConfigV2.aggRaidOppWeightSurplus * surplus
                + AiConfigV2.aggRaidOppWeightRelEdge * relativeEdge
                + AiConfigV2.aggRaidOppWeightMomentum * momentum;
            float warPressure =
                AiConfigV2.aggWarWeightPotentialSat * potentialSaturation
                + AiConfigV2.aggWarWeightSurplus * surplus
                + AiConfigV2.aggWarWeightEcoGate * ecoGate
                + AiConfigV2.aggWarWeightRelEdge * relativeEdge;

            bool hasKnownCombatTarget = opp.All != null && opp.All.Count > 0;
            float rawAggression = hasKnownCombatTarget
                ? Mathf.Clamp01(Mathf.Max(raidOpportunity, warPressure))
                    * (underSiege ? AiConfigV2.aggSiegeDamp : 1f)
                : 0f;

            breakdown.AggRaidOpportunity = Mathf.Clamp01(raidOpportunity);
            breakdown.AggWarPressure = Mathf.Clamp01(warPressure);
            breakdown.AggOpportunity = opportunity;
            breakdown.AggSurplus = surplus;
            breakdown.AggRelativeEdge = relativeEdge;
            breakdown.AggPotentialSaturation = potentialSaturation;
            breakdown.AggMomentum = momentum;
            breakdown.BestOpportunity = opp.Best;
            breakdown.OpportunityReport = opp;
            breakdown.RequiredDefensiveReserve = requiredReserve;
            breakdown.OffensiveFreePower = freePower;

            float recon = Smooth(state, DesireAxis.Recon, rawRecon);
            float aggression = Smooth(state, DesireAxis.Aggression, rawAggression);

            desires.Raw[DesireAxis.Recon] = recon;
            desires.Raw[DesireAxis.Aggression] = aggression;
            desires.Raw[DesireAxis.Defence] = AiConfigV2.desirePlaceholderInactive;
            desires.Raw[DesireAxis.Economy] = AiConfigV2.desirePlaceholderInactive;
            desires.Raw[DesireAxis.Development] = AiConfigV2.desirePlaceholderInactive;

            desires.MilitaryThreat = MilitaryThreat(snapshot, underSiege);
            desires.EconomicRunway = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ecoSecurity));

            state.PrevOwnPower = snapshot.Self.TotalPower;
            state.PrevObservedEnemies = CurrentObservedEnemies(snapshot);
            state.LastTurn = snapshot.TurnNumber;

            Radar radar = Radar.Normalize(desires);
            LogDesires(desires, breakdown, radar, rawRecon, rawAggression, enemyDropFrac, ownDropFrac,
                state, opp);

            return new RadarAssessment { Desires = desires, Breakdown = breakdown, Radar = radar };
        }

        private static float ReconExploration(WorldSnapshot snap)
        {
            float explorable = snap.MapKnowledge != null ? snap.MapKnowledge.ExplorableUnknownFrac : 0f;
            return Curves.Ramp(explorable, AiConfigV2.reconExploreRampLo, AiConfigV2.reconExploreRampHi);
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
            var perAsset = new Dictionary<HexCoord, float>();
            IReadOnlyList<AssetThreatSnapshot> threats = snap.Threat?.Threats;
            if (threats != null)
            {
                foreach (AssetThreatSnapshot t in threats)
                {
                    if (t.Asset == null) continue;
                    if (t.Asset.Kind != AssetKind.Citadel && t.Asset.Kind != AssetKind.Base
                        && t.Asset.Kind != AssetKind.Facility)
                        continue;
                    float need = (t.Contact?.Army?.EffectiveArmyPower ?? 0f) * AiConfigV2.aggDefenceConfidenceMargin;
                    if (!perAsset.TryGetValue(t.Asset.Hex, out float cur) || need > cur)
                        perAsset[t.Asset.Hex] = need;
                }
            }

            float reserve = perAsset.Values.Sum();
            reserve = Mathf.Max(reserve, AiConfigV2.aggHomeGuardFloor);
            requiredReserve = reserve;
            freePower = Mathf.Max(0f, snap.Self.TotalPower - reserve);
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

        private static float Smooth(AiRadarState state, DesireAxis axis, float raw)
        {
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
            float rawRecon, float rawAggression, float enemyDropFrac, float ownDropFrac,
            AiRadarState state, CombatOpportunityReport opp)
        {
            AiDebugLog.Write($"[AI][V2]   desires — RCN raw {F(rawRecon)} smoothed {F(d.Raw[DesireAxis.Recon])} "
                + $"(explRaw {F(b.ReconExploration)} exploreP {F(b.ReconExplorePressure)} "
                + $"survRaw {F(b.ReconSurveillance)} refreshP {F(b.ReconRefreshPressure)} "
                + $"blind {F(b.ReconEnemyBlindness)})");
            AiDebugLog.Write($"[AI][V2]   desires — AGG raw {F(rawAggression)} smoothed {F(d.Raw[DesireAxis.Aggression])} "
                + $"= max(raid {F(b.AggRaidOpportunity)}, war {F(b.AggWarPressure)}) "
                + $"[opp {F(b.AggOpportunity)} surp {F(b.AggSurplus)} edge {F(b.AggRelativeEdge)} "
                + $"sat {F(b.AggPotentialSaturation)} mom {F(b.AggMomentum)}]");
            AiDebugLog.Write($"[AI][V2]   desires — reserve {F(b.RequiredDefensiveReserve)} free {F(b.OffensiveFreePower)} "
                + $"| lossPulse enemy {F(state.EnemyLossPulse)} (drop {F(enemyDropFrac)}) "
                + $"own {F(state.OwnLossPulse)} (drop {F(ownDropFrac)})");
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
}
