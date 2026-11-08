using System;
using Game.Economy;

namespace Game.Map
{
    public partial class PlayerRoot
    {
        public int TradeRate { get; private set; } = TradeActions.MinimumRate;
        public bool TradedThisTurn { get; private set; }
        public bool TradeTurnOpen { get; private set; }
        public event Action TradeChanged;
        internal void BeginTradeTurn() => TradeTurnOpen = true;
        internal void CommitTrade(int nextRate)
        {
            TradeRate = nextRate; TradedThisTurn = true;
            VisionSystem.PublishAfterMutation(TradeChanged);
        }
        internal void CloseTradeTurn()
        {
            TradeTurnOpen = false; TradedThisTurn = false;
            VisionSystem.PublishAfterMutation(TradeChanged);
        }
        internal void EndTradeTurn()
        {
            if (!TradeTurnOpen) return;
            TradeTurnOpen = false;
            if (!TradedThisTurn) TradeRate = Math.Max(TradeActions.MinimumRate, TradeRate - 1);
            TradedThisTurn = false;
            VisionSystem.PublishAfterMutation(TradeChanged);
        }
    }
}
