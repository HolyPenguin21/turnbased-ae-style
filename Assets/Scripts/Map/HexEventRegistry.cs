using System.Collections.Generic;
using Game.Cards;
using Game.HexGrid;
using Game.Players;

namespace Game.Map
{
    // What a hex knows about its own Hex Event, keyed by hex — mirrors HexResourceBonusRegistry/
    // BuildingRegistry's shape. ResolvedCardRewards is resolved once, at placement time (see
    // GenerateRandomEvents) rather than at grant time, since EventDefinition itself has no
    // back-reference to its owning EventCatalog for EventCatalog.ResolveCard to be called
    // against later.
    public static class HexEventRegistry
    {
        public class Entry
        {
            public EventDefinition Definition;
            public bool Consumed;
            // The guard is deliberately NEVER a real ArmyData/ArmyRegistry entry until the
            // player (or AI) actually commits to Explore (see HexSelectionController.Events.cs's
            // own SpawnEventGuard, the only place one of these ever gets spawned) — everything
            // needed to build it later is resolved once, right here, at placement time (see
            // CitadelSetupController.MapContent.PlaceEvent), same reasoning as
            // ResolvedCardRewards below. A pre-spawned guard, even with its own marker hidden,
            // was still a live ArmyRegistry entry BattleInitiator.FindEnemyAt could find: a red
            // move-preview arrow leaked its presence before the player ever got near it, and it
            // made a "collision hex" (an unrelated pre-existing neutral army sharing this event's
            // hex, see GenerateRandomEvents) look permanently un-cleared even after that unrelated
            // army was actually beaten (see the user's own report). Null/empty for an unguarded
            // event.
            public string GuardArmyName;
            public List<(CardDefinition card, int count)> ResolvedGuardMembers = new List<(CardDefinition, int)>();
            // The shared neutral PlayerSetupData every other neutral army on the map is owned by
            // (see CitadelSetupController._neutralPlayer) — captured once here since
            // CitadelSetupController itself is destroyed right after setup finishes (see its own
            // Destroy(gameObject) call) and can't be reached again once the guard actually needs
            // spawning, mid-game.
            public PlayerSetupData GuardOwner;
            // Set once Explore is actually chosen (see HexSelectionController.Events.cs's
            // ResolveEventExplore) — tells ResolveHexAfterVictory's own post-battle hook
            // (BattleScreenUI.Combat.cs) apart from a "collision hex" just now going clear of an
            // UNRELATED army: false there means nothing has asked this event anything yet (offer
            // the same Explore/Skip choice a clean hex shows on entry); true means the fight that
            // just ended WAS this event's own guard, so the reward just pays out — nothing left
            // to ask.
            public bool Triggered;
            public List<(RewardEntry reward, CardDefinition card)> ResolvedCardRewards = new List<(RewardEntry, CardDefinition)>();
        }

        private static readonly Dictionary<HexCoord, Entry> ByHex = new Dictionary<HexCoord, Entry>();

        public static void Clear()
        {
            ByHex.Clear();
        }

        public static void Set(HexCoord hex, EventDefinition definition, string guardArmyName,
            List<(CardDefinition, int)> resolvedGuardMembers, PlayerSetupData guardOwner,
            List<(RewardEntry, CardDefinition)> resolvedCardRewards)
        {
            ByHex[hex] = new Entry
            {
                Definition = definition,
                GuardArmyName = guardArmyName,
                ResolvedGuardMembers = resolvedGuardMembers ?? new List<(CardDefinition, int)>(),
                GuardOwner = guardOwner,
                ResolvedCardRewards = resolvedCardRewards ?? new List<(RewardEntry, CardDefinition)>(),
            };
        }

        public static Entry FindAt(HexCoord hex)
        {
            return ByHex.TryGetValue(hex, out Entry entry) ? entry : null;
        }

        // The one thing every trigger/AI/reward call site actually wants to know before doing
        // anything else — an Entry existing but already Consumed means this hex is done for good
        // (see the user's own "one-time reward" rule; an undefeated/repelled guard does NOT set
        // this, only an actually-claimed reward does).
        public static bool HasActiveEvent(HexCoord hex)
        {
            Entry entry = FindAt(hex);
            return entry != null && !entry.Consumed;
        }

        public static void MarkConsumed(HexCoord hex)
        {
            if (ByHex.TryGetValue(hex, out Entry entry))
                entry.Consumed = true;
        }
    }
}
