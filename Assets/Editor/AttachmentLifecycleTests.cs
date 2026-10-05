#if UNITY_INCLUDE_TESTS
using System.Reflection;
using TMPro;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.EditorTests
{
    // Full transaction/scene-bound witnesses. Run these in Unity alongside the pure slot tests.
    public sealed class AttachmentLifecycleTests
    {
        private PlayerSetupData _owner;
        private PlayerRoot _root;
        private GameObject _scene;
        private HexSelectionController _selector;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear();
            StrategicResourceReservationLedger.ClearAll();
            VisionSystem.Configure(null);
            _owner = new PlayerSetupData();
            _root = PlayerRoot.Create(_owner, "attachment transaction witness");
            _root.ActionPoints = 20;
            _root.AddResource(ResourceType.Tech, 10);
            PlayerRootRegistry.Register(_owner, _root);
            _scene = new GameObject("inactive attachment test scene");
            _scene.SetActive(false);
            _selector = _scene.AddComponent<HexSelectionController>();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear();
            StrategicResourceReservationLedger.ClearAll();
            AiHandRegistry.Clear(); VisionSystem.Configure(null);
            Object.DestroyImmediate(_scene); Object.DestroyImmediate(_root.gameObject);
        }

        [TestCase(AttachmentSlot.Equipment, StrategicReservedResource.Tech, false)]
        [TestCase(AttachmentSlot.Mutator, StrategicReservedResource.Tech, false)]
        [TestCase(AttachmentSlot.Equipment, StrategicReservedResource.ActionPoints, false)]
        [TestCase(AttachmentSlot.Mutator, StrategicReservedResource.ActionPoints, false)]
        [TestCase(AttachmentSlot.Equipment, StrategicReservedResource.Tech, true)]
        [TestCase(AttachmentSlot.Mutator, StrategicReservedResource.Tech, true)]
        public void StandaloneAttachmentRechecksOtherOwnersHoldBeforePayment(AttachmentSlot slot,
            StrategicReservedResource reserved, bool produced)
        {
            var definition = AttachmentSlotTests.Attachment(slot);
            definition.apCost = 3; definition.activationApCost = 1;
            definition.resourceCost = new ResourceCost { tech = 4 };
            var card = new CardData(definition) { ResearchProductionCreated = produced };
            var hand = new AiHandData(null, default, 0); hand.AddCard(card);
            var unit = AttachmentSlotTests.Body(); unit.Owner = _owner;
            var army = new ArmyData { Owner = _owner }; army.Members.Add(unit); ArmyRegistry.Register(army);
            var ctx = new AiTurnContext { TurnNumber = 1 };
            var play = new NonCombatCardPlayer.NonCombatPlay
                { Card = card, Kind = NonCombatCardPlayer.PlayKind.Equipment, EquipHost = unit };
            // The hold arrives after candidate creation, so execution must query the bank anew.
            StrategicResourceReservationLedger.Upsert(_owner, 1, new StrategicResourceReservation
            {
                Owner = "other-build", Reason = StrategicReservationReason.EconomyBuildCompletion,
                Resource = reserved, Amount = reserved == StrategicReservedResource.Tech ? 10 : 20,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            });
            int version = V2StateVersion.Current;
            var result = NonCombatCardPlayer.Execute(play, null, _owner, _root, hand, ctx);
            bool blocked = !produced;
            Assert.That(result.Played, Is.EqualTo(!blocked));
            if (blocked)
            {
                Assert.That(result.StateChanged, Is.False);
                Assert.That(V2StateVersion.Current, Is.EqualTo(version));
                Assert.That(_root.ActionPoints, Is.EqualTo(20));
                Assert.That(_root.GetResource(ResourceType.Tech), Is.EqualTo(10));
                Assert.That(EquipmentSystem.GetAttachment(unit, definition), Is.Null);
                Assert.That(hand.Hand.Contains(card), Is.True);
                StrategicResourceReservationLedger.ReleaseByOwner(_owner, 1, "other-build");
                result = NonCombatCardPlayer.Execute(play, null, _owner, _root, hand, ctx);
                Assert.That(result.Played, Is.True, result.FailReason);
            }
            Assert.That(_root.ActionPoints, Is.EqualTo(produced ? 19 : 17));
            Assert.That(_root.GetResource(ResourceType.Tech), Is.EqualTo(produced ? 10 : 6));
            Assert.That(hand.Hand.Contains(card), Is.False);
            Assert.That(EquipmentSystem.GetAttachment(unit, definition), Is.SameAs(definition));
        }

        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void StandaloneAttachmentDoesNotPayForARecipientRemovedAfterPlanning(AttachmentSlot slot)
        {
            var definition = AttachmentSlotTests.Attachment(slot);
            definition.apCost = 3; definition.resourceCost = new ResourceCost { tech = 4 };
            var card = new CardData(definition);
            var hand = new AiHandData(null, default, 0); hand.AddCard(card);
            var unit = AttachmentSlotTests.Body(); unit.Owner = _owner;
            var army = new ArmyData { Owner = _owner }; army.Members.Add(unit); ArmyRegistry.Register(army);
            var play = new NonCombatCardPlayer.NonCombatPlay
                { Card = card, Kind = NonCombatCardPlayer.PlayKind.Equipment, EquipHost = unit };
            army.Members.Clear();
            var result = NonCombatCardPlayer.Execute(play, null, _owner, _root, hand,
                new AiTurnContext { TurnNumber = 1 });
            Assert.That(result.Played, Is.False);
            Assert.That(result.StateChanged, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(20));
            Assert.That(_root.GetResource(ResourceType.Tech), Is.EqualTo(10));
            Assert.That(EquipmentSystem.GetAttachment(unit, definition), Is.Null);
            Assert.That(hand.Hand.Contains(card), Is.True);
        }

        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void HandCardRebindDuringAttachmentPreviewKeepsNewHostNameAndArt(AttachmentSlot slot)
        {
            var go = new GameObject("hand card rebind", typeof(RectTransform));
            go.transform.SetParent(_scene.transform);
            var ui = go.AddComponent<CardUI>();
            var art = go.AddComponent<Image>();
            var labelGo = new GameObject("name", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform);
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            var button = new GameObject("attachment toggle");
            button.transform.SetParent(go.transform);
            var toggle = button.AddComponent<EquipmentArtToggle>();
            void Field(object target, string name, object value) => target.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
            Field(ui, "rectTransform", go.GetComponent<RectTransform>());
            Field(ui, "artImage", art); Field(ui, "nameText", label);
            Field(ui, slot == AttachmentSlot.Mutator ? "mutatorArtToggle" : "equipmentArtToggle", toggle);
            Field(toggle, "cardArtImage", art); Field(toggle, "nameOverrideText", label);
            var attachment = AttachmentSlotTests.Attachment(slot); attachment.displayName = "Attachment";
            var oldHost = AttachmentSlotTests.Host(); oldHost.displayName = "Old host";
            var newHost = AttachmentSlotTests.Host(); newHost.displayName = "New host";
            var sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), Vector2.zero);
            attachment.art = sprite; newHost.art = sprite;
            try
            {
                var card = new CardData(oldHost);
                if (slot == AttachmentSlot.Mutator) card.Mutator = attachment;
                else card.Equipment = attachment;
                ui.Setup(null, card, 1, 1, 0, 0, 1);
                toggle.OnPointerEnter(null);
                Assert.That(label.text, Is.EqualTo("Attachment"));
                ui.Setup(null, new CardData(newHost), 1, 1, 0, 0, 1);
                Assert.That(label.text, Is.EqualTo("New host"));
                Assert.That(art.sprite, Is.SameAs(sprite));
                Assert.That(button.activeSelf, Is.False);
            }
            finally { Object.DestroyImmediate(sprite); }
        }

        [Test]
        public void RepairAfterMaximumClampClearsOnlyHitPointConsumption()
        {
            var unit = AttachmentSlotTests.Body(); unit.Owner = _owner;
            unit.HitPointsCurrent = 2; unit.MoveCurrent = 1; unit.Fate = 1;
            EquipmentSystem.ApplyAttachments(unit,
                AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.HitPoints, 4, true), null);
            var hex = new HexCoord(88, -31);
            var building = new BuildingData { Owner = _owner, Hex = hex, IsBase = true };
            BuildingRegistry.Register(hex, building);
            Assert.That(UnitRepair.TryRepair(unit, hex, _root, out var reason), Is.True, reason);
            EquipmentSystem.ApplyAttachments(unit, null,
                AttachmentSlotTests.Attachment(AttachmentSlot.Mutator, EquipmentStat.HitPoints, 6));
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(10));
            Assert.That(unit.MoveCurrent, Is.EqualTo(1));
            Assert.That(unit.Fate, Is.EqualTo(1));
        }

        [TestCase(AttachmentSlot.Equipment, false, false)]
        [TestCase(AttachmentSlot.Equipment, true, false)]
        [TestCase(AttachmentSlot.Mutator, false, false)]
        [TestCase(AttachmentSlot.Mutator, true, false)]
        [TestCase(AttachmentSlot.Equipment, false, true)]
        [TestCase(AttachmentSlot.Equipment, true, true)]
        [TestCase(AttachmentSlot.Mutator, false, true)]
        [TestCase(AttachmentSlot.Mutator, true, true)]
        public void SameTransactionChargesInstanceCostsOnce(AttachmentSlot slot, bool produced, bool live)
        {
            var definition = AttachmentSlotTests.Attachment(slot);
            definition.apCost = 3; definition.activationApCost = 1;
            definition.resourceCost = new ResourceCost { tech = 4 };
            var attachment = new CardData(definition) { ResearchProductionCreated = produced };
            var card = new CardData(AttachmentSlotTests.Host());
            var unit = AttachmentSlotTests.Body(); unit.Owner = _owner;
            bool attached = live ? EquipmentSystem.TryAttach(attachment, unit, _root, out _)
                : EquipmentSystem.TryAttach(attachment, card, _root, out _);
            Assert.That(attached, Is.True);
            Assert.That(_root.ActionPoints, Is.EqualTo(20 - (produced ? 1 : 3)));
            Assert.That(_root.GetResource(ResourceType.Tech), Is.EqualTo(produced ? 10 : 6));
            attached = live ? EquipmentSystem.TryAttach(attachment, unit, _root, out _)
                : EquipmentSystem.TryAttach(attachment, card, _root, out _);
            Assert.That(attached, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(20 - (produced ? 1 : 3)));
            Assert.That(_root.GetResource(ResourceType.Tech), Is.EqualTo(produced ? 10 : 6));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void DeployCarriesBothSlotsIntoArmyOrGarrison(bool hero, bool garrison)
        {
            var hex = new HexCoord(88, -31);
            var army = new ArmyData { Owner = _owner, Hex = hex, IsGarrison = garrison };
            var building = new BuildingData { Owner = _owner, Hex = hex };
            building.Abilities.Add(UnitAbilities.Barracks);
            BuildingRegistry.Register(hex, building);
            var definition = AttachmentSlotTests.Host(hero);
            definition.requiredBuildingAbility = UnitAbilities.Barracks;
            var equipment = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, amount: 8, replace: true);
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator);
            var card = new CardData(definition) { Equipment = equipment, Mutator = mutator };
            Assert.That(ArmyActions.DeployUnitFromCard(definition, _owner, army, _root, _selector,
                out string reason, sourceCard: card), Is.True, reason);
            var unit = army.Members[0];
            Assert.That(unit.Equipment, Is.SameAs(equipment));
            Assert.That(unit.Mutator, Is.SameAs(mutator));
            Assert.That(unit.Attack, Is.EqualTo(10));
            Assert.That(unit.IsHero, Is.EqualTo(hero));
        }

        [Test]
        public void HandAndLiveTransactionsAllowBothSlotsAndRejectOnlyTheOccupiedOne()
        {
            var card = new CardData(AttachmentSlotTests.Host());
            var unit = AttachmentSlotTests.Body(); unit.Owner = _owner;
            var equipment = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment);
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator);
            Assert.That(EquipmentSystem.TryAttach(equipment, card, _root, out _), Is.True);
            Assert.That(EquipmentSystem.TryAttach(mutator, card, _root, out _), Is.True);
            Assert.That(card.Equipment, Is.SameAs(equipment));
            Assert.That(card.Mutator, Is.SameAs(mutator));
            Assert.That(EquipmentSystem.TryAttach(mutator, unit, _root, out _), Is.True);
            Assert.That(EquipmentSystem.TryAttach(equipment, unit, _root, out _), Is.True);
            Assert.That(unit.Equipment, Is.SameAs(equipment));
            Assert.That(unit.Mutator, Is.SameAs(mutator));
            Assert.That(EquipmentSystem.TryAttach(equipment, unit, _root, out _), Is.False);
            Assert.That(EquipmentSystem.TryAttach(mutator, unit, _root, out _), Is.False);
        }

        [Test]
        public void AircraftReturnPreservesBothSlotsWithoutReattachingOrPayment()
        {
            var handUI = _scene.AddComponent<CardHandUI>();
            typeof(HexSelectionController).GetField("cardHandUI", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_selector, handUI);
            var host = AttachmentSlotTests.Host(); host.isAviation = true;
            var unit = AttachmentSlotTests.Body(host); unit.IsAviation = true; unit.Owner = _owner;
            var equipment = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment);
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator);
            EquipmentSystem.ApplyAttachments(unit, equipment, mutator);
            var army = new ArmyData { Owner = _owner, IsAirfield = true, Hex = new HexCoord(88, -31) };
            army.Members.Add(unit);
            AviationActions.ReturnAircraftToDeck(army, _selector);
            var hand = AiHandRegistry.GetOrCreate(_owner, null, 0);
            Assert.That(hand.Hand.Count, Is.EqualTo(1));
            Assert.That(hand.Hand[0].Equipment, Is.SameAs(equipment));
            Assert.That(hand.Hand[0].Mutator, Is.SameAs(mutator));
            Assert.That(hand.Hand[0].ResearchProductionCreated, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(20));
            Assert.That(_root.GetResource(ResourceType.Tech), Is.EqualTo(10));
        }

        [Test]
        public void LiveMutatorPublishesVisibilityAndContentThroughExistingRefreshPath()
        {
            var unit = AttachmentSlotTests.Body(); unit.Owner = _owner;
            var army = new ArmyData { Owner = _owner, Hex = new HexCoord(88, -31) };
            army.Members.Add(unit); ArmyRegistry.Register(army);
            int visibility = 0, content = 0;
            System.Action<PlayerSetupData> onVisibility = player => { if (player == _owner) visibility++; };
            System.Action<PlayerSetupData, HexCoord> onContent = (player, hex) =>
            { if (player == _owner && hex.Equals(army.Hex)) content++; };
            VisionSystem.VisibilityChanged += onVisibility;
            VisionSystem.VisibleContentChanged += onContent;
            try
            {
                var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator);
                mutator.equipment.clearAbilityFamilies.Add(AbilityFamily.Recce);
                mutator.equipment.addAbilities.Add("r3s8");
                Assert.That(EquipmentSystem.TryAttach(mutator, unit, _root, out _), Is.True);
                Assert.That(visibility, Is.EqualTo(1));
                Assert.That(content, Is.GreaterThanOrEqualTo(1));
                Assert.That(unit.Abilities, Does.Contain("r3s8").And.Not.Contain("r1s4"));
            }
            finally
            {
                VisionSystem.VisibilityChanged -= onVisibility;
                VisionSystem.VisibleContentChanged -= onContent;
            }
        }
    }
}
#endif
