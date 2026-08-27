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
    //   _detected[unit][observer]  — the observer's CompletedTurnsFor(observer) snapshot at
    //                                the moment a hidden challenge succeeded. The detection
    //                                is live while CompletedTurnsProvider(observer) <= that
    //                                snapshot, i.e. through the end of the observer's next
    //                                own turn (see the design's acceptance example — this is
    //                                NOT TurnNumber+1, turn order is re-rolled each round).
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

        // Terrain move cost at a hex — hideDice = stealthLevel + (moveCost - 1). Set by
        // whoever owns the HexMap at setup; default flat 1 (no terrain bump) for the sim.
        public static Func<HexCoord, int> TerrainMoveCostProvider = _ => 1;

        // Opt-in developer diagnostic only (project owner's spec §3/§9) — never a player- or
        // owner-facing signal. Off by default.
        public static bool DebugLog;

        private static readonly Dictionary<UnitData, Dictionary<PlayerSetupData, long>> _detected
            = new Dictionary<UnitData, Dictionary<PlayerSetupData, long>>();

        // Fired whenever a unit's hidden/detected state changes so UI rosters/markers,
        // AiMapMemory and VisionSystem content notifications rebuild. StealthChangedAt carries
        // the affected hex where one is known (for a targeted content refresh).
        public static event Action StealthChanged;
        public static event Action<HexCoord> StealthChangedAt;

        public static void Clear()
        {
            _detected.Clear();
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
                || !byObserver.TryGetValue(observer, out long snapshot))
                return false;
            if (CompletedTurnsProvider(observer) <= snapshot)
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
            byObserver[observer] = CompletedTurnsProvider(observer);
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
                if (pair.Value.TryGetValue(observer, out long snapshot) && now > snapshot)
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
        public static int SpotPoolAgainst(PlayerSetupData observer, HexCoord hiddenHex)
        {
            if (observer == null)
                return 0;
            int best = 0;

            foreach (ArmyData army in ArmyRegistry.AllForOwner(observer))
            {
                if (army.Members.Count == 0)
                    continue;
                best = Math.Max(best, SourcePool(army.Hex, hiddenHex,
                    AbilityParams.GetBestRecceSpotStrength(army),
                    AbilityParams.GetBestRecceRadius(army) > 0));
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
                best = Math.Max(best, SourcePool(building.Hex, hiddenHex, spot, hasRadius));
            }

            return best;
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
        public static bool ResolveDetection(UnitData unit, PlayerSetupData observer, HexCoord hex)
        {
            if (unit == null || !unit.IsHidden || observer == null || observer == unit.Owner)
                return false;
            if (IsDetectedBy(unit, observer))
                return true; // already personally visible to this observer — no re-roll

            int spot = SpotPoolAgainst(observer, hex);
            if (spot <= 0)
                return false;

            int hide = HideDiceFor(unit, hex);
            ChallengeResult result = ChallengeResolver.Resolve(spot, hide);
            // Tie keeps stealth — strictly more spot successes than hide successes to detect.
            bool detected = result.AttackerSuccesses > result.DefenderSuccesses;

            if (DebugLog)
                Game.Ai.AiDebugLog.Write($"[STEALTH] {observer.Nickname} vs hidden {unit.Name} @ ({hex.Q},{hex.R}): "
                    + $"spot {spot} ({result.AttackerSuccesses}) vs hide {hide} ({result.DefenderSuccesses}) "
                    + $"-> {(detected ? "DETECTED" : "still hidden")}");

            if (detected)
            {
                MarkDetected(unit, observer);
                StealthChangedAt?.Invoke(hex);
                StealthChanged?.Invoke();
            }
            return detected;
        }

        // ------------------------------------------------------------------ trigger points ----
        // The ONLY three events that run checks (§3). Never on hex-menu open, army select,
        // map pan or round start.

        // A. An army finished arriving on `arrivalHex`. Both directions:
        //   - each hidden member of the moved army vs every enemy whose vision covers the hex;
        //   - each enemy hidden unit now inside the moved army's vision vs the mover's owner.
        public static void RunChecksForArrival(ArmyData movedArmy, HexCoord arrivalHex)
        {
            if (movedArmy?.Owner == null)
                return;
            var done = new HashSet<(UnitData, PlayerSetupData)>();

            foreach (UnitData member in movedArmy.Members)
            {
                if (!member.IsHidden)
                    continue;
                foreach (PlayerSetupData observer in EnemiesWithVisionOf(movedArmy.Owner, arrivalHex))
                    if (done.Add((member, observer)))
                        ResolveDetection(member, observer, arrivalHex);
            }

            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(movedArmy.Owner))
                foreach (ArmyData other in ArmyRegistry.AllAt(hex))
                {
                    if (other.Owner == null || other.Owner == movedArmy.Owner)
                        continue;
                    foreach (UnitData member in other.Members)
                        if (member.IsHidden && done.Add((member, movedArmy.Owner)))
                            ResolveDetection(member, movedArmy.Owner, hex);
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
                            ResolveDetection(member, owner, hex);
                }
        }

        // C. A hidden unit finished an active action from the shared hex action menu (a
        //    non-move action — moves go through A). Re-check that actor against every enemy
        //    observer of its hex.
        public static void RunChecksAfterHiddenUnitAction(UnitData actor, HexCoord hex, PlayerSetupData owner)
        {
            if (actor == null || !actor.IsHidden || owner == null)
                return;
            foreach (PlayerSetupData observer in EnemiesWithVisionOf(owner, hex))
                ResolveDetection(actor, observer, hex);
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
