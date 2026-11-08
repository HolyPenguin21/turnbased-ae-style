using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Core;
using Game.Map;
using Game.Players;

namespace Game.Economy
{
    public readonly struct TradeStep
    {
        public readonly ResourceType Give, Receive;
        public TradeStep(ResourceType give, ResourceType receive) { Give = give; Receive = receive; }
    }

    public static class TradeActions
    {
        public const int MinimumRate = 2;
        public static bool HasAccess(PlayerSetupData player)
        {
            if (player == null || player.IsNeutral || player.IsEliminated) return false;
            foreach (var army in ArmyRegistry.AllForOwner(player))
                if (!army.IsPrison)
                    foreach (var unit in army.Members)
                        if (unit != null && unit.HitPointsCurrent > 0 && !unit.IsPrisoner
                            && unit.HasAbility(UnitAbilities.Trade)) return true;
            foreach (var building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != player || building.StructurePointsCurrent <= 0) continue;
                if (building.HasAbility(UnitAbilities.Trade)) return true;
                foreach (var facility in building.FacilitySlots)
                    if (facility != null && facility.StructurePointsCurrent > 0
                        && facility.HasAbility(UnitAbilities.Trade)) return true;
            }
            return false;
        }

        public static long Price(int rate, int units) => rate < MinimumRate || units < 1
            ? -1 : (long)rate * units + (long)units * (units - 1) / 2;

        // Pure simulation. No partial payment, including when the last step cannot be paid.
        public static bool Simulate(int[] stock, int rate, IReadOnlyList<TradeStep> steps,
            out int[] after, out int nextRate)
        {
            after = null; nextRate = rate;
            if (stock == null || stock.Length != 4 || rate < MinimumRate || steps == null || steps.Count == 0
                || (long)rate + steps.Count > int.MaxValue) return false;
            var values = (int[])stock.Clone();
            foreach (int value in values) if (value < 0) return false;
            int workingRate = rate;
            foreach (var step in steps)
            {
                int give = (int)step.Give, receive = (int)step.Receive;
                if (give < 0 || give > 3 || receive < 0 || receive > 3 || give == receive
                    || values[give] < workingRate || values[receive] == int.MaxValue) return false;
                values[give] -= workingRate++; values[receive]++;
            }
            after = values; nextRate = workingRate; return true;
        }

        public static bool TryExchange(PlayerSetupData player, int expectedRate, IReadOnlyList<TradeStep> steps,
            long generation, out string reason)
        {
            reason = null;
            if (!GameSession.IsCurrent(generation)) { reason = "This match has ended."; return false; }
            var root = PlayerRootRegistry.FindFor(player);
            if (root == null || !HasAccess(player)) { reason = "Trade is unavailable."; return false; }
            if (!root.TradeTurnOpen) { reason = "Resource exchange is only available during your turn."; return false; }
            if (expectedRate != root.TradeRate) { reason = "The exchange rate changed. Please try again."; return false; }
            var stock = new int[4];
            for (int i = 0; i < 4; i++) stock[i] = root.GetResource((ResourceType)i);
            if (!Simulate(stock, expectedRate, steps, out var after, out int nextRate))
            { reason = "Not enough resources, or an invalid exchange."; return false; }
            using var mutation = VisionSystem.BeginMutation();
            root.CommitTrade(nextRate);
            for (int i = 0; i < 4; i++) root.AddResource((ResourceType)i, after[i] - stock[i]);
            return true;
        }
    }
}
