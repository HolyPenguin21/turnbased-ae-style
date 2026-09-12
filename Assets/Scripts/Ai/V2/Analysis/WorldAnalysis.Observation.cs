using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Observation — step stamps, invalidation detection (CaptureStepObservation, PublishStepObservationDelta, the change-detection helpers, and the snapshot diagnostic log).
    // File-split (mechanical, no behaviour change) from WorldAnalysis.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 5. Still exactly the WorldAnalysis
    // class; only this snapshot family's slice moved to its own file.
    public static partial class WorldAnalysis
    {
        internal static StepObservationStamp CaptureStepObservation(
            PlayerRoot root, AiHandData hand, WorldSnapshot snapshot) =>
            new StepObservationStamp(snapshot,
                root != null ? AiV2Trace.Stamp(root) : default, hand);

        internal static void PublishStepObservationDelta(PlayerSetupData player, int turn,
            StepObservationStamp before, StepObservationStamp after,
            ExecutionResult execution)
        {
            if (player == null || before == null || after == null)
                return;

            HashSet<int> contacts = ChangedContactIds(before.Snapshot, after.Snapshot);
            if (contacts.Count > 0)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.ReconKnowledge
                    | StrategicInvalidationReason.Contact,
                    contactIds: contacts);

            var reconHexes = new HashSet<HexCoord>();
            if (ReconKnowledgeChanged(before.Snapshot, after.Snapshot))
            {
                if (after.Snapshot?.MapKnowledge?.Frontier != null)
                    foreach (FrontierHexSnapshot frontier in after.Snapshot.MapKnowledge.Frontier)
                        reconHexes.Add(frontier.Hex);
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.ReconKnowledge, hexes: reconHexes);
            }

            HashSet<HexCoord> eventHexes = NewHexes(
                before.Snapshot?.Known?.EventGuardHexes,
                after.Snapshot?.Known?.EventGuardHexes);
            if (execution != null
                && execution.StopReason == ExecutionStopReason.HexEventStarted)
                eventHexes.Add(execution.FinalHex);
            if (eventHexes.Count > 0)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.ReconKnowledge
                    | StrategicInvalidationReason.EventState,
                    hexes: eventHexes);

            HashSet<HexCoord> resourceHexes =
                NewActionableResourceSites(before.Snapshot, after.Snapshot);
            if (resourceHexes.Count > 0)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.ReconKnowledge
                    | StrategicInvalidationReason.ResourceSite,
                    hexes: resourceHexes);

            HashSet<int> actorIds = ChangedActorIds(before.Snapshot, after.Snapshot);
            if (actorIds.Count > 0)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.Actor,
                    actorIds: actorIds);

            HashSet<int> capabilityActorIds =
                ChangedCapabilityActorIds(before.Snapshot, after.Snapshot);
            if (capabilityActorIds.Count > 0)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.Capability,
                    actorIds: capabilityActorIds);

            if (ThreatChanged(before.Snapshot, after.Snapshot))
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.Threat);

            if (InfrastructureChanged(before.Snapshot, after.Snapshot))
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.Infrastructure
                    | StrategicInvalidationReason.Capability);

            if (ResourceStockChanged(before.Resources, after.Resources))
                StrategicInterruptRegistry.Mark(
                    player, turn, StrategicInvalidationReason.Resources);

            // A builder settling onto its BuildExtraction/FoundBase hex changes nothing else this
            // step (no InfrastructureChanged, no Actor delta) — without an explicit fact here the
            // typed loop sees "no invalidation" and stops before Phase A gets to build on it.
            if (execution != null && execution.EconomyDeliveryReady)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.ResourceSite | StrategicInvalidationReason.Actor,
                    actorIds: execution.ActualActorArmyId.HasValue
                        ? new[] { execution.ActualActorArmyId.Value }
                        : null,
                    hexes: new[] { execution.FinalHex });

            if (before.Hand != after.Hand
                || before.HandVersion != after.HandVersion)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.Hand
                    | StrategicInvalidationReason.Capability,
                    hand: after.Hand);
        }

        private static bool ReconKnowledgeChanged(WorldSnapshot before, WorldSnapshot after)
        {
            MapKnowledgeSnapshot a = before?.MapKnowledge;
            MapKnowledgeSnapshot b = after?.MapKnowledge;
            if (a == null || b == null)
                return a != b;
            if (a.VisitedHexes != b.VisitedHexes || a.VisibleHexes != b.VisibleHexes)
                return true;

            var af = new HashSet<HexCoord>((a.Frontier ?? System.Array.Empty<FrontierHexSnapshot>())
                .Select(x => x.Hex));
            var bf = new HashSet<HexCoord>((b.Frontier ?? System.Array.Empty<FrontierHexSnapshot>())
                .Select(x => x.Hex));
            if (!af.SetEquals(bf))
                return true;

            var av = a.VisitedHexSet ?? new HashSet<HexCoord>();
            var bv = b.VisitedHexSet ?? new HashSet<HexCoord>();
            return !av.SetEquals(bv);
        }

        private static HashSet<int> ChangedContactIds(
            WorldSnapshot before, WorldSnapshot after)
        {
            Dictionary<int, AiMapMemory.KnownEnemySighting> old =
                KnownSightingsById(before);
            Dictionary<int, AiMapMemory.KnownEnemySighting> current =
                KnownSightingsById(after);
            var changed = new HashSet<int>();

            foreach (KeyValuePair<int, AiMapMemory.KnownEnemySighting> kv in current)
            {
                if (!old.TryGetValue(kv.Key, out AiMapMemory.KnownEnemySighting prior)
                    || !SameSighting(prior, kv.Value))
                    changed.Add(kv.Key);
            }
            foreach (int id in old.Keys)
                if (!current.ContainsKey(id))
                    changed.Add(id);
            return changed;
        }

        private static Dictionary<int, AiMapMemory.KnownEnemySighting> KnownSightingsById(
            WorldSnapshot snapshot)
        {
            var result = new Dictionary<int, AiMapMemory.KnownEnemySighting>();
            AddSightings(result, snapshot?.Known?.EnemySightings);
            AddSightings(result, snapshot?.Known?.NeutralSightings);
            return result;
        }

        private static void AddSightings(
            Dictionary<int, AiMapMemory.KnownEnemySighting> result,
            IEnumerable<AiMapMemory.KnownEnemySighting> sightings)
        {
            if (sightings == null) return;
            foreach (AiMapMemory.KnownEnemySighting sighting in sightings)
                if (sighting.ArmyId > 0)
                    result[sighting.ArmyId] = sighting;
        }

        private static bool SameSighting(AiMapMemory.KnownEnemySighting a,
            AiMapMemory.KnownEnemySighting b) =>
            a.Hex.Equals(b.Hex) && a.Owner == b.Owner
            && a.MemberCount == b.MemberCount
            && a.AttackSum == b.AttackSum && a.DefenseSum == b.DefenseSum
            && a.HasAntiAir == b.HasAntiAir
            && a.RecceRadius == b.RecceRadius
            && a.RecceSpotStrength == b.RecceSpotStrength
            && a.SeenTurn == b.SeenTurn;

        private static HashSet<int> ChangedActorIds(WorldSnapshot before, WorldSnapshot after)
        {
            var old = (before?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).ToDictionary(a => a.ArmyId);
            var current = (after?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).ToDictionary(a => a.ArmyId);
            var changed = new HashSet<int>();

            foreach (KeyValuePair<int, ArmySnapshot> kv in current)
            {
                if (!old.TryGetValue(kv.Key, out ArmySnapshot prior)
                    || !SameActor(prior, kv.Value))
                    changed.Add(kv.Key);
            }
            foreach (int id in old.Keys)
                if (!current.ContainsKey(id))
                    changed.Add(id);
            return changed;
        }

        private static bool SameActor(ArmySnapshot a, ArmySnapshot b) =>
            a.Hex.Equals(b.Hex) && a.MemberCount == b.MemberCount
            && a.HasActivatedThisTurn == b.HasActivatedThisTurn
            && a.CurrentMovement == b.CurrentMovement
            && a.ActivationApCost == b.ActivationApCost
            && a.ActivationEnergyCost == b.ActivationEnergyCost
            && a.IsHidden == b.IsHidden && a.IsAir == b.IsAir
            && a.IsSoloRecce == b.IsSoloRecce
            && a.IsStructuralRaidActor == b.IsStructuralRaidActor;

        private static HashSet<int> ChangedCapabilityActorIds(
            WorldSnapshot before, WorldSnapshot after)
        {
            var old = (before?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).ToDictionary(a => a.ArmyId);
            var current = (after?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).ToDictionary(a => a.ArmyId);
            var changed = new HashSet<int>();
            foreach (KeyValuePair<int, ArmySnapshot> kv in current)
            {
                if (!old.TryGetValue(kv.Key, out ArmySnapshot prior)
                    || prior.MemberCount != kv.Value.MemberCount
                    || prior.IsAir != kv.Value.IsAir
                    || prior.IsSoloRecce != kv.Value.IsSoloRecce
                    || prior.IsStructuralRaidActor != kv.Value.IsStructuralRaidActor
                    || prior.ActivationApCost != kv.Value.ActivationApCost
                    || prior.ActivationEnergyCost != kv.Value.ActivationEnergyCost)
                    changed.Add(kv.Key);
            }
            foreach (int id in old.Keys)
                if (!current.ContainsKey(id))
                    changed.Add(id);
            return changed;
        }

        private static bool ThreatChanged(WorldSnapshot before, WorldSnapshot after)
        {
            ThreatModel a = before?.Threat;
            ThreatModel b = after?.Threat;
            if (a == null || b == null)
                return a != b;
            if (a.UnderSiege != b.UnderSiege)
                return true;
            string[] ak = (a.Threats ?? System.Array.Empty<AssetThreatSnapshot>())
                .Select(ThreatKey).OrderBy(x => x).ToArray();
            string[] bk = (b.Threats ?? System.Array.Empty<AssetThreatSnapshot>())
                .Select(ThreatKey).OrderBy(x => x).ToArray();
            return !ak.SequenceEqual(bk);
        }

        private static string ThreatKey(AssetThreatSnapshot t)
        {
            if (t == null) return "-";
            int contactId = t.Contact?.Army?.ArmyId ?? 0;
            HexCoord assetHex = t.Asset != null ? t.Asset.Hex : default;
            return $"{contactId}:{t.Asset?.Kind}:{assetHex.Q},{assetHex.R}:"
                + $"{t.EnemyEta}:{t.ResponseEta}:{t.CanDamage}:{t.Severity:0.000}";
        }

        private static bool InfrastructureChanged(WorldSnapshot before, WorldSnapshot after)
        {
            string[] a = InfrastructureKeys(before);
            string[] b = InfrastructureKeys(after);
            return !a.SequenceEqual(b);
        }

        private static string[] InfrastructureKeys(WorldSnapshot snapshot)
        {
            IEnumerable<string> known = (snapshot?.Known?.Buildings
                    ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .Select(x => $"{x.Hex.Q},{x.Hex.R}:{x.IsStartingCitadel}:"
                    + string.Join(",", (x.FacilityAbilities ?? System.Array.Empty<string>())
                        .OrderBy(v => v)));
            IEnumerable<string> development = (snapshot?.Development?.Facilities
                    ?? System.Array.Empty<DevelopmentFacility>())
                .Select(x => $"dev:{x.Hex.Q},{x.Hex.R}:{x.Mode}:{x.HasHero}:{x.Contested}");
            return known.Concat(development).OrderBy(x => x).ToArray();
        }

        private static HashSet<HexCoord> NewHexes(
            IEnumerable<HexCoord> before, IEnumerable<HexCoord> after)
        {
            var old = new HashSet<HexCoord>(before ?? System.Array.Empty<HexCoord>());
            var result = new HashSet<HexCoord>();
            if (after != null)
                foreach (HexCoord hex in after)
                    if (!old.Contains(hex)) result.Add(hex);
            return result;
        }

        private static HashSet<HexCoord> NewActionableResourceSites(
            WorldSnapshot before, WorldSnapshot after)
        {
            var old = new HashSet<HexCoord>();
            if (before?.Known?.ResourceHexes != null)
                foreach (AiMapMemory.KnownResourceHex site in before.Known.ResourceHexes)
                    old.Add(site.Hex);

            var result = new HashSet<HexCoord>();
            if (after?.Known?.ResourceHexes == null || after.Economy == null)
                return result;
            foreach (EconomyExtractionOpportunity site in after.Economy.ExtractionOpportunities
                     ?? System.Array.Empty<EconomyExtractionOpportunity>())
                if (!old.Contains(site.Hex)
                    && site.MarginalIncomeGain > AiConfigV2.allocatorSliceEpsilon)
                    result.Add(site.Hex);
            return result;
        }

        private static bool ResourceStockChanged(
            V2ResourceStamp before, V2ResourceStamp after) =>
            before.Valid && after.Valid
            && (before.Human != after.Human || before.Energy != after.Energy
                || before.Materials != after.Materials || before.Tech != after.Tech);

        private static void LogSnapshot(PlayerSetupData player, WorldSnapshot s)
        {
            string nick = player?.Nickname ?? "?";
            SelfSnapshot self = s.Self;
            EconomyStanding eco = s.Economy;
            ThreatModel th = s.Threat;

            AiDebugLog.Write($"[AI][V2] {nick} worldscan turn {s.TurnNumber} — "
                + $"map {P(s.MapKnowledge.UnknownFrac)} dark (visited {s.MapKnowledge.VisitedHexes}/{s.MapKnowledge.TotalHexes}, "
                + $"visible {s.MapKnowledge.VisibleHexes}) | frontier {s.MapKnowledge.Frontier.Count} hexes, "
                + $"explorable {P(s.MapKnowledge.ExplorableUnknownFrac)}");

            AiDebugLog.Write($"[AI][V2]   self.power field={F(self.FieldPower)} garrison={F(self.GarrisonPower)} total={F(self.TotalPower)} "
                + $"| bestStack={F(self.BestStackPotential)} totalPotential={F(self.TotalMilitaryPotential)} "
                + $"| AP={self.ActionPoints} hand={self.Hand.Count}/{self.HandCapacity} deck={self.Deck.Count} "
                + $"| dev fac={(self.HasDevFacility ? 1 : 0)} op={(self.HasDevOperator ? 1 : 0)}");
            if (s.Development != null)
            {
                DevelopmentReadiness rd = s.Development;
                AiDebugLog.Write($"[AI][V2]   dev.readiness facilities={rd.Facilities.Count} "
                    + $"withHero={(rd.AnyFacilityWithHero ? 1 : 0)} offerings={rd.Offerings.Count} "
                    + $"bestP={P(rd.BestSuccessChance)} surplus={P(rd.SurplusFraction)} "
                    + $"prodSupport={F(rd.ProductionSupport)} targets={rd.UpgradeTargetCount}"
                    + $"{(rd.Facilities.Any(f => f.Contested) ? " [contested]" : "")}");
            }
            AiDebugLog.Write($"[AI][V2]   self.stock H/E/M/T={F(self.Stockpile.Human)}/{F(self.Stockpile.Energy)}/"
                + $"{F(self.Stockpile.Materials)}/{F(self.Stockpile.Tech)} "
                + $"| income={F(self.PerTurnIncome.Human)}/{F(self.PerTurnIncome.Energy)}/"
                + $"{F(self.PerTurnIncome.Materials)}/{F(self.PerTurnIncome.Tech)}");
            foreach (ArmySnapshot a in self.Armies)
                AiDebugLog.WriteVerbose($"[AI][V2]     army \"{ArmyLabel(a)}\" @{a.Hex.Q},{a.Hex.R} eff={F(a.EffectiveArmyPower)} "
                    + $"(compo={P(a.CompositionQuality)}, rawAtk/Def={F(a.AttackSum)}/{F(a.DefenseSum)}, n={a.MemberCount}"
                    + $"{(a.HasHero ? ", hero" : "")}){(a.IsGarrison ? " [garrison]" : "")}");

            AiDebugLog.Write($"[AI][V2]   economy.security={P(eco.EconomicSecurity)} "
                + $"(absFloor={P(eco.AbsFloor)} rel={F(eco.RelativePressure)} bottleneck={P(eco.BottleneckPressure)}) "
                + $"| deckNeed H/E/M/T={F(eco.DeckResourceNeed.Human)}/{F(eco.DeckResourceNeed.Energy)}/"
                + $"{F(eco.DeckResourceNeed.Materials)}/{F(eco.DeckResourceNeed.Tech)} "
                + $"| targetIncome H/E/M/T={F(eco.IncomeTarget.Human)}/{F(eco.IncomeTarget.Energy)}/"
                + $"{F(eco.IncomeTarget.Materials)}/{F(eco.IncomeTarget.Tech)} total={F(eco.IncomeTarget.Sum)} "
                + $"actualIncome={F(self.PerTurnIncome.Sum)}");
            foreach (EconomyResourceStanding rs in eco.PerType)
                AiDebugLog.WriteVerbose($"[AI][V2]     eco.{rs.Type} own={F(rs.OwnIncome)} fieldMedian={F(rs.FieldMedianIncome)} "
                    + $"ratio={F(rs.Ratio)}");

            int honest = th.Contacts.Count(c => c.Source == ContactSource.Honest);
            int cheat = th.Contacts.Count - honest;
            AiDebugLog.Write($"[AI][V2]   threat: contacts {th.Contacts.Count} (honest={honest} cheat={cheat}) "
                + $"assets {th.Assets.Count} listedThreats {th.Threats.Count} siege={(th.UnderSiege ? 1 : 0)}");
            foreach (AssetThreatSnapshot t in th.Threats.OrderByDescending(x => x.Severity).Take(6))
                AiDebugLog.WriteVerbose($"[AI][V2]     THREAT sev={F(t.Severity)} asset={t.Asset.Kind}@{t.Asset.Hex.Q},{t.Asset.Hex.R} "
                    + $"val={F(t.Asset.Value)} def={F(t.Asset.Defense)} vs {ContactLabel(t.Contact)} "
                    + $"canDmg={(t.CanDamage ? 1 : 0)} win={P(t.AttackWinChance)} "
                    + $"etaE={(t.EnemyEta?.ToString() ?? "-")} etaR={(t.ResponseEta?.ToString() ?? "-")} "
                    + $"dmg={P(t.PotentialDamage)} conf={P(t.Confidence)}");
        }

        private static string ArmyLabel(ArmySnapshot a) => a.Owner?.Nickname ?? "army";

        private static string ContactLabel(EnemyContactSnapshot c)
        {
            string who = c.Army?.Owner?.Nickname ?? "enemy";
            string where = c.Position.HasValue
                ? $"@{c.Position.Value.Q},{c.Position.Value.R}"
                : c.RegionCenter.HasValue ? $"~{c.RegionCenter.Value.Q},{c.RegionCenter.Value.R}r{c.RegionRadius}" : "?";
            return $"{who}({c.Knowledge},{c.Source},{where},pow={F(c.Army?.EffectiveArmyPower ?? 0f)})";
        }

        private static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        private static string P(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    }
}
