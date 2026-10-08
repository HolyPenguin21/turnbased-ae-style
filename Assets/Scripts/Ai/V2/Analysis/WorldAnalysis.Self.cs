using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;
using Game.Aviation;
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
    // Self — BuildSelf, army/ability projection (ToArmySnapshot + its helpers, BuildApActionEconomy), and ArmyVisionRadius (also used by BuildTrueWorld in WorldAnalysis.Knowledge.cs).
    // File-split (mechanical, no behaviour change) from WorldAnalysis.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 5. Still exactly the WorldAnalysis
    // class; only this snapshot family's slice moved to its own file.
    public static partial class WorldAnalysis
    {
        private static SelfSnapshot BuildSelf(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var self = new SelfSnapshot();

            List<ArmyData> ownArmies = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && !a.IsPrison)
                .ToList();

            List<BuildingData> buildings = BuildingRegistry.AllBuildings()
                .Where(b => b != null).ToList();
            HexCoord? configuredCitadel = player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue
                ? new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value)
                : (HexCoord?)null;
            BuildingData citadelBuilding = buildings.FirstOrDefault(b => b.Owner == player
                && (b.IsStartingCitadel
                    || configuredCitadel.HasValue && b.Hex.Equals(configuredCitadel.Value)));
            HexCoord? canonicalCitadel = citadelBuilding != null
                ? (HexCoord?)citadelBuilding.Hex : null;
            List<HexCoord> baseHexes = OwnedBaseHexes(buildings, player, configuredCitadel);
            HexCoord citadel = canonicalCitadel
                ?? baseHexes.Select(h => (HexCoord?)h).FirstOrDefault()
                ?? default;

            self.Citadel = citadel;
            self.HoldsStartingCitadel = canonicalCitadel.HasValue
                && (!configuredCitadel.HasValue || canonicalCitadel.Value.Equals(configuredCitadel.Value));
            self.BaseHexes = baseHexes;
            using (new ProfileScope("AI/Self.ArmySnapshots"))
                self.Armies = ownArmies.Select(a => ToArmySnapshot(a, player, isOwn: true, ArmyVisionRadius(ctx), ctx)).ToList();
            // Includes own remaining MP: rebuild with Self even when enemy knowledge is unchanged.
            using (new ProfileScope("AI/Self.ReconCapture"))
                self.ReconCaptureOpportunities = ownArmies.Where(AiArmyRoles.IsSoloRecce)
                .SelectMany(a => HexGridMath.Neighbors(a.Hex).Concat(new[] { a.Hex })
                    .Where(h => ReconReactionPolicy.CanCaptureStructureAt(player, ctx?.Map, a, h))
                    .Select(h => (a.Id, h))).ToList();

            // Freeze the GENUINE route-existence fact for every structural raid
            // actor against every own base, the exact same SafeStepPathing oracle Provisioning
            // re-runs live for the Return leg (ProvisionReturn's FindNextSafeStep), so a
            // snapshot-only consumer (AiReturnBasePolicy.SelectReturnBase /
            // ReturnBaseStillValid) can tell a structurally unreachable base apart from one that is
            // merely temporarily blocked this turn, without doing live pathing itself.
            using (new ProfileScope("AI/Self.ReturnRoutes"))
            if (baseHexes.Count > 0)
                foreach (ArmySnapshot a in self.Armies)
                {
                    if (!a.IsStructuralRaidActor) continue;
                    var reachable = new List<HexCoord>();
                    foreach (HexCoord baseHex in baseHexes)
                        if (SafeStepPathing.FindSafePathCost(ctx.Map, player, a.Hex, baseHex) != int.MaxValue)
                            reachable.Add(baseHex);
                    a.ReachableOwnBaseHexes = reachable;
                }

            // Economy consumes exact physical home costs, including solo collectors. Keep the
            // combat return-policy facts above unchanged: its fallback contract is different.
            using (new ProfileScope("AI/Self.HomeRoutes"))
            foreach (ArmySnapshot actor in self.Armies.Where(a => !a.IsAir && !a.IsAirfield
                         && !a.IsGarrison && !a.IsPrison))
                actor.EconomyHomeRouteCosts = EconomyHomeRoutes(player, ctx,
                    actor.Hex, actor.MaxMovement, baseHexes);

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
            self.PoolCards = self.Hand.Where(c => c?.Definition != null)
                .Select(c => (c.Definition, c.Equipment, c.Mutator, true))
                .Concat(self.Deck.Where(d => d != null).Select(d => (d, (CardDefinition)null, (CardDefinition)null, false)))
                .ToList();
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

            using (new ProfileScope("AI/Self.ApActionEconomy"))
                BuildApActionEconomy(self, player, ownArmies, hand, ctx);

            using (new ProfileScope("AI/Self.ForceMeasures"))
                BuildForceMeasures(self, player, ownArmies);

            self.MobilizationHeld = ctx != null
                && OperationContinuationWindow.IsMobilizationHeld(player, ctx.TurnNumber);
            return self;
        }

        // Canonical Base topology projection. Garrison containers are deliberately absent from
        // this contract: a temporarily empty/recreated garrison cannot make the strategic Base,
        // airfield, repair point or Recon anchor disappear from Analysis.
        internal static List<HexCoord> OwnedBaseHexes(IEnumerable<BuildingData> buildings,
            PlayerSetupData player, HexCoord? citadel)
        {
            List<BuildingData> source = (buildings ?? System.Array.Empty<BuildingData>())
                .Where(b => b != null).ToList();
            var result = source
                .Where(b => b != null && b.Owner == player && b.IsBase)
                .Select(b => b.Hex)
                .Distinct()
                .OrderBy(h => h.Q).ThenBy(h => h.R)
                .ToList();
            // Old saves may predate the IsBase bit, but the fallback still requires a LIVE owned
            // starting-citadel building. Raw PlayerSetupData coordinates survive capture/destruction
            // and must never resurrect a phantom Base anchor.
            if (citadel.HasValue && !result.Contains(citadel.Value)
                && source.Any(b => b.Owner == player && b.IsStartingCitadel
                    && b.Hex.Equals(citadel.Value)))
                result.Add(citadel.Value);
            result.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
            return result;
        }

        // 2026-09-30 (user decision) — mobilization start (B): the strongest ONE stack the bodies
        // already on the field could form (AiPower.TotalMilitaryPotential, one commander-in-slot
        // rule). Counted: ground bodies of every field army, busy ones included (a Raid or an
        // ActiveDefence finishes and its army comes back), and the bodies a garrison may spare
        // above its defence floor (AiArmyRoles.SpareableBodies). Not counted: aviation, explicit
        // scouts (lone scouts and armies a Scout mission holds — the same set AttackForcePool
        // leaves out, so the gate compares like with like), prisoners and the garrison's
        // mandatory defence. Heroes
        // bring only their slots (their power is 0); a facility operator commands nothing.
        internal static float FieldStrikePotential(PlayerSetupData player, IEnumerable<ArmyData> ownArmies,
            float groundAvailablePower)
        {
            var pool = new List<AiPower.PowerUnit>();
            HexCoord citadel = AiTurnController.GarrisonHexFor(player);
            HashSet<int> scouts = AttackForcePool.ScoutMissionArmyIds(player);
            foreach (ArmyData a in ownArmies ?? Enumerable.Empty<ArmyData>())
            {
                if (a == null || a.IsPrison || a.IsAirfield || AviationRules.IsAirArmy(a)
                    || AiArmyRoles.IsSoloRecce(a) || scouts.Contains(a.Id))
                    continue;
                List<UnitData> bodies = a.Members
                    .Where(u => u != null && !u.IsPrisoner && !u.IsSummoned
                        && AiArmyRoles.IsGroundBattleBody(u)).ToList();
                if (a.IsGarrison)
                {
                    HashSet<int> spare = AiArmyRoles.SpareableBodies(bodies,
                        set => AiPower.EffectiveArmyPower(set.ToList()),
                        AiArmyRoles.GarrisonDefenceFloor(groundAvailablePower, a.Hex.Equals(citadel)));
                    bodies = bodies.Where((u, i) => spare.Contains(i)).ToList();
                }
                pool.AddRange(bodies.Select(AiPower.ToPowerUnit));
                pool.AddRange(a.Members
                    .Where(u => u != null && u.IsHero && !u.IsPrisoner && !u.IsAviation
                        && !AiArmyRoles.IsFacilityOperator(player, a.Hex, u))
                    .Select(AiPower.ToPowerUnit));
            }
            return AiPower.TotalMilitaryPotential(pool);
        }

        private static bool IsMilitaryCard(CardDefinition d) =>
            d.cardType == CardType.Unit || d.cardType == CardType.Hero;

        // Strike force step 3 — the SelfSnapshot force measures, all on one scale: the AiPower
        // strength of one composed ground stack. The pools are nested on the map stack, each
        // step adding one source, so FieldPotential + Reserve.Units + Reserve.Hero is exactly
        // TotalMilitaryPotential. Aviation never joins a ground stack; it is reserve only.
        // Prisoners do not fight and are no one's commander.
        private static void BuildForceMeasures(SelfSnapshot self, PlayerSetupData player,
            List<ArmyData> ownArmies)
        {
            var commandHeroes = new List<OwnCommandHero>();
            var mapPool = new List<AiPower.PowerUnit>();
            foreach (ArmyData a in ownArmies)
                foreach (UnitData m in a.Members)
                {
                    if (m == null || m.IsAviation || m.IsPrisoner) continue;
                    mapPool.Add(AiPower.ToPowerUnit(m));
                    if (!m.IsHero) continue;
                    commandHeroes.Add(new OwnCommandHero(
                        HeroRoleEvaluator.Profile(m, m.RuntimeId), ForceSource.Map));
                }

            var handUnits = new List<AiPower.PowerUnit>();
            var handHeroes = new List<AiPower.PowerUnit>();
            var equipment = new List<CardDefinition>();
            float aviation = 0f;

            // Cards carry no runtime id: hand then deck, by position, as negative keys.
            // Hand bodies/heroes are kept for BestStackPotential (map + hand); the deck feeds
            // only CommandHeroes, equipment and aviation here.
            void AddCard(CardDefinition d, ForceSource source, int key, List<AiPower.PowerUnit> units,
                List<AiPower.PowerUnit> heroes, CardData held = null)
            {
                if (d == null) return;
                if (d.cardType == CardType.Equipment) { equipment.Add(d); return; }
                if (!IsMilitaryCard(d)) return;
                // A held card counts with the equipment already attached to it.
                AiPower.PowerUnit pu = held != null ? AiPower.ToPowerUnit(held) : AiPower.ToPowerUnit(d);
                if (d.isAviation) { aviation += pu.BasePower; return; }
                if (d.cardType == CardType.Unit) { units?.Add(pu); return; }
                heroes?.Add(pu);
                commandHeroes.Add(new OwnCommandHero(HeroRoleEvaluator.Profile(d, key), source));
            }
            for (int i = 0; i < self.Hand.Count; i++)
                AddCard(self.Hand[i]?.Definition, ForceSource.Hand, -(1 + i),
                    handUnits, handHeroes, self.Hand[i]);
            for (int i = 0; i < self.Deck.Count; i++)
                AddCard(self.Deck[i], ForceSource.Deck, -(1 + self.Hand.Count + i),
                    null, null);

            // One commander-in-slot rule for every nested ceiling (AiPower.NestedPotentials).
            AiPower.ForcePotentials ceilings;
            using (new ProfileScope("AI/Self.NestedPotentials"))
                ceilings = AiPower.NestedPotentials(
                    ownArmies.SelectMany(a => a.Members), self.Hand, self.Deck);

            self.FieldPotential = ceilings.Field;
            self.BestStackPotential = AiPower.TotalMilitaryPotential(
                mapPool.Concat(handUnits).Concat(handHeroes).ToList());
            self.TotalMilitaryPotential = ceilings.Total;
            using (new ProfileScope("AI/Self.AdditivePower"))
                PlayerForceAnalysis.AdditivePower(player, ownArmies, self.Hand, self.Deck,
                    out self.DeployedPower, out self.AvailablePower);
            using (new ProfileScope("AI/Self.FieldStrikePotential"))
                self.FieldStrikePotential = FieldStrikePotential(player, ownArmies, self.AvailablePower);
            // The force an Attack can actually assemble (busy armies, scouts, garrison defence,
            // garrison heroes and operators out): its peak, roster and pool.
            AttackForcePool attackPool;
            using (new ProfileScope("AI/Self.AttackForcePool"))
                attackPool = AttackForcePool.Build(player, self);
            self.AttackPeak = attackPool.Peak;
            self.StrikeRoster = attackPool.Roster;
            self.StrikePool = attackPool.Pool;
            self.StrikePoolKeyCounts = attackPool.KeyCounts;
            self.FistPower = self.Armies.Where(a => a.IsStructuralRaidActor)
                .Select(a => a.EffectiveArmyPower).DefaultIfEmpty(0f).Max();
            self.StartPotential = ForceBaselineRegistry.TryGetStart(player, out float start)
                ? start : self.TotalMilitaryPotential;
            self.Reserve = new ForceReserve(
                units: ceilings.UnitsReserve,
                hero: ceilings.HeroReserve,
                equipment: EquipmentReserve(self, ownArmies, equipment),
                aviation: aviation);
            self.CommandHeroes = commandHeroes;
        }

        // Σ over the equipment cards of each one's combat gain on a free, legal ground host
        // (StrategicCardEvaluator.EquipmentDeltaParts — the one equipment value). A host takes one
        // item; the largest gains are matched first.
        private static float EquipmentReserve(SelfSnapshot self, List<ArmyData> ownArmies,
            List<CardDefinition> equipment)
        {
            if (equipment.Count == 0)
                return 0f;

            // Each free ground host as "the combat gain of this item on it" (0 = does not fit).
            var hosts = new List<System.Func<CardDefinition, float>>();
            foreach (AttachmentSlot slot in new[] { AttachmentSlot.Equipment, AttachmentSlot.Mutator })
            {
                foreach (ArmyData a in ownArmies)
                    foreach (UnitData u in a.Members)
                        if (u != null && !u.IsAviation && !u.IsPrisoner && u.OriginatingCard != null
                            && (slot == AttachmentSlot.Equipment ? u.Equipment == null : u.Mutator == null))
                            hosts.Add(eq => eq.attachmentSlot == slot
                                && EquipmentSystem.FitsHost(eq, u.OriginatingCard, out _)
                                    ? ReserveBodies(StrategicCardEvaluator.EquipmentDeltaParts(eq, u)) : 0f);
                foreach (CardData c in self.Hand)
                    if (IsGroundHostCard(c?.Definition)
                        && (slot == AttachmentSlot.Equipment ? c.Equipment == null : c.Mutator == null))
                        hosts.Add(eq => eq.attachmentSlot == slot && EquipmentSystem.FitsHost(eq, c.Definition, out _)
                            ? ReserveBodies(StrategicCardEvaluator.EquipmentDeltaParts(eq, c)) : 0f);
                foreach (CardDefinition d in self.Deck)
                    if (IsGroundHostCard(d))
                        hosts.Add(eq => eq.attachmentSlot == slot && EquipmentSystem.FitsHost(eq, d, out _)
                            ? ReserveBodies(StrategicCardEvaluator.EquipmentDeltaParts(eq, d)) : 0f);
            }

            var pairs = new List<(float gain, int item, int host)>();
            for (int e = 0; e < equipment.Count; e++)
                for (int h = 0; h < hosts.Count; h++)
                {
                    float gain = hosts[h](equipment[e]);
                    if (gain > 0f) pairs.Add((gain, e, h));
                }
            pairs.Sort((x, y) => x.gain != y.gain ? y.gain.CompareTo(x.gain)
                : x.item != y.item ? x.item.CompareTo(y.item) : x.host.CompareTo(y.host));

            var usedItems = new HashSet<int>();
            var usedHosts = new HashSet<int>();
            float total = 0f;
            foreach ((float gain, int item, int host) in pairs)
                if (!usedItems.Contains(item) && !usedHosts.Contains(host))
                {
                    usedItems.Add(item);
                    usedHosts.Add(host);
                    total += gain;
                }
            return total;
        }

        // The ONE equipment formula, read in its reference (snapshot-free) context, converted at this
        // consumer boundary: the reserve counts combat bodies. EquipmentDelta.Combat is U_combat divided by
        // the persistence factor, U_combat = 1.10 x dC in card score, and one reference body is
        // AiConfigV2.equipReserveReferenceBodyC of C.
        private static float ReserveBodies(StrategicCardEvaluator.EquipmentDelta delta) =>
            delta.Combat * AiConfigV2.equipmentUpgradePersistence / AiConfigV2.equipCombatCardScale
            / AiConfigV2.equipReserveReferenceBodyC;

        private static bool IsGroundHostCard(CardDefinition d) =>
            d != null && IsMilitaryCard(d) && !d.isAviation;

        // AI-MGR — Dynamic Strategic Effect Utility. Snapshot-pure AP action-economy read: how many
        // recurring-AP sources are in play, how much AP the AI could still usefully spend this turn,
        // and from those the marginal value of one more AP/turn. ACTION economy only — never a
        // H/E/M/T security read. Also counts the non-hero bodies a hero's Command could realistically
        // put to use.
        private static void BuildApActionEconomy(SelfSnapshot self, PlayerSetupData player,
            List<ArmyData> ownArmies, AiHandData hand, AiTurnContext ctx)
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
                    if (m != null && m.IsGroundCombatant) nonHeroBodies++;
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
                EstimatedDrawApDemand = Mathf.Min(hand?.RemainingDeck?.Count ?? 0,
                    Mathf.Max(0, AiConfigV2.handReplenishTargetCards - (hand?.Hand?.Count ?? 0)))
                    * (ctx?.DrawApCost ?? 0),
                WitnessedApDemand = ctx != null ? ApTurnPressure.WitnessedDemand(player, ctx.TurnNumber) : null,
            };
            self.DeployableCombatBodies = nonHeroBodies;
        }

        internal static ArmySnapshot ToArmySnapshot(ArmyData a, PlayerSetupData viewer, bool isOwn, int armyVisionRadius, AiTurnContext ctx = null)
        {
            var nonHero = a.Members.Where(m => m.IsGroundCombatant).ToList();
            bool allHidden = !isOwn && a.Members.Count > 0
                && a.Members.All(m => StealthSystem.IsHiddenFrom(m, viewer));

            bool reconAir = isOwn && ReconAirCapacityPolicy.IsReadyStandaloneWing(viewer, a);
            if (!reconAir && isOwn && ctx != null && ReconAirCapacityPolicy.IsAirborneReconWing(viewer, a))
            {
                ReconAirSortieState projected = ReconAirReservationPrepass.ProjectScoringSortie(viewer, ctx, a);
                reconAir = projected != null && projected.Phase != ReconAirPhase.Return && projected.Phase != ReconAirPhase.Hold;
            }

            return new ArmySnapshot
            {
                ArmyId = a.Id,
                Owner = a.Owner,
                Hex = a.Hex,
                IsGarrison = a.IsGarrison,
                IsPrison = a.IsPrison,
                IsAir = a.IsAirArmy,
                IsAirfield = a.IsAirfield,
                CanServeReconAir = reconAir,
                StoredAircraft = isOwn && a.IsAirfield
                    ? a.Members.Where(AviationRules.IsAviation).Select(u => new StoredAircraftLaunchCost(
                        u.RuntimeId, Mathf.Max(0, u.ActivationApCost), Mathf.Max(0, u.LaunchEnergyCost),
                        AviationRules.EffectiveMoveCurrent(u))).ToList()
                    : (IReadOnlyList<StoredAircraftLaunchCost>)System.Array.Empty<StoredAircraftLaunchCost>(),
                MemberCount = a.Members.Count,
                HasHero = a.Members.Any(m => m.IsHero),
                HeroCount = a.Members.Count(m => m.IsHero),
                BestHeroCommandRating = a.Members.Where(m => m.IsHero).Select(m => m.CommandRating).DefaultIfEmpty(0).Max(),
                // A hero is public battle information once seen; a stealth-hidden commander is not.
                Commander = a.Commander != null
                    && (isOwn || !StealthSystem.IsHiddenFrom(a.Commander, viewer))
                    ? WorthIt.SideCommander.Of(a.Commander) : default,
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
                SafeUnlandedEndsRemaining = a.IsAirArmy
                    ? AviationRange.SafeUnlandedEndsRemaining(a) : 0,
                Capacity = a.Capacity,
                OccupiedBattleSlots = a.Members.Count,
                Members = nonHero.Select(WorthIt.FromLiveUnit).ToList(),
                NonHeroMutatorOccupied = isOwn ? nonHero.Select(u => u.Mutator != null).ToList() : null,
                MembersWithHeroes = a.Members.Select(WorthIt.FromLiveUnit).ToList(),
                RecoveryMembers = a.Members.Select((u, index) => ToRaidRecoveryMember(
                    a, u, index, viewer, isOwn)).ToList(),
                NonHeroRuntimeIds = isOwn ? nonHero.Select(u => u.RuntimeId).ToList() : System.Array.Empty<int>(),
                NonHeroSpareable = isOwn && a.IsGarrison
                    ? nonHero.Select(u => AiArmyRoles.CanSpareGarrisonMember(viewer, a, u)).ToList() : null,
                NonHeroCurrentMovement = nonHero.Select(u => AviationRules.EffectiveMoveCurrent(u)).ToList(),
                EconomyRosterProtected = isOwn && MissionIntentRegistry.GetOrCreate(a.Owner).All.Any(i => i != null
                    && i.Status == IntentStatus.Active && i.Kind != MissionKind.Economy
                    && i.PreferredMoverArmyId == a.Id),
                HeroCurrentMovement = a.Members.Where(u => u.IsHero)
                    .Select(AviationRules.EffectiveMoveCurrent).DefaultIfEmpty(a.CurrentMovement).Min(),
                NonHeroActivationApCosts = nonHero.Select(u => u.ActivationApCost).ToList(),
                NonHeroMoveMax = nonHero.Select(u => u.MoveMax).ToList(),
                NonHeroIsAviation = nonHero.Select(u => u.IsAviation).ToList(),
                HeroActivationApCost = a.Members.Where(u => u.IsHero)
                    .Sum(u => u.ActivationApCost),
                HeroMoveMax = a.Members.Where(u => u.IsHero)
                    .Select(u => u.MoveMax).DefaultIfEmpty(a.MaxMovement).Min(),
                HeroIsHomeVocation = a.Members.Where(u => u.IsHero)
                    .Any(HeroRoleEvaluator.HasSupportVocation),
                HasResearchOperator = a.Members.Any(u => u.IsHero && !u.IsPrisoner
                    && u.HasAbility(UnitAbilities.Researcher)),
                HasProductionOperator = a.Members.Any(u => u.IsHero && !u.IsPrisoner
                    && u.HasAbility(UnitAbilities.Assembler)),
                ActivationApCost = a.ActivationApCost,
                ActivationEnergyCost = a.ActivationEnergyCost,
                HasActivatedThisTurn = a.HasActivatedThisTurn,
                PendingActivationApCost = a.PendingActivationApCost,
                PendingActivationEnergyCost = a.PendingActivationEnergyCost,
                ActivationCoveredUnitRuntimeIds = ArmyRegistry.AllForOwner(a.Owner)
                    .Where(other => other != null)
                    .SelectMany(other => other.Members)
                    .Where(unit => unit != null && a.HasActivationCoverageFor(unit))
                    .Select(unit => unit.RuntimeId)
                    .Distinct()
                    .ToList(),
                CurrentMovement = a.CurrentMovement,
                IsSoloRecce = isOwn && AiArmyRoles.IsSoloRecce(a),
                IsMobileEconomyBuilder = isOwn && AiArmyRoles.IsHeroLed(a),
                IsStructuralRaidActor = isOwn && AiArmyRoles.IsStructuralGroundCombatActor(a),
                IsHidden = isOwn && StealthSystem.IsArmyFullyHidden(a),
                CanEnterStealth = isOwn && a.Members.Any(StealthSystem.CanEnterStealth),
                StealthLevel = isOwn
                    ? a.Members.Select(AbilityParams.GetStealthLevel).DefaultIfEmpty(0).Max()
                    : 0,
                EffectiveVisionRadius = armyVisionRadius + AbilityParams.GetBestRecceRadius(a),
                CollectionCapacity = isOwn ? CollectionCapacityOf(a) : default(ResourceBundle),
            };
        }

        private static RaidRecoveryMemberSnapshot ToRaidRecoveryMember(ArmyData army, UnitData unit,
            int index, PlayerSetupData viewer, bool isOwn)
        {
            WorthIt.DefenderProfile current = WorthIt.FromLiveUnit(unit);
            var full = new WorthIt.DefenderProfile(current.Defense, current.HasCeramicArmor,
                current.TypeTags, current.Attack, current.MaxHitPoints, current.Initiative,
                current.Abilities, current.MaxHitPoints, range: current.Range);
            bool initialized = isOwn && unit.RepairResourceCost != null;
            if (isOwn && unit.HitPointsCurrent < unit.HitPointsMax && !initialized)
                AiDebugLog.WriteDeduped($"repair-cost:{unit.RuntimeId}",
                    $"[AI][V2][RaidRecovery] decision=SKIP_REPAIR unit={unit.RuntimeId} "
                    + "reason=repair_cost_not_initialized_at_spawn_or_load_boundary");
            ResourceVector cost = initialized
                ? new ResourceVector(0f,
                    unit.RepairResourceCost.Get(ResourceType.Human),
                    unit.RepairResourceCost.Get(ResourceType.Energy),
                    unit.RepairResourceCost.Get(ResourceType.Materials),
                    unit.RepairResourceCost.Get(ResourceType.Tech))
                : ResourceVector.Zero;
            bool isBody = AiArmyRoles.IsGroundBattleBody(unit);
            bool canSpare = isOwn && army.Members.Count > 1 && isBody
                && army.CanLeaveWithoutOvercrowding(unit)
                && (!army.IsGarrison || AiArmyRoles.CanSpareGarrisonMember(viewer, army, unit));
            return new RaidRecoveryMemberSnapshot(unit.RuntimeId, index, unit.IsHero,
                unit.IsAviation, isBody, canSpare, unit.ActivationApCost, current, full,
                initialized, cost);
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

    }
}
