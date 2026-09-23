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
    // lane already uses, minus AirSupport (§79 keeps Attack ground-only in this first version) and
    // minus a post-success Return: §8 says the army STAYS on the Base it just took.
    //   Assault         — march on the target and take it.
    //   Reinforcement   — the primary cannot clear the site; a support army is being brought in.
    //   SupportReturn   — the shared GroundCombat handoff left the support container empty-handed
    //                     and it walks home. Same leg Raid already owns.
    //   RecoveryReturn  — the operation is no longer viable; the primary withdraws to an own Base.
    public enum AttackMissionPhase
    {
        Assault = 0,
        Reinforcement = 1,
        SupportReturn = 2,
        RecoveryReturn = 3,
    }

    // The mission-layer transport for one Attack leg. Every field is a frozen decision the
    // Provisioning/Execution stages read and never re-derive — no target re-pick, no base re-pick,
    // no strategic re-scoring below this point.
    public struct AttackMissionTarget
    {
        public AttackMissionPhase Phase;
        public AttackTargetRef Target;
        public int? PrimaryArmyId;
        public int? SupportArmyId;
        // Where the leg is actually walking this turn: the target site for Assault/Reinforcement,
        // an own Base for the two return legs.
        public HexCoord DestinationHex;
        public HexCoord? RecoveryBaseHex;
        public HexCoord? SupportReturnHex;
        // The honest site facts this leg was planned against (§30/§31).
        public float DefenderHexDefenseBonus;
        public int DefenderCount;
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public int EstimatedEta;
        // ATK §17 — the game turn on which this operation last took an opportunistic side strike,
        // copied here from the durable AttackIntent so the executor reads a FROZEN fact like every
        // other field of this leg instead of reaching into intent state mid-step. 0 (the struct
        // default) means "never": turn numbering starts at 1.
        public int OpportunisticStrikeTurn;
    }

    // ===========================================================================================
    //  ATK §19/§39 — ATTACK OBJECTIVE ENUMERATION.
    //
    //  One objective per known hostile Base/Citadel. This is an OBJECTIVE EVALUATOR sitting beside
    //  RaidObjectiveEvaluator and ActiveDefenceObjectiveEvaluator at the existing Strategy/Objectives
    //  level — deliberately not a new Manager, Layer or Service, and it owns no actor selection, no
    //  combat estimator and no movement. AggressionObjectiveEvaluator remains the aggregation owner
    //  for the Aggression axis.
    //
    //  Knowledge boundary (§19/§20/§65): targets come ONLY from snap.Known.Buildings — this player's
    //  own honest structural memory. BuildingRegistry, TrueWorld and global ArmyRegistry sweeps are
    //  never read here; a Base nobody has ever seen is not a target, and a Base last seen under Red
    //  stays a Red target until it is genuinely re-observed.
    // ===========================================================================================
    public sealed class AttackObjective
    {
        public AggressionObjectiveKind Kind => AggressionObjectiveKind.Attack;
        public AttackTargetRef Target;
        // The known defender package standing on the target site (§31): its garrison and every
        // known enemy army on that same hex, as one fight. Never split into separate objectives.
        public IReadOnlyList<WorthIt.DefenderProfile> Defenders =
            Array.Empty<WorthIt.DefenderProfile>();
        public int DefenderCount;
        public float TargetPower;
        // Turns since the site was last actually observed. int.MaxValue-safe: 0 when the memory
        // carries no stamp at all, which is treated as maximally stale by the score below.
        public int IntelAgeTurns;
        public TaskScore TaskScore;
        public float BaseValue => TaskScore.Value;

        public HexCoord Hex => Target.Hex;
        public string ObjectiveId => $"Attack#{Target.DiagnosticLabel}";
        public MissionIntentKey IntentKey => MissionIntentKey.ForAttack(Target);
    }

    public static class AttackObjectiveEvaluator
    {
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
        //   structure gone       -> INVALIDATED
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
            foreach (AiMapMemory.KnownBuilding b in buildings)
            {
                if (!b.Hex.Equals(target.Hex))
                    continue;
                if (!b.IsBase && !b.IsStartingCitadel)
                    return AttackTargetStatus.Invalidated;
                if (b.Owner == null || b.Owner.ColorIndex != target.ExpectedOwnerId)
                    return AttackTargetStatus.Invalidated;
                return AttackTargetStatus.Continue;
            }
            // The structure is no longer remembered at all — AiMapMemory only drops a record when
            // the hex was genuinely re-observed without it.
            return AttackTargetStatus.Invalidated;
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

            AiMapMemory.KnownBuilding? remembered = AiMapMemory.KnownBuildingAt(player, target.Hex);
            if (!remembered.HasValue)
                return AttackTargetStatus.Invalidated;
            AiMapMemory.KnownBuilding b = remembered.Value;
            if (!b.IsBase && !b.IsStartingCitadel)
                return AttackTargetStatus.Invalidated;
            if (b.Owner == null || b.Owner.ColorIndex != target.ExpectedOwnerId)
                return AttackTargetStatus.Invalidated;
            return AttackTargetStatus.Continue;
        }

        // ---- site facts the mission layer needs (§30/§31) -------------------------------------

        // The defence bonus a defender standing on the target site actually gets, through the one
        // fog-honest owner. `map` may be null, in which case terrain is simply not known to this
        // caller and only the remembered structural defence contributes.
        public static float KnownSiteDefenceBonus(WorldSnapshot snap, HexMap map, HexCoord hex) =>
            AiMapMemory.KnownHexDefenseBonus(snap?.Observer, map, hex);

        // Every known hostile body standing on the site, as ONE defender package (§31). A garrison
        // and two field armies sitting on the same Base are one fight, never three objectives.
        public static List<WorthIt.DefenderProfile> KnownSiteDefenders(WorldSnapshot snap, HexCoord hex)
        {
            var defenders = new List<WorthIt.DefenderProfile>();
            IEnumerable<AiMapMemory.KnownEnemySighting> sightings = snap?.Known?.EnemySightings
                ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>();
            foreach (AiMapMemory.KnownEnemySighting s in sightings
                .Where(s => s.Hex.Equals(hex))
                .OrderBy(s => s.ArmyId))
            {
                if (s.Defenders != null)
                    defenders.AddRange(s.Defenders);
            }
            return defenders;
        }

        // ---- response terms (§35) -------------------------------------------------------------

        // The response half of the Attack score, folded onto the intrinsic objective score exactly
        // the way ActiveDefenceObjectiveEvaluator.WithResponse already does for its own lane: the
        // win chance the shared estimator produced for this concrete force against this concrete
        // site (hex defence included), the AP/ETA price of delivering it, and the cost of taking
        // this particular army off whatever it is doing now. No Attack-specific slot.
        public static TaskScore WithResponse(AttackObjective objective, ArmySnapshot actor,
            float winChance, int eta, float moverOpportunityCost = 0f,
            int? projectedActivationAp = null)
        {
            TaskScore s = objective.TaskScore;
            float activation = actor != null && !actor.HasActivatedThisTurn
                ? Mathf.Max(0, projectedActivationAp ?? actor.ActivationApCost) : 0f;
            return new TaskScore(
                staleness: s.Staleness,
                strategicRelevance: s.StrategicRelevance,
                threatDirection: s.ThreatDirection,
                ownTerritoryProximity: s.OwnTerritoryProximity,
                frontProgress: s.FrontProgress,
                corridorAlignment: s.CorridorAlignment,
                economicExpansionValue: s.EconomicExpansionValue,
                winChance: TaskScoreEvaluator.WinChance(winChance),
                cardPrice: activation * AiConfigV2.taskScoreReactivationApWeight,
                delivery: TaskScoreEvaluator.DeliveryFromEta(
                    projectedActivationAp ?? actor?.ActivationApCost ?? 0,
                    eta, AiConfigV2.taskScoreReactivationApWeight),
                moverOpportunityCost: Mathf.Max(0f, moverOpportunityCost));
        }

        // ---- internals -------------------------------------------------------------------------

        // §19 — Owner != us, Owner != Neutral, owner not eliminated, and the structure is a Base or
        // a starting Citadel. A Facility that is not a Base is explicitly NOT an Attack target.
        internal static bool IsHostileStrategicStructure(AiMapMemory.KnownBuilding b,
            PlayerSetupData player) =>
            b.Owner != null && b.Owner != player && !b.Owner.IsNeutral && !b.Owner.IsEliminated
            && (b.IsBase || b.IsStartingCitadel);

        private static AttackObjective Build(WorldSnapshot snap, PlayerSetupData player,
            AiMapMemory.KnownBuilding b, bool hasDirection, HexCoord anchor, HexCoord directionTarget)
        {
            AttackTargetKind kind = b.IsStartingCitadel
                ? AttackTargetKind.Citadel : AttackTargetKind.Base;
            List<WorthIt.DefenderProfile> defenders = KnownSiteDefenders(snap, b.Hex);

            // §34 — the site's own defence is NOT a positive term for the attacker. It is priced
            // exactly once, as a reduction of WinChance through the shared WorthIt estimator (the
            // bonus GroundCombatAssemblyRequest.DefenderHexDefenseBonus carries), so TerrainDefense
            // is deliberately left at zero here. Defender power likewise stays out of
            // MilitaryTargetRelevance: it already lowers WinChance and already shows up as
            // ThreatDirection where those defenders genuinely threaten us.
            float assetNorm = (kind == AttackTargetKind.Citadel
                ? AiConfigV2.assetValueCitadel : AiConfigV2.assetValueBase)
                / Mathf.Max(1f, AiConfigV2.assetValueCitadel);

            int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, b.Hex);
            int intelAge = IntelAge(snap, b);

            float frontProgress = hasDirection
                ? WorldAnalysis.ForwardProgressToward(anchor, directionTarget, b.Hex) : 0f;
            float corridorAlignment = hasDirection
                ? WorldAnalysis.CorridorAlignmentToward(anchor, directionTarget, b.Hex,
                    AiConfigV2.attackCorridorDetourScale) : 0f;

            var score = new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(assetNorm),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                frontProgress: TaskScoreEvaluator.FrontProgress(frontProgress),
                corridorAlignment: TaskScoreEvaluator.CorridorAlignment(corridorAlignment),
                threatDirection: TaskScoreEvaluator.ThreatDirection(SiteThreatToUs(snap, b.Hex)),
                staleness: TaskScoreEvaluator.StaleIntelPenalty(
                    intelAge / (float)Mathf.Max(1, AiConfigV2.scoutSurveilStaleTurnsHi)));
            // EconomicExpansionValue is deliberately NOT populated (§35/§77). It may only be filled
            // when the existing Economy network model genuinely proves that holding this node opens
            // a resource cluster we cannot already reach — a generic "more territory is good"
            // multiplier would be exactly the invented war justification §43 forbids.

            return new AttackObjective
            {
                Target = AttackTargetRef.For(b.Hex, b.Owner, kind),
                Defenders = defenders,
                DefenderCount = defenders.Count,
                TargetPower = AiPower.EffectiveArmyPowerFromProfiles(defenders),
                IntelAgeTurns = intelAge,
                TaskScore = score,
            };
        }

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
