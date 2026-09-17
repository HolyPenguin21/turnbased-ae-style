from pathlib import Path

provision = Path('Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs')
s = provision.read_text()
old = '''            if (donor != null)
            {
                if (!DemandLayer.EconomyLoanAllowed(donor, target.BuildValue, distance,
                        hero.CurrentMovement, out float loanNet))
                    return EconomyCompletionPlan.No(ProvisionFailure.MoverContended(
                        $"loan rejected donor={donor.IntentKey} distance={distance} move={hero.CurrentMovement} net={loanNet:0.##}"));
            }
'''
new = '''            if (donor != null)
            {
                // Provisioning validates LIVE path/MP against the SAME Demand-owned loan
                // predicate. Reprice the already selected builder's projected roster from
                // current physical facts; never resurrect the old raw-hex distance scorer.
                EconomyBuilderRouteSnapshot liveRoute = builderChoice.Route;
                liveRoute.TravelCost = distance;
                liveRoute.CurrentMovement = hero.CurrentMovement;
                liveRoute.HasActivatedThisTurn = hero.HasActivatedThisTurn;
                var liveChoice = new DemandLayer.EconomyBuilderChoice
                {
                    Route = liveRoute,
                    TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(
                        liveRoute, target.BuildApCost,
                        target.Kind == EconomyTaskKind.BuildExtraction),
                };
                if (!DemandLayer.EconomyLoanAllowed(donor, target.BuildValue,
                        liveChoice, target.BuildApCost, out float loanNet))
                    return EconomyCompletionPlan.No(ProvisionFailure.MoverContended(
                        $"loan rejected donor={donor.IntentKey} distance={distance} move={hero.CurrentMovement} net={loanNet:0.##}"));
            }
'''
assert s.count(old) == 1, f'provisioning loan gate: {s.count(old)} anchors'
provision.write_text(s.replace(old, new, 1))

p = Path('Assets/Editor/AiEconomyDecisionTests.cs')
t = p.read_text()
start = t.index('        [Test]\n        public void EconomyLoan_SoftReconCanBeBorrowedForHighSameTurnValue()')
end = t.index('        [Test]\n        public void EconomyHeroMaterialization_NewArmyIsOperationalDeliveryOnlyForEconomy()', start)
original = t[start:end]
assert original.count('EconomyLoanAllowed(') == 5, original
replacement = '''        // Match the shared, real assignment-AP model used by the production owner.
        private static DemandLayer.EconomyBuilderChoice LoanChoiceForPolicyTest(int distance, int mp)
        {
            var route = new EconomyBuilderRouteSnapshot
            {
                TravelCost = distance,
                CurrentMovement = mp,
                MaxMovement = 3,
                HasActivatedThisTurn = true,
                ActivationApCost = 1,
            };
            return new DemandLayer.EconomyBuilderChoice
            {
                Route = route,
                TotalAssignmentApCost = DemandLayer.EstimateEconomyAssignmentAp(route, 0f, false),
            };
        }

        [Test]
        public void EconomyLoan_SoftReconCanBeBorrowedForHighSameTurnValue()
        {
            MissionIntent donor = ScoutDonor(CommitmentTier.Soft, ScoutTargetKind.Explore);
            Assert.That(DemandLayer.EconomyLoanAllowed(donor, 80f,
                LoanChoiceForPolicyTest(2, 3), 0f, out float net), Is.True);
            Assert.That(net, Is.GreaterThanOrEqualTo(AiConfigV2.economyLoanHysteresisThreshold));
        }

        [Test]
        public void EconomyLoan_HardOrCriticalSurveilCannotBeBorrowed()
        {
            Assert.That(DemandLayer.EconomyLoanAllowed(
                ScoutDonor(CommitmentTier.Hard, ScoutTargetKind.Explore), 100f,
                LoanChoiceForPolicyTest(1, 3), 0f, out _), Is.False);
            Assert.That(DemandLayer.EconomyLoanAllowed(
                ScoutDonor(CommitmentTier.Soft, ScoutTargetKind.Surveil), 100f,
                LoanChoiceForPolicyTest(1, 3), 0f, out _), Is.False);
        }

        [Test]
        public void EconomyLoan_StartedRaidCannotBeBorrowed()
        {
            var donor = new MissionIntent
            {
                Kind = MissionKind.Raid, Funding = CommitmentTier.Soft,
                Objective = new RaidIntent { OperationStarted = true },
            };
            Assert.That(DemandLayer.EconomyLoanAllowed(donor, 100f,
                LoanChoiceForPolicyTest(1, 3), 0f, out _), Is.False);
        }

        [Test]
        public void EconomyLoan_MustCompleteMovementThisTurn()
        {
            MissionIntent donor = ScoutDonor(CommitmentTier.Soft, ScoutTargetKind.Explore);
            Assert.That(DemandLayer.EconomyLoanAllowed(donor, 100f,
                LoanChoiceForPolicyTest(4, 3), 0f, out _), Is.False);
        }

'''
p.write_text(t[:start] + replacement + t[end:])

# Regenerate policy-cost tests from the same current physical facts after a live MP change.
small = Path('Assets/Editor/AiEconomyAssignmentApTests.cs')
tests = small.read_text()
ending = '    }\n}\n#endif\n'
assert tests.count(ending) == 1
extra = '''        [Test]
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

'''
small.write_text(tests.replace(ending, extra + ending, 1))

assert 'EconomyLoanAllowed(donor, target.BuildValue, distance' not in provision.read_text()
assert provision.read_text().count('DemandLayer.EconomyLoanAllowed(') == 1
assert p.read_text().count('DemandLayer.EconomyLoanAllowed(') == 5
print('PASS: provisioning uses shared loan policy with live route; 4 prior tests updated, 1 live regression added')
