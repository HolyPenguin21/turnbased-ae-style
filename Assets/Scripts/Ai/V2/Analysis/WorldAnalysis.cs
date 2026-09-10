using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  WORLD ANALYSIS  (Strategy V2 build-order step 2, 2026-08-29)
    // ===========================================================================================
    //  Builds the one shared WorldSnapshot at the top of Pipeline.RunTurn. Everything downstream
    //  reads that object and never touches raw game state again.
    //
    //  PORTED FROM V1 (adapted, not rewritten):
    //    - AiStrategyDirector.Evaluate's "shared readings" block   -> BuildSelf / BuildKnown / BuildMapKnowledge
    //    - IncomeProjection.IncomeFor / TotalIncome                    -> BuildSelf.PerTurnIncome / BuildEconomy
    //    - AiDefencePlanner.CheatEstimateRaiderThreat (its SCOPE)  -> BuildThreat cheat-contact loop
    //      (the private method itself is left untouched in V1; V2 re-derives the same scan from
    //       TrueWorld.EnemyArmies using the SAME AiConfig radii/shape constants so the two can't
    //       silently diverge on the numbers)
    //    - AiDefencePlanner.DynamicPatrolUrgencyScore             -> NOT ported. Its job (a Patrol
    //      urgency score) is replaced by continuous AssetThreatSnapshot.Severity; Patrol/Intercept
    //      mission value is MissionLayer's problem, from expected Severity reduction.
    //    - AiDefencePlanner.IsUnderSiege                          -> OR'd into ThreatModel.UnderSiege
    //
    //  CHEAT BOUNDARY: cheat data lives only in TrueWorld and in Cheat-sourced EnemyContactSnapshots.
    //  A Cheat contact is structurally forbidden a Position (see MakeCheatContact) — spec-18 as a
    //  type invariant.
    // ===========================================================================================
    public static class WorldAnalysis
    {
        private const int NoHeroStackCapacity = 2;

        public static WorldSnapshot Scan(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var snap = new WorldSnapshot { TurnNumber = ctx.TurnNumber };
            snap.Self = BuildSelf(player, root, hand, ctx);
            snap.Development = BuildDevelopment(player, root, hand, ctx);
            snap.Known = BuildKnown(player, snap.Self.BaseHexes);
            AiReconMemory.Observe(player, ctx.TurnNumber, snap.Known.EnemySightings);
            snap.TrueWorld = BuildTrueWorld(player, ctx);
            snap.MapKnowledge = BuildMapKnowledge(player, ctx, snap);
            snap.Economy = BuildEconomy(player, root, ctx, snap);
            snap.Threat = BuildThreat(player, ctx, snap);
            LogSnapshot(player, snap);
            return snap;
        }

        public static WorldSnapshot RefreshOperationalState(WorldSnapshot prev, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (prev == null)
                return Scan(player, root, hand, ctx);

            var snap = new WorldSnapshot
            {
                TurnNumber = prev.TurnNumber,
                Known = prev.Known,
                TrueWorld = prev.TrueWorld,
                MapKnowledge = prev.MapKnowledge,
            };
            snap.Self = BuildSelf(player, root, hand, ctx);
            snap.Development = BuildDevelopment(player, root, hand, ctx);
            snap.Economy = BuildEconomy(player, root, ctx, snap);
            snap.Threat = BuildThreat(player, ctx, snap);

            SelfSnapshot s = snap.Self;
            AiDebugLog.WriteVerbose($"[AI][V2] {player?.Nickname} op-refresh — AP {s.ActionPoints} "
                + $"hand {s.Hand.Count}/{s.HandCapacity} armies {s.Armies.Count} "
                + $"field {F(s.FieldPower)} garrison {F(s.GarrisonPower)} "
                + $"bestStack {F(s.BestStackPotential)} threats {snap.Threat?.Threats?.Count ?? 0}");
            return snap;
        }

        public static WorldSnapshot RefreshStrategicKnowledge(WorldSnapshot prev, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (prev == null)
                return Scan(player, root, hand, ctx);

            var snap = new WorldSnapshot { TurnNumber = prev.TurnNumber };
            snap.Self = BuildSelf(player, root, hand, ctx);
            snap.Development = BuildDevelopment(player, root, hand, ctx);
            snap.Known = BuildKnown(player, snap.Self.BaseHexes);
            AiReconMemory.Observe(player, ctx.TurnNumber, snap.Known.EnemySightings);
            snap.TrueWorld = BuildTrueWorld(player, ctx);
            snap.MapKnowledge = BuildMapKnowledge(player, ctx, snap);
            snap.Economy = BuildEconomy(player, root, ctx, snap);
            snap.Threat = BuildThreat(player, ctx, snap);

            AiDebugLog.WriteVerbose($"[AI][V2] {player?.Nickname} knowledge-refresh — "
                + $"enemyKnown {snap.Known.EnemySightings.Count} neutralKnown {snap.Known.NeutralSightings.Count} "
                + $"visited {snap.MapKnowledge.VisitedHexes}/{snap.MapKnowledge.TotalHexes} "
                + $"frontier {snap.MapKnowledge.Frontier.Count} threats {snap.Threat.Threats.Count}");
            return snap;
        }


        // Settled observation boundary for one bounded task. Analysis owns factual comparison;
        // Orchestration only decides which typed task family consumes the published invalidation.
        internal sealed class StepObservationStamp
        {
            internal readonly WorldSnapshot Snapshot;
            internal readonly V2ResourceStamp Resources;
            internal readonly AiHandData Hand;
            internal readonly int HandVersion;

            internal StepObservationStamp(WorldSnapshot snapshot, V2ResourceStamp resources,
                AiHandData hand)
            {
                Snapshot = snapshot;
                Resources = resources;
                Hand = hand;
                HandVersion = hand?.MutationVersion ?? -1;
            }
        }

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
                    + $"bestP={P(rd.BestSuccessChance)} surplus={P(rd.SurplusFraction)} targets={rd.UpgradeTargetCount}"
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

        private static SelfSnapshot BuildSelf(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var self = new SelfSnapshot();

            List<ArmyData> ownArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && !a.IsPrison)
                .ToList();

            var baseHexes = ownArmies
                .Where(a => a.IsGarrison)
                .Select(a => a.Hex)
                .Distinct()
                .ToList();

            HexCoord citadel = player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue
                ? new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value)
                : (baseHexes.Count > 0 ? baseHexes[0] : default);
            if (baseHexes.Count == 0)
                baseHexes.Add(citadel);

            self.Citadel = citadel;
            self.BaseHexes = baseHexes;
            self.Armies = ownArmies.Select(a => ToArmySnapshot(a, player, isOwn: true, ArmyVisionRadius(ctx))).ToList();

            self.FieldPower = self.Armies.Where(a => !a.IsGarrison).Sum(a => a.EffectiveArmyPower);
            self.GarrisonPower = self.Armies.Where(a => a.IsGarrison).Sum(a => a.EffectiveArmyPower);
            self.TotalPower = self.FieldPower + self.GarrisonPower;

            foreach (ResourceType t in ResourceBundle.All)
            {
                self.Stockpile.Add(t, root != null ? root.GetResource(t) : 0);
                self.PerTurnIncome.Add(t, IncomeProjection.IncomeFor(player, t, ctx.Map));
            }
            self.ActionPoints = root != null ? root.ActionPoints : 0;

            self.Hand = hand?.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>();
            self.Deck = hand?.RemainingDeck ?? (IReadOnlyList<CardDefinition>)System.Array.Empty<CardDefinition>();
            self.HandCapacity = hand?.Capacity ?? 0;
            self.HasFreeHandSlot = hand?.HasFreeSlot ?? false;

            ReconAirObservationCapacity airObs = ReconAirCapacityPolicy.Evaluate(player, root);
            self.AirborneReconWings = airObs.AirborneReconWings;
            self.SpareAirObservationSorties = airObs.SpareSorties;

            self.HasDevFacility = BuildingRegistry.AllBuildings().Any(b => b != null && b.Owner == player
                && (b.HasFacilityWithAbility(UnitAbilities.Research) || b.HasFacilityWithAbility(UnitAbilities.Production)));
            self.HasDevOperator = ownArmies
                .SelectMany(a => a.Members)
                .Any(m => m != null && m.IsHero
                    && (m.HasAbility(UnitAbilities.Researcher) || m.HasAbility(UnitAbilities.Assembler)));

            BuildApActionEconomy(self, player, ownArmies);

            var nowPool = new List<AiPower.PowerUnit>();
            int nowCap = NoHeroStackCapacity;
            foreach (ArmyData a in ownArmies)
                foreach (UnitData m in a.Members)
                {
                    nowPool.Add(AiPower.ToPowerUnit(m));
                    if (m.IsHero && m.CommandRating > nowCap) nowCap = m.CommandRating;
                }
            foreach (CardData c in self.Hand)
                if (c?.Definition != null && IsMilitaryCard(c.Definition))
                {
                    nowPool.Add(AiPower.ToPowerUnit(c.Definition));
                    if (c.Definition.cardType == CardType.Hero && c.Definition.commandRating > nowCap)
                        nowCap = c.Definition.commandRating;
                }

            var ceilingPool = new List<AiPower.PowerUnit>(nowPool);
            int ceilingCap = nowCap;
            foreach (CardDefinition d in self.Deck)
                if (d != null && IsMilitaryCard(d))
                {
                    ceilingPool.Add(AiPower.ToPowerUnit(d));
                    if (d.cardType == CardType.Hero && d.commandRating > ceilingCap)
                        ceilingCap = d.commandRating;
                }

            self.BestStackPotential = AiPower.BestStackPotential(nowPool, nowCap);
            self.TotalMilitaryPotential = AiPower.TotalMilitaryPotential(ceilingPool, ceilingCap);

            return self;
        }

        private static bool IsMilitaryCard(CardDefinition d) =>
            d.cardType == CardType.Unit || d.cardType == CardType.Hero;

        private static readonly ResearchProductionMode[] DevModes =
            { ResearchProductionMode.Research, ResearchProductionMode.Production };

        // The ONE Research/Production capability detect. Snapshot-pure: enumerates own facilities
        // (+ the qualifying hero), then every catalog card that passes facility ability + hero +
        // CanAffordCard. Success chance remains a soft EV/ranking input. The enemy-on-hex rule is recorded
        // as DevelopmentFacility.Contested but is NOT applied here — a contested facility still
        // produces offerings for the analyzer/radar; Phase A alone skips execution while contested.
        private static DevelopmentReadiness BuildDevelopment(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
        {
            var rd = new DevelopmentReadiness();
            var facilities = new List<DevelopmentFacility>();
            var offerings = new List<DevelopmentOffering>();
            rd.Facilities = facilities;
            rd.Offerings = offerings;
            if (player == null || root == null)
                return rd;

            int targets = 0;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
            {
                if (a == null || a.IsPrison) continue;
                foreach (UnitData m in a.Members)
                    if (m != null && !m.IsHero) targets++;
            }
            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                    if (c?.Definition != null && c.Definition.cardType == CardType.Unit) targets++;
            rd.UpgradeTargetCount = targets;

            ResearchProductionCatalog catalog = ctx?.ResearchProductionCatalog;

            foreach (BuildingData b in BuildingRegistry.AllBuildings())
            {
                if (b == null || b.Owner != player) continue;
                foreach (ResearchProductionMode mode in DevModes)
                {
                    if (!b.HasFacilityWithAbility(ResearchProductionSystem.FacilityAbility(mode)))
                        continue;
                    UnitData hero = ResearchProductionSystem.FindActor(player, b.Hex, mode);
                    facilities.Add(new DevelopmentFacility
                    {
                        Hex = b.Hex,
                        Mode = mode,
                        HasHero = hero != null,
                        Contested = BattleInitiator.FindEnemyAt(b.Hex, player) != null,
                        HeroFate = hero != null ? Mathf.Max(0, hero.Fate) : 0,
                        HeroCommandRating = hero != null ? Mathf.Max(0, hero.CommandRating) : 0,
                    });
                    // Offering enumeration is owned by GenerationSource below. This loop
                    // records readiness only, so Analysis does not keep a second copy of source
                    // eligibility/card/operator logic.
                }
            }

            if (catalog != null)
                foreach (GenerationStep g in GenerationSource.Enumerate(
                    player, root, ctx, hand, null, null, includeContested: true))
                {
                    var stake = new ResourceBundle();
                    ResourceCost cost = g.CardDef?.resourceCost;
                    if (cost != null)
                        foreach (ResourceType t in ResourceBundle.All)
                            stake.Add(t, cost.Get(t));
                    offerings.Add(new DevelopmentOffering
                    {
                        FacilityHex = g.FacilityHex,
                        Mode = g.Mode,
                        Card = g.CardDef,
                        SuccessChance = g.SuccessChance,
                        ProducesEquipment = g.ProducesEquipment,
                        StakeCost = stake,
                        Generation = g,
                    });
                }

            bool facilityCardInHand = false, researcherCardInHand = false, assemblerCardInHand = false;
            if (hand?.Hand != null)
                foreach (CardData c in hand.Hand)
                {
                    CardDefinition d = c?.Definition;
                    if (d == null || d.grantedAbilities == null)
                        continue;
                    if (d.cardType == CardType.Facility
                        && (d.grantedAbilities.Contains(UnitAbilities.Research)
                            || d.grantedAbilities.Contains(UnitAbilities.Production)))
                        facilityCardInHand = true;
                    if (d.cardType == CardType.Hero)
                    {
                        if (d.grantedAbilities.Contains(UnitAbilities.Researcher)) researcherCardInHand = true;
                        if (d.grantedAbilities.Contains(UnitAbilities.Assembler)) assemblerCardInHand = true;
                    }
                }

            rd.AnyFacilityWithHero = facilities.Any(f => f.HasHero);
            rd.AnyOperatorlessFacility = facilities.Any(f => !f.HasHero && !f.Contested);
            rd.ResearcherCardInHand = researcherCardInHand;
            rd.AssemblerCardInHand = assemblerCardInHand;
            rd.DevPathViable = rd.AnyFacilityWithHero || facilities.Count > 0 || facilityCardInHand;
            rd.BestSuccessChance = offerings.Count > 0 ? offerings.Max(o => o.SuccessChance) : 0f;
            rd.SurplusFraction = SurplusFraction(player, root, ctx);
            return rd;
        }

        // [0..1] proxy for "am I spending surplus, not resources I need". Per resource type:
        // spendable(t) (the tighter of the legacy + strategic reservation floors) over two turns of
        // income; the WORST type governs. First pass — the analyzer's A_total (opportunity cost vs
        // playing a card) is the real gate; this is the radar's coarse appetite signal. Tune later.
        private static float SurplusFraction(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            if (player == null || root == null)
                return 0f;
            float worst = 1f;
            foreach (ResourceType t in ResourceBundle.All)
            {
                float legacy = AiResourceReservation.Available(root, player, t);
                float strategic = ctx != null
                    ? StrategicResourceReservationLedger.Spendable(player, ctx.TurnNumber,
                        StrategicResourceReservationLedger.Map(t), root.GetResource(t))
                    : float.MaxValue;
                float spendable = Mathf.Max(0f, Mathf.Min(legacy, strategic));
                float income = Mathf.Max(1f, IncomeProjection.IncomeFor(player, t, ctx?.Map));
                worst = Mathf.Min(worst, Mathf.Clamp01(spendable / (income * 2f)));
            }
            return worst;
        }

        // AI-MGR — Dynamic Strategic Effect Utility. Snapshot-pure AP action-economy read: how many
        // recurring-AP sources are in play, how much AP the AI could still usefully spend this turn,
        // and from those the marginal value of one more AP/turn. ACTION economy only — never a
        // H/E/M/T security read. Also counts the non-hero bodies a hero's Command could realistically
        // put to use.
        private static void BuildApActionEconomy(SelfSnapshot self, PlayerSetupData player,
            List<ArmyData> ownArmies)
        {
            int recurringApSources = 0;
            foreach (ArmyData a in ownArmies)
            {
                if (a.IsPrison) continue;
                foreach (UnitData m in a.Members)
                    if (m != null && m.HasAbility(UnitAbilities.ApBonus))
                        recurringApSources++;
            }
            foreach (BuildingData b in BuildingRegistry.AllBuildings())
            {
                if (b == null || b.Owner != player) continue;
                if (b.HasAbility(UnitAbilities.ApBonus))
                    recurringApSources++;
                if (b.FacilitySlots != null)
                    foreach (FacilityData f in b.FacilitySlots)
                        if (f != null && f.HasAbility(UnitAbilities.ApBonus))
                            recurringApSources++;
            }

            int unactivatedArmies = 0;
            float armyApDemand = 0f;
            int nonHeroBodies = 0;
            foreach (ArmyData a in ownArmies)
            {
                foreach (UnitData m in a.Members)
                    if (m != null && !m.IsHero) nonHeroBodies++;
                if (a.IsGarrison || a.IsPrison || a.IsAirArmy || a.Members.Count == 0) continue;
                if (a.HasActivatedThisTurn) continue;
                unactivatedArmies++;
                armyApDemand += Mathf.Max(1, a.ActivationApCost);
            }

            int apCards = 0;
            float cardApDemand = 0f;
            foreach (CardData c in self.Hand)
            {
                float ap = c != null ? c.EffectivePlayApCost : 0f;
                if (ap > 0f) { apCards++; cardApDemand += ap; }
                if (c?.Definition != null && c.Definition.cardType == CardType.Unit) nonHeroBodies++;
            }

            float devDemand = self.HasDevFacility && self.HasDevOperator ? AiConfigV2.apDevActionApProxy : 0f;
            float airDemand = (self.AirborneReconWings + self.SpareAirObservationSorties) * AiConfigV2.apAirSortieApProxy;

            // STRUCTURAL FACTS ONLY — an upper bound. WorldAnalysis does not decide which of these
            // are useful/legal this turn; the owner-witnessed AP workload is assembled at evaluation
            // time in StrategicManager Phase A/B. These feed only the discounted structural fallback
            // (StrategicEffectRegistry.StructuralFallbackApDemand). No ramp here.
            self.ApEconomy = new ApActionEconomySnapshot
            {
                BaseActionPoints = self.ActionPoints,
                RecurringApSources = recurringApSources,
                RecurringApPerTurn = recurringApSources * UnitAbilities.ApBonusActionPointsPerSource,
                UnactivatedActionableArmies = unactivatedArmies,
                ApCostingHandActions = apCards,
                EstimatedArmyApDemand = armyApDemand,
                EstimatedCardApDemand = cardApDemand,
                EstimatedDevelopmentApDemand = devDemand,
                EstimatedAirApDemand = airDemand,
            };
            self.DeployableCombatBodies = nonHeroBodies;
        }

        private static ArmySnapshot ToArmySnapshot(ArmyData a, PlayerSetupData viewer, bool isOwn, int armyVisionRadius)
        {
            var nonHero = a.Members.Where(m => !m.IsHero).ToList();
            bool allHidden = !isOwn && a.Members.Count > 0
                && a.Members.All(m => StealthSystem.IsHiddenFrom(m, viewer));

            return new ArmySnapshot
            {
                ArmyId = a.Id,
                Owner = a.Owner,
                Hex = a.Hex,
                IsGarrison = a.IsGarrison,
                IsPrison = a.IsPrison,
                IsAir = a.IsAirArmy,
                IsAirfield = a.IsAirfield,
                MemberCount = a.Members.Count,
                HasHero = a.Members.Any(m => m.IsHero),
                HeroCommandRating = a.Members.Where(m => m.IsHero).Select(m => m.CommandRating).DefaultIfEmpty(0).Max(),
                HasAntiAir = a.Members.Any(m => m.HasAbility(UnitAbilities.AntiAir)),
                // review-r4 P1 ARCH — the coverage roles come from StrategicEffectRegistry, so a new
                // counter/support/mobility mechanic flows in without editing this file.
                StrategicCoverage = StrategicCoverageOf(a),
                // final closure §3.3 — own-army ally auras, so the effect context can price the
                // marginal buff a standing aura gives an incoming candidate. Empty until an aura row
                // exists in the registry.
                AllyAuraEffects = isOwn ? AllyAuraEffectsOf(a) : System.Array.Empty<StrategicEffect>(),
                IsHiddenFromUs = allHidden,
                AttackSum = WorthIt.AttackSum(a),
                DefenseSum = WorthIt.DefenseSum(a),
                EffectiveArmyPower = AiPower.EffectiveArmyPower(a.Members),
                CompositionQuality = AiPower.CompositionQualityOf(a.Members),
                MaxMovement = a.MaxMovement,
                Capacity = a.Capacity,
                OccupiedBattleSlots = a.Members.Count,
                Members = nonHero.Select(WorthIt.FromLiveUnit).ToList(),
                MembersWithHeroes = a.Members.Select(WorthIt.FromLiveUnit).ToList(),
                ActivationApCost = a.ActivationApCost,
                ActivationEnergyCost = a.ActivationEnergyCost,
                HasActivatedThisTurn = a.HasActivatedThisTurn,
                CurrentMovement = a.CurrentMovement,
                IsSoloRecce = isOwn && AiArmyRoles.IsSoloRecce(a),
                IsMobileEconomyBuilder = isOwn && AiArmyRoles.IsHeroLed(a),
                IsStructuralRaidActor = isOwn
                    && !a.IsPrison && !a.IsGarrison && !a.IsAirArmy && !a.IsAirfield
                    && !AiArmyRoles.IsSoloRecce(a) && !AiArmyRoles.IsSoloHeroAwaitingEscort(a)
                    && a.Members.Count > 0,
                IsHidden = isOwn && a.Members.Count > 0 && a.Members.All(m => m.IsHidden),
                CanEnterStealth = isOwn && a.Members.Any(StealthSystem.CanEnterStealth),
                StealthLevel = isOwn
                    ? a.Members.Select(AbilityParams.GetStealthLevel).DefaultIfEmpty(0).Max()
                    : 0,
                EffectiveVisionRadius = armyVisionRadius + AbilityParams.GetBestRecceRadius(a),
                CollectionCapacity = isOwn ? CollectionCapacityOf(a) : default(ResourceBundle),
            };
        }

        private static ResourceBundle CollectionCapacityOf(ArmyData army)
        {
            var result = new ResourceBundle();
            if (army?.Members == null)
                return result;
            foreach (ResourceType type in ResourceBundle.All)
                result.Add(type, army.Members.Count(member => member != null
                    && member.HasAbility(UnitAbilities.CollectAbilityFor(type))));
            return result;
        }

        // review-r4 P1 ARCH — union of every member's registry-resolved coverage roles (abilities +
        // effective moveMax). One place, no per-role branch.
        private static RoleCoverage StrategicCoverageOf(ArmyData a)
        {
            RoleCoverage c = RoleCoverage.None;
            if (a?.Members != null)
                foreach (UnitData m in a.Members)
                    if (m != null)
                        c = c.Union(StrategicEffectRegistry.CoverageOf(m.Abilities, m.MoveMax));
            return c;
        }

        // final closure §3.3 — the ally-aura effects standing in `a` (members' registry-resolved
        // effects whose context is EligibleAllies). One place, no per-ability branch. Empty until an
        // aura row is added to StrategicEffectRegistry.ByAbility.
        private static IReadOnlyList<StrategicEffect> AllyAuraEffectsOf(ArmyData a)
        {
            List<StrategicEffect> list = null;
            if (a?.Members != null)
                foreach (UnitData m in a.Members)
                {
                    if (m == null) continue;
                    foreach (StrategicEffect e in StrategicEffectRegistry.Resolve(m.Abilities, m.MoveMax))
                        if (e.Context == StrategicEffectContext.EligibleAllies)
                            (list ??= new List<StrategicEffect>()).Add(e);
                }
            return list ?? (IReadOnlyList<StrategicEffect>)System.Array.Empty<StrategicEffect>();
        }

        private static int ArmyVisionRadius(AiTurnContext ctx) =>
            ctx != null && ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0;

        private static KnownSnapshot BuildKnown(PlayerSetupData player, IReadOnlyList<HexCoord> baseHexes)
        {
            var known = new KnownSnapshot
            {
                EnemySightings = AiMapMemory.AllKnownEnemySightings(player).ToList(),
                NeutralSightings = AiMapMemory.AllKnownNeutralSightings(player).ToList(),
                Buildings = AiMapMemory.AllKnownBuildings(player).ToList(),
                EventGuardHexes = AiMapMemory.KnownEventGuardHexes(player).ToList(),
                ResourceHexes = AiMapMemory.AllKnownResourceHexes(player).ToList(),
            };

            known.EnemyKnownStrength = known.EnemySightings.Sum(s => s.DefenseSum + s.AttackSum);

            int nearest = int.MaxValue;
            float nearBases = 0f;
            foreach (AiMapMemory.KnownEnemySighting s in known.EnemySightings)
            {
                int d = baseHexes.Min(b => HexGridMath.Distance(b, s.Hex));
                if (d < nearest) nearest = d;
                if (d <= AiConfig.raidThreatRadius + 2)
                    nearBases += s.DefenseSum + s.AttackSum;
            }
            known.NearestEnemyToBase = nearest == int.MaxValue ? 99 : nearest;
            known.EnemyStrengthNearBases = nearBases;

            return known;
        }

        private static TrueWorldSnapshot BuildTrueWorld(PlayerSetupData player, AiTurnContext ctx)
        {
            var tw = new TrueWorldSnapshot();
            var enemyArmies = new List<ArmySnapshot>();
            var neutralArmies = new List<ArmySnapshot>();
            var opponents = new List<OpponentSnapshot>();

            foreach (PlayerSetupData p in GameSession.Players ?? new List<PlayerSetupData>())
            {
                if (p == null || p == player) continue;

                List<ArmyData> armies = ArmyRegistry.AllForOwner(p)
                    .Where(a => a != null && !a.IsPrison && a.Members.Count > 0)
                    .ToList();
                var snaps = armies.Select(a => ToArmySnapshot(a, player, isOwn: false, ArmyVisionRadius(ctx))).ToList();

                if (p.IsNeutral)
                {
                    neutralArmies.AddRange(snaps);
                    continue;
                }

                enemyArmies.AddRange(snaps);

                if (!p.IsEliminated)
                {
                    PlayerRoot pr = PlayerRootRegistry.FindFor(p);
                    var opp = new OpponentSnapshot
                    {
                        Player = p,
                        ArmyCount = snaps.Count,
                        ArmyPower = snaps.Sum(s => s.EffectiveArmyPower),
                    };
                    foreach (ResourceType t in ResourceBundle.All)
                    {
                        opp.PerTurnIncome.Add(t, IncomeProjection.IncomeFor(p, t, ctx.Map));
                        opp.Stockpile.Add(t, pr != null ? pr.GetResource(t) : 0);
                    }
                    opponents.Add(opp);
                }
            }

            tw.EnemyArmies = enemyArmies;
            tw.NeutralArmies = neutralArmies;
            tw.Opponents = opponents;
            tw.AllBuildings = BuildingRegistry.AllBuildings()
                .Where(b => b != null)
                .Select(ToBuildingSnapshot)
                .ToList();
            return tw;
        }

        private static BuildingSnapshot ToBuildingSnapshot(BuildingData b)
        {
            var abilities = new HashSet<string>();
            foreach (FacilityData f in b.FacilitySlots)
                if (f != null)
                    abilities.UnionWith(f.Abilities);
            return new BuildingSnapshot
            {
                Hex = b.Hex,
                Owner = b.Owner,
                IsStartingCitadel = b.IsStartingCitadel,
                Defense = b.Defense,
                FacilityAbilities = abilities,
            };
        }

        private static MapKnowledgeSnapshot BuildMapKnowledge(PlayerSetupData player, AiTurnContext ctx, WorldSnapshot snap)
        {
            HexMap map = ctx.Map;
            var all = new List<HexCoord>();
            var visitedSet = new HashSet<HexCoord>();
            var everSeenSet = new HashSet<HexCoord>();
            int visited = 0, visible = 0;
            foreach (HexCoord c in map.AllCoords)
            {
                all.Add(c);
                if (VisionSystem.IsVisited(player, c)) { visited++; visitedSet.Add(c); }
                if (VisionSystem.HasEverSeen(player, c)) everSeenSet.Add(c);
                if (VisionSystem.IsVisible(player, c)) visible++;
            }
            int total = all.Count;

            IReadOnlyList<HexCoord> baseHexes = snap.Self.BaseHexes;
            var neutralHexes = new HashSet<HexCoord>(
                (snap.Known.NeutralSightings ?? new List<AiMapMemory.KnownEnemySighting>()).Select(s => s.Hex));
            List<AiMapMemory.KnownEnemySighting> nonNeutral =
                (snap.Known.EnemySightings ?? new List<AiMapMemory.KnownEnemySighting>()).ToList();
            int exposureR = AiConfigV2.frontierEnemyExposureRadius;

            bool OnMap(HexCoord h) => map.TryGetTerrainAt(h, out _);
            // Spec §19 — neutral occupancy is NOT a universal hard block any more. It is
            // actor-state-aware (a fully-hidden scout passes) and exported separately as
            // NeutralOccupiedHexes. HardBlocked is now only what blocks EVERY scout.
            bool HardBlocked(HexCoord h) =>
                !OnMap(h) || AiMapMemory.IsScoutDangerous(player, h);
            bool NeutralAt(HexCoord h) => neutralHexes.Contains(h);
            bool EnemyExposed(HexCoord h)
            {
                foreach (AiMapMemory.KnownEnemySighting e in nonNeutral)
                    if (HexGridMath.Distance(e.Hex, h) <= exposureR) return true;
                return false;
            }
            int DetectorsAt(HexCoord h)
            {
                int n = 0;
                foreach (AiMapMemory.KnownEnemySighting e in nonNeutral)
                    if (HexGridMath.Distance(e.Hex, h) <= exposureR && e.CanDetectStealthAt(h)) n++;
                return n;
            }
            int NearestBaseDist(HexCoord h) =>
                baseHexes.Count > 0 ? baseHexes.Min(b => HexGridMath.Distance(b, h)) : 0;

            var reachableVisited = new HashSet<HexCoord>();
            var queue = new Queue<HexCoord>();
            foreach (HexCoord b in baseHexes)
                if (OnMap(b) && reachableVisited.Add(b))
                    queue.Enqueue(b);
            while (queue.Count > 0)
            {
                HexCoord cur = queue.Dequeue();
                foreach (HexCoord n in HexGridMath.Neighbors(cur))
                {
                    if (reachableVisited.Contains(n) || HardBlocked(n)) continue;
                    if (!VisionSystem.IsVisited(player, n)) continue;
                    reachableVisited.Add(n);
                    queue.Enqueue(n);
                }
            }

            var raw = new List<FrontierHexSnapshot>();
            foreach (HexCoord c in all)
            {
                // A frontier hex is a place a scout stands on next; keep neutral-occupied hexes out
                // of that set (conservative for waypoint choice) even though the explorable flood
                // below now flows THROUGH them for a hidden scout.
                if (VisionSystem.IsVisited(player, c) || HardBlocked(c) || NeutralAt(c)) continue;
                bool touchesReachable = false;
                int fresh = 0;
                foreach (HexCoord n in HexGridMath.Neighbors(c))
                {
                    if (reachableVisited.Contains(n)) touchesReachable = true;
                    if (!VisionSystem.IsVisited(player, n) && !HardBlocked(n)) fresh++;
                }
                if (!touchesReachable) continue;
                bool exposed = EnemyExposed(c);
                raw.Add(new FrontierHexSnapshot
                {
                    Hex = c,
                    FreshNeighbors = fresh,
                    DistanceFromNearestBase = NearestBaseDist(c),
                    EnemyExposure = exposed,
                    StealthDetectionRisk = exposed && DetectorsAt(c) > 0,
                });
            }

            var frontier = new List<FrontierHexSnapshot>();
            var frontierSet = new HashSet<HexCoord>();
            if (raw.Count > 0)
            {
                int nearestFrontierDist = raw.Min(f => f.DistanceFromNearestBase);
                int bandLimit = nearestFrontierDist + AiConfigV2.frontierWaveBand;
                foreach (FrontierHexSnapshot f in raw)
                {
                    if (f.DistanceFromNearestBase > bandLimit) continue;
                    frontier.Add(f);
                    frontierSet.Add(f.Hex);
                }
            }

            int explorable = 0;
            if (frontierSet.Count > 0)
            {
                var darkSeen = new HashSet<HexCoord>(frontierSet);
                var darkQueue = new Queue<HexCoord>(frontierSet);
                while (darkQueue.Count > 0)
                {
                    HexCoord cur = darkQueue.Dequeue();
                    explorable++;
                    foreach (HexCoord n in HexGridMath.Neighbors(cur))
                    {
                        if (darkSeen.Contains(n) || HardBlocked(n)) continue;
                        if (VisionSystem.IsVisited(player, n)) continue;
                        darkSeen.Add(n);
                        darkQueue.Enqueue(n);
                    }
                }
            }

            return new MapKnowledgeSnapshot
            {
                TotalHexes = total,
                VisitedHexes = visited,
                VisibleHexes = visible,
                UnknownFrac = total > 0 ? 1f - (float)visited / total : 0f,
                Frontier = frontier,
                ExplorableUnknownFrac = total > 0 ? (float)explorable / total : 0f,
                AllHexes = all,
                ScoutHardBlockedHexes = new HashSet<HexCoord>(all.Where(HardBlocked)),
                NeutralOccupiedHexes = new HashSet<HexCoord>(all.Where(NeutralAt)),
                VisitedHexSet = visitedSet,
                EverSeenHexSet = everSeenSet,
            };
        }

        private static EconomyStanding BuildEconomy(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snap)
        {
            var eco = new EconomyStanding();
            var perType = new List<EconomyResourceStanding>();

            List<PlayerSetupData> others = (GameSession.Players ?? new List<PlayerSetupData>())
                .Where(p => p != null && p != player && !p.IsNeutral && !p.IsEliminated)
                .ToList();

            var handNeed = new ResourceBundle();
            var deckNeed = new ResourceBundle();
            AccumulateCardCosts(snap.Self.Hand, ref handNeed);
            AccumulateCardCosts(snap.Self.Deck, ref deckNeed);
            eco.HandResourceNeed = handNeed;
            eco.RemainingDeckResourceNeed = deckNeed;
            var allNeed = new ResourceBundle();
            foreach (ResourceType t in ResourceBundle.All)
                allNeed.Add(t, handNeed.Get(t) + deckNeed.Get(t));
            eco.DeckResourceNeed = allNeed;

            var reservedNeed = new ResourceBundle();
            var spendableStock = new ResourceBundle();
            foreach (ResourceType t in ResourceBundle.All)
            {
                float own = snap.Self.PerTurnIncome.Get(t);
                var otherIncomes = others.Select(p => (float)IncomeProjection.IncomeFor(p, t, ctx.Map)).ToList();
                float median = Median(otherIncomes);
                float reserved = StrategicResourceReservationLedger.Active(
                    player, ctx.TurnNumber, StrategicResourceReservationLedger.Map(t));
                float spendable = StrategicSpendability.SpendableAmount(player, root, ctx, t);
                reservedNeed.Add(t, reserved);
                spendableStock.Add(t, spendable);
                perType.Add(EconomyStanding.CalculateResource(t, own, median,
                    handNeed.Get(t), deckNeed.Get(t), reserved, spendable,
                    ResourceStarvationRegistry.Pressure(player, t)));
            }
            eco.PerType = perType;
            eco.ReservedOperationalNeed = reservedNeed;
            eco.SpendableStockpile = spendableStock;
            var incomeTarget = new ResourceBundle();
            foreach (EconomyResourceStanding rs in perType)
                incomeTarget.Add(rs.Type, rs.IncomeTarget);
            eco.IncomeTarget = incomeTarget;
            EconomyResourceStanding worst = perType
                .OrderByDescending(x => x.DeficitScore).ThenBy(x => x.Type).First();
            eco.MostDeficientResource = worst.Type;
            eco.MaxDeficitScore = worst.DeficitScore;
            eco.MeanDeficitScore = perType.Average(x => x.DeficitScore);
            eco.BottleneckPressure = eco.MaxDeficitScore;
            eco.AbsFloor = perType.Average(x => x.RunwayCoverage);
            eco.RelativePressure = 1f - 2f * perType.Average(x => x.RelativeIncomeGap);
            eco.EconomicSecurity = Mathf.Clamp01(1f - (
                AiConfigV2.economyDesireMaxWeight * eco.MaxDeficitScore
                + AiConfigV2.economyDesireMeanWeight * eco.MeanDeficitScore));

            var standings = perType.ToDictionary(x => x.Type, x => x);
            var knownBuildings = (snap.Known?.Buildings
                ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .GroupBy(x => x.Hex).ToDictionary(g => g.Key, g => g.First());
            var extraction = new List<EconomyExtractionOpportunity>();
            foreach ((HexCoord Hex, ResourceType Type, int Yield) site
                     in KnownExtractionYields(snap))
            {
                ResourceType resourceType = site.Type;
                int effectiveYield = site.Yield;

                int currentCollection = 0;
                if (knownBuildings.TryGetValue(site.Hex, out AiMapMemory.KnownBuilding building))
                {
                    // BuildingPlayExecutor can only add a Facility to our own building, and only
                    // while an unlocked slot was last observed free.
                    if (building.Owner != player || building.FreeFacilitySlots <= 0)
                        continue;
                    currentCollection = building.CollectedAmount(resourceType);
                }

                int ownArmyCollectors = Mathf.RoundToInt((snap.Self.Armies
                    ?? System.Array.Empty<ArmySnapshot>())
                    .Where(a => a != null && a.Hex.Equals(site.Hex))
                    .Sum(a => a.CollectionCapacity.Get(resourceType)));
                bool armiesCanCollect = !(snap.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Any(enemy => enemy.Hex.Equals(site.Hex));
                int marginal = IncomeProjection.MarginalOwnerCollectionAtHex(
                    effectiveYield, currentCollection, 1,
                    ownArmyCollectors, armiesCanCollect);
                if (marginal <= 0)
                    continue;
                extraction.Add(new EconomyExtractionOpportunity
                {
                    Hex = site.Hex,
                    ResourceType = resourceType,
                    EffectiveYield = effectiveYield,
                    CurrentBuildingCollection = currentCollection,
                    MarginalIncomeGain = marginal,
                    BaseNetworkSynergy = EconomyBaseNetworkSynergy(snap, site.Hex),
                    NearbyResourceClusterValue = EconomyResourceClusterValue(
                        snap, site.Hex, standings),
                    BuilderRoutes = EconomyBuilderRoutes(snap, player, ctx, site.Hex),
                });
            }
            eco.ExtractionOpportunities = extraction;

            // Base opportunities are structural site facts only. Card-specific value/cost remains
            // Strategy/Demand's responsibility, but Analysis owns the one legal candidate set so
            // the desire gate and demand emission cannot disagree.
            var baseOpportunities = new List<EconomyBaseOpportunity>();
            List<CardData> baseCards = (snap.Self.Hand ?? System.Array.Empty<CardData>())
                .Where(c => c?.Definition?.cardType == CardType.Base).ToList();
            if (baseCards.Count > 0 && snap.Self.BaseHexes != null)
            {
                var occupied = knownBuildings;
                var knownSites = new HashSet<HexCoord>((snap.Known?.ResourceHexes
                    ?? System.Array.Empty<AiMapMemory.KnownResourceHex>()).Select(x => x.Hex));
                var knownMapHexes = new HashSet<HexCoord>(
                    snap.MapKnowledge?.EverSeenHexSet
                    ?? snap.MapKnowledge?.VisitedHexSet
                    ?? (ISet<HexCoord>)new HashSet<HexCoord>());
                int ownedExtractionSites = knownBuildings.Values.Count(b => b.Owner == player
                    && !b.IsBase && knownSites.Contains(b.Hex));
                float infrastructurePressure = Mathf.Clamp01(ownedExtractionSites
                    / Mathf.Max(1f, snap.Self.BaseHexes.Count * 3f));
                var activeBaseTargets = new HashSet<HexCoord>(
                    MissionIntentRegistry.GetOrCreate(player).All
                        .Where(i => i != null && i.Status == IntentStatus.Active
                            && i.Kind == MissionKind.Economy
                            && i.Economy?.Kind == EconomyTaskKind.FoundBase)
                        .Select(i => i.Economy.TargetHex));
                var directionalSites = new HashSet<HexCoord>();
                bool hasDirection = TrySelectBaseExpansionDirection(snap, player,
                    out HexCoord targetCitadel, out HexCoord anchor);
                if (hasDirection)
                    directionalSites.UnionWith(knownMapHexes);
                directionalSites.UnionWith(activeBaseTargets);
                foreach (HexCoord hex in directionalSites.OrderBy(x => x.Q).ThenBy(x => x.R))
                    {
                        bool continuing = activeBaseTargets.Contains(hex);
                        if (!knownMapHexes.Contains(hex)
                            || !MeetsBaseSpacing(snap.Self.BaseHexes, hex)
                            || (!continuing && (!hasDirection
                                || !IsForwardBaseCandidate(snap.Self.BaseHexes, anchor,
                                    targetCitadel, hex))))
                            continue;
                        bool hasBuilding = occupied.TryGetValue(hex,
                            out AiMapMemory.KnownBuilding knownBuilding);
                        bool convertsOwnedExtraction = hasBuilding && knownSites.Contains(hex)
                            && knownBuilding.Owner == player && !knownBuilding.IsBase;
                        if (hasBuilding && !convertsOwnedExtraction)
                            continue;
                        baseOpportunities.Add(new EconomyBaseOpportunity
                        {
                            Hex = hex,
                            HexYield = knownSites.Contains(hex)
                                ? EconomyKnownHexYield(snap, hex) : default(ResourceBundle),
                            CapacityValue = convertsOwnedExtraction ? 1f : 0.5f,
                            NearbyResourceClusterValue = EconomyResourceClusterValue(
                                snap, hex, standings),
                            NetworkExpansionValue = EconomyBaseNetworkExpansionValue(
                                snap, hex, standings),
                            InfrastructurePressure = infrastructurePressure,
                            LogisticsValue = Mathf.Clamp01(snap.Self.BaseHexes
                                .Min(baseHex => HexGridMath.Distance(baseHex, hex))
                                / Mathf.Max(1f, AiConfigV2.economyBaseFoundScanRadius)),
                            ConvertsOwnedExtractionSite = convertsOwnedExtraction,
                            BuilderRoutes = EconomyBuilderRoutes(snap, player, ctx, hex),
                        });
                    }
            }
            eco.BaseOpportunities = baseOpportunities;
            bool baseActionable = baseOpportunities.Count > 0;
            bool extractionActionable = extraction.Any(site =>
                site.MarginalIncomeGain > AiConfigV2.allocatorSliceEpsilon);
            eco.HasActionableOpportunity = extractionActionable || baseActionable;

            return eco;
        }

        // AiMapMemory owns the complete last-observed resource line. Economy enumerates that
        // frozen knowledge once per positive type and never reconstructs it from the live map.
        internal static IReadOnlyList<(HexCoord Hex, ResourceType Type, int Yield)>
            KnownExtractionYields(WorldSnapshot snap)
        {
            var result = new List<(HexCoord, ResourceType, int)>();
            if (snap?.Known?.ResourceHexes == null)
                return result;
            foreach (AiMapMemory.KnownResourceHex known in snap.Known.ResourceHexes
                         .GroupBy(x => x.Hex).Select(g => g.First())
                         .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R))
            {
                result.AddRange(PositiveResourceYields(known.Hex,
                    ObservedResourceBundle(known.Yield)));
            }
            return result;
        }

        internal static IReadOnlyList<(HexCoord Hex, ResourceType Type, int Yield)>
            PositiveResourceYields(HexCoord hex, ResourceBundle yield)
        {
            var result = new List<(HexCoord, ResourceType, int)>();
            foreach (ResourceType type in ResourceBundle.All)
            {
                int amount = Mathf.RoundToInt(yield.Get(type));
                if (amount > 0)
                    result.Add((hex, type, amount));
            }
            return result;
        }

        internal static bool TrySelectBaseExpansionDirection(WorldSnapshot snap,
            PlayerSetupData player, out HexCoord targetCitadel, out HexCoord anchor)
        {
            targetCitadel = default;
            anchor = default;
            if (snap?.Self?.BaseHexes == null || snap.Self.BaseHexes.Count == 0)
                return false;
            List<HexCoord> targets = (snap.Known?.Buildings
                    ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .Where(b => b.IsStartingCitadel && b.Owner != null && b.Owner != player
                    && !b.Owner.IsNeutral && !b.Owner.IsEliminated)
                .Select(b => b.Hex)
                .ToList();
            if (targets.Count == 0)
            {
                // Explicit Economy knowledge exception: initial expansion must not be disabled
                // until Recon happens to find a distant opponent. TrueWorld already isolates the
                // sanctioned cheat; only starting-citadel coordinates cross this boundary.
                targets = (snap.TrueWorld?.AllBuildings
                        ?? System.Array.Empty<BuildingSnapshot>())
                    .Where(b => b != null && b.IsStartingCitadel && b.Owner != null
                        && b.Owner != player && !b.Owner.IsNeutral && !b.Owner.IsEliminated)
                    .Select(b => b.Hex)
                    .ToList();
            }
            if (targets.Count == 0)
                return false;
            HexCoord citadel = targets
                .OrderBy(b => snap.Self.BaseHexes.Min(h => HexGridMath.Distance(h, b)))
                .ThenBy(b => b.Q).ThenBy(b => b.R)
                .First();
            targetCitadel = citadel;
            anchor = snap.Self.BaseHexes
                .OrderBy(h => HexGridMath.Distance(h, citadel))
                .ThenBy(h => h.Q).ThenBy(h => h.R).First();
            return true;
        }

        internal static bool IsForwardBaseCandidate(IReadOnlyList<HexCoord> ownBases,
            HexCoord anchor, HexCoord targetCitadel, HexCoord candidate)
        {
            if (ownBases == null || ownBases.Count == 0)
                return false;
            return MeetsBaseSpacing(ownBases, candidate)
                && HexGridMath.Distance(candidate, targetCitadel)
                    < HexGridMath.Distance(anchor, targetCitadel);
        }

        internal static bool MeetsBaseSpacing(IReadOnlyList<HexCoord> ownBases,
            HexCoord candidate) => ownBases != null && ownBases.Count > 0
            && ownBases.Min(h => HexGridMath.Distance(h, candidate))
                >= AiConfigV2.economyBaseMinSpacing;

        private static IReadOnlyList<EconomyBuilderRouteSnapshot> EconomyBuilderRoutes(
            WorldSnapshot snap, PlayerSetupData player, AiTurnContext ctx, HexCoord target)
        {
            var result = new List<EconomyBuilderRouteSnapshot>();
            if (snap?.Self?.Armies == null || player == null || ctx?.Map == null)
                return result;

            Dictionary<int, ArmyData> liveById = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null).ToDictionary(a => a.Id);
            HashSet<int> activeEconomyActors = new HashSet<int>(
                MissionIntentRegistry.GetOrCreate(player).All
                    .Where(i => i != null && i.Status == IntentStatus.Active
                        && i.Kind == MissionKind.Economy && i.PreferredMoverArmyId.HasValue)
                    .Select(i => i.PreferredMoverArmyId.Value));
            foreach (ArmySnapshot army in snap.Self.Armies
                         .Where(a => a != null).OrderBy(a => a.ArmyId))
            {
                if (army.IsGarrison)
                {
                    if (army.HasHero && army.Hex.Equals(target))
                        result.Add(new EconomyBuilderRouteSnapshot
                        {
                            ArmyId = army.ArmyId, TravelCost = 0, ReturnTravelCost = 0,
                            CurrentMovement = army.CurrentMovement, MaxMovement = army.MaxMovement,
                            ActivationApCost = army.ActivationApCost, ArmySize = army.MemberCount,
                            HasActivatedThisTurn = army.HasActivatedThisTurn,
                            EffectiveArmyPower = army.EffectiveArmyPower,
                            HasActiveEconomyCommitment = activeEconomyActors.Contains(army.ArmyId),
                            IsOnTarget = true,
                        });
                    continue;
                }
                if (!army.IsMobileEconomyBuilder
                    || !liveById.TryGetValue(army.ArmyId, out ArmyData live))
                    continue;
                int cost = SafeStepPathing.FindSafePathCost(ctx.Map, live, target);
                if (cost == int.MaxValue)
                    continue;
                int returnCost = int.MaxValue;
                foreach (HexCoord home in snap.Self.BaseHexes ?? System.Array.Empty<HexCoord>())
                {
                    int candidate = SafeStepPathing.FindSafePathCost(ctx.Map, player, target, home);
                    if (candidate < returnCost) returnCost = candidate;
                }
                if (returnCost == int.MaxValue)
                    returnCost = HexGridMath.Distance(target, army.Hex);
                result.Add(new EconomyBuilderRouteSnapshot
                {
                    ArmyId = army.ArmyId,
                    TravelCost = cost,
                    ReturnTravelCost = returnCost,
                    CurrentMovement = army.CurrentMovement,
                    MaxMovement = army.MaxMovement,
                    ActivationApCost = army.ActivationApCost,
                    HasActivatedThisTurn = army.HasActivatedThisTurn,
                    ArmySize = army.MemberCount,
                    EffectiveArmyPower = army.EffectiveArmyPower,
                    HasActiveEconomyCommitment = activeEconomyActors.Contains(army.ArmyId),
                    IsOnTarget = army.Hex.Equals(target),
                });
            }
            return result;
        }

        private static float EconomyBaseNetworkSynergy(WorldSnapshot snap, HexCoord target)
        {
            if (snap?.Self?.BaseHexes == null || snap.Self.BaseHexes.Count == 0)
                return 0f;
            int distance = snap.Self.BaseHexes.Min(h => HexGridMath.Distance(h, target));
            return 1f / Mathf.Max(1f, distance);
        }

        private static float EconomyResourceClusterValue(WorldSnapshot snap,
            HexCoord target,
            IReadOnlyDictionary<ResourceType, EconomyResourceStanding> standings)
        {
            if (snap?.Known?.ResourceHexes == null)
                return 0f;
            float value = 0f;
            foreach (HexCoord site in snap.Known.ResourceHexes.Select(x => x.Hex).Distinct())
            {
                if (HexGridMath.Distance(target, site) > AiConfigV2.economyResourceClusterRadius)
                    continue;
                ResourceBundle yield = EconomyKnownHexYield(snap, site);
                foreach (ResourceType type in ResourceBundle.All)
                    if (yield.Get(type) > 0f
                        && standings.TryGetValue(type, out EconomyResourceStanding standing))
                        value += yield.Get(type) * Mathf.Max(0.1f, standing.DeficitScore);
            }
            return value;
        }

        private static float EconomyBaseNetworkExpansionValue(WorldSnapshot snap,
            HexCoord target, IReadOnlyDictionary<ResourceType, EconomyResourceStanding> standings)
        {
            if (snap?.Known?.ResourceHexes == null || snap.Self?.BaseHexes == null)
                return 0f;
            float value = 0f;
            foreach (HexCoord site in snap.Known.ResourceHexes.Select(x => x.Hex).Distinct())
            {
                if (HexGridMath.Distance(target, site) > AiConfigV2.economyBaseFoundScanRadius
                    || snap.Self.BaseHexes.Any(baseHex => HexGridMath.Distance(baseHex, site)
                        <= AiConfigV2.economyBaseFoundScanRadius))
                    continue;
                ResourceBundle yield = EconomyKnownHexYield(snap, site);
                foreach (ResourceType type in ResourceBundle.All)
                    if (standings.TryGetValue(type, out EconomyResourceStanding standing))
                        value += yield.Get(type) * Mathf.Max(0.25f, standing.DeficitScore);
            }
            return value;
        }

        private static ResourceBundle EconomyKnownHexYield(WorldSnapshot snap, HexCoord hex) =>
            snap?.Known?.ResourceHexes == null
                ? default(ResourceBundle)
                : snap.Known.ResourceHexes
                    .Where(x => x.Hex.Equals(hex))
                    .Select(x => ObservedResourceBundle(x.Yield))
                    .FirstOrDefault();

        private static ResourceBundle ObservedResourceBundle(ResourceYields yield) =>
            yield == null ? default(ResourceBundle) : new ResourceBundle
            {
                Human = yield.Get(ResourceType.Human),
                Energy = yield.Get(ResourceType.Energy),
                Materials = yield.Get(ResourceType.Materials),
                Tech = yield.Get(ResourceType.Tech),
            };

        private static void AccumulateCardCosts(IEnumerable<CardData> cards, ref ResourceBundle need)
        {
            foreach (CardData card in cards)
            {
                CardDefinition d = card?.Definition;
                if (d == null) continue;
                if (d.cardType != CardType.Unit && d.cardType != CardType.Hero
                    && d.cardType != CardType.Facility && d.cardType != CardType.Base)
                    continue;
                ResourceCost cost = CardCostRules.PlayResources(card);
                if (cost == null) continue;
                foreach (ResourceType t in ResourceBundle.All)
                    need.Add(t, cost.Get(t));
            }
        }

        private static void AccumulateCardCosts(IEnumerable<CardDefinition> defs, ref ResourceBundle need)
        {
            foreach (CardDefinition d in defs)
            {
                if (d == null || d.resourceCost == null) continue;
                if (d.cardType != CardType.Unit && d.cardType != CardType.Hero
                    && d.cardType != CardType.Facility && d.cardType != CardType.Base)
                    continue;
                foreach (ResourceType t in ResourceBundle.All)
                    need.Add(t, d.resourceCost.Get(t));
            }
        }

        private static float Median(List<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            int n = values.Count;
            return n % 2 == 1 ? values[n / 2] : 0.5f * (values[n / 2 - 1] + values[n / 2]);
        }

        private static ThreatModel BuildThreat(PlayerSetupData player, AiTurnContext ctx, WorldSnapshot snap)
        {
            var model = new ThreatModel();
            var contacts = new List<EnemyContactSnapshot>();

            foreach (AiMapMemory.KnownEnemySighting s in snap.Known.EnemySightings)
            {
                bool visibleNow = VisionSystem.IsVisible(player, s.Hex);
                contacts.Add(new EnemyContactSnapshot
                {
                    Army = SightingToArmySnapshot(s),
                    Knowledge = visibleNow ? ContactKnowledge.Exact : ContactKnowledge.LastKnown,
                    Source = ContactSource.Honest,
                    Position = s.Hex,
                    Confidence = visibleNow ? AiConfigV2.threatConfidenceExact : AiConfigV2.threatConfidenceLastKnown,
                    LastObservedTurn = visibleNow ? snap.TurnNumber : s.SeenTurn,
                });
            }

            var liveArmyIds = new HashSet<int>(snap.Known.EnemySightings.Select(s => s.ArmyId));
            foreach (ReconObservation obs in AiReconMemory.Historical(player, liveArmyIds))
            {
                int age = System.Math.Max(0, snap.TurnNumber - obs.LastObservedTurn);
                contacts.Add(new EnemyContactSnapshot
                {
                    Army = ObservationToArmySnapshot(obs),
                    Knowledge = ContactKnowledge.LastKnown,
                    Source = ContactSource.Honest,
                    Position = obs.LastObservedHex,
                    Confidence = AiConfigV2.threatConfidenceLastKnown * AiReconMemory.ConfidenceDecay(age),
                    LastObservedTurn = obs.LastObservedTurn,
                });
            }

            foreach (HexCoord home in snap.Self.BaseHexes)
            {
                if (AiMapMemory.HasKnownEnemyWithin(player, home, AiConfig.defenceReactionRadius))
                    continue;

                ArmySnapshot strongest = null;
                float strongestSum = 0f;
                foreach (ArmySnapshot ea in snap.TrueWorld.EnemyArmies)
                {
                    if (ea.IsGarrison || ea.MemberCount == 0 || ea.MemberCount > AiConfig.makeshiftScoutMinMembers)
                        continue;
                    if (HexGridMath.Distance(home, ea.Hex) > AiConfig.defenceReactionRadius)
                        continue;
                    float sum = ea.AttackSum + ea.DefenseSum;
                    if (sum > strongestSum)
                    {
                        strongestSum = sum;
                        strongest = ea;
                    }
                }
                if (strongest != null)
                    contacts.Add(MakeCheatContact(strongest, home, AiConfig.defenceReactionRadius));
            }
            model.Contacts = contacts;

            var byArmy = new Dictionary<int, EnemyContactSnapshot>();
            foreach (EnemyContactSnapshot c in contacts)
            {
                if (c.Source != ContactSource.Honest || !c.Position.HasValue) continue;
                int id = c.Army?.ArmyId ?? 0;
                if (id <= 0) continue;
                if (!byArmy.TryGetValue(id, out EnemyContactSnapshot cur) || c.LastObservedTurn > cur.LastObservedTurn)
                    byArmy[id] = c;
            }
            model.ReconContactByArmyId = byArmy;

            var assets = new List<StrategicAssetSnapshot>();
            float totalIncome = snap.Self.PerTurnIncome.Sum;

            foreach (BuildingSnapshot b in snap.TrueWorld.AllBuildings.Where(x => x.Owner == player))
            {
                ArmySnapshot garrison = snap.Self.Armies.FirstOrDefault(a => a.IsGarrison && a.Hex.Equals(b.Hex));
                var defenders = garrison?.Members ?? (IReadOnlyList<WorthIt.DefenderProfile>)System.Array.Empty<WorthIt.DefenderProfile>();
                float garrisonDef = defenders.Sum(d => d.Defense);

                AssetKind kind = b.IsStartingCitadel ? AssetKind.Citadel
                    : b.HasFacilityAbility(UnitAbilities.Barracks) ? AssetKind.Base
                    : AssetKind.Facility;

                assets.Add(new StrategicAssetSnapshot
                {
                    Hex = b.Hex,
                    Kind = kind,
                    HexDefenseBonus = b.Defense,
                    Defense = b.Defense + garrisonDef,
                    Defenders = defenders,
                    Value = BuildingAssetValue(kind, b, snap, totalIncome),
                });
            }

            foreach (ArmySnapshot a in snap.Self.Armies.Where(x => !x.IsGarrison && !x.IsPrison && x.MemberCount > 0))
            {
                assets.Add(new StrategicAssetSnapshot
                {
                    Hex = a.Hex,
                    Kind = AssetKind.Army,
                    HexDefenseBonus = 0f,
                    Defense = a.DefenseSum,
                    Defenders = a.Members,
                    Value = Mathf.Min(AiConfigV2.assetValueArmyCap, a.EffectiveArmyPower / AiConfigV2.assetValueArmyPowerDivisor),
                });
            }
            model.Assets = assets;

            var threats = new List<AssetThreatSnapshot>();
            List<ArmySnapshot> ownFieldForResponse = snap.Self.Armies
                .Where(a => !a.IsPrison && a.MemberCount > 0).ToList();

            foreach (EnemyContactSnapshot c in contacts)
            {
                foreach (StrategicAssetSnapshot asset in assets)
                {
                    bool canDamage = WorthIt.CanDamageAll(c.Army.Members, asset.Defenders, asset.HexDefenseBonus);
                    float winChance = WorthIt.WinChance(
                        (IReadOnlyCollection<WorthIt.DefenderProfile>)c.Army.Members,
                        (IReadOnlyCollection<WorthIt.DefenderProfile>)asset.Defenders,
                        asset.HexDefenseBonus);

                    int? enemyEta = null;
                    if (c.Position.HasValue)
                        enemyEta = CeilDiv(HexGridMath.Distance(c.Position.Value, asset.Hex),
                            Mathf.Max(AiConfigV2.etaFallbackMoveBudget, c.Army.MaxMovement));

                    int? responseEta = null;
                    foreach (ArmySnapshot r in ownFieldForResponse)
                    {
                        int e = CeilDiv(HexGridMath.Distance(r.Hex, asset.Hex),
                            Mathf.Max(AiConfigV2.etaFallbackMoveBudget, r.MaxMovement));
                        if (!responseEta.HasValue || e < responseEta.Value)
                            responseEta = e;
                    }

                    float potentialDamage = winChance * (canDamage ? 1f : 0f);
                    float severity = Severity(winChance, potentialDamage, enemyEta, responseEta, canDamage, c.Confidence);
                    if (severity < AiConfigV2.severityListingCutoff) continue;

                    threats.Add(new AssetThreatSnapshot
                    {
                        Asset = asset,
                        Contact = c,
                        CanDamage = canDamage,
                        EnemyEta = enemyEta,
                        ResponseEta = responseEta,
                        AttackWinChance = winChance,
                        PotentialDamage = potentialDamage,
                        Confidence = c.Confidence,
                        Severity = severity,
                    });
                }
            }
            model.Threats = threats;

            bool derivedSiege = threats.Any(t =>
                (t.Asset.Kind == AssetKind.Citadel || t.Asset.Kind == AssetKind.Base)
                && t.AttackWinChance >= AiConfigV2.siegeEnemyWinChanceThreshold
                && ((t.Contact.Position.HasValue
                        && HexGridMath.Distance(t.Contact.Position.Value, t.Asset.Hex) <= AiConfigV2.siegeRadius)
                    || (t.EnemyEta.HasValue && t.EnemyEta.Value <= AiConfigV2.siegeEnemyEtaTurns)));
            model.UnderSiege = derivedSiege;

            return model;
        }

        private static EnemyContactSnapshot MakeCheatContact(ArmySnapshot source, HexCoord regionCenter, int regionRadius)
        {
            return new EnemyContactSnapshot
            {
                Army = new ArmySnapshot
                {
                    ArmyId = -1,
                    Owner = source.Owner,
                    Hex = default,
                    MemberCount = source.MemberCount,
                    HasHero = source.HasHero,
                    HasAntiAir = source.HasAntiAir,
                    IsHiddenFromUs = source.IsHiddenFromUs,
                    AttackSum = source.AttackSum,
                    DefenseSum = source.DefenseSum,
                    EffectiveArmyPower = source.EffectiveArmyPower,
                    MaxMovement = 0,
                    Members = source.Members,
                },
                Knowledge = ContactKnowledge.Region,
                Source = ContactSource.Cheat,
                Position = null,
                RegionCenter = regionCenter,
                RegionRadius = regionRadius,
                Confidence = AiConfigV2.threatConfidenceCheatRegion,
            };
        }

        private static ArmySnapshot ObservationToArmySnapshot(ReconObservation o)
        {
            var members = o.Defenders != null
                ? new List<WorthIt.DefenderProfile>(o.Defenders)
                : new List<WorthIt.DefenderProfile>();
            return new ArmySnapshot
            {
                ArmyId = o.ArmyId,
                Owner = o.Owner,
                Hex = o.LastObservedHex,
                MemberCount = o.MemberCount,
                HasAntiAir = o.HasAntiAir,
                AttackSum = o.AttackSum,
                DefenseSum = o.DefenseSum,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(members),
                MaxMovement = 1,
                Members = members,
            };
        }

        private static ArmySnapshot SightingToArmySnapshot(AiMapMemory.KnownEnemySighting s)
        {
            var members = s.Defenders != null
                ? new List<WorthIt.DefenderProfile>(s.Defenders)
                : new List<WorthIt.DefenderProfile>();
            return new ArmySnapshot
            {
                ArmyId = -1,
                Owner = s.Owner,
                Hex = s.Hex,
                MemberCount = s.MemberCount,
                HasAntiAir = s.HasAntiAir,
                AttackSum = s.AttackSum,
                DefenseSum = s.DefenseSum,
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(members),
                MaxMovement = 1,
                Members = members,
            };
        }

        private static float BuildingAssetValue(AssetKind kind, BuildingSnapshot b, WorldSnapshot snap, float totalIncome)
        {
            switch (kind)
            {
                case AssetKind.Citadel: return AiConfigV2.assetValueCitadel;
                case AssetKind.Base: return AiConfigV2.assetValueBase;
                default:
                    float v = AiConfigV2.assetValueFacilityBase;
                    if (b.HasFacilityAbility(UnitAbilities.Barracks))
                        v += AiConfigV2.assetValueFacilityBarracksBonus;
                    if (b.HasFacilityAbility(UnitAbilities.Research) || b.HasFacilityAbility(UnitAbilities.Production))
                        v += AiConfigV2.assetValueFacilityDevBonus * (snap.Self.HasDevOperator || snap.Self.HasDevFacility ? 1f : 0.3f);
                    for (int i = 0; i < ResourceBundle.All.Length; i++)
                    {
                        ResourceType t = ResourceBundle.All[i];
                        if (b.HasFacilityAbility(UnitAbilities.CollectAbilityFor(t)))
                        {
                            float share = totalIncome > 0.0001f ? snap.Self.PerTurnIncome.Get(t) / totalIncome : 0.25f;
                            v += AiConfigV2.assetValueFacilityCollectorBonus * share;
                        }
                    }
                    return v;
            }
        }

        private static float Severity(float winChance, float potentialDamage, int? enemyEta, int? responseEta,
            bool canDamage, float confidence)
        {
            float etaUrgency = enemyEta.HasValue
                ? 1f / (1f + enemyEta.Value)
                : 1f / (1f + AiConfigV2.etaUnknownContactPenalty);

            float responseHeadstart = (enemyEta.HasValue && responseEta.HasValue)
                ? Mathf.Clamp01((responseEta.Value - enemyEta.Value) / 4f)
                : 0f;

            float posWeight = AiConfigV2.severityWinChanceWeight + AiConfigV2.severityDamageWeight
                + AiConfigV2.severityEtaWeight + AiConfigV2.severityCanDamageWeight;
            float raw = AiConfigV2.severityWinChanceWeight * winChance
                + AiConfigV2.severityDamageWeight * potentialDamage
                + AiConfigV2.severityEtaWeight * etaUrgency
                + AiConfigV2.severityCanDamageWeight * (canDamage ? 1f : 0f)
                - AiConfigV2.severityResponseHeadstartWeight * responseHeadstart;

            return confidence * Mathf.Clamp01(raw / Mathf.Max(0.0001f, posWeight));
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
