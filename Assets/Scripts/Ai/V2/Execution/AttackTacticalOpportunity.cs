using System.Collections.Generic;
using System.Globalization;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // The one chosen side strike, or None. A pure VALUE: the selector below decides nothing about
    // the operation's identity and writes nothing anywhere — the caller either walks this waypoint
    // for exactly one step or ignores it.
    internal readonly struct AttackTacticalStrike
    {
        internal readonly bool HasValue;
        internal readonly HexCoord Hex;
        internal readonly int EnemyArmyId;
        internal readonly string EnemyName;
        internal readonly float EnemyRawStrength;
        internal readonly float ProjectedWinChance;
        // Route economics this pick was accepted on (§15), kept for the log line and the §18
        // ordering: cost to contact, extra cost the detour adds over the direct route, and the
        // cost still left from the contact hex to the main target.
        internal readonly int ContactCost;
        internal readonly int DetourCost;
        internal readonly int OnwardCost;

        internal AttackTacticalStrike(HexCoord hex, int enemyArmyId, string enemyName,
            float enemyRawStrength, float projectedWinChance,
            int contactCost, int detourCost, int onwardCost)
        {
            HasValue = true;
            Hex = hex;
            EnemyArmyId = enemyArmyId;
            EnemyName = enemyName;
            EnemyRawStrength = enemyRawStrength;
            ProjectedWinChance = projectedWinChance;
            ContactCost = contactCost;
            DetourCost = detourCost;
            OnwardCost = onwardCost;
        }

        internal static AttackTacticalStrike None => default;
    }

    // ===========================================================================================
    //  ATK §9-§18 — OPPORTUNISTIC ENEMY KILL INSIDE AN ATTACK STEP.
    //
    //  An Attack army marching on a Base/Citadel may destroy a weak enemy FIELD army it passes, if
    //  that fight practically does not delay the main operation. What this is NOT, spelled out
    //  because every one of these would be a different feature:
    //
    //    §9/§10  not a MissionIntent, not a MissionProposal, not a retarget. AttackIntent.Target
    //            stays the same Base before and after the strike; nothing here can write it.
    //    §13     not a second combat estimator. Safety is the existing shared
    //            WorthIt/GroundCombatFeasibility owner, at the same fresh-start gate a new fight
    //            has to clear anywhere else in the codebase.
    //    §14     not a capability shortage. A candidate that is too strong is IGNORED; a secondary
    //            tactical opportunity may never raise Production/Reinforcement Demand, and this
    //            file structurally cannot — it produces no proposal and no requirement, only a
    //            waypoint for the step that is already funded and already moving.
    //    §12     not an importance model. CompositionQuality, ability synergies, Hero skills,
    //            equipment effects and AiPower.EffectiveArmyPower are all deliberately unused;
    //            significance is the crude raw Attack+Defense scalar of combat bodies only.
    //
    //  It lives beside AttackExecutor because §9 makes it a tactical decision of the CURRENT step,
    //  re-taken from a fresh world every step, and not a planning-tier or continuity-tier concept.
    // ===========================================================================================
    internal static class AttackTacticalOpportunity
    {
        // `turn` is the game turn; `target` is the frozen Attack leg, whose OpportunisticStrikeTurn
        // carries the §17 once-per-turn marker Continuity stamped on the durable intent.
        internal static AttackTacticalStrike Select(PlayerSetupData player, HexMap map,
            WorldSnapshot snap, ArmyData army, AttackMissionTarget target, int turn)
        {
            if (player == null || map == null || snap?.Known == null || army == null
                || army.Members == null || army.Members.Count == 0)
                return AttackTacticalStrike.None;
            // Only the assault leg may divert. A reinforcement convoy, a walking-home primary and a
            // returning support container have no business hunting anything.
            if (target.Phase != AttackMissionPhase.Assault || !target.Target.HasValue)
                return AttackTacticalStrike.None;

            // §17 — at most ONE opportunistic strike per Attack per game turn, so the operation can
            // never degenerate into a hunt. The marker is turn-local state on the durable
            // AttackIntent, frozen into this leg by the mission layer; a struct default of 0 can
            // never match a real turn, which starts at 1.
            if (target.OpportunisticStrikeTurn == turn)
                return AttackTacticalStrike.None;

            HexCoord mainTarget = target.Target.Hex;
            int currentMovement = army.CurrentMovement;
            if (currentMovement <= 0)
                return AttackTacticalStrike.None;

            // §12 — our own side of the crude raw scalar: non-hero, non-aviation combat bodies.
            var ownBodies = new List<UnitData>();
            float ownRaw = 0f;
            foreach (UnitData u in army.Members)
            {
                if (!AiArmyRoles.IsGroundCombatBody(u))
                    continue;
                ownBodies.Add(u);
                ownRaw += u.Attack + u.Defense;
            }
            if (ownBodies.Count == 0)
                return AttackTacticalStrike.None;

            int directCost = SafeStepPathing.FindSafePathCost(map, army, mainTarget);
            if (directCost == int.MaxValue)
                // No honest route to the main objective at all — the assault step itself will fail
                // over this; a side fight cannot be judged "on the way" to nowhere.
                return AttackTacticalStrike.None;

            int maxMovement = Mathf.Max(1, army.MaxMovement);
            float significanceFloor = SignificanceFloor(ownRaw);

            // §13 — safety is the shared estimator over the army that will actually fight: its whole
            // roster (WorthIt keeps only ground combatants itself), never the §12 significance subset.
            List<WorthIt.DefenderProfile> attackers = BuildAttackerProfiles(army.Members);
            AttackTacticalStrike best = AttackTacticalStrike.None;

            foreach (AiMapMemory.KnownEnemySighting s in snap.Known.EnemySightings
                ?? (IReadOnlyList<AiMapMemory.KnownEnemySighting>)
                    System.Array.Empty<AiMapMemory.KnownEnemySighting>())
            {
                if (!Eligible(player, map, snap, army, target, s, mainTarget, currentMovement,
                        maxMovement, directCost, significanceFloor, ownRaw, attackers,
                        out AttackTacticalStrike candidate))
                    continue;
                if (Prefer(candidate, best))
                    best = candidate;
            }

            if (best.HasValue)
                AiDebugLog.Write($"[AI][V2][Attack][Tactical] decision=STRIKE "
                    + $"target={target.Target.DiagnosticLabel} enemy=#{best.EnemyArmyId} "
                    + $"({best.EnemyName}) at ({best.Hex.Q},{best.Hex.R}) raw={F(best.EnemyRawStrength)} "
                    + $"ownRaw={F(ownRaw)} win={F(best.ProjectedWinChance)} contact={best.ContactCost} "
                    + $"detour={best.DetourCost} onward={best.OnwardCost} mp={currentMovement}");
            return best;
        }

        // ---- §11 eligibility -------------------------------------------------------------------

        // Every condition of §11 in the order the spec lists them. A single failed condition means
        // IGNORE — there is deliberately no partial credit and no scoring inside this gate.
        private static bool Eligible(PlayerSetupData player, HexMap map, WorldSnapshot snap,
            ArmyData army, AttackMissionTarget target, AiMapMemory.KnownEnemySighting s,
            HexCoord mainTarget, int currentMovement, int maxMovement, int directCost,
            float significanceFloor, float ownRaw, List<WorthIt.DefenderProfile> attackers,
            out AttackTacticalStrike candidate)
        {
            candidate = AttackTacticalStrike.None;

            // enemy player, not Neutral, not ourselves, not an eliminated leftover
            if (s.Owner == null || s.Owner == player || s.Owner.IsNeutral || s.Owner.IsEliminated)
                return false;
            // a real body with a known roster — the fight has to be estimable at all
            if (s.MemberCount <= 0 || s.Defenders == null || s.Defenders.Count == 0)
                return false;
            // honestly observed THIS turn. A remembered last-known position is fine for strategy
            // but not for committing a marching army to a detour it cannot verify.
            if (s.SeenTurn < snap.TurnNumber)
                return false;
            if (s.Hex.Equals(army.Hex))
                return false;
            // not standing on the main objective — that is the assault, not a side strike (§11)
            if (s.Hex.Equals(mainTarget))
                return false;
            // not standing on ANY other known hostile structure (Base/Citadel/Facility): winning there
            // takes the structure, so that fight is a different strategic decision (a second Attack
            // objective), never a tactical detour (§11/§26).
            if (AttackObjectiveEvaluator.IsKnownHostileAttackSite(snap, player, s.Hex))
                return false;
            // not already the objective of a live ActiveDefence response (§11): that lane owns the
            // answer to this army, and Attack must not race it for the same kill.
            if (UnderActiveDefenceResponse(player, s.ArmyId))
                return false;

            // §12 — significance, on the crude raw scalar and nothing else.
            float enemyRaw = RawCombatBodyStrength(s.Defenders);
            if (enemyRaw < significanceFloor)
                return Reject(s, target, "not_significant", enemyRaw);
            // weaker than the Attack army, on the same scalar
            if (enemyRaw >= ownRaw)
                return Reject(s, target, "not_weaker", enemyRaw);

            // §15 — route economics on real safe-route costs, before the estimator is paid for.
            int contactCost = SafeStepPathing.FindSafePathCost(map, army, s.Hex);
            if (contactCost == int.MaxValue)
                return Reject(s, target, "unreachable", enemyRaw);
            // After the contact there must still BE an operation to continue: a real onward route
            // from the contact hex to the main target, with a real next step on it.
            HexPath onward = SafeStepPathing.FindSafePath(map, player, s.Hex, mainTarget, maxMovement);
            if (onward?.Hexes == null || onward.Hexes.Count < 2)
                return Reject(s, target, "no_route_onward", enemyRaw);
            int onwardCost = onward.TotalCost;
            if (!RouteEconomicsAllowDetour(directCost, contactCost, onwardCost,
                    currentMovement, maxMovement, StepCost(map, onward.Hexes[1]),
                    out string routeReason))
                return Reject(s, target, routeReason, enemyRaw);

            // §13 — safety LAST, through the one shared estimator, at the same fresh-start gate any
            // new fight has to clear. The defender's own hex bonus is a real property of the fight
            // and comes from the one fog-honest owner.
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, map, s.Hex);
            if (!GroundCombatFeasibility.Clears(attackers, WorthIt.SideCommander.Of(army.Commander),
                    new[] { new WorthIt.DefendingArmy(s.Defenders, s.Commander) },
                    GroundCombatAdmissionPolicy.FreshStartWinChanceGate, hexBonus,
                    out float win, out bool cover))
                return Reject(s, target, cover ? "win_chance_too_low" : "cannot_cover_defenders",
                    enemyRaw);

            candidate = new AttackTacticalStrike(s.Hex, s.ArmyId, s.Name, enemyRaw, win,
                contactCost, contactCost + onwardCost - directCost, onwardCost);
            return true;
        }

        // §12 — the raw-scalar bar a candidate must clear to be an army "worth attention": a real
        // share of our own raw strength, but never below an absolute floor that keeps a single
        // scrap unit from pulling a whole campaign off its axis.
        internal static float SignificanceFloor(float ownRaw) => Mathf.Max(
            AiConfigV2.attackTacticalOpportunityMinRawStrength,
            ownRaw * AiConfigV2.attackTacticalOpportunityMinStrengthShare);

        internal static float RawCombatBodyStrength(
            IReadOnlyList<WorthIt.DefenderProfile> roster)
        {
            float raw = 0f;
            if (roster == null)
                return raw;
            for (int i = 0; i < roster.Count; i++)
                if (AiArmyRoles.IsGroundCombatBody(roster[i]))
                    raw += roster[i].Attack + roster[i].Defense;
            return raw;
        }

        // ---- §15 route economics ---------------------------------------------------------------

        // The WHOLE "is this really on the way" answer, as pure arithmetic over real safe-route
        // costs, so it can be read — and exercised by the acceptance harness — without a world:
        //
        //   A = the army's hex, E = the candidate, B = the main Base/Citadel target
        //   directCost  = cost(A -> B)      contactCost  = cost(A -> E)
        //   onwardCost  = cost(E -> B)      nextStepCost = entry cost of the first hex of E -> B
        //
        // Three independent conditions, all from §15:
        //   1. ETA(A -> E -> B) <= ETA(A -> B) — the detour costs the operation no extra turn.
        //   2. contactCost < currentMovement, STRICTLY — contact is reachable this turn AND some
        //      movement budget is left afterwards. `<=` would authorise a strike that consumes the
        //      turn entirely, which is precisely the delay this gate exists to prevent.
        //   3. that leftover budget actually pays for the first onward step, so a legal
        //      continuation toward B exists rather than merely a route on paper.
        internal static bool RouteEconomicsAllowDetour(int directCost, int contactCost,
            int onwardCost, int currentMovement, int maxMovement, int nextStepCost,
            out string reason)
        {
            reason = null;
            if (directCost == int.MaxValue || contactCost == int.MaxValue
                || onwardCost == int.MaxValue)
            {
                reason = "unreachable";
                return false;
            }
            if (contactCost >= currentMovement)
            {
                reason = "not_reachable_this_turn";
                return false;
            }
            int move = Mathf.Max(1, maxMovement);
            if (AiV2Util.CeilDiv(contactCost + onwardCost, move)
                > AiV2Util.CeilDiv(directCost, move))
            {
                reason = "delays_operation";
                return false;
            }
            if (currentMovement - contactCost < Mathf.Max(1, nextStepCost))
            {
                reason = "no_affordable_next_step";
                return false;
            }
            return true;
        }

        // ---- §18 preference --------------------------------------------------------------------

        // Prefer the STRONGEST significant army that is still safe to destroy — the point of the
        // strike is to remove a noticeable slice of the enemy's field force as cheaply as possible,
        // so picking the weakest would be the wrong answer. Every later key is a tie-break, and the
        // last one is the stable ArmyId so the choice never depends on memory enumeration order.
        internal static bool Prefer(AttackTacticalStrike a, AttackTacticalStrike b)
        {
            if (!b.HasValue)
                return true;
            int c = b.EnemyRawStrength.CompareTo(a.EnemyRawStrength);
            if (c != 0) return c < 0;
            c = a.DetourCost.CompareTo(b.DetourCost);
            if (c != 0) return c < 0;
            c = a.ContactCost.CompareTo(b.ContactCost);
            if (c != 0) return c < 0;
            c = a.OnwardCost.CompareTo(b.OnwardCost);
            if (c != 0) return c < 0;
            return a.EnemyArmyId < b.EnemyArmyId;
        }

        // ---- internals -------------------------------------------------------------------------

        private static List<WorthIt.DefenderProfile> BuildAttackerProfiles(IReadOnlyList<UnitData> bodies)
        {
            var profiles = new List<WorthIt.DefenderProfile>(bodies.Count);
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i] != null)
                    profiles.Add(WorthIt.FromLiveUnit(bodies[i]));
            return profiles;
        }

        // §11 "urgent ActiveDefence target", read off the one intent store rather than re-deriving
        // a second urgency model: an ActiveDefence intent exists at all only for a real threat to a
        // real asset, and an ACTIVE (not suspended) one is precisely the case where that lane is
        // responding to this army right now.
        private static bool UnderActiveDefenceResponse(PlayerSetupData player, int enemyArmyId)
        {
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            return state != null
                && state.TryGet(MissionIntentKey.ForActiveDefence(enemyArmyId),
                    out MissionIntent intent)
                && intent?.ActiveDefence != null && intent.Status == IntentStatus.Active;
        }

        // What entering `step` actually charges a ground mover — the same per-hex terrain read the
        // movement owner itself applies (AiTurnController.FindAffordableStep). Aviation's flat
        // charge is irrelevant here: an Attack primary is a ground force by construction (§79) and
        // the non-aviation filter on its own bodies already guarantees it.
        private static int StepCost(HexMap map, HexCoord step)
        {
            map.TryGetTerrainAt(step, out TerrainTypeEntry entry);
            return entry != null ? Mathf.Max(1, entry.moveCost) : 1;
        }

        private static bool Reject(AiMapMemory.KnownEnemySighting s, AttackMissionTarget target,
            string reason, float enemyRaw)
        {
            AiDebugLog.WriteDeduped($"atk-tac-{target.Target.DiagnosticLabel}-{s.ArmyId}-{reason}",
                $"[AI][V2][Attack][Tactical] decision=IGNORE target={target.Target.DiagnosticLabel} "
                + $"enemy=#{s.ArmyId} at ({s.Hex.Q},{s.Hex.R}) raw={F(enemyRaw)} reason={reason}");
            return false;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
