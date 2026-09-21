#!/usr/bin/env python3
"""One-shot exact-anchor patch; fails closed if repo diverged. Removed by workflow."""
from pathlib import Path


def replace(path, before, after):
    p = Path(path)
    source = p.read_text(encoding='utf-8')
    count = source.count(before)
    if count != 1:
        raise RuntimeError(f'{path}: expected one anchor, got {count}: {before[:90]!r}')
    p.write_text(source.replace(before, after, 1), encoding='utf-8')

continuity = 'Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.cs'
replace(continuity,
    '        // Materialization has delivered the Hero for one concrete Economy prerequisite (Capability.\n',
    '''        // Continuity is the sole owner of durable build-site leases. Physical placement may
        // allow a completed extraction site to become a Base later; two unfinished owners of
        // the SAME site are nevertheless incompatible. Recovery/collection is not construction.
        internal static bool HoldsEconomyBuildSite(MissionIntent intent, HexCoord hex)
        {
            return intent != null && intent.Kind == MissionKind.Economy
                && (intent.Status == IntentStatus.Active || intent.Status == IntentStatus.Suspended)
                && intent.Economy != null
                && (intent.Economy.Kind == EconomyTaskKind.FoundBase
                    || intent.Economy.Kind == EconomyTaskKind.BuildExtraction)
                && intent.Economy.TargetHex.Equals(hex);
        }

        // Same actor, same objective and same physical card use existing takeover ownership
        // policy. An independent actor/card/objective cannot acquire an already-leased site.
        internal static bool CanGrantEconomyBuildSite(PlayerSetupData player, HexCoord hex,
            MissionIntentKey candidateKey, int builderArmyId, CardData card)
        {
            if (player == null) return false;
            return !MissionIntentRegistry.GetOrCreate(player).All.Any(intent =>
                HoldsEconomyBuildSite(intent, hex)
                && intent.PreferredMoverArmyId != builderArmyId
                && !intent.IntentKey.Equals(candidateKey)
                && (card == null || intent.Economy.BuildCard != card));
        }

        // Materialization has delivered the Hero for one concrete Economy prerequisite (Capability.
''')
replace(continuity,
    '            // P0-3 (AI V2 economy audit 2026-09-21) + 2026-09-21 Block A — this is the ONE place\n',
    '''            // Reject independent spatial contenders BEFORE retiring any existing intent or
            // touching its per-owner reserves. Demand-level dedup is not a durable ownership gate.
            if (!CanGrantEconomyBuildSite(player, objective.TargetHex, intent.IntentKey,
                    builderArmyId, objective.BuildCard))
                return null;

            // P0-3 (AI V2 economy audit 2026-09-21) + 2026-09-21 Block A — this is the ONE place
''')
replace(continuity,
    '            string oldOwner = EconomyMissionPlanner.OwnerKey(incumbent.LastAttemptKey);\n',
    '''            // Retarget has no displacement transaction for a third-party site lease.
            // Validate BEFORE releasing the original owner or rewriting its objective.
            if (state.All.Any(i => !object.ReferenceEquals(i, incumbent)
                    && HoldsEconomyBuildSite(i, target)))
                return false;

            string oldOwner = EconomyMissionPlanner.OwnerKey(incumbent.LastAttemptKey);
''')
provisioning = 'Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs'
replace(provisioning,
    '            if (deferredGarrison != null)\n            {\n                // 2026-09-14 review round 10 (P1) — reserve the physical build-stage resources NOW,\n',
    '''            if (deferredGarrison != null)
            {
                // Preflight the canonical Continuity site lease before spending a builder
                // extraction AP or reserving completion resources for an incompatible project.
                int candidateBuilder = deferredPlan.Container?.Id ?? -1;
                if (!MissionContinuityLayer.CanGrantEconomyBuildSite(player, target.TargetHex,
                        currentIntentKey, candidateBuilder, target.BuildCard))
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        "economy build site already leased by another active project"));

                // 2026-09-14 review round 10 (P1) — reserve the physical build-stage resources NOW,
''')
replace(provisioning,
    '            // Reserve the physical stage cost NOW (same cross-mission-visibility reasoning as the\n',
    '''            if (!MissionContinuityLayer.CanGrantEconomyBuildSite(player, target.TargetHex,
                    currentIntentKey, hero.Id, target.BuildCard))
                return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                    "economy build site already leased by another active project"));

            // Reserve the physical stage cost NOW (same cross-mission-visibility reasoning as the
''')
replace(provisioning,
    '                MissionContinuityLayer.BeginEconomyDelivery(player, new AxisDemand\n',
    '                MissionIntent delivery = MissionContinuityLayer.BeginEconomyDelivery(player, new AxisDemand\n')
replace(provisioning,
    '                }, hero.Id, ctx.TurnNumber);\n\n            // "Pending" means Execution still has real work to do before movement:',
    '''                }, hero.Id, ctx.TurnNumber);
                if (delivery == null)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        "economy build site lease changed during provisioning"));

            // "Pending" means Execution still has real work to do before movement:''')
# A block with an else and declaration needs braces: normalize the exact existing branch.
replace(provisioning,
    '''            else
                // AI economy commitment/recovery audit (2026-09-15) — this hero cannot finish the
''',
    '''            else
            {
                // AI economy commitment/recovery audit (2026-09-15) — this hero cannot finish the
''')
replace(provisioning,
    '''                if (delivery == null)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        "economy build site lease changed during provisioning"));

            // "Pending" means Execution still has real work to do before movement:''',
    '''                if (delivery == null)
                    return ProvisioningResult.Fail(ProvisionFailure.MoverContended(
                        "economy build site lease changed during provisioning"));
            }

            // "Pending" means Execution still has real work to do before movement:''')
finalize = 'Assets/Scripts/Ai/V2/Strategy/PhaseA/CapabilityDeliveryEvaluator.cs'
replace(finalize,
    '                    MissionContinuityLayer.BeginEconomyDelivery(player, demand, builderId, ctx.TurnNumber);\n',
    '''                    // A physically deployed Hero is not a fulfilled Economy build demand
                    // unless Continuity successfully owns its destination lease.
                    if (MissionContinuityLayer.BeginEconomyDelivery(
                            player, demand, builderId, ctx.TurnNumber) == null)
                        continue;
''')

test = Path('Assets/Editor/AiEconomyBuildSiteOwnershipTests.cs')
if test.exists():
    raise RuntimeError('test file already exists')
test.write_text('''#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiEconomyBuildSiteOwnershipTests
    {
        private static MissionIntent Build(EconomyTaskKind kind, HexCoord hex, int actor)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy,
                Status = IntentStatus.Active,
                PreferredMoverArmyId = actor,
                Objective = new EconomyIntent
                {
                    Kind = kind, TargetHex = hex, BuilderArmyId = actor,
                },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            intent.LastAttemptKey = new StableMissionKey(MissionKind.Economy,
                (int)kind, 0, hex.Q, hex.R);
            return intent;
        }

        private static AxisDemand Demand(HexCoord hex) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy,
            Capability = CapabilityKind.EconomicInfrastructure,
            TargetHex = hex,
        };

        [Test]
        public void DistinctActorAndCard_CannotOverwriteFoundBaseWithExtractionOnSameHex()
        {
            var player = new PlayerSetupData();
            var site = new HexCoord(4, 3);
            var incumbent = Build(EconomyTaskKind.FoundBase, site, 12);
            MissionIntentRegistry.GetOrCreate(player).Put(incumbent);
            Assert.That(MissionContinuityLayer.BeginEconomyDelivery(player, Demand(site), 20, 3),
                Is.Null);
            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(
                incumbent.IntentKey, out MissionIntent actual), Is.True);
            Assert.That(actual, Is.SameAs(incumbent));
        }

        [Test]
        public void IndependentSites_DoNotConflict()
        {
            var player = new PlayerSetupData();
            var incumbent = Build(EconomyTaskKind.FoundBase, new HexCoord(4, 3), 12);
            MissionIntentRegistry.GetOrCreate(player).Put(incumbent);
            Assert.That(MissionContinuityLayer.BeginEconomyDelivery(player,
                Demand(new HexCoord(7, 3)), 20, 3), Is.Not.Null);
            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(
                incumbent.IntentKey, out MissionIntent actual), Is.True);
            Assert.That(actual, Is.SameAs(incumbent));
        }

        [Test]
        public void SameDeliveryReentry_IsIdempotent()
        {
            var player = new PlayerSetupData();
            var site = new HexCoord(4, 3);
            MissionIntent first = MissionContinuityLayer.BeginEconomyDelivery(
                player, Demand(site), 20, 3);
            MissionIntent second = MissionContinuityLayer.BeginEconomyDelivery(
                player, Demand(site), 20, 4);
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.CreatedTurn, Is.EqualTo(3));
        }

        [Test]
        public void RecoveryAndMobileCollection_DoNotLeaseBuildSite()
        {
            var hex = new HexCoord(4, 3);
            Assert.That(MissionContinuityLayer.HoldsEconomyBuildSite(
                Build(EconomyTaskKind.ReturnBuilder, hex, 1), hex), Is.False);
            Assert.That(MissionContinuityLayer.HoldsEconomyBuildSite(
                Build(EconomyTaskKind.MobileCollection, hex, 2), hex), Is.False);
            Assert.That(MissionContinuityLayer.HoldsEconomyBuildSite(
                Build(EconomyTaskKind.BuildExtraction, hex, 3), hex), Is.True);
        }
    }
}
#endif
''', encoding='utf-8')
meta = Path(str(test) + '.meta')
if meta.exists():
    raise RuntimeError('test meta already exists')
meta.write_text('fileFormatVersion: 2\nguid: 2d6e6b2d4bc84c14b2d182cbb9e2d604\n', encoding='utf-8')
print('Spatial ownership patch applied: Continuity, Provisioning, capability handoff, four tests.')
