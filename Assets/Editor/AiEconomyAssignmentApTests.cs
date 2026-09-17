#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Task 2 regression coverage: DemandLayer.EstimateEconomyAssignmentAp() must never drop a
    // future re-activation it hasn't actually already paid for. The bug only manifested when
    // CurrentMovement == 0 (this turn's activation is fully spent with nothing left for the
    // route), so the "already paid" discount must key off whether MP is actually available for
    // the first leg, not merely off HasActivatedThisTurn.
    public class AiEconomyAssignmentApTests
    {
        private static EconomyBuilderRouteSnapshot Route(int travelCost, int currentMovement,
            int maxMovement, bool hasActivatedThisTurn, int activationApCost = 4,
            int returnTravelCost = 0, bool isOnTarget = false) => new EconomyBuilderRouteSnapshot
        {
            ArmyId = 1,
            TravelCost = travelCost,
            ReturnTravelCost = returnTravelCost,
            CurrentMovement = currentMovement,
            MaxMovement = maxMovement,
            ActivationApCost = activationApCost,
            HasActivatedThisTurn = hasActivatedThisTurn,
            IsOnTarget = isOnTarget,
        };

        [Test]
        public void ZeroCurrentMovement_KeepsTheFutureReactivationInThePrice()
        {
            // Owner's game example: builder already activated, 0/3 MP, 3 hexes from the site;
            // activation 4 AP, build card 2 AP, no return leg. Before the fix this priced at 2
            // (the future activation vanished); the correct price is 2 (card) + 4 (future
            // activation next turn) = 6.
            var route = Route(travelCost: 3, currentMovement: 0, maxMovement: 3,
                hasActivatedThisTurn: true, activationApCost: 4);
            float cost = DemandLayer.EstimateEconomyAssignmentAp(route, buildApCost: 2f,
                includeReturn: false);
            Assert.That(cost, Is.EqualTo(6f));
        }

        [Test]
        public void PartiallySpentMovement_StillChargesTheSameWayAsFullyDepleted()
        {
            // 1/3 MP left (currentTurnAlreadyProgressesRoute == true, so the discount branch IS
            // taken) vs. 0/3 MP (discount branch skipped entirely): both still land on exactly one
            // future activation once ceiling rounding is applied (0/3: ceil(3/3)=1 turn, no
            // discount; 1/3: 1+ceil((3-1)/3)=2 turns, discount removes the already-paid one -> 1),
            // so the two paths must agree on the final price even though they take different
            // branches to get there.
            var depleted = Route(travelCost: 3, currentMovement: 0, maxMovement: 3,
                hasActivatedThisTurn: true, activationApCost: 4);
            var partially = Route(travelCost: 3, currentMovement: 1, maxMovement: 3,
                hasActivatedThisTurn: true, activationApCost: 4);
            float depletedCost = DemandLayer.EstimateEconomyAssignmentAp(depleted, 2f, false);
            float partiallyCost = DemandLayer.EstimateEconomyAssignmentAp(partially, 2f, false);
            Assert.That(partiallyCost, Is.EqualTo(depletedCost));
        }

        [Test]
        public void SufficientMovementThisTurn_StillDiscountsTheAlreadyPaidActivation()
        {
            // 2/3 MP, 3 hexes away, already activated this turn: this turn's activation genuinely
            // buys progress on the route, so the existing discount must still apply.
            // outboundTurns = 1 + ceil((3-2)/3) = 2; one of those turns is already paid.
            var route = Route(travelCost: 3, currentMovement: 2, maxMovement: 3,
                hasActivatedThisTurn: true, activationApCost: 4);
            float cost = DemandLayer.EstimateEconomyAssignmentAp(route, buildApCost: 2f,
                includeReturn: false);
            Assert.That(cost, Is.EqualTo(2f + 1 * 4f));
        }

        [Test]
        public void ArrivesThisTurn_NoFutureActivationCharged()
        {
            // 3/3 MP, 3 hexes away: arrives this turn, no re-activation needed regardless of
            // HasActivatedThisTurn.
            var route = Route(travelCost: 3, currentMovement: 3, maxMovement: 3,
                hasActivatedThisTurn: true, activationApCost: 4);
            float cost = DemandLayer.EstimateEconomyAssignmentAp(route, buildApCost: 2f,
                includeReturn: false);
            Assert.That(cost, Is.EqualTo(2f));
        }

        [Test]
        public void NotYetActivatedThisTurn_ChargesTheComingActivationInFull()
        {
            // Fresh mover (e.g. a hero not yet activated), full MP, but still 1 hex short of
            // arriving in a single turn: HasActivatedThisTurn == false, so no discount applies —
            // this turn's own activation still has to be paid.
            var route = Route(travelCost: 4, currentMovement: 3, maxMovement: 3,
                hasActivatedThisTurn: false, activationApCost: 4);
            float cost = DemandLayer.EstimateEconomyAssignmentAp(route, buildApCost: 2f,
                includeReturn: false);
            // outboundTurns = 1 + ceil((4-3)/3) = 2, no discount -> 2 activations.
            Assert.That(cost, Is.EqualTo(2f + 2 * 4f));
        }

        [Test]
        public void RouteWithReturnLeg_StillChargesTheZeroMovementOutboundCorrectly()
        {
            var route = Route(travelCost: 3, currentMovement: 0, maxMovement: 3,
                hasActivatedThisTurn: true, activationApCost: 4, returnTravelCost: 3);
            float cost = DemandLayer.EstimateEconomyAssignmentAp(route, buildApCost: 2f,
                includeReturn: true);
            // 1 outbound turn (no discount, 0 MP) + 1 return turn = 2 activations + build.
            Assert.That(cost, Is.EqualTo(2f + 2 * 4f));
        }

        [Test]
        public void PinnedBuilderZeroMovement_PricesHigherThanCheaperFreeBuilder()
        {
            // Regression for the "cheaper free candidate looks artificially better than the
            // pinned incumbent" failure mode: a pinned builder at 0/3 MP must not appear to cost
            // the same as a fully-mobile free builder standing right next to the site.
            var pinnedZeroMp = Route(travelCost: 3, currentMovement: 0, maxMovement: 3,
                hasActivatedThisTurn: true, activationApCost: 4);
            var freeAdjacent = Route(travelCost: 1, currentMovement: 3, maxMovement: 3,
                hasActivatedThisTurn: false, activationApCost: 1);
            float pinnedCost = DemandLayer.EstimateEconomyAssignmentAp(pinnedZeroMp, 2f, false);
            float freeCost = DemandLayer.EstimateEconomyAssignmentAp(freeAdjacent, 2f, false);
            Assert.That(pinnedCost, Is.GreaterThan(freeCost));
        }
        private static MissionIntent LoanableScout() => new MissionIntent
        {
            Kind = MissionKind.Scout,
            Status = IntentStatus.Active,
            Funding = CommitmentTier.Soft,
            Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore },
        };

        [Test]
        public void EconomyLoan_AlreadyActivatedMoverPaysNoPerHexFee()
        {
            // 3 hexes, 3 MP and activation already paid: only 2 card AP, no extra AP.
            // The same site score 7 minus interruption loss 4 = 3 (above threshold 1.6).
            var route = Route(3, 3, 3, true, activationApCost: 4);
            var choice = new DemandLayer.EconomyBuilderChoice
            {
                Route = route,
                TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(route, 2f, false),
            };
            Assert.That(choice.TotalAssignmentApCost, Is.EqualTo(2f));
            bool allowed = DemandLayer.EconomyLoanAllowed(LoanableScout(), 7f, choice, 2f,
                out float netValue);
            Assert.That(netValue, Is.EqualTo(3f));
            Assert.That(allowed, Is.True, "already paid MP must never incur a second hex fee");
        }

        [Test]
        public void EconomyLoan_UsesRealOutboundAndReturnActivations()
        {
            // Fresh 4-AP activation, 3-hex trip and optional 4-AP return.
            // Same site: roundtrip 12-8-4=0 rejected; one-way 12-4-4=4 allowed.
            var route = Route(3, 3, 3, false, activationApCost: 4, returnTravelCost: 3);
            var choice = new DemandLayer.EconomyBuilderChoice { Route = route };
            choice.TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(route, 2f, true);
            Assert.That(choice.TotalAssignmentApCost, Is.EqualTo(10f));
            Assert.That(DemandLayer.EconomyLoanAllowed(LoanableScout(), 12f, choice, 2f,
                out float roundTrip), Is.False);
            Assert.That(roundTrip, Is.EqualTo(0f));
            choice.TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(route, 2f, false);
            Assert.That(DemandLayer.EconomyLoanAllowed(LoanableScout(), 12f, choice, 2f,
                out float oneWay), Is.True);
            Assert.That(oneWay, Is.EqualTo(4f));
        }

        [Test]
        public void EconomyLoan_PreservesReachabilityAndDonorProtection()
        {
            var route = Route(4, 3, 3, true, activationApCost: 4);
            var choice = new DemandLayer.EconomyBuilderChoice
            {
                Route = route,
                TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(route, 2f, false),
            };
            Assert.That(DemandLayer.EconomyLoanAllowed(LoanableScout(), 30f, choice, 2f,
                out _), Is.False, "high value must not override the same-turn reachability gate");
            route = Route(3, 3, 3, true, activationApCost: 4);
            choice.Route = route;
            choice.TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(route, 2f, false);
            var surveil = LoanableScout();
            surveil.Objective = new ScoutIntent { Kind = ScoutTargetKind.Surveil };
            Assert.That(DemandLayer.EconomyLoanAllowed(surveil, 30f, choice, 2f,
                out _), Is.False);
            var hard = LoanableScout();
            hard.Funding = CommitmentTier.Hard;
            Assert.That(DemandLayer.EconomyLoanAllowed(hard, 30f, choice, 2f,
                out _), Is.False);
            var raid = new MissionIntent
            {
                Kind = MissionKind.Raid, Funding = CommitmentTier.Soft,
                Objective = new RaidIntent { OperationStarted = true },
            };
            Assert.That(DemandLayer.EconomyLoanAllowed(raid, 30f, choice, 2f,
                out _), Is.False);
        }

        [Test]
        public void EconomyLoan_RepricesWhenLiveMoverHasLostItsPaidMovement()
        {
            var route = Route(3, 3, 3, true, activationApCost: 4);
            var choice = new DemandLayer.EconomyBuilderChoice { Route = route };
            choice.TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(route, 2f, false);
            Assert.That(DemandLayer.EconomyLoanAllowed(LoanableScout(), 7f,
                choice, 2f, out float initial), Is.True);
            Assert.That(initial, Is.EqualTo(3f));
            // Live provisioning re-observes 0 MP; this route can no longer be loaned
            // this turn, regardless of the previously approved plan.
            route.CurrentMovement = 0;
            choice.Route = route;
            choice.TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(route, 2f, false);
            Assert.That(DemandLayer.EconomyLoanAllowed(LoanableScout(), 7f,
                choice, 2f, out float updated), Is.False);
            Assert.That(updated, Is.EqualTo(-1f));
        }

    }
}
#endif
