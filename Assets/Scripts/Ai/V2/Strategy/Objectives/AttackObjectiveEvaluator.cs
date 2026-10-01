using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ATK §23 — the execution legs of ONE Attack operation. Deliberately the same shape the Raid
    // lane already uses, minus a post-success Return: §8 says the army STAYS on the Base it just
    // took. Air support is a side leg (AirSupport below), not a phase of the operation.
    //   Assault         — march on the target and take it.
    //   Reinforcement   — the primary cannot clear the site; a support army is being brought in.
    //   SupportReturn   — the shared GroundCombat handoff left the support container empty-handed
    //                     and it walks home. Same leg Raid already owns.
    //   RecoveryReturn  — the operation is no longer viable; the primary withdraws to an own Base.
    //   Gather          — no single army (nor a same-hex package) clears the site, but free armies
    //                     spread across hexes do together: the host (PrimaryArmyId) holds while
    //                     every planned support walks to it in parallel and hands its bodies over
    //                     (GroundCombatAssemblyPlanner.PlanGather owns the host/support choice).
    //   GatherReturn    — a gather support that already handed over walks home (strike force
    //                     step 5). One leg per donor, run beside whatever the operation does;
    //                     it is never the operation's own phase (AttackIntent.GatherReturns).
    //   AirSupport      — a wing strikes the site's defenders right before the assault (the one
    //                     GroundCombatAirSupport, as Raid's). Run by the wing beside the
    //                     operation's own leg; never the operation's own phase.
    public enum AttackMissionPhase
    {
        Assault = 0,
        Reinforcement = 1,
        SupportReturn = 2,
        RecoveryReturn = 3,
        Gather = 4,
        GatherReturn = 5,
        AirSupport = 6,
    }

    // T01 — the mobilization preparation inside the ONE Gather phase (no new phase, no second
    // assembly system). A preparation Gather may run around a weak, hero-only or still-empty own
    // field host at the own starting Citadel until the host strictly clears the dynamic 80% peak;
    // its ordinary support legs stay plain Gather legs. The two steps below have no walking
    // support of their own:
    //   CreateHost — bind the one preparation container: reuse an empty shell standing on the
    //                own Citadel, else ArmyActions.CreateArmyWithMember (first legal same-hex
    //                member), else ArmyActions.CreateArmy (an empty shell). 2 AP when created.
    //   Assemble   — same-hex bodies/hero from legal donors (garrison floors and operators kept)
    //                join the host through the canonical transfer, 0 AP.
    //   RecruitDonors — ATK-F05: the priced purchase of supports another operation holds
    //                (GroundCombatDonorPolicy). A FRESH proposal whose TaskScore carries what those
    //                operations lose (MoverOpportunityCost), never the Hard intent's lifecycle; no
    //                mutation, 0 AP. Executed, its GatherSupportArmyIds enter the intent
    //                (AdvanceIntent) and the lender retires on the next pass, as after a fresh gather.
    public enum AttackPreparationStep
    {
        None = 0,
        CreateHost = 1,
        Assemble = 2,
        RecruitDonors = 3,
        // User decision 30.09: the fist is assembled on an own Base (PreparationStagingBase), where
        // cards and generated outputs land in it directly. A host standing elsewhere first walks
        // there (a plain Transit of the host, the walk-home leg's machinery).
        MoveHost = 4,
        // 2026-10-01 (variant B) — a hero with a larger Command than the host's commander, standing
        // in an own garrison elsewhere, leaves it as a lone-hero container (CreateArmyWithMember);
        // it then walks to the host as a Gather leg (CommanderLeg) and takes command there.
        FetchCommander = 5,
    }

    // The mission-layer transport for one Attack leg. Every field is a frozen decision the
    // Provisioning/Execution stages read and never re-derive — no target re-pick, no base re-pick,
    // no strategic re-scoring below this point.
    public struct AttackMissionTarget
    {
        public AttackMissionPhase Phase;
        public AttackTargetRef Target;
        public int? PrimaryArmyId;
        // The army this leg moves for Reinforcement / SupportReturn / Gather.
        public int? SupportArmyId;
        // Gather only: every support the frozen gather plan still expects at the host, including
        // this leg's SupportArmyId. Continuity copies it into the durable AttackIntent.
        public int[] GatherSupportArmyIds;
        // AirSupport only: the bound wing and the own base it lands at.
        public int? AirSupportArmyId;
        public HexCoord? AirSupportLandingHex;
        // Where the leg is actually walking this turn: the target site for Assault, the primary's
        // hex for Reinforcement/Gather, an own Base for the two return legs.
        public HexCoord DestinationHex;
        public HexCoord? RecoveryBaseHex;
        public HexCoord? SupportReturnHex;
        // The honest site facts this leg was planned against (§30/§31).
        public float DefenderHexDefenseBonus;
        public int DefenderCount;
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public bool ForceCommitted;
        public int EstimatedEta;
        // ATK §17 — the game turn on which this operation last took an opportunistic side strike,
        // copied here from the durable AttackIntent so the executor reads a FROZEN fact like every
        // other field of this leg instead of reaching into intent state mid-step. 0 (the struct
        // default) means "never": turn numbering starts at 1.
        public int OpportunisticStrikeTurn;
        // T01 — every leg of a mobilization preparation carries it (AttackIntent.Preparation);
        // PreparationStep names the host-side step, None for an ordinary support leg.
        public bool Preparation;
        public AttackPreparationStep PreparationStep;
        // 2026-10-01 (variant B) — this Gather leg walks the preparation's fetched commander
        // (AttackIntent.CommanderArmyId): its handoff counts the larger Command as progress.
        public bool CommanderLeg;
        // FetchCommander only: the own garrison the hero leaves and the hero (UnitData.RuntimeId).
        public int? CommanderDonorArmyId;
        public int CommanderUnitId;
    }

    // ===========================================================================================
    //  ATK §19/§39 — ATTACK OBJECTIVE ENUMERATION.
    //
    //  One objective per known hostile Base/Citadel, defended or not. A facility installed in
    //  a Base is part of that Base, never a separate Attack objective. Other buildings, including
    //  resource sites, are outside this lane. ActiveDefence independently avoids fights on any
    //  known foreign structure (ActiveDefenceObjectiveEvaluator.OnKnownForeignStructure).
    //  This is an OBJECTIVE EVALUATOR sitting beside
    //  RaidObjectiveEvaluator and ActiveDefenceObjectiveEvaluator at the existing Strategy/Objectives
    //  level — deliberately not a new Manager, Layer or Service, and it owns no actor selection, no
    //  combat estimator and no movement. AggressionObjectiveEvaluator remains the aggregation owner
    //  for the Aggression axis.
    //
    //  Knowledge boundary (§19/§20/§65): targets come ONLY from snap.Known.Buildings — this player's
    //  own honest structural memory. BuildingRegistry, TrueWorld and global ArmyRegistry sweeps are
    //  never read here; a Base nobody has ever seen is not a target, and a Base last seen under Red
    //  stays a Red target until it is genuinely re-observed. The one sanctioned exception (T01):
    //  every live opponent's STARTING Citadel coordinates + original owner
    //  (WorldAnalysis.SanctionedEnemyCitadels) are a location-only objective until that hex is
    //  first observed; its defenders stay unknown and it never becomes a sighting or a threat.
    // ===========================================================================================
    public sealed class AttackObjective
    {
        public AggressionObjectiveKind Kind => AggressionObjectiveKind.Attack;
        public AttackTargetRef Target;
        // The known opposition on the target site (§31): its garrison and every known enemy army
        // on that same hex, each its own battle with its own commander — one objective.
        public IReadOnlyList<WorthIt.DefendingArmy> Opposition = Array.Empty<WorthIt.DefendingArmy>();
        // Every defending body of that opposition (counts, power, coverage) — derived from
        // Opposition on read, never stored as a second copy.
        public IReadOnlyList<WorthIt.DefenderProfile> Defenders => WorthIt.UnitsOf(Opposition);
        public int DefenderCount => Defenders.Count;
        public float TargetPower;
        // Turns since the site was last actually observed. int.MaxValue-safe: 0 when the memory
        // carries no stamp at all, which is treated as maximally stale by the score below.
        public int IntelAgeTurns;
        public TaskScore TaskScore;
        public float BaseValue => TaskScore.Value;
        // T01 — a sanctioned location-only starting Citadel: its coordinates and original owner
        // are allowed knowledge, but the site itself was never observed. Opposition is UNKNOWN
        // (not "observed empty"): preparation may aim at it, a march may not start until Recon
        // has actually seen the site (ObservationNeeds publishes it).
        public bool LocationOnly;

        public HexCoord Hex => Target.Hex;
        public string ObjectiveId => $"Attack#{Target.DiagnosticLabel}";
        public MissionIntentKey IntentKey => MissionIntentKey.ForAttack(Target);
    }

    public static class AttackObjectiveEvaluator
    {
        // The own Base a mobilization preparation assembles on: of every held own Base
        // (SelfSnapshot.BaseHexes, starting Citadel included) the one nearest to the target; ties
        // go to the starting Citadel, then Q, R. Null when no own Base is held. Recomputed every
        // pass, so a lost Base simply moves the staging point. With a `host` whose route facts
        // Analysis measured (a structural field army, ArmySnapshot.ReachableOwnBaseHexes) only a
        // Base it can really reach qualifies — an unreachable staging point would otherwise hold
        // the preparation in WAIT forever.
        internal static HexCoord? PreparationStagingBase(WorldSnapshot snap, HexCoord targetHex,
            ArmySnapshot host = null)
        {
            IReadOnlyList<HexCoord> bases = snap?.Self?.BaseHexes;
            if (host != null && host.IsStructuralRaidActor && bases != null)
                bases = bases.Where(h => h.Equals(host.Hex)
                    || (host.ReachableOwnBaseHexes != null && host.ReachableOwnBaseHexes.Contains(h)))
                    .ToList();
            if (bases == null || bases.Count == 0)
                return null;
            HexCoord citadel = snap.Self.Citadel;
            return bases
                .OrderBy(h => HexGridMath.Distance(h, targetHex))
                .ThenByDescending(h => h.Equals(citadel))
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .First();
        }

        // ---- enumeration --------------------------------------------------------------------

        public static List<AttackObjective> Enumerate(WorldSnapshot snap)
        {
            var result = new List<AttackObjective>();
            PlayerSetupData player = snap?.Observer;
            if (snap?.Self == null || player == null)
            {
                AiDebugLog.Write("[AI][V2][Attack][Objective] decision=NONE reason=no_self_snapshot_or_observer");
                return result;
            }

            IReadOnlyList<AiMapMemory.KnownBuilding> buildings = snap.Known?.Buildings
                ?? (IReadOnlyList<AiMapMemory.KnownBuilding>)Array.Empty<AiMapMemory.KnownBuilding>();

            bool hasDirection = WorldAnalysis.TrySelectStrategicDirection(snap, player,
                out HexCoord directionTarget, out HexCoord directionAnchor,
                allowTrueWorldFallback: false);

            foreach (AiMapMemory.KnownBuilding b in buildings)
            {
                if (!IsHostileStrategicStructure(b, player))
                    continue;
                // A hex that is currently ours is never an Attack target, whatever memory says.
                // Self.BaseHexes is the own-Base identity owner (§20); this is the one place the
                // two knowledge sides meet and current truth wins.
                if (snap.Self.BaseHexes != null && snap.Self.BaseHexes.Contains(b.Hex))
                    continue;

                AttackObjective objective = Build(snap, player, b, hasDirection,
                    directionAnchor, directionTarget);
                result.Add(objective);
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                    $"[AI][V2][Attack][Objective] decision=ACCEPT target={objective.Target.DiagnosticLabel} "
                    + $"task={F(objective.BaseValue)} defenders={objective.DefenderCount} "
                    + $"defenderPower={F(objective.TargetPower)} intelAge={objective.IntelAgeTurns}");
            }

            // T01 — the sanctioned starting-Citadel coordinates (WorldAnalysis.SanctionedEnemyCitadels)
            // are an Attack objective before anyone has looked at them. Honest knowledge always
            // wins: a remembered record is the objective above, and a hex this player has EVER seen
            // without such a record was observed destroyed / captured / absent — the coordinates
            // never resurrect it. Location-only knowledge never becomes a defender package, a
            // sighting or a threat: Known.Buildings, Threat and ForceNeed never see it.
            foreach (var (citadelHex, owner) in WorldAnalysis.SanctionedEnemyCitadels(snap))
            {
                if (buildings.Any(b => b.Hex.Equals(citadelHex))
                    || (snap.Self.BaseHexes != null && snap.Self.BaseHexes.Contains(citadelHex))
                    || EverObserved(snap, citadelHex))
                    continue;
                AttackObjective located = Build(snap, player,
                    new AiMapMemory.KnownBuilding(citadelHex, owner, isStartingCitadel: true,
                        facilityAbilities: null),
                    hasDirection, directionAnchor, directionTarget);
                located.LocationOnly = true;
                result.Add(located);
                AiDebugLog.WriteDeduped(located.Target.DiagnosticLabel,
                    $"[AI][V2][Attack][Objective] decision=ACCEPT target={located.Target.DiagnosticLabel} "
                    + $"task={F(located.BaseValue)} knowledge=starting-location-only defenders=unknown");
            }

            result.Sort((a, b) =>
            {
                int c = b.BaseValue.CompareTo(a.BaseValue);
                if (c != 0) return c;
                // ATK §18/§21 — deterministic tie-break on the stable identity, never on
                // enumeration order of a dictionary-backed memory store.
                c = a.Target.Hex.Q.CompareTo(b.Target.Hex.Q);
                if (c != 0) return c;
                c = a.Target.Hex.R.CompareTo(b.Target.Hex.R);
                return c != 0 ? c : a.Target.ExpectedOwnerId.CompareTo(b.Target.ExpectedOwnerId);
            });
            return result;
        }

        public static AttackObjective ForTrackedTarget(WorldSnapshot snap, AttackTargetRef target) =>
            !target.HasValue ? null
                : Enumerate(snap).FirstOrDefault(o => o.Target.Equals(target));

        // ---- target validity (§25) -----------------------------------------------------------

        // The ONE answer to "is this Attack target still the thing we set out to capture", read
        // from honest memory only. A fogged target keeps its last honest answer: absence of a
        // fresh observation is never evidence of change.
        //   owner is us now      -> SUCCESS (caller retires the intent as completed)
        //   structure gone       -> INVALIDATED (the Base/Citadel no longer exists)
        //   defenders arrived    -> CONTINUE (the site is the same objective; the win-chance gate
        //                           and Reinforcement/Recovery answer whether we can still take it)
        //   owner is someone else-> INVALIDATED (a fresh objective may appear for the new owner)
        //   still ExpectedOwner  -> CONTINUE
        public enum AttackTargetStatus { Continue, Captured, Invalidated }

        public static AttackTargetStatus EvaluateTarget(WorldSnapshot snap, AttackTargetRef target)
        {
            if (!target.HasValue || snap?.Self == null)
                return AttackTargetStatus.Invalidated;
            // Current truth first: a hex now in our own Base topology was captured, full stop.
            if (snap.Self.BaseHexes != null && snap.Self.BaseHexes.Contains(target.Hex))
                return AttackTargetStatus.Captured;

            IReadOnlyList<AiMapMemory.KnownBuilding> buildings = snap.Known?.Buildings
                ?? (IReadOnlyList<AiMapMemory.KnownBuilding>)Array.Empty<AiMapMemory.KnownBuilding>();
            AiMapMemory.KnownBuilding? remembered = null;
            foreach (AiMapMemory.KnownBuilding b in buildings)
                if (b.Hex.Equals(target.Hex)) { remembered = b; break; }
            return StatusFromMemory(target, remembered, EverObserved(snap, target.Hex));
        }

        // T01 — the target stands on sanctioned starting-Citadel coordinates this player has never
        // observed: a legal preparation objective whose defenders are UNKNOWN. The march waits for
        // a real observation (the Gather keeps the fist; ObservationNeeds asks Recon).
        public static bool IsLocationOnly(WorldSnapshot snap, AttackTargetRef target)
        {
            if (!target.HasValue || snap?.Self == null)
                return false;
            IReadOnlyList<AiMapMemory.KnownBuilding> buildings = snap.Known?.Buildings
                ?? (IReadOnlyList<AiMapMemory.KnownBuilding>)Array.Empty<AiMapMemory.KnownBuilding>();
            return !buildings.Any(b => b.Hex.Equals(target.Hex))
                && IsUnobservedSanctionedCitadel(target, EverObserved(snap, target.Hex));
        }

        private static bool EverObserved(WorldSnapshot snap, HexCoord hex) =>
            snap?.MapKnowledge?.EverSeenHexSet?.Contains(hex) == true;

        private static bool IsUnobservedSanctionedCitadel(AttackTargetRef target, bool everObserved) =>
            !everObserved && target.Kind == AttackTargetKind.Citadel
            && WorldAnalysis.IsSanctionedEnemyCitadel(target.ExpectedOwner, target.Hex);

        // The one memory-side rule both overloads share. AiMapMemory only drops a building record
        // when the hex was genuinely re-observed without it, so "no longer remembered" is honest:
        //   Base/Citadel target — gone means it is no longer the thing we set out to capture.
        //   never observed, sanctioned starting-Citadel coordinates of the expected owner
        //                        -> CONTINUE (location-only; the first real observation decides)
        private static AttackTargetStatus StatusFromMemory(AttackTargetRef target,
            AiMapMemory.KnownBuilding? remembered, bool everObserved)
        {
            if (!remembered.HasValue)
                return IsUnobservedSanctionedCitadel(target, everObserved)
                    ? AttackTargetStatus.Continue : AttackTargetStatus.Invalidated;
            AiMapMemory.KnownBuilding b = remembered.Value;
            if (!b.IsBase && !b.IsStartingCitadel)
                return AttackTargetStatus.Invalidated;
            if (b.Owner == null || b.Owner.ColorIndex != target.ExpectedOwnerId)
                return AttackTargetStatus.Invalidated;
            return AttackTargetStatus.Continue;
        }

        // The SAME §25 question against LIVE state instead of a snapshot, for the one caller that
        // has no snapshot: MissionRevalidator's per-mission gate, which runs between provisioned
        // missions. The rules are identical and deliberately kept in this one owner rather than
        // reimplemented there — only the two data sources differ: our own current Base topology
        // (live truth about ourselves, exactly what Self.BaseHexes projects) and this observer's
        // own remembered buildings (never a live read of a FOREIGN owner, §19/§65).
        public static AttackTargetStatus EvaluateTargetLive(PlayerSetupData player,
            AttackTargetRef target)
        {
            if (!target.HasValue || player == null)
                return AttackTargetStatus.Invalidated;
            BuildingData live = BuildingRegistry.FindAt(target.Hex);
            if (live != null && live.Owner == player && (live.IsBase || live.IsStartingCitadel))
                return AttackTargetStatus.Captured;

            return StatusFromMemory(target, AiMapMemory.KnownBuildingAt(player, target.Hex),
                VisionSystem.HasEverSeen(player, target.Hex));
        }

        // ---- site facts the mission layer needs (§30/§31) -------------------------------------

        // The defence bonus a defender standing on the target site actually gets, through the one
        // fog-honest owner. `map` may be null, in which case terrain is simply not known to this
        // caller and only the remembered structural defence contributes.
        public static float KnownSiteDefenceBonus(WorldSnapshot snap, HexMap map, HexCoord hex) =>
            AiMapMemory.KnownHexDefenseBonus(snap?.Observer, map, hex);

        // Every known hostile body standing on the site (§31): a garrison and two field armies on
        // the same Base are one objective. Flat roster for counts / power / coverage.
        public static List<WorthIt.DefenderProfile> KnownSiteDefenders(WorldSnapshot snap, HexCoord hex) =>
            WorthIt.UnitsOf(KnownSiteOpposition(snap, hex));

        // The same site as the fights it really is: every known army on the hex is its own battle,
        // with its own observed commander (WorthIt.EstimateSequential plays them strongest first).
        public static List<WorthIt.DefendingArmy> KnownSiteOpposition(WorldSnapshot snap, HexCoord hex)
        {
            IEnumerable<AiMapMemory.KnownEnemySighting> sightings = snap?.Known?.EnemySightings
                ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>();
            return sightings
                .Where(s => s.Hex.Equals(hex) && s.Defenders != null)
                .OrderBy(s => s.ArmyId)
                .Select(s => new WorthIt.DefendingArmy(
                    s.Defenders, s.Commander,
                    AiMapMemory.KnownHexDefenseBonusFor(snap.Observer, hex, s.Owner)))
                .ToList();
        }

        // ---- response terms (§35) -------------------------------------------------------------

        // The response half of the Attack score, folded onto the intrinsic objective score exactly
        // the way ActiveDefenceObjectiveEvaluator.WithResponse already does for its own lane: the
        // win chance the shared estimator produced for this concrete force against this concrete
        // site (hex defence included), the AP/ETA price of delivering it, and the cost of taking
        // this particular army off whatever it is doing now. No Attack-specific slot.
        public static TaskScore WithResponse(AttackObjective objective, ArmySnapshot actor,
            float winChance, int eta, float moverOpportunityCost = 0f,
            int? projectedActivationAp = null) =>
            TaskScoreEvaluator.WithActorResponse(objective.TaskScore, actor, winChance, eta,
                moverOpportunityCost, projectedActivationAp);

        // ---- internals -------------------------------------------------------------------------

        // §19 — Owner != us, Owner != Neutral, owner not eliminated, and the structure is a Base or
        // a starting Citadel. Facility slots do not change the identity of their host building.
        internal static bool IsHostileStrategicStructure(AiMapMemory.KnownBuilding b,
            PlayerSetupData player) =>
            b.Owner != null && b.Owner != player && !b.Owner.IsNeutral && !b.Owner.IsEliminated
            && (b.IsBase || b.IsStartingCitadel);

        private static TaskScore BuildAttackScore(WorldSnapshot snap, AiMapMemory.KnownBuilding b,
            float assetNorm, float frontProgress,
            float corridorAlignment, float readiness) =>
            new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(assetNorm),
                // Attack is offensive by nature — the whole point is projecting force onto a
                // chosen target, near or far. Unlike Raid/Economy/Recon it does not carry the
                // shared near-home bonus / far-from-home penalty.
                ownTerritoryProximity: 0f,
                frontProgress: TaskScoreEvaluator.FrontProgress(frontProgress),
                corridorAlignment: TaskScoreEvaluator.CorridorAlignment(corridorAlignment),
                threatDirection: TaskScoreEvaluator.ThreatDirection(SiteThreatToUs(snap, b.Hex)),
                attackReadiness: TaskScoreEvaluator.AttackReadiness(readiness),
                // No IntelAgePenalty: an Attack target is a fixed structure whose garrison is rarely
                // re-observed, so fresh intel is a bonus the win estimate already reads, never a
                // price for committing to the structure. Intel age stays on the objective
                // (IntelAgeTurns) for diagnostics and Recon's observation needs.
                // Same offensive-restraint slot as Raid: home threat lowers the task, never the
                // shared Aggression Radar that ActiveDefence also depends on.
                citadelThreatRisk: TaskScoreEvaluator.CitadelThreatRisk(snap));

        private static AttackObjective Build(WorldSnapshot snap, PlayerSetupData player,
            AiMapMemory.KnownBuilding b, bool hasDirection, HexCoord anchor, HexCoord directionTarget)
        {
            AttackTargetKind kind = b.IsStartingCitadel ? AttackTargetKind.Citadel
                : AttackTargetKind.Base;
            List<WorthIt.DefendingArmy> opposition = KnownSiteOpposition(snap, b.Hex);
            List<WorthIt.DefenderProfile> defenders = WorthIt.UnitsOf(opposition);

            // §34 — the site's own defence is NOT a positive term for the attacker. It is priced
            // exactly once, as a reduction of WinChance through the shared WorthIt estimator (the
            // bonus GroundCombatAssemblyRequest.DefenderHexDefenseBonus carries), so TerrainDefense
            // is deliberately left at zero here. Defender power likewise stays out of
            // AttackReadiness: it already lowers WinChance and already shows up as
            // ThreatDirection where those defenders genuinely threaten us.
            float assetValue = kind == AttackTargetKind.Citadel ? AiConfigV2.assetValueCitadel
                : AiConfigV2.assetValueBase;
            float assetNorm = assetValue / Mathf.Max(1f, AiConfigV2.assetValueCitadel);

            int intelAge = IntelAge(snap, b);

            float frontProgress = hasDirection
                ? WorldAnalysis.ForwardProgressToward(anchor, directionTarget, b.Hex) : 0f;
            // Corridor asks "is this target on the way to the direction target". The direction
            // target itself is the destination, not something on the way: FrontProgress already
            // credits reaching it in full, so it earns no second positional term.
            float corridorAlignment = hasDirection && !b.Hex.Equals(directionTarget)
                ? WorldAnalysis.CorridorAlignmentToward(anchor, directionTarget, b.Hex,
                    AiConfigV2.attackCorridorDetourScale) : 0f;

            float readiness = Readiness(snap.Self);

            TaskScore score = BuildAttackScore(snap, b, assetNorm,
                frontProgress, corridorAlignment, readiness);
            // EconomicExpansionValue is deliberately NOT populated (§35/§77). It may only be filled
            // when the existing Economy network model genuinely proves that holding this node opens
            // a resource cluster we cannot already reach — a generic "more territory is good"
            // multiplier would be exactly the invented war justification §43 forbids.

            return new AttackObjective
            {
                Target = AttackTargetRef.For(b.Hex, b.Owner, kind),
                Opposition = opposition,
                TargetPower = AiPower.EffectiveArmyPowerFromProfiles(defenders),
                IntelAgeTurns = intelAge,
                TaskScore = score,
            };
        }

        // Strike force step 6 — the observation Attack publishes for Recon: the target site of every
        // live operation that is still going to fight there (Gather / Assault / Reinforcement).
        // Recon closes it with its own Refresh objective (ReconObjectiveEvaluator); Attack never
        // moves a scout itself.
        // Step 7 — when this player knows no hostile Base/Citadel at all, Attack has nothing to aim
        // at: it asks to observe the enemy citadel, whose coordinates are the cheat anchor
        // (WorldAnalysis.TryEnemyCitadelAnchor). Its defenders stay unknown until Recon sees them;
        // only then does it become an ordinary objective through snap.Known.Buildings.
        internal static IEnumerable<HexCoord> ObservationNeeds(WorldSnapshot snap)
        {
            PlayerSetupData player = snap?.Observer;
            if (player == null)
                yield break;
            IReadOnlyList<AiMapMemory.KnownBuilding> buildings = snap.Known?.Buildings
                ?? (IReadOnlyList<AiMapMemory.KnownBuilding>)Array.Empty<AiMapMemory.KnownBuilding>();
            if (!buildings.Any(b => IsHostileStrategicStructure(b, player))
                && WorldAnalysis.TryEnemyCitadelAnchor(snap, out HexCoord citadel))
                yield return citadel;
            foreach (MissionIntent i in MissionIntentRegistry.GetOrCreate(player).All)
            {
                AttackIntent a = i?.Kind == MissionKind.Attack && i.Status == IntentStatus.Active
                    ? i.Attack : null;
                if (a == null || !a.Target.HasValue)
                    continue;
                // Recon's objectives are frozen before Continuity resolves this turn's intents, so
                // the target's own status is read here: a captured / invalidated site needs no look.
                if ((a.Phase == AttackMissionPhase.Gather || a.Phase == AttackMissionPhase.Assault
                        || a.Phase == AttackMissionPhase.Reinforcement)
                    && EvaluateTarget(snap, a.Target) == AttackTargetStatus.Continue)
                    yield return a.Target.Hex;
            }
        }

        // Strike force step 4 — Attack readiness from the SelfSnapshot force measures. Not a gate:
        // how much of the available force already stands in one fist (assembly: Fist / P_field) and
        // how much of the reachable ceiling is already on the map (deployment: P_field against
        // P_deck plus the equipment still in hand/deck). Aviation is parallel support and stays out.
        // 2026-09-30 (user decision) — once the mobilization gate is open the force IS ready to
        // mobilize: the slot is full, so every preparation step (MoveHost first) keeps Attack's
        // priority against fresh work instead of being starved by it.
        internal static float Readiness(SelfSnapshot self)
        {
            if (self == null)
                return 0f;
            if (MobilizationOpen(self))
                return 1f;
            float assembly = self.FistPower / Mathf.Max(1f, self.FieldPotential);
            float deployment = self.FieldPotential
                / Mathf.Max(1f, self.TotalMilitaryPotential + self.Reserve.Equipment);
            return Curves.Ramp(assembly, AiConfigV2.attackAssemblyReadyLo, AiConfigV2.attackAssemblyReadyHi)
                * Curves.Ramp(deployment, AiConfigV2.attackDeploymentReadyLo, AiConfigV2.attackDeploymentReadyHi);
        }

        // Admission uses the current ground-only deck/map ceiling. Strict inequality is
        // intentional: an army at exactly four fifths still prepares.
        internal static bool ForceReady(float attackArmyPower, float currentDeckPeakPower) =>
            currentDeckPeakPower > 0f && attackArmyPower > 0.80f * currentDeckPeakPower;

        // Mobilization opens a new Attack preparation (never a march). Two independent starts
        // (2026-09-30, user decision); the march itself keeps ForceReady's strict > 80%:
        //  (A) deck share — at least three quarters of the additive live + hand + remaining-deck
        //      ground force is already on the map (PlayerForceAnalysis scale). Inclusive; written
        //      as 4·deployed >= 3·available so exactly three quarters (135 of 180) is not lost to
        //      the binary rounding of 0.75f.
        //  (B) field strike force — the bodies already on the field can form the strike army:
        //      SelfSnapshot.FieldStrikePotential (no active scouts, aviation, heroes' own power or
        //      mandatory garrison defence; Raid / ActiveDefence armies count — they come back)
        //      clears the same ForceReady bar on the current deck peak.
        internal static bool MobilizationOpen(float deployedPower, float availablePower) =>
            availablePower > 0f && 4f * deployedPower >= 3f * availablePower;

        internal static bool FieldStrikeForceReady(float fieldStrikePotential, float currentDeckPeakPower) =>
            ForceReady(fieldStrikePotential, currentDeckPeakPower);

        internal static bool MobilizationOpen(SelfSnapshot self) =>
            self != null && (MobilizationOpen(self.DeployedPower, self.AvailablePower)
                || FieldStrikeForceReady(self.FieldStrikePotential, self.AttackPeak));

        // §66 — a stamp of 0 means the record predates observation stamping, which must read as
        // "age unknown", i.e. maximally stale, never as "observed on turn 0".
        private static int IntelAge(WorldSnapshot snap, AiMapMemory.KnownBuilding b)
        {
            int turn = snap?.TurnNumber ?? 0;
            if (b.SeenTurn <= 0)
                return Mathf.Max(1, AiConfigV2.scoutSurveilStaleTurnsHi);
            return Mathf.Max(0, turn - b.SeenTurn);
        }

        // §35 ThreatDirection — do the bodies defending this site actually threaten OUR assets?
        // Read straight off the existing ThreatModel for exactly the armies standing there; this
        // invents no new threat model and double-counts nothing, because the same bodies lower
        // WinChance rather than raising any reward slot.
        private static float SiteThreatToUs(WorldSnapshot snap, HexCoord hex)
        {
            IEnumerable<AssetThreatSnapshot> threats = snap?.Threat?.Threats
                ?? Array.Empty<AssetThreatSnapshot>();
            float worst = 0f;
            foreach (AssetThreatSnapshot t in threats)
            {
                if (t?.Contact?.Army == null || !t.Contact.Position.HasValue
                    || !t.Contact.Position.Value.Equals(hex))
                    continue;
                if (t.Severity > worst)
                    worst = t.Severity;
            }
            return worst;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
