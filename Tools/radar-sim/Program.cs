using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;

namespace RadarSim
{
    // Acceptance harness for Strategy V2 build-order step 3 (the desire evaluators). Hand-builds
    // WorldSnapshot POCOs for curated positions and asserts the normalised Radar tilts the way the
    // spec's "done when" says it should, plus a handful of DesireBreakdown sub-term checks. The
    // real evaluator code runs (StrategyLayer -> CombatOpportunityAnalyzer -> AiPower / WorthIt);
    // only the model layer is touched (no MonoBehaviour / scene / coroutine). Numbers are
    // first-pass — the harness pins BEHAVIOUR (RCN > AGG under fog; AGG > RCN with a beatable
    // target; siege damps AGG; ...), not exact magnitudes.
    internal static class Program
    {
        private static int _passed;
        private static int _failed;
        private static int _armyId = 1;

        private static int Main()
        {
            Scenario01_FogAtStart_ReconLeads();
            Scenario02_WeakNeutralNearby_AggressionLeads_ViaRaidOpportunity();
            Scenario03_TenUnbeatableNeutrals_NoOpportunity();
            Scenario04_NearPotentialCeiling_WarPressureLeads();
            Scenario05_UnderSiege_AggressionDamped();
            Scenario06_ObservedEnemyLoss_MomentumUp();
            Scenario07_OwnLoss_MomentumDown();
            Scenario08_ThreatEatsReserve_SurplusDrops();
            Scenario09_NoEnemyIntel_RelativeEdgeNeutral();
            Scenario10_MapFullyExplored_ReconFallsToFloor();

            Console.WriteLine();
            Console.WriteLine($"radar-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ scenarios ----

        private static void Scenario01_FogAtStart_ReconLeads()
        {
            WorldSnapshot s = BaseSnap(turn: 2);
            s.MapKnowledge.UnknownFrac = 0.90f;
            s.MapKnowledge.ExplorableUnknownFrac = 0.90f;
            s.Self.TotalPower = 3f; s.Self.FieldPower = 0f;
            s.Self.BestStackPotential = 2f; s.Self.TotalMilitaryPotential = 6f;
            // opponents are known to exist (game setup) but we have no honest sighting -> blindness
            s.TrueWorld.Opponents = new List<OpponentSnapshot>
            {
                new OpponentSnapshot { Player = P("A"), ArmyCount = 2, ArmyPower = 20f },
                new OpponentSnapshot { Player = P("B"), ArmyCount = 1, ArmyPower = 10f },
            };

            RadarAssessment a = Eval(s);
            Dump("01", a);
            Check("01 fog at start -> Recon is the dominant axis",
                IsMaxAxis(a, DesireAxis.Recon)
                && a.Radar.Weight[DesireAxis.Recon] > a.Radar.Weight[DesireAxis.Aggression]
                && a.Breakdown.ReconExploration > 0.9f
                && a.Breakdown.ReconEnemyBlindness > 0.5f);
        }

        private static void Scenario02_WeakNeutralNearby_AggressionLeads_ViaRaidOpportunity()
        {
            RadarAssessment a = Eval(WeakNeutralNearbyPosition(underSiege: false));
            Dump("02", a);
            Check("02 strong army + weak neutral nearby -> Aggression leads, driven by raidOpportunity",
                IsMaxAxis(a, DesireAxis.Aggression)
                && a.Radar.Weight[DesireAxis.Aggression] > a.Radar.Weight[DesireAxis.Recon]
                && a.Breakdown.AggRaidOpportunity > a.Breakdown.AggWarPressure
                && a.Breakdown.BestOpportunity.GatePassed
                && a.Breakdown.AggOpportunity > 0.2f);
        }

        private static void Scenario03_TenUnbeatableNeutrals_NoOpportunity()
        {
            WorldSnapshot s = BaseSnap(turn: 12);
            s.MapKnowledge.UnknownFrac = 0.2f;
            s.MapKnowledge.ExplorableUnknownFrac = 0.2f;
            s.Self.TotalPower = 30f; s.Self.FieldPower = 25f;
            s.Self.BestStackPotential = 25f; s.Self.TotalMilitaryPotential = 50f;
            s.Self.Armies = new List<ArmySnapshot> { Army(H(0, 0), P("ME"), garrison: false, heroCmd: 4,
                Prof(6, 4, 5), Prof(6, 4, 5), Prof(6, 4, 5)) };
            PlayerSetupData wild = P("WILD", neutral: true);
            var sightings = new List<AiMapMemory.KnownEnemySighting>();
            for (int i = 0; i < 10; i++)
                sightings.Add(Sighting(H(3 + i, 1), wild, Prof(2, 25, 12))); // brick walls — CanDamage fails
            s.Known.NeutralSightings = sightings;

            RadarAssessment a = Eval(s);
            // The same army/position with ONE weak beatable neutral instead, for contrast.
            RadarAssessment beatable = Eval(WeakNeutralNearbyPosition(underSiege: false));
            Dump("03", a);
            // Invariant: unbeatable targets contribute NOTHING through the opportunity channel.
            // raidOpportunity may still be non-zero here (a big idle army legitimately leans toward
            // war via surplus), but it must be strictly below the beatable-target case, and the
            // opportunity sub-term and the viability gate must be flat zero / false.
            Check("03 ten unbeatable neutrals -> zero opportunity, gate never passes, raidOpportunity below the beatable case",
                a.Breakdown.AggOpportunity == 0f
                && !a.Breakdown.BestOpportunity.GatePassed
                && a.Breakdown.AggRaidOpportunity < beatable.Breakdown.AggRaidOpportunity - 0.1f);
        }

        private static void Scenario04_NearPotentialCeiling_WarPressureLeads()
        {
            WorldSnapshot s = BaseSnap(turn: 20);
            s.MapKnowledge.UnknownFrac = 0.16f;               // map basically open -> low exploration
            s.MapKnowledge.ExplorableUnknownFrac = 0.16f;
            s.Self.TotalPower = 46f; s.Self.FieldPower = 40f;
            s.Self.BestStackPotential = 45f; s.Self.TotalMilitaryPotential = 50f; // ratio 0.9 -> saturated
            s.Economy.EconomicSecurity = 0.8f;
            // no known targets at all -> raidOpportunity has nothing to grab

            RadarAssessment a = Eval(s);
            Dump("04", a);
            Check("04 near military-potential ceiling, calm -> Aggression via warPressure, not raidOpportunity",
                a.Breakdown.AggWarPressure > a.Breakdown.AggRaidOpportunity
                && a.Breakdown.AggPotentialSaturation > 0.6f
                && a.Radar.Weight[DesireAxis.Aggression] > a.Radar.Weight[DesireAxis.Recon]);
        }

        private static void Scenario05_UnderSiege_AggressionDamped()
        {
            RadarAssessment open = Eval(WeakNeutralNearbyPosition(underSiege: false));
            RadarAssessment siege = Eval(WeakNeutralNearbyPosition(underSiege: true));
            Dump("05a open ", open);
            Dump("05b siege", siege);
            Check("05 under siege -> the same beatable target no longer lifts Aggression",
                siege.Desires.Raw[DesireAxis.Aggression] < open.Desires.Raw[DesireAxis.Aggression]
                && siege.Desires.Raw[DesireAxis.Aggression] < 0.2f
                && siege.Desires.MilitaryThreat >= 0.9f);
        }

        private static void Scenario06_ObservedEnemyLoss_MomentumUp()
        {
            var state = new AiRadarState();
            PlayerSetupData enemy = P("ENEMY");

            WorldSnapshot t1 = BaseSnap(turn: 5);
            t1.Threat.Contacts = new List<EnemyContactSnapshot> { Contact(H(3, 3), enemy, power: 20f) };
            StrategyLayer.Evaluate(t1, state);

            WorldSnapshot t2 = BaseSnap(turn: 6);
            t2.Threat.Contacts = new List<EnemyContactSnapshot> { Contact(H(3, 3), enemy, power: 8f) }; // lost a strong unit
            RadarAssessment a2 = StrategyLayer.Evaluate(t2, state);
            Dump("06", a2);
            Check("06 an observed enemy contact getting weaker -> momentum well above neutral",
                a2.Breakdown.AggMomentum > 0.9f);
        }

        private static void Scenario07_OwnLoss_MomentumDown()
        {
            var state = new AiRadarState();

            WorldSnapshot t1 = BaseSnap(turn: 5);
            t1.Self.TotalPower = 40f;
            StrategyLayer.Evaluate(t1, state);

            WorldSnapshot t2 = BaseSnap(turn: 6);
            t2.Self.TotalPower = 15f; // battle went badly
            RadarAssessment a2 = StrategyLayer.Evaluate(t2, state);
            Dump("07", a2);
            Check("07 losing a big chunk of our own army -> momentum well below neutral",
                a2.Breakdown.AggMomentum < 0.1f);
        }

        private static void Scenario08_ThreatEatsReserve_SurplusDrops()
        {
            WorldSnapshot calm = BaseSnap(turn: 15);
            calm.Self.TotalPower = 60f; calm.Self.FieldPower = 45f;

            WorldSnapshot pressed = BaseSnap(turn: 15);
            pressed.Self.TotalPower = 60f; pressed.Self.FieldPower = 45f;
            pressed.Threat.Threats = new List<AssetThreatSnapshot>
            {
                ThreatOn(AssetKind.Base, H(0, 0), contactPower: 40f, severity: 0.6f),
            };

            RadarAssessment ca = Eval(calm);
            RadarAssessment pa = Eval(pressed);
            Dump("08a calm   ", ca);
            Dump("08b pressed", pa);
            Check("08 a real threat on a base consumes the defensive reserve -> surplus collapses",
                pa.Breakdown.RequiredDefensiveReserve > 45f            // ~40 * 1.3
                && pa.Breakdown.OffensiveFreePower < ca.Breakdown.OffensiveFreePower
                && pa.Breakdown.AggSurplus < ca.Breakdown.AggSurplus
                && pa.Breakdown.AggSurplus < 0.2f
                && pa.Desires.MilitaryThreat >= 0.55f);
        }

        private static void Scenario09_NoEnemyIntel_RelativeEdgeNeutral()
        {
            WorldSnapshot s = BaseSnap(turn: 8);
            s.Self.TotalPower = 40f; s.Self.FieldPower = 40f; s.Self.BestStackPotential = 30f;
            s.Known.EnemyKnownStrength = 0f; // never seen them

            RadarAssessment a = Eval(s);
            Dump("09", a);
            Check("09 no enemy intel -> relativeEdge sits at neutral 0.5, not maxed",
                Math.Abs(a.Breakdown.AggRelativeEdge - 0.5f) < 0.0001f);
        }

        private static void Scenario10_MapFullyExplored_ReconFallsToFloor()
        {
            WorldSnapshot s = BaseSnap(turn: 30);
            // Dark hexes remain, but none are reachable on foot (all behind hostile ground) — so
            // the explorable measure is flat zero even though UnknownFrac is not.
            s.MapKnowledge.UnknownFrac = 0.15f;
            s.MapKnowledge.ExplorableUnknownFrac = 0f;
            s.Self.TotalPower = 20f;

            RadarAssessment a = Eval(s);
            Dump("10", a);
            Check("10 nothing reachable left to scout -> exploration ~0, Recon held up only by surveillance",
                a.Breakdown.ReconExploration < 0.02f
                && a.Breakdown.ReconSurveillance > 0.16f && a.Breakdown.ReconSurveillance < 0.20f
                && a.Desires.Raw[DesireAxis.Recon] > 0.03f
                && a.Desires.Raw[DesireAxis.Recon] < 0.15f);
        }

        // ------------------------------------------------------------------ shared positions ----

        // Strong hero-led army at (0,0); one weak neutral two hexes away; map mostly known; calm
        // economy. The canonical "there is a cheap win right here" position.
        private static WorldSnapshot WeakNeutralNearbyPosition(bool underSiege)
        {
            WorldSnapshot s = BaseSnap(turn: 10);
            s.MapKnowledge.UnknownFrac = 0.20f;
            s.MapKnowledge.ExplorableUnknownFrac = 0.20f;
            s.Self.TotalPower = 30f; s.Self.FieldPower = 25f; s.Self.GarrisonPower = 5f;
            s.Self.BestStackPotential = 25f; s.Self.TotalMilitaryPotential = 50f;
            s.Self.Armies = new List<ArmySnapshot>
            {
                Army(H(0, 0), P("ME"), garrison: false, heroCmd: 4, Prof(6, 4, 5), Prof(6, 4, 5), Prof(6, 4, 5)),
            };
            s.Known.NeutralSightings = new List<AiMapMemory.KnownEnemySighting>
            {
                Sighting(H(2, 0), P("WILD", neutral: true), Prof(1, 1, 2)),
            };
            s.Threat.UnderSiege = underSiege;
            return s;
        }

        // ------------------------------------------------------------------ builders ----

        private static WorldSnapshot BaseSnap(int turn)
        {
            var self = new SelfSnapshot
            {
                Citadel = H(0, 0),
                BaseHexes = new List<HexCoord> { H(0, 0) },
                Armies = new List<ArmySnapshot>(),
                FieldPower = 0f, GarrisonPower = 0f, TotalPower = 0f,
                BestStackPotential = 0f, TotalMilitaryPotential = 1f,
                Stockpile = new ResourceBundle(), PerTurnIncome = new ResourceBundle(),
                ActionPoints = 6,
                Hand = new List<CardData>(), Deck = new List<CardDefinition>(),
                HandCapacity = 7, HasFreeHandSlot = true,
                HasDevFacility = false, HasDevOperator = false,
            };
            return new WorldSnapshot
            {
                TurnNumber = turn,
                Self = self,
                Known = new KnownSnapshot
                {
                    EnemySightings = new List<AiMapMemory.KnownEnemySighting>(),
                    NeutralSightings = new List<AiMapMemory.KnownEnemySighting>(),
                    Buildings = new List<AiMapMemory.KnownBuilding>(),
                    EventGuardHexes = new List<HexCoord>(),
                    ResourceHexes = new List<KeyValuePair<HexCoord, ResourceType>>(),
                    EnemyKnownStrength = 0f, NearestEnemyToBase = 99, EnemyStrengthNearBases = 0f,
                },
                TrueWorld = new TrueWorldSnapshot
                {
                    EnemyArmies = new List<ArmySnapshot>(),
                    NeutralArmies = new List<ArmySnapshot>(),
                    AllBuildings = new List<BuildingSnapshot>(),
                    Opponents = new List<OpponentSnapshot>(),
                },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    TotalHexes = 200, VisitedHexes = 100, VisibleHexes = 40, UnknownFrac = 0.5f,
                    ExplorableUnknownFrac = 0.5f,
                    Frontier = Array.Empty<FrontierHexSnapshot>(),
                },
                Economy = new EconomyStanding
                {
                    PerType = new List<EconomyResourceStanding>(),
                    DeckResourceNeed = new ResourceBundle(),
                    RelativePressure = 0f, BottleneckPressure = 0f, AbsFloor = 0.5f, EconomicSecurity = 0.5f,
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Assets = new List<StrategicAssetSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                    UnderSiege = false,
                },
            };
        }

        private static HexCoord H(int q, int r) => new HexCoord(q, r);

        private static PlayerSetupData P(string name, bool neutral = false) =>
            new PlayerSetupData { Nickname = name, IsNeutral = neutral, IsHuman = false };

        private static WorthIt.DefenderProfile Prof(float atk, float def, float hp, int init = 2) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, init);

        private static ArmySnapshot Army(HexCoord hex, PlayerSetupData owner, bool garrison, int heroCmd,
            params WorthIt.DefenderProfile[] members)
        {
            var list = members.ToList();
            return new ArmySnapshot
            {
                ArmyId = _armyId++,
                Owner = owner,
                Hex = hex,
                IsGarrison = garrison,
                IsPrison = false,
                IsAir = false,
                MemberCount = list.Count + (heroCmd > 0 ? 1 : 0),
                HasHero = heroCmd > 0,
                HeroCommandRating = heroCmd,
                HasAntiAir = false,
                IsHiddenFromUs = false,
                AttackSum = list.Sum(m => m.Attack),
                DefenseSum = list.Sum(m => m.Defense),
                EffectiveArmyPower = AiPower.EffectiveArmyPowerFromProfiles(list),
                CompositionQuality = 0.5f,
                MaxMovement = 2,
                Members = list,
            };
        }

        private static AiMapMemory.KnownEnemySighting Sighting(HexCoord hex, PlayerSetupData owner,
            params WorthIt.DefenderProfile[] defenders)
        {
            var list = defenders.ToList();
            return new AiMapMemory.KnownEnemySighting(hex, owner, owner?.Nickname, list.Count,
                list.Sum(d => d.Defense), list.Sum(d => d.Attack), list, false, 0, 0);
        }

        private static EnemyContactSnapshot Contact(HexCoord hex, PlayerSetupData owner, float power,
            ContactKnowledge knowledge = ContactKnowledge.Exact)
        {
            return new EnemyContactSnapshot
            {
                Army = new ArmySnapshot
                {
                    Owner = owner, Hex = hex, EffectiveArmyPower = power,
                    Members = new List<WorthIt.DefenderProfile>(),
                },
                Knowledge = knowledge,
                Source = ContactSource.Honest,
                Position = hex,
                Confidence = knowledge == ContactKnowledge.Exact
                    ? AiConfigV2.threatConfidenceExact : AiConfigV2.threatConfidenceLastKnown,
            };
        }

        private static AssetThreatSnapshot ThreatOn(AssetKind kind, HexCoord hex, float contactPower, float severity)
        {
            return new AssetThreatSnapshot
            {
                Asset = new StrategicAssetSnapshot
                {
                    Hex = hex, Kind = kind, Value = 60f, Defense = 5f, HexDefenseBonus = 2f,
                    Defenders = new List<WorthIt.DefenderProfile>(),
                },
                Contact = new EnemyContactSnapshot
                {
                    Army = new ArmySnapshot
                    {
                        Owner = P("RAIDER"), Hex = hex, EffectiveArmyPower = contactPower,
                        Members = new List<WorthIt.DefenderProfile>(),
                    },
                    Knowledge = ContactKnowledge.Region,
                    Source = ContactSource.Cheat,
                    Confidence = AiConfigV2.threatConfidenceCheatRegion,
                },
                CanDamage = true,
                AttackWinChance = 0.6f,
                PotentialDamage = 0.5f,
                Confidence = AiConfigV2.threatConfidenceCheatRegion,
                Severity = severity,
            };
        }

        // ------------------------------------------------------------------ plumbing ----

        private static RadarAssessment Eval(WorldSnapshot s) => StrategyLayer.Evaluate(s, new AiRadarState());

        private static bool IsMaxAxis(RadarAssessment a, DesireAxis axis)
        {
            float w = a.Radar.Weight[axis];
            foreach (DesireAxis x in DesireAxes.All)
                if (x != axis && a.Radar.Weight[x] > w)
                    return false;
            return true;
        }

        private static void Dump(string tag, RadarAssessment a)
        {
            Console.WriteLine($"  [{tag}] radar {a.Radar.DebugLine()}  "
                + $"| RCN raw expl={F(a.Breakdown.ReconExploration)} surv={F(a.Breakdown.ReconSurveillance)} "
                + $"blind={F(a.Breakdown.ReconEnemyBlindness)}  "
                + $"| AGG raid={F(a.Breakdown.AggRaidOpportunity)} war={F(a.Breakdown.AggWarPressure)} "
                + $"(opp={F(a.Breakdown.AggOpportunity)} surp={F(a.Breakdown.AggSurplus)} edge={F(a.Breakdown.AggRelativeEdge)} "
                + $"sat={F(a.Breakdown.AggPotentialSaturation)} mom={F(a.Breakdown.AggMomentum)})  "
                + $"| reserve={F(a.Breakdown.RequiredDefensiveReserve)} free={F(a.Breakdown.OffensiveFreePower)} "
                + $"| threat={F(a.Desires.MilitaryThreat)} runway={F(a.Desires.EconomicRunway)}");
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
            if (ok) _passed++; else _failed++;
        }
    }
}
