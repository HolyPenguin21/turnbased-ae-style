from pathlib import Path
import re

ROOT = Path('Assets')

def file(path):
    return Path('Assets/Scripts/Ai/V2') / path

def change(path, before, after, expected=1):
    p = Path(path)
    s = p.read_text()
    n = s.count(before)
    if n != expected:
        raise RuntimeError(f'{p}: need {expected} matches, found {n}: {before[:95]!r}')
    p.write_text(s.replace(before, after))
    print('UPDATED', p)

def regex(path, pattern, replacement, expected=1):
    p = Path(path)
    s = p.read_text()
    new, n = re.subn(pattern, replacement, s, flags=re.MULTILINE | re.DOTALL)
    if n != expected:
        raise RuntimeError(f'{p}: regex expected {expected}, got {n}: {pattern[:95]}')
    p.write_text(new)
    print('UPDATED_REGEX', p)

config = file('Foundation/AiConfigV2.Economy.cs')
analysis = file('Analysis/WorldAnalysis.Economy.cs')
snapshot = file('Analysis/WorldSnapshot.cs')
evaluation = file('Evaluation/Cards/StrategicCardEvaluator.cs')
demand = file('Strategy/Demand/DemandLayer.Economy.cs')
recon = file('Strategy/Objectives/ReconObjectiveEvaluator.cs')
tests = Path('Assets/Editor/AiEconomyDecisionTests.cs')
score = file('Evaluation/TaskScore.cs')

# C1/C2: exact source usages confirmed by full project-wide git grep (2026-09-16).
change(config, '        public const float economySiteDeficitValue = 60f;\n', '')
change(config, '        public const float economySiteClusterValue = 6f;\n', '')
change(config, '        public const int economyResourceClusterRadius = 2;\n', '')
change(snapshot, '        public float NearbyResourceClusterValue;\n', '')
change(analysis, '''                    NearbyResourceClusterValue = EconomyResourceClusterValue(
                        snap, site.Hex, standings),
''', '')
regex(analysis, r'        private static float EconomyResourceClusterValue\(WorldSnapshot snap,\n.*?\n        }\n\n(?=        // Structural site fact only)', '', 1)

# C3: remove unused scored spacing, but keep actual MeetsBaseSpacing and min spacing.
change(analysis, '                            SpacingScore = BaseSpacingScore(supportDistance),\n', '')
regex(analysis, r'        // 2026-09-15 — graded companion to MeetsBaseSpacing.*?\n        }\n\n(?=        internal static IReadOnlyList<EconomyBuilderRouteSnapshot>)', '', 1)
regex(config, r'        // 2026-09-15 — graded reward on top of the economyBaseMinSpacing hard gate.*?        public const float economyBaseSpacingValue = 8f;\n', '', 1)
change(demand, '                        + $"spacingDiagnostic={site.SpacingScore:0.###} defenseRaw={site.DefenseBonusValue:0.###} "\n', '                        + $"defenseRaw={site.DefenseBonusValue:0.###} "\n')
change(demand, '                            + $"defense={defense:0.##} spacing={site.SpacingScore:0.##}(diagnostic) "\n', '                            + $"defense={defense:0.##} "\n')
regex(snapshot, r'        // Graded reward for how far this site sits from the nearest owned base.*?        public float SpacingScore;\n', '', 1)

# C4: preserve factual card-effect/airfield/physical/proximity evaluator only;
# remove second, conflicting legacy numeric formula, fake extraction loss and fields.
regex(evaluation, r'        // ===========================================================================================\n        //  BASE SITE SCORING.*?\n        // One owner of the gameplay fact', '''        // Base card facts only. DemandLayer builds the sole TaskScore; this method does NOT
        // calculate a second, bespoke strategic value or an imaginary extraction loss.
        internal readonly struct BaseSiteValue
        {
            internal readonly float HexYield;
            internal readonly float GlobalEffect;
            internal readonly float Airfield;
            internal readonly float Exposure;

            internal BaseSiteValue(float hexYield, float globalEffect, float airfield,
                float exposure)
            {
                HexYield = hexYield;
                GlobalEffect = globalEffect;
                Airfield = airfield;
                Exposure = exposure;
            }
        }

        internal static BaseSiteValue ScoreBaseSite(WorldSnapshot s, EconomyBaseOpportunity site,
            CardData card) => new BaseSiteValue(
                BaseCardMarginalYield(s, site, card.Definition),
                BaseGlobalEffectValue(s, card.Definition),
                BaseAirfieldValue(s, card.Definition, site.Hex),
                ThreatExposure(s, site.Hex));

        // One owner of the gameplay fact''', 1)
change(evaluation, '''        // this hex?". TaskScore reads the pure total; the legacy evaluator below may still attach
        // deficit weighting for old diagnostics without changing the physical fact itself.
        internal static float BaseCardMarginalYield(ResourceBundle yield, CardDefinition definition) =>
            ResourceBundle.All.Sum(type => BaseCardMarginalGain(yield, definition, type));

        internal static float BaseCardMarginalGain(ResourceBundle yield, CardDefinition definition,
            ResourceType type)
        {
            if (definition?.grantedAbilities == null
                || !definition.grantedAbilities.Contains(UnitAbilities.CollectAbilityFor(type)))
                return 0f;
            return Mathf.Max(0f, Mathf.Min(1f, yield.Get(type)));
        }
''', '''        // this hex?". This private helper is only the new card's additional Collect capacity;
        // actual OWNER gain below also accounts for existing army collection.
        private static int BaseCardAdditionalCollectCapacity(ResourceBundle yield,
            CardDefinition definition, ResourceType type)
        {
            if (definition?.grantedAbilities == null
                || !definition.grantedAbilities.Contains(UnitAbilities.CollectAbilityFor(type)))
                return 0;
            return Mathf.RoundToInt(Mathf.Max(0f, Mathf.Min(1f, yield.Get(type))));
        }
''')
change(evaluation, 'int addedCapacity = Mathf.RoundToInt(BaseCardMarginalGain(site.HexYield, definition, type));',
       'int addedCapacity = BaseCardAdditionalCollectCapacity(site.HexYield, definition, type);')
regex(evaluation, r'        // `yield` is the hex\'s remaining UNCOLLECTED amount per type \(structural site fact, see\n.*?\n        }\n\n(?=        private static float BaseGlobalEffectValue)', '', 1)
change(demand, '''                    // Only card-semantic facts are consumed from StrategicCardEvaluator. Its legacy
                    // bespoke ReasonValue/StrategicValue are deliberately ignored by TaskScore.
''', '''                    // Only real card-semantic facts come from Evaluation; TaskScore is the
                    // sole numeric evaluator of this Base's economic and strategic value.
''')
change(demand, '''                    float existingLoss = 0f;
''', '')
change(demand, '''                        hexThreatRisk: risk,
                        existingValueLoss: existingLoss);''', '''                        hexThreatRisk: risk);''', expected=2)
change(demand, '                        + $"lostExtraction={site.LostExtractionIncome:0.###} moverOpportunity={heroCost:0.###}");',
       '                        + $"moverOpportunity={heroCost:0.###}");')
change(demand, '''                            + $"moverOpp={heroCost:0.##} risk={risk:0.##} existingLoss={existingLoss:0.##}",''',
       '''                            + $"moverOpp={heroCost:0.##} risk={risk:0.##}",''')
regex(analysis, r'                        float lostExtractionIncome = 0f;\n                        if \(convertsOwnedExtraction\)\n                            foreach \(ResourceType lostType in ResourceBundle.All\)\n                                lostExtractionIncome \+= knownBuilding.CollectedAmount\(lostType\);\n', '', 1)
change(analysis, '                            LostExtractionIncome = lostExtractionIncome,\n', '')
regex(snapshot, r'        // Total per-turn income \(summed across all ResourceType\).*?        public float LostExtractionIncome;\n', '', 1)
change(snapshot, '''        // and not yet filtered by which Collect abilities the founding card actually has (see
        // StrategicCardEvaluator.BaseHexYieldValue, which applies that per-card gate). Converting
''', '''        // and not yet filtered by the founding card's Collect abilities and existing army
        // collection. StrategicCardEvaluator applies both to derive net OWNER income. Converting
''')
change(analysis, '''        // opportunity record is card-agnostic by design (see the "Base opportunities are
        // structural site facts only" comment above). StrategicCardEvaluator.BaseHexYieldValue
        // is where the specific card's abilities get applied to this remaining yield.
''', '''        // opportunity record is card-agnostic by design. StrategicCardEvaluator uses
        // BaseCardMarginalGain to apply the card and subtract any owned army collection.
''')

# C5: eliminate the duplicate delegation layer without altering phase-specific route costs.
change(recon, 'HomeDistance(snap, hex, distFromBase)',
       'TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex, distFromBase)')
change(recon, 'HomeDistance(snap, hex, distBase)',
       'TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex, distBase)')
change(recon, 'HomeDistance(snap, pos, fallbackDistance)',
       'TaskScoreEvaluator.NearestOwnedHomeDistance(snap, pos, fallbackDistance)')
change(recon, '''        // Shared strategic home-distance contract. Keep this method as the stable test seam; the
        // implementation itself is now owned by the common TaskScore evaluator.
        internal static int HomeDistance(WorldSnapshot snap, HexCoord hex, int fallbackDistFromBase) =>
            TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex, fallbackDistFromBase);

''', '')

# C6: one arithmetic folding owner for single-resource and multi-resource benefit.
regex(score, r'        internal static float EconomicHexBenefit\(float marginalGain, float resourcePriority\)\n.*?\n        internal static float Payback\(float paybackTurns\)', '''        private static float FoldEconomicBenefit(float totalGain, float weightedDeficit)
        {
            float physical = Mathf.Max(0f, totalGain);
            if (physical <= AiConfigV2.allocatorSliceEpsilon)
                return 0f;
            return Mathf.Min(AiConfigV2.taskScoreEconomicPhysicalBenefitMax,
                    physical * AiConfigV2.taskScoreEconomicPhysicalBenefitWeight)
                + Mathf.Clamp01(weightedDeficit) * AiConfigV2.taskScoreEconomicDeficitBonusMax;
        }

        internal static float EconomicHexBenefit(float marginalGain, float resourcePriority) =>
            FoldEconomicBenefit(marginalGain,
                Mathf.Clamp01(resourcePriority) * Mathf.Clamp01(
                    Mathf.Max(0f, marginalGain) / Mathf.Max(AiConfigV2.allocatorSliceEpsilon,
                        AiConfigV2.taskScoreEconomicDeficitFullGain)));

        // Multi-resource shortage belongs to EACH actually produced type. All resource gains
        // aggregate before the one physical cap and the one shortage cap in FoldEconomicBenefit.
        internal static float EconomicHexBenefit(IReadOnlyList<(float Gain, float Priority)> perResource)
        {
            if (perResource == null)
                return 0f;
            float totalGain = 0f;
            float weightedDeficit = 0f;
            foreach (var resource in perResource)
            {
                float gain = Mathf.Max(0f, resource.Gain);
                if (gain <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                totalGain += gain;
                weightedDeficit += Mathf.Clamp01(resource.Priority) * Mathf.Clamp01(
                    gain / Mathf.Max(AiConfigV2.allocatorSliceEpsilon,
                        AiConfigV2.taskScoreEconomicDeficitFullGain));
            }
            return FoldEconomicBenefit(totalGain, weightedDeficit);
        }

        internal static float Payback(float paybackTurns)''', 1)

# Remove dead test fixture values, not the tested legal gates or score assertions.
s = tests.read_text()
s = re.sub(r', SpacingScore = -?[0-9.]+f', '', s)
s = re.sub(r'^\s*SpacingScore = -?[0-9.]+f,\n', '', s, flags=re.MULTILINE)
s = re.sub(r'^\s*NearbyResourceClusterValue = [0-9.]+f,\n', '', s, flags=re.MULTILINE)
s = re.sub(r'^\s*//.*(?:SpacingScore|NearbyResourceClusterValue|BaseHexYieldValue).*\n', '', s, flags=re.MULTILINE)
tests.write_text(s)

# Remove dead config weights only where there are no actual static code readers remaining.
obsolete = ('economyBaseHexYieldValue', 'economyBaseAirfieldValue',
 'economyBaseForwardProgressValue', 'economyBaseCorridorAlignmentValue',
 'economyBaseGlobalEffectValue', 'economyBaseExtractionLossPenalty',
 'economyBuildResourcePenalty', 'economyBuildApPenalty',
 'economyBaseDeliveryApPenalty', 'economyExtractionPaybackValue',
 'economyBaseDefenseBonusValue', 'economySiteThreatPenalty',
 'economySiteTravelPenalty', 'economySiteHeroOpportunityPenalty',
 'economySiteIncomeGainValue', 'economySiteBaseSynergyValue')
s = config.read_text()
for name in obsolete:
    references = [(str(p), lno, line) for p in ROOT.rglob('*.cs') for lno, line in
        enumerate(p.read_text().splitlines(), 1) if 'AiConfigV2.' + name in line
        and not line.lstrip().startswith('//')]
    if references:
        print('RETAIN_LIVE_CONSTANT', name, references[:3])
        continue
    s, n = re.subn(r'^\s*public const (?:float|int) ' + re.escape(name)
                   + r' = [^;]+;\n', '\n', s, flags=re.MULTILINE)
    if n != 1:
        raise RuntimeError('Cannot remove single dead config constant: ' + name)
    print('REMOVED_UNUSED_CONSTANT', name)
# Replace the now obsolete historic calibration prose without modifying any live constants.
s = re.sub(r'        // Net-new-yield weight:.*?(?=        public const float economyBaseDemandMinValue)',
           '', s, flags=re.DOTALL)
s = re.sub(r'        // 2026-09-15 — priority order per project owner:.*?(?=        public const float economyBaseMaxDefenseModifier)',
           '', s, flags=re.DOTALL)
s = re.sub(r'        // 2026-09-15 — recalibrated down from 2.5.*?(?=        public const float economyExtractionMaxPaybackTurns)',
           '', s, flags=re.DOTALL) if 'economySiteTravelPenalty' not in s else s
config.write_text(s)

# Ensure dead identifiers are absent from all compiled C# sources. Historical docs may
# still cite them, but production and active test code must not.
retired = ('economySiteDeficitValue','NearbyResourceClusterValue','EconomyResourceClusterValue',
 'economyResourceClusterRadius','BaseSpacingScore','SpacingScore','economyBaseSpacingValue',
 'economyBaseIdealSpacing','economyBaseSpacingScoreAtMin','economyBaseSpacingDecayPerHex',
 'ReasonValue','IntrinsicBuildCost','ExtractionLossPenalty','StrategicValue',
 'BaseHexYieldValue','LostExtractionIncome')
for term in retired:
    remaining = [(str(p),i+1) for p in ROOT.rglob('*.cs') for i,line in
                 enumerate(p.read_text().splitlines()) if re.search(r'\b'+term+r'\b',line)]
    if remaining:
        raise RuntimeError(f'Stale compiled identifier {term}: {remaining[:12]}')
print('LEGACY_REFERENCE_SWEEP_PASS')
