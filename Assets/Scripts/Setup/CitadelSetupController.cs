using System.Collections.Generic;
using System.Linq;
using Game.Cameras;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Styles;
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
        // prefab/icon/resource bonus all live on the shared GameConfig asset — see GameConfig.
        [SerializeField] private GameConfig gameConfig;

        [Header("Cards")]
        // Only used to look up "Concord Citadel"'s own card art for the auto-placed starting
        // citadel's BuildingData.Art — same single catalog CardHandUI/ArmyViewerModalUI already
        // reference directly (see those for why there's no per-faction lookup mechanism yet).
        [SerializeField] private FactionCardCatalog catalog;

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
            ArmyRegistry.Clear();
            PlayerRootRegistry.Clear();
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
                _selectedHighlight.ApplyStyle(gameConfig.citadelSelectionStyle);
                _selectedHighlight.SetColor(TechnicalColors.CitadelSelection);
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

        // Not parented under this controller (which FinishAllPlacements destroys) or even
        // under the map yet — it ends up under the player's own PlayerRoot once that's
        // created, at the end of the whole step (see CreatePlayerRoots).
        private void SpawnCitadelMarker(PlayerSetupData player, HexCoord hex, Vector2 offset2D)
        {
            if (gameConfig == null || gameConfig.buildingMarkerPrefab == null || map == null)
                return;

            MapObjectVisual marker = Instantiate(gameConfig.buildingMarkerPrefab);
            Vector3 offset = new Vector3(offset2D.x, 0f, offset2D.y) * map.OuterRadius;
            marker.transform.position = map.HexToWorld(hex) + offset;
            marker.SetColor(PlayerColorPalette.Colors[player.ColorIndex]);
            marker.SetIcon(gameConfig.citadelIconSprite);
            marker.SetSortingOrder(gameConfig.buildingCircleSortingOrder, gameConfig.buildingIconSortingOrder);
            _citadelMarkers[player] = marker;

            CardDefinition citadelCard = catalog != null ? catalog.ForType(CardType.Base).FirstOrDefault() : null;

            var building = new BuildingData
            {
                Name = "Citadel", Hex = hex, Owner = player, Visual = marker,
                Art = citadelCard != null ? citadelCard.art : null,
                Level = 1,
                StructurePointsMax = gameConfig.startingStructurePoints,
                StructurePointsCurrent = gameConfig.startingStructurePoints,
                Defense = gameConfig.startingDefense,
                Resistance = gameConfig.startingResistance,
                Fate = gameConfig.startingFate,
                ResourceYield = gameConfig.citadelResourceBonus,
                // The one and only difference from a "Concord Citadel" card played later (see
                // HexSelectionController.SpawnBuilding) — same card, same abilities below, but
                // only THIS building's destruction ends the game for this player (see
                // BuildingRegistry.BuildingDestroyed).
                IsStartingCitadel = true,
            };
            building.Abilities.Add(BuildingAbilities.Base);
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

        private void AssignStartingHexes(List<PlayerSetupData> players)
        {
            _startHexes.Clear();

            List<HexCoord> eligible = GetEdgeEligibleHexes();
            Shuffle(eligible);

            var assigned = new List<HexCoord>();
            foreach (PlayerSetupData player in players)
            {
                if (eligible.Count == 0)
                    break;

                // Greedy farthest-point pick: the eligible hex whose distance to its nearest
                // already-assigned neighbour is largest — spreads players out instead of
                // clumping them even though each candidate is individually random.
                HexCoord best = eligible[0];
                int bestScore = -1;
                foreach (HexCoord candidate in eligible)
                {
                    int score = assigned.Count == 0 ? 0 : MinDistanceTo(candidate, assigned);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }

                assigned.Add(best);
                eligible.Remove(best);
                _startHexes[player] = best;

                SpawnRegionHighlight(player, best);
            }
        }

        private List<HexCoord> GetEdgeEligibleHexes()
        {
            var result = new List<HexCoord>();
            for (int row = 0; row < map.Height; row++)
            {
                for (int col = 0; col < map.Width; col++)
                {
                    int edgeDistance = Mathf.Min(Mathf.Min(col, map.Width - 1 - col), Mathf.Min(row, map.Height - 1 - row));
                    HexCoord coord = HexCoord.FromOffset(col, row);
                    // A candidate must actually exist on the map — otherwise its selectable
                    // neighbourhood would have a hole in the middle, which the boundary tracer
                    // below can't draw as a single closed outline.
                    if (edgeDistance <= gameConfig.maxEdgeDistance && IsSelectable(coord))
                        result.Add(coord);
                }
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

        private static int MinDistanceTo(HexCoord candidate, List<HexCoord> others)
        {
            int min = int.MaxValue;
            foreach (HexCoord other in others)
                min = Mathf.Min(min, HexGridMath.Distance(candidate, other));
            return min;
        }

        private static void Shuffle(List<HexCoord> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
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

            if (!Physics.Raycast(ray, out RaycastHit hit, 1000f, gameConfig.groundLayerMask))
                return;

            HexCoord clicked = map.WorldToHex(hit.point);
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

            CreatePlayerRoots();
            SpawnTestEnemyArmies();

            // Post-generation content passes (see CitadelSetupController.MapContent.cs) — run
            // only now, once every citadel hex is finalized, so they can steer clear of them.
            // Order matches the user's own spec: resources, then neutral armies, then the two
            // not-yet-implemented hooks for random events/special hexes.
            GenerateResources();
            GenerateNeutralArmies();
            GenerateRandomEvents();
            GenerateSpecialHexes();

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
                Faction = Faction.None,
                IsHuman = false,
            };
            PlayerRoot neutralRoot = PlayerRoot.Create(null, "Neutral");
            PlayerRootRegistry.Register(_neutralPlayer, neutralRoot);
        }

        // --- Temporary test scaffolding for the new "Initiating Battle" trigger -----------
        // Two enemy armies spawned 2 hexes from the human player's citadel, owned by the first
        // AI player, so the trigger (see Game.Combat.BattleInitiator) can be exercised by just
        // moving into them — no real second economy/army needed for that. Remove once real
        // combat resolution and/or AI exists and this isn't needed as a manual test fixture.

        private void SpawnTestEnemyArmies()
        {
            if (hexSelectionController == null || catalog == null || map == null)
                return;

            PlayerSetupData human = _allPlayers.Find(p => p != null && p.IsHuman);
            PlayerSetupData enemyOwner = _allPlayers.Find(p => p != null && !p.IsHuman);
            if (human == null || enemyOwner == null || !human.CitadelHexQ.HasValue || !human.CitadelHexR.HasValue)
                return;

            var citadelHex = new HexCoord(human.CitadelHexQ.Value, human.CitadelHexR.Value);

            var testHexes = new List<HexCoord>();
            foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
            {
                var candidate = new HexCoord(citadelHex.Q + dq * 2, citadelHex.R + dr * 2);
                if (map.TryGetTerrainAt(candidate, out _))
                    testHexes.Add(candidate);
                if (testHexes.Count == 2)
                    break;
            }
            if (testHexes.Count < 2)
                return; // map too small / citadel too close to the edge — skip rather than crash

            SpawnTestArmy(testHexes[0], enemyOwner, "Vanguard",
                "Dorian Kesh", "Light Infantry", "Light Infantry", "Light Infantry", "Medium Tank");
            // No hero — exactly 2 units, right at the hero-less hard cap (see ArmyData.
            // ComputeCapacity), so this specifically exercises a heroless-army battle.
            SpawnTestArmy(testHexes[1], enemyOwner, "Reserve",
                "Medium Infantry", "Light Tank");
            // A second army sharing testHexes[1] with Reserve — testHexes[0] stays a
            // single-army hex, this one a two-army hex, so both cases are exercisable (e.g. the
            // battle popup's own side army list — see BattleContactPopupUI).
            SpawnTestArmy(testHexes[1], enemyOwner, "Outpost Guard",
                "Light Infantry", "Light Infantry");

            // A lone hero, no units at all — exercises the Capture Kill Challenge / Escaped-
            // retreat path (see BattleScreenUI.Combat.cs's HandleCaptureKillOutcome) directly,
            // without first grinding a whole army down to just its hero in a real fight. Not
            // Dorian Kesh — he's already leading Vanguard above.
            HexCoord? loneHeroHex = FindTestHex(citadelHex, distance: 3, avoid: testHexes);
            if (loneHeroHex.HasValue)
                SpawnTestArmy(loneHeroHex.Value, enemyOwner, "Lone Rider", "Aldric Voss");
        }

        // Same search as the two-hex loop above (testHexes), just at an arbitrary distance and
        // skipping anything already claimed — factored out once a second, independent test hex
        // was needed.
        private HexCoord? FindTestHex(HexCoord citadelHex, int distance, List<HexCoord> avoid)
        {
            foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
            {
                var candidate = new HexCoord(citadelHex.Q + dq * distance, citadelHex.R + dr * distance);
                if (map.TryGetTerrainAt(candidate, out _) && (avoid == null || !avoid.Contains(candidate)))
                    return candidate;
            }
            return null;
        }

        private void SpawnTestArmy(HexCoord hex, PlayerSetupData owner, string name, params string[] cardNames)
        {
            var army = new ArmyData { Name = name, Hex = hex, Owner = owner };
            ArmyRegistry.Register(army);

            foreach (string cardName in cardNames)
            {
                CardDefinition definition = catalog.cards.Find(c => c != null && c.displayName == cardName);
                if (definition == null)
                    continue;
                bool isHero = definition.cardType == CardType.Hero;
                UnitData spawned = hexSelectionController.SpawnUnit(definition.displayName, owner,
                    definition.moveMax, definition.activationApCost, isHero, definition.commandRating, definition.art,
                    definition.grantedAbilities, definition.attack, definition.range, definition.hitPoints, definition.initiative, definition.fate,
                    definition.defenseRating, definition.resistanceRating);
                if (spawned != null)
                    army.AddMemberSorted(spawned);
            }
            // The army's marker is created only now, after every member's already in — its
            // very first RestackArmiesOn (inside CreateArmyMarker) needs to see a non-empty
            // army to have anything to show at all (see HexSelectionController.
            // NonEmptyArmiesAt).
            hexSelectionController.CreateArmyMarker(army);
        }
    }
}
