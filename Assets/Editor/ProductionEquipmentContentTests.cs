#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using static Game.EditorTests.AttachmentContentTestData;

namespace Game.EditorTests
{
    public sealed class ProductionEquipmentContentTests
    {
        private const string CatalogPath = "Assets/Cards/ResearchProductionCatalog.asset";
        private static readonly string[] FactionPaths =
        {
            "Assets/Cards/IronConcord/CardCatalog_IronConcord.asset",
            "Assets/Cards/TheAshen/CardCatalog_TheAshen.asset",
            "Assets/Cards/TheVessels/CardCatalog_TheVessels.asset",
        };
        private static string[] Keys(string field) => Regex.Matches(Regex.Match(Text(CatalogPath),
            @"(?ms)^  " + field + @":\r?\n(.*?)(?=^  [A-Za-z]\w*:|\z)").Groups[1].Value,
            @"cardKey: ([^\r\n]+)").Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToArray();
        private static List<CardDefinition> Cards() => Blocks(NeutralPath).Select(Read).ToList();
        private static CardDefinition Gear(string name) => Cards().Single(c => c.displayName == name);
        public static IEnumerable<string> ProductionNames() => Cards()
            .Where(c => Keys("productionCards").Contains(c.authoredKey)).Select(c => c.displayName);
        private static CardDefinition Host() => AttachmentSlotTests.Host();

        [Test]
        public void CatalogContainsAllPhysicalEquipmentAndNoResearchMutators()
        {
            var cards = Cards(); var keys = Keys("productionCards");
            var physical = cards.Where(c => c.cardType == CardType.Equipment
                && c.attachmentSlot == AttachmentSlot.Equipment).ToArray();
            Assert.That(keys.Length, Is.GreaterThanOrEqualTo(40));
            Assert.That(keys.Distinct().Count(), Is.EqualTo(keys.Length));
            Assert.That(keys, Is.EquivalentTo(physical.Select(c => c.authoredKey)));
            Assert.That(keys.Intersect(Keys("researchCards")), Is.Empty);
            var allKeys = FactionPaths.Concat(new[] { NeutralPath }).SelectMany(Blocks).Select(Read)
                .Select(c => c.authoredKey).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            Assert.That(allKeys.Distinct().Count(), Is.EqualTo(allKeys.Count));
            foreach (var c in physical)
            {
                Assert.That(c.authoredKey, Does.StartWith("neutral.equipment."));
                Assert.That(c.displayName, Does.Not.StartWith("it "));
                Assert.That(c.equipment.statChanges.Any(s => s.stat == EquipmentStat.Resistance), Is.False);
                Assert.That(c.equipment.hostKinds, Is.EqualTo(new[] { EquipmentHostKind.Unit }));
                Assert.That(c.apCost, Is.EqualTo(1)); Assert.That(c.activationApCost, Is.EqualTo(1));
                Assert.That(c.fate, Is.InRange(3, 6));
            }
            Assert.That(Gear("AT VH Launcher").authoredKey,
                Is.EqualTo("neutral.equipment.armored.at-vh-launcher"));
        }

        [TestCaseSource(nameof(ProductionNames))]
        public void AuthoredProjectionMatchesLiveApplyAndDoesNotMutateTheDefinition(string name)
        {
            var gear = Gear(name); var host = Host(); host.grantedAbilities.Clear();
            var card = new CardData(host);
            var predicted = EquipmentSystem.Project(card, gear);
            Assert.That(card.Equipment, Is.Null); Assert.That(card.Mutator, Is.Null);
            Assert.That(host.attack, Is.EqualTo(5)); Assert.That(host.grantedAbilities, Is.Empty);
            var body = AttachmentSlotTests.Body(host);
            EquipmentSystem.ApplyAttachments(body, gear, null);
            Assert.That(body.Equipment, Is.SameAs(gear)); Assert.That(body.Mutator, Is.Null);
            Assert.That(body.Attack, Is.EqualTo(predicted.Stats[EquipmentStat.Attack]));
            Assert.That(body.Defense, Is.EqualTo(predicted.Stats[EquipmentStat.Defense]));
            Assert.That(body.Range, Is.EqualTo(predicted.Stats[EquipmentStat.Range]));
            Assert.That(body.HitPointsMax, Is.EqualTo(predicted.Stats[EquipmentStat.HitPoints]));
            Assert.That(body.MoveMax, Is.EqualTo(predicted.Stats[EquipmentStat.MoveMax]));
            Assert.That(body.Abilities, Is.EquivalentTo(predicted.Abilities));
            Assert.That(body.Resistance, Is.EqualTo(host.resistanceRating));
        }

        [TestCase("Assault Rifle Kit", 6, 3, 10, 6, 2)]
        [TestCase("Heavy MG", 5, 3, 10, 6, 2)]
        [TestCase("Plasma Gun", 8, 3, 10, 6, 2)]
        [TestCase("Marksman Rifle", 5, 3, 10, 6, 3)]
        [TestCase("Shotgun", 6, 3, 10, 6, 1)]
        [TestCase("Ballistic Shield", 5, 4, 10, 6, 2)]
        [TestCase("Reinforced Chassis", 5, 3, 12, 5, 2)]
        [TestCase("Reactive Armor", 5, 4, 10, 5, 2)]
        [TestCase("Turbocharger", 5, 2, 10, 7, 2)]
        [TestCase("Dozer Blade", 6, 4, 10, 6, 1)]
        [TestCase("Siege Ram", 5, 3, 10, 5, 1)]
        [TestCase("Mortar Rack", 4, 3, 10, 6, 3)]
        [TestCase("Flame Projector", 5, 3, 10, 6, 1)]
        [TestCase("Recoil Cannon", 7, 3, 10, 5, 2)]
        [TestCase("Portable Mortar", 5, 3, 10, 5, 3)]
        [TestCase("Assault Conversion Kit", 5, 4, 10, 6, 1)]
        public void RepresentativeAuthoredEffects(string name, int attack, int defense, int hp, int move, int range)
        {
            var projected = EquipmentSystem.Project(new CardData(Host()), Gear(name));
            Assert.That(new[] { projected.Stats[EquipmentStat.Attack], projected.Stats[EquipmentStat.Defense],
                projected.Stats[EquipmentStat.HitPoints], projected.Stats[EquipmentStat.MoveMax],
                projected.Stats[EquipmentStat.Range] }, Is.EqualTo(new[] { attack, defense, hp, move, range }));
        }

        [TestCase(3, true)]
        [TestCase(7, false)]
        public void HeavyMgOverridesAttackAndEvaluatorPreservesTheSignedDelta(int baseAttack, bool improvement)
        {
            var host = Host(); host.attack = baseAttack; host.grantedAbilities.Clear();
            var gear = Gear("Heavy MG"); var prediction = EquipmentSystem.Project(new CardData(host), gear);
            Assert.That(prediction.Stats[EquipmentStat.Attack], Is.EqualTo(5));
            var delta = StrategicCardEvaluator.EquipmentDeltaParts(gear, host);
            Assert.That(improvement ? delta.Combat > 0 : delta.Combat < 0, Is.True);
            Assert.That(host.attack, Is.EqualTo(baseAttack));
        }

        [TestCase("Ceramic Vest", UnitAbilities.CeramicArmor)]
        [TestCase("Double Barrel", UnitAbilities.CriticalDamage)]
        [TestCase("Grenade Launcher", UnitAbilities.Splash)]
        [TestCase("Shock Rifle", UnitAbilities.ShockAttack)]
        [TestCase("Rail Rifle", UnitAbilities.Hyperkinetic)]
        [TestCase("Incendiary Rifle", UnitAbilities.Pyrokinetic)]
        [TestCase("Flame Projector", UnitAbilities.Scorcher)]
        public void AuthoredAbilitiesReachCombatState(string name, string ability)
        {
            var host = Host(); host.grantedAbilities.Clear();
            var body = AttachmentSlotTests.Body(host);
            EquipmentSystem.ApplyAttachments(body, Gear(name), null);
            Assert.That(body.Abilities, Does.Contain(ability));
        }

        [TestCase("Heavy MG")]
        [TestCase("Plasma Gun")]
        [TestCase("Plasma Cannon")]
        [TestCase("Dozer Blade")]
        [TestCase("Shotgun")]
        public void WeaponReplacementClearsOldWeaponEffectsButKeepsNonWeaponAbilities(string name)
        {
            var host = Host(); host.grantedAbilities.AddRange(new[] { UnitAbilities.Scorcher,
                UnitAbilities.Splash, UnitAbilities.CriticalDamage, UnitAbilities.ShockAttack,
                UnitAbilities.Hyperkinetic, UnitAbilities.Pyrokinetic, UnitAbilities.AntiAir });
            var body = AttachmentSlotTests.Body(host);
            EquipmentSystem.ApplyAttachments(body, Gear(name), null);
            Assert.That(body.Abilities, Is.EquivalentTo(new[] { UnitAbilities.CeramicArmor, UnitAbilities.R1S4 }));
        }

        [TestCase("Dozer Blade")]
        [TestCase("Siege Ram")]
        [TestCase("Mortar Rack")]
        [TestCase("Flame Projector")]
        [TestCase("Rail Cannon")]
        [TestCase("AA Mount")]
        public void GroundConversionsRejectEveryAuthoredAircraftAndAcceptVehicleOrMecha(string name)
        {
            var gear = Gear(name);
            var roster = FactionPaths.SelectMany(Blocks).Select(Read).ToList();
            var aircraft = roster.Where(c => c.cardType == CardType.Unit && c.isAviation).ToList();
            Assert.That(aircraft, Is.Not.Empty);
            foreach (var c in aircraft) Assert.That(EquipmentSystem.FitsHost(gear, c, out _), Is.False, c.displayName);
            var ground = roster.Where(c => c.cardType == CardType.Unit && !c.isAviation
                && (c.unitTypeTags.Contains(UnitTypeTag.Vehicle) || c.unitTypeTags.Contains(UnitTypeTag.Mecha))).ToList();
            Assert.That(ground, Is.Not.Empty);
            foreach (var c in ground) Assert.That(EquipmentSystem.FitsHost(gear, c, out _), Is.True, c.displayName);
        }

        [Test]
        public void InfantryWeaponsAcceptMechanicalInfantryAndDoNotSpreadToHeroes()
        {
            var roster = FactionPaths.SelectMany(Blocks).Select(Read).ToList();
            var infantry = roster.Where(c => c.cardType == CardType.Unit
                && c.unitTypeTags.Contains(UnitTypeTag.Infantry) && c.unitTypeTags.Contains(UnitTypeTag.Mechanical)).ToList();
            Assert.That(infantry, Is.Not.Empty);
            foreach (var host in infantry)
                Assert.That(EquipmentSystem.FitsHost(Gear("Shotgun"), host, out _), Is.True, host.displayName);
            foreach (var hero in roster.Where(c => c.cardType == CardType.Hero))
                Assert.That(EquipmentSystem.FitsHost(Gear("Ballistic Shield"), hero, out _), Is.False, hero.displayName);
        }

        [Test]
        public void NuclearEngineKeepsAircraftCompatibility()
        {
            var aircraft = FactionPaths.SelectMany(Blocks).Select(Read)
                .Where(c => c.cardType == CardType.Unit && c.isAviation).ToList();
            Assert.That(aircraft, Is.Not.Empty);
            foreach (var host in aircraft)
                Assert.That(EquipmentSystem.FitsHost(Gear("Nuclear Engine"), host, out _), Is.True, host.displayName);
        }

        [TestCase("AA Launcher")]
        [TestCase("AA Mount")]
        public void AntiAirAttachmentProvidesRadiusAndARealEntryReaction(string name)
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AntiAirState.Clear();
            VisionSystem.Clear(); VisionSystem.Configure(null); StealthSystem.Clear();
            try
            {
                var player = new PlayerSetupData(); var enemy = new PlayerSetupData();
                var host = Host();
                host.unitTypeTags = name == "AA Launcher"
                    ? new List<UnitTypeTag> { UnitTypeTag.Bio, UnitTypeTag.Infantry }
                    : new List<UnitTypeTag> { UnitTypeTag.Mechanical, UnitTypeTag.Vehicle };
                Assert.That(EquipmentSystem.FitsHost(Gear(name), host, out _), Is.True);
                var body = AttachmentSlotTests.Body(host); body.Owner = player;
                EquipmentSystem.ApplyAttachments(body, Gear(name), null);
                Assert.That(AntiAirRules.TryGetRadius(body, out int radius), Is.True);
                Assert.That(radius, Is.EqualTo(1));
                var ground = new ArmyData { Owner = player, Hex = default };
                ground.Members.Add(body); ArmyRegistry.Register(ground);
                var air = new ArmyData { Owner = enemy, Hex = default };
                air.Members.Add(new Game.Units.UnitData { IsAviation = true, Owner = enemy });
                ArmyRegistry.Register(air); VisionSystem.RecomputeFor(player);
                var reactions = AntiAirRules.CollectEntryReactions(air, air.Hex);
                Assert.That(reactions.Count, Is.EqualTo(1));
                Assert.That(reactions[0].AaUnit, Is.SameAs(body));
                AntiAirState.RecordPrompted(body, air.Id, true);
                Assert.That(AntiAirRules.CollectEntryReactions(air, air.Hex), Is.Empty);
            }
            finally
            {
                ArmyRegistry.Clear(); BuildingRegistry.Clear(); AntiAirState.Clear();
                VisionSystem.Clear(); VisionSystem.Configure(null); StealthSystem.Clear();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EquipmentAndMutatorCoexistInBothInstallationOrders(bool mutatorFirst)
        {
            var host = Host(); var gear = Gear("Shotgun");
            var mutator = Cards().Single(c => c.authoredKey == "neutral.mutator.dermal-plating");
            var hand = new CardData(host) { Equipment = gear, Mutator = mutator };
            var predicted = EquipmentSystem.Project(hand);
            var body = AttachmentSlotTests.Body(host);
            if (mutatorFirst)
            {
                EquipmentSystem.ApplyAttachments(body, null, mutator);
                EquipmentSystem.ApplyAttachments(body, gear, null);
            }
            else
            {
                EquipmentSystem.ApplyAttachments(body, gear, null);
                EquipmentSystem.ApplyAttachments(body, null, mutator);
            }
            Assert.That(body.Equipment, Is.SameAs(gear)); Assert.That(body.Mutator, Is.SameAs(mutator));
            Assert.That(body.Attack, Is.EqualTo(6)); Assert.That(body.Range, Is.EqualTo(1));
            Assert.That(body.Defense, Is.EqualTo(predicted.Stats[EquipmentStat.Defense]));
            Assert.That(body.Abilities, Is.EquivalentTo(predicted.Abilities));
        }

        [TestCaseSource(nameof(ProductionNames))]
        public void MintedEquipmentPaysNoSecondResourceStakeAndReservesOnlyItsPlayAp(string name)
        {
            var gear = Gear(name); var minted = ResearchProductionSystem.MintCard(gear);
            Assert.That(minted.ResearchProductionCreated, Is.True);
            Assert.That(minted.EffectivePlayResourceCost, Is.Null);
            Assert.That(new CardData(gear).EffectivePlayResourceCost, Is.SameAs(gear.resourceCost));
            Assert.That(minted.EffectivePlayApCost, Is.EqualTo(1));
            var host = Host(); host.apCost = 2; host.resourceCost = new ResourceCost { human = 1 };
            var card = new CardData(host);
            var plan = MaterializationPlanFactory.MakeExistingPlan(MaterializationChainKind.AttachDeploy,
                null, card, 0, minted, 1, new PlacementOption(default, DeploymentKind.ExistingArmy, null),
                EquipmentSystem.EffectiveAbilities(card, gear));
            Assert.That(plan.ApCost, Is.EqualTo(3));
            Assert.That(plan.ResCost.human, Is.EqualTo(1)); Assert.That(plan.ResCost.materials, Is.Zero);
            Assert.That(plan.ResCost.energy, Is.Zero); Assert.That(plan.ResCost.tech, Is.Zero);
            var consumed = new MaterializationConsumptionState(); var token = consumed.Push(plan);
            Assert.That(consumed.ExternalDisjoint(minted, null, null), Is.False);
            consumed.Pop(token); Assert.That(consumed.ApUsed, Is.Zero);
            Assert.That(consumed.HumanUsed, Is.Zero); Assert.That(consumed.CardsDisjoint(plan), Is.True);
        }

        [TestCase("Heavy MG")]
        [TestCase("Ceramic Vest")]
        [TestCase("Mortar Rack")]
        public void AuthoredEquipmentCreatesANewCombatCacheEntryAndRepeatReadsIt(string name)
        {
            var host = Host(); host.attack = 3; host.grantedAbilities.Clear();
            var gear = Gear(name);
            var before = new[] { AiPower.ToDefenderProfile(host, null, null) };
            var after = new[] { AiPower.ToDefenderProfile(host, gear, null) };
            var enemies = new[] { new WorthIt.DefenderProfile(3, false, attack: 4, hitPoints: 7, initiative: 2) };
            WorthIt.BeginEstimateCacheScope();
            try
            {
                WorthIt.Estimate(before, enemies, 0);
                var first = WorthIt.Estimate(after, enemies, 0);
                var repeat = WorthIt.Estimate(after, enemies, 0);
                var stats = WorthIt.EndEstimateCacheScope();
                Assert.That(stats.Misses, Is.EqualTo(2)); Assert.That(stats.Hits, Is.EqualTo(1));
                Assert.That(repeat.WinChance, Is.EqualTo(first.WinChance));
                Assert.That(repeat.ExpectedSurvivingHpRatioOnWin, Is.EqualTo(first.ExpectedSurvivingHpRatioOnWin));
            }
            finally { WorthIt.EndEstimateCacheScope(); }
        }

        [Test]
        public void TwinSmgUsesCanonicalCriticalMultiplierAndRetainsItsMeleeTradeoff()
        {
            var host = Host(); host.grantedAbilities.Clear();
            var projected = EquipmentSystem.Project(new CardData(host), Gear("Twin SMG"));
            Assert.That(projected.Stats[EquipmentStat.Attack], Is.EqualTo(6));
            Assert.That(projected.Stats[EquipmentStat.Range], Is.EqualTo(1));
            var damage = ChallengeResult.ApplyAbilityModifiers(2, projected.Abilities,
                new[] { UnitTypeTag.Bio }, Array.Empty<string>(), AbilityMagnitudes.Default);
            Assert.That(damage, Is.EqualTo(4));
            Assert.That(Gear("Twin SMG").fate, Is.EqualTo(Gear("Double Barrel").fate));
            // This is a contract check, not a claim that gameplay balance has been validated.
        }

        [Test, Category("UnityOnly")]
        public void UnityCatalogResolvesFortyCardsWithBothSpriteReferencesForAllFactions()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>(CatalogPath);
            Assert.That(catalog, Is.Not.Null);
            foreach (var faction in new[] { Faction.IronConcord, Faction.Ashen, Faction.Vessels })
            {
                var cards = catalog.ResolveFor(ResearchProductionMode.Production, faction);
                Assert.That(cards.Count, Is.GreaterThanOrEqualTo(40));
                foreach (var c in cards)
                {
                    Assert.That(c.attachmentSlot, Is.EqualTo(AttachmentSlot.Equipment));
                    Assert.That(c.art, Is.Not.Null, c.displayName); Assert.That(c.detailArt, Is.Not.Null, c.displayName);
                    var preview = EquipmentCardText.CardFace(c, null);
                    Assert.That(preview, Does.Contain("Unit").And.Not.Contain("required"));
                }
            }
        }
    }
}
#endif
