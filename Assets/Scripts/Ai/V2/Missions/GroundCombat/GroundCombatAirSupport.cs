using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Ai;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // One way an already-fielded free wing can support an existing ground-combat task (Attack,
    // Raid, ActiveDefence): fly to the task's target, strike it over one or more of its own turns,
    // fly home. It is a technical assignment serving that task, never a strategic goal of its own.
    internal readonly struct AirSupportOption
    {
        internal readonly int WingArmyId;
        internal readonly HexCoord LandingHex;
        internal readonly int EtaTurns;
        internal readonly int FirstStrikeEta;
        // Strike turns the series can deliver before it must land (AviationRange.StrikeTurns).
        internal readonly int StrikeTurns;
        // Expected damage of the whole series against the KNOWN roster; 0 when the roster is
        // unknown (RosterKnown false) — never a fictitious fight against an empty roster.
        internal readonly float ExpectedDamage;
        internal readonly bool RosterKnown;
        // The lane's own win read after the expected series (== current when the roster is unknown).
        internal readonly float WinAfter;
        // The sortie launch now (0 / 0 when the wing already flies a paid sortie).
        internal readonly float Ap;
        // Always 0: continuing a paid sortie costs nothing on later turns.
        internal readonly float RecurringAp;
        internal readonly ResourceVector Resources;

        internal AirSupportOption(int wingArmyId, HexCoord landingHex, int etaTurns,
            int firstStrikeEta, int strikeTurns, float expectedDamage, bool rosterKnown,
            float winAfter, float ap, ResourceVector resources)
        {
            WingArmyId = wingArmyId;
            LandingHex = landingHex;
            EtaTurns = etaTurns;
            FirstStrikeEta = firstStrikeEta;
            StrikeTurns = strikeTurns;
            ExpectedDamage = expectedDamage;
            RosterKnown = rosterKnown;
            WinAfter = winAfter;
            Ap = ap;
            RecurringAp = 0f;
            Resources = resources;
        }
    }

    // A resolved wing and its current sortie state, for the provisioning half below.
    internal readonly struct AirSupportWing
    {
        internal readonly ArmyData Wing;
        internal readonly AirSortie Active;
        // An existing strike sortie this lane already flies (outbound or holding over the target).
        internal readonly bool Continuing;

        internal AirSupportWing(ArmyData wing, AirSortie active, bool continuing)
        {
            Wing = wing;
            Active = active;
            Continuing = continuing;
        }
    }

    // ===========================================================================================
    //  THE ONE AIR SUPPORT OF A GROUND-COMBAT TASK — shared by Raid (a recovery option of its
    //  AirSupport phase), Attack (a side leg beside the assault) and ActiveDefence (a separate
    //  AirSupport intent of the same threat). Each lane keeps its own target identity, win read and
    //  strike policy; wing choice, the strike-series calendar and estimate, landing base, leg
    //  requirements, sortie provisioning and the flight cycle exist once.
    //
    //  Admission is the existing task plus a free wing, a recoverable route and the launch cost
    //  fitting the free bank — no arrival window, no intel age, no minimum win gain. A known roster
    //  only ranks the options (and rejects a wing that physically cannot damage it).
    // ===========================================================================================
    internal static partial class GroundCombatAirSupport
    {
        internal static bool WingLanded(PlayerSetupData player, int? armyId) =>
            armyId.HasValue && AiV2Util.ResolveArmy(player, armyId.Value)?.AirWingLanded == true;

        // Absence is evidence only after a fresh observation reconciled the aviation target pool.
        // Fogged or stale empty collections never suppress a blind support sortie.
        internal static bool TargetKnownEmpty(WorldSnapshot snap, HexCoord hex, AirStrikePolicy policy) =>
            snap?.Known?.AirSightings != null
            && ReconIntelSnapshotRegistry.TryGetLastObservedTurn(snap, hex, out int observed)
            && observed == snap.TurnNumber
            && KnownAirTargets(snap, hex, policy).Sum(s => s.Roster.Units.Count) <= policy.MinimumSurvivors;

        private static List<AiMapMemory.KnownAirSighting> AirTargets(
            IEnumerable<AiMapMemory.KnownAirSighting> sightings, PlayerSetupData observer,
            HexCoord hex, AirStrikePolicy policy) =>
            (sightings ?? Array.Empty<AiMapMemory.KnownAirSighting>())
                .Where(s => s.Hex.Equals(hex) && s.Owner != observer
                    && (!policy.ExactTargetArmyId.HasValue || s.ArmyId == policy.ExactTargetArmyId.Value))
                .OrderBy(s => s.ArmyId).ToList();

        internal static List<AiMapMemory.KnownAirSighting> KnownAirTargets(WorldSnapshot snap,
            HexCoord hex, AirStrikePolicy policy) => AirTargets(snap?.Known?.AirSightings, snap?.Observer, hex, policy);

        internal static int KnownTargetCount(WorldSnapshot snap, HexCoord hex, AirStrikePolicy policy,
            IReadOnlyList<WorthIt.DefendingArmy> legacyOpposition) =>
            snap?.Known?.AirSightings != null
                ? KnownAirTargets(snap, hex, policy).Sum(s => s.Roster.Units.Count)
                : WorthIt.UnitsOf(legacyOpposition).Count;

        internal static bool TargetKnownEmpty(PlayerSetupData player, int turn, HexCoord hex, AirStrikePolicy policy) =>
            AiReconIntelMemory.TryGetLastObservedTurn(player, hex, out int observed)
            && observed == turn
            && AirTargets(AiMapMemory.AllKnownAirSightings(player), player, hex, policy)
                .Sum(s => s.Roster.Units.Count) <= policy.MinimumSurvivors;

        // Apply only the struck armies to the ground package. Event guards and other targets of
        // the later ground battle retain their own roster/commander/terrain facts.
        internal static IReadOnlyList<WorthIt.DefendingArmy> AfterAirStrike(
            IReadOnlyList<WorthIt.DefendingArmy> opposition,
            IReadOnlyList<AiMapMemory.KnownAirSighting> targets,
            AviationCombatEstimator.AirStrikeEstimate estimate)
        {
            var projected = new Dictionary<int, (List<WorthIt.DefenderProfile> Bodies, WorthIt.SideCommander Commander)>();
            var survivors = new Dictionary<int, WorthIt.DefenderProfile>();
            var survivorFates = new Dictionary<int, int>();
            for (int i = 0; i < estimate.ExpectedDefendersAfter.Count; i++)
            {
                int source = estimate.SurvivorSourceIndices != null ? estimate.SurvivorSourceIndices[i] : i;
                survivors[source] = estimate.ExpectedDefendersAfter[i];
                if (estimate.ExpectedCurrentFatesAfter != null && i < estimate.ExpectedCurrentFatesAfter.Count)
                    survivorFates[source] = estimate.ExpectedCurrentFatesAfter[i];
            }
            int offset = 0;
            foreach (var target in targets)
            {
                var bodies = new List<WorthIt.DefenderProfile>();
                WorthIt.SideCommander CommanderAt(int index, WorthIt.DefenderProfile hero) =>
                    new WorthIt.SideCommander(hero.Initiative,
                        survivorFates.TryGetValue(offset + index, out int remaining)
                            ? remaining : target.Roster.CurrentFates[index]);
                WorthIt.SideCommander commander = default;
                for (int i = 0; i < target.Roster.Units.Count; i++)
                    if (survivors.TryGetValue(offset + i, out var unit))
                    {
                        if (unit.IsGroundCombatant) bodies.Add(unit);
                        if (unit.IsHero && !commander.Present && target.Roster.CommanderIndex >= 0)
                            commander = CommanderAt(i, unit);
                    }
                if (target.Roster.CommanderIndex >= 0
                    && survivors.TryGetValue(offset + target.Roster.CommanderIndex, out var survivingCommander))
                    commander = CommanderAt(target.Roster.CommanderIndex, survivingCommander);
                projected[target.ArmyId] = (bodies, commander);
                offset += target.Roster.Units.Count;
            }
            var result = new List<WorthIt.DefendingArmy>();
            foreach (WorthIt.DefendingArmy army in opposition ?? Array.Empty<WorthIt.DefendingArmy>())
                if (army.ArmyId.HasValue && projected.TryGetValue(army.ArmyId.Value, out var after))
                {
                    if (after.Bodies.Count > 0 || after.Commander.Present)
                        result.Add(new WorthIt.DefendingArmy(after.Bodies,
                            targets.First(t => t.ArmyId == army.ArmyId.Value).Roster.CommanderIndex < 0
                                ? army.Commander : after.Commander,
                            army.DefenseBonusOverride, army.ArmyId));
                }
                else result.Add(army);
            return result;
        }

        internal static List<AirSupportOption> Options(WorldSnapshot snap,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, HexCoord targetHex,
            AirStrikePolicy policy, Func<IReadOnlyList<WorthIt.DefendingArmy>, float> winAgainst,
            float currentWin, ISet<int> unavailableArmyIds, int? fixedWingArmyId = null)
        {
            var result = new List<AirSupportOption>();
            if (snap?.Self?.Armies == null || TargetKnownEmpty(snap, targetHex, policy))
                return result;
            HexCoord? landing = LandingBase(snap, targetHex);
            if (ReferenceEquals(snap.Map, null) && !landing.HasValue)
                return result;
            opposition = opposition ?? Array.Empty<WorthIt.DefendingArmy>();
            // BuildKnown always supplies this collection. Null retains the legacy contract for
            // synthetic/older snapshots; production must never build air targets from ground guards.
            bool observedAir = snap?.Known?.AirSightings != null;
            var airTargets = KnownAirTargets(snap, targetHex, policy);
            List<WorthIt.DefenderProfile> defenders = observedAir
                ? airTargets.SelectMany(s => s.Roster.Units).ToList() : WorthIt.UnitsOf(opposition);
            bool rosterKnown = defenders.Count > 0;
            int defenderFate = opposition.Where(o => o.Commander.Present)
                .Select(o => o.Commander.Fate).DefaultIfEmpty(0).Max();

            foreach (ArmySnapshot wing in snap.Self.Armies
                .Where(x => x != null && x.IsAir && !x.IsAirfield && !x.IsPrison
                    && x.MemberCount > 0 && x.CurrentMovement > 0
                    && (!fixedWingArmyId.HasValue || x.ArmyId == fixedWingArmyId.Value)
                    && (unavailableArmyIds == null || !unavailableArmyIds.Contains(x.ArmyId)))
                .OrderBy(x => x.ArmyId))
            {
                if (!ReferenceEquals(snap.Map, null) && observedAir)
                {
                    ArmyData live = AiV2Util.ResolveArmy(snap.Observer, wing.ArmyId);
                    if (live == null) continue;
                    AirSortie active = AirSortieRegistry.ForArmy(snap.Observer, live);
                    if (active != null && (!fixedWingArmyId.HasValue || active.Kind != AirSortieKind.Strike))
                        continue;
                    var projection = ProjectService(snap, snap.Observer, snap.Map, live.Members,
                        live.Hex, new CombatAirSupportRequest(default, targetHex, policy, default), live);
                    if (!projection.HasValue) continue;
                    CombatAirService service = projection.Value;
                    float projectedWin = service.RosterKnown && winAgainst != null
                        ? winAgainst(AfterAirStrike(opposition, airTargets, service.Estimate)) : currentWin;
                    result.Add(new AirSupportOption(wing.ArmyId, service.Landing, service.FirstStrikeEta,
                        service.FirstStrikeEta, service.StrikeTurns,
                        service.RosterKnown ? service.Estimate.ExpectedDamage : 0f,
                        service.RosterKnown, projectedWin, service.Ap,
                        new ResourceVector(0f, 0f, service.Energy, 0f, 0f)));
                    continue;
                }
                List<WorthIt.DefenderProfile> attackers = (wing.RecoveryMembers
                        ?? Array.Empty<RaidRecoveryMemberSnapshot>())
                    .Where(x => x.IsAviation)
                    .OrderBy(x => x.UnitIndex)
                    .Select(x => x.CurrentProfile).ToList();
                if (attackers.Count == 0)
                    continue;

                int strikeTurns = StrikeTurns(wing, targetHex, landing.Value);
                if (strikeTurns <= 0)
                    continue;
                int eta = SortieEta(wing, targetHex);
                ArmyData liveWing = eta <= 1 ? AiV2Util.ResolveArmy(snap.Observer, wing.ArmyId) : null;
                bool[] firstPassSpent = liveWing == null ? null :
                    (wing.RecoveryMembers ?? Array.Empty<RaidRecoveryMemberSnapshot>())
                        .Where(x => x.IsAviation).OrderBy(x => x.UnitIndex)
                        .Select(x => x.UnitIndex >= 0 && x.UnitIndex < liveWing.Members.Count
                            && liveWing.Members[x.UnitIndex].HasAirAttackedThisTurn).ToArray();

                float damage = 0f;
                float winAfter = currentWin;
                if (rosterKnown)
                {
                    AviationCombatEstimator.AirStrikeEstimate estimate =
                        observedAir
                            ? AviationCombatEstimator.EstimateAirStrikeAgainstArmies(attackers,
                                airTargets.Select(t => t.Roster).ToList(), policy, strikeTurns, firstPassSpent)
                            : AviationCombatEstimator.EstimateAirStrike(attackers, defenders, policy,
                                strikeTurns, defenderFate);
                    // A wing that physically cannot hurt the known roster serves nothing; the
                    // RaidSupport survivor floor stays the raid's own policy.
                    if (estimate.ExpectedDamage <= AiConfigV2.allocatorSliceEpsilon
                        || estimate.ExpectedDefendersAfter.Count < policy.MinimumSurvivors)
                        continue;
                    damage = estimate.ExpectedDamage;
                    if (winAgainst != null)
                        winAfter = winAgainst(observedAir ? AfterAirStrike(opposition, airTargets, estimate)
                            : AfterStrike(opposition, estimate.ExpectedDefendersAfter,
                                SourceIndices(estimate, defenders.Count)));
                }

                result.Add(new AirSupportOption(wing.ArmyId, landing.Value, eta, eta, strikeTurns,
                    damage, rosterKnown, winAfter, wing.PendingActivationApCost,
                    new ResourceVector(0f, 0f, wing.PendingActivationEnergyCost, 0f, 0f)));
            }
            return result;
        }

        // The ONE ranking of support options: more expected damage of the series, a better lane
        // win, then sooner, cheaper, lower id.
        internal static List<AirSupportOption> Ranked(IEnumerable<AirSupportOption> options) =>
            (options ?? Enumerable.Empty<AirSupportOption>())
                .OrderByDescending(o => o.ExpectedDamage)
                .ThenByDescending(o => o.WinAfter)
                .ThenBy(o => o.EtaTurns)
                .ThenBy(o => o.Ap)
                .ThenBy(o => o.Resources.Energy)
                .ThenBy(o => o.WingArmyId)
                .ToList();

        // Strike turns this wing can deliver at `target` and still land at `landing`
        // (geometric planning read; provisioning and execution re-prove the live route).
        internal static int StrikeTurns(ArmySnapshot wing, HexCoord target, HexCoord landing) =>
            wing == null ? 0 : AviationRange.StrikeTurns(wing.CurrentMovement,
                Math.Max(1, wing.MaxMovement), wing.SafeUnlandedEndsRemaining,
                HexGridMath.Distance(wing.Hex, target), HexGridMath.Distance(target, landing));

        // The opposition after a strike: each survivor goes back to the army it came from (by its
        // index in UnitsOf(opposition)); an army with no survivor leaves the fight.
        internal static IReadOnlyList<WorthIt.DefendingArmy> AfterStrike(
            IReadOnlyList<WorthIt.DefendingArmy> opposition,
            IReadOnlyList<WorthIt.DefenderProfile> survivors, IReadOnlyList<int> sourceIndices)
        {
            opposition = opposition ?? Array.Empty<WorthIt.DefendingArmy>();
            survivors = survivors ?? Array.Empty<WorthIt.DefenderProfile>();
            var perArmy = new List<WorthIt.DefenderProfile>[opposition.Count];
            var armyOf = new List<int>();
            for (int a = 0; a < opposition.Count; a++)
            {
                perArmy[a] = new List<WorthIt.DefenderProfile>();
                int n = opposition[a].Units?.Count ?? 0;
                for (int k = 0; k < n; k++)
                    armyOf.Add(a);
            }
            for (int s = 0; s < survivors.Count; s++)
            {
                int source = sourceIndices != null && s < sourceIndices.Count ? sourceIndices[s] : s;
                if (source >= 0 && source < armyOf.Count)
                    perArmy[armyOf[source]].Add(survivors[s]);
            }
            var result = new List<WorthIt.DefendingArmy>();
            for (int a = 0; a < opposition.Count; a++)
                if (perArmy[a].Count > 0)
                    result.Add(new WorthIt.DefendingArmy(perArmy[a], opposition[a].Commander,
                        opposition[a].DefenseBonusOverride, opposition[a].ArmyId));
            return result;
        }

        private static IReadOnlyList<int> SourceIndices(
            AviationCombatEstimator.AirStrikeEstimate estimate, int defenderCount) =>
            estimate.SurvivorSourceIndices
            ?? Enumerable.Range(0, Math.Min(defenderCount, estimate.ExpectedDefendersAfter.Count)).ToList();

        // The own base the wing lands at: nearest to the target, then by coordinates.
        internal static HexCoord? LandingBase(WorldSnapshot snap, HexCoord targetHex)
        {
            List<HexCoord> bases = (snap?.Self?.BaseHexes ?? Array.Empty<HexCoord>())
                .Distinct().ToList();
            if (bases.Count == 0)
                return null;
            return bases.OrderBy(x => HexGridMath.Distance(x, targetHex))
                .ThenBy(x => x.Q).ThenBy(x => x.R).First();
        }

        // Turns the wing needs to reach `target`: this turn's remaining movement first.
        internal static int SortieEta(ArmySnapshot wing, HexCoord target) =>
            AiV2Util.TurnsToCover(wing, HexGridMath.Distance(wing.Hex, target));

        // The requirements of a support sortie leg: the sortie launch (0 / 0 on a paid sortie, on
        // every turn of it), its distance and ETA.
        internal static MissionRequirements LegRequirements(ArmySnapshot wing, HexCoord destination,
            int etaTurns)
        {
            float activation = wing?.PendingActivationApCost ?? 0f;
            float energy = wing?.PendingActivationEnergyCost ?? 0f;
            return new MissionRequirements
            {
                RequiresArmy = true, RequiresHero = false, MoverKnown = wing != null,
                ApMinimum = activation, ApDesired = activation, ApMaximum = activation,
                EnergyMinimum = energy, EnergyDesired = energy, EnergyMaximum = energy,
                EstimatedDistance = wing == null ? 0 : HexGridMath.Distance(wing.Hex, destination),
                EtaTurns = Math.Max(1, etaTurns),
            };
        }

        // Is this wing still a valid air army flying a support (Strike) sortie right now?
        internal static bool SortieLive(PlayerSetupData player, int? wingArmyId, out bool wingValid)
        {
            ArmyData wing = wingArmyId.HasValue ? AiV2Util.ResolveArmy(player, wingArmyId.Value) : null;
            wingValid = wing != null && AviationRules.IsValidAirArmy(wing);
            AirSortie sortie = wingValid ? AirSortieRegistry.ForArmy(player, wing) : null;
            // The task owns the wing only while it flies the strike series. Once execution turns
            // the sortie into Rebase (series over), generic aviation obligations own the return.
            return sortie != null && sortie.Kind == AirSortieKind.Strike;
        }

        // The wing already ended this turn over its target to strike again next turn (the
        // executor's HOLD): its support leg is not proposed again this turn.
        internal static bool HoldingThisTurn(PlayerSetupData player, int wingArmyId, int turn)
        {
            ArmyData wing = AiV2Util.ResolveArmy(player, wingArmyId);
            AirSortie sortie = wing != null ? AirSortieRegistry.ForArmy(player, wing) : null;
            return sortie != null && sortie.Kind == AirSortieKind.Strike && sortie.HeldTurn == turn;
        }

        // An airborne strike sortie is a physical landing obligation. When no task holds its wing
        // any more (GroundCombatLegs.HeldAirSupportArmyId — the task retired, released the wing,
        // or its target ended), the same sortie is turned homebound to its landing base and
        // continued by AviationRebasePlanner.FindMandatoryContinuations (settled before card play).
        internal static void ReleaseOrphanStrikes(PlayerSetupData player, IEnumerable<MissionIntent> intents)
        {
            if (player == null)
                return;
            PurgeLanded(player);
            var held = new HashSet<int>((intents ?? Enumerable.Empty<MissionIntent>())
                .Select(GroundCombatLegs.HeldAirSupportArmyId)
                .Where(id => id.HasValue).Select(id => id.Value));
            foreach (AirSortie sortie in AirSortieRegistry.For(player).ToList())
            {
                if (sortie == null || sortie.Kind != AirSortieKind.Strike || sortie.Army == null
                    || held.Contains(sortie.Army.Id))
                    continue;
                SendHome(player, sortie, "no longer held by a task");
            }
        }

        // A completed landing (AviationActions.LandInSlotOrder) puts the aircraft back into the
        // airfield and leaves an ordinary empty shell under the same army id. Every per-wing AI
        // flight record of such an army is closed here, so a later reuse of that shell starts a
        // fresh sortie instead of inheriting a finished one.
        internal static void PurgeLanded(PlayerSetupData player)
        {
            foreach (AirSortie sortie in AirSortieRegistry.For(player).ToList())
                if (sortie?.Army == null || !AviationRules.IsValidAirArmy(sortie.Army))
                    AirSortieRegistry.Remove(player, sortie);
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player).ToList())
            {
                if (army == null || AviationRules.IsValidAirArmy(army)
                    || !ReconAirSortieRegistry.TryGet(player, army.Id, out _))
                    continue;
                ReconAirSortieRegistry.Retire(player, army.Id);
                ReconPatrolStateRegistry.Retire(player, army.Id, "air wing landed");
            }
        }

        // End-of-turn safety net: a support wing still over its target that could not safely end
        // another turn there (its strike leg was not run this turn, or ran out of options) is
        // turned home now. Returns the wings that still have movement to fly home this turn.
        internal static List<ArmyData> RecallUnsafeStrikes(PlayerSetupData player, HexMap map)
        {
            var recalled = new List<ArmyData>();
            if (player == null || map == null)
                return recalled;
            foreach (AirSortie sortie in AirSortieRegistry.For(player).ToList())
            {
                ArmyData wing = sortie?.Army;
                if (sortie == null || sortie.Kind != AirSortieKind.Strike || wing == null
                    || !AviationRules.IsValidAirArmy(wing)
                    || AviationRules.IsOwnedAirfieldAt(wing.Hex, player)
                    || AiAirSortiePlanner.CanEndTurnHereAndRecover(wing, map, player))
                    continue;
                SendHome(player, sortie, "cannot safely end another turn over the target");
                if (wing.CurrentMovement > 0)
                    recalled.Add(wing);
            }
            return recalled;
        }

        internal static void SendHome(PlayerSetupData player, AirSortie sortie, string why)
        {
            sortie.Kind = AirSortieKind.Rebase;
            sortie.Outbound = false;
            sortie.TargetHex = sortie.LandingHex;
            // Keep StrikePolicy: recovery may strike before leaving, but must never replace
            // an exact Raid/Defence target or its survivor floor with Standard targeting.
            AiDebugLog.Write($"[AI][V2][AirSupport] wing #{sortie.Army?.Id} {why} — flies home to "
                + $"({sortie.LandingHex.Q},{sortie.LandingHex.R})");
        }

        // ---- provisioning -------------------------------------------------------------------

        // The wing itself: alive, ours, not claimed this pass, and either free of any sortie or
        // already flying this lane's strike series (its target may have moved — ActiveDefence).
        internal static bool TryResolveWing(PlayerSetupData player, ProvisioningSession session,
            int wingArmyId, string lane, out AirSupportWing resolved, out ProvisionFailure failure)
        {
            resolved = default;
            failure = default;
            ArmyData wing = AiV2Util.ResolveArmy(player, wingArmyId);
            if (wing == null || !AviationRules.IsValidAirArmy(wing) || wing.Owner != player
                || wing.Members.Count == 0 || session.ClaimedArmyIds.Contains(wing.Id))
            {
                failure = ProvisionFailure.MoverContended($"{lane} support wing #{wingArmyId} unavailable");
                return false;
            }
            AirSortie active = AirSortieRegistry.ForArmy(player, wing);
            bool continuing = active != null && active.Kind == AirSortieKind.Strike;
            if (active != null && !continuing)
            {
                failure = ProvisionFailure.MoverContended($"wing #{wing.Id} is reserved by another sortie");
                return false;
            }
            resolved = new AirSupportWing(wing, active, continuing);
            return true;
        }

        // The rest of the sortie: a live recoverable route (start or keep striking at the target
        // and still land in endurance) and the launch AP/Energy fitting the funded envelope, the
        // turn's AP and the free Energy after reservations and earlier claims this pass. A paid
        // sortie costs 0 / 0. No future activation is reserved and no win gain is re-proved.
        internal static bool TryFinishWing(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisioningSession session, FundedEntry funded, AirSupportWing w, HexCoord targetHex,
            string lane, float eps, out HexCoord landing, out float ap, out float energy,
            out ProvisionFailure failure)
        {
            ArmyData wing = w.Wing;
            failure = default;
            landing = default;
            ap = 0f;
            energy = 0f;

            AirStrikePolicy targetPolicy = funded.Mission.Target is RaidMissionTarget raid
                ? AirStrikePolicy.RaidSupport(raid.Target.ArmyId)
                : funded.Mission.Target is ActiveDefenceMissionTarget defence
                    ? AirStrikePolicy.DefenceSupport(defence.EnemyArmyId) : AirStrikePolicy.Standard;
            if (TargetKnownEmpty(session.Snapshot, targetHex, targetPolicy))
            {
                failure = ProvisionFailure.TargetInvalidated("air support has no observed strike target");
                return false;
            }
            Sortie? sameTurn = AiAirSortiePlanner.TryPlanSortie(wing, targetHex, ctx.Map, player);
            MultiTurnSortie? multi = sameTurn.HasValue ? null
                : AiAirSortiePlanner.TryPlanMultiTurnSortie(wing, targetHex, ctx.Map, player);
            if (!sameTurn.HasValue && !multi.HasValue)
            {
                failure = ProvisionFailure.NoExecutableStep(
                    $"wing #{wing.Id} has no recoverable route to the {lane} target");
                return false;
            }
            landing = sameTurn?.LandingHex ?? multi.Value.LandingHex;

            ap = wing.PendingActivationApCost;
            energy = wing.PendingActivationEnergyCost;
            if (ap > funded.Tentative.Ap + eps || energy > funded.PhysicalDraw.Energy + eps)
            {
                failure = ProvisionFailure.EnvelopeTooSmall(
                    new ProvisionRequirement(ap, new ResourceVector(0f, 0f, energy, 0f, 0f)),
                    $"{lane} support wing #{wing.Id} exceeds AP/Energy envelope");
                return false;
            }
            float turnApLeft = StrategicSpendability.SpendableAp(player, root, ctx) - session.ApClaimed;
            if (ap > turnApLeft + eps)
            {
                failure = ProvisionFailure.MoverContended(
                    $"turn AP exhausted: {lane} support wing #{wing.Id} needs {ap:0.##}, {turnApLeft:0.##} left");
                return false;
            }
            float energyLeft = ProvisioningManager.AirSpendableEnergyLeft(player, root, ctx, session);
            if (energy > energyLeft + eps)
            {
                failure = ProvisionFailure.MoverContended(
                    $"spendable Energy exhausted: {lane} support wing #{wing.Id} needs {energy:0.##}, "
                    + $"{energyLeft:0.##} left after reservations and earlier claims this pass");
                return false;
            }
            return true;
        }
    }
}

