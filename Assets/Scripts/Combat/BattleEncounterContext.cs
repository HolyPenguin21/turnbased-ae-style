using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Combat
{
    // Immutable snapshot handed back by BattleEncounterCoordinator.PrepareCommittedEncounter —
    // "this specific encounter, on this hex, between exactly these armies, already stealth-
    // resolved" (see that method's own comment for the reveal guarantee). Every field is a
    // plain read of what was passed in / already computed at prep time; nothing here re-derives
    // anything later, so a caller holding a context never sees it drift out of sync with what
    // was actually shown.
    public sealed class BattleEncounterContext
    {
        public readonly HexCoord Hex;
        public readonly List<ArmyData> Participants;
        // participants[0]/[1] by this project's own long-standing convention (see
        // BattleScreenUI.Show's own comment) — named here too so callers stop re-deriving them.
        public readonly ArmyData Initiator;
        public readonly ArmyData Target;
        // Whether Target has no combat-capable (non-hero) unit left — the same split every
        // contact site already branches on to route into BeginCaptureKillEncounter instead of a
        // normal Ground Combat round (see BattleInitiator.IsCombatCapable).
        public readonly bool TargetHeroOnly;
        // The human viewer this encounter is being presented to, if any — carried purely so a
        // future observer-aware UI tweak has it on hand; not used to gate reveal itself (reveal
        // is unconditional for the real participants, see PrepareCommittedEncounter).
        public readonly PlayerSetupData PresentationObserver;

        public BattleEncounterContext(HexCoord hex, List<ArmyData> participants, ArmyData initiator,
            ArmyData target, bool targetHeroOnly, PlayerSetupData presentationObserver)
        {
            Hex = hex;
            Participants = participants;
            Initiator = initiator;
            Target = target;
            TargetHeroOnly = targetHeroOnly;
            PresentationObserver = presentationObserver;
        }
    }
}
