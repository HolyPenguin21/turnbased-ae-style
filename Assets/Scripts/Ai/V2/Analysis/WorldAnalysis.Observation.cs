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

            // Compare actual opportunity facts, not just newly discovered resource hexes.
            // FoundBase is included in the same canonical producer as extraction and collection.
            // An empty newly scouted base site can wake Economy without waking it merely
            // because an unrelated scout moved: only a genuine opportunity delta publishes.
            HashSet<HexCoord> changedSites =
                ChangedEconomicOpportunitySites(before.Snapshot, after.Snapshot);
            changedSites.ExceptWith(resourceHexes);
            if (changedSites.Count > 0)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.ResourceSite,
                    hexes: changedSites);

            HashSet<int> actorIds = ChangedActorIds(before.Snapshot, after.Snapshot);
            if (actorIds.Count > 0)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.Actor,
                    actorIds: actorIds);

            // Donor-only preparation changes intent state but not the army snapshot.
            if (execution != null && execution.EconomyPrepared)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.Actor,
                    actorIds: execution.ActualActorArmyId.HasValue
                        ? new[] { execution.ActualActorArmyId.Value }
                        : null);

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

            if (execution != null && execution.DevelopmentDeliveryReady)
                StrategicInterruptRegistry.Mark(player, turn,
                    StrategicInvalidationReason.Actor | StrategicInvalidationReason.Capability,
                    actorIds: execution.ActualActorArmyId.HasValue
                        ? new[] { execution.ActualActorArmyId.Value } : null,
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
                if (sighting.ArmyId >= 0)
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
            && a.SeenTurn == b.SeenTurn
            && SameCombatProfiles(a.Defenders, b.Defenders);

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
            && a.IsStructuralRaidActor == b.IsStructuralRaidActor
            && SameCombatCapability(a, b);

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
                    || prior.ActivationEnergyCost != kv.Value.ActivationEnergyCost
                    || !SameCombatCapability(prior, kv.Value))
                    changed.Add(kv.Key);
            }
            foreach (int id in old.Keys)
                if (!current.ContainsKey(id))
                    changed.Add(id);
            return changed;
        }

        private static bool SameCombatCapability(ArmySnapshot a, ArmySnapshot b) =>
            a.AttackSum == b.AttackSum
            && a.DefenseSum == b.DefenseSum
            && a.EffectiveArmyPower == b.EffectiveArmyPower
            && a.CompositionQuality == b.CompositionQuality
            && a.HasHero == b.HasHero
            && a.BestHeroCommandRating == b.BestHeroCommandRating
            && a.Commander.Equals(b.Commander)
            && a.HasResearchOperator == b.HasResearchOperator
            && a.HasProductionOperator == b.HasProductionOperator
            && a.HasAntiAir == b.HasAntiAir
            && a.Capacity == b.Capacity
            && a.OccupiedBattleSlots == b.OccupiedBattleSlots
            && a.StrategicCoverage == b.StrategicCoverage
            && SameCombatProfiles(a.Members, b.Members);

        private static bool SameCombatProfiles(
            IReadOnlyList<WorthIt.DefenderProfile> a,
            IReadOnlyList<WorthIt.DefenderProfile> b)
        {
            int aCount = a?.Count ?? 0;
            int bCount = b?.Count ?? 0;
            if (aCount != bCount)
                return false;
            for (int i = 0; i < aCount; i++)
            {
                WorthIt.DefenderProfile x = a[i];
                WorthIt.DefenderProfile y = b[i];
                if (x.Attack != y.Attack || x.Defense != y.Defense
                    || x.HitPoints != y.HitPoints
                    || x.MaxHitPoints != y.MaxHitPoints
                    || x.Initiative != y.Initiative
                    || x.HasCeramicArmor != y.HasCeramicArmor
                    || !(x.TypeTags ?? System.Array.Empty<UnitTypeTag>())
                        .SequenceEqual(y.TypeTags ?? System.Array.Empty<UnitTypeTag>())
                    || !(x.Abilities ?? System.Array.Empty<string>())
                        .SequenceEqual(y.Abilities ?? System.Array.Empty<string>()))
                    return false;
            }
            return true;
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
                + $"{t.EnemyEta}:{t.ResponseEta}:{t.CanDamage}:"
                + t.Severity.ToString("R", CultureInfo.InvariantCulture);
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
                .Select(x => $"{x.Hex.Q},{x.Hex.R}:{x.IsStartingCitadel}:{x.IsBase}:"
                    + $"owner={x.Owner?.ColorIndex}:{x.Owner?.Nickname}:"
                    // Defense is part of this key because Attack/Reaction combat estimates read it
                    // via AiMapMemory.KnownHexDefenseBonus. The owner of "did
                    // infrastructure change" is this comparison; it must cover every field a
                    // downstream consumer's fingerprint depends on.
                    + $"defense={x.Defense.ToString("R", CultureInfo.InvariantCulture)}:"
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

        // The canonical signature of ONE facility-shaped economic opportunity: the site's
        // own income physics plus WHICH actors can serve it and in what state. Deliberately does
        // NOT include travel costs or turns-to-income: those shift on every step of any candidate
        // builder and would turn this into the blanket recompute the architecture forbids, while
        // an actor actually moving already publishes its own Actor invalidation. What IS included
        // is exactly what can flip a site between actionable and not: yield, collection already
        // taken, marginal gain, and the candidate set with its commitment/on-target/extraction
        // state (a site leased to a committed builder vs. one whose builder was released).
        internal static string EconomyOpportunitySignature(EconomyExtractionOpportunity site)
        {
            string actors = string.Join(",", (site.BuilderRoutes
                    ?? System.Array.Empty<EconomyBuilderRouteSnapshot>())
                .Select(r => $"{r.ArmyId}"
                    + $"{(r.HasActiveEconomyCommitment ? "c" : "")}"
                    + $"{(r.IsOnTarget ? "t" : "")}"
                    + $"{(r.RequiresGarrisonExtraction ? "g" : "")}")
                .OrderBy(x => x, System.StringComparer.Ordinal));
            return $"{(int)site.ResourceType}:{site.EffectiveYield}:"
                + $"{site.CurrentBuildingCollection}:{site.MarginalIncomeGain}:[{actors}]";
        }

        // The mobile-collection counterpart. Facility and MobileCollection stay separate lists
        // with separate physics (see EconomyStanding's own comment) — this only mirrors the shape,
        // it never merges them. Travel/ETA excluded for the same reason as above.
        internal static string MobileCollectionSignature(MobileCollectionOpportunity op) =>
            $"{(int)op.ResourceType}:{op.EffectiveRemainingYield}:{op.CollectorArmyId}";

        // Base uses the same stable candidate/actor facts as extraction. No per-step travel cost:
        // a travelling builder already produces Actor invalidation; moving an unrelated scout
        // must not change the signature of every eligible base site.
        private static string BaseOpportunitySignature(EconomyBaseOpportunity site)
        {
            string actors = string.Join(",", (site.BuilderRoutes
                    ?? System.Array.Empty<EconomyBuilderRouteSnapshot>())
                .Select(r => $"{r.ArmyId}"
                    + $"{(r.HasActiveEconomyCommitment ? "c" : "")}"
                    + $"{(r.IsOnTarget ? "t" : "")}"
                    + $"{(r.RequiresGarrisonExtraction ? "g" : "")}")
                .OrderBy(x => x, System.StringComparer.Ordinal));
            return $"{site.HexYield.Human.ToString("R", CultureInfo.InvariantCulture)}:"
                + $"{site.HexYield.Energy.ToString("R", CultureInfo.InvariantCulture)}:"
                + $"{site.HexYield.Materials.ToString("R", CultureInfo.InvariantCulture)}:"
                + $"{site.HexYield.Tech.ToString("R", CultureInfo.InvariantCulture)}:"
                + $"{site.NewResourceClusterHexes}:{site.ConvertsOwnedExtractionSite}:"
                + $"{site.ForwardProgressValue.ToString("R", CultureInfo.InvariantCulture)}:"
                + $"{site.CorridorAlignmentValue.ToString("R", CultureInfo.InvariantCulture)}:"
                + $"{site.DefenseBonusValue.ToString("R", CultureInfo.InvariantCulture)}:[{actors}]";
        }

        // Every economic opportunity row this snapshot carries, keyed so a row appearing,
        // disappearing or changing is all one comparison. One producer for both the typed
        // invalidation below and the Economy admission fingerprint, so an event can never be
        // published against facts the fingerprint does not also carry (which would raise the
        // trigger and then suppress the re-admission on an unchanged key).
        internal static Dictionary<string, string> EconomyOpportunityRows(WorldSnapshot snapshot)
        {
            var rows = new Dictionary<string, string>();
            EconomyStanding eco = snapshot?.Economy;
            if (eco == null)
                return rows;
            foreach (EconomyExtractionOpportunity x in eco.ExtractionOpportunities
                         ?? System.Array.Empty<EconomyExtractionOpportunity>())
                rows[$"ext|{x.Hex.Q},{x.Hex.R}|{(int)x.ResourceType}"] = EconomyOpportunitySignature(x);
            foreach (EconomyExtractionOpportunity x in eco.CollectorSites
                         ?? System.Array.Empty<EconomyExtractionOpportunity>())
                rows[$"col|{x.Hex.Q},{x.Hex.R}|{(int)x.ResourceType}"] = EconomyOpportunitySignature(x);
            foreach (MobileCollectionOpportunity x in eco.MobileCollectionOpportunities
                         ?? System.Array.Empty<MobileCollectionOpportunity>())
                rows[$"mob|{x.TargetHex.Q},{x.TargetHex.R}|{(int)x.ResourceType}|{x.CollectorArmyId}"] =
                    MobileCollectionSignature(x);

            // Base sites are structural facts even without a card, but they are not actionable
            // until a real Base card is in hand (BuildEconomy.HasActionableOpportunity). If no
            // Base card exists, a hand mutation will re-admit Economy when one arrives; emitting
            // ResourceSite on every newly visited empty hex before then would waste rescans.
            bool baseCardInHand = snapshot?.Self?.Hand?.Any(c =>
                c?.Definition?.cardType == CardType.Base) == true;
            if (baseCardInHand)
                foreach (EconomyBaseOpportunity x in eco.BaseOpportunities
                             ?? System.Array.Empty<EconomyBaseOpportunity>())
                    rows[$"base|{x.Hex.Q},{x.Hex.R}"] = BaseOpportunitySignature(x);
            return rows;
        }

        // The hexes whose economic opportunity genuinely changed between two observations —
        // appeared, disappeared, or changed signature.
        private static HashSet<HexCoord> ChangedEconomicOpportunitySites(
            WorldSnapshot before, WorldSnapshot after)
        {
            var result = new HashSet<HexCoord>();
            Dictionary<string, string> old = EconomyOpportunityRows(before);
            Dictionary<string, string> current = EconomyOpportunityRows(after);
            foreach (KeyValuePair<string, string> kv in current)
                if (!old.TryGetValue(kv.Key, out string prior) || !string.Equals(prior, kv.Value,
                        System.StringComparison.Ordinal))
                    AddRowHex(result, kv.Key);
            foreach (string key in old.Keys)
                if (!current.ContainsKey(key))
                    AddRowHex(result, key);
            return result;
        }

        // "<tag>|<q>,<r>|..." — the hex is always the second segment.
        private static void AddRowHex(HashSet<HexCoord> into, string rowKey)
        {
            string[] parts = rowKey.Split('|');
            if (parts.Length < 2)
                return;
            string[] qr = parts[1].Split(',');
            if (qr.Length != 2
                || !int.TryParse(qr[0], System.Globalization.NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int q)
                || !int.TryParse(qr[1], System.Globalization.NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int r))
                return;
            into.Add(new HexCoord(q, r));
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
                + $"| pField={F(self.FieldPotential)} fist={F(self.FistPower)} bestStack={F(self.BestStackPotential)} "
                + $"pDeck={F(self.TotalMilitaryPotential)} pStart={F(self.StartPotential)} "
                + $"reserve units={F(self.Reserve.Units)} hero={F(self.Reserve.Hero)} "
                + $"equip={F(self.Reserve.Equipment)} air={F(self.Reserve.Aviation)} "
                + $"| AP={self.ActionPoints} hand={self.Hand.Count}/{self.HandCapacity} deck={self.Deck.Count} "
                + $"| dev fac={(self.HasDevFacility ? 1 : 0)} op={(self.HasDevOperator ? 1 : 0)}");
            if (s.Development != null)
            {
                DevelopmentReadiness rd = s.Development;
                AiDebugLog.Write($"[AI][V2]   dev.readiness facilities={rd.Facilities.Count} "
                    + $"withHero={(rd.AnyFacilityWithHero ? 1 : 0)} offerings={rd.Offerings.Count} "
                    + $"bestP={P(rd.BestSuccessChance)} surplus={P(rd.SurplusFraction)} "
                    + $"investSurplus={P(rd.InvestmentSurplus)} targets={rd.UpgradeTargetCount}"
                    + $"{(rd.Facilities.Any(f => f.Contested) ? " [contested]" : "")}");
            }
            AiDebugLog.Write($"[AI][V2]   self.stock H/E/M/T={F(self.Stockpile.Human)}/{F(self.Stockpile.Energy)}/"
                + $"{F(self.Stockpile.Materials)}/{F(self.Stockpile.Tech)} "
                + $"| income={F(self.PerTurnIncome.Human)}/{F(self.PerTurnIncome.Energy)}/"
                + $"{F(self.PerTurnIncome.Materials)}/{F(self.PerTurnIncome.Tech)}");
            AiDebugLog.Write($"[AI][V2]   economy.security={P(eco.EconomicSecurity)} "
                + $"(absFloor={P(eco.AbsFloor)} rel={F(eco.RelativePressure)} bottleneck={P(eco.BottleneckPressure)}) "
                + $"| deckNeed H/E/M/T={F(eco.DeckResourceNeed.Human)}/{F(eco.DeckResourceNeed.Energy)}/"
                + $"{F(eco.DeckResourceNeed.Materials)}/{F(eco.DeckResourceNeed.Tech)} "
                + $"| targetIncome H/E/M/T={F(eco.IncomeTarget.Human)}/{F(eco.IncomeTarget.Energy)}/"
                + $"{F(eco.IncomeTarget.Materials)}/{F(eco.IncomeTarget.Tech)} total={F(eco.IncomeTarget.Sum)} "
                + $"actualIncome={F(self.PerTurnIncome.Sum)}");
            int honest = th.Contacts.Count(c => c.Source == ContactSource.Honest);
            int cheat = th.Contacts.Count - honest;
            AiDebugLog.Write($"[AI][V2]   threat: contacts {th.Contacts.Count} (honest={honest} cheat={cheat}) "
                + $"assets {th.Assets.Count} listedThreats {th.Threats.Count} siege={(th.UnderSiege ? 1 : 0)}");
        }

        private static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        private static string P(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    }
}
