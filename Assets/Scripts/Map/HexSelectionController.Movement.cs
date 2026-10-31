using System.Collections.Generic;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using Game.Styles;
using Game.Terrain;
using Game.Turns;
using Game.UI;
using Game.Units;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Game.Map
{
    // IssueMoveOrder's own precise outcome (2026-08-24, project owner's own report: AiTurnController.
    // MoveArmyRoutine used to have no way to tell WHY an order produced no movement — "no path, no
    // movement left, or a fight blocked the way" was a guess covering five genuinely different
    // guard clauses inside IssueMoveOrder below). Started means the order was accepted and the
    // move coroutine is under way — NOT that the army necessarily reaches `destination`; a partial
    // stop (out of shared move points, vision-revealed contact, etc.) is still Started, and is
    // never itself an error (see MoveArmyRoutine's own Hex-changed check for that outcome).
    // Every other value means IssueMoveOrder rejected the order outright, before the army ever
    // took a single step — one value per guard clause, in the same order they're checked.
    public enum MoveOrderResult
    {
        Started,
        AlreadyMoving,
        CannotMove,
        LockedInCombat,
        NoMovementLeft,
        AlreadyAtDestination,
        NoPath,
        InsufficientStepMovement,
        NoOwnerRoot,
        InsufficientActionPoints,
    }

    // TryBeginBattleAt's own outcome (2026-08-24 P0 fix, project owner's own report: a contact
    // left unresolved by onComplete below — e.g. DelayedBattleRegistry.IsHexPending was true for
    // an unrelated pairing at the same hex — used to just sit there "coexisting" until
    // GameTurnController's own end-of-ROUND sweep eventually forced it, which could be several
    // player-turns away; meanwhile AiTurnController.MoveArmyRoutine's own IsBattleActive wait saw
    // nothing showing and moved straight on to the army's next decision, even though the hex
    // still had a real, un-fought enemy on it). Named so a caller can tell "a battle actually
    // started here" apart from every reason it didn't, instead of a bare bool.
    public enum BattleStartResult
    {
        Started,
        NoContact,
        Pending,
        MoverCannotFight,
    }

    // Move preview (hover) and move order (right-click) half of HexSelectionController — split
    // out purely for file size, same reasoning as HexSelectionController.Factory.cs's own
    // comment. Shares _selectedArmy/_pathArrow/_lastPreviewedHover and the layout helpers
    // (ResolveArmyOffset, RestackArmiesOn, etc.) with the main file, which owns them since every
    // part of this class uses them, not just this one.
    public partial class HexSelectionController
    {
        private void UpdateMovePreview(HexCoord? hoverCoord)
        {
            ArmyData army = GetSelectedArmy();
            // Preview another order only after both the domain operation and its animation
            // settle. The indexed position already follows each legal step while moving.
            if (_selectedArmy == null || _selectedArmy.IsMoving || army == null || army.IsGarrison || army.IsMovementBusy
                || !hoverCoord.HasValue || hoverCoord.Value.Equals(army.Hex))
            {
                if (_lastPreviewedHover.HasValue)
                {
                    _lastPreviewedHover = null;
                    HidePathPreview();
                }
                return;
            }

            if (_lastPreviewedHover.HasValue && _lastPreviewedHover.Value.Equals(hoverCoord.Value)
                && _lastPreviewedPathingVersion == map.PathingVersion)
                return; // same hex and terrain revision — nothing changed, don't re-run pathfinding

            _lastPreviewedHover = hoverCoord;
            _lastPreviewedPathingVersion = map.PathingVersion;

            // flatCost for an air army — an aircraft pays a flat 1 MP per hex regardless of
            // terrain (see AviationRules.MovementCost, what ArmyMovementOperation actually
            // charges), so the route search must rank routes by hex count too. Without it the
            // ground-weighted search hands back a longer detour AROUND expensive terrain
            // (mountains) that the aircraft would never actually fly, and ShowPathArrow then draws
            // that bent route (2026-09-03 fix, project owner's own report — the displayed cost
            // number was already corrected via PathMoveCost, but not the route geometry).
            HexPath path = HexPathfinder.FindPath(map, army.Hex, hoverCoord.Value, AvoidEnemyHex(army),
                flatCost: AviationRules.IsAirArmy(army));
            if (path == null)
            {
                HidePathPreview();
                return;
            }

            // Out of shared move points, or (for an army not yet activated this turn) the owner
            // can't afford the AP that move would cost to activate — either way this order can't
            // actually be issued this turn, so don't draw a route implying it can (mirrors
            // IssueMoveOrder's own AP check further down, just applied to the preview instead of
            // the actual order).
            PlayerRoot ownerRoot = PlayerRootRegistry.FindFor(army.Owner);
            bool canAffordActivation = ArmyActions.CanAffordActivation(army, ownerRoot);
            if (army.CurrentMovement <= 0 || !canAffordActivation)
            {
                if (_pathArrow != null)
                    _pathArrow.Hide();
            }
            else
            {
                // The preview arrow is cosmetic — a bug in its mesh/geometry code must never
                // take down hex selection or move orders with it. Update() runs this before
                // handling clicks, so an uncaught exception here would otherwise skip the rest
                // of that frame's input handling entirely.
                try
                {
                    ShowPathArrow(path, army);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                    if (_pathArrow != null)
                        _pathArrow.Hide();
                }
            }
        }

        private void ShowPathArrow(HexPath path, ArmyData army)
        {
            if (_pathArrow == null)
            {
                var arrowObject = new GameObject("MoveArrow");
                arrowObject.transform.SetParent(transform, false);
                _pathArrow = arrowObject.AddComponent<MoveArrowMarker>();
                // AddComponent runs Awake() synchronously, before there's any chance to hand
                // over gameConfig — Initialize is the actual place MoveArrowMarker pulls its
                // tunables (MoveArrowStyle) from.
                _pathArrow.Initialize(gameConfig);
            }

            // No longer the mover's own player colour — technical green for a plain move,
            // technical red for a path that ends in enemy contact (see Game.Combat.
            // BattleInitiator) — same truncation TryIssueMoveOrder itself will apply, so the
            // preview always shows exactly where the army will actually stop.
            Color color;
            if (AviationRules.IsAirArmy(army))
            {
                // An air army's own attack (AA reaction/air strike, see
                // Game.Aviation.AviationCombatPresenter) never resolves via "move onto/next to an
                // enemy" the way ground contact does (see HandleVisionStep's own comment: ground
                // contact never halts an air army mid-flight), so TruncateAtEnemyContact's
                // path-end check doesn't apply here at all. Per the project owner's own spec
                // (2026-08-26 correction): red requires BOTH that this army still has an attack
                // left to spend this turn (any member not yet HasAirAttackedThisTurn) AND that the
                // path's own destination hex actually holds a real air-strike target (same target
                // set AviationCombatPresenter.FindAirStrikeTargetsAt will strike on arrival) —
                // landing on an empty hex, or one with nothing left to hit, is always green even
                // with attacks still unspent. Vision-gated the same way TruncateAtEnemyContact/
                // AvoidEnemyHex already are below — a fog-hidden target is a surprise on arrival,
                // never something the preview reveals early.
                HexCoord destination = path.Hexes[path.Hexes.Count - 1];
                bool hasTarget = VisionSystem.IsVisible(army.Owner, destination)
                    && (AviationCombatPresenter.FindAirStrikeTargetsAt(destination, army.Owner).Count > 0
                        || AviationCombatPresenter.CanExplosionStrikeAt(army, destination));
                color = HasAirAttackAvailable(army) && hasTarget
                    ? gameConfig.moveArrowAttackColor
                    : gameConfig.moveArrowMoveColor;
            }
            else
            {
                path = TruncateAtEnemyContact(path, army, out ArmyData enemyArmy);
                color = enemyArmy != null ? gameConfig.moveArrowAttackColor : gameConfig.moveArrowMoveColor;
            }

            // path.TotalCost is always terrain-weighted (see HexPathfinder.FindPath) — an air
            // army actually spends flat 1 MP per hex (see AviationRules.MovementCost, what
            // ArmyMovementOperation really charges), so the preview must show that instead.
            int moveCost = AviationRules.PathMoveCost(army, path);
            if (moveCost > army.CurrentMovement)
                color = gameConfig.moveArrowUnreachableColor;

            var points = new List<Vector3>(path.Hexes.Count);
            foreach (HexCoord hex in path.Hexes)
                points.Add(map.HexToWorld(hex));
            // Tail starts halfway between the origin hex's center and its exit edge rather than
            // on the center itself (owner's spec) — the shared edge's midpoint is the midpoint of
            // the two neighbouring centers, so halfway to it is a quarter of the first step.
            if (points.Count >= 2)
                points[0] = Vector3.Lerp(points[0], points[1], 0.25f);
            int apCost = army.PendingActivationApCost;
            int energyCost = army.PendingActivationEnergyCost;
            _pathArrow.Show(points, moveCost, apCost, energyCost, color);
        }

        // Whether ANY member of an air army still has its own attack available this turn — see
        // ShowPathArrow's own comment on why an air army's arrow colour keys off this instead of
        // TruncateAtEnemyContact's target-on-path check.
        private static bool HasAirAttackAvailable(ArmyData army)
        {
            foreach (UnitData member in army.Members)
                if (!member.HasAirAttackedThisTurn)
                    return true;
            return false;
        }

        // Truncates `path` at the first hostile contact — shared by the actual order and its
        // preview. A visible hero-only mover stops at a ground army for Capture/Kill too.
        // Only ever looks at a hex `mover.Owner` currently has VISION of — fog means an enemy
        // sitting on a not-yet-visible hex can't be pre-emptively routed around/stopped at when
        // the path is first computed; discovering it is what ArmyMovementOperation's own
        // shouldStopEarly callback (see HandleVisionStep below) is for, mid-move, one hex at a
        // time, same as the real game.
        private static HexPath TruncateAtEnemyContact(HexPath path, ArmyData mover, out ArmyData enemyArmy)
        {
            enemyArmy = null;
            // A fully hidden army still walks through without initiating contact.
            if (!BattleInitiator.CanInitiateContact(mover))
                return path;
            for (int i = 1; i < path.Hexes.Count; i++)
            {
                if (!VisionSystem.IsVisible(mover.Owner, path.Hexes[i]))
                    continue;
                enemyArmy = BattleInitiator.FindEnemyAt(path.Hexes[i], mover);
                if (enemyArmy != null)
                    return new HexPath(path.Hexes.GetRange(0, i + 1), path.TotalCost);
            }
            return path;
        }

        // HexPathfinder's own soft-avoidance hook (see its own comment) — routes `mover` around
        // a hex holding an enemy army when a reasonable detour exists, instead of always taking
        // the geometrically shortest route straight at/through it and only finding out at
        // TruncateAtEnemyContact time that it has to stop short (see the user's own request:
        // the shortest route often isn't the one that actually lets the army get furthest this
        // turn, once contact truncation is accounted for). Not gated on IsCombatCapable like
        // TruncateAtEnemyContact — a hero-only mover benefits from steering clear too, even
        // though it cannot fight a tactical round. Vision-gated same as TruncateAtEnemyContact
        // — only a currently-visible enemy is something the player could plausibly be routing
        // around; a fog-hidden one is never avoided, only discovered on arrival.
        private static System.Func<HexCoord, bool> AvoidEnemyHex(ArmyData mover)
        {
            return hex => VisionSystem.IsVisible(mover.Owner, hex) && BattleInitiator.FindEnemyAt(hex, mover) != null;
        }

        // Extracted out of TryIssueMoveOrder's own onComplete callback (2026-08-24 P0 fix — see
        // BattleStartResult's own comment) so the exact same contact-detection/popup-vs-immediate
        // decision can also be reasserted as a safety net after the fact — by AiTurnController.
        // MoveArmyRoutine right after a move settles, and (indirectly, unchanged) by
        // GameTurnController's own end-of-round contested sweep — instead of living only inside
        // the move-order's own onComplete closure where a missed call site could never resolve
        // contact at all until the round boundary. Deliberately does NOT touch _selectedHex/
        // highlight/infoPanel/armyButtonRow — that's move-order-flow UI bookkeeping the callers
        // below don't share (the AI safety-net call site has no human selection to clear), so it
        // stays inline in onComplete, gated on this method's own return value instead. Public for
        // AiTurnController.MoveArmyRoutine's own cross-file call.
        public BattleStartResult TryBeginBattleAt(HexCoord hex, ArmyData mover)
        {
            if (!BattleInitiator.CanInitiateContact(mover))
                return BattleStartResult.MoverCannotFight;
            if (DelayedBattleRegistry.IsHexPending(hex))
                return BattleStartResult.Pending;
            ArmyData enemy = BattleInitiator.FindDefenderOnArrival(hex, mover);
            if (enemy == null)
                return BattleStartResult.NoContact;

            // Starting a battle is the attacking army's strategic action for this turn. The
            // tactical attacker may never get a unit turn at all (for example, the defender
            // can be eliminated first by another resolution), so BeginAttack's own per-unit
            // reset is not enough to prevent this mover from receiving a second map order.
            foreach (UnitData member in mover.Members)
                member.MoveCurrent = 0;

            var participants = new List<ArmyData> { mover, enemy };

            // STEALTH-COMBAT-01: contact has just committed to a real encounter (FindDefenderOnArrival
            // refused a fully-hidden defender other than the building's own garrison,
            // CanInitiateContact refused a fully-hidden
            // mover) — this is the one authoritative reveal boundary, run BEFORE any battle/
            // pre-battle UI (including the Fight/Delay popup just below) ever shows, and BEFORE
            // the hero-only classification so it reads the true, full roster. Never touches any
            // OTHER army sharing this hex.
            Game.Combat.BattleEncounterContext encounter =
                Game.Combat.BattleEncounterCoordinator.PrepareCommittedEncounter(hex, participants, mover.Owner);
            // The arriving hero can be the hunted side. Persist the coordinator's canonical
            // hunter/target order in the popup and Delay record as well as in ShowEncounter.
            participants = new List<ArmyData>(encounter.Participants);

            // A hero-only contact (see BattleInitiator.IsEngageable vs IsCombatCapable) has
            // nothing for a normal Tactical Battle Module round to do — no acting units on that
            // side, nothing to click/attack — so it skips the grid entirely and goes straight to
            // a Capture Kill Challenge sequence instead (see BattleScreenUI.BeginCaptureKillEncounter).

            // A human-controlled mover gets the interactive Fight/Delay choice, same as always.
            // An AI/Neutral mover fights immediately instead of ever choosing Delay — see this
            // method's own former home (onComplete below) for why deferring never bought the AI
            // anything and once hung the whole turn loop.
            if (battleContactPopup != null && mover.Owner != null && mover.Owner.IsHuman)
            {
                battleContactPopup.Show(hex, participants, encounter.PresentationObserver,
                    onFight: fastResolve => battleScreen?.ShowEncounter(encounter, null, fastResolve),
                    onDelay: () => DelayedBattleRegistry.Add(new PendingBattle { Hex = hex, Participants = participants }));
            }
            else
            {
                battleScreen?.ShowEncounter(encounter, null);
            }
            return BattleStartResult.Started;
        }

        private void HidePathPreview()
        {
            if (_pathArrow != null)
                _pathArrow.Hide();
        }

        private ArmyData GetSelectedArmy()
        {
            return _selectedArmy?.Data;
        }

        private void TryIssueMoveOrder(HexCoord destination)
        {
            IssueMoveOrder(_selectedArmy, destination);
        }

        // IssueMoveOrder's own reject-reason feedback (see its guard clauses below) — a blocking
        // popup only a human can dismiss, so only shown for a human-owned mover. An AI/Neutral
        // move that can't be afforded just logs and fails quietly instead — same "no one to click
        // a popup during another player's turn" reasoning as the battle-contact branch further
        // down, and per the project owner's own report: an unguarded ShowSpawnHint here left the
        // popup open (and input blocked) for the rest of the game once an AI move failed this way.
        private void NotifyMoveBlocked(ArmyData army, string message)
        {
            if (army?.Owner != null && army.Owner.IsHuman)
                turnController?.ShowSpawnHint(message);
            else
                AiDebugLog.Write($"[AI] {army?.Owner?.Nickname ?? "Neutral"}: move order for {army?.Name} rejected - {message}");
        }

        // Player-agnostic move-order pipeline — pathfinding, AP spend, animated move, vision
        // steps, and enemy-contact handling, all identical to what a human's right-click already
        // triggers via TryIssueMoveOrder above, just taking the mover explicitly instead of
        // reading _selectedArmy. Used by Game.Ai.AiTurnController so an AI-controlled army's
        // move looks and behaves exactly like a human's, with no separate/duplicated movement
        // logic anywhere.
        // onHexEventEncountered — invoked (with the hex) the instant this move claims its stop for
        // a clean Hex Event, BEFORE the event resolves. The AI's TaskExecutor needs this exact
        // fact ("this move walked into an event") and cannot recover it afterwards: an AI mover's
        // event resolves synchronously with no popup, a Skip does not consume the event (so no
        // registry state change fires on a re-visit), and the whole thing is done by the time
        // IssueMoveOrder's caller regains control. Left null by the human path.
        public MoveOrderResult IssueMoveOrder(ArmyController controller, HexCoord destination,
            System.Action<HexCoord> onHexEventEncountered = null,
            bool allowUndefendedBuildingTakeover = true,
            bool allowAiEventExplore = true)
        {
            if (controller == null || controller.IsMoving || controller.Data?.IsMovementBusy == true)
                return MoveOrderResult.AlreadyMoving;

            // The garrison specifically can never move at all (it's anchored to its Barracks
            // building, not a mobile force) — assign units to a real army first (see the
            // garrison button on HexInfoPanelUI, or a precise click on its own marker).
            ArmyData army = controller.Data;
            if (army == null || army.IsGarrison || army.IsAirfield)
            {
                NotifyMoveBlocked(army, $"{army?.Name ?? "This army"} cannot move. Transfer its units to a mobile army.");
                return MoveOrderResult.CannotMove;
            }

            // An unresolved hostile contact must finish before another map order. For a
            // hero-only resident that resolution is Capture/Kill, for combatants it is battle.
            if (!AviationRules.IsAirArmy(army) && BattleInitiator.CanInitiateContact(army)
                && BattleInitiator.FindEnemyAt(army.Hex, army) != null)
            {
                NotifyMoveBlocked(army, $"{army.Name} cannot move while in combat.");
                return MoveOrderResult.LockedInCombat;
            }
            if (army.CurrentMovement <= 0)
            {
                NotifyMoveBlocked(army, $"{army.Name} has no movement left this turn.");
                return MoveOrderResult.NoMovementLeft;
            }
            if (destination.Equals(army.Hex))
                return MoveOrderResult.AlreadyAtDestination;

            // flatCost for an air army — see UpdateMovePreview's own comment: an aircraft's real
            // charge is a flat 1 MP/hex, so the route it's actually ordered along must be the
            // shortest hex-count route, not a ground-weighted detour around mountains. The move
            // itself (ArmyMovementOperation) already charges flat per hex; only the route
            // SELECTION here was still terrain-weighted.
            HexPath path = HexPathfinder.FindPath(map, army.Hex, destination, AvoidEnemyHex(army),
                flatCost: AviationRules.IsAirArmy(army));
            if (path == null)
                return MoveOrderResult.NoPath;

            // "Initiating Battle" (see Game.Combat.BattleInitiator) — moving onto a hex with a
            // combat-capable enemy army starts a fight there instead of continuing past it, even
            // if the original destination was further along the path. Shared with the hover
            // preview arrow (see ShowPathArrow) so it always shows exactly where the army will
            // actually stop.
            if (!AviationRules.IsAirArmy(army))
                path = TruncateAtEnemyContact(path, army, out _);

            // An army only ever stops short of a hex it can't fully afford (see
            // ArmyMovementOperation) — never enters it partway "in debt" any more. Caught
            // here specifically for the very next step: if that alone is already unaffordable,
            // MoveAlong would do nothing at all and the player would see no feedback for why the
            // order silently failed.
            map.TryGetTerrainAt(path.Hexes[1], out TerrainTypeEntry firstStepEntry);
            int terrainFirstStepCost = firstStepEntry != null ? Mathf.Max(1, firstStepEntry.moveCost) : 1;
            int firstStepCost = AviationRules.MovementCost(army, terrainFirstStepCost);
            if (army.CurrentMovement < firstStepCost)
            {
                NotifyMoveBlocked(army,
                    $"Need {firstStepCost} movement to enter this hex; {army.CurrentMovement} left.");
                return MoveOrderResult.InsufficientStepMovement;
            }

            PlayerRoot ownerRoot = PlayerRootRegistry.FindFor(army.Owner);
            if (ownerRoot == null)
                return MoveOrderResult.NoOwnerRoot;

            // AP (ArmyData.ActivationApCost — the sum of every member's own ActivationApCost,
            // so a bigger army costs more to get moving) is only spent the first time this
            // army is given a move order in a turn — every move order after that, for the
            // rest of the turn, costs MoveCurrent only. See ArmyData.HasActivatedThisTurn.
            // An air army instead pays its launch once per sortie (ArmyData.PendingActivation*):
            // continuing a paid sortie, this turn or a later one, is free.
            if (!ArmyActions.CanAffordActivation(army, ownerRoot))
            {
                NotifyMoveBlocked(army, $"{army.Name} needs {army.PendingActivationApCost} AP and {army.PendingActivationEnergyCost} Energy to move.");
                return MoveOrderResult.InsufficientActionPoints;
            }

            HexCoord originHex = army.Hex;
            ArmyController movingArmy = controller;
            if (army.IsCarrierFlightDeck)
                movingArmy.ResetTransform(map, ResolveArmyOffset(army.Hex, movingArmy));

            var movement = ArmyMovementOperation.ForOrder(army, map, path.Hexes, ownerRoot, destination, this,
                arrivalPresentation: result =>
                {
                    HexCoord actualHex = result.Hex;
                    RestackArmiesOn(originHex, null);
                    movingArmy.SetLayoutPosition(map.HexToWorld(actualHex) + ResolveArmyOffset(actualHex, movingArmy), true);
                    if (result.HexEvent) { RestackArmiesOn(actualHex, movingArmy); return; }
                    if (result.Contact == BattleStartResult.Started)
                    {
                        _selectedHex = null;
                        if (highlight != null) highlight.Hide();
                        if (infoPanel != null) infoPanel.Hide();
                        if (armyButtonRow != null) armyButtonRow.Hide();
                    }
                    else if (_selectedArmy == movingArmy)
                    {
                        movingArmy.SetSelected(true);
                        SelectHex(actualHex, preserveSelection: true);
                    }
                    RestackArmiesOn(actualHex, movingArmy);
                }, eventEncountered: onHexEventEncountered,
                allowUndefendedBuildingTakeover: allowUndefendedBuildingTakeover, allowAiEventExplore: allowAiEventExplore);
            System.Action reconcile = () =>
            {
                if (movingArmy != null && army.Controller == movingArmy && ArmyRegistry.IsRegistered(army))
                    RestackArmiesOn(army.Hex, null);
            };
            movingArmy.PlayMovement(movement, map, hex => ResolveArmyOffset(hex, movingArmy),
                onStepStarted: (from, to) => ObserveMovingArmyStep(army, from, to, completed: false),
                onStepCompleted: (from, to) => ObserveMovingArmyStep(army, from, to, completed: true),
                resolveStepAsync: aviationCombatPresenter != null
                    ? (System.Func<HexCoord, HexCoord, ArmyController.StepResolutionOutcome, System.Collections.IEnumerator>)
                        ((from, to, outcome) => aviationCombatPresenter.ResolveStep(army, to, outcome)) : null,
                onSettled: reconcile, onCancelled: reconcile);
            RestackArmiesOn(originHex, movingArmy);

            HidePathPreview();
            _lastPreviewedHover = null;
            return MoveOrderResult.Started;
        }
    }
}
