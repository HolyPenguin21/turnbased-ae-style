using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // What the marching Attack army does about the field around it THIS step, besides marching on
    // its main target. A pure VALUE: deciding it writes nothing anywhere — the mission layer
    // freezes it into the funded leg (AttackMissionTarget.Local), the executor only verifies that
    // it still holds, and Continuity reads Retreat as the one trigger that sends the army home.
    internal enum AttackLocalActionKind
    {
        // March on the main target. Any contact on the way is passed or avoided, not hunted.
        Continue = 0,
        // Fight one known field army reachable with the movement left this turn.
        Intercept = 1,
        // Take an optional intermediate Base (AttackIntermediateBasePolicy) before going on.
        IntermediateBase = 2,
        // A relevant hostile army on the path that the army cannot beat: the attack goes home.
        Retreat = 3,
    }

    internal readonly struct AttackLocalAction
    {
        internal readonly AttackLocalActionKind Kind;
        // Intercept / Retreat: the contact hex. Continue with FightsPathContact: the contact on the path.
        internal readonly HexCoord Hex;
        internal readonly int EnemyArmyId;
        internal readonly string EnemyName;
        // Real route cost to the contact (not hex distance, not MaxMovement).
        internal readonly int ContactCost;
        // The shared estimator's chance against EVERYTHING that stands on the contact hex.
        internal readonly float WinChance;
        internal readonly bool CoversAllDefenders;
        // The intercepted army is a live ActiveDefence objective: Attack serves it, AD proposes no
        // competing ground answer.
        internal readonly bool ServesActiveDefence;
        // Continue only: a contact on the path that clears the voluntary-fight bar is fought on the
        // way (the step into it carries combat authority).
        internal readonly bool FightsPathContact;
        // Why this was chosen / rejected — a log and test fact, never parsed.
        internal readonly string Reason;
        // Retreat: the observed combat inputs of the contact (AttackTacticalOpportunity.ContactFingerprint).
        internal readonly int ContactFingerprint;
        internal readonly float EnemyPower;

        internal bool IsLocalFight => Kind == AttackLocalActionKind.Intercept || FightsPathContact;

        internal AttackLocalAction(AttackLocalActionKind kind, HexCoord hex, int enemyArmyId,
            string enemyName, int contactCost, float winChance, bool covers, string reason,
            bool servesActiveDefence = false, bool fightsPathContact = false,
            int contactFingerprint = 0, float enemyPower = 0f)
        {
            Kind = kind;
            Hex = hex;
            EnemyArmyId = enemyArmyId;
            EnemyName = enemyName;
            ContactCost = contactCost;
            WinChance = winChance;
            CoversAllDefenders = covers;
            Reason = reason;
            ServesActiveDefence = servesActiveDefence;
            FightsPathContact = fightsPathContact;
            ContactFingerprint = contactFingerprint;
            EnemyPower = enemyPower;
        }

        internal static AttackLocalAction Continue(string reason = null) =>
            new AttackLocalAction(AttackLocalActionKind.Continue, default, 0, null, 0, 0f, false, reason);
    }

    // ===========================================================================================
    //  ATTACK LOCAL ACTION — ONE ONE-DAY DECISION FOR A MARCHING ATTACK ARMY (2026-10-08).
    //
    //  An Attack army that has begun its march keeps its main Base / Citadel as the operation
    //  identity (AttackIntent.Target never changes here) and decides, from honest knowledge only,
    //  what to do about the field around it. Priority, strongest first:
    //
    //    0. Retreat   a relevant hostile army on the path that it cannot beat
    //                 (estimator chance below AiConfigV2.attackLocalMinWinChance) — the attack ends
    //                 and the army walks home (Continuity: RecoveryReturn).
    //    1. Continue  the main target is reachable with the movement left now: go there.
    //    2. Intercept an ActiveDefence objective, reachable now, that clears the bar.
    //    3. IntermediateBase  an optional, observed, winnable Base on the way (planner-provided).
    //    4. Intercept any other significant known field army, reachable now, that clears the bar.
    //    5. Continue  march on.
    //
    //  The bar for a voluntary fight is chance >= 0.40 AND coverage (every known defender can be
    //  damaged by at least one of our bodies) — through the one shared GroundCombatFeasibility,
    //  against the WHOLE package on the contact hex with its hex bonus. Reachability is the real
    //  route cost against the movement left (never hex distance, never MaxMovement). A hunt is
    //  structurally impossible: a contact that needs more than today's movement is not chosen, and
    //  one voluntary fight per turn (the marker Continuity stamps on the intent).
    //
    //  Not a second estimator, not a mission, not a demand: it produces a value, never a proposal.
    // ===========================================================================================
    internal static class AttackTacticalOpportunity
    {
        // `main` is the operation's Base/Citadel. `lastLocalTurn` is the once-per-turn marker of
        // voluntary fights. `intermediateBaseAvailable` is the mission layer's already-proven
        // optional Base (AttackIntermediateBasePolicy.Select). `adObjectives` are the live
        // ActiveDefence objectives of this snapshot (null: none).
        internal static AttackLocalAction Decide(WorldSnapshot snap, ArmySnapshot army,
            AttackTargetRef main, int lastLocalTurn, bool intermediateBaseAvailable,
            IReadOnlyList<ActiveDefenceObjective> adObjectives)
        {
            if (snap?.Known == null || army == null || army.Owner == null
                || !main.HasValue || army.Members == null || army.Members.Count == 0)
                return AttackLocalAction.Continue("no_state");
            PlayerSetupData player = army.Owner;
            HexMap map = snap.Map;
            int turn = snap.TurnNumber;
            int currentMovement = army.CurrentMovement;
            int maxMovement = Mathf.Max(1, army.MaxMovement);
            HexCoord mainHex = main.Hex;

            List<WorthIt.DefenderProfile> attackers = army.Members.ToList();
            if (!attackers.Any(AiArmyRoles.IsGroundCombatBody))
                return AttackLocalAction.Continue("no_ground_body");

            // Geometric route to the main target: terrain alone (SafeRouteProfile.Attack). A hex
            // that costs more than MaxMovement to enter is impassable; a short current MP is only
            // waiting. Contacts are judged below, they do not make the route "not exist".
            HexPath route = RouteOf(snap, player, army.Hex, mainHex, maxMovement, SafeRouteProfile.Attack);
            if (route?.Hexes == null || route.Hexes.Count < 2)
                return AttackLocalAction.Continue("main_unreachable_terrain");

            // ---- mandatory: a relevant contact on the path ------------------------------------
            // Independent of the voluntary-fight limit: a winnable contact is fought on the way, an
            // unwinnable hostile army ends the attack, anything else is bypassed. Significance
            // gates voluntary detours only, never an enemy already on this route.
            // Relevant = on the nearest path and within the movement window of today and one full
            // turn more — never a far, arbitrary army.
            int window = currentMovement + maxMovement;
            AttackLocalAction pathFight = default;
            bool hasPathFight = false;
            bool pathContactBypassed = false;
            int cumulative = 0;
            for (int i = 1; i < route.Hexes.Count; i++)
            {
                HexCoord h = route.Hexes[i];
                cumulative += StepCost(map, h);
                if (cumulative > window || h.Equals(mainHex))
                    break;
                List<AiMapMemory.KnownEnemySighting> bodies = KnownBodiesOn(snap, player, h);
                if (bodies.Count == 0)
                    continue;
                List<WorthIt.DefendingArmy> opposition = OppositionOn(snap, h);
                float bonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, map, h);
                bool fightable = ClearsContact(attackers, army.Commander, opposition,
                    bonus, out float win, out bool cover);
                AiMapMemory.KnownEnemySighting? army0 = HostileFieldArmyOn(player, bodies);
                if (!fightable)
                {
                    if (army0.HasValue && win < GroundCombatAdmissionPolicy.AttackLocalWinChanceGate)
                    {
                        AiMapMemory.KnownEnemySighting e = army0.Value;
                        return new AttackLocalAction(AttackLocalActionKind.Retreat, h, e.ArmyId, e.Name,
                            cumulative, win, cover, "hostile_contact_below_threshold",
                            contactFingerprint: ContactFingerprint(opposition),
                            enemyPower: AiPower.EffectiveArmyPowerFromProfiles(WorthIt.UnitsOf(opposition), bonus));
                    }
                    // Not a retreat: a neutral contact, or one the estimator likes
                    // but we cannot cover. The two stay distinct in the log.
                    pathContactBypassed = true;
                    AiDebugLog.WriteDeduped($"atk-path-{main.DiagnosticLabel}-{h.Q},{h.R}-{cover}",
                        $"[AI][V2][Attack][Contact] decision=BYPASS target={main.DiagnosticLabel} "
                        + $"at ({h.Q},{h.R}) win={F(win)} cover={(cover ? 1 : 0)} reason="
                        + (win >= GroundCombatAdmissionPolicy.AttackLocalWinChanceGate && !cover
                            ? "no_coverage" : "below_threshold"));
                    continue;
                }
                AiMapMemory.KnownEnemySighting lead = bodies[0];
                pathFight = new AttackLocalAction(AttackLocalActionKind.Continue, h, lead.ArmyId, lead.Name,
                    cumulative, win, cover, "fight_contact_on_path", fightsPathContact: true);
                hasPathFight = true;
                break;
            }

            // ---- 1. the main target is reachable with today's movement ------------------------
            int mainCost = route.TotalCost;
            bool reachableNow = mainCost <= currentMovement;
            if (hasPathFight)
                return pathFight;
            if (pathContactBypassed)
            {
                // The straight path is spoiled by a contact we will not fight: reachability is that
                // of the route that goes around it (Combat profile blocks every remembered army).
                int around = RouteOf(snap, player, army.Hex, mainHex, maxMovement,
                    SafeRouteProfile.Combat)?.TotalCost ?? int.MaxValue;
                reachableNow = around != int.MaxValue && around <= currentMovement;
            }
            if (reachableNow)
                return AttackLocalAction.Continue("main_target_reachable_now");

            // ---- voluntary fights: one per turn ------------------------------------------------
            if (lastLocalTurn == turn || currentMovement <= 0)
                return AttackLocalAction.Continue(lastLocalTurn == turn ? "local_fight_spent_this_turn" : "no_movement");

            List<AttackLocalAction> candidates = InterceptCandidates(snap, army, attackers, mainHex,
                currentMovement, maxMovement, adObjectives);
            AttackLocalAction adBest = candidates.FirstOrDefault(c => c.ServesActiveDefence);
            if (adBest.Kind == AttackLocalActionKind.Intercept)
                return adBest;
            if (intermediateBaseAvailable)
                return new AttackLocalAction(AttackLocalActionKind.IntermediateBase, default, 0, null, 0, 0f,
                    false, "intermediate_base_available");
            AttackLocalAction ordinary = candidates.FirstOrDefault();
            return ordinary.Kind == AttackLocalActionKind.Intercept
                ? ordinary : AttackLocalAction.Continue("no_local_target");
        }

        // The execution-side check of a frozen decision. The frozen choice is never replaced by a
        // different enemy: an Intercept holds only while the SAME army is still the chosen one;
        // otherwise the step is skipped and the mission layer re-plans. Anything the world newly
        // makes mandatory (a Retreat, a winnable contact on the path) is taken from `now`.
        internal static AttackLocalAction ForExecution(AttackLocalAction frozen, AttackLocalAction now,
            out string rejection)
        {
            rejection = null;
            if (now.Kind == AttackLocalActionKind.Retreat)
            {
                rejection = "hostile_contact_now_unwinnable";
                return now;
            }
            if (frozen.Kind == AttackLocalActionKind.Intercept)
            {
                if (now.Kind == AttackLocalActionKind.Intercept && now.EnemyArmyId == frozen.EnemyArmyId)
                    return now;
                rejection = "chosen_contact_no_longer_valid";
                return frozen;
            }
            // A step planned as a plain march never jumps to a voluntary target after funding.
            if (now.Kind == AttackLocalActionKind.Intercept || now.Kind == AttackLocalActionKind.IntermediateBase)
                return AttackLocalAction.Continue("voluntary_target_not_funded");
            return now;
        }

        // ---- candidates ---------------------------------------------------------------------------

        private static List<AttackLocalAction> InterceptCandidates(WorldSnapshot snap, ArmySnapshot army,
            List<WorthIt.DefenderProfile> attackers, HexCoord mainHex, int currentMovement,
            int maxMovement, IReadOnlyList<ActiveDefenceObjective> adObjectives)
        {
            var found = new List<AttackLocalAction>();
            PlayerSetupData player = army.Owner;
            foreach (AiMapMemory.KnownEnemySighting s in snap.Known.EnemySightings
                ?? (IReadOnlyList<AiMapMemory.KnownEnemySighting>)
                    System.Array.Empty<AiMapMemory.KnownEnemySighting>())
            {
                if (s.Owner == null || s.Owner == player || s.Owner.IsNeutral || s.Owner.IsEliminated
                    || s.IsGarrison || s.IsAir)
                    continue;
                if (s.MemberCount <= 0 || s.Defenders == null || s.Defenders.Count == 0)
                    continue;
                if (s.Hex.Equals(army.Hex) || s.Hex.Equals(mainHex))
                    continue;
                // A fight on a known foreign structure could destroy it: that is Attack's own
                // target business, never a side fight.
                if (ActiveDefenceObjectiveEvaluator.OnKnownForeignStructure(snap, s.Hex))
                    continue;
                // An ActiveDefence Intercept already answering this army owns it.
                if (UnderActiveDefenceResponse(player, s.ArmyId))
                    continue;
                int adIndex = IndexOfObjective(adObjectives, s.ArmyId);
                List<WorthIt.DefendingArmy> opposition = OppositionOn(snap, s.Hex);
                float power = AiPower.EffectiveArmyPowerFromProfiles(WorthIt.UnitsOf(opposition), 0f);
                if (adIndex < 0 && !ActiveDefenceObjectiveEvaluator.IsSignificantHostilePower(
                        AiPower.EffectiveArmyPowerFromProfiles(s.Defenders, 0f)))
                    continue;
                // Real route cost to the contact (the contact hex itself is the exempt endpoint),
                // against the movement left: more than that is a multi-day chase.
                int contactCost = RouteOf(snap, player, army.Hex, s.Hex, maxMovement,
                    SafeRouteProfile.Combat)?.TotalCost ?? int.MaxValue;
                if (contactCost == int.MaxValue || contactCost > currentMovement)
                    continue;
                float bonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, snap.Map, s.Hex);
                if (!ClearsContact(attackers, army.Commander, opposition,
                        bonus, out float win, out bool cover))
                {
                    AiDebugLog.WriteDeduped($"atk-int-{army.ArmyId}-{s.ArmyId}-{cover}",
                        $"[AI][V2][Attack][Local] decision=IGNORE enemy=#{s.ArmyId} at ({s.Hex.Q},{s.Hex.R}) "
                        + $"win={F(win)} cover={(cover ? 1 : 0)} reason="
                        + (win >= GroundCombatAdmissionPolicy.AttackLocalWinChanceGate
                            ? "cannot_cover_defenders" : "win_chance_too_low"));
                    continue;
                }
                found.Add(new AttackLocalAction(AttackLocalActionKind.Intercept, s.Hex, s.ArmyId, s.Name,
                    contactCost, win, cover, adIndex >= 0 ? "serves_active_defence" : "ordinary_intercept",
                    servesActiveDefence: adIndex >= 0, enemyPower: power));
            }
            found.Sort((a, b) => Compare(a, b, adObjectives));
            return found;
        }

        // Several equally valid targets: ActiveDefence's own significance order first, then the
        // observed strength of the package, then the cheaper contact, then the stable ArmyId.
        // Never random, never dependent on the order sightings were enumerated.
        internal static int Compare(AttackLocalAction a, AttackLocalAction b,
            IReadOnlyList<ActiveDefenceObjective> adObjectives)
        {
            int c = b.ServesActiveDefence.CompareTo(a.ServesActiveDefence);
            if (c != 0) return c;
            if (a.ServesActiveDefence)
            {
                c = IndexOfObjective(adObjectives, a.EnemyArmyId).CompareTo(IndexOfObjective(adObjectives, b.EnemyArmyId));
                if (c != 0) return c;
            }
            c = b.EnemyPower.CompareTo(a.EnemyPower);
            if (c != 0) return c;
            c = a.ContactCost.CompareTo(b.ContactCost);
            if (c != 0) return c;
            return a.EnemyArmyId.CompareTo(b.EnemyArmyId);
        }

        private static int IndexOfObjective(IReadOnlyList<ActiveDefenceObjective> objectives, int enemyArmyId)
        {
            if (objectives == null)
                return -1;
            for (int i = 0; i < objectives.Count; i++)
                if (objectives[i]?.Target.EnemyArmyId == enemyArmyId)
                    return i;
            return -1;
        }

        // ---- knowledge ----------------------------------------------------------------------------

        // Every remembered contact is evaluated from its last observed roster, whether visible
        // now or in FoW. Re-observation corrects memory before the next settled-step snapshot;
        // no hidden live position, composition or HP is consulted here.
        private static List<AiMapMemory.KnownEnemySighting> KnownBodiesOn(WorldSnapshot snap,
            PlayerSetupData player, HexCoord hex)
        {
            var bodies = new List<AiMapMemory.KnownEnemySighting>();
            foreach (AiMapMemory.KnownEnemySighting s in (snap.Known.EnemySightings
                ?? (IReadOnlyList<AiMapMemory.KnownEnemySighting>)System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings
                    ?? (IReadOnlyList<AiMapMemory.KnownEnemySighting>)System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .OrderBy(x => x.ArmyId))
                if (s.Hex.Equals(hex) && s.Owner != player && s.Defenders != null && s.Defenders.Count > 0)
                    bodies.Add(s);
            if (bodies.Count == 0 && snap.Known.EventGuards != null)
                foreach (KnownEventGuardSnapshot g in snap.Known.EventGuards)
                    if (g.Hex.Equals(hex) && g.Defenders != null && g.Defenders.Count > 0)
                    {
                        bodies.Add(new AiMapMemory.KnownEnemySighting(hex, null, g.Name ?? "guard",
                            g.Defenders.Count, 0f, 0f, g.Defenders, seenTurn: snap.TurnNumber, armyId: 0));
                        break;
                    }
            return bodies;
        }

        // The whole fight on that hex, as the game resolves it: every known army there, its own
        // battle with its own commander and its own hex bonus (WorthIt.EstimateSequential), plus
        // the event guard standing on it. NOT a single enemy picked out of the stack.
        internal static List<WorthIt.DefendingArmy> OppositionOn(WorldSnapshot snap, HexCoord hex)
        {
            var result = new List<WorthIt.DefendingArmy>();
            if (snap?.Known == null)
                return result;
            if (snap.Known.EventGuards != null)
                foreach (KnownEventGuardSnapshot g in snap.Known.EventGuards)
                    if (g.Hex.Equals(hex) && g.Defenders != null && g.Defenders.Count > 0)
                    {
                        result.Add(new WorthIt.DefendingArmy(g.Defenders, g.Commander,
                            AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, hex, defendingOwner: null)));
                        break;
                    }
            foreach (AiMapMemory.KnownEnemySighting s in (snap.Known.EnemySightings
                ?? (IReadOnlyList<AiMapMemory.KnownEnemySighting>)System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings
                    ?? (IReadOnlyList<AiMapMemory.KnownEnemySighting>)System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .OrderBy(x => x.ArmyId))
                if (s.Hex.Equals(hex) && s.Owner != snap.Observer && s.Defenders != null && s.Defenders.Count > 0)
                    result.Add(new WorthIt.DefendingArmy(s.Defenders, s.Commander,
                        AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, hex, s.Owner), s.ArmyId));
            return result;
        }

        // The enemy PLAYER's mobile army among the bodies on a hex (a building-bound garrison and a
        // neutral are not "a hostile army" for the retreat rule), strongest roster first.
        private static AiMapMemory.KnownEnemySighting? HostileFieldArmyOn(PlayerSetupData player,
            List<AiMapMemory.KnownEnemySighting> bodies)
        {
            AiMapMemory.KnownEnemySighting? best = null;
            foreach (AiMapMemory.KnownEnemySighting s in bodies)
            {
                if (s.Owner == null || s.Owner == player || s.Owner.IsNeutral || s.Owner.IsEliminated
                    || s.IsGarrison || s.IsAir)
                    continue;
                if (!best.HasValue || s.MemberCount > best.Value.MemberCount
                    || s.MemberCount == best.Value.MemberCount && s.ArmyId < best.Value.ArmyId)
                    best = s;
            }
            return best;
        }

        // The local-contact gate of the one ground-combat check. A standalone hero is priced as a
        // Capture/Kill inside the estimator itself (WorthIt), so this needs no hero rule of its own.
        internal static bool ClearsContact(IReadOnlyList<WorthIt.DefenderProfile> attackers,
            WorthIt.SideCommander commander, IReadOnlyList<WorthIt.DefendingArmy> opposition,
            float bonus, out float win, out bool cover) =>
            GroundCombatFeasibility.Clears(attackers, commander, opposition,
                GroundCombatAdmissionPolicy.AttackLocalWinChanceGate, bonus, out win, out cover,
                GroundCombatAdmissionPolicy.AttackLocalArmyRequiresCoverage);

        internal static int ContactFingerprint(IReadOnlyList<WorthIt.DefendingArmy> opposition)
        {
            unchecked
            {
                int hash = CombatFingerprint(WorthIt.UnitsOf(opposition));
                foreach (WorthIt.DefendingArmy army in opposition)
                {
                    hash = hash * 31 + (army.Commander.Present ? 1 : 0);
                    hash = hash * 31 + army.Commander.Initiative;
                    hash = hash * 31 + army.Commander.Fate;
                    foreach (WorthIt.DefenderProfile profile in army.Units)
                        if (profile.IsHero) hash = hash * 31 + profile.FateMax;
                }
                return hash;
            }
        }

        // The inputs of the fight that decide who wins, folded into one number: roster, HP, armour
        // and initiative per body. Two reads of the same contact in the same state agree; any
        // visible change (a wounded body, a new one, equipment) changes it.
        internal static int CombatFingerprint(IReadOnlyList<WorthIt.DefenderProfile> roster)
        {
            unchecked
            {
                int hash = 17;
                if (roster == null)
                    return hash;
                // Order-free: the same stack listed in a different order is the same fight.
                int sum = 0, xor = 0;
                for (int i = 0; i < roster.Count; i++)
                {
                    WorthIt.DefenderProfile p = roster[i];
                    int h = (int)System.Math.Round(p.Attack * 10f) * 31
                        + (int)System.Math.Round(p.Defense * 10f) * 131
                        + (int)System.Math.Round(p.HitPoints * 10f) * 1031
                        + p.Initiative * 10007 + (p.HasCeramicArmor ? 7 : 0) + (p.IsHero ? 13 : 0)
                        + (p.Abilities?.Count ?? 0) * 100003;
                    sum += h;
                    xor ^= h * 397;
                }
                hash = hash * 31 + roster.Count;
                hash = hash * 31 + sum;
                return hash * 31 + xor;
            }
        }

        // ---- the old strike's pure helpers that other tools still read -----------------------------

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

        // ---- internals -------------------------------------------------------------------------

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
        // movement owner itself applies (AiTurnController.FindAffordableStep).
        private static int StepCost(HexMap map, HexCoord step)
        {
            if (map == null)
                return 1;
            map.TryGetTerrainAt(step, out TerrainTypeEntry entry);
            return entry != null ? Mathf.Max(1, entry.moveCost) : 1;
        }

        // The ground route the decision reasons on. A snapshot without a map (the synthetic analysis
        // fixtures; the live Scan always supplies one) falls back to a straight line, one cost
        // point per hex, exactly as AiV2Util.TravelRoute and AttackIntermediateBasePolicy.Route do.
        private static HexPath RouteOf(WorldSnapshot snap, PlayerSetupData player, HexCoord from,
            HexCoord to, int maxMovement, SafeRouteProfile profile) =>
            snap.Map == null
                ? AiV2Util.StraightLine(from, to)
                : SafeStepPathing.FindSafePath(snap.Map, player, from, to, maxMovement, profile);

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
