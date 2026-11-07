#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class AiTradeFundingTests
    {
        private PlayerSetupData player;
        private PlayerRoot root;
        private AiTurnContext context;
        [SetUp] public void SetUp()
        {
            GameSession.ClearMatch();
            PlayerRootRegistry.Clear(); BuildingRegistry.Clear(); ArmyRegistry.Clear();
            AiHandRegistry.Clear(); ResourceStarvationRegistry.Clear(); StrategicResourceReservationLedger.ClearAll();
            player = new PlayerSetupData(); root = PlayerRoot.Create(player, "Trade test");
            PlayerRootRegistry.Register(player, root); root.BeginTradeTurn();
            context = new AiTurnContext { TurnNumber = 1 };
            var building = new BuildingData { Owner = player, Hex = new HexCoord(0, 0),
                StructurePointsCurrent = 6, StructurePointsMax = 6, IsBase = true };
            building.Abilities.Add(UnitAbilities.Trade); BuildingRegistry.Register(building.Hex, building);
        }
        [TearDown] public void TearDown()
        {
            PlayerRootRegistry.Clear(); BuildingRegistry.Clear(); ArmyRegistry.Clear(); AiHandRegistry.Clear();
            ResourceStarvationRegistry.Clear(); StrategicResourceReservationLedger.ClearAll();
            if (root != null) Object.DestroyImmediate(root.gameObject);
        }
        [Test] public void TradeAbilityOnABaseGrantsAccessWithoutSpecificCardName()
        { Assert.That(TradeActions.HasAccess(player), Is.True); }
        [Test] public void PureFundingPreviewUsesFullProgressiveCost()
        {
            root.AddResource(ResourceType.Materials, 9);
            Assert.That(TradeFunding.TryPlan(player, root, context, new ResourceCost(tech: 3), out var plan), Is.True);
            Assert.That(plan.ExternalCost.materials, Is.EqualTo(9)); Assert.That(plan.ExternalCost.tech, Is.Zero);
            Assert.That(plan.Steps.Count, Is.EqualTo(3)); Assert.That(root.TradeRate, Is.EqualTo(2));
            Assert.That(root.GetResource(ResourceType.Materials), Is.EqualTo(9));
        }
        [Test] public void CannotPartiallyFundThreeUnitsWithEightMaterials()
        {
            root.AddResource(ResourceType.Materials, 8);
            Assert.That(TradeFunding.TryPlan(player, root, context, new ResourceCost(tech: 3), out _), Is.False);
            Assert.That(root.TradeRate, Is.EqualTo(2)); Assert.That(root.GetResource(ResourceType.Materials), Is.EqualTo(8));
        }
        [Test] public void TradeThenOriginalPaymentWorksWithZeroAp()
        {
            root.AddResource(ResourceType.Materials, 9); int ap = root.ActionPoints;
            Assert.That(TradeFunding.TryPlan(player, root, context, new ResourceCost(tech: 3), out var plan), Is.True);
            Assert.That(TradeActions.TryExchange(player, plan.Rate, plan.Steps, GameSession.Generation, out _), Is.True);
            Assert.That(new ResourceCost(tech: 3).TryPayFrom(root, out _), Is.True);
            Assert.That(root.GetResource(ResourceType.Tech), Is.Zero); Assert.That(root.GetResource(ResourceType.Materials), Is.Zero);
            Assert.That(root.ActionPoints, Is.EqualTo(ap)); Assert.That(root.TradeRate, Is.EqualTo(5));
        }
        [Test] public void ResourcesNeededByHeldCardsAreNotSold()
        {
            var hand = new AiHandData(null, Faction.None, 0);
            hand.AddCard(new CardData(new CardDefinition { cardType = CardType.Unit,
                resourceCost = new ResourceCost(materials: 9) }));
            AiHandRegistry.RegisterRestored(player, hand);
            root.AddResource(ResourceType.Materials, 9);
            Assert.That(TradeFunding.TryPlan(player, root, context, new ResourceCost(tech: 3), out _), Is.False);
            root.AddResource(ResourceType.Materials, 9);
            Assert.That(TradeFunding.TryPlan(player, root, context, new ResourceCost(tech: 3), out _), Is.True);
        }
        [Test] public void ForeignReservationCannotBecomeTradeCurrency()
        {
            root.AddResource(ResourceType.Materials, 9);
            MissionLeaseBook.Upsert(player, context.TurnNumber, new StrategicResourceReservation
            {
                Identity = ReservationOwner.ForPass("other-action"),
                Resource = StrategicReservedResource.Materials, Amount = 9,
                Reason = StrategicReservationReason.StrategicReactionPass,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            });
            Assert.That(TradeFunding.TryPlan(player, root, context, new ResourceCost(tech: 1), out _), Is.False);
        }
        [Test] public void RateSettlesOnlyOnceAtOwnTurnEndAndRestoresExactly()
        {
            root.AddResource(ResourceType.Materials, 5);
            var step = new TradeStep(ResourceType.Materials, ResourceType.Tech);
            Assert.That(TradeActions.TryExchange(player, 2, new[] {step,step}, GameSession.Generation, out _), Is.True);
            root.EndTradeTurn(); Assert.That(root.TradeRate, Is.EqualTo(4));
            root.BeginTradeTurn(); root.EndTradeTurn(); Assert.That(root.TradeRate, Is.EqualTo(3));
            root.EndTradeTurn(); Assert.That(root.TradeRate, Is.EqualTo(3));
            var state = root.CaptureEconomyState(); root.BeginTradeTurn(); root.EndTradeTurn();
            root.RestoreEconomyState(state); Assert.That(root.TradeRate, Is.EqualTo(3));
            Assert.That(root.TradeTurnOpen, Is.False);
        }
        [Test] public void LostAccessAndClosedTurnDoNotSpend()
        {
            root.AddResource(ResourceType.Materials, 2); root.EndTradeTurn();
            Assert.That(TradeActions.TryExchange(player, 2,
                new[] {new TradeStep(ResourceType.Materials,ResourceType.Tech)}, GameSession.Generation, out _), Is.False);
            root.BeginTradeTurn(); BuildingRegistry.Clear();
            Assert.That(TradeFunding.TryPlan(player, root, context, new ResourceCost(tech: 1), out _), Is.False);
            Assert.That(root.GetResource(ResourceType.Materials), Is.EqualTo(2));
        }
    }
}
#endif
