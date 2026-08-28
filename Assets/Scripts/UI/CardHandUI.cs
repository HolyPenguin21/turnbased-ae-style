using System.Collections.Generic;
using Game.Ai;
using Game.Aviation;
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
using UnityEngine.InputSystem;
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
        // "Cards" on the first line, the remaining draw-pile count on the second — see
        // RefreshDeckCountText/SetDeckCountText.
        [SerializeField] private TMP_Text deckCountText;
        [SerializeField] private Button scrollLeftButton;
        [SerializeField] private Button scrollRightButton;
        [SerializeField] private GameTurnController turnController;
        [SerializeField] private int drawApCost = 2;
        [SerializeField] private StartingDeckCatalog startingDeckCatalog;
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

        // Exposed so Game.Ai.AiHandRegistry can resolve every AI player's own hand from the same
        // StartingDeckCatalog the human draws from — each player (human or AI) now resolves
        // their own deck from it via their own PlayerSetupData.Faction, rather than everyone
        // sharing one hardcoded catalog+deckIndices pair.
        public StartingDeckCatalog StartingDeckCatalog => startingDeckCatalog;
        public int StartingHandSize => startingHandSize;
        public int DrawApCost => drawApCost;

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

        [SerializeField] private int startingHandSize = 6;
        // Hard cap on cards the player can hold at once — enforced in OnDrawClicked (the only
        // way a card gets added to hand after the starting deal).
        [SerializeField] private int maxHandSize = 10;

        private readonly List<CardUI> _cards = new List<CardUI>();

        // Equipment attach mode (see EquipmentSystem / the project owner's spec): a
        // CardType.Equipment card was right-clicked in hand and is now waiting for the player to
        // left-click a Unit/Hero card to hang it on — either another card in this hand, or a
        // live unit's card in the open Army Viewer. The equipment card stays IN the hand the
        // whole time (this is a pending selection, not a drag). A second right-click, or Esc,
        // cancels; a failed attach shows the reason and also ends the mode, card left in hand.
        private CardData _pendingEquipment;
        public bool IsAttachMode => _pendingEquipment != null;
        // Dev-only mirror of _cards for GameTurnController's debugFollowAiVision toggle (see
        // ShowAiHandDebug) — kept fully separate so swapping the display never touches the
        // human's own real hand/deck state underneath.
        private readonly List<CardUI> _debugCards = new List<CardUI>();
        private bool _showingDebugHand;
        // Reused every PreviewDrag call instead of allocating a fresh List each time —
        // OnDrag can fire many times a frame while a card is held, so this was previously
        // the single biggest source of GC garbage (and the FPS drops that go with it) during
        // a drag.
        private readonly List<CardUI> _scratchVisible = new List<CardUI>();
        private int _scrollOffset;
        // Consumed (RemoveAt), not cycled — every card in the deck is one-time-use for the
        // whole game, so drawing must never hand out the same physical card twice, even though
        // the same CardDefinition can appear several times (several copies of the same card,
        // see StartingDeckCatalog.BuildDeckPool).
        private readonly List<CardDefinition> _remainingDeck = new List<CardDefinition>();
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
        // Screen position at which the last hex raycast was actually fired. OnDrag can tick
        // several times per frame; RaycastHex is a physics raycast against the hex grid, and
        // the hex under the cursor can't have changed unless the cursor itself moved a few
        // pixels — so the raycast is skipped entirely until the pointer has moved past
        // HoverRaycastMoveThreshold from here. -1 means "no raycast fired yet this stretch".
        private Vector2 _lastHoverRaycastScreenPos = new Vector2(-1f, -1f);
        private const float HoverRaycastMoveThreshold = 4f;
        // Resolved once and cached rather than re-looked-up via GameSession.FindHumanRoot()
        // every Update() — same fix as ResourceBarUI's own _humanRoot (see its comment); the
        // human's PlayerRoot never changes once registered. Falls back to re-resolving if still
        // null (Update can run before setup registers it).
        private PlayerRoot _humanRoot;
        // Cached the same way as _humanRoot above, and for the same reason: the human's
        // PlayerSetupData is fixed once the match is running, but UpdateDragHoverValidity used
        // to re-run the FindHumanPlayer() list scan on every hex change during a drag. Lazily
        // (re)resolved via HumanPlayer since Awake can run before the session registers it.
        private PlayerSetupData _humanPlayer;
        private PlayerSetupData HumanPlayer
        {
            get
            {
                if (_humanPlayer == null)
                    _humanPlayer = FindHumanPlayer();
                return _humanPlayer;
            }
        }
        // Guards deckCountText's per-frame text set — the deck count only actually changes on a
        // draw, not every frame, so re-formatting and re-assigning the string when nothing
        // changed was pure waste.
        private int _lastDisplayedDeckCount = -1;

        private void Awake()
        {
            if (drawButton != null)
                drawButton.onClick.AddListener(OnDrawClicked);
            if (scrollLeftButton != null)
                scrollLeftButton.onClick.AddListener(OnScrollLeftClicked);
            if (scrollRightButton != null)
                scrollRightButton.onClick.AddListener(OnScrollRightClicked);

            CreateSlotBackgrounds();

            // Reverse link so ArmyUnitCardUI's own click can route into equipment attach mode
            // (see BeginAttachMode) without every card prefab needing its own hand reference.
            if (armyViewerModal != null)
                armyViewerModal.SetCardHand(this);

            if (startingDeckCatalog != null)
            {
                PlayerSetupData human = FindHumanPlayer();
                Faction faction = human != null ? human.Faction : Faction.IronConcord;
                _remainingDeck.AddRange(startingDeckCatalog.BuildDeckPool(faction));
            }

            for (int i = 0; i < startingHandSize; i++)
            {
                CardDefinition card = PopRandomCard();
                if (card == null)
                    break;
                AddCard(new CardData(card));
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

        // Removes and returns one random card from the remaining deck (null if it's empty) —
        // shared by the starting-hand draw above and OnDrawClicked below, so both pull from the
        // same shrinking pool without replacement.
        private CardDefinition PopRandomCard()
        {
            if (_remainingDeck.Count == 0)
                return null;
            int poolIndex = Random.Range(0, _remainingDeck.Count);
            CardDefinition card = _remainingDeck[poolIndex];
            _remainingDeck.RemoveAt(poolIndex);
            return card;
        }

        // Called once by GameTurnController.BeginGame, right after citadel setup — same
        // trigger as ResourceBarUI/the end-turn button. The panel starts inactive in the
        // scene, so Awake (and so the starting-hand population above) doesn't run until this
        // actually activates it. Also the counterpart to Hide() below — reshown once
        // BattleScreenUI closes. Relayout(animated: false) covers any card added by
        // GrantEventReward/GrantCard while a guard-fight battle screen kept the hand hidden —
        // AddCard's own Relayout call ran with the GameObject inactive, so its
        // StartCoroutine(AnimateTo(...)) (CardUI.Retarget) never actually placed the card; this
        // reactivation is the first point the coroutine can run at all.
        public void Show()
        {
            gameObject.SetActive(true);
            Relayout(animated: false);
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

        // Turn-based game, event-driven UI: the draw button's interactable state depends on
        // whose turn it is (turnController.TurnStateChanged), whether dragging is blocked
        // (turnController.CardDraggingBlockedChanged), and the human's AP
        // (_humanRoot.ResourcesChanged) — all three now fire their own change notification, so
        // there's nothing left that only a per-frame poll could catch.
        private void OnEnable()
        {
            _humanRoot = FindHumanRoot();
            if (_humanRoot != null)
                _humanRoot.ResourcesChanged += RefreshDrawButtonInteractable;
            if (turnController != null)
            {
                turnController.CardDraggingBlockedChanged += OnCardDraggingBlockedChanged;
                turnController.TurnStateChanged += RefreshDrawButtonInteractable;
            }
            RefreshDeckCountText();
            RefreshDrawButtonInteractable();
        }

        private void OnDisable()
        {
            if (_humanRoot != null)
                _humanRoot.ResourcesChanged -= RefreshDrawButtonInteractable;
            if (turnController != null)
            {
                turnController.CardDraggingBlockedChanged -= OnCardDraggingBlockedChanged;
                turnController.TurnStateChanged -= RefreshDrawButtonInteractable;
            }
        }

        private void OnCardDraggingBlockedChanged(bool _) => RefreshDrawButtonInteractable();

        // Esc cancels an in-progress equipment attach (see BeginAttachMode) — but only when the
        // Army Viewer isn't open: that modal owns Esc while it's showing and forwards the
        // attach-cancel itself first (see ArmyViewerModalUI.Update), so this would double-fire.
        // Unity's Input System has no key-pressed event, so this is a genuine per-frame poll.
        private void Update()
        {
            if (_pendingEquipment == null || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
                return;
            if (armyViewerModal != null && armyViewerModal.IsShowing)
                return;
            CancelAttachMode();
        }

        private void RefreshDeckCountText()
        {
            if (deckCountText == null || _remainingDeck.Count == _lastDisplayedDeckCount)
                return;
            SetDeckCountText(_remainingDeck.Count);
        }

        // Unconditional write, unlike RefreshDeckCountText's own no-op-if-unchanged guard —
        // used wherever the count needs to switch to a DIFFERENT player's number (see
        // ShowAiHandDebug/HideAiHandDebug), where the guard can't tell "already correct" apart
        // from "just happens to coincide with the last player's own count".
        private void SetDeckCountText(int count)
        {
            if (deckCountText == null)
                return;
            _lastDisplayedDeckCount = count;
            deckCountText.text = $"Cards\n{count}";
        }

        private void RefreshDrawButtonInteractable()
        {
            if (drawButton == null)
                return;
            if (_humanRoot == null)
                _humanRoot = FindHumanRoot();
            drawButton.interactable = _remainingDeck.Count > 0
                && CanDragCards()
                && _humanRoot != null
                && _humanRoot.CanSpendActionPoints(drawApCost);
        }

        // Thin local aliases for GameSession's own versions — kept so this file's many call
        // sites don't all need touching, now that the lookup itself lives in one shared place.
        private static PlayerSetupData FindHumanPlayer() => GameSession.FindHumanPlayer();

        private static PlayerRoot FindHumanRoot() => GameSession.FindHumanRoot();

        // Dev-only: swaps the visible hand row for `hand`'s own cards (see GameTurnController.
        // debugFollowAiVision) — the real hand's own _cards are only hidden, never touched, so
        // nothing about the human's actual hand/deck state changes. Read-only in practice without
        // needing its own flag: CanDragCards() already requires turnController.CurrentPlayer.
        // IsHuman, which is false for the whole AI turn this is ever shown during, so these cards
        // simply can't be picked up. Capped at MaxVisible — no scroll wired up for the debug view,
        // per the project owner's own "just let me see it" ask, not a full second hand widget.
        public void ShowAiHandDebug(AiHandData hand)
        {
            if (cardPrefab == null || handContainer == null)
                return;

            foreach (CardUI card in _debugCards)
                Destroy(card.gameObject);
            _debugCards.Clear();

            foreach (CardUI card in _cards)
                card.gameObject.SetActive(false);
            _showingDebugHand = true;

            // The deck counter switches to THIS player's own remaining-deck count too —
            // otherwise it stays frozen on whichever player's turn last actually wrote it (the
            // user's own report: the deck count doesn't update, including when the turn passes
            // to a different AI player while this debug view is following it).
            SetDeckCountText(hand?.RemainingDeckCount ?? 0);

            if (hand == null)
                return;

            for (int i = 0; i < hand.Hand.Count && i < MaxVisible; i++)
            {
                CardUI card = Instantiate(cardPrefab, handContainer);
                card.Setup(this, hand.Hand[i], restingScale, hoverScale, hoverLift, animDuration, dragHoverShrink);
                card.SetHome(new Vector2(SlotX(i), 0f), animated: false);
                card.transform.SetAsLastSibling();
                _debugCards.Add(card);
            }
        }

        // Re-lays-out the currently shown debug hand from `hand`'s latest contents — a no-op
        // unless ShowAiHandDebug is already active, so Game.Ai.AiTurnController can call this
        // after every decision step (hand contents can change mid-turn — a draw, a deploy) without
        // needing to know whether debugFollowAiVision is even on.
        public void RefreshAiHandDebugIfShowing(AiHandData hand)
        {
            if (_showingDebugHand)
                ShowAiHandDebug(hand);
        }

        // Reverts to the real hand — called once a human's own turn begins again (see
        // GameTurnController.BeginPlayerTurn).
        public void HideAiHandDebug()
        {
            if (!_showingDebugHand)
                return;
            _showingDebugHand = false;

            foreach (CardUI card in _debugCards)
                Destroy(card.gameObject);
            _debugCards.Clear();

            // Force the counter back to the human's own real count — ShowAiHandDebug may have
            // just overwritten it with an AI player's number.
            SetDeckCountText(_remainingDeck.Count);
            Relayout(animated: false);
        }

        // Single entry point for a card entering the human's hand from OUTSIDE the normal draw —
        // challenge rewards, event rewards (via HexSelectionController.GrantCard), returned
        // aircraft. Enforces the hand cap; returns false (card lost, hint shown) if the hand is
        // already full. Equipment cards reach the hand only through here (temporarily also via a
        // StartingDeck entry for testing — see StartingDeckCatalog).
        public bool AddCardToHand(CardDefinition definition)
        {
            if (definition == null)
                return false;
            return AddCardToHand(new CardData(definition));
        }

        // Same, but for a card instance that already exists and must be preserved as-is rather
        // than rebuilt from its definition — an aircraft returning from an airfield/air army
        // still carries its attached Equipment on the CardData (see AviationActions.
        // ReturnAircraftToDeck). The Equipment's cost was already paid when it was attached, so
        // this is a restore, not a fresh attach — nothing is charged here.
        public bool AddCardToHand(CardData data)
        {
            if (data?.Definition == null)
                return false;
            if (_cards.Count >= maxHandSize)
            {
                turnController?.ShowSpawnHint($"Hand is full ({maxHandSize}) — {data.Definition.displayName} was lost.");
                return false;
            }
            AddCard(data);
            return true;
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

            int newIndex = IndexForDropX(card, currentIndex, localPosition.x);
            if (newIndex != currentIndex)
            {
                _cards.RemoveAt(currentIndex);
                _cards.Insert(Mathf.Clamp(newIndex, 0, _cards.Count), card);
                Relayout(animated: true);
                // Only touches sibling order (a hierarchy change on every card) when the order
                // actually changed — while nothing's changing between ticks, this used to run
                // every single frame for no visible effect.
                RestoreSiblingOrder();
                // Re-assert the dragged card on top, but ONLY here — RestoreSiblingOrder just
                // put it back at its list index. Calling this every OnDrag tick (as it used to)
                // dirtied the parent canvas's sort/batch every single frame; the card is already
                // last-sibling from OnBeginDrag and stays there until the next reorder.
                card.transform.SetAsLastSibling();
            }
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

            // Cursor hasn't moved far enough since the last raycast for the hex under it to
            // have changed — keep the previous validity, skip the physics raycast.
            if (_dragHexKnown
                && (screenPosition - _lastHoverRaycastScreenPos).sqrMagnitude
                   < HoverRaycastMoveThreshold * HoverRaycastMoveThreshold)
                return;
            _lastHoverRaycastScreenPos = screenPosition;

            HexCoord? hex = hexSelection != null ? hexSelection.RaycastHex(screenPosition) : null;
            if (_dragHexKnown && Equals(_lastDragHex, hex))
                return;
            _dragHexKnown = true;
            _lastDragHex = hex;

            CardDefinition definition = card.Data?.Definition;
            PlayerSetupData human = HumanPlayer;
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
            return building != null && building.Owner == player && building.IsBase;
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

            // Aircraft never pass through the ordinary garrison deployment path: it would make
            // a valid card temporarily mix with heroes/ground units and bypass airfield capacity.
            if (definition.isAviation)
            {
                if (!AviationActions.TryDeployFromCard(definition, human, PlayerRootRegistry.FindFor(human), hexSelection,
                        hex.Value, out string aviationFailReason, card.Data?.Equipment))
                {
                    turnController.ShowSpawnHint(aviationFailReason);
                    return false;
                }
                RemoveCard(card);
                return true;
            }

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

            if (!DeployUnit(definition, human, garrison, PlayerRootRegistry.FindFor(human), card.Data?.Equipment))
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

            // Captured heroes "just lie there" (see ArmyData.IsPrison) — a Prison is reachable
            // from the modal's own in-modal button row same as any other army, but it's not
            // somewhere a fresh Unit/Hero card can ever land, per the user's own call.
            if (targetArmy.IsPrison)
                return false;

            if (definition.isAviation)
            {
                if (!targetArmy.IsAirfield)
                {
                    turnController.ShowSpawnHint("Aircraft can only be deployed into an airfield.");
                    return false;
                }
                if (!AviationActions.TryDeployFromCard(definition, human, PlayerRootRegistry.FindFor(human), hexSelection,
                        targetArmy.Hex, out string aviationFailReason, card.Data?.Equipment))
                {
                    turnController.ShowSpawnHint(aviationFailReason);
                    return false;
                }
                armyViewerModal.RefreshAfterExternalDeploy();
                RemoveCard(card);
                return true;
            }

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

            if (!DeployUnit(definition, human, targetArmy, PlayerRootRegistry.FindFor(human), card.Data?.Equipment))
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

        // Shared by both drop paths above — thin wrapper over Game.Map.ArmyActions.
        // DeployUnitFromCard (the same player-agnostic core Game.Ai.AiTurnController calls for
        // an AI player), just turning a failure into this player's own hint popup.
        // attachedEquipment: a CardType.Equipment card hung on this card while it was in hand
        // (see EquipmentSystem) — carried onto the spawned unit by DeployUnitFromCard.
        private bool DeployUnit(CardDefinition definition, PlayerSetupData owner, ArmyData targetArmy, PlayerRoot root,
            CardDefinition attachedEquipment = null)
        {
            if (hexSelection == null || root == null)
                return false;

            if (!ArmyActions.DeployUnitFromCard(definition, owner, targetArmy, root, hexSelection, out string failReason,
                    attachedEquipment))
            {
                if (failReason != null)
                    turnController.ShowSpawnHint(failReason);
                return false;
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

        // Same, addressed by the CardData rather than its CardUI — used by the equipment attach
        // flow, which only holds the pending _pendingEquipment CardData.
        private void RemoveCardData(CardData data)
        {
            CardUI card = _cards.Find(c => c != null && c.Data == data);
            if (card != null)
                RemoveCard(card);
        }

        // --- equipment attach mode (see _pendingEquipment) ---------------------------------

        // Right-clicked a CardType.Equipment card in hand. From here the player left-clicks a
        // Unit/Hero card (in this hand or the open Army Viewer). Only during the human's own
        // confirmed turn, same gate as playing a card.
        public void BeginAttachMode(CardData equipmentCard)
        {
            if (equipmentCard?.Definition == null || equipmentCard.Definition.cardType != CardType.Equipment)
                return;
            if (!CanDragCards())
            {
                turnController?.ShowSpawnHint("You can only attach equipment on your own turn.");
                return;
            }
            _pendingEquipment = equipmentCard;
            turnController?.ShowSpawnHint(
                $"Attaching {equipmentCard.Definition.displayName} — left-click a unit or hero. Right-click or Esc to cancel.");
        }

        public void CancelAttachMode()
        {
            if (_pendingEquipment == null)
                return;
            _pendingEquipment = null;
            turnController?.ShowSpawnHint("Attach cancelled.");
        }

        // Left-clicked a Unit/Hero card still in this hand — attach to it before it's ever
        // deployed (the grant rides along on CardData.Equipment; see ArmyActions.DeployUnitFromCard).
        public void TryAttachToHandCard(CardData targetCard)
        {
            if (_pendingEquipment == null || targetCard == null || targetCard == _pendingEquipment)
                return;
            PlayerRoot root = PlayerRootRegistry.FindFor(FindHumanPlayer());
            if (EquipmentSystem.TryAttach(_pendingEquipment.Definition, targetCard, root, out string reason))
            {
                turnController?.ShowSpawnHint($"{_pendingEquipment.Definition.displayName} attached to {targetCard.Definition.displayName}.");
                _cards.Find(c => c != null && c.Data == targetCard)?.RefreshEquipmentToggle();
                RemoveCardData(_pendingEquipment);
            }
            else
            {
                turnController?.ShowSpawnHint(reason);
            }
            _pendingEquipment = null;
        }

        // Left-clicked a live unit's card in the open Army Viewer (routed via
        // ArmyViewerModalUI.TryConsumeAttachClick). Returns true if a pending attach was
        // handled at all (so the caller can suppress its normal detail-view click).
        public bool TryAttachToUnit(UnitData unit)
        {
            if (_pendingEquipment == null)
                return false;
            PlayerSetupData human = FindHumanPlayer();
            if (unit == null || unit.Owner != human)
            {
                turnController?.ShowSpawnHint("You can only attach equipment to your own units.");
            }
            else if (EquipmentSystem.TryAttach(_pendingEquipment.Definition, unit, PlayerRootRegistry.FindFor(human), out string reason))
            {
                turnController?.ShowSpawnHint($"{_pendingEquipment.Definition.displayName} attached to {unit.Name}.");
                RemoveCardData(_pendingEquipment);
            }
            else
            {
                turnController?.ShowSpawnHint(reason);
            }
            _pendingEquipment = null;
            return true;
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

        // Maps a drop x-position to an absolute index into _cards, the same way
        // ArmyViewerModalUI.ResolveGridSlotIndex maps a drop position to a grid cell: divide the
        // distance from the first slot's left edge by the slot pitch and floor it, instead of
        // comparing dropX against each neighbour's slot centre in turn. The old centre-comparison
        // approach effectively used a single shared threshold near the hand's own pivot (x=0)
        // rather than each neighbour's actual position, so a neighbour could give way well before
        // (or only well after) the dragged card actually reached it. Flooring from the slot's left
        // edge instead makes a neighbour give way right as the dragged card crosses into its slot,
        // symmetrically on both sides — dragged card excluded via _scratchVisible, and only visible
        // cards are considered since the dragged card is always visible while held.
        //
        // Hysteresis (IndexHysteresis): the card keeps its current slot until dropX moves more
        // than that fraction of a slot PAST the boundary into the next one — and won't come back
        // until it crosses the same margin the other way. Without this dead band, a cursor
        // resting right on a slot boundary flip-flops the index every few pixels (and every time
        // the neighbours' 0.12s shuffle nudges things), which read as the cards twitching.
        private const float IndexHysteresis = 0.35f;

        private int IndexForDropX(CardUI dragged, int currentIndex, float dropX)
        {
            _scratchVisible.Clear();
            foreach (CardUI c in _cards)
                if (c != dragged && c.gameObject.activeSelf)
                    _scratchVisible.Add(c);

            float step = cardSize.x * (1f - overlapFraction);
            if (step <= 0f)
                return _scrollOffset + _scratchVisible.Count;

            float totalWidth = MaxVisible > 0 ? (MaxVisible - 1) * step : 0f;
            float slot0LeftEdge = -totalWidth * 0.5f - step * 0.5f;
            float raw = (dropX - slot0LeftEdge) / step;

            // Slot the card currently sits in, in the same visible-slot space as `raw` (raw is
            // in [k, k+1) while the card is over visible slot k). Stay put unless raw has left
            // [cur - H, cur + 1 + H); only then re-floor to wherever it actually landed.
            int cur = Mathf.Clamp(currentIndex - _scrollOffset, 0, _scratchVisible.Count);
            int slot = (raw >= cur + 1f + IndexHysteresis || raw < cur - IndexHysteresis)
                ? Mathf.FloorToInt(raw)
                : cur;
            return _scrollOffset + Mathf.Clamp(slot, 0, _scratchVisible.Count);
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
            if (_remainingDeck.Count == 0 || !CanDragCards())
                return;
            if (_cards.Count >= maxHandSize)
            {
                turnController.ShowSpawnHint($"Hand is full ({maxHandSize} cards) — play or discard before drawing.");
                return;
            }
            PlayerRoot root = FindHumanRoot();
            if (root == null || !root.CanSpendActionPoints(drawApCost))
                return;

            CardDefinition card = PopRandomCard();
            if (card == null)
                return;

            RefreshDeckCountText();
            root.SpendActionPoints(drawApCost);
            AddCard(new CardData(card));
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
