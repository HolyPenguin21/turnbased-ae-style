from pathlib import Path


def change(path, old, new, count=1):
    p = Path(path)
    s = p.read_text(encoding='utf-8')
    actual = s.count(old)
    assert actual == count, f'{path}: expected {count} anchors, found {actual}: {old[:130]!r}'
    p.write_text(s.replace(old, new), encoding='utf-8')


snapshot = 'Assets/Scripts/Ai/V2/Analysis/WorldSnapshot.cs'
change(snapshot,
    '        public float Ratio;               // OwnIncome / max(1, FieldMedianIncome)\n    }\n',
    '''        public float Ratio;               // OwnIncome / max(1, FieldMedianIncome)

        // This snapshot fact limits a site's physical income to the income actually
        // useful for the known hand/remaining deck within the existing runway horizon.
        // SpendableStockpile already excludes reservations, so reserved demand must
        // NOT be added again: that would count the same protected resources twice.
        public float UsefulMarginalIncomeGain(float marginalGain)
        {
            float physicalGain = Mathf.Max(0f, marginalGain);
            if (physicalGain <= AiConfigV2.allocatorSliceEpsilon)
                return 0f;
            float horizon = Mathf.Max(1f, AiConfigV2.economyRunwayHorizonTurns);
            float plannedNeed = Mathf.Max(0f, HandResourceNeed)
                + Mathf.Max(0f, RemainingDeckResourceNeed);
            float covered = Mathf.Max(0f, SpendableStockpile)
                + Mathf.Max(0f, OwnIncome) * horizon;
            return Mathf.Min(physicalGain, Mathf.Max(0f, plannedNeed - covered) / horizon);
        }
    }
''')

demand = 'Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Economy.cs'
change(demand, '            int rejectedPayback = 0;\n',
    '            int rejectedPayback = 0;\n            int rejectedSurplus = 0;\n')
change(demand,
    '''                float gain = Mathf.Max(0f, site.MarginalIncomeGain);
                if (gain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
''',
    '''                float gain = Mathf.Max(0f, site.MarginalIncomeGain);
                if (gain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                // Keep raw income for execution; value and payback use only
                // economically useful marginal income from the frozen snapshot.
                float usefulGain = rs.UsefulMarginalIncomeGain(gain);
                if (usefulGain <= AiConfigV2.allocatorSliceEpsilon)
                {
                    // Existing delivery is still proposed directly by EconomyMissionPlanner
                    // from its durable intent, without refreshing it with surplus economics.
                    rejectedSurplus++;
                    continue;
                }
''')
change(demand, '                float payback = EconomyPaybackTurns(gain, resourceCost, cardAp);',
    '                float payback = EconomyPaybackTurns(usefulGain, resourceCost, cardAp);')
change(demand,
    '                    economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(gain, resourcePriority),',
    '                    economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(usefulGain, resourcePriority),')
change(demand,
    '                    + $"marginalGain={gain:0.###} paybackTurns={payback:0.###} cardAp={cardAp:0.###} "',
    '                    + $"marginalGain={gain:0.###} usefulGain={usefulGain:0.###} paybackTurns={payback:0.###} cardAp={cardAp:0.###} "')
change(demand,
    '                    + $"marginalGain={gain:0.##} payback={payback:0.##} "',
    '                    + $"marginalGain={gain:0.##} usefulGain={usefulGain:0.##} payback={payback:0.##} "')
change(demand,
    '            int rejectionTotal = rejectedNoBuilder + baseNoBuilder + rejectedPayback\n',
    '            int rejectionTotal = rejectedNoBuilder + baseNoBuilder + rejectedPayback + rejectedSurplus\n')
change(demand,
    '                + $"payback={rejectedPayback} strategic_value={rejectedStrategicValue + baseStrategicValue} "',
    '                + $"payback={rejectedPayback} surplus={rejectedSurplus} strategic_value={rejectedStrategicValue + baseStrategicValue} "')
change(demand,
    '''                    float economicGainFact = Mathf.Max(0f, facts.HexYield);
                    float paybackTurns = economicGainFact > AiConfigV2.allocatorSliceEpsilon
                        ? EconomyPaybackTurns(economicGainFact,
                            StrategicCardEvaluator.ResourceCostSum(card.EffectivePlayResourceCost),
                            card.EffectivePlayApCost)
                        : float.PositiveInfinity;
''',
    '                    float economicGainFact = Mathf.Max(0f, facts.HexYield);\n')
change(demand, '                    var marginalByResource = new List<(float Gain, float Priority)>();\n',
    '                    var marginalByResource = new List<(float Gain, float Priority)>();\n                    float usefulGainTotal = 0f;\n')
change(demand,
    '''                        float priority = s.Economy.PerType
                            .Where(x => x.Type == type)
                            .Select(x => TaskScoreEvaluator.ResourcePriority(x,
                                ResourceStarvationRegistry.Pressure(player, type)))
                            .DefaultIfEmpty(0f).First();
                        marginalByResource.Add((typeGain, priority));
''',
    '''                        EconomyResourceStanding standing = s.Economy.PerType
                            .FirstOrDefault(x => x.Type == type);
                        float usefulTypeGain = standing.UsefulMarginalIncomeGain(typeGain);
                        if (usefulTypeGain <= AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        float priority = TaskScoreEvaluator.ResourcePriority(standing,
                            ResourceStarvationRegistry.Pressure(player, type));
                        marginalByResource.Add((usefulTypeGain, priority));
                        usefulGainTotal += usefulTypeGain;
''')
change(demand,
    '                    float basePriority = marginalByResource.Count == 0 ? 0f\n',
    '''                    float paybackTurns = usefulGainTotal > AiConfigV2.allocatorSliceEpsilon
                        ? EconomyPaybackTurns(usefulGainTotal, resourceCost, card.EffectivePlayApCost)
                        : float.PositiveInfinity;
                    float basePriority = marginalByResource.Count == 0 ? 0f
''')
change(demand,
    '                    float payback = economicGainFact > AiConfigV2.allocatorSliceEpsilon\n',
    '                    float payback = usefulGainTotal > AiConfigV2.allocatorSliceEpsilon\n')
change(demand,
    '                        $"economicGain={economicGainFact:0.###} resourcePriority={basePriority:0.###} "',
    '                        $"economicGain={economicGainFact:0.###} usefulEconomicGain={usefulGainTotal:0.###} resourcePriority={basePriority:0.###} "')

tests = 'Assets/Editor/AiEconomyDecisionTests.cs'
change(tests,
    'new EconomyResourceStanding { Type = ResourceType.Human, DeficitScore = max, IncomeGap = max },',
    'new EconomyResourceStanding { Type = ResourceType.Human, DeficitScore = max, IncomeGap = max, RemainingDeckResourceNeed = 8f },')
for resource in ('Energy', 'Materials', 'Tech'):
    change(tests,
        f'new EconomyResourceStanding {{ Type = ResourceType.{resource}, DeficitScore = other }},',
        f'new EconomyResourceStanding {{ Type = ResourceType.{resource}, DeficitScore = other, RemainingDeckResourceNeed = 8f }},')
for resource in ('Energy', 'Materials'):
    change(tests,
        f'new EconomyResourceStanding {{ Type = ResourceType.{resource}, DeficitScore = 0.8f }},',
        f'new EconomyResourceStanding {{ Type = ResourceType.{resource}, DeficitScore = 0.8f, RemainingDeckResourceNeed = 8f }},')
marker = '''        [Test]
        public void EconomySiteScore_ThreatCanMakeSaferPeerWin()
'''
extra = '''        [Test]
        public void EconomyDemand_RejectsSurplusEvenWhenOpponentProducesMore()
        {
            WorldSnapshot snap = SnapshotWithDeficits(0.6f, 0.2f, actionable: true);
            var abundant = EconomyStanding.CalculateResource(ResourceType.Materials,
                ownIncome: 5f, opponentMedianIncome: 20f, handNeed: 1f,
                remainingDeckNeed: 1f, reservedOperationalNeed: 0f,
                spendableStockpile: 100f, starvationPressure: 0f);
            var needed = EconomyStanding.CalculateResource(ResourceType.Energy,
                ownIncome: 0f, opponentMedianIncome: 0f, handNeed: 8f,
                remainingDeckNeed: 0f, reservedOperationalNeed: 0f,
                spendableStockpile: 0f, starvationPressure: 0f);
            snap.Economy.PerType = new[]
            {
                new EconomyResourceStanding { Type = ResourceType.Human },
                needed, abundant, new EconomyResourceStanding { Type = ResourceType.Tech },
            };
            snap.Economy.ExtractionOpportunities = new[]
            {
                ExtractionOpportunity(new HexCoord(2, 0), ResourceType.Materials, 1),
                ExtractionOpportunity(new HexCoord(3, 0), ResourceType.Energy, 1),
            };
            Assert.That(TaskScoreEvaluator.ResourcePriority(abundant), Is.GreaterThan(0f));
            AxisDemand onlyUseful = DemandLayer.EconomyDemands(snap,
                new DesireBreakdown(), null, null, null).Single();
            Assert.That(onlyUseful.EconomyResourceType, Is.EqualTo(ResourceType.Energy));
            Assert.That(onlyUseful.EconomyExpectedIncomeGain, Is.EqualTo(1f),
                "Execution truth remains the raw physical marginal income");
        }

'''
change(tests, marker, extra + marker)

unified = 'Assets/Editor/AiUnifiedTaskScoreTests.cs'
marker = '''        [Test]
        public void PhysicalCostConversions_UseOneSharedScale()
'''
extra = '''        [Test]
        public void UsefulMarginalIncome_UsesRealRemainingNeedAndOneExistingRunway()
        {
            EconomyResourceStanding abundant = EconomyStanding.CalculateResource(
                ResourceType.Materials, ownIncome: 5f, opponentMedianIncome: 20f,
                handNeed: 1f, remainingDeckNeed: 1f, reservedOperationalNeed: 0f,
                spendableStockpile: 100f, starvationPressure: 0f);
            Assert.That(TaskScoreEvaluator.ResourcePriority(abundant), Is.GreaterThan(0f),
                "Opponent's higher income can raise old deficit without creating spending need");
            float surplusUseful = abundant.UsefulMarginalIncomeGain(1f);
            Assert.That(surplusUseful, Is.Zero);
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(surplusUseful,
                TaskScoreEvaluator.ResourcePriority(abundant)), Is.Zero);

            EconomyResourceStanding futureCard = EconomyStanding.CalculateResource(
                ResourceType.Energy, ownIncome: 1f, opponentMedianIncome: 1f,
                handNeed: 0f, remainingDeckNeed: 9f, reservedOperationalNeed: 0f,
                spendableStockpile: 0f, starvationPressure: 0f);
            Assert.That(futureCard.UsefulMarginalIncomeGain(1f), Is.EqualTo(1f));

            EconomyResourceStanding partial = EconomyStanding.CalculateResource(
                ResourceType.Tech, ownIncome: 2f, opponentMedianIncome: 2f,
                handNeed: 7f, remainingDeckNeed: 0f, reservedOperationalNeed: 0f,
                spendableStockpile: 0f, starvationPressure: 0f);
            Assert.That(partial.UsefulMarginalIncomeGain(1f),
                Is.EqualTo(1f / AiConfigV2.economyRunwayHorizonTurns).Within(0.0001f));

            EconomyResourceStanding alreadyReserved = EconomyStanding.CalculateResource(
                ResourceType.Human, ownIncome: 0f, opponentMedianIncome: 0f,
                handNeed: 3f, remainingDeckNeed: 0f, reservedOperationalNeed: 2f,
                spendableStockpile: 4f, starvationPressure: 0f);
            Assert.That(alreadyReserved.UsefulMarginalIncomeGain(1f), Is.Zero,
                "Reservation was already excluded from spendable stock; no double count");

            float energy = futureCard.UsefulMarginalIncomeGain(1f);
            float materials = abundant.UsefulMarginalIncomeGain(1f);
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(
                new List<(float Gain, float Priority)> { (materials, 1f), (energy, 1f) }),
                Is.EqualTo(TaskScoreEvaluator.EconomicHexBenefit(energy, 1f)).Within(0.0001f),
                "One surplus resource must not inherit another resource's deficit in a Base");
        }

'''
change(unified, marker, extra + marker)
print('PASS: guarded source edits and regression additions in four existing files')
