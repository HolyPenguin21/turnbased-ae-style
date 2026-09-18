using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cameras;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Styles;
using Game.Terrain;
using Game.Turns;
using Game.UI;
using Game.Units;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.Setup
{
    // Everything about the "place your citadel" setup step, in one place: assigns each
    // player a candidate starting hex (near the map edge, spread apart), then walks through
    // EVERY player, in order, one at a time — human or AI alike. A human player's turn pans
    // the camera to their candidate, shows the confirm popup once it arrives, and waits for
    // them to click a hex in their candidate's neighbourhood. An AI player's turn (no
    // decision-making exists yet) just resolves instantly onto its candidate — still its own
    // turn in the sequence, so real AI logic can slot in later without reshaping this loop.
    // Only once the very last player in the list has been resolved does the whole step get
    // cleaned up — one PlayerRoot container per player (plus a neutral one) gets created
    // first, so citadel/unit markers can move there and outlive the cleanup.
    //
    // This step's temporary visuals (region/selection highlight markers) are spawned as
    // children of this GameObject, so the final cleanup just destroys this whole object
    // (plus the popup canvas) in one shot — no separate per-highlight cleanup calls needed.
    // Citadel markers are spawned unparented instead, specifically so they're unaffected by
    // that cleanup.
    public partial class CitadelSetupController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private HexMap map;
        [SerializeField] private RtsCameraController cameraController;
        [SerializeField] private HexSelectionController hexSelectionController;

        [Header("Popup")]
        [SerializeField] private GameObject popupCanvas;
        [SerializeField] private Button confirmButton;

        [Header("Camera")]
        // Camera feel/pacing stays local, not on GameConfig — this is this step's own pan
        // timing, not a shared setting.
        [SerializeField] private float cameraPanDuration = 1.5f;

        [Header("Config")]
        // Starting-hex rule, highlight prefabs/height, ground raycast layer, citadel marker
        // prefab/resource bonus all live on the shared GameConfig asset — see GameConfig. The
        // icon itself now comes from each player's own catalog (see ResolveCatalog below).
        [SerializeField] private GameConfig gameConfig;

        [Header("Cards")]
        // Looked up per-player via ResolveCatalog (GetCatalog(player.Faction)) for that
        // player's own starting citadel card art/icon — same per-player resolution CardHandUI
        // already uses, so a mixed-faction game no longer stamps every citadel with whichever
        // faction happened to be wired into a single fixed reference.
        [SerializeField] private StartingDeckCatalog startingDeckCatalog;

        // Hand-authored neutral armies placed by GenerateNeutralArmies during map generation —
        // see NeutralArmyCatalog's own comment ("available for map generation to place fixed
        // armies on the map"). Assign the NeutralArmyCatalog asset in the Inspector.
        [SerializeField] private NeutralArmyCatalog neutralArmyCatalog;

        // Hand-authored Hex Events placed by GenerateRandomEvents during map generation — see
        // EventCatalog's own comment. A guarded event's guard army still resolves through
        // neutralArmyCatalog above (EventDefinition.guardArmyName), same [ArmyTag] convention as
        // every other map-guard reference; this catalog only supplies the event's own
        // image/description/rewards.
        [SerializeField] private EventCatalog eventCatalog;

        [Header("Turns")]
        // Kicked off once every player has placed their citadel — see GameTurnController.
        [SerializeField] private GameTurnController turnController;

        private readonly Dictionary<PlayerSetupData, HexCoord> _startHexes = new Dictionary<PlayerSetupData, HexCoord>();
        private readonly List<HexCoord> _validHexes = new List<HexCoord>();
        private readonly List<PlayerSetupData> _allPlayers = new List<PlayerSetupData>();
        private readonly Dictionary<PlayerSetupData, MapObjectVisual> _citadelMarkers = new Dictionary<PlayerSetupData, MapObjectVisual>();

        private HexShaderHighlight _selectedHighlight;
        private HexCoord _currentCandidate;
        private HexCoord? _placedHex;
        private int _currentPlayerIndex = -1;
        private bool _canPlace;

        private void Start()
        {
            if (hexSelectionController != null)
                hexSelectionController.enabled = false;

            // No player has a citadel on the map yet, so there's nothing meaningful to pan/zoom
            // toward — camera stays locked on whatever framing it started with for this entire
            // step (PanTo's own programmatic glides between candidates still work regardless,
            // same as during the Tactical Battle Module; see SetPanningEnabled's own comment).
            // Re-enabled in FinishAllPlacements once every player's citadel is actually placed.
            if (cameraController != null)
                cameraController.SetPanningEnabled(false);

            if (confirmButton != null)
            {
                confirmButton.interactable = false;
                confirmButton.onClick.RemoveAllListeners();
                confirmButton.onClick.AddListener(OnConfirmClicked);
            }

            if (gameConfig == null || map == null || GameSession.Players == null || GameSession.Players.Count == 0)
                return;

            BuildingRegistry.Clear();
            HexResourceBonusRegistry.Clear();
            HexEventRegistry.Clear();
            ArmyRegistry.Clear();
            PlayerRootRegistry.Clear();
            AntiAirState.Clear();
            // Coordinate-keyed (not object-reference-keyed like the registries above) — a leftover
            // entry here would misreport a pending battle at that hex in a BRAND NEW game, and
            // GameTurnController's own end-of-turn drain could resolve it against destroyed
            // previous-session armies/players. Found missing during the same audit that caught
            // AntiAirState above.
            Game.Combat.DelayedBattleRegistry.Clear();
            AiHandRegistry.Clear();
            Game.Ai.V2.StrategicCapabilityLeaseRegistry.ClearAll();
            Game.Ai.V2.ReconCapacityDeficitRegistry.ClearAll();
            // Configure before AssignStartingHexes/the per-player loop below ever registers an
            // army or building — both registries recompute vision the moment something is
            // registered (see ArmyRegistry.Register/BuildingRegistry.Register), so the radii
            // need to already be known by then, not set afterward.
            VisionSystem.Clear();
            VisionSystem.Configure(gameConfig);
            // Stealth detection needs the terrain move-cost at a hidden unit's hex for its
            // hide-dice bump; hand StealthSystem a lookup into the live map here, same
            // ordering rationale as VisionSystem.Configure above.
            StealthSystem.Clear();
            StealthSystem.TerrainMoveCostProvider = hex =>
                map.TryGetTerrainAt(hex, out TerrainTypeEntry terrainEntry) && terrainEntry != null
                    ? Mathf.Max(1, terrainEntry.moveCost)
                    : 1;
            // Same ordering requirement as VisionSystem.Configure above — AiMapMemory listens to
            // VisionSystem.VisibilityChanged, which the very first Register call below can
            // already fire, so the subscription (and a clean slate from any previous game) both
            // need to be in place before that happens.
            AiMapMemory.Clear();
            AiMapMemory.EnsureSubscribed(map);
            Game.Ai.V2.AirSortieRegistry.Clear();
            Game.Ai.V2.AiRadarStateRegistry.Clear(); // Strategy V2 per-player smoothing / loss-pulse state
            Game.Ai.V2.AiReconMemory.Clear();        // Strategy V2 long recon observation history
            Game.Ai.V2.ScoutTrailRegistry.ClearAll(); // Strategy V2 bounded per-scout backtrack trail (spec §5)
            Game.Ai.V2.ResourceStarvationRegistry.Clear(); // Strategy V2 decaying resource-starvation economic feedback (spec §17)
            Game.Ai.V2.AiAllocatorStateRegistry.Clear(); // Strategy V2 per-player allocator reject-cooldown map
            Game.Ai.V2.MissionIntentRegistry.Clear();    // Strategy V2 per-player durable mission-intent store (step 7)
            Game.Ai.V2.CapabilityPoolExhaustionRegistry.Clear(); // Strategy V2 per-turn capability-pool exhaustion scope
            Game.Ai.V2.V2TurnActivityTelemetry.Clear();          // Strategy V2 per-turn main/reaction/total activity record
            Game.Ai.V2.AiV2Trace.Clear();                        // Strategy V2 per-player debuggability trace scopes (correlation ids)
            Game.Turns.InitiativePublicHistory.Clear();          // public previous-initiative results (opponent estimate)
            Game.Ai.V2.Initiative.InitiativeAnalyticsHistory.Clear(); // per-player initiative AP telemetry
            AiResourceReservation.Clear();
            AssignStartingHexes(GameSession.Players);

            _allPlayers.Clear();
            _allPlayers.AddRange(GameSession.Players);
            if (_allPlayers.Count == 0)
                return;

            BeginPlayerTurn(0);
        }

        // --- Per-player turn sequencing --------------------------------------------------
        // One ordered pass over EVERY player, human or AI — not "all humans, then the rest".
        // The whole step only finishes once the last player in this list, whoever they are,
        // has been resolved.

        private void BeginPlayerTurn(int index)
        {
            _currentPlayerIndex = index;
            PlayerSetupData player = _allPlayers[index];
            _currentCandidate = _startHexes[player];

            if (!player.IsHuman)
            {
                // No AI decision-making exists yet — for now an AI "turn" is just an instant
                // confirmation on its candidate. Structured as its own turn (not skipped) so
                // real AI logic can slot in here later without reshaping this loop.
                FinalizePlayer(player, _currentCandidate);
                AdvanceToNextPlayer();
                return;
            }

            BuildValidHexes(_currentCandidate);
            _placedHex = null;
            _canPlace = false;
            if (confirmButton != null)
                confirmButton.interactable = false;
            if (popupCanvas != null)
                popupCanvas.SetActive(false);

            // A fresh marker per player (not reused) so everyone's confirmed pick stays
            // visible as we move on to the next player. Always the same technical colour
            // (not the player's own) — this marks "your citadel", not "which player".
            if (gameConfig != null)
            {
                var highlightObject = new GameObject("SelectedHighlight");
                highlightObject.transform.SetParent(transform, false);
                _selectedHighlight = highlightObject.AddComponent<HexShaderHighlight>();
                _selectedHighlight.ApplyStyle(HexShaderHighlight.FixedMapSelectionStyle);
                _selectedHighlight.SetColor(TechnicalColors.HexSelection);
            }

            if (cameraController != null)
                cameraController.PanTo(map.HexToWorld(_currentCandidate), cameraPanDuration, ShowPopup);
            else
                ShowPopup(); // no camera configured — just show it immediately
        }

        private void FinalizePlayer(PlayerSetupData player, HexCoord hex)
        {
            player.CitadelHexQ = hex.Q;
            player.CitadelHexR = hex.R;

            // The hex is about to hold exactly the citadel building, nothing else — resolved
            // explicitly here rather than by querying live map/registry state, since the marker
            // doesn't exist yet at this point for a query to find.
            HexObjectLayout.Result layout = HexObjectLayout.Resolve(gameConfig, hasBuilding: true, new List<PlayerSetupData>());
            SpawnCitadelMarker(player, hex, layout.BuildingOffset);
            CreateGarrison(player, hex);
            CreatePrison(player, hex);

            // The hex's resource display (already showing its plain terrain yield since map
            // generation) needs to be redrawn now with the citadel's bonus folded in.
            MapResourceDisplay resourceDisplay = map != null ? map.GetComponent<MapResourceDisplay>() : null;
            if (resourceDisplay != null)
                resourceDisplay.RefreshHex(hex);
        }

        private FactionCardCatalog ResolveCatalog(PlayerSetupData player) =>
            player != null && startingDeckCatalog != null ? startingDeckCatalog.GetCatalog(player.Faction) : null;

        // Not parented under this controller (which FinishAllPlacements destroys) or even
        // under the map yet — it ends up under the player's own PlayerRoot once that's
        // created, at the end of the whole step (see CreatePlayerRoots).
        private void SpawnCitadelMarker(PlayerSetupData player, HexCoord hex, Vector2 offset2D)
        {
            if (map == null)
                return;

            FactionCardCatalog catalog = ResolveCatalog(player);
            if (catalog == null || catalog.citadelPrefab == null)
                return;

            MapObjectVisual marker = Instantiate(catalog.citadelPrefab);
            Vector3 offset = new Vector3(offset2D.x, 0f, offset2D.y) * map.OuterRadius;
            marker.transform.position = map.HexToWorld(hex) + offset;
            marker.SetColor(PlayerColorPalette.Colors[player.ColorIndex]);
            if (catalog != null && catalog.citadelIcon != null)
                marker.SetIcon(catalog.citadelIcon);
            marker.SetSortingOrder(MapSortingOrder.BuildingCircle, MapSortingOrder.BuildingIcon);
            _citadelMarkers[player] = marker;

            CardDefinition citadelCard = catalog != null ? catalog.ForType(CardType.Base).FirstOrDefault() : null;

            // Same stats source as a "Concord Citadel" card played later (see
            // HexSelectionController.SpawnBuilding) — both read the citadel card's own
            // hitPoints/defenseRating/resistanceRating/fate now, so the two can never drift
            // apart the way the old GameConfig.startingStructurePoints-and-friends baseline
            // once could. 1 is just a safe non-zero fallback for a misconfigured scene with no
            // catalog assigned — same defensive spirit as citadelCard's own null checks below.
            int structurePoints = citadelCard != null ? citadelCard.hitPoints : 1;
            var building = new BuildingData
            {
                Name = "Citadel", Hex = hex, Owner = player, Visual = marker,
                Art = citadelCard != null ? citadelCard.art : null,
                DetailArt = citadelCard != null ? (citadelCard.detailArt != null ? citadelCard.detailArt : citadelCard.art) : null,
                Level = 1,
                StructurePointsMax = structurePoints,
                StructurePointsCurrent = structurePoints,
                Defense = citadelCard != null ? citadelCard.defenseRating : 1,
                Resistance = citadelCard != null ? citadelCard.resistanceRating : 1,
                Fate = citadelCard != null ? citadelCard.fate : 1,
                // Starting citadel must read the same airfield configuration as a later Base
                // card; otherwise the first aircraft has no valid deployment target.
                AirfieldCapacity = citadelCard != null ? Mathf.Max(0, citadelCard.airfieldCapacity) : 0,
                // The one and only difference from a "Concord Citadel" card played later (see
                // HexSelectionController.SpawnBuilding) — same card, same abilities below, but
                // only THIS building's destruction ends the game for this player (see
                // BuildingRegistry.BuildingDestroyed).
                IsStartingCitadel = true,
            };
            building.IsBase = true;
            // Abilities come from the card itself (Barracks, Citadel, the 4 CollectX — see the
            // catalog) rather than being hardcoded here a second time — a card played later
            // reads the exact same list (see SpawnBuilding), so the two can never drift apart
            // again the way Supply/CollectX once did.
            if (citadelCard != null)
                foreach (string ability in citadelCard.grantedAbilities)
                    building.Abilities.Add(ability);
            BuildingRegistry.Register(hex, building);

            // The bonus belongs to the hex the player chose, permanently — not to the citadel's
            // continued presence there (see HexResourceBonusRegistry). Stamped once, here, at
            // the moment the citadel is actually placed.
            HexResourceBonusRegistry.Set(hex, gameConfig.citadelResourceBonus);
        }

        // Every citadel starts with one — deployed Unit/Hero cards land here first (see
        // CardHandUI.TryPlayCard), matching the original game's manual rather than needing a
        // separate "unassigned pile" data structure. Not the same thing as a player-created
        // army (see ArmyData.IsGarrison) — this one is never renamed, and its capacity rule is
        // its own (faction base + bonus, not a hero's Command Rating).
        private void CreateGarrison(PlayerSetupData player, HexCoord hex)
        {
            var garrison = new ArmyData { Name = "Garrison", Hex = hex, Owner = player, IsGarrison = true };
            ArmyRegistry.Register(garrison);
            hexSelectionController?.CreateArmyMarker(garrison);
        }

        // Holds this player's Captured enemy heroes (see BattleScreenUI.Combat.cs's
        // TryImprison) — starts empty and stays that way for most games. Deliberately no
        // CreateArmyMarker call: unlike the garrison, this one never gets a map icon at all,
        // empty or not (see ArmyData.IsPrison's own comment).
        private void CreatePrison(PlayerSetupData player, HexCoord hex)
        {
            var prison = new ArmyData { Name = "Prison", Hex = hex, Owner = player, IsPrison = true };
            ArmyRegistry.Register(prison);
        }

        private void AdvanceToNextPlayer()
        {
            int nextIndex = _currentPlayerIndex + 1;
            if (nextIndex < _allPlayers.Count)
                BeginPlayerTurn(nextIndex);
            else
                FinishAllPlacements();
        }

        private void ShowPopup()
        {
            _canPlace = true;
            if (popupCanvas != null)
                popupCanvas.SetActive(true);
        }

        // --- Starting hex assignment (near the edge, spread apart) ---------------------
        //
        // The field is a hexagon of hexes around (0,0) now, not a rectangle (see
        // HexMapGenerator) — "spread around the map" means angular sectors around that centre
        // (one per player) rather than a farthest-point search over a rectangular edge band.
        // Per the project owner's own call: still one player per sector so they can't clump,
        // but BOTH the sector-ring rotation and which eligible hex within a sector gets picked
        // are randomised per game, so starting spots don't always land in the same place.

        private void AssignStartingHexes(List<PlayerSetupData> players)
        {
            _startHexes.Clear();

            List<HexCoord> eligible = GetEdgeEligibleHexes();
            if (eligible.Count == 0 || players.Count == 0)
                return;

            List<HexCoord>[] sectors = BucketByAngleAroundCenter(eligible, players.Count);

            for (int i = 0; i < players.Count; i++)
            {
                // A sector can come up empty if the edge band is sparse there (e.g. corner/City
                // ruins exclusions ate every candidate in it) — fall back to any still-unused
                // eligible hex rather than leaving that player unplaced.
                List<HexCoord> pool = sectors[i].Count > 0 ? sectors[i] : eligible;
                HexCoord pick = pool[Random.Range(0, pool.Count)];
                eligible.Remove(pick);
                foreach (List<HexCoord> sector in sectors)
                    sector.Remove(pick);

                _startHexes[players[i]] = pick;
                SpawnRegionHighlight(players[i], pick);
            }
        }

        // Buckets `coords` into `sectorCount` angular slices around the field's (0,0) centre —
        // shared by starting-hex assignment above and GenerateResources' outside-resource
        // spread (CitadelSetupController.MapContent.cs's BuildEvenSectors calls this too). The
        // slice boundaries themselves are rotated by a random offset each call so which exact
        // hexes fall together isn't identical from one game to the next, on top of the random
        // pick each caller then makes within a bucket.
        private static List<HexCoord>[] BucketByAngleAroundCenter(List<HexCoord> coords, int sectorCount)
        {
            var buckets = new List<HexCoord>[sectorCount];
            for (int i = 0; i < sectorCount; i++)
                buckets[i] = new List<HexCoord>();

            float sectorWidth = 360f / sectorCount;
            float angleOffset = Random.Range(0f, sectorWidth);

            foreach (HexCoord coord in coords)
            {
                float adjusted = (AngleAroundCenterDegrees(coord) - angleOffset + 360f) % 360f;
                int sector = Mathf.Clamp((int)(adjusted / sectorWidth), 0, sectorCount - 1);
                buckets[sector].Add(coord);
            }

            return buckets;
        }

        // Angle (0-360) of a hex's world position around the field's (0,0) centre — outerRadius
        // doesn't affect the angle itself, only the distance, so 1f is fine as a stand-in.
        private static float AngleAroundCenterDegrees(HexCoord coord)
        {
            Vector3 world = HexGridMath.AxialToWorld(coord.Q, coord.R, 1f);
            return (Mathf.Atan2(world.z, world.x) * Mathf.Rad2Deg + 360f) % 360f;
        }

        private List<HexCoord> GetEdgeEligibleHexes()
        {
            List<HexCoord> cityRuinsHexes = GetCityRuinsHexes();
            List<HexCoord> corners = GetFieldCorners();
            var origin = new HexCoord(0, 0);

            var result = new List<HexCoord>();
            foreach (HexCoord coord in map.AllCoords)
            {
                int edgeDistance = map.FieldRadius - HexGridMath.Distance(origin, coord);
                if (edgeDistance > gameConfig.maxEdgeDistance)
                    continue;

                // Off the corner tiles specifically (project owner's own spec, 2026-08-22 —
                // "не прям в угловых участках карты"), and kept at least
                // gameConfig.minCityRuinsDistance hexes from every "City ruins" hex so a
                // fresh citadel never opens the game already standing next to a garrisoned
                // outpost.
                if (corners.Any(corner => HexGridMath.Distance(coord, corner) <= gameConfig.cornerExclusionRadius))
                    continue;
                if (cityRuinsHexes.Any(ruin => HexGridMath.Distance(coord, ruin) < gameConfig.minCityRuinsDistance))
                    continue;

                result.Add(coord);
            }
            return result;
        }

        // The field's own 6 corner hexes (a hexagon-of-hexes has 6, not 4) — anchor points for
        // cornerExclusionRadius above. Every candidate already comes from map.AllCoords (see
        // GetEdgeEligibleHexes), so unlike the old rectangular corners these are always real,
        // on-map hexes too.
        private List<HexCoord> GetFieldCorners()
        {
            var result = new List<HexCoord>();
            int radius = map.FieldRadius;
            foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
                result.Add(new HexCoord(dq * radius, dr * radius));
            return result;
        }

        // Every hex whose terrain is "City ruins" — same name/lookup convention as
        // GenerateCityRuinsGarrisons (CitadelSetupController.MapContent.cs) uses for the
        // post-placement garrison pass. Terrain is already fully painted on `map` by the time
        // this setup step runs, so it's safe to read here even though the garrisons themselves
        // don't spawn until FinishAllPlacements, well after starting hexes are assigned.
        private List<HexCoord> GetCityRuinsHexes()
        {
            var result = new List<HexCoord>();
            foreach (HexCoord hex in map.AllCoords)
            {
                if (map.TryGetTerrainAt(hex, out TerrainTypeEntry terrain) &&
                    string.Equals(terrain.terrainName, CityRuinsTerrainName, System.StringComparison.OrdinalIgnoreCase))
                    result.Add(hex);
            }
            return result;
        }

        // A hex is offered as a citadel spot only if it actually exists on the map — every
        // terrain type is buildable now, including Mountains, since none of them block
        // movement or placement any more, just cost more to move through.
        private bool IsSelectable(HexCoord coord)
        {
            return map.TryGetTerrainAt(coord, out _);
        }

        private void SpawnRegionHighlight(PlayerSetupData player, HexCoord coord)
        {
            if (map == null)
                return;

            // The candidate + its selectable neighbours — near the map's edge (which is
            // exactly where every candidate is, by construction) some neighbours don't
            // actually exist, and any that are Mountains aren't valid citadel spots either.
            // HexClusterGlow.shader traces the true outer boundary of whichever of these end
            // up in the set itself, so there's no boundary geometry to build here anymore.
            var cluster = new List<HexCoord> { coord };
            foreach (HexCoord neighbor in HexGridMath.Neighbors(coord))
                if (IsSelectable(neighbor))
                    cluster.Add(neighbor);

            var highlightObject = new GameObject("RegionHighlight");
            highlightObject.transform.SetParent(transform, false);
            HexClusterHighlight marker = highlightObject.AddComponent<HexClusterHighlight>();
            marker.ApplyStyle(gameConfig.regionHighlightStyle);
            marker.SetColor(PlayerColorPalette.Colors[player.ColorIndex]);
            marker.ShowCluster(cluster, map.OuterRadius);
        }

        // --- Citadel placement (current human player clicks within their neighbourhood) --

        private void BuildValidHexes(HexCoord candidate)
        {
            // The candidate itself is just the random reference point used to scatter
            // players around the map — it's a perfectly valid citadel spot too, same as its
            // 6 neighbours (GetEdgeEligibleHexes already guarantees the candidate itself isn't
            // Mountains, so only the neighbours need the impassable check here).
            _validHexes.Clear();
            if (IsSelectable(candidate))
                _validHexes.Add(candidate);
            foreach (HexCoord neighbor in HexGridMath.Neighbors(candidate))
                if (IsSelectable(neighbor))
                    _validHexes.Add(neighbor);
        }

        private void Update()
        {
            if (!_canPlace)
                return;

            // Space confirms the popup exactly when the Confirm button itself would accept a
            // click — i.e. only after a valid hex has actually been picked.
            if (confirmButton != null && confirmButton.interactable && UIFocusUtility.WasSpacePressed())
            {
                OnConfirmClicked();
                return;
            }

            if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame)
                return;

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            if (targetCamera == null || map == null)
                return;

            Vector2 mousePos = Mouse.current.position.ReadValue();
            Ray ray = targetCamera.ScreenPointToRay(mousePos);

            // The map is flat at Y=0 — a math plane intersection instead of Physics.Raycast
            // against a baked Ground collider, so this can't silently break if that collider's
            // ever missing/disabled (see HexSelectionController.RaycastHex's identical fix).
            if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter))
                return;

            HexCoord clicked = map.WorldToHex(ray.GetPoint(enter));
            if (!_validHexes.Contains(clicked))
                return; // outside this player's selectable neighbourhood — ignore

            _placedHex = clicked;
            if (confirmButton != null)
                confirmButton.interactable = true;
            if (_selectedHighlight != null)
                _selectedHighlight.ShowAt(map.HexToWorld(clicked), map.OuterRadius);
        }

        // --- Confirm ---------------------------------------------------------------------

        private void OnConfirmClicked()
        {
            if (!_placedHex.HasValue)
                return;

            FinalizePlayer(_allPlayers[_currentPlayerIndex], _placedHex.Value);
            AdvanceToNextPlayer();
        }

        private void FinishAllPlacements()
        {
            if (popupCanvas != null)
                Destroy(popupCanvas);

            if (hexSelectionController != null)
                hexSelectionController.enabled = true;

            // Every citadel is placed now — hand manual pan/zoom back to the player.
            if (cameraController != null)
                cameraController.SetPanningEnabled(true);

            CreatePlayerRoots();

            // Post-generation content passes (see CitadelSetupController.MapContent.cs) — run
            // only now, once every citadel hex is finalized, so they can steer clear of them.
            // Order matches the user's own spec: resources, then neutral armies, then City ruins
            // garrisons (must run before events so those hexes register as guaranteed event
            // targets — see GenerateCityRuinsGarrisons's own comment), then random events, then
            // the not-yet-implemented special-hexes hook.
            GenerateResources();
            GenerateNeutralArmies();
            GenerateCityRuinsGarrisons();
            GenerateRandomEvents();
            GenerateSpecialHexes();

            // Fog needs an actual viewer the instant citadel placement ends — otherwise it stays
            // off (VisionSystem.IsVisibleToCurrentViewer fails open with no CurrentViewer) for
            // the whole dice-off phase and however many AI turns come before the human's own
            // first one, which is exactly the gap the project owner flagged: resource hexes and
            // every army sitting fully visible right up until GameTurnController.BeginPlayerTurn
            // eventually reaches a human. GameTurnController's own turn loop still owns
            // CurrentViewer from here on (see its BeginPlayerTurn) — this is only the interim
            // value for the window before that loop's first human turn actually begins.
            PlayerSetupData viewer = GameSession.FindHumanPlayer();
            if (viewer != null)
            {
                VisionSystem.CurrentViewer = viewer;
                VisionSystem.RecomputeFor(viewer);
            }

            if (turnController != null)
                turnController.BeginGame();

            // Removes this controller AND every highlight it spawned as a child in one shot.
            // Citadel markers aren't affected — SpawnCitadelMarker never parented them here,
            // and CreatePlayerRoots just moved them under their own player's root.
            Destroy(gameObject);
        }

        // One container GameObject per active player (everything they own lives under it from
        // here on) plus a neutral one for map objects that belong to no player. Done only now,
        // once every player's citadel is finalised — not earlier, so this stays a single clean
        // step rather than something the placement loop has to thread through.
        private void CreatePlayerRoots()
        {
            foreach (PlayerSetupData player in _allPlayers)
            {
                PlayerRoot root = PlayerRoot.Create(player, $"Player_{player.Nickname}");
                if (_citadelMarkers.TryGetValue(player, out MapObjectVisual citadelMarker))
                {
                    citadelMarker.transform.SetParent(root.transform, worldPositionStays: true);
                    root.SetCitadel(citadelMarker);
                }
                PlayerRootRegistry.Register(player, root);

                // Garrison markers (see CreateGarrison) are created before this point, when no
                // PlayerRoot exists yet for CreateArmyMarker to parent under — same ordering
                // problem the citadel marker above already has to solve. Sweeping every
                // registered army here, rather than special-casing garrisons, means this stays
                // correct for any other army creation site that might ever run this early too.
                foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
                    if (army.Controller != null)
                        army.Controller.transform.SetParent(root.transform, worldPositionStays: true);
            }

            // A real PlayerSetupData (never added to _allPlayers/GameSession.Players, so it
            // never gets a turn slot or a dice-off roll) rather than a bare null Owner — plain
            // null is explicitly rejected by CreateArmyMarker (no marker, no visible presence at
            // all), and most of the codebase already assumes Owner resolves to a real profile
            // (colour, nickname, ...) rather than guarding for null everywhere. See
            // GenerateNeutralArmies (CitadelSetupController.MapContent.cs) for what actually
            // spawns under this owner.
            _neutralPlayer = new PlayerSetupData
            {
                Nickname = "Neutral",
                ColorIndex = PlayerColorPalette.NeutralColorIndex, // dark indigo, reserved — never offered to real players
                Faction = Faction.Neutral,
                IsHuman = false,
                IsNeutral = true,
            };
            PlayerRoot neutralRoot = PlayerRoot.Create(null, "Neutral");
            PlayerRootRegistry.Register(_neutralPlayer, neutralRoot);
        }

    }
}
