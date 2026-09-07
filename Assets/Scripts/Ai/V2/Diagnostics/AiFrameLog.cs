using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI FRAME LOG  (Strategy V2 — readable per-block turn trace)
    // ===========================================================================================
    //  Human-readable dump of the FROZEN turn frame — the blocks that run once, up front, before
    //  anything mutates game state: GAME STATE, WORLD ANALYSIS, STRATEGY LAYER, OBJECTIVES,
    //  MISSION CONTINUITY. Diagnostics only: no decisions, no state, every read null-guarded so a
    //  malformed snapshot can never throw out of a log call. One multi-line block per stage into
    //  AiDebugLog, gated by AiConfigV2.frameLogEnabled.
    //
    //  This is deliberately SEPARATE from AiV2Trace (the correlation-id [CHECK]/[STATE] machinery):
    //  that answers "why did attempt M03 get this AP"; this answers "what did the AI see and want
    //  this turn" in one glance.
    // ===========================================================================================
    internal static class AiFrameLog
    {
        private const string P = "[AI][V2][FRAME]";

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static string N2(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        private static string Pct(float frac01) => (frac01 * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

        private static void Head(string title) =>
            AiDebugLog.Write($"{P} -- {title} " + new string('-', System.Math.Max(3, 42 - title.Length)));

        // ---- GAME STATE -----------------------------------------------------------------------
        public static void GameState(WorldSnapshot snap, AiHandData hand)
        {
            if (!AiConfigV2.frameLogEnabled) return;
            SelfSnapshot self = snap?.Self;
            Head("GAME STATE");
            if (self == null) { AiDebugLog.Write($"{P}   (no self snapshot)"); return; }

            ResourceBundle sp = self.Stockpile;
            ResourceBundle inc = self.PerTurnIncome;
            AiDebugLog.Write($"{P}   AP {self.ActionPoints}   "
                + $"H {N(sp.Human)} E {N(sp.Energy)} M {N(sp.Materials)} T {N(sp.Tech)}   "
                + $"income H+{N(inc.Human)} E+{N(inc.Energy)} M+{N(inc.Materials)} T+{N(inc.Tech)}");

            var armies = self.Armies ?? (IReadOnlyList<ArmySnapshot>)System.Array.Empty<ArmySnapshot>();
            int garr = armies.Count(a => a != null && a.IsGarrison);
            int field = armies.Count(a => a != null && !a.IsGarrison);
            AiDebugLog.Write($"{P}   armies {armies.Count} (field {field}, garrison {garr})   "
                + $"power own {N(self.TotalPower)} (field {N(self.FieldPower)}, garr {N(self.GarrisonPower)})   "
                + $"best-stack {N(self.BestStackPotential)}");

            IReadOnlyList<CardData> h = self.Hand ?? hand?.Hand;
            string names = h == null || h.Count == 0
                ? "(empty)"
                : string.Join(", ", h.Select(c => c?.Definition?.displayName ?? "?"));
            int deck = self.Deck?.Count ?? hand?.RemainingDeckCount ?? 0;
            AiDebugLog.Write($"{P}   hand {(h?.Count ?? 0)}/{self.HandCapacity}: [{names}]   deck {deck} drawable");
        }

        // ---- WORLD ANALYSIS -----------------------------------------------------------------
        public static void WorldAnalysis(WorldSnapshot snap)
        {
            if (!AiConfigV2.frameLogEnabled) return;
            Head("WORLD ANALYSIS");
            if (snap == null) { AiDebugLog.Write($"{P}   (no snapshot)"); return; }

            MapKnowledgeSnapshot mk = snap.MapKnowledge;
            if (mk != null)
                AiDebugLog.Write($"{P}   map: visited {mk.VisitedHexes}/{mk.TotalHexes} "
                    + $"({Pct(mk.TotalHexes > 0 ? mk.VisitedHexes / (float)mk.TotalHexes : 0f)})   "
                    + $"frontier {(mk.Frontier?.Count ?? 0)} hexes   "
                    + $"explorable-unknown {Pct(mk.ExplorableUnknownFrac)}");

            KnownSnapshot known = snap.Known;
            ThreatModel threat = snap.Threat;
            var contacts = threat?.Contacts ?? (IReadOnlyList<EnemyContactSnapshot>)System.Array.Empty<EnemyContactSnapshot>();
            int exact = contacts.Count(c => c != null && c.Knowledge == ContactKnowledge.Exact);
            int lk = contacts.Count(c => c != null && c.Knowledge == ContactKnowledge.LastKnown);
            int region = contacts.Count(c => c != null && c.Knowledge == ContactKnowledge.Region);
            AiDebugLog.Write($"{P}   enemy: {contacts.Count} contacts ({exact} exact, {lk} last-known, {region} region)");
            if (known != null)
                AiDebugLog.Write($"{P}          seen force ~{N(known.EnemyKnownStrength)} "
                    + $"(Σ raw atk+def of remembered stacks)   "
                    + $"nearest-to-base {known.NearestEnemyToBase} hex   near-bases ~{N(known.EnemyStrengthNearBases)}");

            float maxSev = threat?.Threats == null || threat.Threats.Count == 0
                ? 0f : threat.Threats.Max(t => t?.Severity ?? 0f);
            AiDebugLog.Write($"{P}   threat: {(threat != null && threat.UnderSiege ? "UNDER SIEGE" : "clear")}   "
                + $"assets tracked {(threat?.Threats?.Count ?? 0)}   max severity {N2(maxSev)}");

            EconomyStanding eco = snap.Economy;
            if (eco != null)
                AiDebugLog.Write($"{P}   economy: security {N2(eco.EconomicSecurity)} "
                    + $"(abs-floor {N2(eco.AbsFloor)}, vs-field {eco.RelativePressure:+0.00;-0.00;0.00}, "
                    + $"bottleneck {N2(eco.BottleneckPressure)})");

            DevelopmentReadiness rd = snap.Development;
            if (rd != null)
                AiDebugLog.Write($"{P}   development: facilities {rd.Facilities.Count} "
                    + $"(with-hero {(rd.AnyFacilityWithHero ? "yes" : "no")}"
                    + $"{(rd.Facilities.Any(f => f.Contested) ? ", CONTESTED" : "")})  "
                    + $"offerings {rd.Offerings.Count}  best-success {N2(rd.BestSuccessChance)}  "
                    + $"surplus {N2(rd.SurplusFraction)}  upgrade-targets {rd.UpgradeTargetCount}");
        }

        // ---- STRATEGY LAYER ---------------------------------------------------------------
        public static void Strategy(RadarAssessment a)
        {
            if (!AiConfigV2.frameLogEnabled) return;
            Head("STRATEGY LAYER");
            if (a?.Radar == null || a.Desires == null) { AiDebugLog.Write($"{P}   (no assessment)"); return; }

            Radar r = a.Radar;
            float sum = DesireAxes.All.Sum(ax => r.Weight.TryGetValue(ax, out float w) ? w : 0f);
            AiDebugLog.Write($"{P}   radar   " + string.Join("  ",
                DesireAxes.All.Select(ax => $"{DesireAxes.Abbrev(ax)} {N2(r.Weight.TryGetValue(ax, out float w) ? w : 0f)}"))
                + $"   (sum {N2(sum)})");
            AiDebugLog.Write($"{P}   modifiers: military-threat {N2(a.Desires.MilitaryThreat)} (0=none .. 1=existential)   "
                + $"economic-runway {N2(a.Desires.EconomicRunway)} (0=broke .. 1=surplus)");

            DesireBreakdown b = a.Breakdown;
            if (b != null)
            {
                AiDebugLog.Write($"{P}   recon drivers:  explore {N2(b.ReconExploration)}  surveil {N2(b.ReconSurveillance)}  "
                    + $"blindness {N2(b.ReconEnemyBlindness)}  explore-press {N2(b.ReconExplorePressure)}  refresh-press {N2(b.ReconRefreshPressure)}");
                AiDebugLog.Write($"{P}   agg drivers:    opp {N2(b.AggOpportunity)}  raid-opp {N2(b.AggRaidOpportunity)}  "
                    + $"war-press {N2(b.AggWarPressure)}  rel-edge {N2(b.AggRelativeEdge)}  saturation {N2(b.AggPotentialSaturation)}  momentum {N2(b.AggMomentum)}");
                // Own-power balance (AiPower scale, NOT AP, NOT a 0..1 desire) — feeds the Aggression
                // surplus/free-power driver. home-guard = power kept back to defend bases (floored at
                // AiConfigV2.aggHomeGuardFloor); free = TotalPower - home-guard, available to attack.
                AiDebugLog.Write($"{P}   force balance:  home-guard {N2(b.RequiredDefensiveReserve)}  offensive-free {N2(b.OffensiveFreePower)}");
                AiDebugLog.Write($"{P}   dev drivers:    facility-ready {N2(b.DevFacilityReady)}  surplus {N2(b.DevSurplusFraction)}  "
                    + $"offering-quality {N2(b.DevOfferingQuality)}  best-success {N2(b.DevBestSuccessChance)}  targets {b.DevUpgradeTargets}");
                AiDebugLog.Write($"{P}   NOTE: Defence / Economy have no evaluator yet (raw desire = 0); "
                    + $"no driver breakdown exists for them.");
            }
        }

        // ---- OBJECTIVES -----------------------------------------------------------------------
        public static void Objectives(IReadOnlyList<ReconObjective> recon, IReadOnlyList<AggressionObjective> agg)
        {
            if (!AiConfigV2.frameLogEnabled) return;
            Head("OBJECTIVES");

            recon = recon ?? (IReadOnlyList<ReconObjective>)System.Array.Empty<ReconObjective>();
            AiDebugLog.Write($"{P}   recon {recon.Count}:");
            foreach (ReconObjective o in recon.OrderByDescending(o => o.BaseValue).Take(8))
                AiDebugLog.Write($"{P}     {o.Kind}@{o.FocusHex.Q},{o.FocusHex.R}  base {N(o.BaseValue)}  "
                    + $"risk {N2(o.DetectionRisk)}  stealth {o.Stealth}  severity {N2(o.Severity)}");

            agg = agg ?? (IReadOnlyList<AggressionObjective>)System.Array.Empty<AggressionObjective>();
            AiDebugLog.Write($"{P}   aggression {agg.Count}:");
            foreach (AggressionObjective o in agg.OrderByDescending(o => o.BaseValue).Take(8))
                AiDebugLog.Write($"{P}     {o.ObjectiveId}@{o.LastKnownHex.Q},{o.LastKnownHex.R}  base {N(o.BaseValue)}  "
                    + $"readyWin {N2(o.ReadyWinChance)}  asmWin {N2(o.AssemblableWinChance)}  def {o.DefenderCount}  "
                    + $"gate {(o.GatePassed ? 1 : 0)}{(o.NeedsCombatPower ? " needsPower" : "")}{(o.NeedsHero ? " needsHero" : "")}");
        }

        // ---- MISSION CONTINUITY ------------------------------------------------------------
        public static void MissionContinuity(IReadOnlyList<MissionIntent> intents, ActorCommitments commits)
        {
            if (!AiConfigV2.frameLogEnabled) return;
            Head("MISSION CONTINUITY");

            intents = intents ?? (IReadOnlyList<MissionIntent>)System.Array.Empty<MissionIntent>();
            AiDebugLog.Write($"{P}   active intents {intents.Count}:");
            foreach (MissionIntent i in intents)
            {
                if (i == null) continue;
                string susp = i.Status == IntentStatus.Suspended ? $"/{i.Suspended}" : "";
                string mover = i.PreferredMoverArmyId.HasValue ? $"#{i.PreferredMoverArmyId}" : "-";
                AiDebugLog.Write($"{P}     {i.Kind}/{i.IntentKey.SubKind} obj#{i.IntentKey.ObjectiveId} "
                    + $"@{i.IntentKey.Q},{i.IntentKey.R}  funding {i.Funding}  status {i.Status}{susp}  "
                    + $"age {i.TurnsActive}t  stall {i.StallTurns}t  mover {mover}  ap {N(i.CumulativeApSpent)}  steps {i.StepsMovedTotal}");
            }

            var claimed = commits?.ClaimedArmyIds;
            AiDebugLog.Write($"{P}   claimed armies: "
                + (claimed == null || claimed.Count == 0 ? "(none)" : string.Join(", ", claimed.Select(id => "#" + id))));
        }
    }
}
