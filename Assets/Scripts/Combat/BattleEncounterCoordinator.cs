using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Combat
{
    // STEALTH-COMBAT-01: the one authoritative lifecycle boundary between "contact just
    // happened" and "any battle/pre-battle UI is allowed to show". Every entry point that's
    // about to display a Fight/Delay popup, a delayed-battle "Continue" popup, a Tactical
    // Battle Module screen, or a Capture Kill Challenge — TryBeginBattleAt,
    // ResolveHexAfterVictory's chained-battle branch, TryChainPendingRetreatContact,
    // GameTurnController.ResolveDelayedBattlesThen — must call PrepareCommittedEncounter
    // FIRST, right before it shows anything, and use ITS OWN TargetHeroOnly rather than
    // re-deriving one before the reveal has run.
    //
    // Deliberately NOT a speculative query: this is only ever called once contact has actually
    // committed to becoming a real encounter (BattleInitiator already found a real opponent, or
    // a queued/delayed contact is finally being drained) — never from a hover preview, a
    // FindEnemyAt probe, or anything that might not actually turn into a fight.
    public static class BattleEncounterCoordinator
    {
        // Reveals every hidden member of the REAL participants of this encounter (see
        // StealthSystem.RevealForBattle — one consolidated notification, not one ExitStealth
        // per unit), then builds the context. Never reveals anything else on the hex: other
        // armies of the same owner, neighboring hidden armies, anything FindEnemyAt merely
        // considered, or a future chained target — those are separate encounters and get their
        // own PrepareCommittedEncounter call when (if) they actually happen.
        public static BattleEncounterContext PrepareCommittedEncounter(HexCoord hex, List<ArmyData> participants,
            PlayerSetupData presentationObserver = null)
        {
            ArmyData initiator = participants != null && participants.Count > 0 ? participants[0] : null;
            ArmyData target = participants != null && participants.Count > 1 ? participants[1] : null;
            bool targetHeroOnly = target != null && !BattleInitiator.IsCombatCapable(target);

            StealthSystem.RevealForBattle(participants, hex);

            return new BattleEncounterContext(hex, participants, initiator, target, targetHeroOnly, presentationObserver);
        }
    }
}
