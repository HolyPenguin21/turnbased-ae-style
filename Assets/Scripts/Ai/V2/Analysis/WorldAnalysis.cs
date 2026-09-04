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
            snap.Known = BuildKnown(player, snap.Self.BaseHexes);
            AiReconMemory.Observe(player, ctx.TurnNumber, snap.Known.EnemySightings);
            snap.TrueWorld = BuildTrueWorld(player, ctx);
            snap.MapKnowledge = BuildMapKnowledge(player, ctx, snap);
            snap.Economy = BuildEconomy(player, ctx, snap);
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
            snap.Economy = BuildEconomy(player, ctx, snap);
            snap.Threat = BuildThreat(player, ctx, snap);

            SelfSnapshot s = snap.Self;
            AiDebugLog.Write($"[AI][V2] {player?.Nickname} op-refresh — AP {s.ActionPoints} "
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
            snap.Known = BuildKnown(player, snap.Self.BaseHexes);
            AiReconMemory.Observe(player, ctx.TurnNumber, snap.Known.EnemySightings);
            snap.TrueWorld = BuildTrueWorld(player, ctx);
            snap.MapKnowledge = BuildMapKnowledge(player, ctx, snap);
            snap.Economy = BuildEconomy(player, ctx, snap);
            snap.Threat = BuildThreat(player, ctx, snap);

            AiDebugLog.Write($"[AI][V2] {player?.Nickname} knowledge-refresh — "
                + $"enemyKnown {snap.Known.EnemySightings.Count} neutralKnown {snap.Known.NeutralSightings.Count} "
                + $"visited {snap.MapKnowledge.VisitedHexes}/{snap.MapKnowledge.TotalHexes} "
                + $"frontier {snap.MapKnowledge.Frontier.Count} threats {snap.Threat.Threats.Count}");
            return snap;
        }

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
            AiDebugLog.Write($"[AI][V2]   self.stock H/E/M/T={F(self.Stockpile.Human)}/{F(self.Stockpile.Energy)}/"
                + $"{F(self.Stockpile.Materials)}/{F(self.Stockpile.Tech)} "
                + $"| income={F(self.PerTurnIncome.Human)}/{F(self.PerTurnIncome.Energy)}/"
                + $"{F(self.PerTurnIncome.Materials)}/{F(self.PerTurnIncome.Tech)}");
            foreach (ArmySnapshot a in self.Armies)
                AiDebugLog.Write($"[AI][V2]     army \"{ArmyLabel(a)}\" @{a.Hex.Q},{a.Hex.R} eff={F(a.EffectiveArmyPower)} "
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
                AiDebugLog.Write($"[AI][V2]     eco.{rs.Type} own={F(rs.OwnIncome)} fieldMedian={F(rs.FieldMedianIncome)} "
                    + $"ratio={F(rs.Ratio)}");

            int honest = th.Contacts.Count(c => c.Source == ContactSource.Honest);
            int cheat = th.Contacts.Count - honest;
            AiDebugLog.Write($"[AI][V2]   threat: contacts {th.Contacts.Count} (honest={honest} cheat={cheat}) "
                + $"assets {th.Assets.Count} listedThreats {th.Threats.Count} siege={(th.UnderSiege ? 1 : 0)}");
            foreach (AssetThreatSnapshot t in th.Threats.OrderByDescending(x => x.Severity).Take(6))
                AiDebugLog.Write($"[AI][V2]     THREAT sev={F(t.Severity)} asset={t.Asset.Kind}@{t.Asset.Hex.Q},{t.Asset.Hex.R} "
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
            };
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
            int visited = 0, visible = 0;
            foreach (HexCoord c in map.AllCoords)
            {
                all.Add(c);
                if (VisionSystem.IsVisited(player, c)) { visited++; visitedSet.Add(c); }
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
            };
        }

        private static EconomyStanding BuildEconomy(PlayerSetupData player, AiTurnContext ctx, WorldSnapshot snap)
        {
            var eco = new EconomyStanding();
            var perType = new List<EconomyResourceStanding>();

            List<PlayerSetupData> others = (GameSession.Players ?? new List<PlayerSetupData>())
                .Where(p => p != null && p != player && !p.IsNeutral && !p.IsEliminated)
                .ToList();

            float worstRatio = float.MaxValue;
            float relAccum = 0f;
            foreach (ResourceType t in ResourceBundle.All)
            {
                float own = snap.Self.PerTurnIncome.Get(t);
                var otherIncomes = others.Select(p => (float)IncomeProjection.IncomeFor(p, t, ctx.Map)).ToList();
                float median = Median(otherIncomes);
                float ratio = own / Mathf.Max(1f, median);
                perType.Add(new EconomyResourceStanding
                {
                    Type = t, OwnIncome = own, FieldMedianIncome = median, Ratio = ratio,
                });
                worstRatio = Mathf.Min(worstRatio, ratio);
                float d = ratio - 1f;
                relAccum += d / (1f + Mathf.Abs(d));
            }
            eco.PerType = perType;
            eco.RelativePressure = Mathf.Clamp(relAccum / ResourceBundle.All.Length, -1f, 1f);
            eco.BottleneckPressure = Mathf.Clamp01(1f - (worstRatio == float.MaxValue ? 1f : worstRatio));

            // ResourceBundle is a struct. These helpers MUST receive it by ref; passing by value
            // silently accumulated into a copy and left DeckResourceNeed at 0/0/0/0 every turn.
            var need = new ResourceBundle();
            AccumulateCardCosts(snap.Self.Hand, ref need);
            AccumulateCardCosts(snap.Self.Deck, ref need);
            eco.DeckResourceNeed = need;

            // Do NOT treat the whole remaining deck as something income must repay inside a fixed
            // three-turn window. That made a normal opening deck (e.g. 67 total resource points)
            // report a nonsensical ~22 income/turn target. The sustainable target is per resource:
            //   1) what one typical remaining playable card asks for, and
            //   2) what the opponent field currently earns.
            // We need to keep pace with the larger of those two signals. Existing stockpile is a
            // runway buffer for SECURITY, but never inflates/deflates the target itself.
            int remainingPlayableCards = snap.Self.Hand.Count(card =>
                    card?.Definition != null
                    && (card.Definition.cardType == CardType.Unit || card.Definition.cardType == CardType.Hero
                        || card.Definition.cardType == CardType.Facility || card.Definition.cardType == CardType.Base))
                + snap.Self.Deck.Count(d => d != null
                    && (d.cardType == CardType.Unit || d.cardType == CardType.Hero
                        || d.cardType == CardType.Facility || d.cardType == CardType.Base));
            float cadenceDenom = Mathf.Max(1f, remainingPlayableCards);
            float runwayTurns = Mathf.Max(1f, AiConfigV2.economyDeckNeedHorizonTurns);
            var incomeTarget = new ResourceBundle();
            float coverageAccum = 0f;
            float worstCoverage = 1f;
            foreach (EconomyResourceStanding rs in perType)
            {
                float cardCadence = need.Get(rs.Type) / cadenceDenom;
                float target = Mathf.Max(cardCadence, rs.FieldMedianIncome);
                incomeTarget.Add(rs.Type, target);

                float smoothCoverage = 1f;
                if (target > 0.0001f)
                {
                    float stockRunwayPerTurn = snap.Self.Stockpile.Get(rs.Type) / runwayTurns;
                    float effectiveSupply = rs.OwnIncome + Mathf.Min(target, stockRunwayPerTurn);
                    float coverage = Mathf.Clamp01(effectiveSupply / target);
                    smoothCoverage = Mathf.SmoothStep(0f, 1f, coverage);
                }
                coverageAccum += smoothCoverage;
                worstCoverage = Mathf.Min(worstCoverage, smoothCoverage);
            }
            eco.IncomeTarget = incomeTarget;
            float meanCoverage = coverageAccum / Mathf.Max(1, ResourceBundle.All.Length);
            eco.AbsFloor = Mathf.Clamp01(0.65f * meanCoverage + 0.35f * worstCoverage);

            float relTerm = (eco.RelativePressure + 1f) * 0.5f;
            float wSum = AiConfigV2.economySecurityAbsWeight + AiConfigV2.economySecurityRelWeight
                       + AiConfigV2.economySecurityBottleneckWeight;
            eco.EconomicSecurity = Mathf.Clamp01((
                AiConfigV2.economySecurityAbsWeight * eco.AbsFloor
                + AiConfigV2.economySecurityRelWeight * relTerm
                + AiConfigV2.economySecurityBottleneckWeight * (1f - eco.BottleneckPressure)) / Mathf.Max(0.0001f, wSum));

            return eco;
        }

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
                    bool canDamage = ProfilesCanDamageAll(c.Army.Members, asset.Defenders, asset.HexDefenseBonus);
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

        private static bool ProfilesCanDamageAll(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, float extraDefense)
        {
            if (defenders == null || defenders.Count == 0) return true;
            if (attackers == null || attackers.Count == 0) return false;
            foreach (WorthIt.DefenderProfile def in defenders)
            {
                bool covered = false;
                foreach (WorthIt.DefenderProfile atk in attackers)
                    if (WorthIt.CanDamage(atk.Attack, def, extraDefense))
                    {
                        covered = true;
                        break;
                    }
                if (!covered) return false;
            }
            return true;
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
