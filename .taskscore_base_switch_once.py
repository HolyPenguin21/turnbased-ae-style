from pathlib import Path
import uuid

root = Path('Assets/Scripts/Ai/V2')
def patch(name, before, after):
    path = root / name
    src = path.read_text()
    count = src.count(before)
    if count != 1:
        raise RuntimeError(f'{path}: exact patch expected once, got {count}, near {before[:110]!r}')
    path.write_text(src.replace(before, after, 1))
    print('PATCHED', path)

patch('Strategy/Demand/AxisDemand.cs', '''        public float EconomyStrategicUrgency;
        public int? EconomyPreferredBuilderArmyId;''', '''        public float EconomyStrategicUrgency;
        // Full intrinsic incumbent value witnessed during the same Base candidate scan.
        // Only non-null for the ONE selected challenger; never substitute site-only BuildValue.
        public float? EconomySwitchIncumbentValue;
        public int? EconomyPreferredBuilderArmyId;''')

patch('Strategy/Demand/DemandLayer.Economy.cs', '''            List<AxisDemand> selected = extractionRanked
                .Take(Mathf.Max(0, AiConfigV2.economyMaxInfrastructureDemandsPerTurn))
                .Concat(baseRanked.Take(
                    Mathf.Max(0, AiConfigV2.economyMaxExpansionBaseDemandsPerTurn)))
                .ToList();''', '''            // Select a Base challenger BEFORE the cap of one. That cap governs executable
            // demands, not the number of sites permitted into the hysteresis comparison.
            // Only a candidate that can reuse the incumbent's EXACT card and actor may
            // replace a live commitment; changing actors requires separate provisioning.
            AxisDemand selectedBase = SelectBaseDemandForCurrentCommitment(
                baseRanked.ToList(), activeIntents);
            List<AxisDemand> selected = extractionRanked
                .Take(Mathf.Max(0, AiConfigV2.economyMaxInfrastructureDemandsPerTurn))
                .Concat(selectedBase != null
                    && AiConfigV2.economyMaxExpansionBaseDemandsPerTurn > 0
                        ? new[] { selectedBase } : System.Array.Empty<AxisDemand>())
                .ToList();''')

patch('Strategy/Demand/DemandLayer.Economy.cs', '''        // This criterion gates ONLY continuity staging; canonical net-value admission still
''', '''        // One Base selection decision owner. The incumbent's current fully delivered score
        // wins over its captured score when a same-card/same-actor candidate is still present.
        // Never compare the challenger's full Value against Economy.BuildValue (site only).
        internal static AxisDemand SelectBaseDemandForCurrentCommitment(
            IReadOnlyList<AxisDemand> ranked, IReadOnlyList<MissionIntent> activeIntents)
        {
            AxisDemand first = ranked?.FirstOrDefault();
            MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                && i.Kind == MissionKind.Economy && i.Status == IntentStatus.Active
                && i.Economy?.Kind == EconomyTaskKind.FoundBase
                && i.Economy.BuildCard != null && i.PreferredMoverArmyId.HasValue);
            if (first == null || incumbent == null)
                return first;

            AxisDemand refreshed = ranked.FirstOrDefault(d => d != null
                && d.TargetHex.HasValue && d.TargetHex.Value.Equals(incumbent.Economy.TargetHex)
                && d.EconomyBuildCard == incumbent.Economy.BuildCard
                && d.EconomyPreferredBuilderArmyId == incumbent.PreferredMoverArmyId);
            float? incumbentValue = refreshed != null ? refreshed.Value
                : incumbent.Economy.IntrinsicValue;
            if (!incumbentValue.HasValue)
                return first;  // no canonical comparator; keep the existing commitment

            AxisDemand challenger = null;
            foreach (AxisDemand candidate in ranked)
            {
                if (candidate == null || candidate.EconomyBuildCard != incumbent.Economy.BuildCard
                    || candidate.EconomyPreferredBuilderArmyId != incumbent.PreferredMoverArmyId
                    || !candidate.TargetHex.HasValue
                    || candidate.TargetHex.Value.Equals(incumbent.Economy.TargetHex))
                    continue;
                candidate.EconomySwitchIncumbentValue = incumbentValue.Value;
                if (!CanReplaceCommittedBase(incumbent, candidate))
                    continue;
                if (challenger == null || candidate.Value > challenger.Value)
                    challenger = candidate;
            }
            return challenger ?? first;
        }

        // One hysteresis/admission predicate reused by Demand, Phase A and Continuity.
        // Explicit scan provenance prevents a stale site-only score from authorizing a switch.
        internal static bool CanReplaceCommittedBase(MissionIntent incumbent, AxisDemand rival) =>
            incumbent != null && incumbent.Status == IntentStatus.Active
            && incumbent.Kind == MissionKind.Economy
            && incumbent.Economy?.Kind == EconomyTaskKind.FoundBase
            && incumbent.PreferredMoverArmyId.HasValue
            && incumbent.Economy.BuildCard != null
            && rival?.RequestingAxis == DesireAxis.Economy
            && rival.Capability == CapabilityKind.EconomicExpansionBase
            && rival.TargetHex.HasValue
            && !rival.TargetHex.Value.Equals(incumbent.Economy.TargetHex)
            && rival.EconomyBuildCard == incumbent.Economy.BuildCard
            && rival.EconomyPreferredBuilderArmyId == incumbent.PreferredMoverArmyId
            && rival.EconomySwitchIncumbentValue.HasValue
            && rival.Value >= AiConfigV2.economyBaseDemandMinValue
            && rival.Value > rival.EconomySwitchIncumbentValue.Value
                + AiConfigV2.economyBaseSwitchHysteresisThreshold;

        // This criterion gates ONLY continuity staging; canonical net-value admission still
''')

patch('Strategy/StrategicPhaseA.cs', '''                AxisDemand supersedingBaseSite = null;
                if (protectedActiveBase.Economy.BuildCard != null
                    && hand.Hand.Contains(protectedActiveBase.Economy.BuildCard))
                {
                    supersedingBaseSite = demands
                        .Where(d => d != null && d.RequestingAxis == DesireAxis.Economy
                            && d.Capability == CapabilityKind.EconomicExpansionBase
                            && d.TargetHex.HasValue
                            && !d.TargetHex.Value.Equals(protectedActiveBase.Economy.TargetHex)
                            && d.Value > protectedActiveBase.Economy.BuildValue
                                + AiConfigV2.economyBaseSwitchHysteresisThreshold)
                        .OrderByDescending(d => d.Value)
                        .FirstOrDefault();
                }
                if (supersedingBaseSite != null)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.A economy hold — released active "
                        + $"{protectedActiveBase.Economy.Kind} "
                        + $"@({protectedActiveBase.Economy.TargetHex.Q},"
                        + $"{protectedActiveBase.Economy.TargetHex.R}) "
                        + $"value={protectedActiveBase.Economy.BuildValue:0.##}: newly-known "
                        + $"@({supersedingBaseSite.TargetHex.Value.Q},{supersedingBaseSite.TargetHex.Value.R}) "
                        + $"value={supersedingBaseSite.Value:0.##} clears the hysteresis margin");
                    protectedActiveBase = null;   // released — Base slot is free this pass
                }
                else
                {
                    ProtectActiveEconomyBuild(protectedActiveBase);
                }''', '''                AxisDemand supersedingBaseSite = null;
                if (protectedActiveBase.Economy.BuildCard != null
                    && hand.Hand.Contains(protectedActiveBase.Economy.BuildCard)
                    && economyAxisAuthoritative)
                {
                    supersedingBaseSite = demands
                        .Where(d => DemandLayer.CanReplaceCommittedBase(protectedActiveBase, d))
                        .OrderByDescending(d => d.Value)
                        .FirstOrDefault();
                }
                if (supersedingBaseSite != null
                    && MissionContinuityLayer.TryRetargetCommittedBase(player,
                        protectedActiveBase, supersedingBaseSite, ctx.TurnNumber))
                {
                    // Continuity has atomically rekeyed the same intent/actor and released
                    // the old reservation owner; protect the NEW exact build before any cards.
                    AiDebugLog.Write($"[AI][V2]   strat.A economy hold — switched Base "
                        + $"to ({supersedingBaseSite.TargetHex.Value.Q},"
                        + $"{supersedingBaseSite.TargetHex.Value.R}) net={supersedingBaseSite.Value:0.##}");
                    ProtectActiveEconomyBuild(protectedActiveBase);
                }
                else
                {
                    if (supersedingBaseSite != null)
                        demands = demands.Where(d => !object.ReferenceEquals(d, supersedingBaseSite))
                            .ToList(); // failed transition cannot leak a rival executable demand
                    ProtectActiveEconomyBuild(protectedActiveBase);
                }''')

patch('Continuity/MissionContinuityLayer.cs', '''        internal static void BeginEconomyBuilderRecovery(PlayerSetupData player,
''', '''        // The existing Continuity owner performs the only Base commitment switch. Same
        // card/actor are mandatory: no double-booking, no accidental donor/loan release.
        // Release the old reservation owner before rekeying; Phase A then reserves the new
        // target using the SAME intent object already referenced by the active-intent list.
        internal static bool TryRetargetCommittedBase(PlayerSetupData player,
            MissionIntent incumbent, AxisDemand challenger, int turn)
        {
            if (player == null || !DemandLayer.CanReplaceCommittedBase(incumbent, challenger)
                || (incumbent.LastProgressTurn == turn && incumbent.StepsMovedTotal > 0))
                return false; // an actor that already advanced this turn cannot be rerouted mid-step
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntentKey oldKey = incumbent.IntentKey;
            if (!state.TryGet(oldKey, out MissionIntent owned)
                || !object.ReferenceEquals(owned, incumbent))
                return false;
            HexCoord target = challenger.TargetHex.Value;
            var newKey = new MissionIntentKey(MissionKind.Economy,
                (int)EconomyTaskKind.FoundBase, 0, target.Q, target.R);
            if (state.TryGet(newKey, out MissionIntent occupied)
                && !object.ReferenceEquals(occupied, incumbent))
                return false;

            string oldOwner = EconomyMissionPlanner.OwnerKey(incumbent.LastAttemptKey);
            string oldTargetOwner = InfrastructureFulfillment.EconomyReservationOwner(new AxisDemand
            {
                Capability = CapabilityKind.EconomicExpansionBase,
                TargetHex = incumbent.Economy.TargetHex,
            });
            StrategicResourceReservationLedger.ReleaseByOwner(player, turn, oldOwner);
            if (oldTargetOwner != oldOwner)
                StrategicResourceReservationLedger.ReleaseByOwner(player, turn, oldTargetOwner);
            state.Remove(oldKey);
            EconomyIntent objective = incumbent.Economy;
            objective.TargetHex = target;
            objective.BuildCard = challenger.EconomyBuildCard;
            objective.BuildResourceCost = challenger.EconomyBuildResourceCost;
            objective.BuildApCost = challenger.EconomyBuildApCost;
            objective.MinimumFollowupAp = challenger.MinimumFollowupAp;
            objective.IntrinsicValue = challenger.Value;
            objective.BuildValue = challenger.EconomySiteValue;
            objective.BuilderArmyId = incumbent.PreferredMoverArmyId;
            objective.ProjectedActivationApCost = challenger.EconomyProjectedActivationApCost;
            objective.ProjectedMaxMovement = challenger.EconomyProjectedMaxMovement;
            incumbent.IntentKey = newKey;
            incumbent.LastAttemptKey = new StableMissionKey(MissionKind.Economy,
                (int)EconomyTaskKind.FoundBase, 0, target.Q, target.R);
            incumbent.CreatedTurn = turn;
            incumbent.TurnsActive = 1;
            incumbent.LastProgressTurn = turn;
            incumbent.StallTurns = 0;
            incumbent.StepsMovedTotal = 0;
            incumbent.CumulativeApSpent = 0f;
            state.Put(incumbent);
            return true;
        }

        internal static void BeginEconomyBuilderRecovery(PlayerSetupData player,
''')

test = Path('Assets/Editor/AiTaskScoreBaseSwitchRegressionTests.cs')
if test.exists(): raise RuntimeError('Refusing to overwrite Base switch regression')
test.write_text('''#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiTaskScoreBaseSwitchRegressionTests
    {
        private static AxisDemand BaseDemand(CardData card, int actor, int q,
            float net, float site) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy,
            Capability = CapabilityKind.EconomicExpansionBase,
            DesiredAmount = 1f,
            TargetHex = new HexCoord(q, 0),
            EconomyBuildCard = card,
            EconomyPreferredBuilderArmyId = actor,
            Value = net,
            EconomySiteValue = site,
            EconomyBuildApCost = 2f,
            MinimumFollowupAp = 2f,
        };

        [Test]
        public void Selector_SeesChallengerBeyondActiveFirstCap_UsingFullValue()
        {
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            var player = new PlayerSetupData();
            try
            {
                AxisDemand original = BaseDemand(card, 9, 3, 20f, 40f);
                MissionIntent incumbent = MissionContinuityLayer.BeginEconomyDelivery(
                    player, original, 9, 1);
                AxisDemand oldSite = BaseDemand(card, 9, 3, 20f, 40f);
                AxisDemand rival = BaseDemand(card, 9, 6, 34f, 38f);
                AxisDemand chosen = DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { oldSite, rival }, new[] { incumbent });
                Assert.That(chosen, Is.SameAs(rival));
                Assert.That(chosen.EconomySwitchIncumbentValue, Is.EqualTo(20f));
                Assert.That(DemandLayer.CanReplaceCommittedBase(incumbent, chosen), Is.True);
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { oldSite, BaseDemand(card, 9, 7, 29f, 80f) },
                    new[] { incumbent }), Is.SameAs(oldSite),
                    "a site-only score of 80 must not defeat net 20 by less than the +10 margin");
                Assert.That(DemandLayer.SelectBaseDemandForCurrentCommitment(
                    new[] { oldSite, BaseDemand(card, 11, 8, 90f, 90f) },
                    new[] { incumbent }), Is.SameAs(oldSite),
                    "a different actor cannot steal this already committed card/mission");
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void Retarget_PreservesOneActorAndCard_RekeysIntentAndAttempt()
        {
            var player = new PlayerSetupData();
            var card = new CardData(new CardDefinition { cardType = CardType.Base });
            try
            {
                MissionIntent incumbent = MissionContinuityLayer.BeginEconomyDelivery(
                    player, BaseDemand(card, 9, 3, 20f, 40f), 9, 1);
                incumbent.Funding = CommitmentTier.Hard;
                MissionIntentKey oldKey = incumbent.IntentKey;
                StableMissionKey oldAttempt = incumbent.LastAttemptKey;
                AxisDemand rival = BaseDemand(card, 9, 6, 34f, 38f);
                rival.EconomySwitchIncumbentValue = 20f;
                Assert.That(MissionContinuityLayer.TryRetargetCommittedBase(
                    player, incumbent, rival, 2), Is.True);
                MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
                Assert.That(state.Count, Is.EqualTo(1));
                Assert.That(state.TryGet(oldKey, out _), Is.False);
                Assert.That(state.TryGet(incumbent.IntentKey, out MissionIntent found), Is.True);
                Assert.That(found, Is.SameAs(incumbent));
                Assert.That(incumbent.LastAttemptKey, Is.Not.EqualTo(oldAttempt));
                Assert.That(incumbent.PreferredMoverArmyId, Is.EqualTo(9));
                Assert.That(incumbent.Economy.BuildCard, Is.SameAs(card));
                Assert.That(incumbent.Economy.IntrinsicValue, Is.EqualTo(34f));
                Assert.That(incumbent.Economy.BuildValue, Is.EqualTo(38f));
                Assert.That(incumbent.Funding, Is.EqualTo(CommitmentTier.Hard));
                Assert.That(MissionContinuityLayer.TryRetargetCommittedBase(
                    player, incumbent, rival, 2), Is.False,
                    "a switched intent cannot re-switch onto its already-owned target");
            }
            finally { MissionIntentRegistry.Clear(); }
        }
    }
}
#endif
''')
Path(str(test)+'.meta').write_text('fileFormatVersion: 2\nguid: '+uuid.uuid4().hex+'\n')
print('BASE_SWITCH_PATCH_AND_TESTS_CREATED')
