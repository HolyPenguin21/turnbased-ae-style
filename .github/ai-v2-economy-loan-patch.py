from pathlib import Path
import subprocess

path = Path('Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Economy.cs')
s = path.read_text()

def replace_once(old, new, label):
    global s
    assert s.count(old) == 1, f'{label}: expected one anchor, found {s.count(old)}'
    s = s.replace(old, new, 1)

old = '''            return EconomyBuilderCandidates(snap, target, routes, activeIntents, commitments)
                .Where(x => x.route.IsOnTarget || ActiveAssignment(activeIntents, x.army.ArmyId) == null
                    || ActiveAssignment(activeIntents, x.army.ArmyId).Kind == MissionKind.Economy
                    || EconomyLoanAllowed(ActiveAssignment(activeIntents, x.army.ArmyId), buildValue,
                        x.route.TravelCost, x.army.CurrentMovement, out _))
                .Select(x => AssessEconomyArmy(snap, target, x.route, x.army,
                    buildApCost, includeReturn))
                .Where(x => x.Suitability != EconomyArmySuitability.Ineligible)
'''
new = '''            // Assess the EXACT candidate first: loan admission and final TaskScore must
            // price the same projected roster, outbound trip and return activations.
            // The old raw-hex penalty introduced a second incompatible delivery scorer.
            return EconomyBuilderCandidates(snap, target, routes, activeIntents, commitments)
                .Select(x => AssessEconomyArmy(snap, target, x.route, x.army,
                    buildApCost, includeReturn))
                .Where(x => x.Suitability != EconomyArmySuitability.Ineligible)
                .Where(x => x.Route.IsOnTarget
                    || ActiveAssignment(activeIntents, x.Army.ArmyId) == null
                    || ActiveAssignment(activeIntents, x.Army.ArmyId).Kind == MissionKind.Economy
                    || EconomyLoanAllowed(ActiveAssignment(activeIntents, x.Army.ArmyId), buildValue,
                        x, buildApCost, out _))
'''
replace_once(old, new, 'assess-before-loan')
replace_once('''                .ThenBy(x => x.TotalAssignmentApCost
                    + (!x.Route.IsOnTarget && x.Army?.HeroIsHomeVocation == true
''', '''                // Among equally suitable builders use canonical delivery plus the ONE
                // donor interruption loss. Otherwise a cheaper AP loan can still lose
                // intrinsic value to a slightly dearer uncommitted actor.
                .ThenBy(x => Mathf.Max(0f, x.TotalAssignmentApCost - buildApCost)
                    * AiConfigV2.taskScoreReactivationApWeight
                    + EconomyMissionOpportunityCost(x, activeIntents))
                .ThenBy(x => x.TotalAssignmentApCost
                    + (!x.Route.IsOnTarget && x.Army?.HeroIsHomeVocation == true
''', 'candidate-order')
replace_once('''        internal static bool EconomyLoanAllowed(MissionIntent donor, float buildValue,
            int routeCost, int movementAvailable, out float netValue)
        {
            netValue = buildValue - AiConfigV2.economyLoanContinuationLoss
                - Mathf.Max(0, routeCost) * AiConfigV2.taskScoreReactivationApWeight;
            return EconomyDonorStructurallyEligible(donor)
                && routeCost <= movementAvailable
                && netValue >= AiConfigV2.economyLoanHysteresisThreshold;
        }
''', '''        internal static bool EconomyLoanAllowed(MissionIntent donor, float buildValue,
            EconomyBuilderChoice builder, float buildApCost, out float netValue)
        {
            // buildValue is already card-priced site TaskScore.Value. The builder's
            // assessed operation AP includes the card and real outbound/return activations;
            // subtract only extra AP via the SAME conversion as final TaskScore.Delivery.
            float extraAp = Mathf.Max(0f,
                (builder?.TotalAssignmentApCost ?? buildApCost) - buildApCost);
            netValue = buildValue - extraAp * AiConfigV2.taskScoreReactivationApWeight
                - AiConfigV2.economyLoanContinuationLoss;
            // Same-turn reachability remains a legality gate rather than a per-hex fee.
            // Donor protections for Surveil, started Raid and Hard commitments are unchanged.
            return builder != null && EconomyDonorStructurallyEligible(donor)
                && builder.Route.TravelCost <= builder.Route.CurrentMovement
                && netValue >= AiConfigV2.economyLoanHysteresisThreshold;
        }
''', 'loan-policy')
assert s.count('EconomyLoanAllowed(') == 2
assert s.count('EconomyMissionOpportunityCost(x, activeIntents)') == 1
path.write_text(s)

p = Path('Assets/Editor/AiEconomyAssignmentApTests.cs')
t = p.read_text()
ending = '    }\n}\n#endif\n'
assert t.count(ending) == 1, 'test class ending changed'
added = '''        private static MissionIntent LoanableScout() => new MissionIntent
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

'''
p.write_text(t.replace(ending, added + ending, 1))

# Reject any unadjusted caller of the old signature in the checked-in project.
result = subprocess.run(['git', 'grep', '-n', 'EconomyLoanAllowed(', 'HEAD', '--',
    'Assets/Scripts/Ai/V2', 'Assets/Editor'], capture_output=True, text=True)
print(result.stdout)
assert result.stdout.count('EconomyLoanAllowed(') == 2, 'unexpected loan-policy call sites; audit before commit'
print('PASS: guarded source rewrite and 3 NUnit regressions staged')
