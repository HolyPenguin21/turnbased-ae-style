using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.Units;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // The player's hand: a row of overlapping CardUI at the bottom of the screen, plus a
    // "draw" button. Cards sit at restingScale until hovered (grows to full size + lifts —
    // see CardUI), and can be dragged with LMB — the rest of the hand reorders live as it's
    // dragged (see PreviewDrag), not just on release. Hidden until the game actually starts
    // (see Show, called from GameTurnController.BeginGame — same trigger as the resource bar
    // and end-turn button) and hover/scroll always work, but dragging a card only works on the
    // human's own turn — see CardUI.OnBeginDrag's use of CanDragCards.
    //
    // Only MaxVisible cards are shown at once — if the player holds more than that, the
    // extras are scrolled in/out with scrollLeftButton/scrollRightButton rather than shrinking
    // every card to fit. _cards holds EVERY card the player has, in hand order; _scrollOffset
    // is the index of the first one currently shown.
    public class CardHandUI : MonoBehaviour
    {
        [SerializeField] private RectTransform handContainer;
        [SerializeField] private CardUI cardPrefab;
        [SerializeField] private Button drawButton;
        // "Cards" on the first line, the remaining draw-pile count on the second — see Update.
        [SerializeField] private TMP_Text deckCountText;
        [SerializeField] private Button scrollLeftButton;
        [SerializeField] private Button scrollRightButton;
        [SerializeField] private GameTurnController turnController;
        [SerializeField] private int drawApCost = 2;
        [SerializeField] private FactionCardCatalog catalog;
        // Used to figure out which hex (if any) a card was dropped on, and to actually spawn
        // the unit there — see TryPlayCard.
        [SerializeField] private HexSelectionController hexSelection;
        // A Unit/Hero card dropped onto the open Army Viewer deploys straight into whichever
        // army it's currently showing, instead of going through the hex/garrison flow — see
        // TryDeployIntoArmyModal.
        [SerializeField] private ArmyViewerModalUI armyViewerModal;
        // Same idea for a Facility card dropped onto the open Base Viewer — see
        // TryDeployIntoBaseModal.
        [SerializeField] private BaseViewerModalUI baseViewerModal;
        // Only used for GameConfig.FormatAbilities — see CardUI.Setup, which reads this via the
        // Hand reference it already keeps rather than needing its own separate config field.
        [SerializeField] private GameConfig gameConfig;

        public GameConfig GameConfig => gameConfig;

        [Header("Layout")]
        [SerializeField] private Vector2 cardSize = new Vector2(96f, 140f);
        // Purely cosmetic — the slot background rects (see CreateSlotBackgrounds) can be sized
        // independently of the actual cards/slot spacing (still driven by cardSize below), so
        // this can be tuned without touching any layout math.
        [SerializeField] private Vector2 slotVisualSize = new Vector2(85f, 120f);
        [Range(0f, 0.3f)]
        [SerializeField] private float overlapFraction = 0.08f;
        [SerializeField] private float restingScale = 0.9f;
        [SerializeField] private float hoverScale = 1.2f;
        [SerializeField] private float hoverLift = 20f;
        [SerializeField] private float animDuration = 0.12f;
        // How much smaller a dragged card gets while poised over a hex it could actually be
        // dropped on — a fraction of its full carried size, e.g. 0.5 means it shrinks to 50%.
        // See CardUI.SetDragHoverValid.
        [SerializeField] [Range(0f, 0.9f)] private float dragHoverShrink = 0.5f;
        // Not exposed as a setting — purely a rendering-window size, not a hand rule (see
        // maxHandSize below for the actual gameplay cap), so it stays a fixed constant rather
        // than something that needs tuning per game.
        private const int MaxVisible = 7;
        // How far (in HandContainer-local pixels) a dragged card can be lifted above/below the
        // hand row and still trigger live reordering — beyond this, it reads as "being taken
        // somewhere else" (e.g. toward the map to play it) and the rest of the hand stops
        // reacting to it. See PreviewDrag.
        [SerializeField] private float dragBandBuffer = 50f;

        // The whole deck for this game: indices into catalog.cards, duplicates allowed (e.g.
        // several Light Infantry entries for several copies) — seeds _remainingDeck on Awake,
        // which both the starting hand and drawButton draw random cards from without
        // replacement (see PopRandomDeckIndex).
        [SerializeField] private List<int> deckIndices = new List<int>();
        [SerializeField] private int startingHandSize = 6;
        // Hard cap on cards the player can hold at once — enforced in OnDrawClicked (the only
        // way a card gets added to hand after the starting deal).
        [SerializeField] private int maxHandSize = 10;

        private readonly List<CardUI> _cards = new List<CardUI>();
        // Reused every PreviewDrag call instead of allocating a fresh List each time —
        // OnDrag can fire many times a frame while a card is held, so this was previously
        // the single biggest source of GC garbage (and the FPS drops that go with it) during
        // a drag.
        private readonly List<CardUI> _scratchVisible = new List<CardUI>();
        private int _scrollOffset;
        // Consumed (RemoveAt), not cycled — every card in the deck is one-time-use for the
        // whole game, so drawing must never hand out the same physical card twice, even though
        // duplicate catalog indices (several copies of the same card) are expected.
        private readonly List<int> _remainingDeck = new List<int>();
        // Tracks whether the card currently being dragged is still within the hand row's drag
        // band as of the last PreviewDrag call — set there, read in FinishDrop to decide
        // "reorder within the hand" (still in band) vs. "attempt to play it onto the map"
        // (dragged clear of the hand). Only one card can be dragged at a time.
        private bool _dragWithinBand = true;
        // Which hex (if any) was under the cursor as of the last drag-hover validity check, and
        // whether that check has run at all yet this "outside the band" stretch — read/updated
        // in UpdateDragHoverValidity so the (relatively expensive) BuildingRegistry lookup only
        // runs once per hex actually changed under the cursor, not every frame.
        private HexCoord? _lastDragHex;
        private bool _dragHexKnown;

        private void Awake()
        {
            if (drawButton != null)
                drawButton.onClick.AddListener(OnDrawClicked);
            if (scrollLeftButton != null)
                scrollLeftButton.onClick.AddListener(OnScrollLeftClicked);
            if (scrollRightButton != null)
                scrollRightButton.onClick.AddListener(OnScrollRightClicked);

            CreateSlotBackgrounds();

            _remainingDeck.AddRange(deckIndices);

            if (catalog != null)
                for (int i = 0; i < startingHandSize; i++)
                {
                    int index = PopRandomDeckIndex();
                    if (index < 0)
                        break;
                    if (index < catalog.cards.Count)
                        AddCard(new CardData(catalog.cards[index]));
                }
        }

        // Faint translucent boxes, one per MaxVisible slot, permanently in place behind
        // wherever cards currently sit (same "a droppable spot is here" look as
        // ArmyUnitCardUI's own empty-capacity placeholders) — makes it obvious the hand has
        // fixed slots to land in rather than a row that just centres on however many cards
        // happen to be held. Created once, never destroyed/rebuilt — unlike _cards, these don't
        // correspond to any particular card and never need to change.
        private static readonly Color SlotBackgroundColor = new Color(1f, 1f, 1f, 0.12f);

        private void CreateSlotBackgrounds()
        {
            if (handContainer == null)
                return;

            for (int i = 0; i < MaxVisible; i++)
            {
                var go = new GameObject($"Slot{i}", typeof(RectTransform), typeof(Image));
                var rt = (RectTransform)go.transform;
                rt.SetParent(handContainer, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = slotVisualSize;
                rt.anchoredPosition = new Vector2(SlotX(i), 0f);
                // Inserted at the front, in order — every card is added to handContainer AFTER
                // these (see AddCard), so later siblings (the actual cards) always render on
                // top without needing any extra sorting-order bookkeeping here.
                rt.SetSiblingIndex(i);

                var image = go.GetComponent<Image>();
                image.color = SlotBackgroundColor;
                image.raycastTarget = false;
            }
        }

        // Removes and returns one random catalog index from the remaining deck (-1 if it's
        // empty) — shared by the starting-hand draw above and OnDrawClicked below, so both
        // pull from the same shrinking pool without replacement.
        private int PopRandomDeckIndex()
        {
            if (_remainingDeck.Count == 0)
                return -1;
            int poolIndex = Random.Range(0, _remainingDeck.Count);
            int catalogIndex = _remainingDeck[poolIndex];
            _remainingDeck.RemoveAt(poolIndex);
            return catalogIndex;
        }

        // Called once by GameTurnController.BeginGame, right after citadel setup — same
        // trigger as ResourceBarUI/the end-turn button. The panel starts inactive in the
        // scene, so Awake (and so the starting-hand population above) doesn't run until this
        // actually activates it. Also the counterpart to Hide() below — reshown once
        // BattleScreenUI closes.
        public void Show()
        {
            gameObject.SetActive(true);
        }

        // Called by BattleScreenUI while a battle is open — the hand means nothing behind the
        // battle screen (no play-a-card-onto-the-map interaction makes sense mid-combat) and
        // would otherwise visually crowd/overlap it.
        public void Hide()
        {
            gameObject.SetActive(false);
        }

        // Hover and hand-scrolling always work regardless of whose turn it is — only actually
        // picking a card up (CardUI.OnBeginDrag) is gated on this. Deliberately checks
        // CardDraggingBlocked, not the broader InputBlocked — the Army Viewer being open must
        // NOT stop this, since dragging a card onto it is exactly how it deploys straight into
        // that army (see TryPlayCard/TryDeployIntoArmyModal).
        public bool CanDragCards()
        {
            return turnController != null
                && turnController.CurrentPlayer != null
                && turnController.CurrentPlayer.IsHuman
                && turnController.TurnConfirmed
                && !turnController.CardDraggingBlocked;
        }

        // Keeps the draw button's interactable state in sync with whose turn it is and how
        // much AP the human has — both can change from several different places (turn
        // transitions, buying initiative dice, spending AP elsewhere), so this just polls
        // every frame rather than trying to hook an event into all of them (same reasoning as
        // ResourceBarUI's resource polling).
        private void Update()
        {
            if (deckCountText != null)
                deckCountText.text = $"Cards\n{_remainingDeck.Count}";

            if (drawButton == null)
                return;
            PlayerRoot root = FindHumanRoot();
            drawButton.interactable = _remainingDeck.Count > 0
                && CanDragCards()
                && root != null
                && root.CanSpendActionPoints(drawApCost);
        }

        private static PlayerSetupData FindHumanPlayer()
        {
            return GameSession.Players?.Find(p => p != null && p.IsHuman);
        }

        private static PlayerRoot FindHumanRoot()
        {
            PlayerSetupData human = FindHumanPlayer();
            return human != null ? PlayerRootRegistry.FindFor(human) : null;
        }

        public void AddCard(CardData data)
        {
            if (cardPrefab == null || handContainer == null)
                return;

            CardUI card = Instantiate(cardPrefab, handContainer);
            card.Setup(this, data, restingScale, hoverScale, hoverLift, animDuration, dragHoverShrink);
            _cards.Add(card);
            // Scroll so the newly added card is actually visible, not silently added past the
            // edge of the window.
            _scrollOffset = Mathf.Max(0, _cards.Count - MaxVisible);
            Relayout(animated: true);
            RestoreSiblingOrder();
        }

        // Called by a CardUI every time it moves while dragged — reorders the hand live so the
        // rest of the cards make room for it immediately, instead of waiting for the drop.
        // Only does anything while localPosition.y is still within dragBandBuffer of the hand
        // row — once the card is lifted well clear of it (e.g. carried toward the map), the
        // rest of the hand stops reacting, and this returns early before touching the list or
        // running IndexForDropX at all. Once clear of the band, screenPosition instead drives
        // the drop-target shrink preview (see UpdateDragHoverValidity) — reordering and the
        // map-hover preview are mutually exclusive, matching "still in the hand" vs. "being
        // carried toward the map".
        public void PreviewDrag(CardUI card, Vector2 localPosition, Vector2 screenPosition)
        {
            _dragWithinBand = IsWithinDragBand(localPosition.y);
            if (!_dragWithinBand)
            {
                UpdateDragHoverValidity(card, screenPosition);
                return;
            }

            // Back within the band (e.g. carried out toward the map and back) — any hex-hover
            // shrink no longer applies, and the next time it leaves the band this should check
            // fresh rather than trusting a hex it hasn't actually looked under since.
            _dragHexKnown = false;
            card.SetDragHoverValid(false);

            int currentIndex = _cards.IndexOf(card);
            if (currentIndex < 0)
                return;

            int newIndex = IndexForDropX(card, localPosition.x);
            if (newIndex != currentIndex)
            {
                _cards.RemoveAt(currentIndex);
                _cards.Insert(Mathf.Clamp(newIndex, 0, _cards.Count), card);
                Relayout(animated: true);
                // Only touches sibling order (a hierarchy change on every card) when the order
                // actually changed — while nothing's changing between ticks, this used to run
                // every single frame for no visible effect.
                RestoreSiblingOrder();
            }
            card.transform.SetAsLastSibling(); // dragged card always renders above the rest
        }

        // Re-checks "could this card actually be dropped on the hex under the cursor right
        // now" only when that hex has actually changed since the last check — the
        // BuildingRegistry/ownership/ability lookup is cheap, but there's no reason to redo it
        // every single frame while the cursor sits still over the same hex.
        private void UpdateDragHoverValidity(CardUI card, Vector2 screenPosition)
        {
            // Either modal being open takes over the drop entirely (see TryPlayCard) — hex
            // hover-checking below doesn't apply while one is up. Unlike a hex, hovering a modal
            // never shrinks the card — that shrink is a hex-drop-specific affordance, not a
            // general "valid target" indicator.
            if ((armyViewerModal != null && armyViewerModal.IsShowing) || (baseViewerModal != null && baseViewerModal.IsShowing))
            {
                card.SetDragHoverValid(false);
                return;
            }

            HexCoord? hex = hexSelection != null ? hexSelection.RaycastHex(screenPosition) : null;
            if (_dragHexKnown && Equals(_lastDragHex, hex))
                return;
            _dragHexKnown = true;
            _lastDragHex = hex;

            CardDefinition definition = card.Data?.Definition;
            PlayerSetupData human = FindHumanPlayer();
            bool valid = hex.HasValue && human != null
                && (IsValidDropTarget(definition, human, hex.Value)
                    || IsValidBaseDropTarget(definition, human, hex.Value)
                    || IsValidFacilityHexDropTarget(definition, human, hex.Value));
            card.SetDragHoverValid(valid);
        }

        private static bool IsUnitOrHero(CardDefinition definition)
        {
            return definition != null && (definition.cardType == CardType.Unit || definition.cardType == CardType.Hero);
        }

        // Shared by the live drag-hover preview above and the actual drop attempt in
        // TryDeployUnitOrHero, so the two can never disagree about what counts as a valid target.
        private static bool IsValidDropTarget(CardDefinition definition, PlayerSetupData player, HexCoord hex)
        {
            if (!IsUnitOrHero(definition))
                return false;
            if (string.IsNullOrEmpty(definition.requiredBuildingAbility))
                return false;

            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.Owner == player && building.HasAbility(definition.requiredBuildingAbility);
        }

        // Same idea, for a Base card — the target hex must already have one of the player's own
        // armies with a Hero present (see TryBuildBase) — founding a base needs a Hero on-site,
        // not just empty ground. The hex itself must be either bare or already host a hero-built
        // resource site with few enough occupied Facility slots to fit inside a fresh Base (see
        // CanMergeIntoResourceSite) — anything else (a citadel, another player's Base) blocks it.
        // No ownership/territory restriction beyond that, since no borders/zone-of-control
        // system exists in the game yet. A visible enemy army on the hex blocks it too, same as
        // extraction facilities (see RefreshResourceActionRow) — building requires an
        // uncontested hex.
        private static bool IsValidBaseDropTarget(CardDefinition definition, PlayerSetupData player, HexCoord hex)
        {
            if (definition == null || definition.cardType != CardType.Base)
                return false;
            if (!HexSelectionController.HasOwnHeroArmyAt(hex, player))
                return false;
            if (BattleInitiator.FindEnemyAt(hex, player) != null)
                return false;
            BuildingData existing = BuildingRegistry.FindAt(hex);
            return existing == null || CanMergeIntoResourceSite(existing);
        }

        // A dragged Base card can land on a hex that already has a hero-built resource site (see
        // HexSelectionController.TryBuildExtractionFacility) as long as the new Base's own slot
        // capacity can fit every extraction Facility already built there (see TryBuildBase,
        // which carries them over) — capacity, not currently-unlocked count, so pre-existing
        // facilities are grandfathered in even before the fresh Base's Level has "earned" that
        // many slots.
        private static bool CanMergeIntoResourceSite(BuildingData existing)
        {
            // Identified by HasTieredUnlock=false rather than a separate ability tag — a
            // hero-built resource site is the only kind of building that's ever false here (see
            // HexSelectionController.TryBuildExtractionFacility).
            if (existing.HasTieredUnlock)
                return false;
            int occupied = 0;
            foreach (FacilityData facility in existing.FacilitySlots)
                if (facility != null)
                    occupied++;
            return occupied <= BuildingData.DefaultTotalFacilitySlots;
        }

        // A Facility card dropped straight onto a hex (not the open modal) — valid wherever the
        // player owns a Base-tagged building, regardless of whether it currently has a free
        // slot (that's checked, with its own hint, in TryDeployFacilityToHex — this is only the
        // "is this even the right kind of hex" check shared with the hover preview).
        private static bool IsValidFacilityHexDropTarget(CardDefinition definition, PlayerSetupData player, HexCoord hex)
        {
            if (definition == null || definition.cardType != CardType.Facility)
                return false;
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.Owner == player && building.HasAbility(BuildingAbilities.Base);
        }

        private bool IsWithinDragBand(float localY)
        {
            float maxY = cardSize.y * 0.5f + hoverLift + dragBandBuffer;
            float minY = -(cardSize.y * 0.5f + dragBandBuffer);
            return localY >= minY && localY <= maxY;
        }

        // Called once the drag ends. If the card was still within the hand's drag band, this
        // is just a reorder — the hand's order already matches what PreviewDrag showed, so this
        // only lets the card itself settle into that slot (it stops being IsDragging so
        // Relayout's SetHome call actually retargets it now). Otherwise (dragged clear of the
        // hand row) it's an attempt to play the card onto the map at screenPosition — see
        // TryPlayCard. On success the card is gone (removed from the hand) and there's nothing
        // left to snap back; on failure (wrong/no hex, can't afford it) it falls through to the
        // same snap-back as a reorder.
        public void FinishDrop(CardUI card, Vector2 screenPosition)
        {
            _dragHexKnown = false;
            _lastDragHex = null;

            if (!_dragWithinBand && TryPlayCard(card, screenPosition))
                return;

            Relayout(animated: false);
            // Relayout's SetHome call on `card` is very likely a no-op (see SnapToHome) since
            // its home was already silently tracked to this same slot while it was being
            // dragged — force the actual visual snap regardless.
            card.SnapToHome();
            RestoreSiblingOrder();
        }

        // Dispatches by CardType — each has its own valid-target rule and destination (a
        // garrison, a brand-new building, or an open modal's grid). Anything else (there is
        // nothing else right now) falls through to the normal snap-back-to-hand behaviour.
        private bool TryPlayCard(CardUI card, Vector2 screenPosition)
        {
            CardDefinition definition = card.Data?.Definition;
            if (definition == null || turnController == null)
                return false;

            switch (definition.cardType)
            {
                case CardType.Unit:
                case CardType.Hero:
                    return TryDeployUnitOrHero(card, definition, screenPosition);
                case CardType.Base:
                    return TryBuildBase(card, definition, screenPosition);
                case CardType.Facility:
                    // Dropped onto the open Base Viewer takes over the whole flow (exact slot
                    // under the cursor) — otherwise, dropped straight onto a hex with the
                    // player's own Base, it lands in the first free slot (see
                    // TryDeployFacilityToHex).
                    if (baseViewerModal != null && baseViewerModal.IsShowing)
                        return TryDeployIntoBaseModal(card, definition, screenPosition);
                    return TryDeployFacilityToHex(card, definition, screenPosition);
                default:
                    return false;
            }
        }

        private bool TryDeployUnitOrHero(CardUI card, CardDefinition definition, Vector2 screenPosition)
        {
            // Dropped onto the open Army Viewer takes over the whole flow — see
            // TryDeployIntoArmyModal.
            if (armyViewerModal != null && armyViewerModal.IsShowing)
                return TryDeployIntoArmyModal(card, definition, screenPosition);

            if (hexSelection == null)
                return false;

            HexCoord? hex = hexSelection.RaycastHex(screenPosition);
            if (!hex.HasValue)
                return false; // dropped somewhere with no hex under it — treat as a cancelled drag

            PlayerSetupData human = FindHumanPlayer();
            if (human == null)
                return false;

            if (!IsValidDropTarget(definition, human, hex.Value))
            {
                turnController.ShowSpawnHint($"Can't deploy {definition.displayName} here — needs a building with {definition.requiredBuildingAbility}.");
                return false;
            }

            // Deployed cards land in the player's garrison first (see ArmyData.IsGarrison) —
            // guaranteed to exist here since IsValidDropTarget already required a Barracks-abled
            // building the player owns, and every such building gets a garrison the moment it's
            // placed (see CitadelSetupController.CreateGarrison). Capacity is a hard cap (see
            // ArmyData.Capacity) — a full garrison blocks the deploy, same popup as the
            // no-Barracks case, checked before any AP/resources are spent.
            ArmyData garrison = ArmyRegistry.FindGarrisonAt(hex.Value, human);
            if (garrison == null || !garrison.HasRoom)
            {
                turnController.ShowSpawnHint($"Garrison here is full — can't deploy {definition.displayName}.");
                return false;
            }

            if (!DeployUnit(definition, human, garrison, PlayerRootRegistry.FindFor(human)))
                return false;

            RemoveCard(card);
            return true;
        }

        // A Base card builds a brand-new building — the target hex must either be bare or
        // already host a hero-built resource site with few enough Facilities to fit inside the
        // new Base (see IsValidBaseDropTarget/CanMergeIntoResourceSite), AND already have one of
        // the player's own armies with a Hero present — founding a base needs a Hero on-site,
        // not just empty ground. Checked as separate steps so the hint always names the actual
        // reason instead of a generic "invalid" message.
        private bool TryBuildBase(CardUI card, CardDefinition definition, Vector2 screenPosition)
        {
            if (hexSelection == null)
                return false;

            HexCoord? hex = hexSelection.RaycastHex(screenPosition);
            if (!hex.HasValue)
                return false;

            PlayerSetupData human = FindHumanPlayer();
            if (human == null)
                return false;

            BuildingData existing = BuildingRegistry.FindAt(hex.Value);
            if (existing != null && !CanMergeIntoResourceSite(existing))
            {
                turnController.ShowSpawnHint($"Can't build {definition.displayName} here — this hex already has a building.");
                return false;
            }
            if (!HexSelectionController.HasOwnHeroArmyAt(hex.Value, human))
            {
                turnController.ShowSpawnHint($"Can't build {definition.displayName} here — needs one of your armies with a Hero on this hex.");
                return false;
            }
            if (BattleInitiator.FindEnemyAt(hex.Value, human) != null)
            {
                turnController.ShowSpawnHint($"Can't build {definition.displayName} here — an enemy army holds this hex.");
                return false;
            }

            PlayerRoot root = PlayerRootRegistry.FindFor(human);
            if (root == null)
                return false;

            if (!root.CanSpendActionPoints(definition.apCost))
            {
                turnController.ShowSpawnHint($"Not enough action points to build {definition.displayName}.");
                return false;
            }
            if (!definition.resourceCost.CanAfford(root))
            {
                turnController.ShowSpawnHint($"Not enough resources to build {definition.displayName}.");
                return false;
            }

            root.SpendActionPoints(definition.apCost);
            definition.resourceCost.PayFrom(root);

            // Absorb whatever was already built on a bare resource site into the new Base's own
            // slots (see CanMergeIntoResourceSite) before its old marker is replaced.
            FacilityData[] carriedOver = existing?.FacilitySlots;
            if (existing != null && existing.Visual != null)
                Destroy(existing.Visual.gameObject);

            BuildingData building = hexSelection.SpawnBuilding(definition, hex.Value, human);
            if (building != null && carriedOver != null)
            {
                int slot = 0;
                foreach (FacilityData facility in carriedOver)
                {
                    if (facility == null)
                        continue;
                    while (slot < building.FacilitySlots.Length && building.FacilitySlots[slot] != null)
                        slot++;
                    if (slot >= building.FacilitySlots.Length)
                        break;
                    building.FacilitySlots[slot] = facility;
                    slot++;
                }
            }

            RemoveCard(card);
            return true;
        }

        // Dropped onto the open Army Viewer instead of a hex — deploys straight into whichever
        // army it's currently showing (garrison or a named one). An army can move away from the
        // Barracks that created it, so its current hex still needs the same check the hex-drop
        // path applies (see IsValidDropTarget) — deploy must stay consistent regardless of which
        // path is used to reach it.
        private bool TryDeployIntoArmyModal(CardUI card, CardDefinition definition, Vector2 screenPosition)
        {
            if (!armyViewerModal.ContainsScreenPoint(screenPosition))
                return false;

            ArmyData targetArmy = armyViewerModal.CurrentArmy;
            PlayerSetupData human = FindHumanPlayer();
            if (targetArmy == null || human == null || targetArmy.Owner != human)
                return false;

            if (!IsValidDropTarget(definition, human, targetArmy.Hex))
            {
                turnController.ShowSpawnHint($"Can't deploy {definition.displayName} here — {targetArmy.Name} needs to be on a building with {definition.requiredBuildingAbility}.");
                return false;
            }

            if (!targetArmy.HasRoom)
            {
                turnController.ShowSpawnHint($"{targetArmy.Name} is full — can't deploy {definition.displayName}.");
                return false;
            }

            if (!DeployUnit(definition, human, targetArmy, PlayerRootRegistry.FindFor(human)))
                return false;

            armyViewerModal.RefreshAfterExternalDeploy();
            RemoveCard(card);
            return true;
        }

        // Dropped onto the open Base Viewer instead of a hex — deploys straight into whichever
        // unlocked+empty Facility slot it lands on (see BaseViewerModalUI.TryPlaceFacility).
        // AP/resources are only spent once that call actually succeeds — a locked or
        // already-filled slot costs nothing, same as every other invalid-drop case.
        private bool TryDeployIntoBaseModal(CardUI card, CardDefinition definition, Vector2 screenPosition)
        {
            if (!baseViewerModal.ContainsScreenPoint(screenPosition))
                return false;

            BuildingData targetBuilding = baseViewerModal.CurrentBuilding;
            PlayerSetupData human = FindHumanPlayer();
            if (targetBuilding == null || human == null || targetBuilding.Owner != human)
                return false;

            PlayerRoot root = PlayerRootRegistry.FindFor(human);
            if (root == null)
                return false;

            if (!root.CanSpendActionPoints(definition.apCost))
            {
                turnController.ShowSpawnHint($"Not enough action points to deploy {definition.displayName}.");
                return false;
            }
            if (!definition.resourceCost.CanAfford(root))
            {
                turnController.ShowSpawnHint($"Not enough resources to deploy {definition.displayName}.");
                return false;
            }

            if (!baseViewerModal.TryPlaceFacility(definition, screenPosition))
                return false;

            root.SpendActionPoints(definition.apCost);
            definition.resourceCost.PayFrom(root);
            RemoveCard(card);
            return true;
        }

        // Dropped straight onto a hex carrying the player's own Base — no need to open the
        // modal first. Lands in the first free (unlocked + empty) Facility slot; a full or
        // fully-locked Base, or unaffordable AP/resources, each get their own specific hint
        // rather than a generic failure, same layered pattern as every other deploy path here.
        private bool TryDeployFacilityToHex(CardUI card, CardDefinition definition, Vector2 screenPosition)
        {
            if (hexSelection == null)
                return false;

            HexCoord? hex = hexSelection.RaycastHex(screenPosition);
            if (!hex.HasValue)
                return false; // dropped somewhere with no hex under it — treat as a cancelled drag

            PlayerSetupData human = FindHumanPlayer();
            if (human == null)
                return false;

            if (!IsValidFacilityHexDropTarget(definition, human, hex.Value))
                return false; // not the player's own Base — nothing to say, same as any other irrelevant drop

            BuildingData building = BuildingRegistry.FindAt(hex.Value);
            int slotIndex = building.FindFirstAvailableFacilitySlot();
            if (slotIndex < 0)
            {
                turnController.ShowSpawnHint($"{building.Name} has no free Facility slot for {definition.displayName}.");
                return false;
            }

            PlayerRoot root = PlayerRootRegistry.FindFor(human);
            if (root == null)
                return false;

            if (!root.CanSpendActionPoints(definition.apCost))
            {
                turnController.ShowSpawnHint($"Not enough action points to deploy {definition.displayName}.");
                return false;
            }
            if (!definition.resourceCost.CanAfford(root))
            {
                turnController.ShowSpawnHint($"Not enough resources to deploy {definition.displayName}.");
                return false;
            }

            root.SpendActionPoints(definition.apCost);
            definition.resourceCost.PayFrom(root);
            building.FacilitySlots[slotIndex] = FacilityData.FromDefinition(definition);

            RemoveCard(card);
            return true;
        }

        // Shared by both drop paths above: affordability check, spend, spawn the actual unit,
        // and add it to whichever army is receiving it — always at that army's own hex, which
        // is also the only hex it could ever have been dropped to (the hex-drop path only
        // ever reaches here via that same army's hex, and the modal-drop path deploys straight
        // into the army it's currently showing).
        private bool DeployUnit(CardDefinition definition, PlayerSetupData owner, ArmyData targetArmy, PlayerRoot root)
        {
            if (hexSelection == null || root == null)
                return false;

            if (!root.CanSpendActionPoints(definition.apCost))
            {
                turnController.ShowSpawnHint($"Not enough action points to deploy {definition.displayName}.");
                return false;
            }
            if (!definition.resourceCost.CanAfford(root))
            {
                turnController.ShowSpawnHint($"Not enough resources to deploy {definition.displayName}.");
                return false;
            }

            root.SpendActionPoints(definition.apCost);
            definition.resourceCost.PayFrom(root);
            bool isHero = definition.cardType == CardType.Hero;
            UnitData spawned = hexSelection.SpawnUnit(definition.displayName, owner, definition.moveMax, definition.activationApCost, isHero, definition.commandRating, definition.art, definition.grantedAbilities, definition.attack, definition.range, definition.hitPoints, definition.initiative, definition.fate, definition.defenseRating, definition.resistanceRating);
            if (spawned != null)
            {
                targetArmy.AddMemberSorted(spawned);
                // The unit has no map presence of its own (see Game.Map.ArmyController) — only
                // targetArmy's own marker does, and this may be its first member ever (e.g. a
                // garrison that had zero units until now), so its visibility needs refreshing.
                hexSelection.RestackArmiesOn(targetArmy.Hex, null);
            }
            return true;
        }

        // Removes a played card from the hand entirely — unlike a failed drop, this one never
        // comes back.
        private void RemoveCard(CardUI card)
        {
            _cards.Remove(card);
            Destroy(card.gameObject);
            Relayout(animated: true);
            RestoreSiblingOrder();
        }

        // Sibling order = hand order = left-to-right stacking (later siblings render on top of
        // earlier ones) — called after any reorder, and after a hover ends, to undo the
        // temporary SetAsLastSibling() a hovered/dragged card gets so it isn't hidden behind
        // its neighbour.
        public void RestoreSiblingOrder()
        {
            foreach (CardUI card in _cards)
                card.transform.SetAsLastSibling();
        }

        // Maps a drop x-position to an absolute index into _cards, by comparing against the
        // slot positions of the currently VISIBLE cards only (dragged card excluded) — the
        // dragged card is always visible while held, so this never needs to reason about
        // cards currently scrolled out of view. Called from PreviewDrag, which can fire many
        // times a frame while a card is held, so this reuses _scratchVisible instead of
        // allocating a new list every call.
        private int IndexForDropX(CardUI dragged, float dropX)
        {
            _scratchVisible.Clear();
            foreach (CardUI c in _cards)
                if (c != dragged && c.gameObject.activeSelf)
                    _scratchVisible.Add(c);

            for (int i = 0; i < _scratchVisible.Count; i++)
            {
                if (dropX < SlotX(i))
                    return _scrollOffset + i;
            }
            return _scrollOffset + _scratchVisible.Count;
        }

        private void Relayout(bool animated)
        {
            ClampScroll();

            for (int i = 0; i < _cards.Count; i++)
            {
                int visibleIndex = i - _scrollOffset;
                bool isVisible = visibleIndex >= 0 && visibleIndex < MaxVisible;
                if (_cards[i].gameObject.activeSelf != isVisible)
                    _cards[i].gameObject.SetActive(isVisible);
                if (isVisible)
                    _cards[i].SetHome(new Vector2(SlotX(visibleIndex), 0f), animated);
            }
            UpdateScrollButtons();
        }

        private void ClampScroll()
        {
            int maxOffset = Mathf.Max(0, _cards.Count - MaxVisible);
            _scrollOffset = Mathf.Clamp(_scrollOffset, 0, maxOffset);
        }

        private void UpdateScrollButtons()
        {
            if (scrollLeftButton != null)
                scrollLeftButton.interactable = _scrollOffset > 0;
            if (scrollRightButton != null)
                scrollRightButton.interactable = _scrollOffset + MaxVisible < _cards.Count;
        }

        // Fixed slot positions — every slot index always sits at the same X regardless of how
        // many cards are actually in hand (that's the whole change from the old behaviour: the
        // group used to re-centre, and so shift every existing card, on every add/remove/scroll
        // window change). The MaxVisible-slot block as a WHOLE is still centred on the
        // container's own pivot (x=0), just never on a per-card-count basis any more.
        private float SlotX(int index)
        {
            float step = cardSize.x * (1f - overlapFraction);
            float totalWidth = MaxVisible > 0 ? (MaxVisible - 1) * step : 0f;
            return -totalWidth * 0.5f + index * step;
        }

        private void OnDrawClicked()
        {
            if (catalog == null || _remainingDeck.Count == 0 || !CanDragCards())
                return;
            if (_cards.Count >= maxHandSize)
            {
                turnController.ShowSpawnHint($"Hand is full ({maxHandSize} cards) — play or discard before drawing.");
                return;
            }
            PlayerRoot root = FindHumanRoot();
            if (root == null || !root.CanSpendActionPoints(drawApCost))
                return;

            int index = PopRandomDeckIndex();
            if (index < 0 || index >= catalog.cards.Count)
                return;

            root.SpendActionPoints(drawApCost);
            AddCard(new CardData(catalog.cards[index]));
        }

        private void OnScrollLeftClicked()
        {
            _scrollOffset--;
            Relayout(animated: true);
        }

        private void OnScrollRightClicked()
        {
            _scrollOffset++;
            Relayout(animated: true);
        }
    }
}
