#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    [Category("UnityNative")]
    public sealed class CardCommandTransactionTests
    {
        private PlayerSetupData owner;
        private PlayerRoot root;
        private HexMap map;
        private readonly HexCoord hex = new HexCoord(1, 1);
        [SetUp] public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear(); VisionSystem.Clear(); StealthSystem.Clear();
            VisionSystem.Configure(null);
            owner = new PlayerSetupData { PlayerId = "commands", IsHuman = true, Hand = new PlayerHandData(null, 0) };
            root = PlayerRoot.Create(owner, "commands account"); PlayerRootRegistry.Register(owner, root);
            root.ActionPoints = 10; root.AddResource(ResourceType.Tech, 10);
            map = new GameObject("commands terrain").AddComponent<HexMap>();
            map.SetData(2, 1, new Dictionary<HexCoord, TerrainTypeEntry> { [hex] = new TerrainTypeEntry() });
        }
        [TearDown] public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear(); VisionSystem.Clear(); StealthSystem.Clear();
            UnityEngine.Object.DestroyImmediate(root.gameObject); UnityEngine.Object.DestroyImmediate(map.gameObject);
        }
        private CardData Card(CardType type = CardType.Facility)
        {
            var definition = new CardDefinition { authoredKey = "command.card", cardType = type,
                apCost = 3, activationApCost = 1, resourceCost = new ResourceCost(tech: 2),
                displayName = "Card", hitPoints = 5, requiredBuildingAbility = UnitAbilities.Barracks };
            var card = new CardData(definition); owner.Hand.AddCard(card); return card;
        }
        private BuildingData Base()
        {
            var b = new BuildingData { Owner = owner, Hex = hex, IsBase = true };
            b.Abilities.Add(UnitAbilities.Barracks);
            using (MatchRestoreScope.Begin()) BuildingRegistry.Register(hex, b);
            return b;
        }
        private ArmyData Garrison()
        {
            Base();
            var a = new ArmyData { Owner = owner, Hex = hex, IsGarrison = true };
            using (MatchRestoreScope.Begin()) ArmyRegistry.Register(a); return a;
        }
        private string Snapshot() => root.ActionPoints + ":" + string.Join(",", Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().Select(root.GetResource)) + ":"
            + owner.Hand.MutationVersion + ":" + owner.Hand.Capacity + ":"
            + string.Join(",", owner.Hand.RemainingDeck.Select(c => c.authoredKey)) + ":"
            + string.Join(",", owner.Hand.Hand.Select(c => c.RuntimeId + "/" + c.ResearchProductionCreated + "/" + c.Equipment?.authoredKey + "/" + c.Mutator?.authoredKey)) + ":"
            + string.Join(",", ArmyRegistry.AllArmies().Select(a => a.Id + "/" + a.Hex + "/" + string.Join(";", a.Members.Select(u => u.RuntimeId + "@" + u.HitPointsCurrent + "@" + u.Fate + "@" + u.MoveCurrent)))) + ":"
            + string.Join(",", BuildingRegistry.AllBuildings().Select(b => b.Hex + "/" + b.Name + "/" + b.Level + "/" + b.StructurePointsCurrent + "/" + string.Join(";", b.FacilitySlots.Select(f => f?.Name)))) + ":"
            + UnitData.IdentityHighWaterMark + ":" + CardData.IdentityHighWaterMark;

        [Test] public void DebitObserversSeeAllDebitsTogetherAndCompensationIsOneUse()
        {
            var quote = new PaymentQuote(2, new ResourceCost(tech: 3));
            var seen = new List<string>(); root.ResourcesChanged += () => seen.Add(root.ActionPoints + "/" + root.GetResource(ResourceType.Tech));
            Assert.That(quote.TryDebit(root, out var receipt, out _), Is.True);
            Assert.That(seen, Is.Not.Empty); Assert.That(seen.All(s => s == "8/7"), Is.True);
            root.AddResource(ResourceType.Tech, 1);
            Assert.That(receipt.TryCompensate(), Is.True); Assert.That(receipt.TryCompensate(), Is.False);
            Assert.That(root.ActionPoints, Is.EqualTo(10)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(11));
        }
        [Test] public void RejectedDebitDoesNotPartiallySpendAndQuoteIsDetachedFromMutableCost()
        {
            var cost = new ResourceCost(tech: 3); var quote = new PaymentQuote(2, cost); cost.tech = 99;
            Assert.That(quote.TryDebit(root, out _, out _), Is.True);
            Assert.That(root.ActionPoints, Is.EqualTo(8)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(7));
            string before = Snapshot(); Assert.That(new PaymentQuote(2, new ResourceCost(tech: 8)).TryDebit(root, out var failed, out _), Is.False);
            Assert.That(failed, Is.Null); Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(new PaymentQuote(-2, null).TryDebit(root, out _, out _), Is.False);
            Assert.That(new PaymentQuote(0, new ResourceCost(tech: -1)).TryDebit(root, out _, out _), Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before));
        }
        [Test] public void PlayRejectsForeignAndAlreadyConsumedRuntimeCardWithoutMutation()
        {
            Base(); var card = Card(); var other = new PlayerSetupData { Hand = new PlayerHandData(null, 0) };
            string before = Snapshot();
            Assert.That(CardHandCommands.TryPlay(other, owner.Hand, card, CardPlayTarget.AtHex(hex), map).Ok, Is.False);
            Assert.That(CardHandCommands.TryPlay(owner, owner.Hand, CardData.CreateDetached(card.Definition, card.RuntimeId), CardPlayTarget.AtHex(hex), map).Ok, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(CardHandCommands.TryPlay(owner, owner.Hand, card, CardPlayTarget.AtHex(hex), map).Ok, Is.True);
            before = Snapshot();
            Assert.That(CardHandCommands.TryPlay(owner, owner.Hand, card, CardPlayTarget.AtHex(hex), map).Ok, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before));
        }
        [TestCase(false)] [TestCase(true)] public void PlayFromHexAndModalHasIdenticalCostAndResult(bool modal)
        {
            var b = Base(); var card = Card();
            var result = CardHandCommands.TryPlay(owner, owner.Hand, card, modal ? CardPlayTarget.InBuilding(b) : CardPlayTarget.AtHex(hex), map);
            Assert.That(result.Ok, Is.True, result.FailReason); Assert.That(root.ActionPoints, Is.EqualTo(7));
            Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(8)); Assert.That(owner.Hand.Hand, Is.Empty);
            Assert.That(b.FacilitySlots[0]?.Name, Is.EqualTo("Card"));
        }
        [Test] public void SuccessfulPlayPublishesOnlyFinalAccountHandAndWorld()
        {
            var b = Base(); var card = Card(); var seen = new List<bool>();
            root.ResourcesChanged += () => seen.Add(!owner.Hand.Hand.Contains(card) && b.FacilitySlots[0] != null && root.ActionPoints == 7 && root.GetResource(ResourceType.Tech) == 8);
            owner.Hand.HandChanged += () => seen.Add(!owner.Hand.Hand.Contains(card) && b.FacilitySlots[0] != null && root.ActionPoints == 7 && root.GetResource(ResourceType.Tech) == 8);
            var result = CardHandCommands.TryPlay(owner, owner.Hand, card, CardPlayTarget.AtHex(hex), map);
            Assert.That(result.Ok, Is.True, result.FailReason); Assert.That(seen, Is.Not.Empty); Assert.That(seen.All(x => x), Is.True);
        }
        [TestCase("ap")] [TestCase("resources")] [TestCase("capacity")] [TestCase("terrain")] [TestCase("stale")]
        public void RefusalPreservesCompleteState(string why)
        {
            var b = Base(); var card = Card();
            if (why == "ap") root.ActionPoints = 2;
            if (why == "resources") root.AddResource(ResourceType.Tech, -9);
            if (why == "capacity") for (int i = 0; i < b.UnlockedFacilitySlots; i++) b.FacilitySlots[i] = new FacilityData();
            if (why == "terrain") map.SetTerrainAt(hex, new TerrainTypeEntry { blocksGroundMovement = true });
            if (why == "stale") { using (MatchRestoreScope.Begin()) BuildingRegistry.Unregister(hex); }
            string before = Snapshot();
            Assert.That(CardHandCommands.TryPlay(owner, owner.Hand, card, CardPlayTarget.InBuilding(b), map).Ok, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before));
        }
        [TestCase(false)] [TestCase(true)] public void GroundDeploymentDoesNotRequirePresentation(bool produced)
        {
            var a = Garrison(); var card = Card(CardType.Unit); card.ResearchProductionCreated = produced;
            var result = CardHandCommands.TryPlay(owner, owner.Hand, card, CardPlayTarget.InArmy(a), map);
            Assert.That(result.Ok, Is.True, result.FailReason); Assert.That(a.Members.Count, Is.EqualTo(1));
            Assert.That(root.ActionPoints, Is.EqualTo(produced ? 9 : 7)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(produced ? 10 : 8));
            Assert.That(owner.Hand.Hand, Is.Empty);
        }
        [Test] public void ReentrantPlayCannotConsumeTwice()
        {
            Base(); var card = Card(); CardCommandResult second = default;
            root.ResourcesChanged += () => second = CardHandCommands.TryPlay(owner, owner.Hand, card, CardPlayTarget.AtHex(hex), map);
            Assert.That(CardHandCommands.TryPlay(owner, owner.Hand, card, CardPlayTarget.AtHex(hex), map).Ok, Is.True);
            Assert.That(second.Ok, Is.False); Assert.That(root.ActionPoints, Is.EqualTo(7)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(8));
        }
        [Test] public void PaidDrawPublishesDebitWithCardAndRejectsFullHand()
        {
            owner.Hand = new PlayerHandData(new[] { new CardDefinition { authoredKey = "draw", cardType = CardType.Unit } }, 0, 1);
            bool consistent = false; owner.Hand.HandChanged += () => consistent = root.ActionPoints == 8 && owner.Hand.Hand.Count == 1 && owner.Hand.RemainingDeckCount == 0;
            var result = CardHandCommands.TryDraw(owner, owner.Hand, 2);
            Assert.That(result.Ok, Is.True, result.FailReason); Assert.That(consistent, Is.True);
            string before = Snapshot(); Assert.That(CardHandCommands.TryDraw(owner, owner.Hand, 2).Ok, Is.False); Assert.That(Snapshot(), Is.EqualTo(before));
        }

        [Test] public void InfrastructureFailureRefundsButPaidProductionLossAndSingleUseDoNot()
        {
            var a = Garrison(); a.IsGarrison = false;
            var hero = new UnitData { Owner = owner, IsHero = true }; a.Members.Add(hero);
            using (MatchRestoreScope.Begin()) BuildingRegistry.Unregister(hex);
            string before = Snapshot();
            var failed = InfrastructureActions.TryFoundBaseOnMap(map,
                new CardDefinition { cardType = CardType.Base, grantedAbilities = new List<string> { UnitAbilities.Barracks } },
                hex, owner, 2, new ResourceCost(tech: 3), completeBeforeCommit: _ => false);
            Assert.That(failed.Ok, Is.False); Assert.That(Snapshot(), Is.EqualTo(before));
            var blueprint = new CardDefinition { authoredKey = "paid", cardType = CardType.Equipment };
            var state = new BlueprintQuotaState(); state.unlockedKeys.Add("paid");
            var quota = BlueprintQuota.FromState(state); Assert.That(quota.TryReserve("paid"), Is.True);
            Assert.That(new PaymentQuote(2, new ResourceCost(tech: 3)).TryDebit(root, out _, out _), Is.True);
            var attempt = new ProductionAttempt(blueprint, quota, blueprint, owner, root, owner.Hand);
            Assert.That(attempt.CompleteToHand(false), Is.Null);
            Assert.That(quota.Available("paid"), Is.True);
            var item = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment); item.grantedAbilities.Add(UnitAbilities.SingleUse);
            var unit = AttachmentSlotTests.Body(); EquipmentSystem.ApplyAttachments(unit, item, null);
            Assert.That(EquipmentSystem.ConsumeAfterAttack(unit), Is.True);
            Assert.That(root.ActionPoints, Is.EqualTo(8)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(7));
        }
        [Test] public void RepeatedCommitDoesNotConsumeTwiceAndProductionMayOverflow()
        {
            owner.Hand = new PlayerHandData(null, 0, 0);
            var blueprint = new CardDefinition { authoredKey = "repeat" };
            var state = new BlueprintQuotaState(); state.unlockedKeys.Add("repeat");
            var quota = BlueprintQuota.FromState(state); quota.TryReserve("repeat");
            var attempt = new ProductionAttempt(blueprint, quota, blueprint, owner, root, owner.Hand);
            var produced = attempt.CompleteToHand(true);
            Assert.That(produced, Is.Not.Null); Assert.That(produced.ResearchProductionCreated, Is.True);
            string before = Snapshot(); Assert.That(attempt.CompleteToHand(true), Is.Null); Assert.That(attempt.Complete(true), Is.Null);
            Assert.That(Snapshot(), Is.EqualTo(before)); Assert.That(owner.Hand.Hand.Count, Is.EqualTo(1));
            Assert.That(quota.Available("repeat"), Is.True);
        }
        [TestCase("dispose")] [TestCase("hand")] [TestCase("world")]
        public void StaleProductionCompletionCannotMintOrDeliver(string change)
        {
            var blueprint = new CardDefinition { authoredKey = "stale" };
            var originalHand = owner.Hand;
            var attempt = new ProductionAttempt(blueprint, null, blueprint, owner, root, originalHand);
            if (change == "dispose") attempt.Dispose();
            if (change == "hand") owner.Hand = new PlayerHandData(null, 0);
            if (change == "world") PlayerRootRegistry.Clear();
            int issued = CardData.IdentityHighWaterMark;
            Assert.That(attempt.CompleteToHand(true), Is.Null);
            Assert.That(originalHand.Hand, Is.Empty); Assert.That(owner.Hand.Hand, Is.Empty);
            Assert.That(CardData.IdentityHighWaterMark, Is.EqualTo(issued));
        }
        [Test] public void AttachmentPlayConsumesExactlyItsOwnCardAndRejectsForeignTarget()
        {
            var body = new CardData(AttachmentSlotTests.Host()); owner.Hand.AddCard(body);
            var gear = new CardData(AttachmentSlotTests.Attachment(AttachmentSlot.Mutator)); owner.Hand.AddCard(gear);
            gear.Definition.apCost = 2; gear.Definition.resourceCost = new ResourceCost(tech: 3);
            string before = Snapshot();
            Assert.That(CardHandCommands.TryPlay(owner, owner.Hand, gear, CardPlayTarget.OnCard(new CardData(body.Definition)), map).Ok, Is.False);
            // The separately created foreign card only advances the identity issuer, never hand/world.
            Assert.That(root.ActionPoints, Is.EqualTo(10)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(10));
            Assert.That(CardHandCommands.TryPlay(owner, owner.Hand, gear, CardPlayTarget.OnCard(body), map).Ok, Is.True);
            Assert.That(owner.Hand.Hand, Is.EqualTo(new[] { body })); Assert.That(body.Mutator, Is.SameAs(gear.Definition));
            Assert.That(root.ActionPoints, Is.EqualTo(8)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(7));
            before = Snapshot(); Assert.That(CardHandCommands.TryPlay(owner, owner.Hand, gear, CardPlayTarget.OnCard(body), map).Ok, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before));
        }

        [TestCase(false)] [TestCase(true)] public void AircraftHexAndModalUseSameAirfieldAndCost(bool modal)
        {
            var b = Base(); b.AirfieldCapacity = 2;
            var field = Game.Aviation.AviationActions.EnsureAirfield(null, owner, hex);
            var card = Card(CardType.Unit); card.Definition.isAviation = true;
            var result = CardHandCommands.TryPlay(owner, owner.Hand, card,
                modal ? CardPlayTarget.InArmy(field) : CardPlayTarget.AtHex(hex), map);
            Assert.That(result.Ok, Is.True, result.FailReason); Assert.That(field.Members.Count, Is.EqualTo(1));
            Assert.That(field.Members[0].IsAviation, Is.True); Assert.That(root.ActionPoints, Is.EqualTo(7));
            Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(8)); Assert.That(owner.Hand.Hand, Is.Empty);
        }
        [Test] public void ProducedRapidReactionDeploymentRemainsFreeAndRetainsAttachments()
        {
            var a = Garrison(); var card = Card(CardType.Unit); card.ResearchProductionCreated = true;
            card.Equipment = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment);
            card.Equipment.equipment.addAbilities.Add(UnitAbilities.RapidReaction);
            var result = CardHandCommands.TryPlay(owner, owner.Hand, card, CardPlayTarget.AtHex(hex), map);
            Assert.That(result.Ok, Is.True, result.FailReason); Assert.That(root.ActionPoints, Is.EqualTo(10));
            Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(10)); Assert.That(a.Members.Single().Equipment, Is.SameAs(card.Equipment));
            Assert.That(a.Members.Single().ActivationApCost, Is.Zero);
        }
        [Test] public void InvalidDrawDoesNotChangeDeckHandAccountOrIdentityIssuer()
        {
            owner.Hand = new PlayerHandData(new[] { new CardDefinition { authoredKey = "draw" } }, 0, 1);
            root.ActionPoints = 1; string before = Snapshot();
            Assert.That(CardHandCommands.TryDraw(owner, owner.Hand, 2).Ok, Is.False); Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(CardHandCommands.TryDraw(owner, new PlayerHandData(null, 0), 0).Ok, Is.False); Assert.That(Snapshot(), Is.EqualTo(before));
        }
        [Test] public void LoadRetainsExactCardIdentityAndProducedCostsThenOldHandIsRejected()
        {
            var a = Garrison(); var card = Card(CardType.Unit); card.ResearchProductionCreated = true;
            var oldHand = owner.Hand;
            owner.Hand = PlayerHandData.FromState(oldHand.CaptureState(), _ => card.Definition);
            string before = Snapshot();
            Assert.That(CardHandCommands.TryPlay(owner, oldHand, card, CardPlayTarget.AtHex(hex), map).Ok, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before));
            var restored = owner.Hand.Hand.Single(); Assert.That(restored.RuntimeId, Is.EqualTo(card.RuntimeId));
            Assert.That(CardHandCommands.TryPlay(owner, owner.Hand, restored, CardPlayTarget.AtHex(hex), map).Ok, Is.True);
            Assert.That(root.ActionPoints, Is.EqualTo(9)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(10));
            Assert.That(a.Members.Count, Is.EqualTo(1));
        }
        [Test] public void ExtractionQuoteAndApplyUseSharedMarginalIncomeAndSpendBeforePublishing()
        {
            var a = Garrison(); a.Members.Add(new UnitData { Owner = owner, IsHero = true, MoveMax = 4, MoveCurrent = 4 });
            using (MatchRestoreScope.Begin()) BuildingRegistry.Unregister(hex);
            var config = ScriptableObject.CreateInstance<GameConfig>();
            try
            {
                var def = new CardDefinition { cardType = CardType.Facility, apCost = 2, resourceCost = new ResourceCost(tech: 3),
                    grantedAbilities = new List<string> { UnitAbilities.CollectTech } };
                map.SetTerrainAt(hex, new TerrainTypeEntry { resourceYields = new Game.Economy.ResourceYields { tech = 1 } });
                Assert.That(InfrastructureActions.CanBuildExtractionSite(map, config, def, hex, owner, out var quote, out var why), Is.True, why);
                Assert.That(quote.Ap, Is.EqualTo(2));
                bool final = false;
                root.ResourcesChanged += () => final = BuildingRegistry.FindAt(hex)?.CollectedAmount(ResourceType.Tech) == 1
                    && a.Members[0].MoveCurrent == 0 && root.ActionPoints == 8 && root.GetResource(ResourceType.Tech) == 7;
                var result = InfrastructureActions.TryBuildExtractionSiteOnMap(map, config, def, hex, owner);
                Assert.That(result.Ok, Is.True, result.FailReason); Assert.That(final, Is.True);
                string before = Snapshot();
                Assert.That(InfrastructureActions.TryBuildExtractionSiteOnMap(map, config, def, hex, owner).Ok, Is.False);
                Assert.That(Snapshot(), Is.EqualTo(before));
            }
            finally { UnityEngine.Object.DestroyImmediate(config); }
        }

        [TestCase(1, false)] [TestCase(5, true)]
        public void DeploymentCapacityUsesAttachmentAdjustedCommander(int command, bool expected)
        {
            var a = Garrison(); a.IsGarrison = false;
            a.Members.Add(new UnitData { Owner = owner }); a.Members.Add(new UnitData { Owner = owner });
            var card = Card(CardType.Hero); card.Definition.commandRating = command == 1 ? 5 : 1;
            card.Equipment = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.CommandRating, command, true);
            string before = Snapshot();
            var result = CardHandCommands.TryPlay(owner, owner.Hand, card, CardPlayTarget.InArmy(a), map);
            Assert.That(result.Ok, Is.EqualTo(expected), result.FailReason);
            if (!expected) Assert.That(Snapshot(), Is.EqualTo(before));
            else { Assert.That(a.Capacity, Is.EqualTo(5)); Assert.That(a.Members.Count, Is.EqualTo(3)); }
        }

        [Test] public void ProductionStartQuotesRevalidatesAndPublishesPaidAttemptBeforeDelivery()
        {
            owner.IsHuman = false;
            var a = Garrison();
            var hero = new UnitData { Owner = owner, IsHero = true, HitPointsCurrent = 1, IsHidden = true };
            hero.Abilities.Add(UnitAbilities.Researcher); a.Members.Add(hero);
            var b = BuildingRegistry.FindAt(hex); var lab = new FacilityData(); lab.Abilities.Add(UnitAbilities.Research);
            b.FacilitySlots[0] = lab;
            var blueprint = Card(CardType.Equipment).Definition;
            var faction = ScriptableObject.CreateInstance<FactionCardCatalog>(); faction.cards.Add(blueprint);
            var catalog = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
            catalog.cardCatalogs.Add(faction); catalog.researchCards.Add(new ResearchProductionEntry { cardKey = blueprint.authoredKey });
            try
            {
                string before = Snapshot();
                Assert.That(ResearchProductionSystem.TryQuoteAttempt(owner, root, hero, hex, ResearchProductionMode.Research,
                    blueprint, catalog, out var quote, out _), Is.True); Assert.That(quote.Ap, Is.EqualTo(3));
                Assert.That(Snapshot(), Is.EqualTo(before));
                ProductionAttempt attempt = null; bool final = false;
                root.ResourcesChanged += () => final = attempt != null && !hero.IsHidden && root.ActionPoints == 7 && root.GetResource(ResourceType.Tech) == 8;
                Assert.That(ResearchProductionSystem.TryStartAttempt(owner, root, hero, hex, ResearchProductionMode.Research,
                    blueprint, catalog, out var reason, out attempt), Is.True, reason);
                Assert.That(final, Is.True); Assert.That(attempt.CompleteToHand(false), Is.Null);
                Assert.That(root.ActionPoints, Is.EqualTo(7)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(8));
            }
            finally { UnityEngine.Object.DestroyImmediate(catalog); UnityEngine.Object.DestroyImmediate(faction); }
        }

        [Test] public void OldProductionCallbackCannotCompleteTheReplacementAttempt()
        {
            var blueprint = new CardDefinition { authoredKey = "callback" };
            var oldAttempt = new ProductionAttempt(blueprint, null, blueprint, owner, root, owner.Hand);
            oldAttempt.Dispose();
            var current = new ProductionAttempt(blueprint, null, blueprint, owner, root, owner.Hand);
            var holder = new GameObject("inactive callback presenter"); holder.SetActive(false);
            var selection = holder.AddComponent<HexSelectionController>();
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var field = typeof(HexSelectionController).GetField("_rpAttempt", flags); field.SetValue(selection, current);
                var callback = typeof(HexSelectionController).GetMethod("OnResearchProductionResolved", flags);
                callback.Invoke(selection, new object[] { oldAttempt, true });
                Assert.That(field.GetValue(selection), Is.SameAs(current)); Assert.That(owner.Hand.Hand, Is.Empty);
                callback.Invoke(selection, new object[] { current, true });
                Assert.That(owner.Hand.Hand.Count, Is.EqualTo(1));
                callback.Invoke(selection, new object[] { current, true });
                Assert.That(owner.Hand.Hand.Count, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(holder); }
        }

        [Test] public void DisablingProductionPresenterCancelsAttemptAndUnlocksPicker()
        {
            var blueprint = new CardDefinition { authoredKey = "disable" };
            var attempt = new ProductionAttempt(blueprint, null, blueprint, owner, root, owner.Hand);
            var holder = new GameObject("inactive callback presenter"); holder.SetActive(false);
            var selection = holder.AddComponent<HexSelectionController>();
            var modal = holder.AddComponent<Game.UI.ResearchProductionModalUI>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            try
            {
                typeof(HexSelectionController).GetField("_rpAttempt", flags).SetValue(selection, attempt);
                typeof(HexSelectionController).GetField("_rpTransactionActive", flags).SetValue(selection, true);
                typeof(HexSelectionController).GetField("researchProductionModal", flags).SetValue(selection, modal);
                var busy = typeof(Game.UI.ResearchProductionModalUI).GetField("_busy", flags); busy.SetValue(modal, true);
                typeof(HexSelectionController).GetMethod("OnDisable", flags).Invoke(selection, null);
                Assert.That(busy.GetValue(modal), Is.False);
                Assert.That(attempt.CompleteToHand(true), Is.Null); Assert.That(owner.Hand.Hand, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(holder); }
        }

        [Test] public void FoundingRollbackRestoresTheRegistryOfFullyLandedCarrierSortie()
        {
            var hero = new UnitData { Owner = owner, IsHero = true, HitPointsCurrent = 3 };
            var carrier = new UnitData { Owner = owner, HitPointsCurrent = 3, AirfieldCapacity = 2 };
            var host = new ArmyData { Owner = owner, Hex = hex }; host.Members.Add(hero); host.Members.Add(carrier);
            ArmyRegistry.Register(host);
            var plane = new UnitData { Owner = owner, IsAviation = true, HitPointsCurrent = 3, SortieLaunchPaid = true,
                ConsecutiveUnlandedEnds = 2, HasEmergencyFlightPenalty = true };
            var wing = new ArmyData { Owner = owner, Hex = hex, IsAirArmy = true, LaunchCarrier = carrier };
            wing.Members.Add(plane); ArmyRegistry.Register(wing);
            wing.PendingAirStrikePolicy = Game.Aviation.AirStrikePolicy.Transit;
            int removed = 0; Action<ArmyData> onRemoved = _ => removed++; ArmyRegistry.ArmyRemoved += onRemoved;
            int landed = 0; bool retiredDuringAttempt = false;
            try
            {
                var result = InfrastructureActions.TryFoundBaseOnMap(map,
                    new CardDefinition { cardType = CardType.Base }, hex, owner, 2, new ResourceCost(tech: 3),
                    completeBeforeCommit: _ =>
                    {
                        landed = Game.Aviation.AviationActions.LandInSlotOrder(wing, null);
                        retiredDuringAttempt = !ArmyRegistry.IsRegistered(wing);
                        return false;
                    });
                Assert.That(landed, Is.EqualTo(1)); Assert.That(retiredDuringAttempt, Is.True);
                Assert.That(result.Ok, Is.False); Assert.That(ArmyRegistry.IsRegistered(wing), Is.True);
                Assert.That(ArmyRegistry.FindArmyContaining(plane), Is.SameAs(wing));
                Assert.That(wing.IsAirArmy, Is.True); Assert.That(wing.AirWingLanded, Is.False);
                Assert.That(wing.PendingAirStrikePolicy.HasValue, Is.True);
                Assert.That(wing.PendingAirStrikePolicy.Value.Kind, Is.EqualTo(Game.Aviation.AirStrikePolicyKind.Transit));
                Assert.That(carrier.CarrierAirfield.Members, Is.Empty); Assert.That(wing.Members, Is.EqualTo(new[] { plane }));
                Assert.That(plane.SortieLaunchPaid, Is.True); Assert.That(plane.ConsecutiveUnlandedEnds, Is.EqualTo(2));
                Assert.That(plane.HasEmergencyFlightPenalty, Is.True); Assert.That(root.ActionPoints, Is.EqualTo(10));
                Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(10)); Assert.That(removed, Is.Zero);
                Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
            }
            finally { ArmyRegistry.ArmyRemoved -= onRemoved; }
        }

        [TestCase(true, 10, 0)] [TestCase(false, 10, 1)] [TestCase(false, 0, 1)]
        public void ExtractionActionRowUsesTheSameMarginalIncomeAndKeepsBudgetFailureHints(bool unitCollects, int ap, int expectedActions)
        {
            var a = Garrison();
            var hero = new UnitData { Owner = owner, IsHero = true, HitPointsCurrent = 3 };
            if (unitCollects) hero.Abilities.Add(UnitAbilities.CollectTech);
            a.Members.Add(hero); root.ActionPoints = ap;
            map.SetTerrainAt(hex, new TerrainTypeEntry { resourceYields = new ResourceYields { tech = 1 } });
            var holder = new GameObject("inactive callback presenter"); holder.SetActive(false);
            var selection = holder.AddComponent<HexSelectionController>();
            var panel = holder.AddComponent<Game.UI.HexInfoPanelUI>();
            var turn = holder.AddComponent<Game.Turns.GameTurnController>();
            var prefab = holder.AddComponent<Game.UI.ResourceActionButtonUI>();
            var config = ScriptableObject.CreateInstance<GameConfig>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            try
            {
                var def = new CardDefinition { cardType = CardType.Facility, apCost = 2, resourceCost = new ResourceCost(tech: 3),
                    grantedAbilities = new List<string> { UnitAbilities.CollectTech } };
                config.extractionFacilityCards = new[] { null, null, null, def }; config.resourceActionButtonPrefab = prefab;
                typeof(Game.Turns.GameTurnController).GetField("<CurrentPlayer>k__BackingField", flags).SetValue(turn, owner);
                typeof(HexSelectionController).GetField("map", flags).SetValue(selection, map);
                typeof(HexSelectionController).GetField("turnController", flags).SetValue(selection, turn);
                typeof(HexSelectionController).GetField("gameConfig", flags).SetValue(selection, config);
                typeof(HexSelectionController).GetField("infoPanel", flags).SetValue(selection, panel);
                typeof(HexSelectionController).GetMethod("RefreshResourceActionRow", flags).Invoke(selection,
                    new object[] { hex });
                var actions = (System.Collections.ICollection)typeof(Game.UI.HexInfoPanelUI).GetField("_wantedActions", flags).GetValue(panel);
                Assert.That(actions.Count, Is.EqualTo(expectedActions));
            }
            finally { UnityEngine.Object.DestroyImmediate(holder); UnityEngine.Object.DestroyImmediate(config); }
        }

        [TestCase("activate")] [TestCase("reorder")] [TestCase("launch")]
        public void OtherWorldCommitsRejectAnUnregisteredArmy(string command)
        {
            var one = new UnitData { Owner = owner, HitPointsCurrent = 3, IsAviation = command == "launch" };
            var two = new UnitData { Owner = owner, HitPointsCurrent = 3 };
            var army = new ArmyData { Owner = owner, Hex = hex, IsAirfield = command == "launch" };
            army.Members.Add(one); if (command == "reorder") army.Members.Add(two);
            ArmyRegistry.Register(army); ArmyRegistry.Unregister(army);
            bool ok;
            if (command == "activate") ok = ArmyActions.TryPayActivation(army, root);
            else if (command == "reorder") ok = ArmyActions.ReorderMembers(army, new[] { two, one }, out _);
            else ok = Game.Aviation.AviationActions.TryLaunch(army, new[] { one }, null, null, out _, out _);
            Assert.That(ok, Is.False); Assert.That(root.ActionPoints, Is.EqualTo(10));
            Assert.That(army.Members[0], Is.SameAs(one)); Assert.That(ArmyRegistry.AllArmies(), Is.Empty);
        }

        [TestCase("ground")] [TestCase("garrison")] [TestCase("airfield")]
        public void AircraftPreparationRejectsEveryStaleContainer(string stale)
        {
            var ground = new ArmyData { Owner = owner, Hex = hex };
            var garrison = new ArmyData { Owner = owner, Hex = hex, IsGarrison = true };
            var airfield = new ArmyData { Owner = owner, Hex = hex, IsAirfield = true };
            var body = new UnitData { Owner = owner, HitPointsCurrent = 3 };
            var plane = new UnitData { Owner = owner, HitPointsCurrent = 3, IsAviation = true };
            ground.Members.Add(body); airfield.Members.Add(plane);
            ArmyRegistry.Register(ground); ArmyRegistry.Register(garrison); ArmyRegistry.Register(airfield);
            ArmyRegistry.Unregister(stale == "ground" ? ground : stale == "garrison" ? garrison : airfield);
            Assert.That(ArmyActions.TryUnloadAndBoardAircraft(ground, garrison, airfield, new[] { plane }, null, out _), Is.False);
            Assert.That(ground.Members, Is.EqualTo(new[] { body })); Assert.That(garrison.Members, Is.Empty);
            Assert.That(airfield.Members, Is.EqualTo(new[] { plane })); Assert.That(root.ActionPoints, Is.EqualTo(10));
        }

        [Test] public void ArmyCreationCannotRecoverMembersFromAStaleSource()
        {
            var unit = new UnitData { Owner = owner, HitPointsCurrent = 3 };
            var source = new ArmyData { Owner = owner, Hex = hex }; source.Members.Add(unit);
            ArmyRegistry.Register(source); ArmyRegistry.Unregister(source);
            var holder = new GameObject("inactive callback presenter"); holder.SetActive(false);
            var selection = holder.AddComponent<HexSelectionController>();
            var catalog = ScriptableObject.CreateInstance<FactionCardCatalog>();
            try
            {
                typeof(HexSelectionController).GetField("map", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(selection, map);
                var created = ArmyActions.CreateArmyWithMember(owner, hex, catalog, source, unit, selection, out _);
                Assert.That(created, Is.Null); Assert.That(ArmyRegistry.AllArmies(), Is.Empty);
                Assert.That(source.Members, Is.EqualTo(new[] { unit })); Assert.That(root.ActionPoints, Is.EqualTo(10));
            }
            finally { UnityEngine.Object.DestroyImmediate(holder); UnityEngine.Object.DestroyImmediate(catalog); }
        }
    }
}
#endif
