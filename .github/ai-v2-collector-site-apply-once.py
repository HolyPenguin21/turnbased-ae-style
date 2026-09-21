#!/usr/bin/env python3
"""One-shot guarded edit of the existing economy owners. Removed by the workflow."""
from pathlib import Path

analysis = Path('Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Economy.cs')
snapshot = Path('Assets/Scripts/Ai/V2/Analysis/WorldSnapshot.cs')
demand = Path('Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Economy.cs')
tests = Path('Assets/Editor/AiEconomyDecisionTests.cs')
sources = {p: p.read_text(encoding='utf-8') for p in (analysis, snapshot, demand, tests)}

def replace_one(text, old, new, name):
    count = text.count(old)
    assert count == 1, f'{name}: expected one exact anchor, found {count}'
    return text.replace(old, new, 1)

# WorldAnalysis is the ONLY source of physical opportunity facts. Keep Facility
# and mobile Collector marginals separate while sharing IncomeProjection physics.
s = sources[analysis]
start = '            var extraction = new List<EconomyExtractionOpportunity>();\n'
end = '            eco.ExtractionOpportunities = extraction;\n'
assert s.count(start) == 1 and s.count(end) == 1, 'WorldAnalysis: opportunity region missing/ambiguous'
first = s.index(start)
last = s.index(end, first) + len(end)
old = s[first:last]
for required in ('var collectorSites = new List<EconomyExtractionOpportunity>();',
                 'IncomeProjection.OwnerCollectionAtHex', 'if (marginal <= 0)',
                 'eco.CollectorSites = collectorSites;', 'building.FreeFacilitySlots <= 0'):
    assert required in old, f'WorldAnalysis prerequisite missing: {required}'
new = '''            var extraction = new List<EconomyExtractionOpportunity>();
            var collectorSites = new List<EconomyExtractionOpportunity>();
            foreach ((HexCoord Hex, ResourceType Type, int Yield) site
                     in KnownExtractionYields(snap))
            {
                ResourceType resourceType = site.Type;
                int effectiveYield = site.Yield;
                if (KnownHostileAtHex(snap, site.Hex))
                    continue;

                bool hasBuilding = knownBuildings.TryGetValue(site.Hex,
                    out AiMapMemory.KnownBuilding building);
                // Real income consumes the building's portion FIRST, even if its owner is
                // another player. A Facility needs our building and a free matching slot;
                // a field Collector is independent of building ownership and slot capacity.
                int currentCollection = hasBuilding ? building.CollectedAmount(resourceType) : 0;
                int ownArmyCollectors = Mathf.RoundToInt((snap.Self.Armies
                    ?? System.Array.Empty<ArmySnapshot>())
                    .Where(a => a != null && a.Hex.Equals(site.Hex))
                    .Sum(a => a.CollectionCapacity.Get(resourceType)));
                bool armiesCanCollect = !(snap.Known?.EnemySightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Any(enemy => enemy.Hex.Equals(site.Hex));
                (int facilityGain, int collectorGain) = MarginalSiteCollectionGains(
                    effectiveYield, currentCollection, ownArmyCollectors, armiesCanCollect);

                if (collectorGain > 0)
                    collectorSites.Add(new EconomyExtractionOpportunity
                    {
                        Hex = site.Hex,
                        ResourceType = resourceType,
                        EffectiveYield = effectiveYield,
                        CurrentBuildingCollection = currentCollection,
                        MarginalIncomeGain = collectorGain,
                        BaseNetworkSynergy = EconomyBaseNetworkSynergy(snap, site.Hex),
                        BuilderRoutes = System.Array.Empty<EconomyBuilderRouteSnapshot>(),
                    });

                // BuildingPlayExecutor.CanPlaceFacilityAt and the live building's
                // HasFacilityWithAbility require an OWNED building, available Facility slot,
                // and no existing facility with the same collection ability. In particular,
                // none of those rules may filter the independent mobile collector list.
                if (!hasBuilding || building.Owner != player || building.FreeFacilitySlots <= 0
                    || building.HasFacilityWithAbility(UnitAbilities.CollectAbilityFor(resourceType))
                    || facilityGain <= 0)
                    continue;
                extraction.Add(new EconomyExtractionOpportunity
                {
                    Hex = site.Hex,
                    ResourceType = resourceType,
                    EffectiveYield = effectiveYield,
                    CurrentBuildingCollection = currentCollection,
                    MarginalIncomeGain = facilityGain,
                    BaseNetworkSynergy = EconomyBaseNetworkSynergy(snap, site.Hex),
                    BuilderRoutes = BuilderRoutesFor(site.Hex),
                });
            }
            eco.CollectorSites = collectorSites;
            eco.ExtractionOpportunities = extraction;
'''
s = s[:first] + new + s[last:]
s = replace_one(s, '''                if (!eco.IsIncomeDeficient(snap.Self, site.ResourceType))
                    continue;

                int buildingCollection = site.CurrentBuildingCollection;''', '''                // Demand admits a new collector for USEFUL income, not strictly for a
                // rate below IncomeTarget. Apply that same UsefulMarginalIncomeGain test
                // below to existing free actors, so a newly delivered collector gets work.
                int buildingCollection = site.CurrentBuildingCollection;''', 'mobile assignment gate')
s = replace_one(s, '''            bool collectorActionable = collectorSites.Any(site =>
                eco.IsIncomeDeficient(snap.Self, site.ResourceType)
                && standings[site.ResourceType].UsefulMarginalIncomeGain(site.MarginalIncomeGain)
                    > AiConfigV2.allocatorSliceEpsilon);''', '''            // This is the same structural-opportunity interpretation as extractionActionable:
            // Phase A/Materialization remain the sole owners of physical card and route admission.
            bool collectorActionable = collectorSites.Any(site =>
                standings[site.ResourceType].UsefulMarginalIncomeGain(site.MarginalIncomeGain)
                    > AiConfigV2.allocatorSliceEpsilon);''', 'economy desire gate')
helper_anchor = '        // AiMapMemory owns the complete last-observed resource line. Economy enumerates that\n'
helper = '''        // The only Analysis-level projection of the TWO different additions at a site.
        // IncomeProjection owns all resource physics; this preserves a Facility's net
        // owner gain when an army would merely lose the same slice, independently of
        // the gain from deploying an additional mobile collector onto the remainder.
        internal static (int FacilityGain, int CollectorGain) MarginalSiteCollectionGains(
            int effectiveYield, int buildingCollection, int ownArmyCollectors,
            bool armiesCanCollect)
        {
            int before = IncomeProjection.OwnerCollectionAtHex(effectiveYield,
                buildingCollection, ownArmyCollectors, armiesCanCollect);
            int facility = IncomeProjection.MarginalOwnerCollectionAtHex(effectiveYield,
                buildingCollection, 1, ownArmyCollectors, armiesCanCollect);
            int collector = Mathf.Max(0, IncomeProjection.OwnerCollectionAtHex(
                effectiveYield, buildingCollection, ownArmyCollectors + 1,
                armiesCanCollect) - before);
            return (facility, collector);
        }

'''
s = replace_one(s, helper_anchor, helper + helper_anchor, 'Analysis physical helper')
sources[analysis] = s

# Restore the two unrelated comment edits introduced by the abandoned WIP;
# leave only the new snapshot fact in WorldSnapshot.cs.
s = sources[snapshot]
s = replace_one(s,
    'read still has it. IsHiddenFromUs is only ever true inside TrueWorld (a fog/cheat-read',
    'read still has it. IsHiddenFromUs is only ever true inside TrueWorld (a fog-honest',
    'restore army comment')
s = replace_one(s,
    'across several regional cheat contacts. It never supplies a position and therefore\n        // does not weaken the honest-contact boundary; ActiveDefence still admits Honest contacts only.',
    'across several regional cheat contacts. It never supplies a position and therefore does\n        // not weaken the honest-contact boundary; ActiveDefence still admits Honest contacts only.',
    'restore contact comment')
s = replace_one(s,
    '''        // Mobile collectors use the same physical marginal model as Facilities, but their
        // sites do not require building ownership or a free Facility slot. Only Analysis writes
        // this list; Demand must never reinterpret ExtractionOpportunities as collector sites.''',
    '''        // A mobile Collector's marginal gain is the extra ARMY collection, not a
        // Facility's net owner gain. This distinct physical site list ignores Facility
        // ownership/slots, and only WorldAnalysis writes its observed facts.''',
    'snapshot collector semantics')
sources[snapshot] = s

# Two exact changes in the EXISTING demand owner, not a second demand generator.
s = sources[demand]
s = replace_one(s,
    '''            if (s.Economy?.ExtractionOpportunities == null)
                yield break;''',
    '''            if (s.Economy?.CollectorSites == null)
                yield break;''',
    'collector demand source guard')
s = replace_one(s,
    '''            foreach (EconomyExtractionOpportunity site in s.Economy.ExtractionOpportunities)
            {
                if (existingMobileCoverage.Contains''',
    '''            foreach (EconomyExtractionOpportunity site in s.Economy.CollectorSites)
            {
                if (existingMobileCoverage.Contains''',
    'collector demand source iterator')
assert s.count('foreach (EconomyExtractionOpportunity site in s.Economy.ExtractionOpportunities') == 1, 'Facility demand must remain on ExtractionOpportunities'
sources[demand] = s

# Regression tests in the existing economy suite: no new test fixture/layer.
s = sources[tests]
test_anchor = '''        [Test]
        public void ExtractionDemand_OneFacilitySlotFundsOnlyOneResourceType()'''
new_tests = '''        [TestCase(3, 0, 2, true, 0, 1)]
        [TestCase(3, 0, 2, false, 1, 0)]
        [TestCase(3, 1, 0, true, 1, 1)]
        [TestCase(3, 3, 0, true, 0, 0)]
        public void EconomyMarginalPhysics_FacilityAndCollectorAreDifferentOperations(
            int yield, int building, int armies, bool canCollect,
            int expectedFacility, int expectedCollector)
        {
            (int facility, int collector) = WorldAnalysis.MarginalSiteCollectionGains(
                yield, building, armies, canCollect);
            Assert.That(facility, Is.EqualTo(expectedFacility));
            Assert.That(collector, Is.EqualTo(expectedCollector));
        }

        [Test]
        public void CollectorDemand_SiteWithoutFacilityOpportunityStillGetsOwnDemand()
        {
            WorldSnapshot snap = SnapshotWithDeficits(0.8f, 0.8f, actionable: true);
            var target = new HexCoord(2, 0);
            snap.Economy.ExtractionOpportunities =
                System.Array.Empty<EconomyExtractionOpportunity>();
            snap.Economy.CollectorSites = new[]
            {
                ExtractionOpportunity(target, ResourceType.Materials, 2),
            };
            // IncomeTarget defaults to zero here. A useful deck-resource gain must still
            // be actionable, or a created collector will never receive a mobile mission.
            Assert.That(snap.Economy.IsIncomeDeficient(snap.Self, ResourceType.Materials),
                Is.False);
            List<AxisDemand> emitted = DemandLayer.EconomyDemands(
                snap, new DesireBreakdown(), null, null, null).ToList();
            Assert.That(emitted, Has.Count.EqualTo(1));
            Assert.That(emitted[0].Capability, Is.EqualTo(CapabilityKind.CollectorCapability));
            Assert.That(emitted[0].TargetHex, Is.EqualTo(target));
            Assert.That(emitted[0].EconomyExpectedIncomeGain, Is.EqualTo(2f));
        }

        [Test]
        public void CollectorDemand_DoesNotBorrowFacilitySiteWhenCollectorYieldIsExhausted()
        {
            WorldSnapshot snap = SnapshotWithDeficits(0.8f, 0.8f, actionable: true);
            snap.Economy.ExtractionOpportunities = new[]
            {
                ExtractionOpportunity(new HexCoord(2, 0), ResourceType.Materials, 1),
            };
            snap.Economy.CollectorSites =
                System.Array.Empty<EconomyExtractionOpportunity>();
            Assert.That(DemandLayer.CollectorCapabilityDemands(snap,
                snap.Economy.PerType.ToDictionary(x => x.Type, x => x), null, null),
                Is.Empty);
        }

'''
s = replace_one(s, test_anchor, new_tests + test_anchor, 'existing editor regression suite')
sources[tests] = s

# Preflight all targets; only write after every exact-anchor assertion succeeded.
assert sources[analysis].count('eco.CollectorSites = collectorSites;') == 1
assert sources[demand].count('s.Economy.CollectorSites') == 1
for path, text in sources.items():
    path.write_text(text, encoding='utf-8')
    print(f'Updated {path}: {len(text.splitlines())} lines')
print('PASS: distinct Facility/Collector physical gains, consistent useful-income assignment, demand wiring and regression cases')
