using System;
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
            // Every player who has personally DISCOVERED this event — got its own Explore/Skip
            // choice and left it unresolved (human Skip button, AI decline, or abandoning its
            // guard fight via Retreat, see HexSelectionController.HandleRetreatFromHexEvent).
            // MapEventDisplay's on-map marker is gated on the CURRENT VIEWER being in here, on top
            // of the ordinary fog check — a hex-event marker is a per-player fact, NOT something
            // every player who merely has vision of the hex gets to see (the project owner's own
            // report: an event another player scouted/skipped must stay invisible to everyone
            // else). Never populated for an event that's only ever been Explored straight through
            // (it's Consumed before a marker would matter) or never reached at all.
            public HashSet<PlayerSetupData> DiscoveredBy = new HashSet<PlayerSetupData>();
            public List<(RewardEntry reward, CardDefinition card)> ResolvedCardRewards = new List<(RewardEntry, CardDefinition)>();
            // Set once the player (or AI) leaves this event unresolved via Skip (see
            // HexSelectionController.Events.cs's own ShowEventChoice) — drives MapEventDisplay's
            // standalone marker: never shown before this (an undiscovered event has no map
            // presence at all), and once true, remembered forever the same "seen once, never
            // re-hidden" way the resource row already is (see VisionSystem.
            // HasEverSeenByCurrentViewer). Exploring straight through on the very first visit
            // never needs a marker at all — the event is usually already Consumed by the time
            // fog would matter again.
            public bool Skipped;
        }

        private static readonly Dictionary<HexCoord, Entry> ByHex = new Dictionary<HexCoord, Entry>();

        // Fired the moment MarkSkipped/MarkConsumed actually change an entry — MapEventDisplay's
        // own subscription is what actually spawns/despawns the on-map marker; kept here rather
        // than as a bool other code has to poll, same reasoning as VisionSystem.VisibilityChanged.
        public static event Action<HexCoord> EventSkipped;
        public static event Action<HexCoord> EventConsumed;

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

        // True when `army` is the live, spawned guard for the Hex Event on `hex` — the ArmyData
        // SpawnEventGuard (see HexSelectionController.Events.cs) registers once Explore is chosen.
        // It's an ordinary neutral ArmyData with no distinguishing flag of its own, so it's
        // matched by GuardOwner + GuardArmyName. The event's card-stat guard is tracked entirely
        // separately (see AiMapMemory.KnownEventGuards) and is what the ground raid planner reads;
        // callers use THIS to keep the transient spawned guard out of physical-army handling that
        // must not apply to a Hex Event — aviation never targets an event guard (per the project
        // owner's own rule: "авиация вообще не взаимодействует с эвентом"), and the spawned guard
        // must never leak into a player's fog-of-war neutral-army sightings, where it would
        // otherwise surface as an air-strike target.
        public static bool IsEventGuardArmy(HexCoord hex, ArmyData army)
        {
            if (army == null)
                return false;
            Entry entry = FindAt(hex);
            return entry != null && !entry.Consumed
                && !string.IsNullOrEmpty(entry.GuardArmyName)
                && army.Owner == entry.GuardOwner
                && army.Name == entry.GuardArmyName;
        }

        // Has `player` personally discovered the event on `hex` — see Entry.DiscoveredBy. Drives
        // MapEventDisplay's per-viewer marker visibility; false for a null player (e.g. during
        // citadel setup, before there's a viewer) so nothing leaks in that window.
        public static bool IsDiscoveredBy(HexCoord hex, PlayerSetupData player)
        {
            Entry entry = FindAt(hex);
            return entry != null && player != null && entry.DiscoveredBy.Contains(player);
        }

        public static void MarkConsumed(HexCoord hex)
        {
            if (ByHex.TryGetValue(hex, out Entry entry))
            {
                entry.Consumed = true;
                EventConsumed?.Invoke(hex);
            }
        }

        // "Пропустить" — see ShowEventChoice's own comment for why this fires from both the
        // human Skip button and the AI's own decline branch alike. `discoverer` is the player
        // who just left it unresolved; recorded even when the event was ALREADY skipped by
        // someone else (a second player independently declining the same event still has to get
        // its marker — see Entry.DiscoveredBy), so the DiscoveredBy add sits outside the
        // fire-once guard.
        public static void MarkSkipped(HexCoord hex, PlayerSetupData discoverer)
        {
            if (!ByHex.TryGetValue(hex, out Entry entry))
                return;
            if (discoverer != null)
                entry.DiscoveredBy.Add(discoverer);
            if (!entry.Skipped)
            {
                entry.Skipped = true;
                EventSkipped?.Invoke(hex);
            }
        }
    }
}
