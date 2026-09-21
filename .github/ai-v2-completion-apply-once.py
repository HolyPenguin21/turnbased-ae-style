#!/usr/bin/env python3
"""Guarded one-shot patch to reconcile stale per-owner completion AP; removed by workflow."""
from pathlib import Path


def replace(path, before, after):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    count = text.count(before)
    if count != 1:
        raise RuntimeError(f'{path}: expected one exact anchor, found {count}: {before[:100]!r}')
    p.write_text(text.replace(before, after, 1), encoding='utf-8')

ledger = 'Assets/Scripts/Ai/V2/State/StrategicResourceReservation.cs'
replace(ledger,
    '        public static bool HasOwnerReason(PlayerSetupData player, int turn, string owner,\n',
    '''        // Read-only enumeration of real completion owners, never a second reservation ledger.
        // Snapshot the keys before the lifecycle owner modifies its own rows.
        internal static IReadOnlyList<string> CompletionOwners(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return System.Array.Empty<string>();
            return e.Reservations.Where(r => r.Reason == StrategicReservationReason.EconomyBuildCompletion)
                .Select(r => r.Owner).Where(owner => !string.IsNullOrEmpty(owner))
                .Distinct().OrderBy(owner => owner).ToList();
        }

        public static bool HasOwnerReason(PlayerSetupData player, int turn, string owner,
''')
infra = 'Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs'
replace(infra,
    '        internal static void ClearDeferredEconomyResources(PlayerSetupData player, int turn) =>\n',
    '''        // The only Completion -> Deferred / Released transition. Repeating this on the
        // same owner after the first downgrade is a no-op; unrelated projects are untouched.
        internal static void ReconcileEconomyCompletionOwner(PlayerSetupData player, int turn,
            string owner, MissionIntent intent, bool durableValid, bool completionThisTurn)
        {
            if (!StrategicResourceReservationLedger.HasOwnerReason(player, turn, owner,
                    StrategicReservationReason.EconomyBuildCompletion))
                return;
            if (!durableValid || intent?.Economy == null)
            {
                StrategicResourceReservationLedger.ReleaseByOwner(player, turn, owner);
                return;
            }
            if (completionThisTurn)
                return;

            // Explicit owner-scoped downgrade: a repeated Phase A deferred request MUST NOT
            // implicitly demote a still-executable Completion, but this settled lifecycle
            // decision has proved it cannot finish this turn. Keep only durable H/E/M/T.
            StrategicResourceReservationLedger.ReplaceReasonOwner(player, turn,
                StrategicReservationReason.EconomyDeferredBuild, owner, replaceOwnerRows: true);
            ReserveEconomyCost(player, turn, owner, intent.Economy.BuildResourceCost, 0f,
                StrategicReservationReason.EconomyDeferredBuild);
        }

        // Called after settled operational steps AND immediately before each Phase B admission.
        // Compute current-turn feasibility from the REAL actor/path/AP/card, not the snapshot
        // used by the earlier Provisioning prediction. No global world rebuild or AP threshold.
        internal static void ReconcileEconomyCompletionReservations(PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (player == null || root == null || ctx == null)
                return;
            int turn = ctx.TurnNumber;
            IReadOnlyList<string> owners =
                StrategicResourceReservationLedger.CompletionOwners(player, turn);
            if (owners.Count == 0)
                return;
            List<MissionIntent> intents = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Kind == MissionKind.Economy && i.Economy != null
                    && (i.Economy.Kind == EconomyTaskKind.FoundBase
                        || i.Economy.Kind == EconomyTaskKind.BuildExtraction))
                .ToList();
            foreach (string owner in owners)
            {
                MissionIntent intent = intents.FirstOrDefault(i =>
                    EconomyMissionPlanner.OwnerKey(i.LastAttemptKey) == owner);
                if (intent == null)
                {
                    ReconcileEconomyCompletionOwner(player, turn, owner, null, false, false);
                    continue;
                }
                EconomyIntent build = intent.Economy;
                ArmyData actor = ArmyRegistry.AllForOwner(player).FirstOrDefault(a =>
                    a != null && a.Id == intent.PreferredMoverArmyId && a.Owner == player
                    && AiArmyRoles.IsHeroLed(a));
                bool cardStillAvailable = build.Kind != EconomyTaskKind.FoundBase
                    || (build.BuildCard != null && hand?.Hand?.Contains(build.BuildCard) == true);
                bool durableValid = actor != null && cardStillAvailable;
                if (!durableValid)
                {
                    ReconcileEconomyCompletionOwner(player, turn, owner, intent, false, false);
                    continue;
                }
                int route = actor.Hex.Equals(build.TargetHex) ? 0
                    : ctx.Map == null ? int.MaxValue
                    : SafeStepPathing.FindSafePathCost(ctx.Map, actor, build.TargetHex);
                float freeApWithOwnHold = StrategicResourceReservationLedger.SpendableExcludingOwner(
                    player, turn, StrategicReservedResource.ActionPoints, root.ActionPoints, owner);
                float activationAp = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
                bool completionThisTurn = intent.Status == IntentStatus.Active
                    && route != int.MaxValue && route <= actor.CurrentMovement
                    && freeApWithOwnHold + AiConfigV2.allocatorSliceEpsilon
                        >= build.BuildApCost + (route > 0 ? activationAp : 0f)
                    && (build.BuildResourceCost == null || build.BuildResourceCost.CanAfford(root));
                ReconcileEconomyCompletionOwner(player, turn, owner, intent, true,
                    completionThisTurn);
            }
        }

        internal static void ClearDeferredEconomyResources(PlayerSetupData player, int turn) =>
''')
pipeline = 'Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs'
replace(pipeline,
    '''                        MissionContinuityLayer.ReconcileStep(
                            player, snapshot.TurnNumber, outcome);

                    settledSteps++;
''',
    '''                        MissionContinuityLayer.ReconcileStep(
                            player, snapshot.TurnNumber, outcome);
                    // A single atomic move may consume the last MP after Provisioning had
                    // legitimately reserved this owner's completion AP. Settle its stage now.
                    InfrastructureFulfillment.ReconcileEconomyCompletionReservations(
                        player, root, hand, ctx);

                    settledSteps++;
''')
replace(pipeline,
    '''                yield return RunTypedAdmissions();

                // Management/Development is another bounded task family, not the owner of the
''',
    '''                yield return RunTypedAdmissions();
                // Also reconcile on bounded/no-progress exits where no additional typed
                // admission occurs: Phase B must see AP that no actor can spend on a build.
                InfrastructureFulfillment.ReconcileEconomyCompletionReservations(
                    player, root, hand, ctx);

                // Management/Development is another bounded task family, not the owner of the
''')
replace(pipeline,
    '''                    var phaseBRound = new StrategicPhaseResult();
                    yield return StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
''',
    '''                    // A prior Phase B action may have spent AP or removed a build card.
                    // Revalidate each owner's stronger completion claim before the next pass.
                    InfrastructureFulfillment.ReconcileEconomyCompletionReservations(
                        player, root, hand, ctx);
                    var phaseBRound = new StrategicPhaseResult();
                    yield return StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
''')
test = 'Assets/Editor/AiEconomyReservationLifecycleTests.cs'
replace(test,
    '''        [Test]
        public void TessekT10_ReachingSitePromotesDeferredDeliveryToCompletionAp()
''',
    '''        [Test]
        public void CompletionBecomesUnexecutable_DowngradesOnlyItsOwnApAndPreservesPhysicalHold()
        {
            var player = new PlayerSetupData();
            const int turn = 11;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            var ownerCost = new ResourceCost(human: 2, materials: 3);
            var otherCost = new ResourceCost(energy: 2, tech: 1);
            MissionIntent intent = ActiveFoundBaseIntent(19, new HexCoord(3, 2), ownerCost, 4f);
            string owner = EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey);
            const string other = "independent-other-build";
            InfrastructureFulfillment.ReserveEconomyCost(player, turn, owner,
                ownerCost, 4f);
            InfrastructureFulfillment.ReserveEconomyCost(player, turn, other,
                otherCost, 3f);

            // After a settled movement, this actor can no longer reach its site this turn.
            // The project itself remains valid and has future-turn physical requirements.
            InfrastructureFulfillment.ReconcileEconomyCompletionOwner(player, turn, owner,
                intent, durableValid: true, completionThisTurn: false);
            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn, owner,
                StrategicReservationReason.EconomyBuildCompletion), Is.False);
            Assert.That(StrategicResourceReservationLedger.OwnerReasonMatches(player, turn,
                owner, StrategicReservationReason.EconomyDeferredBuild, ownerCost, 4f), Is.True);
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.ActionPoints), Is.EqualTo(3f),
                "the first build releases its 4 AP while another owner's 3 AP stays reserved");
            Assert.That(StrategicResourceReservationLedger.OwnerReasonMatches(player, turn,
                other, StrategicReservationReason.EconomyBuildCompletion, otherCost, 3f), Is.True);
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Human), Is.EqualTo(2f));
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Materials), Is.EqualTo(3f));

            // Idempotent reentry, and Phase A's ordinary deferred request cannot re-promote AP.
            InfrastructureFulfillment.ReconcileEconomyCompletionOwner(player, turn, owner,
                intent, durableValid: true, completionThisTurn: false);
            InfrastructureFulfillment.ReserveDeferredEconomyResourcesForActiveIntent(
                player, turn, intent);
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.ActionPoints), Is.EqualTo(3f));
        }

        [Test]
        public void InvalidCompletion_ReleasesOnlyInvalidOwnersRows()
        {
            var player = new PlayerSetupData();
            const int turn = 12;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            var invalidCost = new ResourceCost(materials: 5);
            var otherCost = new ResourceCost(energy: 2);
            MissionIntent invalid = ActiveFoundBaseIntent(21, new HexCoord(1, 3), invalidCost, 4f);
            string owner = EconomyMissionPlanner.OwnerKey(invalid.LastAttemptKey);
            const string other = "independent-other-build";
            InfrastructureFulfillment.ReserveEconomyCost(player, turn, owner,
                invalidCost, 4f);
            InfrastructureFulfillment.ReserveEconomyCost(player, turn, other,
                otherCost, 3f);
            InfrastructureFulfillment.ReconcileEconomyCompletionOwner(player, turn, owner,
                invalid, durableValid: false, completionThisTurn: false);
            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn, owner,
                StrategicReservationReason.EconomyBuildCompletion), Is.False);
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.ActionPoints), Is.EqualTo(3f));
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Materials), Is.Zero);
            Assert.That(StrategicResourceReservationLedger.OwnerReasonMatches(player, turn,
                other, StrategicReservationReason.EconomyBuildCompletion, otherCost, 3f), Is.True);
        }

        [Test]
        public void ValidCompletion_ReconciliationKeepsOwnAp()
        {
            var player = new PlayerSetupData();
            const int turn = 13;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            var cost = new ResourceCost(materials: 2);
            MissionIntent intent = ActiveFoundBaseIntent(23, new HexCoord(1, 3), cost, 4f);
            string owner = EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey);
            InfrastructureFulfillment.ReserveEconomyCost(player, turn, owner, cost, 4f);
            InfrastructureFulfillment.ReconcileEconomyCompletionOwner(player, turn, owner,
                intent, durableValid: true, completionThisTurn: true);
            Assert.That(StrategicResourceReservationLedger.OwnerReasonMatches(player, turn,
                owner, StrategicReservationReason.EconomyBuildCompletion, cost, 4f), Is.True);
        }

        [Test]
        public void TessekT10_ReachingSitePromotesDeferredDeliveryToCompletionAp()
''')
print('Owner-scoped completion revalidation: ledger, infrastructure, pipeline, three regressions.')
