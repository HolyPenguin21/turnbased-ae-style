#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Characterization of StrategicPhaseA.FulfillDemands, written before splitting the method into
    // stages (2026-10-24). Each test pins what the pass does TODAY on a small world with an empty
    // hand: which demands are deferred, held, built, carried as residual, and what the carried
    // reservation and AP budget look like afterwards. Tests marked "current behaviour" pin a
    // finding the owner has not ruled on yet; change them together with that decision.
    // PlayerRoot.Create needs the native engine, so these run in Unity only.
    public class AiPhaseAFulfillDemandsCharacterizationTests
    {
        private PlayerSetupData _player;
        private PlayerRoot _root;
        private AiHandData _hand;

        [SetUp]
        public void SetUp()
        {
            _player = new PlayerSetupData();
            _root = PlayerRoot.Create(_player, "phase A characterization");
            _root.ActionPoints = 6;
            _root.AddResource(ResourceType.Materials, 5);
            _hand = new AiHandData(null, default, 0);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_root.gameObject);
            MissionIntentRegistry.Clear();
            StrategicResourceReservationLedger.ClearAll();
        }

        private WorldSnapshot Snap(int turn) => new WorldSnapshot
        {
            Observer = _player, TurnNumber = turn,
            Self = new SelfSnapshot { Armies = System.Array.Empty<ArmySnapshot>(),
                BaseHexes = System.Array.Empty<HexCoord>(), Hand = _hand.Hand },
        };

        private StrategicPhaseResult Run(int turn, PhaseAApBudget budget, AxisDemand[] demands,
            MissionIntent[] active = null, MaterializationReservation carried = null,
            bool authoritative = true, Radar radar = null, bool deferCold = false) =>
            StrategicPhaseA.FulfillDemands(Snap(turn), _player, _root, _hand,
                new AiTurnContext { TurnNumber = turn }, budget, demands, new ActorCommitments(),
                MissionIntentRegistry.GetOrCreate(_player), active,
                new System.Collections.Generic.List<ReconObjective>(), carried, authoritative, radar,
                deferCold);

        private static Radar RadarOf(DesireAxis axis, float weight)
        {
            var r = new Radar();
            foreach (DesireAxis a in DesireAxes.All)
                r.Weight[a] = 1f;
            r.Weight[axis] = weight;
            return r;
        }

        private static CardData Card(CardType type) => new CardData(new CardDefinition { cardType = type });

        private static AxisDemand Extraction(HexCoord target, CardData card = null) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy, Capability = CapabilityKind.EconomicInfrastructure,
            TargetHex = target, EconomyResourceType = ResourceType.Materials, EconomyBuildCard = card,
            EconomyBuildResourceCost = new ResourceCost(materials: 2), DesiredAmount = 1, Value = 5f,
        };

        private static AxisDemand Scout(HexCoord target, float amount = 1f, bool deferred = false) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Recon, Capability = CapabilityKind.ScoutCapability,
            TargetHex = target, DesiredAmount = amount, Value = 3f, IsPersistenceDeferred = deferred,
        };

        private static AxisDemand Field(HexCoord target, bool refit = false) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Aggression, Capability = CapabilityKind.FieldCombatPower,
            TargetHex = target, DesiredAmount = 3, Value = 4f, AttackLocalRefit = refit,
        };

        private static AxisDemand BaseHero() => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy, Capability = CapabilityKind.Hero,
            TargetHex = new HexCoord(5, -2), DesiredAmount = 1, Value = 6f,
            EconomyBuildResourceCost = new ResourceCost(materials: 3),
        };

        private float Held(int turn, StrategicReservedResource res) =>
            StrategicResourceReservationLedger.Active(_player, turn, res);

        [Test]
        public void MissingRoot_ReturnsTheCarriedReservationUntouched()
        {
            var carried = new MaterializationReservation();
            carried.UnresolvedDemands.Add(Scout(new HexCoord(1, 1)));
            StrategicPhaseResult r = StrategicPhaseA.FulfillDemands(null, _player, null, _hand,
                new AiTurnContext { TurnNumber = 3 }, null, null, null, null, carriedReservation: carried);
            Assert.That(r.Reservation, Is.SameAs(carried));
            Assert.That(r.Reservation.UnresolvedDemands, Has.Count.EqualTo(1));
            Assert.That(r.StateChanged, Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void NoDemands_ClearsThePreIntentHoldOnlyWithEconomysAuthoritativeView(bool authoritative)
        {
            EconomyReservationLifecycle.ReserveDeferredEconomyResourcesForPendingHero(_player, 10, BaseHero());
            Assert.That(Held(10, StrategicReservedResource.Materials), Is.EqualTo(3f));

            Run(10, PhaseAApBudget.Create(_root), System.Array.Empty<AxisDemand>(),
                authoritative: authoritative);

            Assert.That(Held(10, StrategicReservedResource.Materials), Is.EqualTo(authoritative ? 0f : 3f));
        }

        [Test]
        public void NoDemands_KeepsAnEarlierEntrysResidual_CurrentBehaviour()
        {
            // Current behaviour (finding: the early return skips the residual rewrite every other
            // exit performs, so a satisfied need from an earlier entry stays a residual claim).
            var carried = new MaterializationReservation();
            carried.UnresolvedDemands.Add(Scout(new HexCoord(1, 1)));
            StrategicPhaseResult r = Run(10, PhaseAApBudget.Create(_root), System.Array.Empty<AxisDemand>(),
                carried: carried);
            Assert.That(r.Reservation.UnresolvedDemands, Has.Count.EqualTo(1));
        }

        [Test]
        public void FreshZeroRadarDemand_IsDeferredAsAResidualCloneAndItsCardClaimed()
        {
            CardData mine = Card(CardType.Facility);
            AxisDemand cold = Extraction(new HexCoord(2, 1), mine);
            var carried = new MaterializationReservation();
            carried.UnresolvedDemands.Add(Scout(new HexCoord(1, 1)));

            StrategicPhaseResult r = Run(11, PhaseAApBudget.Create(_root), new[] { cold }, carried: carried,
                radar: RadarOf(DesireAxis.Economy, 0f), deferCold: true);

            Assert.That(r.InfrastructureAttempts, Is.Zero, "a deferred demand is not attempted");
            Assert.That(r.Reservation.UnresolvedDemands, Has.Count.EqualTo(1), "the earlier residual is replaced");
            AxisDemand residual = r.Reservation.UnresolvedDemands[0];
            Assert.That(residual, Is.Not.SameAs(cold));
            Assert.That(residual.Capability, Is.EqualTo(CapabilityKind.EconomicInfrastructure));
            Assert.That(residual.DesiredAmount, Is.EqualTo(1f));
            Assert.That(r.Reservation.ClaimedEconomyBuildCards, Does.Contain(mine));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ActiveExtraction_IsProtectedBeforeCardArbitration(bool authoritative)
        {
            CardData mine = Card(CardType.Facility);
            MissionIntent intent = EconomyContinuity.BeginEconomyDelivery(_player,
                Extraction(new HexCoord(3, -5), mine), 25, 12);

            StrategicPhaseResult r = Run(12, PhaseAApBudget.Create(_root), System.Array.Empty<AxisDemand>(),
                new[] { intent }, authoritative: authoritative);

            Assert.That(r.StateChanged, Is.False);
            Assert.That(r.Reservation.ClaimedEconomyBuildCards, Does.Contain(mine));
            Assert.That(intent.LastProtectedTurn, Is.EqualTo(12));
            Assert.That(Held(12, StrategicReservedResource.Materials), Is.EqualTo(2f));
        }

        [Test]
        public void NoChain_ResidualsKeepRemainingAmount_DeferredStaysDeferred_RefitIsDropped()
        {
            AxisDemand scout = Scout(new HexCoord(4, 4));
            AxisDemand deferred = Scout(new HexCoord(5, 4), amount: 2f, deferred: true);
            AxisDemand field = Field(new HexCoord(6, 4));
            AxisDemand refit = Field(new HexCoord(7, 4), refit: true);
            var zero = new AxisDemand { RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower, DesiredAmount = 0, Value = 9f };
            PhaseAApBudget budget = PhaseAApBudget.Create(_root);
            budget.ReserveActorFollowup(777, 3f);

            StrategicPhaseResult r = Run(13, budget, new[] { scout, null, deferred, field, refit, zero });

            Assert.That(budget.ActorFollowupIds, Is.Empty, "a refit promise for a missing army is dropped on entry");
            Assert.That(r.CardsPlayed, Is.Zero);
            Assert.That(r.Reservation.UnresolvedDemands.Select(d => (d.Capability, d.DesiredAmount,
                    d.IsPersistenceDeferred, d.StructurallyUndeliverable)),
                Is.EqualTo(new[]
                {
                    (CapabilityKind.ScoutCapability, 1f, false, true),
                    (CapabilityKind.FieldCombatPower, 3f, false, true),
                    (CapabilityKind.ScoutCapability, 2f, true, false),
                }));
            Assert.That(deferred.IsPersistenceDeferred, Is.True, "no deliverable candidate, no promotion");
            Assert.That(scout.StructurallyUndeliverable, Is.True, "the verdict is written on the input demand");
        }

        [Test]
        public void EconomyBuildAndOtherInfrastructure_RunInThePrePassOnce()
        {
            CardData mine = Card(CardType.Facility);
            AxisDemand build = Extraction(new HexCoord(2, -1), mine);
            var guard = new AxisDemand { RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.EconomicInfrastructure, TargetHex = new HexCoord(0, 0),
                DesiredAmount = 1, Value = 2f };

            StrategicPhaseResult r = Run(14, PhaseAApBudget.Create(_root), new[] { build, guard });

            Assert.That(r.InfrastructureAttempts, Is.EqualTo(2));
            Assert.That(r.InfrastructureBuilt, Is.Zero);
            Assert.That(r.Reservation.UnresolvedDemands, Has.Count.EqualTo(2));
            Assert.That(r.Reservation.ClaimedEconomyBuildCards, Does.Contain(mine));
        }

        [Test]
        public void DevelopmentAndGlobalSourceCompete_ZeroRadarInfrastructureRunsAfterCards()
        {
            var dev = new AxisDemand { RequestingAxis = DesireAxis.Development,
                Capability = CapabilityKind.DevelopmentInfrastructure, TargetHex = new HexCoord(1, 1),
                DesiredAmount = 1, Value = 4f };
            var global = new AxisDemand { RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.GlobalResourceFacility, DesiredAmount = 1, Value = 3f };
            var cold = new AxisDemand { RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.EconomicInfrastructure, TargetHex = new HexCoord(0, 1),
                DesiredAmount = 1, Value = 2f };

            StrategicPhaseResult r = Run(15, PhaseAApBudget.Create(_root), new[] { dev, global, cold },
                radar: RadarOf(DesireAxis.Aggression, 0f));

            // Arbitrated infrastructure that cannot act is closed by the proposal without a counted
            // attempt; only the zero-Radar residual reaches the build executor.
            Assert.That(r.InfrastructureAttempts, Is.EqualTo(1));
            Assert.That(r.Reservation.UnresolvedDemands.Select(d => d.Capability), Is.EqualTo(new[]
            {
                CapabilityKind.DevelopmentInfrastructure, CapabilityKind.GlobalResourceFacility,
                CapabilityKind.EconomicInfrastructure,
            }));
        }

        [Test]
        public void Reentry_KeepsBuildCardClaimsButRewritesTheResidualPool_CurrentBehaviour()
        {
            // Current behaviour (finding: a dirty-axis re-entry replaces the whole residual pool,
            // so the other axes' residuals from the first entry are gone).
            CardData mine = Card(CardType.Facility);
            PhaseAApBudget budget = PhaseAApBudget.Create(_root);
            StrategicPhaseResult first = Run(16, budget, new[] { Extraction(new HexCoord(2, -1), mine),
                Scout(new HexCoord(4, 4)) });
            Assert.That(first.Reservation.UnresolvedDemands, Has.Count.EqualTo(2));

            StrategicPhaseResult second = Run(16, budget, new[] { Field(new HexCoord(6, 4)) },
                carried: first.Reservation, authoritative: false, deferCold: true);

            Assert.That(second.Reservation, Is.SameAs(first.Reservation));
            Assert.That(second.Reservation.UnresolvedDemands.Select(d => d.Capability),
                Is.EqualTo(new[] { CapabilityKind.FieldCombatPower }));
            Assert.That(second.Reservation.ClaimedEconomyBuildCards, Does.Contain(mine));
        }

        [Test]
        public void UndeliverableBasePrerequisite_DropsItsHoldButKeepsTheCardClaim()
        {
            CardData outpost = Card(CardType.Base);
            AxisDemand hero = BaseHero();
            hero.EconomyBuildCard = outpost;
            EconomyReservationLifecycle.ReserveDeferredEconomyResourcesForPendingHero(_player, 20, hero);

            StrategicPhaseResult r = Run(20, PhaseAApBudget.Create(_root), new[] { hero });

            Assert.That(Held(20, StrategicReservedResource.Materials), Is.Zero);
            Assert.That(r.Reservation.ClaimedEconomyBuildCards, Does.Contain(outpost));
            Assert.That(r.Reservation.UnresolvedDemands.Single().StructurallyUndeliverable, Is.True);
        }
    }
}
#endif
