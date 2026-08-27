using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    // Individual stealth + personal (per-observer) detection — the layer VisionSystem
    // deliberately does NOT cover. VisionSystem answers "does player P see hex H"; this
    // answers "does player P see UNIT U right now", which can be false even on a fully
    // visible hex (project owner's own design). Nothing here is stored in VisionSystem's
    // hex sets.
    //
    // State split:
    //   UnitData.IsHidden          — owner-facing "this unit is in stealth" (set only via
    //                                EnterStealth/ExitStealth here).
    //   _detected[unit][observer]  — the observer's completed-turn ordinal THROUGH which the
    //                                detection stays live (an "expiry ordinal", not a raw
    //                                snapshot). The detection is live while
    //                                CompletedTurnsProvider(observer) <= that value; it lapses
    //                                once the observer finishes a turn past it. This is NOT
    //                                TurnNumber+1 — turn order is re-rolled each round.
    //                                When the challenge succeeds on the OBSERVER'S OWN turn the
    //                                expiry is CompletedTurnsFor(observer) + 1 (their own
    //                                completed-turn count isn't bumped until that turn ends, so
    //                                a bare snapshot would already read "expired" one turn
    //                                early); on anyone else's turn it is the bare
    //                                CompletedTurnsFor(observer), which already means "through
    //                                the end of the observer's next turn". See
    //                                ObserverTakingTurnProvider.
    //
    // The hidden challenge itself reuses Game.Combat.ChallengeResolver (the one deterministic/
    // testable dice path) and is SILENT: no popup, no combat UI, no player-facing or owner-
    // facing log. The only diagnostic is DebugLog, opt-in, default off (project owner's spec).
    public static class StealthSystem
    {
        // Set by GameTurnController.BeginGame. Default returns 0 so the standalone stealth
        // simulation (Tools/stealth-sim) can drive detection expiry by hand without a live
        // turn controller.
        public static Func<PlayerSetupData, long> CompletedTurnsProvider = _ => 0L;

        // True while `observer` is the player whose turn is being taken RIGHT NOW. Set by
        // GameTurnController.BeginGame; default false so the standalone stealth simulation (and
        // any pre-game call) treats every detection as happening on someone else's turn. A
        // detection scored during the observer's own turn must still last through the end of
        // their NEXT turn — but the active player's completed-turn counter isn't incremented
        // until that turn ends, so without this flag MarkDetected would record an expiry that
        // PurgeExpiredFor lapses at the end of the very same turn (project owner's own P1).
        public static Func<PlayerSetupData, bool> ObserverTakingTurnProvider = _ => false;

        // Terrain move cost at a hex — hideDice = stealthLevel + (moveCost - 1). Set by
        // whoever owns the HexMap at setup; default flat 1 (no terrain bump) for the sim.
        public static Func<HexCoord, int> TerrainMoveCostProvider = _ => 1;

        // Opt-in developer diagnostic only (project owner's spec §3/§9) — never a player- or
        // owner-facing signal. Off by default.
        public static bool DebugLog;

        // The hidden challenge's dice roll — the shared ChallengeResolver path by default
        // (project owner's spec §2). A single injectable seam so the standalone stealth
        // simulation can drive exact spot/hide-success counts without UnityEngine.Random.
        public static Func<int, int, ChallengeResult> ChallengeRoller = ChallengeResolver.Resolve;

        private static readonly Dictionary<UnitData, Dictionary<PlayerSetupData, long>> _detected
            = new Dictionary<UnitData, Dictionary<PlayerSetupData, long>>();

        // Per-detector queue of "you just spotted X at H" lines, drained at the start of that
        // detector's own next turn (see GameTurnController.OnTurnConfirmed — shown via
        // SpawnHintPopupUI right after the aviation end-of-turn damage messages). ONLY the
        // player who rolled the successful detection ever sees these; the hidden unit's owner
        // is still told nothing (design §4/§16). A fresh detection of the same (unit, observer)
        // pair is not re-announced while the previous one is still live.
        private static readonly Dictionary<PlayerSetupData, List<string>> _detectionNotices
            = new Dictionary<PlayerSetupData, List<string>>();

        // Fired whenever a unit's hidden/detected state changes so UI rosters/markers,
        // AiMapMemory and VisionSystem content notifications rebuild. StealthChangedAt carries
        // the affected hex where one is known (for a targeted content refresh).
        public static event Action StealthChanged;
        public static event Action<HexCoord> StealthChangedAt;

        public static void Clear()
        {
            _detected.Clear();
            _detectionNotices.Clear();
        }

        // Removes and returns `observer`'s queued detection announcements (empty list if none).
        public static List<string> TakeDetectionNotices(PlayerSetupData observer)
        {
            if (observer != null && _detectionNotices.TryGetValue(observer, out List<string> notices))
            {
                _detectionNotices.Remove(observer);
                return notices;
            }
            return new List<string>();
        }

        // ---------------------------------------------------------------- stealth state ----

        public static bool CanEnterStealth(UnitData unit)
            => unit != null && !unit.IsHidden && AbilityParams.GetStealthLevel(unit) > 0;

        public static void EnterStealth(UnitData unit)
        {
            if (!CanEnterStealth(unit))
                return;
            unit.IsHidden = true;
            Notify(unit, null);
        }

        // Voluntary owner exit (0 AP) AND the involuntary reveal a directed enemy action /
        // a landed air strike triggers — same clean-up either way.
        public static void ExitStealth(UnitData unit)
        {
            if (unit == null || !unit.IsHidden)
                return;
            unit.IsHidden = false;
            _detected.Remove(unit);
            Notify(unit, null);
        }

        // Drop stealth on every hidden member of `army` — what the start of a battle does
        // (project owner's own call): once a fight is actually joined, both sides' hidden
        // units reveal and fight as an ordinary army. No-op for a member that isn't hidden.
        public static void RevealArmy(ArmyData army)
        {
            if (army == null)
                return;
            foreach (UnitData member in new List<UnitData>(army.Members))
                if (member.IsHidden)
                    ExitStealth(member);
        }

        // Death / removal from play / return-to-deck — drop every trace so a recycled
        // UnitData reference can never carry a stale detection.
        public static void OnUnitRemoved(UnitData unit)
        {
            if (unit == null)
                return;
            bool had = unit.IsHidden || _detected.ContainsKey(unit);
            unit.IsHidden = false;
            _detected.Remove(unit);
            if (had)
                Notify(unit, null);
        }

        // ------------------------------------------------------------ personal detection ----

        public static bool IsDetectedBy(UnitData unit, PlayerSetupData observer)
        {
            if (unit == null || observer == null)
                return false;
            if (!_detected.TryGetValue(unit, out Dictionary<PlayerSetupData, long> byObserver)
                || !byObserver.TryGetValue(observer, out long expiry))
                return false;
            if (CompletedTurnsProvider(observer) <= expiry)
                return true;
            // Expired — drop lazily so a later re-detection starts clean.
            byObserver.Remove(observer);
            if (byObserver.Count == 0)
                _detected.Remove(unit);
            return false;
        }

        public static void MarkDetected(UnitData unit, PlayerSetupData observer)
        {
            if (unit == null || observer == null)
                return;
            if (!_detected.TryGetValue(unit, out Dictionary<PlayerSetupData, long> byObserver))
            {
                byObserver = new Dictionary<PlayerSetupData, long>();
                _detected[unit] = byObserver;
            }
            long expiry = CompletedTurnsProvider(observer);
            if (ObserverTakingTurnProvider(observer))
                expiry += 1; // scored on the observer's own turn — must survive through the
                             // end of their NEXT turn, not the current one (see class comment).
            byObserver[observer] = expiry;
            // A new personal detection changes what `observer` can see — refresh UI rosters/
            // markers and AiMapMemory (spec §4).
            StealthChanged?.Invoke();
        }

        // Drop every now-expired detection whose observer is `observer` and fire one change
        // notification if anything actually lapsed — called at that observer's turn boundary
        // (GameTurnController.AdvanceToNextPlayer) so UI/AI stop treating the unit as visible
        // the moment the window closes, not only on the next unrelated refresh.
        public static void PurgeExpiredFor(PlayerSetupData observer)
        {
            if (observer == null)
                return;
            bool anyLapsed = false;
            long now = CompletedTurnsProvider(observer);
            var emptied = new List<UnitData>();
            foreach (KeyValuePair<UnitData, Dictionary<PlayerSetupData, long>> pair in _detected)
            {
                if (pair.Value.TryGetValue(observer, out long expiry) && now > expiry)
                {
                    pair.Value.Remove(observer);
                    anyLapsed = true;
                    if (pair.Value.Count == 0)
                        emptied.Add(pair.Key);
                }
            }
            foreach (UnitData unit in emptied)
                _detected.Remove(unit);
            if (anyLapsed)
                StealthChanged?.Invoke();
        }

        // THE predicate every enemy-facing query filters on: "is `unit` currently invisible
        // to `observer`". A unit that isn't hidden, is the observer's own, or has been
        // personally detected by the observer is NOT hidden from them.
        public static bool IsHiddenFrom(UnitData unit, PlayerSetupData observer)
        {
            if (unit == null || !unit.IsHidden)
                return false;
            if (observer == null || observer == unit.Owner)
                return false;
            return !IsDetectedBy(unit, observer);
        }

        // ----------------------------------------------------- army-level roster helpers ----
        // Combat/movement/aviation still run at ArmyData granularity, so every place that
        // builds a roster/target list FOR AN OBSERVER must go through these instead of raw
        // army.Members / ArmyRegistry.AllAt.

        public static IEnumerable<UnitData> TargetableMembersFor(ArmyData army, PlayerSetupData observer)
        {
            if (army == null)
                yield break;
            foreach (UnitData member in army.Members)
                if (!IsHiddenFrom(member, observer))
                    yield return member;
        }

        public static bool HasAnyTargetableMember(ArmyData army, PlayerSetupData observer)
        {
            if (army == null)
                return false;
            foreach (UnitData member in army.Members)
                if (!IsHiddenFrom(member, observer))
                    return true;
            return false;
        }

        public static bool HasTargetableCombatMember(ArmyData army, PlayerSetupData observer)
        {
            if (army == null)
                return false;
            foreach (UnitData member in army.Members)
                if (!member.IsHero && !IsHiddenFrom(member, observer))
                    return true;
            return false;
        }

        // Every member hidden from `observer` — the army is entirely invisible to them, so it
        // can neither be contacted by them nor (as a mover) capture their building.
        public static bool ArmyFullyHiddenFrom(ArmyData army, PlayerSetupData observer)
        {
            if (army == null || army.Members.Count == 0)
                return false;
            foreach (UnitData member in army.Members)
                if (!IsHiddenFrom(member, observer))
                    return false;
            return true;
        }

        // -------------------------------------------------------- hidden-action restriction ----
        // A hidden unit itself takes no offensive action (§5). Mixed armies still act through
        // their VISIBLE members — this is per-unit, never "the whole army is frozen".

        public static bool CanActOffensively(UnitData unit) => unit == null || !unit.IsHidden;

        // ---------------------------------------------------------- the hidden challenge ----

        // stealthLevel + terrain bump (ordinary hex +0, move-cost 2 +1, cost 3 / mountains +2).
        public static int HideDiceFor(UnitData unit, HexCoord hex)
        {
            int level = AbilityParams.GetStealthLevel(unit);
            int bump = Math.Max(0, TerrainMoveCostProvider(hex) - 1);
            return level + bump;
        }

        // The MAX spot-die pool any ONE of `observer`'s vision sources brings to bear on
        // `hiddenHex` — observers never sum their dice (§2). An ordinary source has spot
        // strength 0 and contributes max(1, 0) = 1 IN ITS OWN HEX only; an r1sX source also
        // reaches an ADJACENT hex, contributing its raw spot strength there (so r1s0 -> 0,
        // i.e. it reveals the hex and ordinary units but never detects an adjacent stealth).
        // The winning vision source behind a SpotPoolAgainst result — carried purely so the
        // STEALTH diagnostic (§4 logging) can name WHICH of the observer's armies/buildings
        // actually did the spotting. Every non-diagnostic caller keeps using the plain int
        // overload below.
        public readonly struct SpotSource
        {
            public readonly int Pool;
            public readonly string Label;
            public SpotSource(int pool, string label) { Pool = pool; Label = label; }
            public static readonly SpotSource None = new SpotSource(0, "no source");
        }

        public static int SpotPoolAgainst(PlayerSetupData observer, HexCoord hiddenHex)
            => SpotPoolAgainst(observer, hiddenHex, out _);

        public static int SpotPoolAgainst(PlayerSetupData observer, HexCoord hiddenHex, out SpotSource best)
        {
            best = SpotSource.None;
            if (observer == null)
                return 0;
            int bestPool = 0;
            string bestLabel = "no source";

            foreach (ArmyData army in ArmyRegistry.AllForOwner(observer))
            {
                if (army.Members.Count == 0)
                    continue;
                // ArmyData.Hex only updates once a whole move finishes (ArmyRegistry.MoveArmy) —
                // mid-move the live per-step position is ArmyController.CurrentHex instead, same
                // read VisionSystem.RecomputeFor already uses. Matters for the per-step arrival
                // checks: a moving Recce source must challenge from where it actually is on THIS
                // step, not from its stale origin hex (project owner's own P1).
                HexCoord fromHex = army.Controller != null ? army.Controller.CurrentHex : army.Hex;
                int pool = SourcePool(fromHex, hiddenHex,
                    AbilityParams.GetBestRecceSpotStrength(army),
                    AbilityParams.GetBestRecceRadius(army) > 0);
                if (pool > bestPool)
                {
                    bestPool = pool;
                    bestLabel = $"army #{army.Id} \"{army.Name}\"";
                }
            }

            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != observer)
                    continue;
                int spot = AbilityParams.GetBestRecceSpotStrength(building.Abilities);
                bool hasRadius = AbilityParams.GetBestRecceRadius(building.Abilities) > 0;
                foreach (FacilityData facility in building.FacilitySlots)
                {
                    if (facility == null)
                        continue;
                    spot = Math.Max(spot, AbilityParams.GetBestRecceSpotStrength(facility.Abilities));
                    hasRadius |= AbilityParams.GetBestRecceRadius(facility.Abilities) > 0;
                }
                int pool = SourcePool(building.Hex, hiddenHex, spot, hasRadius);
                if (pool > bestPool)
                {
                    (int bcol, int brow) = building.Hex.ToOffset();
                    bestPool = pool;
                    bestLabel = $"building \"{building.Name}\" @ ({bcol}, {brow})";
                }
            }

            best = new SpotSource(bestPool, bestLabel);
            return bestPool;
        }

        private static int SourcePool(HexCoord sourceHex, HexCoord hiddenHex, int spotStrength, bool hasRadiusBonus)
        {
            if (sourceHex.Equals(hiddenHex))
                return Math.Max(1, spotStrength);
            if (hasRadiusBonus && HexGridMath.Distance(sourceHex, hiddenHex) == 1)
                return spotStrength;
            return 0;
        }

        // One silent hidden challenge for a single (unit, observer) pair. Returns true and
        // records a personal detection on success; a spot pool of 0 skips the roll entirely.
        // Callers own the "one challenge per pair per atomic event" dedupe (§3).
        public static bool ResolveDetection(UnitData unit, PlayerSetupData observer, HexCoord hex,
            string checkSource = null, ArmyData hiddenArmy = null)
        {
            if (unit == null || !unit.IsHidden || observer == null || observer == unit.Owner)
                return false;
            if (IsDetectedBy(unit, observer))
                return true; // already personally visible to this observer — no re-roll

            (int col, int row) = hex.ToOffset();
            int spot = SpotPoolAgainst(observer, hex, out SpotSource spotSource);
            // §4 diagnostics — name the trigger ("arrival" / "new vision" / "hidden action"),
            // the hidden unit (plus its army id where the caller knows it — UnitData has no id
            // of its own), and, for a rolled challenge, which observer source won the spot pool.
            string src = string.IsNullOrEmpty(checkSource) ? "unspecified" : checkSource;
            string hiddenId = hiddenArmy != null ? $"{unit.Name} (army #{hiddenArmy.Id})" : unit.Name;
            if (spot <= 0)
            {
                if (DebugLog)
                    Game.Ai.AiDebugLog.Write($"[STEALTH] check[{src}] {observer.Nickname} could not challenge hidden "
                        + $"{hiddenId} @ ({col}, {row}) — spot pool 0 (no source close enough / strong enough).");
                return false;
            }

            int hide = HideDiceFor(unit, hex);
            ChallengeResult result = ChallengeRoller(spot, hide);
            // Tie keeps stealth — strictly more spot successes than hide successes to detect.
            bool detected = result.AttackerSuccesses > result.DefenderSuccesses;

            if (DebugLog)
                Game.Ai.AiDebugLog.Write($"[STEALTH] check[{src}] {observer.Nickname} (via {spotSource.Label}) "
                    + $"vs hidden {hiddenId} @ ({col}, {row}): "
                    + $"spot {spot} ({result.AttackerSuccesses} hits) vs hide {hide} ({result.DefenderSuccesses} hits) "
                    + $"-> {(detected ? "DETECTED" : "still hidden")}");

            if (detected)
            {
                MarkDetected(unit, observer); // fires StealthChanged itself
                StealthChangedAt?.Invoke(hex);
                QueueDetectionNotice(observer, unit, hex);
            }
            return detected;
        }

        // Queues the detector-only "spotted X at H" line for `observer`'s next turn start.
        private static void QueueDetectionNotice(PlayerSetupData observer, UnitData unit, HexCoord hex)
        {
            if (!_detectionNotices.TryGetValue(observer, out List<string> notices))
            {
                notices = new List<string>();
                _detectionNotices[observer] = notices;
            }
            (int col, int row) = hex.ToOffset(); // player-facing (col, row), same as the aviation messages
            string name = string.IsNullOrEmpty(unit.Name) ? "an enemy unit" : unit.Name;
            notices.Add($"Hidden enemy detected: {name} at ({col}, {row}).");
        }

        // ------------------------------------------------------------------ trigger points ----
        // The ONLY three events that run checks (§3). Never on hex-menu open, army select,
        // map pan or round start.

        // A. An army finished arriving on `arrivalHex`. Both directions:
        //   - each hidden member of the moved army vs every enemy whose vision covers the hex;
        //   - each enemy hidden unit now inside the moved army's vision vs the mover's owner.
        //
        // `moveEventSeen` (2026-08-27, стелс §3) — a dedupe set the CALLER owns for the whole
        // movement event, keyed (hidden unit, observer, hex). A multi-hex order calls this once
        // per hex entered (HexSelectionController.Movement): for the mover's own hidden members
        // the hex is `arrivalHex`, which changes every step, so they're still challenged by every
        // observer along the route (deliberate — a hidden unit must not slip past a mid-route
        // observer). But the SECOND loop re-scans every enemy hidden unit the mover can see on
        // EVERY step; without an event-spanning set each of those (enemy unit, mover owner,
        // enemyHex) pairs got a fresh roll per step — several bites at one atomic event, against
        // §3. Passing the same set to all per-hex calls collapses those to one. Falls back to a
        // local per-call set (retreat landing, stealth-sim) when null.
        public static void RunChecksForArrival(ArmyData movedArmy, HexCoord arrivalHex,
            HashSet<(UnitData, PlayerSetupData, HexCoord)> moveEventSeen = null)
        {
            if (movedArmy?.Owner == null)
                return;
            var seen = moveEventSeen ?? new HashSet<(UnitData, PlayerSetupData, HexCoord)>();

            foreach (UnitData member in movedArmy.Members)
            {
                if (!member.IsHidden)
                    continue;
                foreach (PlayerSetupData observer in EnemiesWithVisionOf(movedArmy.Owner, arrivalHex))
                    if (seen.Add((member, observer, arrivalHex)))
                        ResolveDetection(member, observer, arrivalHex, "arrival", movedArmy);
            }

            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(movedArmy.Owner))
                foreach (ArmyData other in ArmyRegistry.AllAt(hex))
                {
                    if (other.Owner == null || other.Owner == movedArmy.Owner)
                        continue;
                    foreach (UnitData member in other.Members)
                        if (member.IsHidden && seen.Add((member, movedArmy.Owner, hex)))
                            ResolveDetection(member, movedArmy.Owner, hex, "arrival", other);
                }
        }

        // B. `owner` just played a card that created or widened a vision source. Every enemy
        //    hidden unit on a hex `owner` now sees (a base/citadel hex included) is checked
        //    against `owner`.
        public static void RunChecksForNewVisionSource(PlayerSetupData owner)
        {
            if (owner == null)
                return;
            var done = new HashSet<(UnitData, PlayerSetupData)>();
            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(owner))
                foreach (ArmyData other in ArmyRegistry.AllAt(hex))
                {
                    if (other.Owner == null || other.Owner == owner)
                        continue;
                    foreach (UnitData member in other.Members)
                        if (member.IsHidden && done.Add((member, owner)))
                            ResolveDetection(member, owner, hex, "new vision", other);
                }
        }

        // C. A hidden unit finished an active action from the shared hex action menu (a
        //    non-move action — moves go through A). Re-check that actor against every enemy
        //    observer of its hex.
        public static void RunChecksAfterHiddenUnitAction(UnitData actor, HexCoord hex, PlayerSetupData owner)
        {
            if (actor == null || !actor.IsHidden || owner == null)
                return;
            // EnemiesWithVisionOf already yields each player at most once, but keep an explicit
            // per-pair guard so the "one challenge per (hidden unit, observer) per event" rule
            // holds by construction here too (§4).
            var done = new HashSet<PlayerSetupData>();
            foreach (PlayerSetupData observer in EnemiesWithVisionOf(owner, hex))
                if (done.Add(observer))
                    ResolveDetection(actor, observer, hex, "hidden action");
        }

        private static IEnumerable<PlayerSetupData> EnemiesWithVisionOf(PlayerSetupData self, HexCoord hex)
        {
            foreach (PlayerSetupData player in GameSession.Players ?? new List<PlayerSetupData>())
                if (player != null && player != self && VisionSystem.IsVisible(player, hex))
                    yield return player;
        }

        private static void Notify(UnitData unit, HexCoord? hex)
        {
            StealthChanged?.Invoke();
            if (hex.HasValue)
                StealthChangedAt?.Invoke(hex.Value);
        }
    }
}
