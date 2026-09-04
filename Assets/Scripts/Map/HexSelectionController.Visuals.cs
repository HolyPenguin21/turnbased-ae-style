using System.Collections.Generic;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using Game.Terrain;
using UnityEngine;

namespace Game.Map
{
    // MAP-VIS-01: the visual-memory/layout half of HexSelectionController — split out purely for
    // size (same reasoning as the Factory/Movement/Events splits), but also the home of this
    // project's one authoritative per-hex visual reconciliation pass (ReconcileHexVisualState),
    // extracted 2026-09-04 per the project owner's own root-cause report:
    //
    // ArmyRegistry.MoveArmy used to be Unregister(oldHex) [fires events] -> set army.Hex ->
    // Register(newHex) [fires events] — a single logical relocation (an ordinary move, a
    // voluntary/automatic retreat, a building-capture handover) briefly published "this army is
    // nowhere" between those two calls. HexSelectionController subscribes to several of those
    // events independently (visibility/content/stealth/building changes) and each subscriber
    // could react and lay out the hex on its own, at a slightly different moment, with a
    // different HexObjectLayout result each time — one pass centring the live building, another
    // pass (reacting to the SAME relocation a beat later) putting the remembered "Last Seen"
    // clone at an edge offset instead of hiding it. Two representations of the same building
    // ended up visible at once.
    //
    // ArmyRegistry.MoveArmy is now atomic (re-keys the index with no events firing until the
    // registry is in its final state, see that method's own comment) so no subscriber ever
    // observes the intermediate "nowhere" state any more. This file's other half of the same
    // fix is ReconcileHexVisualState: ONE method that reads CurrentViewer/live building/live
    // army representatives/remembered building/remembered armies/layout for a hex ALL AT ONCE,
    // decides the final visible state for every one of them together, and only then applies it —
    // instead of several independent callbacks each making part of that decision on their own.
    // RestackArmiesOn (below) is now a thin public compatibility wrapper around it — every
    // existing call site (this class, CardHandUI, ArmyViewerModalUI) keeps working unchanged.
    public partial class HexSelectionController
    {
        // Separate snapshots are required per human viewer: the live building visual may be
        // recoloured or destroyed while hidden, but each human must keep seeing exactly the
        // marker they last observed until that hex enters their vision again.
        private readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, MapObjectVisual>> _rememberedBuildingVisuals =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, MapObjectVisual>>();
        private sealed class RememberedArmyVisual
        {
            public HexCoord Hex;
            public ArmyData Snapshot;
            public MapObjectVisual Visual;
        }
        private readonly Dictionary<PlayerSetupData, Dictionary<int, RememberedArmyVisual>> _rememberedArmyVisuals =
            new Dictionary<PlayerSetupData, Dictionary<int, RememberedArmyVisual>>();
        private readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, List<RememberedArmyVisual>>> _rememberedArmyVisualsByHex =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, List<RememberedArmyVisual>>>();

        private void RememberCurrentlyVisibleContent(PlayerSetupData viewer)
        {
            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(viewer))
            {
                List<ArmyData> enemies = ArmyRegistry.AllAt(hex)
                    .FindAll(army => army.Owner != viewer && army.Owner != null && !army.Owner.IsNeutral
                        && BattleInitiator.IsEngageable(army, viewer));
                HumanVisualMemory.ReconcileVisibleHex(viewer, hex, enemies.ConvertAll(army => army.Id));
                foreach (ArmyData army in enemies)
                {
                    HumanVisualMemory.ObserveArmy(viewer, army, hex);
                    UpdateRememberedArmyVisual(viewer, army, hex);
                }

                BuildingData building = BuildingRegistry.FindAt(hex);
                bool exists = building != null && building.Visual != null;
                HumanVisualMemory.ObserveBuilding(viewer, hex, exists);
                if (exists)
                    UpdateRememberedBuildingVisual(viewer, hex, building.Visual);
                else
                    RemoveRememberedBuildingVisual(viewer, hex);
            }
            RemoveUnrememberedArmyVisuals(viewer);
        }

        private void UpdateRememberedArmyVisual(PlayerSetupData viewer, ArmyData source, HexCoord hex)
        {
            if (source?.Controller?.Visual == null
                || !HumanVisualMemory.TryGetArmySighting(viewer, source.Id, out HumanVisualMemory.ArmySighting sighting))
                return;
            if (!_rememberedArmyVisuals.TryGetValue(viewer, out Dictionary<int, RememberedArmyVisual> visuals))
            {
                visuals = new Dictionary<int, RememberedArmyVisual>();
                _rememberedArmyVisuals[viewer] = visuals;
            }
            bool alreadyRemembered = visuals.TryGetValue(source.Id, out RememberedArmyVisual remembered);
            if (alreadyRemembered && remembered != null && !remembered.Hex.Equals(hex))
            {
                HexCoord vacatedHex = remembered.Hex;
                RemoveRememberedArmyFromHexIndex(viewer, remembered);
                // Whatever else is still remembered on the hex this army just moved away from
                // (in this viewer's memory) may now need a simpler layout without it.
                ReapplyRememberedLayout(viewer, vacatedHex);
            }
            if (!alreadyRemembered || remembered == null || remembered.Visual == null)
            {
                MapObjectVisual visual = Instantiate(source.Controller.Visual, source.Controller.transform.position,
                    source.Controller.transform.rotation, transform);
                visual.name = source.Controller.Visual.name + " (Last Seen)";
                remembered = new RememberedArmyVisual { Visual = visual };
                visuals[source.Id] = remembered;
            }
            remembered.Hex = hex;
            remembered.Snapshot = sighting.Army;
            bool isAirArmy = AviationRules.IsAirArmy(remembered.Snapshot);
            int terrainDefense = 0;
            if (!isAirArmy && map != null && map.TryGetTerrainAt(hex, out TerrainTypeEntry terrain) && terrain != null)
                terrainDefense = terrain.defenseModifier;
            BuildingData observedBuilding = BuildingRegistry.FindAt(hex);
            int constructionDefense = !isAirArmy && observedBuilding != null && observedBuilding.IsBase ? observedBuilding.Defense : 0;
            remembered.Snapshot.VisualSnapshotConstructionDefense = constructionDefense;
            remembered.Snapshot.VisualSnapshotDefenseBonus = terrainDefense + constructionDefense;
            remembered.Visual.transform.rotation = source.Controller.transform.rotation;
            remembered.Visual.transform.localScale = source.Controller.transform.localScale;
            remembered.Visual.CopyAppearanceFrom(source.Controller.Visual);
            FactionCardCatalog ownerCatalog = source.Owner != null && cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                ? cardHandUI.StartingDeckCatalog.GetCatalog(source.Owner.Faction)
                : null;
            if (ownerCatalog != null && ownerCatalog.armyIcon != null)
                remembered.Visual.SetIcon(ownerCatalog.armyIcon);
            remembered.Visual.SetVisible(false);
            AddRememberedArmyToHexIndex(viewer, remembered);
            // Position last, after the hex index is up to date — see ReapplyRememberedLayout's
            // own comment for why a remembered building+army pair is safe to lay out for real
            // instead of collapsing to centre (per the project owner's own 2026-08-26 follow-up).
            ReapplyRememberedLayout(viewer, hex);
        }

        // Computes the SAME per-owner offset layout the live map uses (HexObjectLayout), but from
        // what THIS viewer has actually remembered TOGETHER for `hex` — never from the live/
        // current occupants, which may include something this viewer never actually confirmed.
        // A remembered building-ghost and army-ghost(s) for the same hex are always written
        // together, from the very same moment of real vision (see
        // RememberCurrentlyVisibleContent/OnBuildingVisualStateChanged) — an army that only moves
        // onto the hex AFTER fog falls never gets a remembered entry at all, so a pair that DOES
        // coexist here already reflects a single confirmed snapshot. Laying that pair out with
        // real offsets instead of forcing them into one blob therefore discloses nothing beyond
        // what's already being shown — unlike the live map, where a lone army arriving on a hex
        // whose building this viewer hasn't personally confirmed yet still must NOT get an offset
        // (that would announce the building's presence for free); this method only ever runs for
        // already-remembered content, so that original concern doesn't apply here.
        private void ReapplyRememberedLayout(PlayerSetupData viewer, HexCoord hex)
        {
            if (map == null || gameConfig == null)
                return;

            MapObjectVisual buildingVisual = null;
            if (_rememberedBuildingVisuals.TryGetValue(viewer, out Dictionary<HexCoord, MapObjectVisual> buildingVisuals))
                buildingVisuals.TryGetValue(hex, out buildingVisual);
            bool hasBuilding = buildingVisual != null;

            var owners = new List<PlayerSetupData>();
            List<RememberedArmyVisual> atHex = null;
            if (_rememberedArmyVisualsByHex.TryGetValue(viewer, out Dictionary<HexCoord, List<RememberedArmyVisual>> byHex))
                byHex.TryGetValue(hex, out atHex);
            if (atHex != null)
                foreach (RememberedArmyVisual remembered in atHex)
                    if (remembered.Snapshot != null && !owners.Contains(remembered.Snapshot.Owner))
                        owners.Add(remembered.Snapshot.Owner);

            HexObjectLayout.Result layout = HexObjectLayout.Resolve(gameConfig, hasBuilding, owners);

            if (hasBuilding)
                buildingVisual.transform.position = map.HexToWorld(hex) + ToWorldOffset(layout.BuildingOffset);
            if (atHex != null)
                foreach (RememberedArmyVisual remembered in atHex)
                {
                    if (remembered.Visual == null || remembered.Snapshot == null)
                        continue;
                    int index = owners.IndexOf(remembered.Snapshot.Owner);
                    Vector2 offset = index >= 0 ? layout.ArmyOffsets[index] : Vector2.zero;
                    remembered.Visual.transform.position = map.HexToWorld(hex) + ToWorldOffset(offset);
                }
        }

        private void AddRememberedArmyToHexIndex(PlayerSetupData viewer, RememberedArmyVisual remembered)
        {
            if (!_rememberedArmyVisualsByHex.TryGetValue(viewer,
                out Dictionary<HexCoord, List<RememberedArmyVisual>> byHex))
            {
                byHex = new Dictionary<HexCoord, List<RememberedArmyVisual>>();
                _rememberedArmyVisualsByHex[viewer] = byHex;
            }
            if (!byHex.TryGetValue(remembered.Hex, out List<RememberedArmyVisual> atHex))
            {
                atHex = new List<RememberedArmyVisual>();
                byHex[remembered.Hex] = atHex;
            }
            if (!atHex.Contains(remembered))
                atHex.Add(remembered);
        }

        private void RemoveRememberedArmyFromHexIndex(PlayerSetupData viewer, RememberedArmyVisual remembered)
        {
            if (!_rememberedArmyVisualsByHex.TryGetValue(viewer,
                out Dictionary<HexCoord, List<RememberedArmyVisual>> byHex)
                || !byHex.TryGetValue(remembered.Hex, out List<RememberedArmyVisual> atHex))
                return;
            atHex.Remove(remembered);
            if (atHex.Count == 0)
                byHex.Remove(remembered.Hex);
            if (byHex.Count == 0)
                _rememberedArmyVisualsByHex.Remove(viewer);
        }

        private void RemoveUnrememberedArmyVisuals(PlayerSetupData viewer)
        {
            if (!_rememberedArmyVisuals.TryGetValue(viewer, out Dictionary<int, RememberedArmyVisual> visuals))
                return;
            var stale = new List<int>();
            foreach (int armyId in visuals.Keys)
                if (!HumanVisualMemory.TryGetArmySighting(viewer, armyId, out _))
                    stale.Add(armyId);
            foreach (int armyId in stale)
            {
                RememberedArmyVisual remembered = visuals[armyId];
                HexCoord vacatedHex = remembered.Hex;
                RemoveRememberedArmyFromHexIndex(viewer, remembered);
                visuals.Remove(armyId);
                if (remembered.Visual != null)
                    Destroy(remembered.Visual.gameObject);
                // Whatever else is still remembered at this hex (a building ghost, another
                // owner's army ghost) may now need to fall back to a simpler layout without this
                // one — e.g. a lone remembered building re-centres once its only companion army
                // ghost is gone.
                ReapplyRememberedLayout(viewer, vacatedHex);
            }
        }

        // Stale-entry garbage collection only (an army HumanVisualMemory no longer has a sighting
        // for at all) — a whole-map sweep because "still remembered somewhere" isn't a per-hex
        // question, unlike the actual show/hide decision below. Called once per RefreshAllVisibility
        // pass (see that method), not per hex.
        private void RemoveUnrememberedArmyVisualsForAllViewers()
        {
            foreach (PlayerSetupData viewer in new List<PlayerSetupData>(_rememberedArmyVisuals.Keys))
                RemoveUnrememberedArmyVisuals(viewer);
        }

        // Per-hex counterpart of RefreshRememberedBuildingVisual, folded into
        // ReconcileHexVisualState itself (MAP-VIS-01) rather than a separate whole-map pass a
        // caller had to remember to run after the fact — that used to let ReconcileHexVisualState
        // decide live army visibility with one predicate while this decided remembered army
        // visibility with a different, non-DebugRevealAll-aware one (VisionSystem.IsVisible
        // instead of CurrentViewerCanRenderLiveHex), so the two could disagree and show a live
        // marker AND a remembered ghost for the same army at once whenever DebugRevealAll made
        // the live one visible without also making the raw vision check true (same bug class
        // CheckBuildingVisualInvariant already guards for the building case). Only ever shows a
        // SINGLE remembered marker per (hex, owner) — mirrors ReconcileHexVisualState's own live
        // "one marker per owner" rule (representativeForOwner) so a viewer who remembers several
        // of the same owner's armies on one hex doesn't get several overlapping ghosts.
        private void RefreshRememberedArmyVisualsAt(HexCoord hex)
        {
            var shownOwnersAtHex = new HashSet<PlayerSetupData>();
            foreach (KeyValuePair<PlayerSetupData, Dictionary<HexCoord, List<RememberedArmyVisual>>> entry in _rememberedArmyVisualsByHex)
            {
                if (!entry.Value.TryGetValue(hex, out List<RememberedArmyVisual> atHex))
                    continue;
                foreach (RememberedArmyVisual remembered in atHex)
                {
                    if (remembered.Visual == null)
                        continue;
                    bool visible = entry.Key == VisionSystem.CurrentViewer
                        && !CurrentViewerCanRenderLiveHex(hex)
                        && remembered.Snapshot != null
                        && shownOwnersAtHex.Add(remembered.Snapshot.Owner);
                    remembered.Visual.SetVisible(visible);
                }
            }
        }

        private void SetRememberedArmyVisualsVisible(bool visible)
        {
            foreach (Dictionary<int, RememberedArmyVisual> visuals in _rememberedArmyVisuals.Values)
                foreach (RememberedArmyVisual remembered in visuals.Values)
                    if (remembered.Visual != null)
                        remembered.Visual.SetVisible(visible);
        }

        private void UpdateRememberedBuildingVisual(PlayerSetupData viewer, HexCoord hex, MapObjectVisual source)
        {
            if (!_rememberedBuildingVisuals.TryGetValue(viewer, out Dictionary<HexCoord, MapObjectVisual> visuals))
            {
                visuals = new Dictionary<HexCoord, MapObjectVisual>();
                _rememberedBuildingVisuals[viewer] = visuals;
            }
            if (!visuals.TryGetValue(hex, out MapObjectVisual snapshot) || snapshot == null)
            {
                snapshot = Instantiate(source, source.transform.position, source.transform.rotation, transform);
                snapshot.name = source.name + " (Last Seen)";
                visuals[hex] = snapshot;
            }
            snapshot.transform.rotation = source.transform.rotation;
            snapshot.transform.localScale = source.transform.localScale;
            snapshot.CopyAppearanceFrom(source);
            snapshot.SetVisible(false);
            // Position last — see ReapplyRememberedLayout's own comment for why a remembered
            // building+army pair is safe to lay out for real instead of collapsing to centre.
            ReapplyRememberedLayout(viewer, hex);
        }

        private void RemoveRememberedBuildingVisual(PlayerSetupData viewer, HexCoord hex)
        {
            if (!_rememberedBuildingVisuals.TryGetValue(viewer, out Dictionary<HexCoord, MapObjectVisual> visuals)
                || !visuals.TryGetValue(hex, out MapObjectVisual snapshot))
                return;
            visuals.Remove(hex);
            if (snapshot != null)
            {
                snapshot.SetVisible(false);
                Destroy(snapshot.gameObject);
            }
            // The building ghost is gone — any remembered army(ies) still at this hex must fall
            // back to whatever layout fits without it (e.g. a lone remembered army re-centres).
            ReapplyRememberedLayout(viewer, hex);
        }

        private void RefreshRememberedBuildingVisual(HexCoord hex)
        {
            foreach (KeyValuePair<PlayerSetupData, Dictionary<HexCoord, MapObjectVisual>> entry in _rememberedBuildingVisuals)
            {
                if (!entry.Value.TryGetValue(hex, out MapObjectVisual snapshot) || snapshot == null)
                    continue;
                // !CurrentViewerCanRenderLiveHex, not !VisionSystem.IsVisible(entry.Key, hex) —
                // see that helper's own comment: this must read the exact same DebugRevealAll-
                // aware fact the live building's own SetVisible check just used a moment ago
                // (liveBuildingVisible in ReconcileHexVisualState), or a fogged-but-remembered hex
                // with DebugRevealAll on shows both the live building (DebugRevealAll made it
                // visible) AND this remembered ghost (raw IsVisible still says fogged) at once.
                bool visible = entry.Key == VisionSystem.CurrentViewer
                    && HumanVisualMemory.IsBuildingKnown(entry.Key, hex)
                    && !CurrentViewerCanRenderLiveHex(hex);
                snapshot.SetVisible(visible);
            }
        }

        private void SetRememberedBuildingVisualsVisible(bool visible)
        {
            foreach (Dictionary<HexCoord, MapObjectVisual> visuals in _rememberedBuildingVisuals.Values)
                foreach (MapObjectVisual snapshot in visuals.Values)
                    if (snapshot != null)
                        snapshot.SetVisible(visible);
        }

        // The one fact that decides whether a LIVE marker is allowed to render for `hex` right
        // now — same DebugRevealAll-aware check ReconcileHexVisualState's own liveBuildingVisible
        // and live-army visibility tests already use (VisionSystem.IsVisibleToCurrentViewer).
        // Named/pulled out specifically so every REMEMBERED-visual visibility decision
        // (RefreshRememberedBuildingVisual, RefreshRememberedArmyVisualsAt) reads this exact same
        // fact instead of an independently written vision check — see this file's own class
        // comment and CheckBuildingVisualInvariant for the duplicate-marker bug two different
        // predicates (one DebugRevealAll-aware, one not) used to produce.
        private static bool CurrentViewerCanRenderLiveHex(HexCoord hex) => VisionSystem.IsVisibleToCurrentViewer(hex);

        // Where a given army should sit on `hex`, resolved from every other non-empty army
        // currently there too (see HexObjectLayout) — not a fixed per-army corner any more.
        // forArmy doesn't need to already be registered at `hex`: the mover sets Data.Hex before
        // calling this, so it usually already shows up in ArmyRegistry.AllAt(hex) on its own,
        // but it's added explicitly if not, so it still gets a real slot instead of defaulting
        // to zero. Only ONE marker is ever positioned per owner (see ReconcileHexVisualState) —
        // this resolves that shared slot regardless of which of an owner's armies asks for it.
        private Vector3 ResolveArmyOffset(HexCoord hex, ArmyController forArmy)
        {
            if (gameConfig == null || map == null)
                return Vector3.zero;

            List<ArmyData> armiesHere = NonEmptyArmiesAt(hex);
            if (!armiesHere.Contains(forArmy.Data))
                armiesHere.Add(forArmy.Data);

            // Only what the current map viewer can actually see contributes to the layout — a
            // fully-hidden enemy army (stealth) or one on a hex the viewer can't see must never
            // shift `forArmy` off-centre and disclose its presence (project owner's own report,
            // кейс 4). forArmy itself always keeps a real slot even if the filter would drop it
            // (e.g. resolving the offset for an AI's own hidden army mid-move) — its own marker
            // is hidden anyway, so the returned offset is harmless, but it must not be indexed
            // out of the list.
            armiesHere = VisibleForLayout(armiesHere, hex);
            if (!armiesHere.Contains(forArmy.Data))
                armiesHere.Add(forArmy.Data);

            List<PlayerSetupData> distinctOwners = DistinctOwners(armiesHere);
            int index = distinctOwners.IndexOf(forArmy.Data.Owner);

            HexObjectLayout.Result layout = HexObjectLayout.Resolve(gameConfig, BuildingKnownToViewer(hex), distinctOwners);
            return index >= 0 ? ToWorldOffset(layout.ArmyOffsets[index]) : Vector3.zero;
        }

        // Armies with zero members don't exist for layout/visibility purposes — a freshly
        // created army (see CreateArmyMarker) or one just emptied out by ArmyViewerModalUI's
        // drag-between-armies flow is invisible until/unless it has at least one unit again.
        private static List<ArmyData> NonEmptyArmiesAt(HexCoord hex)
        {
            // IsPrison excluded outright, non-empty or not — it never gets a marker of its own
            // (see ArmyData.IsPrison's own comment), so it must never become an owner's
            // "representative" army here either, or a real army (e.g. that owner's garrison)
            // would lose its marker to a Prison that was never meant to have one at all.
            return ArmyRegistry.AllAt(hex).FindAll(a => a.Members.Count > 0 && !a.IsPrison && !a.IsAirfield);
        }

        // Every HexObjectLayout offset is in hex-radius units (x = left/right, y = world Z) —
        // this converts one to an actual world-space offset from a hex's centre, shared by
        // every call site below instead of repeating the same Vector2->Vector3 math each time.
        private Vector3 ToWorldOffset(Vector2 layoutOffset)
        {
            return new Vector3(layoutOffset.x, 0f, layoutOffset.y) * map.OuterRadius;
        }

        // MAP-VIS-01: public compatibility wrapper — every existing call site (this class,
        // CardHandUI, ArmyViewerModalUI) keeps calling this exact name/signature; the actual
        // work now lives in ReconcileHexVisualState below, the one authoritative reconciliation
        // pass for a hex's visual state.
        public void RestackArmiesOn(HexCoord hex, ArmyController exclude)
        {
            ReconcileHexVisualState(hex, exclude);
        }

        // The one authoritative per-hex visual reconciliation pass (MAP-VIS-01): computes
        // CurrentViewer, current hex visibility, the live building, live visible army
        // representatives, remembered building, remembered armies, live layout and remembered
        // layout ALL TOGETHER, decides the final visible state for every one of them, and only
        // THEN applies transforms/visibility — never several independent decisions made by
        // separate callbacks at different moments (see this file's own class comment for the
        // duplicate-citadel bug this replaces). `exclude` is whichever army is mid-move right now
        // and already positions itself via its own MoveAlong.
        private void ReconcileHexVisualState(HexCoord hex, ArmyController exclude)
        {
            if (gameConfig == null || map == null)
                return;

            List<ArmyData> armiesHere = NonEmptyArmiesAt(hex);
            bool hasBuilding = FindOwnerAt(hex) != null;

            // Airfields have no independent marker. When one stores aircraft, the owner's garrison
            // marker represents that container, exactly as it represents ground cards — even when
            // the garrison itself is empty (nothing ever staged there), so it still needs a slot in
            // armiesHere below to get laid out and shown at all.
            var airfieldOwners = new HashSet<PlayerSetupData>(ArmyRegistry.AllAt(hex)
                .FindAll(a => a.IsAirfield && a.Members.Count > 0).ConvertAll(a => a.Owner));
            foreach (PlayerSetupData owner in airfieldOwners)
            {
                if (armiesHere.Exists(a => a.Owner == owner))
                    continue;
                ArmyData garrison = ArmyRegistry.FindGarrisonAt(hex, owner);
                if (garrison != null)
                    armiesHere.Add(garrison);
            }

            // Layout is resolved from ONLY what the current map viewer can see (see
            // VisibleForLayout) — never the raw occupant list. A fully-hidden enemy army
            // (stealth) or one on a hex the viewer has no vision of must not contribute an owner
            // slot, or the viewer's own army here gets shoved into a two-owners side slot and
            // silently reveals "someone is on this hex" (project owner's own report, кейс 4).
            // The per-marker visibility test further down already hides that army's own marker;
            // this keeps the REST of the hex laid out as if it weren't there at all.
            List<PlayerSetupData> distinctOwners = DistinctOwners(VisibleForLayout(armiesHere, hex));

            // An army that just dropped to zero members (its last unit dragged out, see
            // CardHandUI/ArmyViewerModalUI's own RestackArmiesOn calls) drops out of armiesHere
            // above from this point on — nobody else ever tells its marker to hide, so it must
            // be done explicitly here, once, right as that happens (see the loop after this
            // method's main one below). Excludes anything just added to armiesHere above (an empty
            // garrison standing in for its owner's non-empty airfield) — that one must stay shown,
            // not be force-hidden right back down by this same-turn sweep.
            List<ArmyData> emptiedHere = ArmyRegistry.AllAt(hex).FindAll(a => a.Members.Count == 0 && !armiesHere.Contains(a));

            // hasBuilding above still drives whether the building marker is touched at all;
            // BuildingKnownToViewer gates whether it factors into the LAYOUT — a building the
            // viewer hasn't personally confirmed must not pull a lone army onto its corner
            // offset (that would announce the building for free), same concern
            // ReapplyRememberedLayout already documents for the remembered path.
            HexObjectLayout.Result layout = HexObjectLayout.Resolve(gameConfig, BuildingKnownToViewer(hex), distinctOwners);

            // One player can have several armies sharing a hex (garrison + one or more named
            // armies), but only one marker per owner is ever actually shown there — the rest
            // stay hidden behind it rather than stacking (see ArmyData.Controller; a unit has no
            // marker of its own at all any more, only its army does). Normally whichever army is
            // encountered first, but the currently selected one (if it's here) always wins
            // instead — so picking a different army via the hex-side button row (see
            // SelectArmyForOrders) actually shows, and later animates, THAT army, not an
            // arbitrary one. An army that's mid-move stays visible regardless (it's the thing the
            // player is watching travel), even if it isn't the representative one.
            var representativeForOwner = new Dictionary<PlayerSetupData, ArmyData>();
            foreach (ArmyData army in armiesHere)
                if (!representativeForOwner.ContainsKey(army.Owner))
                    representativeForOwner[army.Owner] = army;
            if (_selectedArmy != null && armiesHere.Contains(_selectedArmy.Data))
                representativeForOwner[_selectedArmy.Data.Owner] = _selectedArmy.Data;

            // Airfields have no independent marker (see airfieldOwners above, computed early
            // enough to already have added the representative garrison to armiesHere itself).
            // When one stores aircraft, the owner's garrison marker represents that container,
            // exactly as it represents ground cards.
            foreach (PlayerSetupData owner in airfieldOwners)
            {
                ArmyData garrison = armiesHere.Find(a => a.Owner == owner && a.IsGarrison);
                if (garrison != null)
                    representativeForOwner[owner] = garrison;
            }

            for (int i = 0; i < armiesHere.Count; i++)
            {
                ArmyData army = armiesHere[i];
                ArmyController controller = army.Controller;
                if (controller == null)
                    continue;

                // A neutral army never moves on its own (see SpawnNeutralArmy/SpawnEventGuard —
                // both place it once and never relocate it again), so once a viewer has actually
                // seen it, remembering it there can never go stale the way remembering a real
                // player's army would — same "remembered once seen" exception the resource row
                // already gets (see VisionSystem.HasEverSeenByCurrentViewer, MapResourceDisplay).
                bool everSeenNeutral = army.Owner != null && army.Owner.IsNeutral && VisionSystem.HasEverSeenByCurrentViewer(hex);
                bool visible = (representativeForOwner[army.Owner] == army || controller.IsMoving)
                    && (army.Owner == VisionSystem.CurrentViewer || CurrentViewerCanRenderLiveHex(hex)
                        || everSeenNeutral)
                    // An enemy army every member of which is hidden from the current viewer
                    // (and undetected) shows no marker at all (see Game.Map.StealthSystem). A
                    // mixed army still shows — its visible members are real.
                    && !(army.Owner != VisionSystem.CurrentViewer
                         && StealthSystem.ArmyFullyHiddenFrom(army, VisionSystem.CurrentViewer));
                if (controller.Visual != null)
                {
                    controller.Visual.SetVisible(visible);
                    if (visible)
                    {
                        FactionCardCatalog ownerCatalog = cardHandUI != null && cardHandUI.StartingDeckCatalog != null
                            ? cardHandUI.StartingDeckCatalog.GetCatalog(army.Owner.Faction)
                            : null;
                        if (ownerCatalog != null)
                        {
                            Sprite icon = (AviationRules.IsAirArmy(army) || (army.IsGarrison && airfieldOwners.Contains(army.Owner))) && ownerCatalog.airArmyIcon != null
                                ? ownerCatalog.airArmyIcon : ownerCatalog.armyIcon;
                            if (icon != null)
                                controller.Visual.SetIcon(icon);
                        }
                    }
                }
                if (controller == exclude || controller.IsMoving)
                    continue;
                // -1 when this army's owner was filtered out of the layout for the current
                // viewer (VisibleForLayout) — its marker is already hidden by the `visible`
                // test above, so its resting position no longer matters; leave it where it is
                // rather than indexing the layout out of range.
                int ownerIndex = distinctOwners.IndexOf(army.Owner);
                if (ownerIndex < 0)
                    continue;
                controller.transform.position = map.HexToWorld(hex) + ToWorldOffset(layout.ArmyOffsets[ownerIndex]);
            }

            // Only ever hides — never deletes. This runs for EVERY membership change on this
            // hex, including a brand-new army that was just created and hasn't been given its
            // first member yet (see ArmyViewerModalUI.CreateArmy), which is empty by design and
            // must not be torn down for it. Actually deleting an army that's been emptied OUT
            // (as opposed to never filled) only ever happens where a member is actually removed
            // — see DeleteArmyIfEmptied, called explicitly from there instead.
            foreach (ArmyData army in emptiedHere)
                if (army.Controller != null && army.Controller.Visual != null)
                    army.Controller.Visual.SetVisible(false);

            bool liveBuildingVisible = false;
            if (hasBuilding)
            {
                BuildingData building = BuildingRegistry.FindAt(hex);
                if (building != null && building.Visual != null)
                {
                    building.Visual.transform.position = map.HexToWorld(hex) + ToWorldOffset(layout.BuildingOffset);
                    liveBuildingVisible = building.Owner == VisionSystem.CurrentViewer || CurrentViewerCanRenderLiveHex(hex);
                    building.Visual.SetVisible(liveBuildingVisible);
                }
            }

            // Building invariant (MAP-VIS-01 §4): currently visible -> live shown, remembered
            // hidden; known-but-not-visible -> live hidden, remembered shown; unknown -> both
            // hidden. RefreshRememberedBuildingVisual (still in this same pass, right after the
            // live building's own visibility was just decided above) is what actually sets the
            // remembered clone's visibility per this same rule (entry.Key == CurrentViewer &&
            // IsBuildingKnown && !CurrentViewerCanRenderLiveHex) — reading it back here right
            // after is what lets the DEV invariant check below catch the two ever disagreeing.
            RefreshRememberedBuildingVisual(hex);
            // Same "one reconciliation pass" treatment for remembered ARMY ghosts — used to be a
            // separate whole-map RefreshRememberedArmyVisuals() call a caller had to remember to
            // run after the fact (RefreshAllVisibility, below); folded in here so live and
            // remembered army visibility are always decided together, right here, from the same
            // CurrentViewerCanRenderLiveHex fact the armiesHere loop above already used.
            RefreshRememberedArmyVisualsAt(hex);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            CheckBuildingVisualInvariant(hex, liveBuildingVisible);
            CheckArmyVisualInvariant(hex, armiesHere);
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // MAP-VIS-01 §8: DEV-only invariant check — a live building visual and its remembered
        // "Last Seen" clone must never BOTH be visible to the same viewer on the same hex at
        // once (the exact duplicate-citadel symptom this whole pass exists to prevent). Compiled
        // out of a shipping build entirely (Editor + Development Build only), same gate the spec
        // itself calls out for this kind of correctness assertion.
        private void CheckBuildingVisualInvariant(HexCoord hex, bool liveBuildingVisible)
        {
            PlayerSetupData viewer = VisionSystem.CurrentViewer;
            if (viewer == null)
                return;
            if (!_rememberedBuildingVisuals.TryGetValue(viewer, out Dictionary<HexCoord, MapObjectVisual> visuals)
                || !visuals.TryGetValue(hex, out MapObjectVisual snapshot) || snapshot == null)
                return;
            bool rememberedBuildingVisible = snapshot.IsVisible;
            if (liveBuildingVisible && rememberedBuildingVisible)
                Debug.LogError($"[MAP-VIS] invariant violation: viewer={viewer.Nickname} hex=({hex.Q},{hex.R}) "
                    + $"liveBuildingVisible=true rememberedBuildingVisible=true");
        }

        // Same invariant as CheckBuildingVisualInvariant, for the army-ghost side of this pass
        // (RefreshRememberedArmyVisualsAt) — a live army marker and a remembered "Last Seen"
        // ghost of the SAME owner on the SAME hex must never both be visible to the current
        // viewer at once. Checked per owner (not per army) since only one live marker is ever
        // shown per owner (representativeForOwner) and only one remembered ghost per owner
        // (RefreshRememberedArmyVisualsAt's own shownOwnersAtHex dedup).
        private void CheckArmyVisualInvariant(HexCoord hex, List<ArmyData> armiesHere)
        {
            PlayerSetupData viewer = VisionSystem.CurrentViewer;
            if (viewer == null || !_rememberedArmyVisualsByHex.TryGetValue(viewer,
                out Dictionary<HexCoord, List<RememberedArmyVisual>> byHex)
                || !byHex.TryGetValue(hex, out List<RememberedArmyVisual> rememberedAtHex))
                return;

            foreach (ArmyData army in armiesHere)
            {
                if (army?.Controller?.Visual == null || !army.Controller.Visual.IsVisible)
                    continue;
                foreach (RememberedArmyVisual remembered in rememberedAtHex)
                {
                    if (remembered.Visual == null || !remembered.Visual.IsVisible
                        || remembered.Snapshot == null || remembered.Snapshot.Owner != army.Owner)
                        continue;
                    Debug.LogError($"[MAP-VIS] invariant violation: viewer={viewer.Nickname} hex=({hex.Q},{hex.R}) "
                        + $"owner={army.Owner?.Nickname} liveArmyVisible=true rememberedArmyVisible=true");
                }
            }
        }
#endif

        // Re-runs the visibility half of ReconcileHexVisualState (army/building marker show/hide)
        // for EVERY occupied hex on the map, without touching anyone's actual world position —
        // subscribed to VisionSystem.VisibilityChanged and GameTurnController.TurnStateChanged
        // (see OnEnable) since either can change what VisionSystem.CurrentViewer can currently
        // see: the mover's own vision expanding step-by-step during a move (see
        // HexSelectionController.Movement.cs's HandleVisionStep), or the map simply now being
        // rendered from a different human's perspective after the turn passed to them.
        private void RefreshAllVisibility()
        {
            if (map == null)
                return;
            var hexes = new HashSet<HexCoord>(ArmyRegistry.AllOccupiedHexes());
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
                hexes.Add(building.Hex);
            // Include every snapshot, not only the current viewer's known hexes. Otherwise a
            // destroyed-building snapshot belonging to the previous hot-seat viewer is never
            // visited (there is no live registry entry left to contribute its hex) and remains
            // incorrectly visible after perspective switches.
            foreach (Dictionary<HexCoord, MapObjectVisual> visuals in _rememberedBuildingVisuals.Values)
                foreach (HexCoord hex in visuals.Keys)
                    hexes.Add(hex);
            // Same reasoning as the remembered-building hexes just above, for remembered ARMY
            // ghosts: a hex with only a memory of an army that's since fully left ArmyRegistry
            // (destroyed while hidden, say) has no live registry/building entry left to
            // contribute it to `hexes` on its own, so without this its ghost would never get
            // reconciled — see RefreshRememberedArmyVisualsAt, now run per-hex from inside
            // ReconcileHexVisualState below rather than as its own separate whole-map pass.
            foreach (Dictionary<HexCoord, List<RememberedArmyVisual>> byHex in _rememberedArmyVisualsByHex.Values)
                foreach (HexCoord hex in byHex.Keys)
                    hexes.Add(hex);
            foreach (HexCoord hex in hexes)
                ReconcileHexVisualState(hex, null);
            // Whole-map stale-entry cleanup only (see its own comment) — the actual show/hide
            // decision for every still-remembered army ghost already happened per-hex above.
            RemoveUnrememberedArmyVisualsForAllViewers();
        }

        // HexObjectLayout lays out one slot per distinct owner, not one per army — same-owner
        // armies on a hex collapse to a single visible marker (see ReconcileHexVisualState), so
        // the layout must never see an owner twice or it falls through to the "not designed yet"
        // fallback (e.g. a building + 2 same-owner armies would wrongly stack at hex centre
        // instead of keeping the building's corner offset).
        private static List<PlayerSetupData> DistinctOwners(List<ArmyData> armies)
        {
            var result = new List<PlayerSetupData>(armies.Count);
            foreach (ArmyData army in armies)
                if (!result.Contains(army.Owner))
                    result.Add(army.Owner);
            return result;
        }

        // Whoever owns the building at this hex (citadel or a player-built Base alike) — there's
        // still no territory/zone-of-control system, so "owns this hex" and "owns the building
        // here" are the same question. Delegates to BuildingRegistry rather than only matching
        // each player's own citadel coordinates, now that IsBase buildings can exist anywhere a
        // Base card gets played, not just on the starting citadel hex.
        private static PlayerSetupData FindOwnerAt(HexCoord coord)
        {
            return BuildingRegistry.FindAt(coord)?.Owner;
        }

        // Only the armies the CURRENT MAP VIEWER can actually see on `hex` — everything the icon
        // layout (HexObjectLayout) is allowed to reason about. Mirrors the per-marker visibility
        // test in ReconcileHexVisualState so the layout and the shown markers can never disagree:
        //  - the viewer's own armies always count;
        //  - an enemy army counts only if the viewer has vision of the hex AND not every member
        //    is hidden from them (StealthSystem) — a fully-hidden enemy contributes nothing, so
        //    the viewer's own army never shifts to make room for a phantom (project owner's own
        //    report, кейс 4);
        //  - a neutral army the viewer has EVER seen counts even without current vision — the
        //    same "remembered once seen" exception ReconcileHexVisualState already grants its
        //    marker (neutrals never relocate).
        // With no hot-seat perspective set (CurrentViewer == null — AI-only / pre-game) nothing
        // is filtered, exactly as before this method existed.
        private static List<ArmyData> VisibleForLayout(List<ArmyData> armies, HexCoord hex)
        {
            PlayerSetupData viewer = VisionSystem.CurrentViewer;
            if (viewer == null)
                return armies;
            bool hexVisible = VisionSystem.IsVisibleToCurrentViewer(hex);
            var result = new List<ArmyData>(armies.Count);
            foreach (ArmyData army in armies)
            {
                bool own = army.Owner == viewer;
                bool visibleEnemy = hexVisible && !StealthSystem.ArmyFullyHiddenFrom(army, viewer);
                bool everSeenNeutral = army.Owner != null && army.Owner.IsNeutral
                    && VisionSystem.HasEverSeenByCurrentViewer(hex);
                if (own || visibleEnemy || everSeenNeutral)
                    result.Add(army);
            }
            return result;
        }

        // Whether the current map viewer has actually confirmed the building on `hex` — the
        // layout must treat a building the viewer has never personally seen as absent, so a lone
        // army arriving there still sits centred instead of taking the building/army corner
        // offset and announcing the building for free (same concern ReapplyRememberedLayout
        // documents for the remembered path). Its own-building fast path matches
        // ReconcileHexVisualState's building-marker SetVisible check.
        private static bool BuildingKnownToViewer(HexCoord hex)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null)
                return false;
            return building.Owner == VisionSystem.CurrentViewer || VisionSystem.IsVisibleToCurrentViewer(hex);
        }
    }
}
