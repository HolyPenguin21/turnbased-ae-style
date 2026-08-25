using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using Game.Styles;
using UnityEngine;

namespace Game.Map
{
    // Every building currently on the map, keyed by hex — mirrors ArmyRegistry's role for
    // armies. Buildings never change which hex they're on once placed, but their on-hex OFFSET
    // still can (e.g. re-centring when the last army sharing their hex leaves), so
    // HexSelectionController needs a way to find a hex's building (BuildingData.Visual) to
    // reposition it — and card-play spawning needs a way to check what a hex's building can do
    // (BuildingData.Abilities) before deploying a unit there.
    public static class BuildingRegistry
    {
        private static readonly Dictionary<HexCoord, BuildingData> ByHex = new Dictionary<HexCoord, BuildingData>();

        // Fired by Unregister — GameTurnController listens for this to check
        // BuildingData.IsStartingCitadel (the win condition). No combat system exists yet to
        // ever actually call Unregister, same "wired for correctness, currently unreachable"
        // status as BaseViewerModalUI's own Repair button.
        public static event Action<BuildingData> BuildingDestroyed;
        // Fired after the building's visible state changes but before that change can remove its
        // former owner's own vision source. HumanVisualMemory uses this narrow pre-recompute
        // window to record a capture/destruction the player was genuinely watching, without
        // changing AiMapMemory's existing VisibilityChanged-driven decision data.
        public static event Action<HexCoord, BuildingData> VisualStateChanged;

        public static void Clear()
        {
            ByHex.Clear();
        }

        public static void Register(HexCoord hex, BuildingData building)
        {
            ByHex[hex] = building;
            VisionSystem.RecomputeFor(building?.Owner);
            VisionSystem.NotifyContentChanged(hex);
        }

        public static BuildingData FindAt(HexCoord hex)
        {
            return ByHex.TryGetValue(hex, out BuildingData building) ? building : null;
        }

        // Every building on the map regardless of hex or owner — used by GameTurnController's
        // per-turn resource collection, which needs to visit every player's buildings, not just
        // one hex at a time.
        public static IEnumerable<BuildingData> AllBuildings() => ByHex.Values;

        public static void Unregister(HexCoord hex)
        {
            if (!ByHex.TryGetValue(hex, out BuildingData building))
                return;
            ByHex.Remove(hex);
            VisualStateChanged?.Invoke(hex, null);
            BuildingDestroyed?.Invoke(building);
            VisionSystem.RecomputeFor(building.Owner);
            VisionSystem.NotifyContentChanged(hex);
        }

        // Shared by every place a building changes hands through combat, per the user's own
        // Siege spec — a defending army wiped out completely (see BattleScreenUI.Combat.cs's
        // HandleBuildingOnArmyDefeat) or an enemy simply walking onto a hex nobody defended at
        // all (see HexSelectionController.Movement.cs's own undefended-building check). A
        // Base building (a citadel or player-built Base) is CAPTURED intact — ownership
        // only, recoloured to match. Anything else — a bare hero-built extraction facility,
        // which never had a garrison of its own to begin with — has no structure worth
        // capturing, so it's destroyed outright instead, icon and all.
        //
        // 2026-08-24 diagnostics (project owner's own report): a real capture used to be
        // invisible in the AI debug log entirely — only reconstructible after the fact from the
        // AP Bonus delta between two players' turns. Logged HERE, in this one authoritative
        // method every capture/destroy path funnels through (never in AiAggressionPlanner —
        // that class only ever CHOOSES a move, this method is what actually changes the
        // building), so it covers AI, human, and any future capture path alike with exactly one
        // line per event.
        public static void CaptureOrDestroy(BuildingData building, PlayerSetupData newOwner, HexSelectionController hexSelection)
        {
            if (building == null)
                return;
            if (building.IsBase)
            {
                PlayerSetupData previousOwner = building.Owner;
                building.Owner = newOwner;
                if (building.Visual != null)
                    building.Visual.SetColor(newOwner != null ? PlayerColorPalette.Colors[newOwner.ColorIndex] : Color.white);
                // 2026-08-24 fix (project owner's own report — see EnsureGarrisonForBuilding's own
                // comment): a capture used to leave the previous owner's own empty garrison shell
                // behind, unclaimed by the new owner, so every multi-base AI/UI lookup keyed off
                // ArmyData.Owner (AiTurnController.OwnGarrisonArmies and everything built on it)
                // never saw this base as garrisoned at all for its NEW owner. Runs before the
                // vision recomputes below so both sides' vision already reflects the corrected
                // garrison ownership.
                if (newOwner != null)
                    EnsureGarrisonForBuilding(building, hexSelection);
                VisualStateChanged?.Invoke(building.Hex, building);
                // Both sides of the handover lose/gain vision from this specific building —
                // Unregister/Register (the Destroy branch below) already cover that on their
                // own, but a capture never calls either, so both recomputes are done explicitly
                // here instead.
                VisionSystem.RecomputeFor(previousOwner);
                VisionSystem.RecomputeFor(newOwner);
                VisionSystem.NotifyContentChanged(building.Hex);
                AiDebugLog.Write($"[BUILDING] Base \"{building.Name}\" at ({building.Hex.Q},{building.Hex.R}) captured: "
                    + $"{(previousOwner != null ? previousOwner.Nickname : "nobody")} → {(newOwner != null ? newOwner.Nickname : "nobody")}.");
            }
            else
            {
                PlayerSetupData previousOwner = building.Owner;
                Unregister(building.Hex);
                if (building.Visual != null)
                    UnityEngine.Object.Destroy(building.Visual.gameObject);
                AiDebugLog.Write($"[BUILDING] Facility \"{building.Name}\" at ({building.Hex.Q},{building.Hex.R}) owned by "
                    + $"{(previousOwner != null ? previousOwner.Nickname : "nobody")} destroyed by {(newOwner != null ? newOwner.Nickname : "nobody")}.");
            }
        }

        // Shared by every place an army finishes ARRIVING on a hex without a fight of its own —
        // an ordinary strategic move (HexSelectionController.Movement.cs) and, per the user's own
        // spec, a retreat landing there too (BattleScreenUI.Retreat.cs's PerformRetreat). An
        // enemy-owned building with nobody of its own owner left on `hex` to defend it (garrison
        // included — same IsEngageable check Contact uses) changes hands/gets destroyed the
        // moment `mover` arrives, no fight to trigger since there was never anyone there to put
        // one up. No-op for a building `mover` already owns, or a hex with no building at all.
        public static void CaptureOrDestroyIfUndefended(HexCoord hex, PlayerSetupData mover, HexSelectionController hexSelection)
        {
            BuildingData building = FindAt(hex);
            if (building == null || building.Owner == null || building.Owner == mover)
                return;
            foreach (ArmyData resident in ArmyRegistry.AllAt(hex))
                if (resident.Owner == building.Owner && BattleInitiator.IsEngageable(resident))
                    return;
            CaptureOrDestroy(building, mover, hexSelection);
        }

        // Shared by CaptureOrDestroy (a Base changing hands) and HexSelectionController.Factory's
        // own SpawnBuilding (a fresh Base built with Barracks) — the invariant every multi-base
        // AI/UI lookup already assumes (AiTurnController.OwnGarrisonArmies/OwnGarrisonHexes and
        // everything built on them, ArmyViewerModalUI, ...): a Barracks-tagged Base has EXACTLY one
        // IsGarrison army, owned by whoever currently owns the building. No-op for a building with
        // no Barracks ability at all (a bare resource site never gets a garrison of its own).
        //
        // Repurposes whatever IsGarrison army is already sitting on the hex (2026-08-24, project
        // owner's own spec — "передать его новому владельцу", not destroy+recreate) rather than
        // tearing it down and building a fresh one — its ArmyController/marker/Id all survive
        // untouched, and no HexSelectionController reference is even needed for that path. Only
        // registers a brand-new ArmyData (which DOES need one, to create its map marker) when none
        // exists on the hex at all yet — the first-founding case SpawnBuilding itself covers.
        internal static void EnsureGarrisonForBuilding(BuildingData building, HexSelectionController hexSelection)
        {
            if (building == null || building.Owner == null || !building.HasAbility(UnitAbilities.Barracks))
                return;

            ArmyData garrison = ArmyRegistry.AllAt(building.Hex).FirstOrDefault(a => a.IsGarrison);
            if (garrison == null)
            {
                garrison = new ArmyData { Name = "Garrison", Hex = building.Hex, Owner = building.Owner, IsGarrison = true };
                ArmyRegistry.Register(garrison);
                hexSelection?.CreateArmyMarker(garrison);
                return;
            }

            if (garrison.Owner == building.Owner)
                return; // already this owner's own garrison — nothing to do

            if (garrison.Members.Count > 0)
            {
                // Should be unreachable — CaptureOrDestroy only ever fires once every defending
                // army on the hex (garrison included) is already empty, whether through combat
                // resolution or CaptureOrDestroyIfUndefended's own IsEngageable check. Logged, not
                // silently overwritten, so a real invariant break surfaces instead of quietly
                // stealing the previous owner's still-fielded troops.
                AiDebugLog.Write($"[BUILDING] invariant violation — captured base at ({building.Hex.Q},{building.Hex.R}) "
                    + $"still has a non-empty garrison \"{garrison.Name}\" ({garrison.Members.Count} member(s)) owned by "
                    + $"{(garrison.Owner != null ? garrison.Owner.Nickname : "nobody")}.");
                return;
            }

            PlayerSetupData previousGarrisonOwner = garrison.Owner;
            garrison.Owner = building.Owner;
            if (garrison.Controller != null && garrison.Controller.Visual != null)
                garrison.Controller.Visual.SetColor(PlayerColorPalette.Colors[building.Owner.ColorIndex]);
            AiDebugLog.Write($"[BUILDING] Base \"{building.Name}\" at ({building.Hex.Q},{building.Hex.R}) garrison transferred: "
                + $"{(previousGarrisonOwner != null ? previousGarrisonOwner.Nickname : "nobody")} → {building.Owner.Nickname}, members=0.");
        }
    }
}
