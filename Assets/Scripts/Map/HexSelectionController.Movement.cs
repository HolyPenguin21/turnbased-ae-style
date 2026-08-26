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
            // No army (or it's the garrison, which can never move), or it's already mid-move
            // (see TryIssueMoveOrder) — nothing to preview. army.Hex only updates once the move
            // actually finishes (ArmyRegistry.MoveArmy), so without this check the preview would
            // keep pathfinding from the stale origin hex for the whole animation. Once the move
            // ends, SelectHex re-runs on the destination and a fresh hover elsewhere shows a new
            // arrow again, same as any other selection change.
            if (_selectedArmy == null || _selectedArmy.IsMoving || army == null || army.IsGarrison
                || !hoverCoord.HasValue || hoverCoord.Value.Equals(army.Hex))
            {
                if (_lastPreviewedHover.HasValue)
                {
                    _lastPreviewedHover = null;
                    HidePathPreview();
                }
                return;
            }

            if (_lastPreviewedHover.HasValue && _lastPreviewedHover.Value.Equals(hoverCoord.Value))
                return; // same hex as last frame — nothing changed, don't re-run pathfinding

            _lastPreviewedHover = hoverCoord;

            HexPath path = HexPathfinder.FindPath(map, army.Hex, hoverCoord.Value, AvoidEnemyHex(army));
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
            bool needsActivation = !army.HasActivatedThisTurn;
            int energyCost = needsActivation ? army.ActivationEnergyCost : 0;
            PlayerRoot ownerRoot = PlayerRootRegistry.FindFor(army.Owner);
            bool canAffordActivation = !needsActivation
                || (ownerRoot != null && ownerRoot.CanSpendActionPoints(army.ActivationApCost)
                    && ownerRoot.GetResource(ResourceType.Energy) >= energyCost);
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
            ArmyData enemyArmy = null;
            if (!AviationRules.IsAirArmy(army))
                path = TruncateAtEnemyContact(path, army, out enemyArmy);
            Color color = enemyArmy != null ? gameConfig.moveArrowAttackColor : gameConfig.moveArrowMoveColor;

            var points = new List<Vector3>(path.Hexes.Count);
            foreach (HexCoord hex in path.Hexes)
                points.Add(map.HexToWorld(hex));
            int apCost = army.HasActivatedThisTurn ? 0 : army.ActivationApCost;
            int energyCost = army.HasActivatedThisTurn ? 0 : army.ActivationEnergyCost;
            // path.TotalCost is always terrain-weighted (see HexPathfinder.FindPath) — an air
            // army actually spends flat 1 MP per hex (see AviationRules.MovementCost, what
            // ArmyController.MoveRoutine really charges), so the preview must show that instead.
            _pathArrow.Show(points, AviationRules.PathMoveCost(army, path), apCost, energyCost, color);
        }

        // Truncates `path` at the first hex (after the origin) holding a combat-capable enemy
        // army, if any — shared by TryIssueMoveOrder (the actual order) and ShowPathArrow (its
        // hover preview). Only checked for a combat-capable mover itself (a hero-only army isn't
        // "combat capable" per the manual and has its own separate — not yet implemented —
        // capture/kill handling), in which case this is a no-op and returns `path` unchanged.
        // Only ever looks at a hex `mover.Owner` currently has VISION of — fog means an enemy
        // sitting on a not-yet-visible hex can't be pre-emptively routed around/stopped at when
        // the path is first computed; discovering it is what ArmyController.MoveRoutine's own
        // shouldStopEarly callback (see HandleVisionStep below) is for, mid-move, one hex at a
        // time, same as the real game.
        private static HexPath TruncateAtEnemyContact(HexPath path, ArmyData mover, out ArmyData enemyArmy)
        {
            enemyArmy = null;
            if (!BattleInitiator.IsCombatCapable(mover))
                return path;
            for (int i = 1; i < path.Hexes.Count; i++)
            {
                if (!VisionSystem.IsVisible(mover.Owner, path.Hexes[i]))
                    continue;
                enemyArmy = BattleInitiator.FindEnemyAt(path.Hexes[i], mover.Owner);
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
        // though it never triggers a stop there itself. Vision-gated same as TruncateAtEnemyContact
        // — only a currently-visible enemy is something the player could plausibly be routing
        // around; a fog-hidden one is never avoided, only discovered on arrival.
        private static System.Func<HexCoord, bool> AvoidEnemyHex(ArmyData mover)
        {
            return hex => VisionSystem.IsVisible(mover.Owner, hex) && BattleInitiator.FindEnemyAt(hex, mover.Owner) != null;
        }

        // Per-step callback for ArmyController.MoveRoutine's shouldStopEarly — called once per
        // hex actually entered, right after the army lands there. Recomputes the mover's own
        // vision from its new position FIRST (so the fog/markers/labels visibly update live as
        // the army advances, not just once the whole move finishes), then checks whether this
        // specific hex was previously unknown to the mover's owner: if so and it turns out to
        // hold an enemy army or a foreign-owned building, the move stops right here instead of
        // continuing blind toward a destination that was only ever chosen without knowing this
        // was in the way. A hex the owner could already see (nothing newly revealed) never
        // interrupts, regardless of what's there — TruncateAtEnemyContact/AvoidEnemyHex already
        // handled anything visible at path-computation time.
        private static bool HandleVisionStep(ArmyData mover, HexCoord hex)
        {
            bool wasKnown = VisionSystem.IsVisible(mover.Owner, hex);
            VisionSystem.RecomputeFor(mover.Owner);
            // Air armies still reveal the map while flying, but strategic ground contact and a
            // foreign building never halt them; their own step resolver later handles AA/raid.
            if (AviationRules.IsAirArmy(mover))
                return false;
            if (wasKnown)
                return false;

            if (BattleInitiator.FindEnemyAt(hex, mover.Owner) != null)
                return true;

            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.Owner != null && building.Owner != mover.Owner;
        }

        // A pure check (no side effects) for shouldStopEarly: does `hex` carry an active,
        // un-consumed Hex Event with no engageable enemy also on it — the "clean hex" case (see
        // HexSelectionController.Events.cs's BeginCleanHexEvent). A Hex Event's own guard is never
        // a real ArmyRegistry entry until the player actually commits to Explore (see
        // HexEventRegistry.Entry.ResolvedGuardMembers's own comment), so it can never be what's
        // found here — only an unrelated pre-existing neutral army (see CitadelSetupController.
        // MapContent.GenerateNeutralArmies) can. A hex carrying one of those is deliberately NOT
        // claimed here — that "collision" case is left entirely to the ordinary enemy-contact
        // handling right below in onComplete, which already stops the move on its own (see
        // TruncateAtEnemyContact/actualEnemyContact); the event itself only resolves once that
        // combat is fully done AND the hex is fully clear (see BattleScreenUI.Combat.cs's own
        // ResolveHexAfterVictory, which hooks into HexSelectionController.TriggerHexEventIfClear),
        // per the user's own "triggers only after full battle resolution" rule. Vision is
        // deliberately NOT recomputed here, unlike HandleVisionStep — an event hex isn't gated on
        // fog/reveal, it triggers whenever entered, known or not.
        private static bool HasUnclaimedCleanEventHex(ArmyData mover, HexCoord hex)
        {
            HexEventRegistry.Entry entry = HexEventRegistry.FindAt(hex);
            if (entry == null || entry.Consumed)
                return false;
            foreach (ArmyData other in ArmyRegistry.AllAt(hex))
                if (other.Owner != mover.Owner && BattleInitiator.IsEngageable(other))
                    return false;
            return true;
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
            if (!BattleInitiator.IsCombatCapable(mover))
                return BattleStartResult.MoverCannotFight;
            if (DelayedBattleRegistry.IsHexPending(hex))
                return BattleStartResult.Pending;
            ArmyData enemy = BattleInitiator.FindEnemyAt(hex, mover.Owner);
            if (enemy == null)
                return BattleStartResult.NoContact;

            // Starting a battle is the attacking army's strategic action for this turn. The
            // tactical attacker may never get a unit turn at all (for example, the defender
            // can be eliminated first by another resolution), so BeginAttack's own per-unit
            // reset is not enough to prevent this mover from receiving a second map order.
            foreach (UnitData member in mover.Members)
                member.MoveCurrent = 0;

            var participants = new List<ArmyData> { mover, enemy };
            // A hero-only contact (see BattleInitiator.IsEngageable vs IsCombatCapable) has
            // nothing for a normal Tactical Battle Module round to do — no acting units on that
            // side, nothing to click/attack — so it skips the grid entirely and goes straight to
            // a Capture Kill Challenge sequence instead (see BattleScreenUI.BeginCaptureKillEncounter).
            bool targetHeroOnly = !BattleInitiator.IsCombatCapable(enemy);

            // A human-controlled mover gets the interactive Fight/Delay choice, same as always.
            // An AI/Neutral mover fights immediately instead of ever choosing Delay — see this
            // method's own former home (onComplete below) for why deferring never bought the AI
            // anything and once hung the whole turn loop.
            if (battleContactPopup != null && mover.Owner != null && mover.Owner.IsHuman)
            {
                battleContactPopup.Show(hex, participants,
                    onFight: () =>
                    {
                        if (targetHeroOnly)
                            battleScreen?.BeginCaptureKillEncounter(mover, enemy, null);
                        else
                            battleScreen?.Show(hex, participants, null);
                    },
                    onDelay: () => DelayedBattleRegistry.Add(new PendingBattle { Hex = hex, Participants = participants }));
            }
            else if (targetHeroOnly)
            {
                battleScreen?.BeginCaptureKillEncounter(mover, enemy, null);
            }
            else
            {
                battleScreen?.Show(hex, participants, null);
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
                AiDebugLog.Write($"[AI] {army?.Owner?.Nickname ?? "Neutral"}: move order for {army?.Name} rejected — {message}");
        }

        // Player-agnostic move-order pipeline — pathfinding, AP spend, animated move, vision
        // steps, and enemy-contact handling, all identical to what a human's right-click already
        // triggers via TryIssueMoveOrder above, just taking the mover explicitly instead of
        // reading _selectedArmy. Used by Game.Ai.AiTurnController so an AI-controlled army's
        // move looks and behaves exactly like a human's, with no separate/duplicated movement
        // logic anywhere.
        public MoveOrderResult IssueMoveOrder(ArmyController controller, HexCoord destination)
        {
            if (controller == null || controller.IsMoving)
                return MoveOrderResult.AlreadyMoving;

            // The garrison specifically can never move at all (it's anchored to its Barracks
            // building, not a mobile force) — assign units to a real army first (see the
            // garrison button on HexInfoPanelUI, or a precise click on its own marker).
            ArmyData army = controller.Data;
            if (army == null || army.IsGarrison || army.IsAirfield)
            {
                NotifyMoveBlocked(army, $"{army?.Name ?? "This army"} can't move — assign its units to a real army first.");
                return MoveOrderResult.CannotMove;
            }
            RefreshArmyIcon(army);

            // An army sharing its hex with a combat-capable enemy army can't just walk away —
            // the only way out is retreating from battle (see the manual's Retreat Challenge),
            // which doesn't exist yet, so for now this hex is a dead end until real combat
            // resolution can free it. See Game.Combat.BattleInitiator.
            //
            // Gated on IsCombatCapable(army) — the MOVER, not whatever enemy is sharing the hex
            // (2026-08-24 P0 fix, project owner's own report: a hero-only army that stumbled onto
            // an enemy via fog-of-war reveal mid-move never triggers TryBeginBattleAt's own
            // contact branch either — see MoverCannotFight — so it just finishes its move
            // "coexisting" with that enemy. Without this gate, the very next order for that same
            // army hit this exact check and read as permanently LockedInCombat, with no fight to
            // ever resolve it and no way out — a real Capture Kill Challenge/Retreat skill this
            // army doesn't have. A hero-only army was never a real combat participant on this hex
            // to begin with, so it stays free to just walk off).
            if (!AviationRules.IsAirArmy(army) && BattleInitiator.IsCombatCapable(army)
                && BattleInitiator.FindEnemyAt(army.Hex, army.Owner) != null)
            {
                NotifyMoveBlocked(army, $"{army.Name} is locked in combat and can't move away.");
                return MoveOrderResult.LockedInCombat;
            }
            if (army.CurrentMovement <= 0)
            {
                NotifyMoveBlocked(army, $"{army.Name} has no movement points left this turn.");
                return MoveOrderResult.NoMovementLeft;
            }
            if (destination.Equals(army.Hex))
                return MoveOrderResult.AlreadyAtDestination;

            HexPath path = HexPathfinder.FindPath(map, army.Hex, destination, AvoidEnemyHex(army));
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
            // ArmyController.MoveRoutine) — never enters it partway "in debt" any more. Caught
            // here specifically for the very next step: if that alone is already unaffordable,
            // MoveAlong would do nothing at all and the player would see no feedback for why the
            // order silently failed.
            map.TryGetTerrainAt(path.Hexes[1], out TerrainTypeEntry firstStepEntry);
            int terrainFirstStepCost = firstStepEntry != null ? Mathf.Max(1, firstStepEntry.moveCost) : 1;
            int firstStepCost = AviationRules.MovementCost(army, terrainFirstStepCost);
            if (army.CurrentMovement < firstStepCost)
            {
                NotifyMoveBlocked(army,
                    $"Not enough movement points to enter that hex ({firstStepCost} needed, {army.CurrentMovement} left).");
                return MoveOrderResult.InsufficientStepMovement;
            }

            PlayerRoot ownerRoot = PlayerRootRegistry.FindFor(army.Owner);
            if (ownerRoot == null)
                return MoveOrderResult.NoOwnerRoot;

            // AP (ArmyData.ActivationApCost — the sum of every member's own ActivationApCost,
            // so a bigger army costs more to get moving) is only spent the first time this
            // army is given a move order in a turn — every move order after that, for the
            // rest of the turn, costs MoveCurrent only. See ArmyData.HasActivatedThisTurn.
            bool needsActivation = !army.HasActivatedThisTurn;
            int energyCost = needsActivation ? army.ActivationEnergyCost : 0;
            if (needsActivation && (!ownerRoot.CanSpendActionPoints(army.ActivationApCost)
                || ownerRoot.GetResource(ResourceType.Energy) < energyCost))
            {
                NotifyMoveBlocked(army, $"Not enough resources to move {army.Name} ({army.ActivationApCost} AP, {energyCost} Energy needed).");
                return MoveOrderResult.InsufficientActionPoints;
            }
            if (needsActivation)
            {
                ownerRoot.SpendActionPoints(army.ActivationApCost);
                if (energyCost > 0)
                    ownerRoot.AddResource(ResourceType.Energy, -energyCost);
                army.HasActivatedThisTurn = true;
            }

            HexCoord originHex = army.Hex;
            ArmyController movingArmy = controller;

            // Set true by shouldStopEarly below the instant it claims the stop for a "clean" Hex
            // Event hex — read inside onComplete to skip the ordinary enemy-contact branch for
            // this same hex-entry (see HasUnclaimedCleanEventHex's own comment on why a
            // "collision" hex is deliberately NOT claimed here and falls through to that branch
            // unmodified instead).
            bool eventStopClaimed = false;

            // Let the idle hover/pulse animation ease back to its resting pose first — jumping
            // straight from mid-bob/mid-pulse into the move animation was what made movement
            // look jerky right as it started. SettleThen already claims IsMoving immediately,
            // so a second order can't sneak in during that brief wait.
            movingArmy.SettleThen(() =>
            {
                movingArmy.MoveAlong(map, path.Hexes, hex => ResolveArmyOffset(hex, movingArmy), () =>
                {
                    // shouldStopEarly (below) already recomputed vision for every hex actually
                    // entered — nothing extra needed here just to catch up on that.
                    // The army can stop short partway along the path, the moment the next hex
                    // would cost more than what's left — movingArmy.CurrentHex (tracked live
                    // during MoveAlong, see ArmyController) reflects wherever it actually ended
                    // up, which is NOT necessarily `destination`. ArmyRegistry.MoveArmy is the
                    // one place Data.Hex itself actually changes, once the whole move is
                    // finished.
                    HexCoord actualHex = movingArmy.CurrentHex;
                    ArmyRegistry.MoveArmy(army, actualHex);

                    // Create the container as soon as aircraft reach their own barracks. They
                    // do not merge into it: landing is only the end-turn refuel condition.
                    if (AviationRules.IsAirArmy(army) && AviationRules.IsOwnedAirfieldAt(actualHex, army.Owner))
                        AviationActions.EnsureAirfield(this, army.Owner, actualHex);

                    // The origin hex's own layout (a building recentring once the last army
                    // actually leaves, in particular) only reads correctly once MoveArmy above
                    // has re-keyed `army` away from it — the earlier RestackArmiesOn(originHex,
                    // ...) call, made when the order was first given, still saw `army`
                    // registered there and couldn't reflect this.
                    RestackArmiesOn(originHex, null);

                    // The user's own Siege spec, undefended case: a building with no engageable
                    // army of its own owner present at all just changes hands/gets destroyed the
                    // moment an enemy army finishes moving onto its hex — no fight to trigger,
                    // nothing to delay, there was never anyone there to put one up (see
                    // BattleScreenUI.Combat.cs's HandleBuildingOnArmyDefeat for the "army got
                    // wiped mid-fight" case this doesn't cover). Runs regardless of whether an
                    // actual army contact ALSO fires below — a third player's building could sit
                    // on the same hex as someone else's army. Shared with BattleScreenUI.Retreat.
                    // cs's PerformRetreat, which needs the exact same check for a retreat landing
                    // on an undefended hex — see BuildingRegistry.CaptureOrDestroyIfUndefended.
                    if (!AviationRules.IsAirArmy(army))
                        BuildingRegistry.CaptureOrDestroyIfUndefended(actualHex, army.Owner, this);

                    // movingArmy's own marker was last positioned by MoveAlong's resolveOffset
                    // call for actualHex, which ran BEFORE the destroy above — if that undefended
                    // building was what CaptureOrDestroyIfUndefended just tore down, the offset it
                    // landed on (beside the now-gone building) is stale. RestackArmiesOn below
                    // deliberately skips movingArmy (still IsMoving here — see ArmyController.
                    // MoveAlong's own comment on why onComplete runs before that flips false), so
                    // nothing else would ever re-snap it to hex centre until the hex was reselected
                    // (see the user's own report). Cheap/idempotent when nothing actually changed.
                    movingArmy.transform.position = map.HexToWorld(actualHex) + ResolveArmyOffset(actualHex, movingArmy);

                    // Re-checked fresh against `actualHex`, NOT the `enemyArmy` truncation found
                    // before the move even started — the army can run out of shared move points
                    // and stop well short of the hex TruncateAtEnemyContact had in mind (e.g. it
                    // takes 2 but only 1 remains), in which case there was never any real contact
                    // and this must stay silent. See TryBeginBattleAt's own comment for what
                    // MoverCannotFight/Pending/NoContact each mean, and — for Pending specifically
                    // — why leaving that pairing unresolved here is no longer the end of the story
                    // (AiTurnController.MoveArmyRoutine's own post-move safety check, and
                    // GameTurnController's end-of-round sweep, both still eventually force it).
                    // Hex Events: a "clean" hex claimed by shouldStopEarly below — resolved here,
                    // AFTER the registry re-keying/RestackArmiesOn(originHex, ...) above, rather
                    // than synchronously inside shouldStopEarly itself (mid-coroutine, before that
                    // re-keying), so a popup showing or a battle opening from BeginCleanHexEvent
                    // never sees the mover still registered at its stale origin hex. Takes over
                    // this whole branch (see the else below) — the ordinary enemy-contact check
                    // never even runs for this hex-entry.
                    if (eventStopClaimed)
                    {
                        BeginCleanHexEvent(army, actualHex, destination, movingArmy);
                        RestackArmiesOn(actualHex, movingArmy);
                        return;
                    }

                    BattleStartResult battleResult = AviationRules.IsAirArmy(army)
                        ? BattleStartResult.NoContact
                        : TryBeginBattleAt(actualHex, army);

                    if (battleResult == BattleStartResult.Started)
                    {
                        // Not a full Deselect() (that would also clear _selectedArmy, still
                        // needed below in the general case) — but the ORIGIN hex's own selection
                        // must still go: the army has already left it, and the general
                        // re-select-at-actualHex logic below is skipped entirely on contact (see
                        // the else branch's own comment), so nothing else would ever clear it.
                        // Left alone, its highlight/info panels — and the multi-army button row,
                        // if the origin hex still has 2+ of the player's own armies left on it —
                        // kept pointing at a hex/roster the army no longer occupies/belongs to.
                        _selectedHex = null;
                        if (highlight != null) highlight.Hide();
                        if (infoPanel != null) infoPanel.Hide();
                        if (armyInfoPanel != null) armyInfoPanel.Hide();
                        if (armyButtonRow != null) armyButtonRow.Hide();
                    }
                    // Re-arm the hover/pulse animation once the move finishes, if it's still the
                    // active selection (the player may have selected something else meanwhile) —
                    // and follow the selection itself to wherever the army actually stopped,
                    // instead of leaving the highlight/info panels behind on wherever it was
                    // originally clicked from. Skipped entirely on contact — SelectHex would just
                    // re-show the very panels just hidden above, right underneath the popup.
                    else if (_selectedArmy == movingArmy)
                    {
                        movingArmy.SetSelected(true);
                        SelectHex(actualHex, preserveSelection: true);
                    }

                    // The army just settled on `actualHex` — anyone else already resting
                    // there (e.g. it now forms a two-different-owners pair) needs to shift to
                    // match.
                    RestackArmiesOn(actualHex, movingArmy);
                }, shouldStopEarly: hex =>
                {
                    // An air army still reveals FOW via HandleVisionStep below, but never triggers
                    // or stops for a Hex Event — only a ground army can Explore one.
                    if (!AviationRules.IsAirArmy(army) && HasUnclaimedCleanEventHex(army, hex))
                    {
                        eventStopClaimed = true;
                        return true;
                    }
                    return HandleVisionStep(army, hex);
                },
                onStepStarted: (from, to) => ObserveMovingArmyStep(army, from, to, completed: false),
                onStepCompleted: (from, to) => ObserveMovingArmyStep(army, from, to, completed: true));
                // Leaving originHex can just as easily change what's left behind there (e.g. a
                // pair collapsing back down to one army, which should re-centre).
                RestackArmiesOn(originHex, movingArmy);
            });

            HidePathPreview();
            _lastPreviewedHover = null;
            return MoveOrderResult.Started;
        }
    }
}
