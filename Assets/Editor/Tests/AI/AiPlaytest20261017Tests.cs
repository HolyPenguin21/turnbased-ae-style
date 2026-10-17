#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Playtest 2026-10-17 (Ysolde captured Cassia's Citadel at T23): one regression per root cause.
    public class AiPlaytest20261017Tests
    {
        [SetUp]
        [TearDown]
        public void Reset()
        {
            AiReconMemory.Clear();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
        }

        private static ReconObservation Observation(HexCoord hex, int seenTurn, int move,
            bool garrison = false) => new ReconObservation
        {
            ArmyId = 15, Owner = new PlayerSetupData(), LastObservedHex = hex,
            LastObservedTurn = seenTurn, MaxMovement = move, IsGarrison = garrison,
        };

        // A building gives its owner sight of its hex and the ring around it (radius 1).
        private static PlayerSetupData ObserverSeeing(HexCoord hex)
        {
            var player = new PlayerSetupData { Nickname = "Cassia" };
            BuildingRegistry.Register(hex, new BuildingData { Owner = player, Hex = hex });
            VisionSystem.RecomputeFor(player);
            return player;
        }

        // Rust Bite stepped from its seen hex into fog: an empty last hex is not its end.
        [Test]
        public void ReconHistory_KeepsAnArmyThatCouldHaveLeftUnseen()
        {
            var last = new HexCoord(-3, 3);
            PlayerSetupData cassia = ObserverSeeing(last);
            Assert.That(VisionSystem.IsVisible(cassia, last), Is.True);

            Assert.That(AiReconMemory.GoneOnReobservation(cassia, 21, Observation(last, 20, 2), null),
                Is.False, "hexes two steps away are out of sight: the army may stand there");
        }

        [Test]
        public void ReconHistory_DropsAnArmyWhoseWholeReachIsInSight()
        {
            var last = new HexCoord(0, 0);
            PlayerSetupData cassia = ObserverSeeing(last);
            // Only the seen ring is enterable: nowhere unseen to go -> gone (e.g. destroyed).
            System.Func<HexCoord, bool, bool> onlyRing = (h, air) => HexGridMath.Distance(h, last) <= 1;

            Assert.That(AiReconMemory.GoneOnReobservation(cassia, 21, Observation(last, 20, 2), onlyRing),
                Is.True);
            Assert.That(AiReconMemory.GoneOnReobservation(cassia, 21,
                Observation(last, 20, 2, garrison: true), null), Is.True,
                "a building-bound garrison cannot walk away");
            Assert.That(AiReconMemory.GoneOnReobservation(cassia, 21,
                Observation(new HexCoord(9, 9), 20, 2), null), Is.False,
                "a last hex out of sight is not a re-observation");
        }

        private static AiMapMemory.KnownEnemySighting Enemy(HexCoord hex, int move, float defense = 3f) =>
            new AiMapMemory.KnownEnemySighting(hex, new PlayerSetupData(), "Rust Bite", 1, defense, 5f,
                new[] { new WorthIt.DefenderProfile(defense, false, attack: 5f, hitPoints: 5f) },
                armyId: 15, maxMovement: move);

        // T19: Rust Bite (-3,3) reached Base (-3,4) first and the Citadel (-5,1) second; the
        // Citadel kept its peacetime share and was stripped for a raid.
        [Test]
        public void ThreatFloor_CitadelCountsEveryEnemyThatReachesIt()
        {
            var baseHex = new HexCoord(-3, 4);
            var citadel = new HexCoord(-5, 1);
            AiMapMemory.KnownEnemySighting rustBite = Enemy(new HexCoord(-3, 3), 2);
            float expected = AiPower.SightingPower(rustBite) * AiConfigV2.aggDefenceConfidenceMargin;
            var garrisons = new[] { baseHex, citadel };

            Assert.That(AiArmyRoles.GarrisonThreatFloor(new[] { rustBite }, garrisons, baseHex),
                Is.EqualTo(expected).Within(1e-4f));
            Assert.That(AiArmyRoles.GarrisonThreatFloor(new[] { rustBite }, garrisons, citadel),
                Is.Zero, "an ordinary second garrison still yields to the nearest one");
            Assert.That(AiArmyRoles.GarrisonThreatFloor(new[] { rustBite }, garrisons, citadel, isCitadel: true),
                Is.EqualTo(expected).Within(1e-4f));
        }

        [Test]
        public void ThreatFloor_UnseenContactAdvancesAndFadesWithConfidence()
        {
            var citadel = new HexCoord(0, 0);
            AiMapMemory.KnownEnemySighting far = Enemy(new HexCoord(6, 0), 2);
            float full = AiPower.SightingPower(far) * AiConfigV2.aggDefenceConfidenceMargin;
            var contacts = new List<(AiMapMemory.KnownEnemySighting, int)> { (far, 0) };

            Assert.That(AiArmyRoles.GarrisonThreatFloor(contacts, new[] { citadel }, citadel, true),
                Is.Zero, "a live sighting six hexes out with move 2 is two turns short");
            contacts[0] = (far, 1);
            Assert.That(AiArmyRoles.GarrisonThreatFloor(contacts, new[] { citadel }, citadel, true),
                Is.EqualTo(full * AiReconMemory.ConfidenceDecay(1)).Within(1e-4f),
                "one turn unseen: it may have advanced one more turn's march");
        }

        // Cassia T16-T22: her Research operator stood in the full Citadel; the free Base never got
        // offered, so the Laboratory waited for a CapacityUnlock it never won. Engine-bound
        // (ScriptableObject catalog): runs in Unity EditMode.
        [Test]
        public void PreparationSite_FullStaffedBaseDoesNotLockWhileAFreeSlotBaseExists()
        {
            var player = new PlayerSetupData();
            var citadel = new HexCoord(-5, 1);
            var freeBase = new HexCoord(-6, 5);
            var full = new BuildingData { Owner = player, Hex = citadel, IsBase = true };
            for (int i = 0; i < full.UnlockedFacilitySlots; i++)
                full.FacilitySlots[i] = FacilityData.FromDefinition(new Game.Cards.CardDefinition
                    { cardType = Game.Cards.CardType.Facility, authoredKey = "filler" + i,
                      grantedAbilities = new List<string> { "Intel" } });
            BuildingRegistry.Register(citadel, full);
            var researcher = new Game.Units.UnitData { IsHero = true, MoveMax = 2 };
            researcher.Abilities.Add(Game.Cards.UnitAbilities.Researcher);
            var garrison = new ArmyData { Owner = player, Hex = citadel, IsGarrison = true };
            garrison.Members.Add(researcher);
            ArmyRegistry.Register(garrison);

            var hand = new AiHandData(null, default, 0);
            hand.AddCard(new Game.Cards.CardData(new Game.Cards.CardDefinition
            {
                cardType = Game.Cards.CardType.Facility, authoredKey = "laboratory",
                resourceCost = new Game.Cards.ResourceCost(human: 1, energy: 1, tech: 1),
                grantedAbilities = new List<string> { Game.Cards.UnitAbilities.Research },
            }));
            var output = new Game.Cards.CardDefinition
                { cardType = Game.Cards.CardType.Equipment, authoredKey = "mutator" };
            var cards = UnityEngine.ScriptableObject.CreateInstance<Game.Cards.FactionCardCatalog>();
            cards.cards.Add(output);
            var catalog = UnityEngine.ScriptableObject.CreateInstance<Game.Cards.ResearchProductionCatalog>();
            catalog.cardCatalogs.Add(cards);
            catalog.researchCards.Add(new Game.Cards.ResearchProductionEntry { cardKey = output.authoredKey });
            var config = UnityEngine.ScriptableObject.CreateInstance<Game.Core.GameConfig>();
            config.baseUpgradeTiers = new[]
                { new Game.Cards.BaseUpgradeTier { apCost = 2, cost = new Game.Cards.ResourceCost(1, 1, 1, 1) } };
            var ctx = new AiTurnContext { TurnNumber = 19, ResearchProductionCatalog = catalog, GameConfig = config };
            try
            {
                Assert.That(DevelopmentOpportunityEvaluator.SelectedPreparationSite(
                        Game.Cards.ResearchProductionMode.Research, player, hand, ctx, (IMissionIntentView)null),
                    Is.EqualTo(citadel), "the only base: its operator keeps it the site");

                BuildingRegistry.Register(freeBase, new BuildingData { Owner = player, Hex = freeBase, IsBase = true });
                Assert.That(DevelopmentOpportunityEvaluator.SelectedPreparationSite(
                        Game.Cards.ResearchProductionMode.Research, player, hand, ctx, (IMissionIntentView)null),
                    Is.Null, "a free-slot base stays an alternative to the unlock path");
            }
            finally
            {
                ArmyRegistry.Clear();
                UnityEngine.Object.DestroyImmediate(catalog);
                UnityEngine.Object.DestroyImmediate(cards);
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        // Ysolde T15-T20: the host already past the power bar, one defender nobody can damage.
        [Test]
        public void Coverage_CountsTheDefendersNoAttackerCanDamage()
        {
            var weak = new WorthIt.DefenderProfile(1f, false, attack: 3f, hitPoints: 4f);
            var brick = new WorthIt.DefenderProfile(9f, false, attack: 2f, hitPoints: 6f);
            var soft = new WorthIt.DefenderProfile(1f, false, attack: 2f, hitPoints: 4f);
            var heavy = new WorthIt.DefenderProfile(2f, false, attack: 12f, hitPoints: 6f);
            var opposition = new[] { new WorthIt.DefendingArmy(new[] { brick, soft }, default) };

            Assert.That(WorthIt.UncoveredDefenderCount(new[] { weak }, opposition), Is.EqualTo(1));
            Assert.That(WorthIt.CanDamageAll(new[] { weak }, opposition), Is.False);
            Assert.That(WorthIt.UncoveredDefenderCount(new[] { weak, heavy }, opposition), Is.Zero);
            Assert.That(WorthIt.CanDamageAll(new[] { weak, heavy }, opposition), Is.True);
            Assert.That(WorthIt.UncoveredDefenderCount(System.Array.Empty<WorthIt.DefenderProfile>(),
                System.Array.Empty<WorthIt.DefendingArmy>()), Is.Zero);
        }
    }
}
#endif
