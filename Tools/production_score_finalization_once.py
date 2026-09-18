#!/usr/bin/env python3
"""One-time guarded Raid-to-Development witness patch; no new scorer or manager."""
from pathlib import Path

DEMAND = Path('Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Development.cs')
TEST = Path('Assets/Editor/AiProductionScoreAlignmentTests.cs')


def once(body, needle, replacement, title):
    n = body.count(needle)
    assert n == 1, f'{title}: expected 1 occurrence, got {n}'
    print('PASS', title)
    return body.replace(needle, replacement, 1)


def patch_demand(text):
    text = once(text, 'using Game.Players;\n', 'using Game.Players;\nusing Game.Units;\n', 'use gameplay UnitData namespace')
    text = once(text,
        'HasSupportedDevelopmentAxisDemand(op, formedDemands, activeIntents, player);',
        'HasSupportedDevelopmentAxisDemand(op, formedDemands, activeIntents, player, s);',
        'preparation witness receives snapshot')
    text = once(text,
        'op, formedDemands, activeIntents, player))',
        'op, formedDemands, activeIntents, player, s))',
        'final opportunity witness receives snapshot')
    text = once(text,
        '''        // Production amplifies an already-owned need; it never originates one. In the current
        // scope only a real Recon capability delta or the exact builder of an Economy obligation
        // is a valid witness. Attack/Defence matching remains with WorthIt when those axes return.
''',
        '''        // Production amplifies an already-owned need; never invent a mission. Recon and
        // Economy keep their existing evidence; a bound active Raid can ALSO witness equipment
        // when its own primary combat roster improves against the known target by WorthIt.
        // Independent reinforcement FieldCombatPower demands are NOT fulfilled by upgrading
        // their existing primary: only a separate deployable army can close those.
''', 'development witness contract')
    text = once(text,
        '''            PlayerSetupData player)
        {
            if (op == null)
                return false;
''',
        '''            PlayerSetupData player, WorldSnapshot snap = null)
        {
            if (op == null)
                return false;
''', 'preserve witness API, optional snapshot for Raid')
    text = once(text,
        '''            return reconWitness && ImprovesReconCapability(op);
        }

        private static bool ImprovesReconCapability''',
        '''            if (reconWitness && ImprovesReconCapability(op))
                return true;

            // Only an ACTUAL owned Raid primary may justify strengthening its existing unit.
            // Research/Production mode has no bearing here: the offered output must be Equipment.
            // A potential future raid (or an independent reinforcement demand) cannot create
            // a generic "upgrade the strongest body" entitlement without a named recipient.
            if (snap == null || op.RecipientUnit.IsHero || op.Card?.cardType != CardType.Equipment
                || op.Card.equipment == null || activeIntents == null)
                return false;
            foreach (MissionIntent intent in activeIntents)
            {
                RaidIntent raid = intent?.Raid;
                if (intent == null || intent.Status != IntentStatus.Active
                    || intent.Kind != MissionKind.Raid || raid == null
                    || !raid.Target.HasValue || raid.PrimaryArmyId != army.Id
                    || (raid.Phase != RaidMissionPhase.Assault
                        && raid.Phase != RaidMissionPhase.Reinforcement))
                    continue;
                var defenders = AiV2Util.KnownDefenders(snap, raid.Target);
                if (defenders.Count == 0)
                    continue;
                // The same defender-side base bonus enters both immutable projections.
                // Terrain is not present in the snapshot, so this is a marginal signal,
                // never a substitute for Raid's final live WorthIt admission.
                float hexBonus = WorthIt.HexDefenseBonus(raid.LastKnownHex, null);
                if (ImprovesRaidCombatOutcome(op.RecipientUnit, army.Members,
                    op.Card.equipment, defenders, hexBonus))
                    return true;
            }
            return false;
        }

        // WorthIt owns combat rules and simulation. EquipmentSystem owns the exact stat/ability
        // projection. Compare the SAME primary's roster before/after replacing only its recipient,
        // without mutating gameplay UnitData or pretending the grant created a new combat body.
        internal static bool ImprovesRaidCombatOutcome(UnitData recipient,
            IReadOnlyCollection<UnitData> members, EquipmentGrant grant,
            IReadOnlyCollection<WorthIt.DefenderProfile> defenders, float hexBonus = 0f)
        {
            if (recipient == null || recipient.IsHero || grant == null || members == null
                || defenders == null || defenders.Count == 0 || !members.Contains(recipient))
                return false;

            var before = new List<WorthIt.DefenderProfile>();
            var after = new List<WorthIt.DefenderProfile>();
            var stats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = recipient.Attack,
                [EquipmentStat.Defense] = recipient.Defense,
                [EquipmentStat.HitPoints] = recipient.HitPointsMax,
                [EquipmentStat.Initiative] = recipient.Initiative,
            };
            PredictedEquipmentState predicted = EquipmentSystem.Predict(grant, stats, recipient.Abilities);
            int attack = predicted.Stats.TryGetValue(EquipmentStat.Attack, out int atk)
                ? atk : recipient.Attack;
            int defense = predicted.Stats.TryGetValue(EquipmentStat.Defense, out int def)
                ? def : recipient.Defense;
            int maxHp = predicted.Stats.TryGetValue(EquipmentStat.HitPoints, out int hp)
                ? hp : recipient.HitPointsMax;
            int currentHp = Mathf.Clamp(recipient.HitPointsCurrent
                + Mathf.Max(0, maxHp - recipient.HitPointsMax), 1, maxHp);
            int initiative = predicted.Stats.TryGetValue(EquipmentStat.Initiative, out int init)
                ? init : recipient.Initiative;
            var projected = new WorthIt.DefenderProfile(defense,
                predicted.Abilities.Contains(UnitAbilities.CeramicArmor), recipient.TypeTags.ToList(),
                attack, currentHp, initiative, predicted.Abilities, maxHp);

            foreach (UnitData unit in members)
            {
                if (unit == null || unit.IsHero)
                    continue;
                before.Add(WorthIt.FromLiveUnit(unit));
                after.Add(object.ReferenceEquals(unit, recipient) ? projected : WorthIt.FromLiveUnit(unit));
            }
            bool coversBefore = WorthIt.CanDamageAll(before, defenders, hexBonus);
            bool coversAfter = WorthIt.CanDamageAll(after, defenders, hexBonus);
            if (!coversAfter)
                return false;
            if (!coversBefore)
                return true;

            WorthIt.BattleEstimate previous = WorthIt.Estimate(before, defenders, hexBonus);
            WorthIt.BattleEstimate improved = WorthIt.Estimate(after, defenders, hexBonus);
            return improved.WinChance > previous.WinChance
                || (improved.WinChance == previous.WinChance
                    && (improved.ExpectedSurvivingHpRatioOnWin > previous.ExpectedSurvivingHpRatioOnWin
                        || improved.CriticalAfterBattleChance < previous.CriticalAfterBattleChance));
        }

        private static bool ImprovesReconCapability''', 'raid-bound WorthIt improvement only')
    assert text.count('ImprovesRaidCombatOutcome(') == 2
    return text


def patch_tests(text):
    text = once(text, 'using System.Linq;\n',
        'using System.Linq;\nusing Game.Combat;\nusing Game.Units;\n', 'test namespaces')
    marker = '''        [Test]
        public void EquipmentSupport_UsesTheProducedCardNotResearchOrProductionMode()
'''
    tests = '''        [Test]
        public void RaidEquipmentWitnessRequiresActualWorthItImprovement()
        {
            var primary = new UnitData
            {
                Attack = 1, Defense = 2, Initiative = 2,
                HitPointsCurrent = 8, HitPointsMax = 8,
            };
            var guards = new[]
            {
                new WorthIt.DefenderProfile(defense: 4, hasCeramicArmor: false,
                    attack: 6, hitPoints: 8, initiative: 2),
            };
            var moveOnly = new EquipmentGrant();
            moveOnly.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.MoveMax, amount = 3,
            });
            Assert.That(DemandLayer.ImprovesRaidCombatOutcome(
                primary, new[] { primary }, moveOnly, guards), Is.False,
                "Mobility alone cannot claim a WorthIt combat improvement against known guards");
            var weapon = new EquipmentGrant();
            weapon.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.Attack, amount = 20,
            });
            Assert.That(DemandLayer.ImprovesRaidCombatOutcome(
                primary, new[] { primary }, weapon, guards), Is.True,
                "A proven improvement in the primary's combat outcome can support its Raid");
            Assert.That(DemandLayer.ImprovesRaidCombatOutcome(
                primary, new[] { primary }, weapon, Array.Empty<WorthIt.DefenderProfile>()), Is.False,
                "An unobserved enemy cannot justify speculative Raid equipment");
            Assert.That(primary.Attack, Is.EqualTo(1),
                "Projection must never mutate the living army before generation/attachment");
            Assert.That(primary.Equipment, Is.Null);
        }

'''
    return once(text, marker, tests + marker, 'raid before/after nonmutating regression test')

updated = {DEMAND: patch_demand(DEMAND.read_text(encoding='utf-8')),
           TEST: patch_tests(TEST.read_text(encoding='utf-8'))}
assert 'GenerateDeploy' in Path('Assets/Scripts/Ai/V2/Materialization/MaterializationChainEnumerator.cs').read_text(encoding='utf-8')
assert 'ScoreGeneratedEquipmentUpgrade' in Path('Assets/Scripts/Ai/V2/Evaluation/Cards/StrategicCardEvaluator.cs').read_text(encoding='utf-8')
for path, content in updated.items():
    path.write_text(content, encoding='utf-8')
print('PASS two files patched, generation path and scoring ownership preserved')
