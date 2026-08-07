using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;

namespace Game.Combat
{
    // A battle the attacker chose to delay instead of fighting immediately (see the manual's
    // "Delay Attack") — parked in DelayedBattleRegistry until every player has passed this turn
    // (see GameTurnController.BeginNewTurn), at which point it's shown again, informationally,
    // right before the (placeholder) battle screen opens.
    public class PendingBattle
    {
        public HexCoord Hex;
        public List<ArmyData> Participants;
    }
}
