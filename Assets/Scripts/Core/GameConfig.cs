using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Styles;
using Game.Terrain;
using Game.UI;
using UnityEngine;

namespace Game.Core
{
    // Single place to configure shared prefabs and gameplay constants instead of scattering
    // them as [SerializeField] across multiple controllers. One asset, referenced wherever
    // needed — works from both scenes (MainMenu/Game) without DontDestroyOnLoad, unlike a
    // scene GameObject would. Camera feel (RtsCameraController) deliberately stays out of
    // this — it's tuning for one specific camera rig, not a shared/gameplay setting.
    [CreateAssetMenu(fileName = "GameConfig", menuName = "Game/Game Config")]
    public class GameConfig : ScriptableObject
    {
        [Header("Map Interaction")]
        // Shared by every controller that raycasts against the map's ground collider.
        public LayerMask groundLayerMask = ~0;

        [Header("Map Objects")]
        // Two variants of the same coloured-circle-plus-icon visual: buildings and units sit
        // at different spots on the same hex (and can overlap a little — that's fine), so they
        // need their own prefab each rather than sharing one MapObject.
        public MapObjectVisual buildingMarkerPrefab;
        // A hex only ever shows one visible marker per owner, standing in for that owner's
        // whole presence there — one army marker at a time (see HexSelectionController.
        // RestackArmiesOn), never one per individual unit; a unit has no map presence of its
        // own at all.
        public MapObjectVisual armyMarkerPrefab;
        // Where each sits within its hex, in hex-radius units (x = left/right, y = the world Z
        // axis, same convention as the resource row). These three fields are only ever used
        // when a hex has MORE than one occupant — a lone occupant (building, army, or whatever
        // else eventually shares a hex) always just sits centred instead. See HexObjectLayout
        // for the actual per-hex resolution logic; these are just its raw tunables.
        // Building+army sharing a hex: building sits bottom-left so it doesn't overlap the
        // resource row above; the army sits bottom-right, mirrored, and is allowed to overlap
        // the building icon a little.
        public Vector2 buildingIconOffset = new Vector2(-0.25f, -0.25f);
        public Vector2 armyIconOffset = new Vector2(0.25f, -0.25f);
        // Two armies of DIFFERENT owners sharing a hex with no building: mirrored left/right of
        // the hex centre, vertically centred (y normally 0). Same-owner armies sharing a hex
        // instead stack visually — not designed yet, see HexObjectLayout's fallback.
        public Vector2 twoArmySideOffset = new Vector2(0.25f, 0f);

        // Every scene element sits flat at Y=0 (the ground quad is the one exception, see
        // MapGenerationSettings — it's pushed slightly below) — draw order between everything
        // else is Renderer.sortingOrder, not height. See also HexHighlightStyle.sortingOrder
        // (hex highlights: 1-2) and MoveArrowStyle's own sortingOrder fields (5-8).
        [Header("Map Object Sorting (order in layer)")]
        public int mapSortingOrder = 0;
        public int buildingCircleSortingOrder = 3;
        public int buildingIconSortingOrder = 4;
        public int armyCircleSortingOrder = 9;
        public int armyIconSortingOrder = 10;

        [Header("Army")]
        // The only icon actually shown on the map — a unit is purely an army member, with no
        // independent map presence of its own, so the marker always represents an owner's whole
        // army (see HexSelectionController.RestackArmiesOn). Global for now (one faction);
        // becomes per-faction once more than one exists.
        public Sprite armyIconSprite;

        [Header("Army Viewer")]
        // Instantiated by two different controllers (ArmyButtonRowUI — both the hex-side row
        // and the one embedded in ArmyViewerModalUI — and ArmyViewerModalUI's own unit grid),
        // so this lives here rather than as a duplicated [SerializeField] on each, same
        // reasoning as playerRowPrefab/resourceIconPrefab below.
        public ArmyButtonUI armyButtonPrefab;
        public ArmyUnitCardUI armyUnitCardPrefab;

        [Header("Citadel")]
        public Sprite citadelIconSprite;
        public ResourceYields citadelResourceBonus = new ResourceYields
        {
            human = 1,
            energy = 1,
            materials = 1,
            tech = 1,
        };

        [Header("Base Building")]
        // Instantiated by BaseViewerModalUI for its facility-slot grid — same reasoning as
        // armyUnitCardPrefab above (shared prefab reference, not a duplicated per-component
        // [SerializeField]).
        public BaseSlotCardUI baseSlotCardPrefab;
        // The auto-placed starting citadel has no CardDefinition to source stats from (unlike a
        // player-built Base, which reads its own card's structurePointsMax/defense/resistance)
        // — these are its baseline instead. See CitadelSetupController.SpawnCitadelMarker.
        public int startingStructurePoints = 6;
        public int startingDefense = 2;
        public int startingResistance = 1;
        public int startingFate = 1;
        // Exactly 3 tiers this pass — Level 1->2->3->4, each unlocking one more Facility slot
        // (see BuildingData.UnlockedFacilitySlots). Costs from the user's own spec.
        public BaseUpgradeTier[] baseUpgradeTiers =
        {
            new BaseUpgradeTier { apCost = 2, cost = new ResourceCost { human = 1, energy = 1, materials = 1, tech = 1 } },
            new BaseUpgradeTier { apCost = 4, cost = new ResourceCost { human = 2, energy = 2, materials = 2, tech = 2 } },
            new BaseUpgradeTier { apCost = 6, cost = new ResourceCost { human = 4, energy = 4, materials = 4, tech = 4 } },
        };

        [Header("Resource Extraction")]
        // Indexed by ResourceType (Human/Energy/Materials/Tech) — the 4 always-available,
        // never-in-hand Facility cards a hero builds directly via HexInfoPanelUI's resource
        // action buttons (see HexSelectionController.TryBuildExtractionFacility). Embedded here
        // directly rather than living in a FactionCardCatalog — CardDefinition is a plain
        // [System.Serializable] record, not its own asset, so there'd be nothing for a catalog
        // entry to be referenced BY; CardHandUI/deckIndices never see these at all, so they can
        // never be drawn into a hand.
        public CardDefinition[] extractionFacilityCards = new CardDefinition[4];
        // Paid upgrade tiers for an already-placed Collect-tagged Facility (see
        // BaseViewerModalUI.ImproveFacility) — a separate, cheaper ladder from baseUpgradeTiers
        // since this only raises one Facility's own yield contribution, not a whole new slot.
        public BaseUpgradeTier[] facilityUpgradeTiers =
        {
            new BaseUpgradeTier { apCost = 2, cost = new ResourceCost { human = 1, energy = 1, materials = 1, tech = 1 } },
            new BaseUpgradeTier { apCost = 4, cost = new ResourceCost { human = 2, energy = 2, materials = 2, tech = 2 } },
        };
        // Instantiated by ResourceActionRowUI for HexInfoPanelUI's up-to-4 "build extractor"
        // buttons — same shared-prefab reasoning as armyButtonPrefab above.
        public ResourceActionButtonUI resourceActionButtonPrefab;
        // The marker for a hero-built resource site (see HexSelectionController.
        // TryBuildExtractionFacility) — a distinct prefab from buildingMarkerPrefab, not just a
        // different icon swapped in at runtime, since its icon is baked directly onto its own
        // Object_Image sprite rather than set via MapObjectVisual.SetIcon.
        public MapObjectVisual facilityMarkerPrefab;

        [Header("Ability Abbreviations")]
        // Short display form for each ability tag (see BuildingAbilities) — used wherever a
        // card/building/unit's abilities are listed in a description area (CardUI, ArmyUnitCardUI,
        // BaseSlotCardUI, and the two viewer modals' own detail panels — see FormatAbilities).
        // An ability not listed here just falls back to showing its raw tag name.
        public AbilityAbbreviation[] abilityAbbreviations = new AbilityAbbreviation[0];

        // Descriptions for FormatAbilitiesDetailed come from here rather than being duplicated
        // onto AbilityAbbreviation — this asset is already the populated reference for every
        // ability tag (see UnitAbilityCatalog.knownAbilities).
        public UnitAbilityCatalog abilityCatalog;

        // Falls back to the raw tag if it has no entry above.
        public string GetAbilityAbbreviation(string ability)
        {
            if (abilityAbbreviations != null)
                foreach (AbilityAbbreviation entry in abilityAbbreviations)
                    if (entry != null && entry.ability == ability && !string.IsNullOrEmpty(entry.abbreviation))
                        return entry.abbreviation;
            return ability;
        }

        // Shared by every place that lists a card/building/unit's abilities, so they can't drift
        // on how that's formatted — space-separated abbreviations (see GetAbilityAbbreviation),
        // empty string for a null/empty set rather than each call site checking separately.
        public string FormatAbilities(IEnumerable<string> abilities)
        {
            if (abilities == null)
                return string.Empty;
            var parts = new List<string>();
            foreach (string ability in abilities)
                parts.Add(GetAbilityAbbreviation(ability));
            return string.Join(" ", parts);
        }

        // Falls back to the raw tag if it has no entry above (same fallback rule as
        // GetAbilityAbbreviation).
        public string GetAbilityFullName(string ability)
        {
            if (abilityAbbreviations != null)
                foreach (AbilityAbbreviation entry in abilityAbbreviations)
                    if (entry != null && entry.ability == ability && !string.IsNullOrEmpty(entry.fullName))
                        return entry.fullName;
            return ability;
        }

        // Null if abilityCatalog isn't assigned, or the tag has no entry (or an empty
        // description) there — callers skip the "- description" line entirely rather than
        // showing a blank one (see FormatAbilitiesDetailed).
        public string GetAbilityDescription(string ability)
        {
            return abilityCatalog != null ? abilityCatalog.GetDescription(ability) : null;
        }

        // The detail-panel counterpart to FormatAbilities: full name, then "- description" on
        // its own line — used only where a card/unit/building is shown in a dedicated detail
        // view (ArmyViewerModalUI.ShowUnitDetail, BaseViewerModalUI, BattleScreenUI.Grid's
        // detail panel), never on the compact card itself (that stays abbreviation-only, see
        // FormatAbilities).
        public string FormatAbilitiesDetailed(IEnumerable<string> abilities)
        {
            if (abilities == null)
                return string.Empty;
            var parts = new List<string>();
            foreach (string ability in abilities)
            {
                string fullName = GetAbilityFullName(ability);
                string description = GetAbilityDescription(ability);
                parts.Add(description == null ? fullName : $"{fullName}\n- {description}");
            }
            return string.Join("\n", parts);
        }

        [Header("Citadel Setup Step")]
        // How far from the map edge a player's random starting hex can land.
        public int maxEdgeDistance = 1;

        [Header("Hex Highlight Styles")]
        // Noise/glow tunables for HexShaderHighlight/HexClusterHighlight (see
        // HexHighlightStyle) — one per usage context, so each can look different instead of
        // every instance sharing the shader files' own Properties-block defaults. Colour isn't
        // part of the style: the region highlight uses each player's own colour, the other two
        // use fixed TechnicalColors, both set separately at the call site.
        // The neighbourhood cluster shown while picking a citadel spot (CitadelSetupController.SpawnRegionHighlight).
        public HexHighlightStyle regionHighlightStyle = new HexHighlightStyle();
        // The confirmed single-hex citadel pick, drawn above the region cluster via sortingOrder.
        public HexHighlightStyle citadelSelectionStyle = new HexHighlightStyle { sortingOrder = 2 };
        // In-game hex selection (HexSelectionController).
        public HexHighlightStyle hexSelectionStyle = new HexHighlightStyle();
        // The currently-acting unit's cell in the Tactical Battle Module grid (UIRaggedGlowUI,
        // Custom/UIRaggedGlow.shader — see BattleGridCellUI/BattleScreenUI). Same style class as
        // the three above even though this one drives a UI shader, not a world-space one — the
        // knobs mean the same thing either way, just in UI-rect units instead of world (hex)
        // units, so the defaults here are scaled up accordingly (a margin of 0.6 world units
        // would be sub-pixel on an ~80x100 UI cell). Colour is TechnicalColors.BattleActingUnit,
        // set at the call site same as the three above.
        public HexHighlightStyle battleActingUnitHighlightStyle = new HexHighlightStyle
        {
            margin = 6f,
            lineThickness = 2f,
            noiseReach = 4f,
            noiseScale = 0.15f,
            noiseSpeed = 1.2f,
            glowIntensity = 0.8f,
            glowWidth = 6f,
        };

        [Header("Resources")]
        public ResourceIconVisual resourceIconPrefab;
        // Horizontal gap between icons in the row, and how far above the hex centre the row
        // sits — both in hex-radius units. If the row shows up below the hex instead of above
        // it, flip the sign of Resource Row Offset (world "up" on screen depends on the
        // camera, no code change needed).
        public float resourceIconSpacing = 0.4f;
        public float resourceRowOffset = 0.6f;

        [Header("Map Generation")]
        public MapGenerationSettings mapGeneration = new MapGenerationSettings();

        [Header("Player Setup")]
        public int minPlayers = 2;
        public int maxPlayers = 4;
        public PlayerRowUI playerRowPrefab;

        [Header("Initiative Dice")]
        // Cost of one bonus initiative die, in units of whichever single resource the player
        // spends it from (see PlayerRoot.BuyInitiativeDie) — same price regardless of which
        // resource is chosen.
        public int initiativeDicePrice = 3;

        [Header("Move Arrow")]
        // Comet-trail move-order preview (see MoveArrowMarker / MoveArrowStyle).
        public MoveArrowStyle moveArrowStyle = new MoveArrowStyle();
        // Which of these two the preview/order arrow uses depends on whether the path (as
        // truncated by Game.Combat.BattleInitiator) ends on a plain hex or on a combat-capable
        // enemy army — no longer the mover's own player colour, so red could be freed up for
        // this and reserved out of PlayerColorPalette (see there).
        public Color moveArrowMoveColor = new Color(0.15f, 0.75f, 0.25f);
        public Color moveArrowAttackColor = new Color(0.80f, 0.15f, 0.15f);

        [Header("Clouds")]
        // Drifting cloud-shadow overlay for the strategic map (see Game.Map.MapCloudOverlay /
        // Custom/CloudDrift.shader) — one shared style, same pattern as every other style block
        // above, so the look is tuned here rather than as loose fields on the overlay component.
        public CloudStyle cloudStyle = new CloudStyle();
    }
}
