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
    //  AGGRESSION is ONE axis with TWO internal drivers, combined by max():
    //    raidOpportunity — "a profitable target I can take right now" (opportunity + surplus +
    //                      relativeEdge + momentum).  `opportunity` comes from the shared
    //                      CombatOpportunityAnalyzer — never a private aggression-only estimator.
    //    warPressure     — "built out, economy fine, time to break the main opponent"
    //                      (potentialSaturation + surplus + ecoGate + relativeEdge).
    //    raw = max(raidOpportunity, warPressure) * (UnderSiege ? aggSiegeDamp : 1).
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

    // Response-curve primitives. Everything else (Clamp01 / Lerp / SmoothStep) is Mathf as-is.
    internal static class Curves
    {
        // 0 below lo, 1 at/above hi, linear between.
        public static float Ramp(float v, float lo, float hi) =>
            Mathf.Clamp01((v - lo) / Mathf.Max(0.0001f, hi - lo));

        public static float InvRamp(float v, float lo, float hi) => 1f - Ramp(v, lo, hi);
    }

    // Step-3 evaluator output — the "why" behind each axis. Not just debug: MissionLayer reads it.
    public sealed class DesireBreakdown
    {
        // --- Recon (one axis, three contributions) ---
        public float ReconExploration;
        public float ReconSurveillance;
        public float ReconEnemyBlindness;

        // --- Aggression (one axis, two drivers over shared sub-terms) ---
        public float AggRaidOpportunity;   // driver
        public float AggWarPressure;       // driver
        public float AggOpportunity;
        public float AggSurplus;
        public float AggRelativeEdge;
        public float AggPotentialSaturation;
        public float AggMomentum;          // Clamp01(0.5 + 0.5*enemyLossPulse - 0.5*ownLossPulse)

        // Carried through verbatim so downstream reuses this analysis, never re-derives it.
        public CombatOpportunity BestOpportunity = CombatOpportunity.None;
        // Step 9 — the WHOLE shared combat-opportunity report from this cycle's Aggression
        // evaluation, frozen here so AggressionObjectiveEvaluator can turn it into frozen Raid
        // objectives and DemandLayer / AggressionMissionPlanner read the same set. The Raid lane
        // never re-scans the world (spec §9, AC #5).
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

    // Per-player cross-turn state — V2's own, same static-registry shape as V1's AiStrategyRegistry.
    // Holds the only things step 3 needs to carry between turns: the smoothed axis values and the
    // material for the two loss pulses.
    public sealed class AiRadarState
    {
        public readonly Dictionary<DesireAxis, float> Smoothed = new Dictionary<DesireAxis, float>();
        public float PrevOwnPower;
        public float EnemyLossPulse;
        public float OwnLossPulse;
        public int LastTurn = -1;

        // Honest, positioned enemy contacts seen last turn. Matched contact-to-contact next turn
        // (owner + within enemyLossMatchRadius) so a force merely walking out of vision — the
        // contact disappearing entirely — is NOT counted as an enemy loss.
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

    // ===========================================================================================
    //  THE EVALUATOR ENTRY POINT
    // ===========================================================================================
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

            // ---- momentum pulses (the only step that needs cross-turn state) ----
            UpdateLossPulses(snapshot, state, out float enemyDropFrac, out float ownDropFrac);
            float momentum = Mathf.Clamp01(0.5f + 0.5f * state.EnemyLossPulse - 0.5f * state.OwnLossPulse);

            // ================= RECON =================
            float exploration = ReconExploration(snapshot);
            float surveillance = ReconSurveillance(snapshot);
            float blindness = ReconEnemyBlindness(snapshot);
            float rawRecon = Mathf.Clamp01(
                AiConfigV2.reconWeightExploration * exploration
                + AiConfigV2.reconWeightSurveillance * surveillance
                + AiConfigV2.reconWeightBlindness * blindness);

            breakdown.ReconExploration = exploration;
            breakdown.ReconSurveillance = surveillance;
            breakdown.ReconEnemyBlindness = blindness;

            // ================= AGGRESSION =================
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

            float rawAggression = Mathf.Clamp01(Mathf.Max(raidOpportunity, warPressure))
                * (underSiege ? AiConfigV2.aggSiegeDamp : 1f);

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

            // ---- smoothing: Recon + Aggression only ----
            float recon = Smooth(state, DesireAxis.Recon, rawRecon);
            float aggression = Smooth(state, DesireAxis.Aggression, rawAggression);

            desires.Raw[DesireAxis.Recon] = recon;
            desires.Raw[DesireAxis.Aggression] = aggression;
            desires.Raw[DesireAxis.Defence] = AiConfigV2.desirePlaceholderInactive;
            desires.Raw[DesireAxis.Economy] = AiConfigV2.desirePlaceholderInactive;
            desires.Raw[DesireAxis.Development] = AiConfigV2.desirePlaceholderInactive;

            // ---- out-of-simplex scalars: raw, unsmoothed ----
            desires.MilitaryThreat = MilitaryThreat(snapshot, underSiege);
            desires.EconomicRunway = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ecoSecurity));

            // ---- persist cross-turn state ----
            state.PrevOwnPower = snapshot.Self.TotalPower;
            state.PrevObservedEnemies = CurrentObservedEnemies(snapshot);
            state.LastTurn = snapshot.TurnNumber;

            Radar radar = Radar.Normalize(desires);
            LogDesires(desires, breakdown, radar, rawRecon, rawAggression, enemyDropFrac, ownDropFrac,
                state, opp);

            return new RadarAssessment { Desires = desires, Breakdown = breakdown, Radar = radar };
        }

        // ---------------------------------------------------------------- Recon ----

        // DECAYING contribution. Driven by ExplorableUnknownFrac — the share of the map that is
        // still dark AND still reachable on foot (WorldAnalysis floods the frontier outward). The
        // slice locked behind an enemy citadel or a hostile guard is already excluded from that
        // number, so no separate floor is needed; it hits 0 exactly when the frontier empties.
        // No turn term — decay is state-driven. (Build-order step 4 — replaces reconUnreachableFloor.)
        private static float ReconExploration(WorldSnapshot snap)
        {
            float explorable = snap.MapKnowledge != null ? snap.MapKnowledge.ExplorableUnknownFrac : 0f;
            return Curves.Ramp(explorable, AiConfigV2.reconExploreRampLo, AiConfigV2.reconExploreRampHi);
        }

        // SUSTAINED contribution. A non-burning baseline (there is always value in re-scanning hex
        // content, resource sites, keeping vision current) plus a bump for the share of TARGETABLE
        // contacts gone stale. "Targetable" == honest AND positioned: a Cheat Region/Unknown
        // contact has no hex a Scout could be sent to (type invariant), so counting it here would
        // raise surveillance desire with no surveil mission able to answer it — that uncertainty
        // is enemyBlindness's job. Stale == LastKnown (Exact means we see it right now).
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

        // "We know an opponent is fielded but have zero honest CONCRETE position on it." Binary
        // magnitude. Must read the SAME contract ReconSurveillance does — snap.Threat.Contacts,
        // which since build-order step 4 also carries AiReconMemory's historical LastKnown entries.
        // Reading snap.Known.EnemySightings instead would double-count: a contact that has aged out
        // of V1's 2-turn memory but still has an honest last-known hex would raise Surveillance
        // ("I know a stale position") AND Blindness ("I know no position") at the same time.
        private static float ReconEnemyBlindness(WorldSnapshot snap)
        {
            bool opponentFielded = snap.TrueWorld?.Opponents != null
                && snap.TrueWorld.Opponents.Any(o => o != null && o.ArmyCount > 0);
            bool hasConcreteHonestPosition = snap.Threat?.Contacts != null
                && snap.Threat.Contacts.Any(c => c.Source == ContactSource.Honest && c.Position.HasValue);
            return (opponentFielded && !hasConcreteHonestPosition) ? AiConfigV2.reconBlindnessMagnitude : 0f;
        }

        // ---------------------------------------------------------------- Aggression ----

        // OffensiveFreePower = TotalPower - RequiredDefensiveReserve, where the reserve is what the
        // ThreatModel says we need to hold home confidently: per threatened Citadel/Base/Facility,
        // the strongest threatening contact's power * a confidence margin. Garrison force ABOVE
        // that reserve counts as usable — the surplus grows from security-optional strength, not
        // from army size alone.
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

        // Out-of-simplex absolute scalar — the normalised vector can't tell "40% to Defence because
        // nothing else competed" from "40% is nowhere near enough". Top Severity on a Citadel/Base
        // asset, forced up when the AI is behaving as if besieged.
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

        // ---------------------------------------------------------------- pulses / state ----

        private static void UpdateLossPulses(WorldSnapshot snap, AiRadarState state,
            out float enemyDropFrac, out float ownDropFrac)
        {
            // --- own losses: no fog, straight TotalPower delta ---
            float curOwn = snap.Self.TotalPower;
            ownDropFrac = (state.LastTurn >= 0 && state.PrevOwnPower > 1f)
                ? Mathf.Clamp01((state.PrevOwnPower - curOwn) / state.PrevOwnPower)
                : 0f;
            state.OwnLossPulse = Mathf.Max(state.OwnLossPulse * AiConfigV2.lossPulseDecay,
                Curves.Ramp(ownDropFrac, AiConfigV2.lossPulseRampLo, AiConfigV2.lossPulseRampHi));

            // --- enemy losses: observed-only, matched contact-to-contact ---
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
                    if (bestIdx < 0) continue;              // contact vanished — not a loss
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

        // ---------------------------------------------------------------- log ----

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static void LogDesires(DesireVector d, DesireBreakdown b, Radar radar,
            float rawRecon, float rawAggression, float enemyDropFrac, float ownDropFrac,
            AiRadarState state, CombatOpportunityReport opp)
        {
            AiDebugLog.Write($"[AI][V2]   desires — RCN raw {F(rawRecon)} smoothed {F(d.Raw[DesireAxis.Recon])} "
                + $"(expl {F(b.ReconExploration)} surv {F(b.ReconSurveillance)} blind {F(b.ReconEnemyBlindness)})");
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
