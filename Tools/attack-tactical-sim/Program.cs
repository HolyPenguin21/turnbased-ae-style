using System;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;

namespace AttackTacticalSim
{
    // Acceptance harness for the PURE decision rules of the Attack army's one-day local action
    // (2026-10-08), driven against the real production code:
    //   AttackTacticalOpportunity.Compare          — which of several equal targets wins, and why
    //   AttackTacticalOpportunity.ForExecution     — what an executing step may do with a frozen choice
    //   AttackTacticalOpportunity.CombatFingerprint — order-free identity of a fight's inputs
    //   GroundCombatAdmissionPolicy                — threshold and coverage as two independent facts
    // Everything world-dependent (honest sightings, view, route lookups, the estimator) needs a
    // live HexMap/AiMapMemory and is exercised by Assets/Editor/AiAttackFieldContactTests and in the
    // running game, not faked in a console. Replaces the retired raw-strength / "no extra turn"
    // harness: those rules (enemyRaw < ownRaw, ETA(A->E->B) <= ETA(A->B), leftover movement after
    // contact) were removed on purpose.
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            Order_ActiveDefenceFirst();
            Order_ThenStrongerPackage();
            Order_ThenCheaperContact();
            Order_ThenLowerArmyId();
            Execution_KeepsTheFrozenEnemy();
            Execution_NeverJumpsToAnotherEnemy();
            Execution_TakesAMandatoryRetreat();
            Execution_DoesNotTakeAnUnfundedVoluntaryTarget();
            Fingerprint_IsOrderFree_AndSeesAWoundedBody();
            Policy_LocalGateIsFortyPercentWithCoverage();
            Policy_IntermediateBaseIsFortyPercentWithoutCoverage();
            Policy_RaidAndActiveDefenceAreUntouched();

            if (_failures == 0)
            {
                Console.WriteLine("ALL PASS");
                return 0;
            }
            Console.WriteLine($"{_failures} FAILURE(S)");
            return 1;
        }

        private static AttackLocalAction Intercept(int id, int cost, float power, bool ad = false) =>
            new AttackLocalAction(AttackLocalActionKind.Intercept, new HexCoord(id, 0), id, "e" + id, cost,
                0.9f, true, "t", servesActiveDefence: ad, enemyPower: power);

        private static void Order_ActiveDefenceFirst()
        {
            var objectives = new[]
            {
                new ActiveDefenceObjective { Target = new ActiveDefenceMissionTarget { EnemyArmyId = 4 } },
            };
            Expect(nameof(Order_ActiveDefenceFirst),
                AttackTacticalOpportunity.Compare(Intercept(4, 3, 5f, ad: true), Intercept(9, 1, 50f), objectives) < 0
                && AttackTacticalOpportunity.Compare(Intercept(9, 1, 50f), Intercept(4, 3, 5f, ad: true), objectives) > 0);
        }

        private static void Order_ThenStrongerPackage() => Expect(nameof(Order_ThenStrongerPackage),
            AttackTacticalOpportunity.Compare(Intercept(9, 3, 20f), Intercept(1, 1, 10f), null) < 0);

        private static void Order_ThenCheaperContact() => Expect(nameof(Order_ThenCheaperContact),
            AttackTacticalOpportunity.Compare(Intercept(9, 1, 10f), Intercept(1, 3, 10f), null) < 0);

        private static void Order_ThenLowerArmyId() => Expect(nameof(Order_ThenLowerArmyId),
            AttackTacticalOpportunity.Compare(Intercept(1, 2, 10f), Intercept(9, 2, 10f), null) < 0
            && AttackTacticalOpportunity.Compare(Intercept(9, 2, 10f), Intercept(1, 2, 10f), null) > 0);

        private static void Execution_KeepsTheFrozenEnemy()
        {
            AttackLocalAction frozen = Intercept(5, 2, 10f);
            AttackLocalAction kept = AttackTacticalOpportunity.ForExecution(frozen, Intercept(5, 1, 10f), out string why);
            Expect(nameof(Execution_KeepsTheFrozenEnemy), why == null && kept.EnemyArmyId == 5);
        }

        private static void Execution_NeverJumpsToAnotherEnemy()
        {
            AttackTacticalOpportunity.ForExecution(Intercept(5, 2, 10f), Intercept(9, 1, 99f), out string why);
            Expect(nameof(Execution_NeverJumpsToAnotherEnemy), why != null);
        }

        private static void Execution_TakesAMandatoryRetreat()
        {
            var retreat = new AttackLocalAction(AttackLocalActionKind.Retreat, default, 9, "e", 2, 0.1f, false, "x");
            AttackTacticalOpportunity.ForExecution(AttackLocalAction.Continue(), retreat, out string why);
            Expect(nameof(Execution_TakesAMandatoryRetreat), why != null);
        }

        private static void Execution_DoesNotTakeAnUnfundedVoluntaryTarget()
        {
            AttackLocalAction r = AttackTacticalOpportunity.ForExecution(AttackLocalAction.Continue(),
                Intercept(9, 1, 99f), out string why);
            Expect(nameof(Execution_DoesNotTakeAnUnfundedVoluntaryTarget),
                why == null && r.Kind == AttackLocalActionKind.Continue);
        }

        private static WorthIt.DefenderProfile Body(float atk, float def, float hp) =>
            new WorthIt.DefenderProfile(def, false, null, atk, hp, 4, null, hp);

        private static void Fingerprint_IsOrderFree_AndSeesAWoundedBody()
        {
            var a = new[] { Body(5, 5, 10), Body(7, 3, 12) };
            var b = new[] { Body(7, 3, 12), Body(5, 5, 10) };
            var wounded = new[] { Body(5, 5, 10), Body(7, 3, 11) };
            Expect(nameof(Fingerprint_IsOrderFree_AndSeesAWoundedBody),
                AttackTacticalOpportunity.CombatFingerprint(a) == AttackTacticalOpportunity.CombatFingerprint(b)
                && AttackTacticalOpportunity.CombatFingerprint(a) != AttackTacticalOpportunity.CombatFingerprint(wounded));
        }

        private static void Policy_LocalGateIsFortyPercentWithCoverage() => Expect(
            nameof(Policy_LocalGateIsFortyPercentWithCoverage),
            Math.Abs(GroundCombatAdmissionPolicy.AttackLocalWinChanceGate - 0.40f) < 1e-6f
            && GroundCombatAdmissionPolicy.AttackLocalArmyRequiresCoverage);

        private static void Policy_IntermediateBaseIsFortyPercentWithoutCoverage() => Expect(
            nameof(Policy_IntermediateBaseIsFortyPercentWithoutCoverage),
            Math.Abs(GroundCombatAdmissionPolicy.AttackIntermediateBaseWinChanceGate - 0.40f) < 1e-6f
            && !GroundCombatAdmissionPolicy.AttackIntermediateBaseRequiresCoverage
            && !GroundCombatAdmissionPolicy.RequiresCoverage(0.40f, false)
            && GroundCombatAdmissionPolicy.RequiresCoverage(0.40f, true));

        private static void Policy_RaidAndActiveDefenceAreUntouched() => Expect(
            nameof(Policy_RaidAndActiveDefenceAreUntouched),
            Math.Abs(GroundCombatAdmissionPolicy.FreshStartWinChanceGate - 0.80f) < 1e-6f
            && Math.Abs(GroundCombatAdmissionPolicy.ContinuationWinChanceFloor - 0.55f) < 1e-6f
            && GroundCombatAdmissionPolicy.RequiresCoverage(0.80f, null)
            && GroundCombatAdmissionPolicy.RequiresCoverage(0.55f, null));

        private static void Expect(string name, bool ok)
        {
            if (!ok)
                _failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        }
    }
}
