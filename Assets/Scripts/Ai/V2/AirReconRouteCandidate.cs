using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI-AIR-01 — STRATEGIC AIR-RECON TARGET & ROUTE SELECTION
    // ===========================================================================================
    //  Air Recon must never fly to a hex just because GroundVisited == false. Aviation occupies
    //  nothing physically; it is an information tool, not a cheap substitute for a ground scout's
    //  walk. Two things this file adds on top of the pure per-step footprint scoring that
    //  ReconAirStepPlanner used to do alone:
    //
    //   1. AirReconAnchorModel — forms the sortie's DIRECTION first, from strategic landmarks, in
    //      priority order: known/hidden enemy concentration, the enemy Citadel, own facility
    //      perimeters carrying stale intel, the corridors between a known enemy and our valuable
    //      assets, and only then the unknown frontier. The omniscient TrueWorld read is used for
    //      DIRECTION ONLY — it raises sector pressure, it never marks a hex observed / discovered.
    //
    //   2. AirReconRouteScorer — scores a candidate first step for its PROVEN WHOLE ROUTE
    //      (outbound + return path already vetted by the shared aviation safety layer), not just
    //      the destination hex. A longer sweep that passes a stale facility perimeter or a probable
    //      enemy corridor can outscore a shorter radial out-and-back. Additive composite; every
    //      component is kept separate so the [Recon][Air][Route] log stays legible.
    // ===========================================================================================

    internal enum AirReconAnchorKind
    {
        EnemyConcentration,      // known / probable / hidden enemy army mass — sanitized to a sector
        EnemyCitadel,            // known Citadel, or its real sector as a hidden directional bias
        FriendlyFacilityApproach,// own production/resource facility perimeter with stale intel
        EnemyCorridor,           // space between a known enemy (army/Citadel) and our valuable asset
        IntelRefresh,            // a strategically important hex whose data has gone stale
        UnknownFrontier,         // genuine dark frontier — used only after every meaningful source
    }

    internal readonly struct AirReconStrategicAnchor
    {
        public readonly AirReconAnchorKind Kind;
        public readonly ReconSector Sector;
        public readonly HexCoord FocusHex;
        public readonly bool HasFocus;
        public readonly float Weight;

        public AirReconStrategicAnchor(AirReconAnchorKind kind, ReconSector sector, float weight,
            HexCoord focusHex, bool hasFocus)
        {
            Kind = kind;
            Sector = sector;
            Weight = weight;
            FocusHex = focusHex;
            HasFocus = hasFocus;
        }
    }

    // The formed strategic-direction picture for one air decision. Built once per Pick/PickFromStorage
    // and shared by every candidate first step.
    internal sealed class AirReconAnchorSet
    {
        public IReadOnlyList<AirReconStrategicAnchor> Anchors = Array.Empty<AirReconStrategicAnchor>();
        // Blended per-sector pressure [0..1+] measured from our Citadel as origin — the superset of
        // ReconDirectionModel's raw enemy-direction sectors, now folding in Citadel, facilities,
        // corridors and (weakly) the frontier.
        public IReadOnlyDictionary<ReconSector, float> SectorPressure =
            new Dictionary<ReconSector, float>();
        public ReconSector? CitadelSector;
        public float CitadelConfidence;   // 1.0 formally known, < 1 hidden directional bias only
        // Own facility perimeter hexes whose intel has gone stale — a route that sweeps near one
        // earns FriendlyFacilityCoverValue.
        public IReadOnlyList<HexCoord> StaleFacilityHexes = Array.Empty<HexCoord>();

        public float PressureFor(ReconSector sector) =>
            SectorPressure != null && SectorPressure.TryGetValue(sector, out float v) ? v : 0f;
    }

    internal static class AirReconAnchorModel
    {
        public static AirReconAnchorSet Build(WorldSnapshot snapshot, PlayerSetupData self, int turn)
        {
            var set = new AirReconAnchorSet();
            if (snapshot?.Self == null)
                return set;

            HexCoord origin = snapshot.Self.Citadel;
            var anchors = new List<AirReconStrategicAnchor>();
            var pressure = new Dictionary<ReconSector, float>();
            foreach (ReconSector s in Enum.GetValues(typeof(ReconSector)))
                pressure[s] = 0f;

            void AddPressure(ReconSector s, float w)
            {
                if (w > 0f) pressure[s] += w;
            }

            // --- 1. Enemy concentration (sanitized cheat: one base unit per true-world army). ----
            IReadOnlyList<ArmySnapshot> trueEnemies = snapshot.TrueWorld?.EnemyArmies;
            if (trueEnemies != null && trueEnemies.Count > 0)
            {
                var bySector = new Dictionary<ReconSector, int>();
                int counted = 0;
                foreach (ArmySnapshot e in trueEnemies)
                {
                    if (e == null) continue;
                    ReconSector s = ReconDirectionModel.Sector(origin, e.Hex);
                    bySector.TryGetValue(s, out int c);
                    bySector[s] = c + 1;
                    counted++;
                }
                foreach (KeyValuePair<ReconSector, int> kv in bySector)
                {
                    float frac = counted > 0 ? kv.Value / (float)counted : 0f;
                    float w = AiConfigV2.airReconAnchorConcentrationWeight * frac;
                    anchors.Add(new AirReconStrategicAnchor(AirReconAnchorKind.EnemyConcentration,
                        kv.Key, w, default, false));
                    AddPressure(kv.Key, w);
                }
            }

            // --- 2. Enemy Citadel — formally known, else real sector as a hidden directional bias.
            ReconSector? citadelSector = null;
            float citadelConfidence = 0f;
            AiMapMemory.KnownBuilding? knownCitadel = snapshot.Known?.Buildings?
                .Where(b => b.IsStartingCitadel && b.Owner != null && b.Owner != self)
                .Select(b => (AiMapMemory.KnownBuilding?)b)
                .FirstOrDefault();
            if (knownCitadel.HasValue)
            {
                citadelSector = ReconDirectionModel.Sector(origin, knownCitadel.Value.Hex);
                citadelConfidence = 1f;
                anchors.Add(new AirReconStrategicAnchor(AirReconAnchorKind.EnemyCitadel,
                    citadelSector.Value, AiConfigV2.airReconCitadelDirectionWeight,
                    knownCitadel.Value.Hex, true));
            }
            else
            {
                BuildingSnapshot hidden = snapshot.TrueWorld?.AllBuildings?
                    .FirstOrDefault(b => b != null && b.IsStartingCitadel && b.Owner != null && b.Owner != self);
                if (hidden != null)
                {
                    citadelSector = ReconDirectionModel.Sector(origin, hidden.Hex);
                    citadelConfidence = AiConfigV2.airReconCitadelHiddenConfidence;
                    // Sector only — the hidden hex is NOT exposed as a focus / discovered point.
                    anchors.Add(new AirReconStrategicAnchor(AirReconAnchorKind.EnemyCitadel,
                        citadelSector.Value,
                        AiConfigV2.airReconCitadelDirectionWeight * citadelConfidence, default, false));
                }
            }
            if (citadelSector.HasValue)
                AddPressure(citadelSector.Value,
                    AiConfigV2.airReconCitadelDirectionWeight * citadelConfidence);
            set.CitadelSector = citadelSector;
            set.CitadelConfidence = citadelConfidence;

            // --- 3. Own facilities with stale perimeter intel + probable enemy approach. ---------
            var ownFacilityHexes = new List<HexCoord>();
            if (snapshot.Known?.Buildings != null)
                foreach (AiMapMemory.KnownBuilding b in snapshot.Known.Buildings)
                    if (b.Owner == self && !b.IsStartingCitadel)
                        ownFacilityHexes.Add(b.Hex);
            if (snapshot.Self.BaseHexes != null)
                foreach (HexCoord h in snapshot.Self.BaseHexes)
                    if (!h.Equals(origin) && !ownFacilityHexes.Contains(h))
                        ownFacilityHexes.Add(h);

            var staleFacilities = new List<HexCoord>();
            foreach (HexCoord f in ownFacilityHexes)
            {
                bool stale = !AiReconIntelMemory.TryGetIntelAge(self, f, turn, out int age)
                    || age >= AiConfigV2.airReconFacilityStaleAgeMin;
                if (!stale)
                    continue;
                staleFacilities.Add(f);
                ReconSector fs = ReconDirectionModel.Sector(origin, f);
                anchors.Add(new AirReconStrategicAnchor(AirReconAnchorKind.FriendlyFacilityApproach,
                    fs, AiConfigV2.airReconFacilityCoverWeight, f, true));
                AddPressure(fs, AiConfigV2.airReconFacilityCoverWeight);

                // Probable enemy approach onto that facility — the direction from the facility
                // toward the nearest known enemy sighting / the enemy Citadel.
                HexCoord? threat = NearestKnownThreat(snapshot, self, f, knownCitadel);
                if (threat.HasValue)
                {
                    ReconSector approach = ReconDirectionModel.Sector(origin, threat.Value);
                    AddPressure(approach, 0.5f * AiConfigV2.airReconFacilityCoverWeight);
                }
            }
            set.StaleFacilityHexes = staleFacilities;

            // --- 4. Enemy movement corridors — midpoint between a known enemy and our asset. -----
            var ownAssets = new List<HexCoord> { origin };
            ownAssets.AddRange(ownFacilityHexes);
            IEnumerable<HexCoord> knownEnemyPoints = (snapshot.Known?.EnemySightings ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>())
                .Select(s => s.Hex);
            if (knownCitadel.HasValue)
                knownEnemyPoints = knownEnemyPoints.Concat(new[] { knownCitadel.Value.Hex });
            foreach (HexCoord ep in knownEnemyPoints)
            {
                HexCoord asset = ownAssets.OrderBy(a => HexGridMath.Distance(a, ep)).First();
                var mid = new HexCoord((ep.Q + asset.Q) / 2, (ep.R + asset.R) / 2);
                ReconSector cs = ReconDirectionModel.Sector(origin, mid);
                anchors.Add(new AirReconStrategicAnchor(AirReconAnchorKind.EnemyCorridor,
                    cs, AiConfigV2.airReconAnchorCorridorWeight, mid, true));
                AddPressure(cs, AiConfigV2.airReconAnchorCorridorWeight);
            }

            // --- 5. Intel refresh — known enemy sightings whose data has gone stale. -------------
            if (snapshot.Known?.EnemySightings != null)
                foreach (AiMapMemory.KnownEnemySighting s in snapshot.Known.EnemySightings)
                {
                    if (AiReconIntelMemory.TryGetIntelAge(self, s.Hex, turn, out int age)
                        && age < AiConfigV2.airReconFacilityStaleAgeMin)
                        continue;
                    ReconSector rs = ReconDirectionModel.Sector(origin, s.Hex);
                    anchors.Add(new AirReconStrategicAnchor(AirReconAnchorKind.IntelRefresh,
                        rs, 0.5f * AiConfigV2.airReconFacilityCoverWeight, s.Hex, true));
                    AddPressure(rs, 0.5f * AiConfigV2.airReconFacilityCoverWeight);
                }

            // --- 6. Unknown frontier — weakest source, only ever a tie-breaker (spec §1 last). ---
            IReadOnlyList<FrontierHexSnapshot> frontier = snapshot.MapKnowledge?.Frontier;
            if (frontier != null && frontier.Count > 0)
            {
                var frontierSectors = new HashSet<ReconSector>();
                foreach (FrontierHexSnapshot fh in frontier)
                    frontierSectors.Add(ReconDirectionModel.Sector(origin, fh.Hex));
                foreach (ReconSector fs in frontierSectors)
                {
                    anchors.Add(new AirReconStrategicAnchor(AirReconAnchorKind.UnknownFrontier,
                        fs, AiConfigV2.airReconAnchorFrontierWeight, default, false));
                    AddPressure(fs, AiConfigV2.airReconAnchorFrontierWeight);
                }
            }

            // Normalise so the strongest sector reads ~1.0 — the per-step direction term is a
            // Clamp01 of this, and every downstream weight is calibrated against a 0..1 signal.
            float peak = pressure.Values.DefaultIfEmpty(0f).Max();
            if (peak > 1f)
                foreach (ReconSector s in pressure.Keys.ToList())
                    pressure[s] /= peak;

            set.SectorPressure = pressure;
            set.Anchors = anchors
                .OrderBy(a => (int)a.Kind)
                .ThenByDescending(a => a.Weight)
                .ToList();
            return set;
        }

        private static HexCoord? NearestKnownThreat(WorldSnapshot snapshot, PlayerSetupData self,
            HexCoord from, AiMapMemory.KnownBuilding? knownCitadel)
        {
            HexCoord? best = null;
            int bestD = int.MaxValue;
            if (snapshot.Known?.EnemySightings != null)
                foreach (AiMapMemory.KnownEnemySighting s in snapshot.Known.EnemySightings)
                {
                    int d = HexGridMath.Distance(from, s.Hex);
                    if (d < bestD) { bestD = d; best = s.Hex; }
                }
            if (knownCitadel.HasValue)
            {
                int d = HexGridMath.Distance(from, knownCitadel.Value.Hex);
                if (d < bestD) best = knownCitadel.Value.Hex;
            }
            return best;
        }
    }

    // -------------------------------------------------------------------------------------------
    //  ROUTE CANDIDATE + SCORER
    // -------------------------------------------------------------------------------------------
    internal readonly struct AirReconRouteCandidate
    {
        public readonly HexCoord FirstStep;
        public readonly HexCoord ObjectiveHex;
        public readonly HexCoord LandingHex;
        public readonly AirReconAnchorKind? AnchorKind;

        public readonly float InformationGain;
        public readonly float StaleIntelRefreshValue;
        public readonly float EnemyInterest;
        public readonly float EnemyCitadelDirectionValue;
        public readonly float FriendlyFacilityCoverValue;
        public readonly float RouteObservationValue;
        public readonly float CombatOpportunityValue;
        public readonly float TravelCost;
        public readonly float ActivationCost;
        public readonly float RecoveryRisk;
        public readonly float RedundancyPenalty;
        public readonly float TotalScore;

        public readonly bool Rejected;
        public readonly string RejectReason;
        public readonly string Breakdown;

        public AirReconRouteCandidate(HexCoord firstStep, HexCoord objectiveHex, HexCoord landingHex,
            AirReconAnchorKind? anchorKind, float informationGain, float staleIntelRefreshValue,
            float enemyInterest, float enemyCitadelDirectionValue, float friendlyFacilityCoverValue,
            float routeObservationValue, float combatOpportunityValue, float travelCost,
            float activationCost, float recoveryRisk, float redundancyPenalty, float totalScore,
            bool rejected, string rejectReason, string breakdown)
        {
            FirstStep = firstStep;
            ObjectiveHex = objectiveHex;
            LandingHex = landingHex;
            AnchorKind = anchorKind;
            InformationGain = informationGain;
            StaleIntelRefreshValue = staleIntelRefreshValue;
            EnemyInterest = enemyInterest;
            EnemyCitadelDirectionValue = enemyCitadelDirectionValue;
            FriendlyFacilityCoverValue = friendlyFacilityCoverValue;
            RouteObservationValue = routeObservationValue;
            CombatOpportunityValue = combatOpportunityValue;
            TravelCost = travelCost;
            ActivationCost = activationCost;
            RecoveryRisk = recoveryRisk;
            RedundancyPenalty = redundancyPenalty;
            TotalScore = totalScore;
            Rejected = rejected;
            RejectReason = rejectReason;
            Breakdown = breakdown;
        }
    }

    // R3 review fix — the reservation prepass probes a route WITHOUT owning the live sortie state /
    // registries the executor updates between actors, so identity exclusion and provisional
    // (reserved-but-not-launched) sector coverage must be passed to the planner EXPLICITLY. Both
    // the executor and the prepass now feed the scorer one unambiguous context.
    internal sealed class AirReconScoringContext
    {
        // The sortie whose own recent footprint stamps must NOT read as "a repeat by another
        // sortie". -1 = exclude nothing (a not-yet-launched storage candidate).
        public int ExcludeSortieId = -1;
        // Wedges (from our Citadel) an air slot accepted EARLIER in the SAME reservation prepass
        // has claimed but not yet launched — invisible to the live ReconAssignment scan, so they
        // are added to the candidate's sector-coverage count here.
        public IReadOnlyList<ReconSector> ProvisionalWedgeClaims;

        public int ProvisionalClaimsIn(ReconSector wedge)
        {
            if (ProvisionalWedgeClaims == null)
                return 0;
            int n = 0;
            for (int i = 0; i < ProvisionalWedgeClaims.Count; i++)
                if (ProvisionalWedgeClaims[i] == wedge)
                    n++;
            return n;
        }
    }

    internal readonly struct AirReconRouteInputs
    {
        public readonly PlayerSetupData Player;
        public readonly HexMap Map;
        public readonly ReconMode Mode;
        public readonly int Turn;
        public readonly HexCoord From;
        public readonly HexCoord FirstStep;
        public readonly HexCoord ObjectiveHex;   // the committed next step (receding horizon); strategic direction lives in Anchors, not here
        public readonly HexCoord LandingHex;
        public readonly IReadOnlyList<HexCoord> OutboundHexes;
        public readonly IReadOnlyList<HexCoord> ReturnHexes;
        public readonly int Vision;
        public readonly int RouteCost;
        public readonly int RequiredTurns;
        public readonly int RequiredUnlandedEnds;
        public readonly float ActivationAp;
        public readonly float ActivationEnergy;
        public readonly int NeverObservedFootprint;
        public readonly float StaleFootprint;
        public readonly AirReconAnchorSet Anchors;
        public readonly WorldSnapshot Snapshot;
        public readonly ReconAirSortieState SortieState;
        public readonly int OtherSectorClaims;
        public readonly int ExcludeSortieId;   // recent-air-coverage from THIS sortie is not "a repeat" (-1 = exclude nothing)

        public AirReconRouteInputs(PlayerSetupData player, HexMap map, ReconMode mode, int turn,
            HexCoord from, HexCoord firstStep, HexCoord objectiveHex, HexCoord landingHex,
            IReadOnlyList<HexCoord> outboundHexes, IReadOnlyList<HexCoord> returnHexes, int vision,
            int routeCost, int requiredTurns, int requiredUnlandedEnds, float activationAp,
            float activationEnergy, int neverObservedFootprint, float staleFootprint,
            AirReconAnchorSet anchors, WorldSnapshot snapshot, ReconAirSortieState sortieState,
            int otherSectorClaims, int excludeSortieId)
        {
            Player = player;
            Map = map;
            Mode = mode;
            Turn = turn;
            From = from;
            FirstStep = firstStep;
            ObjectiveHex = objectiveHex;
            LandingHex = landingHex;
            OutboundHexes = outboundHexes;
            ReturnHexes = returnHexes;
            Vision = vision;
            RouteCost = routeCost;
            RequiredTurns = requiredTurns;
            RequiredUnlandedEnds = requiredUnlandedEnds;
            ActivationAp = activationAp;
            ActivationEnergy = activationEnergy;
            NeverObservedFootprint = neverObservedFootprint;
            StaleFootprint = staleFootprint;
            Anchors = anchors;
            Snapshot = snapshot;
            SortieState = sortieState;
            OtherSectorClaims = otherSectorClaims;
            ExcludeSortieId = excludeSortieId;
        }
    }

    internal static class AirReconRouteScorer
    {
        public static AirReconRouteCandidate Score(AirReconRouteInputs x)
        {
            // --- Destination-footprint split (unchanged basis, kept as two named components). ---
            float infoGain = x.Mode == ReconMode.Explore
                ? AiConfigV2.airReconNeverObservedWeight * x.NeverObservedFootprint
                : 0.20f * AiConfigV2.airReconNeverObservedWeight * x.NeverObservedFootprint;
            float staleRefresh = x.Mode == ReconMode.Explore
                ? 0.20f * AiConfigV2.airReconStaleWeight * x.StaleFootprint
                : AiConfigV2.airReconStaleWeight * x.StaleFootprint;

            // --- EnemyInterest — blended sanitized sector pressure toward the first step. --------
            // R2 review fix — measure the step's wedge from our CITADEL, the same frame the anchor
            // SectorPressure dict is built in (AirReconAnchorModel) and the same one sector
            // deconfliction now uses. A `Sector(from, h)` read (aircraft-relative) meant the anchor
            // lookup compared two different frames for a wing far from home.
            HexCoord sectorOrigin = x.Snapshot?.Self != null ? x.Snapshot.Self.Citadel : x.From;
            ReconSector stepSector = ReconDirectionModel.Sector(sectorOrigin, x.FirstStep);
            float sectorPressure = x.Anchors != null ? x.Anchors.PressureFor(stepSector) : 0f;
            float enemyInterest = AiConfigV2.airReconDirectionWeight * Mathf.Clamp01(sectorPressure);

            // --- EnemyCitadelDirectionValue — first step heads into the Citadel sector. ----------
            float citadelDir = 0f;
            if (x.Anchors?.CitadelSector != null && x.Anchors.CitadelSector.Value == stepSector)
                citadelDir = AiConfigV2.airReconCitadelDirectionWeight * x.Anchors.CitadelConfidence;

            // --- Forward-corridor observation value + redundancy scan (spec §3 / §5). -----------
            // P0 review fix — score ONLY the corridor the sortie actually commits to flying toward
            // its objective (OutboundHexes: this step for a 1-turn boomerang, the real multi-turn
            // approach path otherwise). The RETURN path is a receding-horizon forecast the one-step
            // executor discards and re-plans (landing can even switch via hysteresis), so it must
            // never contribute informational value — only recovery feasibility / cost / risk. This
            // removes the phantom double-count where one future return zone credited several
            // consecutive outbound steps that never flew it.
            var routeHexes = new List<HexCoord>();
            var seen = new HashSet<HexCoord>();
            if (x.OutboundHexes != null)
            {
                foreach (HexCoord h in x.OutboundHexes)
                {
                    if (h.Equals(x.From) || !seen.Add(h))
                        continue;
                    routeHexes.Add(h);
                    if (routeHexes.Count >= AiConfigV2.airReconRouteObservationMaxHexes)
                        break;
                }
            }

            float routeObs = 0f;
            float decay = 1f;
            int informativeHexes = 0;
            float observationNovelty = 0f;   // raw Σ per-hex usefulness — "how much genuinely new/stale territory"
            // P1 review fix — recent-air-coverage overlap is measured on EVERY corridor hex,
            // INDEPENDENTLY of current usefulness. A hex a previous sortie just made fresh has
            // usefulness ~0, so gating the recent check on "still informative" made a successful
            // reflight invisible to the repeat rule. Two separate metrics: ObservationNovelty
            // (drives value) and RecentAirCoverageOverlap (drives the penalty + hard reject).
            // R2 review fix — the overlap query excludes THIS sortie's own footprint stamps
            // (AirReconCoverageRegistry is sortie-tagged): an r1-Recce aircraft footprints all six
            // neighbours of its next candidate step, and without the exclusion every follow-on step
            // scored 100% "recent overlap" against itself and was hard-rejected, stalling the sortie
            // after one step (and breaking AI-AIR-02 multi-turn continuation). A DIFFERENT sortie —
            // including a second wing the same turn — still counts.
            int recentAirCoverageOverlap = 0;
            foreach (HexCoord h in routeHexes)
            {
                float baseLocal = HexInfoUsefulness(x.Player, x.Map, h, x.Turn);
                observationNovelty += baseLocal;
                float local = baseLocal;
                if (baseLocal > 0.01f)
                {
                    float ring = 0f;
                    foreach (HexCoord n in HexGridMath.Neighbors(h))
                        ring += HexInfoUsefulness(x.Player, x.Map, n, x.Turn);
                    local += AiConfigV2.airReconRouteObservationRingWeight * ring;
                    informativeHexes++;
                }
                if (AirReconCoverageRegistry.RecentlyCoveredByOther(x.Player, h, x.Turn,
                        AiConfig.airReconTargetCooldownTurns, x.ExcludeSortieId))
                    recentAirCoverageOverlap++;
                routeObs += decay * local;
                decay *= AiConfigV2.airReconRouteObservationDecay;
            }
            routeObs *= AiConfigV2.airReconRouteObservationWeight;

            // Return path contributes ONLY known-AA proximity to RecoveryRisk (spec §4) — no
            // observation / facility / combat credit.
            int aaAdjacentHexes = 0;
            foreach (HexCoord h in routeHexes)
                if (AiAviationSupport.KnownAaExposureAt(x.Player, h) > 0)
                    aaAdjacentHexes++;
            if (x.ReturnHexes != null)
                foreach (HexCoord h in x.ReturnHexes)
                    if (!h.Equals(x.From) && AiAviationSupport.KnownAaExposureAt(x.Player, h) > 0)
                        aaAdjacentHexes++;

            // --- FriendlyFacilityCoverValue — forward corridor sweeps a stale own-facility
            //     perimeter (outbound hexes only after the P0 fix). --------------------------------
            float facilityCover = 0f;
            if (x.Anchors?.StaleFacilityHexes != null && x.Anchors.StaleFacilityHexes.Count > 0
                && routeHexes.Count > 0)
            {
                int radius = Math.Max(1, AiConfigV2.airReconFacilityCoverRadius);
                foreach (HexCoord f in x.Anchors.StaleFacilityHexes)
                {
                    int minD = int.MaxValue;
                    foreach (HexCoord h in routeHexes)
                        minD = Math.Min(minD, HexGridMath.Distance(h, f));
                    if (minD <= radius)
                        facilityCover += AiConfigV2.airReconFacilityCoverWeight
                            * (1f - minD / (float)(radius + 1));
                }
            }

            // --- CombatOpportunityValue — forward corridor passes an HONESTLY-known enemy
            //     sighting (outbound hexes only after the P0 fix). ---------------------------------
            float combatOpp = 0f;
            IReadOnlyList<AiMapMemory.KnownEnemySighting> sightings = x.Snapshot?.Known?.EnemySightings;
            if (sightings != null && routeHexes.Count > 0)
            {
                foreach (AiMapMemory.KnownEnemySighting s in sightings)
                {
                    bool near = false;
                    foreach (HexCoord h in routeHexes)
                        if (HexGridMath.Distance(h, s.Hex) <= 1) { near = true; break; }
                    if (near)
                        combatOpp += AiConfigV2.airReconCombatOpportunityWeight * (s.HasAntiAir ? 0.5f : 1f);
                }
                combatOpp = Mathf.Min(combatOpp, AiConfigV2.airReconCombatOpportunityCap);
            }

            // --- Costs / risks (spec §4). ------------------------------------------------------
            float travelCost = AiConfigV2.airReconRouteCostPenalty * x.RouteCost
                + AiConfigV2.airReconExtraTurnPenalty * Math.Max(0, x.RequiredTurns - 1);
            float activationCost = AiConfigV2.airReconActivationApPenalty * x.ActivationAp
                + AiConfigV2.airReconActivationEnergyPenalty * x.ActivationEnergy;
            float recoveryRisk = AiConfigV2.airReconRecoveryRiskWeight
                * (Math.Max(0, x.RequiredTurns - 1) + 0.5f * Math.Max(0, x.RequiredUnlandedEnds)
                   + aaAdjacentHexes);

            // --- RedundancyPenalty (spec §5): recent air-coverage overlap + outbound-trail hug +
            //     coarse-sector coverage already held by another Recon actor. --------------------
            float redundancy = AiConfigV2.airReconRedundancyRecentObsPenalty * recentAirCoverageOverlap;
            bool shaping = x.SortieState != null
                && (x.SortieState.Phase == ReconAirPhase.Outbound
                    || x.SortieState.Phase == ReconAirPhase.Turning);
            int trailOverlap = 0;
            bool lateral = false;
            if (shaping)
            {
                trailOverlap = x.SortieState.TrailAdjacency(x.FirstStep, x.From);
                redundancy += AiConfigV2.airReconOutboundTrailOverlapPenalty * trailOverlap;
                float lateralWeight = x.SortieState.Phase == ReconAirPhase.Turning ? 2f : 1f;
                lateral = (infoGain + staleRefresh + routeObs) > 0.01f
                    && HexGridMath.Distance(x.SortieState.LaunchHex, x.FirstStep)
                        <= HexGridMath.Distance(x.SortieState.LaunchHex, x.From);
                if (lateral)
                    routeObs += lateralWeight * AiConfigV2.airReconLateralNoveltyBonus;
            }

            float positive = infoGain + staleRefresh + enemyInterest + citadelDir
                + facilityCover + routeObs + combatOpp;
            float total = positive - travelCost - activationCost - recoveryRisk - redundancy;

            // Coverage deconfliction — soft divisor for a sector another Recon actor (air OR
            // ground) is already working, so several actors spread out instead of grinding one
            // corridor. x.OtherSectorClaims now counts air sorties + ground scouts in the sector
            // (see BuildChoice), and is populated for a storage launch too (P1 review fix).
            if (x.OtherSectorClaims > 0)
                total /= 1f + AiConfigV2.airReconCoverageOverlapPenalty * x.OtherSectorClaims;

            // --- Hard rules (spec §5). --------------------------------------------------------
            bool rejected = false;
            string reject = null;
            if (positive <= AiConfigV2.airReconStrategicValueFloor)
            {
                rejected = true;
                reject = "no strategic value (only GroundVisited==false)";
            }
            else if (routeHexes.Count > 0
                && recentAirCoverageOverlap / (float)routeHexes.Count
                    >= AiConfigV2.airReconRedundancyRecentObsRejectFrac)
            {
                rejected = true;
                reject = $"repeats recent air observation ({recentAirCoverageOverlap}/{routeHexes.Count} corridor hexes)";
            }
            else if (x.OtherSectorClaims >= AiConfigV2.airReconSectorAdequateCoverage
                && observationNovelty <= AiConfigV2.airReconSectorCoveredNoveltyFloor)
            {
                // spec §5 — "the same sector is already adequately covered by another assigned
                // Recon actor". Hard reject only when the sector is staffed AND this corridor
                // brings no substantial new observation; a genuinely novel sweep still passes.
                rejected = true;
                reject = $"sector already covered ({x.OtherSectorClaims} actor(s), novelty={observationNovelty:0.00})";
            }

            AirReconAnchorKind? anchorKind = x.Anchors?.Anchors?
                .Where(a => a.Sector == stepSector)
                .Select(a => (AirReconAnchorKind?)a.Kind)
                .FirstOrDefault();

            string breakdown =
                $"info={infoGain:0.00} stale={staleRefresh:0.00} enemyInt={enemyInterest:0.00} "
                + $"citDir={citadelDir:0.00} facCover={facilityCover:0.00} routeObs={routeObs:0.00}"
                + $"(corridor={routeHexes.Count},informative={informativeHexes},novelty={observationNovelty:0.00},"
                + $"recentOverlap={recentAirCoverageOverlap}) "
                + $"combat={combatOpp:0.00} -travel={travelCost:0.00} -activation={activationCost:0.00} "
                + $"-recovery={recoveryRisk:0.00}(aaAdj={aaAdjacentHexes}) -redundancy={redundancy:0.00}"
                + $"(trail={trailOverlap},lateral={(lateral ? 1 : 0)}) sectorReconActors={x.OtherSectorClaims} "
                + $"anchor={(anchorKind?.ToString() ?? "none")} => {total:0.00}"
                + (rejected ? $" [REJECT {reject}]" : string.Empty);

            return new AirReconRouteCandidate(x.FirstStep, x.ObjectiveHex, x.LandingHex, anchorKind,
                infoGain, staleRefresh, enemyInterest, citadelDir, facilityCover, routeObs, combatOpp,
                travelCost, activationCost, recoveryRisk, redundancy, total, rejected, reject, breakdown);
        }

        // Per-hex information usefulness on the SAME basis ReconAirStepPlanner.ScoreInformation
        // uses: never-observed (no recorded intel age) is worth a full unit, an observed hex ramps
        // 0..1 with IntelAge across the shared staleness window. Aviation reveals; it never marks a
        // hex ground-Visited, so this is deliberately blind to VisionSystem.IsVisited.
        private static float HexInfoUsefulness(PlayerSetupData player, HexMap map, HexCoord h, int turn)
        {
            if (map == null || !map.TryGetTerrainAt(h, out _))
                return 0f;
            if (!AiReconIntelMemory.TryGetIntelAge(player, h, turn, out int age))
                return 1f;
            return Mathf.InverseLerp(AiConfigV2.scoutSurveilStaleTurnsLo,
                AiConfigV2.scoutSurveilStaleTurnsHi, age);
        }
    }
}
