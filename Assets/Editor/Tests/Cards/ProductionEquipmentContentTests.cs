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
            .Where(c => c.cardType == CardType.Equipment && c.equipment.hostKinds.Contains(EquipmentHostKind.Unit) && Keys("productionCards").Contains(c.authoredKey)).Select(c => c.displayName);
        private static CardDefinition Host() => AttachmentSlotTests.Host();

        [Test]
        public void CatalogContainsAllPhysicalEquipmentAndNoResearchMutators()
        {
            var cards = Cards(); var keys = Keys("productionCards");
            var physical = cards.Where(c => c.cardType == CardType.Equipment
                && c.attachmentSlot == AttachmentSlot.Equipment && c.equipment.hostKinds.Contains(EquipmentHostKind.Unit)).ToArray();
            Assert.That(keys.Length, Is.GreaterThanOrEqualTo(47));
            Assert.That(keys.Distinct().Count(), Is.EqualTo(keys.Length));
            Assert.That(keys, Is.EquivalentTo(physical.Select(c => c.authoredKey).Concat(new[] { "neutral.tactic.tactical-missile" })));
            Assert.That(keys.Intersect(Keys("researchCards")), Is.Empty);
            var allKeys = FactionPaths.Concat(new[] { NeutralPath }).SelectMany(Blocks).Select(Read)
                .Select(c => c.authoredKey).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            Assert.That(allKeys.Distinct().Count(), Is.EqualTo(allKeys.Count));
            foreach (var c in physical)
            {
                Assert.That(c.authoredKey, Does.StartWith("neutral.equipment."));
                Assert.That(c.displayName, Does.Not.StartWith("it "));
                Assert.That(c.equipment.hostKinds, Is.EqualTo(new[] { EquipmentHostKind.Unit }));
                Assert.That(c.apCost, EquipmentSystem.IsSingleUse(c) ? Is.InRange(3, 5) : Is.EqualTo(1));
                Assert.That(c.activationApCost, Is.EqualTo(1));
                Assert.That(c.fate, Is.InRange(3, 6));
            }
            Assert.That(Gear("AT VH Launcher").authoredKey,
                Is.EqualTo("neutral.equipment.armored.at-vh-launcher"));
        }

        [Test]
        public void LateWeaponReferenceUtilityPricesTheFirstAttackOnly()
        {
            var targets = Enumerable.Repeat(new WorthIt.DefenderProfile(2, false,
                typeTags: new[] { UnitTypeTag.Armored }, attack: 3, hitPoints: 6, initiative: 1), 5).ToArray();
            var stats = new EfficiencyStats(1, 2, 12, 2, 3, 3, 1, 0);
            foreach (string name in new[] { "Infantry Missile", "Vehicle Missile", "Air Missile", "Air Bomb 3", "Air Bomb 5" })
            {
                var gear = Gear(name);
                var host = new CardDefinition { cardType = CardType.Unit, attack = 1, defenseRating = 2,
                    hitPoints = 12, range = 2, moveMax = 3, initiative = 3, activationApCost = 1 };
                var ctx = new EfficiencyContext { Targets = targets,
                    Battles = new List<IReadOnlyList<WorthIt.DefenderProfile>> { targets },
                    ExpectedActivations = 3, AfterConsumption = EquipmentSystem.Consumption(host, gear, null) };
                var projected = EquipmentSystem.Project(host, gear, null);
                var after = new EfficiencyStats(projected.Stats[EquipmentStat.Attack], 2, 12,
                    projected.Stats[EquipmentStat.Range], 3, 3, 1, 0);
                float utility = gear.equipment.hostTypeTags.Contains(UnitTypeTag.Aircraft)
                    ? EquipmentEfficiency.AviationOffenseDelta(stats, Array.Empty<string>(), after, gear.equipment.addAbilities, ctx)
                    : EquipmentEfficiency.Utility(stats, Array.Empty<string>(), after, gear.equipment.addAbilities, ctx).Total;
                TestContext.WriteLine($"{name}: reference utility={utility:0.000} (before production price/chance)");
                Assert.That(float.IsNaN(utility) || float.IsInfinity(utility), Is.False);
            }
        }

        [Test]
        public void LateWeaponsUseExistingExplosionAndConsumptionRulesWithDistinctHosts()
        {
            var cards = Cards();
            var blueprint = cards.Single(c => c.authoredKey == "neutral.tactic.tactical-missile");
            var missile = cards.Single(c => c.authoredKey == "neutral.production.unit.tactical-missile");
            Assert.That(blueprint.cardType, Is.EqualTo(CardType.Equipment));
            Assert.That(blueprint.isAviation, Is.False);
            Assert.That(blueprint.equipment.hostKinds, Has.No.Member(EquipmentHostKind.Unit));
            Assert.That(blueprint.equipment.hostKinds, Has.No.Member(EquipmentHostKind.Hero));
            Assert.That(Text(CatalogPath), Does.Contain("resultCardKey: " + missile.authoredKey));
            Assert.That(Scalar(Blocks(NeutralPath).Single(b => Scalar(b, "authoredKey") == missile.authoredKey), "deckBuilderExcluded"), Is.EqualTo("1"));
            Assert.That(missile.isAviation, Is.True);
            Assert.That(DeckRules.Category(blueprint), Is.EqualTo(DeckCategory.Equipment));
            Assert.That(ExplosionAttack.IsSingleUseUnit(missile), Is.True);
            Assert.That(ExplosionAttack.TargetLimit(missile.grantedAbilities), Is.EqualTo(10));
            float before = ResearchProductionSystem.EstimateSuccessChanceForFate(4, blueprint);
            float after = ResearchProductionSystem.EstimateSuccessChanceForFate(6, blueprint);
            Assert.That(after, Is.GreaterThan(before));
            TestContext.WriteLine($"Tactical Missile production: Fate4={before:0.000}, Fate6={after:0.000}");
            string[] names = { "Infantry Missile", "Vehicle Missile", "Air Missile", "Air Bomb 3", "Air Bomb 5" };
            foreach (string name in names)
            {
                var gear = Gear(name);
                Assert.That(EquipmentSystem.IsSingleUse(gear), Is.True);
                Assert.That(ExplosionAttack.TargetLimit(gear.equipment.addAbilities),
                    Is.EqualTo(name.StartsWith("Air Bomb") ? name == "Air Bomb 5" ? 5 : 3 : 0));
                if (!name.StartsWith("Air Bomb"))
                {
                    Assert.That(gear.equipment.addAbilities, Is.Empty);
                    Assert.That(gear.equipment.statChanges.Single(c => c.stat == EquipmentStat.Range).amount,
                        Is.EqualTo(name == "Air Missile" ? 0 : 2));
                    Assert.That(gear.equipment.statChanges.Single(c => c.stat == EquipmentStat.Attack).amount,
                        Is.EqualTo(name == "Infantry Missile" ? 8 : name == "Vehicle Missile" ? 10 : 12));
                }
                Assert.That(gear.equipment.addAbilities, Has.No.Member(UnitAbilities.SingleUse),
                    "consuming the attachment must never mark the host as a disposable unit");
                Assert.That(Keys("productionCards"), Does.Contain(gear.authoredKey));
            }
            Assert.That(Gear("Infantry Missile").equipment.hostTypeTags, Is.EqualTo(new[] { UnitTypeTag.Infantry }));
            Assert.That(Gear("Vehicle Missile").equipment.hostTypeTags, Is.EquivalentTo(new[] { UnitTypeTag.Vehicle, UnitTypeTag.Mecha }));
            foreach (string name in names.Skip(2))
                Assert.That(Gear(name).equipment.hostTypeTags, Is.EqualTo(new[] { UnitTypeTag.Aircraft }));
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
        }

        [TestCase("Assault Rifle Kit", 6, 3, 10, 6, 2)]
        [TestCase("Heavy MG", 5, 3, 10, 6, 2)]
        [TestCase("Plasma Gun", 8, 3, 10, 6, 2)]
        [TestCase("Marksman Rifle", 4, 3, 10, 6, 3)]
        [TestCase("Optical Scope", 5, 3, 10, 6, 3)]
        [TestCase("Mobility Harness", 5, 3, 10, 7, 2)]
        [TestCase("Shotgun", 6, 3, 10, 6, 1)]
        [TestCase("Ballistic Shield", 5, 4, 10, 6, 2)]
        [TestCase("Reinforced Chassis", 5, 3, 12, 6, 2)]
        [TestCase("Reactive Armor", 5, 5, 10, 6, 2)]
        [TestCase("Turbocharger", 5, 3, 10, 7, 2)]
        [TestCase("Dozer Blade", 6, 4, 10, 6, 2)]
        [TestCase("Siege Ram", 7, 3, 10, 6, 1)]
        [TestCase("Mortar Rack", 5, 3, 10, 5, 3)]
        [TestCase("Flame Projector", 6, 3, 10, 6, 1)]
        [TestCase("Recoil Cannon", 7, 3, 10, 5, 2)]
        [TestCase("Portable Mortar", 4, 3, 10, 5, 3)]
        [TestCase("Assault Conversion Kit", 5, 4, 12, 6, 2)]
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
            var delta = EquipmentUpgradeValuation.EquipmentDeltaParts(gear, host);
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
        [TestCase("Rail Rifle")]
        [TestCase("Shotgun")]
        [TestCase("Marksman Rifle")]
        public void WeaponReplacementClearsOldWeaponEffectsButKeepsNonWeaponAbilities(string name)
        {
            var host = Host(); host.grantedAbilities.AddRange(new[] { UnitAbilities.Scorcher,
                UnitAbilities.Splash, UnitAbilities.CriticalDamage, UnitAbilities.ShockAttack,
                UnitAbilities.Hyperkinetic, UnitAbilities.Pyrokinetic, UnitAbilities.AntiAir });
            var body = AttachmentSlotTests.Body(host);
            EquipmentSystem.ApplyAttachments(body, Gear(name), null);
            Assert.That(body.Abilities, Is.EquivalentTo(new[] { UnitAbilities.CeramicArmor, UnitAbilities.R1S4 }
                .Concat(name == "Rail Rifle" ? new[] { UnitAbilities.Hyperkinetic } : Array.Empty<string>())));
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

        [TestCase("Shotgun")]
        [TestCase("Marksman Rifle")]
        [TestCase("Optical Scope")]
        [TestCase("Mobility Harness")]
        public void InfantryWeaponsAcceptMechanicalInfantryAndDoNotSpreadToHeroes(string name)
        {
            var roster = FactionPaths.SelectMany(Blocks).Select(Read).ToList();
            var infantry = roster.Where(c => c.cardType == CardType.Unit
                && c.unitTypeTags.Contains(UnitTypeTag.Infantry) && c.unitTypeTags.Contains(UnitTypeTag.Mechanical)).ToList();
            Assert.That(infantry, Is.Not.Empty);
            foreach (var host in infantry)
                Assert.That(EquipmentSystem.FitsHost(Gear(name), host, out _), Is.True, host.displayName);
            foreach (var hero in roster.Where(c => c.cardType == CardType.Hero))
                Assert.That(EquipmentSystem.FitsHost(Gear(name), hero, out _), Is.False, hero.displayName);
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

        [TestCase("Optical Scope")]
        [TestCase("Mobility Harness")]
        [TestCase("Dozer Blade")]
        [TestCase("Assault Conversion Kit")]
        public void InfantryAddonsPreserveExistingWeaponAndUtilityAbilities(string name)
        {
            var host = Host();
            host.grantedAbilities.AddRange(new[] { UnitAbilities.Splash, UnitAbilities.ShockAttack,
                UnitAbilities.Hyperkinetic, UnitAbilities.Pyrokinetic, UnitAbilities.Scorcher,
                UnitAbilities.CriticalDamage, UnitAbilities.AntiAir });
            var expected = host.grantedAbilities.Distinct().ToArray();
            var gear = Gear(name);
            var predicted = EquipmentSystem.Project(new CardData(host), gear);
            Assert.That(predicted.Abilities, Is.EquivalentTo(expected));
            var body = AttachmentSlotTests.Body(host);
            EquipmentSystem.ApplyAttachments(body, gear, null);
            Assert.That(body.Abilities, Is.EquivalentTo(expected));
            Assert.That(host.grantedAbilities.Distinct(), Is.EquivalentTo(expected));
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
            Assert.That(projected.Stats[EquipmentStat.Attack], Is.EqualTo(5));
            Assert.That(projected.Stats[EquipmentStat.Range], Is.EqualTo(1));
            var damage = ChallengeResult.ApplyAbilityModifiers(2, projected.Abilities,
                new[] { UnitTypeTag.Bio }, Array.Empty<string>(), AbilityMagnitudes.Default);
            Assert.That(damage, Is.EqualTo(4));
            Assert.That(Gear("Twin SMG").fate, Is.LessThan(Gear("Double Barrel").fate));
            // This is a contract check, not a claim that gameplay balance has been validated.
        }

        [Test, Category("UnityOnly")]
        public void UnityCatalogResolvesFortySevenCardsWithBothSpriteReferencesForAllFactions()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>(CatalogPath);
            Assert.That(catalog, Is.Not.Null);
            foreach (var faction in new[] { Faction.IronConcord, Faction.Ashen, Faction.Vessels })
            {
                var cards = catalog.ResolveFor(ResearchProductionMode.Production, faction);
                Assert.That(cards.Count, Is.GreaterThanOrEqualTo(47));
                foreach (var c in cards)
                {
                    Assert.That(c.attachmentSlot, Is.EqualTo(AttachmentSlot.Equipment));
                    Assert.That(c.art, Is.Not.Null, c.displayName); Assert.That(c.detailArt, Is.Not.Null, c.displayName);
                    if (c.authoredKey == "neutral.tactic.tactical-missile")
                    { Assert.That(catalog.ResolveResult(c).cardType, Is.EqualTo(CardType.Unit)); continue; }
                    var preview = EquipmentCardText.Description(c, null);
                    Assert.That(preview, Does.Contain("Unit").And.Not.Contain("required"));
                }
            }
        }
        private static readonly string[] PartNames = { "Ballistic Shield", "Assault Rifle Kit", "Optical Scope",
            "Armor Plate", "Reinforced Chassis", "Reactive Armor", "Ceramic Vest", "Ceramic Plating",
            "Mobility Harness", "Servo Actuators", "Turbocharger", "Nuclear Engine", "Dozer Blade", "Assault Conversion Kit" };

        [Test]
        public void EveryWeaponHasOwnAttackAndRangeOnEveryCompatibleHostAndMutator()
        {
            var cards = Cards();
            var weapons = cards.Where(c => c.cardType == CardType.Equipment
                && c.authoredKey.StartsWith("neutral.equipment.") && !PartNames.Contains(c.displayName)).ToArray();
            Assert.That(weapons.Length, Is.EqualTo(33));
            var hosts = FactionPaths.SelectMany(Blocks).Select(Read)
                .Where(c => c.cardType == CardType.Unit || c.cardType == CardType.Hero).ToArray();
            int combinations = 0;
            foreach (var weapon in weapons)
            {
                var attack = weapon.equipment.statChanges.Single(c => c.stat == EquipmentStat.Attack);
                var range = weapon.equipment.statChanges.Single(c => c.stat == EquipmentStat.Range);
                Assert.That(attack.isOverride && range.isOverride, Is.True, weapon.displayName);
                foreach (var host in hosts.Where(h => EquipmentSystem.FitsHost(weapon, h, out _)))
                foreach (var mutator in new CardDefinition[] { null }.Concat(cards.Where(c =>
                    c.attachmentSlot == AttachmentSlot.Mutator && EquipmentSystem.FitsHost(c, host, out _))))
                {
                    var p = EquipmentSystem.Project(host, weapon, mutator);
                    int Bonus(EquipmentStat stat) => mutator?.equipment.statChanges
                        .Where(c => c.stat == stat).Sum(c => c.amount) ?? 0;
                    string context = $"{weapon.displayName}/{host.displayName}/{mutator?.displayName}";
                    Assert.That(p.Stats[EquipmentStat.Attack], Is.EqualTo(attack.amount + Bonus(EquipmentStat.Attack)), context);
                    Assert.That(p.Stats[EquipmentStat.Range], Is.EqualTo(System.Math.Max(1, range.amount) + Bonus(EquipmentStat.Range)), context);
                    foreach (string removed in weapon.equipment.removeAbilities)
                        if (!weapon.equipment.addAbilities.Contains(removed)
                            && !(mutator?.equipment.addAbilities.Contains(removed) ?? false))
                            Assert.That(p.Abilities, Has.No.Member(removed), context);
                    combinations++;
                }
            }
            TestContext.WriteLine($"Validated {combinations} compatible weapon/host/mutator combinations");
            Assert.That(combinations, Is.GreaterThan(10000));
            foreach (var part in cards.Where(c => PartNames.Contains(c.displayName)))
            {
                Assert.That(part.equipment.statChanges.All(c => !c.isOverride), Is.True, part.displayName);
                Assert.That(part.equipment.removeAbilities, Is.Empty, part.displayName);
            }
        }

        [TestCase("Infantry Missile")]
        [TestCase("Vehicle Missile")]
        [TestCase("Air Missile")]
        [TestCase("Air Bomb 5")]
        public void DisposableWeaponCannotInheritAnOldWarheadAndConsumptionRestoresBothSlots(string name)
        {
            var weapon = Gear(name);
            var host = FactionPaths.SelectMany(Blocks).Select(Read)
                .Where(h => EquipmentSystem.FitsHost(weapon, h, out _))
                .OrderByDescending(h => h.unitTypeTags.Contains(UnitTypeTag.Bio)).First();
            host.grantedAbilities.AddRange(new[] { UnitAbilities.CriticalDamage, "Ionic", "Explosion10" });
            var mutation = Cards().Single(c => c.authoredKey == "neutral.mutator.neural-accelerator");
            if (!EquipmentSystem.FitsHost(mutation, host, out _)) mutation = null;
            var body = AttachmentSlotTests.Body(host);
            body.IsAviation = host.isAviation;
            EquipmentSystem.ApplyAttachments(body, null, mutation);
            int attack = body.Attack, range = body.Range, defense = body.Defense, hp = body.HitPointsMax;
            var abilities = body.Abilities.ToArray();
            EquipmentSystem.ApplyAttachments(body, weapon, null);
            Assert.That(body.Abilities, Has.No.Member(UnitAbilities.CriticalDamage));
            Assert.That(body.Abilities, Has.No.Member("Ionic"));
            Assert.That(body.Abilities, Has.No.Member("Explosion10"));
            Assert.That(EquipmentSystem.ConsumeAfterAttack(body), Is.True);
            Assert.That(new[] { body.Attack, body.Range, body.Defense, body.HitPointsMax },
                Is.EqualTo(new[] { attack, range, defense, hp }));
            Assert.That(body.Abilities, Is.EquivalentTo(abilities));
            Assert.That(body.Equipment, Is.Null); Assert.That(body.Mutator, Is.SameAs(mutation));
        }

        [Test]
        public void SelectedVesselsKeepInnateAdvantagesAndStillRejectBioGenomes()
        {
            var vessels = Blocks(FactionPaths[2]).Select(Read).ToArray();
            var expected = new Dictionary<string, (int Attack, int Defense, int Hp)> {
                ["Vessel Rifleman"] = (3, 1, 5), ["Bulwark Vessel"] = (4, 4, 5),
                ["Spindle Gunner"] = (5, 2, 4), ["Lens Watcher"] = (6, 1, 4),
                ["Bell Tortoise"] = (6, 4, 8), ["Heavy Sentinel"] = (6, 4, 8),
                ["Palisade Frame"] = (5, 5, 12) };
            foreach (var row in expected)
            {
                var c = vessels.Single(v => v.displayName == row.Key);
                Assert.That((c.attack, c.defenseRating, c.hitPoints), Is.EqualTo(row.Value), row.Key);
            }
            foreach (var host in vessels.Where(c => c.cardType == CardType.Unit || c.cardType == CardType.Hero))
            foreach (var genome in Cards().Where(c => c.attachmentSlot == AttachmentSlot.Mutator))
                Assert.That(EquipmentSystem.FitsHost(genome, host, out _), Is.False, host.displayName);
        }
    }
}
#endif

