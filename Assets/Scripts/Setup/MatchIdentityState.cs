using System;
using Game.Cards;
using Game.Map;
using Game.Units;

namespace Game.Setup
{
    [Serializable]
    public sealed class MatchIdentityState
    {
        public int ArmyHighWaterMark;
        public int UnitHighWaterMark;
        public int CardHighWaterMark;

        // Persist the actual issued range, not merely max(living entities). Destroyed units
        // and armies can still be referenced by activation ledgers and historical knowledge.
        public static MatchIdentityState Capture() => new MatchIdentityState
        {
            ArmyHighWaterMark = ArmyData.IdentityHighWaterMark,
            UnitHighWaterMark = UnitData.IdentityHighWaterMark,
            CardHighWaterMark = CardData.IdentityHighWaterMark,
        };

        public void Validate()
        {
            if (ArmyHighWaterMark < -1 || UnitHighWaterMark < 0 || CardHighWaterMark < 0
                || ArmyHighWaterMark == int.MaxValue || UnitHighWaterMark == int.MaxValue || CardHighWaterMark == int.MaxValue)
                throw new ArgumentException("Invalid saved identity counters.");
        }
        public void Reserve()
        {
            Validate();
            if (ArmyHighWaterMark >= 0) ArmyData.ReserveIdentity(ArmyHighWaterMark);
            if (UnitHighWaterMark > 0) UnitData.ReserveIdentity(UnitHighWaterMark);
            if (CardHighWaterMark > 0) CardData.ReserveIdentity(CardHighWaterMark);
        }
    }
}
