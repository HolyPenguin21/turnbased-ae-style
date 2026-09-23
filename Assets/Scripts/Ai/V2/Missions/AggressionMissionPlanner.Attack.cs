using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §40 — THE ATTACK MISSION PROPOSALS.
    //
    //  A mechanical partial of AggressionMissionLayer, not a second owner: the Aggression mission
    //  planner stays the one place Aggression proposals are built, and this file only holds the
    //  Attack lane's own semantics (which structure, which defender package, which leg). Every
    //  physical decision below — who can take the fight, what the assembled roster costs, how fast
    //  it travels — is asked of the shared GroundCombat kernel, exactly as Raid and ActiveDefence
    //  already ask it.
    // ===========================================================================================
    internal static partial class AggressionMissionLayer
    {
        internal static void AppendAttack(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ISet<int> committed,
            List<MissionProposal> proposals, AiTurnContext ctx)
        {
            if (snap?.Self == null)
                return;

            // ---- durable legs that carry their own pinned actor and destination ---------------
            if (activeIntents != null)
                foreach (MissionIntent intent in activeIntents.Where(i => i?.Attack != null
                    && i.Status == IntentStatus.Active))
                {
                    AttackIntent a = intent.Attack;
                    if (a.Phase == AttackMissionPhase.RecoveryReturn)
                        AppendAttackWalkHome(snap, intent, a, AttackMissionPhase.RecoveryReturn,
                            a.PrimaryArmyId, a.RecoveryBaseHex, proposals);
                    else if (a.Phase == AttackMissionPhase.SupportReturn)
                        AppendAttackWalkHome(snap, intent, a, AttackMissionPhase.SupportReturn,
                            a.SupportArmyId, a.SupportReturnHex, proposals);
                    else if (a.Phase == AttackMissionPhase.Reinforcement)
                        AppendAttackReinforcement(snap, intent, a, committed, proposals, ctx);
                }

            // ---- Assault: fresh objectives and incumbents still marching on their target ------
            foreach (AttackObjective objective in AttackObjectiveEvaluator.Enumerate(snap))
            {
                MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                    && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack
                    && i.Attack != null && i.Attack.Target.Equals(objective.Target));
                // An incumbent already past the Assault leg is being proposed above; do not also
                // offer it a fresh assault against the same target this pass.
                if (incumbent != null && incumbent.Attack.Phase != AttackMissionPhase.Assault)
                    continue;

                int? pinnedActor = incumbent?.Attack?.PrimaryArmyId;
                var excluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
                if (pinnedActor.HasValue)
                    excluded.Remove(pinnedActor.Value);

                IReadOnlyList<WorthIt.DefenderProfile> defenders = objective.Defenders;
                // §30 — the honest, knowledge-scoped answer to "what defence does a defender on
                // that hex actually get". Never a live BuildingRegistry read.
                float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(
                    snap, ctx?.Map, objective.Hex);

                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                    new GroundCombatAssemblyRequest
                    {
                        Defenders = defenders,
                        WinChanceGate = pinnedActor.HasValue
                            ? GroundCombatAdmissionPolicy.ContinuationWinChanceFloor
                            : GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                        PreferredPrimaryArmyId = pinnedActor,
                        PinToPreferred = pinnedActor.HasValue,
                        ExcludedArmyIds = excluded,
                        DefenderHexDefenseBonus = hexBonus,
                    });

                if (!plan.Feasible)
                {
                    // §24 — a started operation whose primary can no longer clear the site is a
                    // REINFORCEMENT decision, not a dead objective. Continuity moves the phase;
                    // the planner only refrains from proposing an impossible assault.
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                        $"[AI][V2][Attack][Assembly] decision=REJECT target={objective.Target.DiagnosticLabel} "
                        + $"reason={plan.Reason}");
                    continue;
                }

                ArmySnapshot actor = snap.Self.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == plan.BaseArmyId);
                if (actor == null)
                    continue;

                int distance = HexGridMath.Distance(actor.Hex, objective.Hex);
                // Price and time the force this plan will ACTUALLY field, through the same
                // projections Raid and ActiveDefence use — never a host-only figure.
                int projectedMove = GroundCombatAssemblyPlanner.ProjectedMaxMovement(snap, plan)
                    ?? actor.MaxMovement;
                int eta = AiV2Util.CeilDiv(distance,
                    Mathf.Max(AiConfigV2.etaFallbackMoveBudget, projectedMove));
                int? projectedAp = GroundCombatAssemblyPlanner.ProjectedActivationApCost(snap, plan);
                TaskScore score = AttackObjectiveEvaluator.WithResponse(objective, actor,
                    plan.ProjectedWinChance, eta, 0f, projectedAp);

                var target = new AttackMissionTarget
                {
                    Phase = AttackMissionPhase.Assault,
                    Target = objective.Target,
                    PrimaryArmyId = actor.ArmyId,
                    DestinationHex = objective.Hex,
                    DefenderHexDefenseBonus = hexBonus,
                    DefenderCount = objective.DefenderCount,
                    ProjectedWinChance = plan.ProjectedWinChance,
                    CoversAllDefenders = plan.CoversAllDefenders,
                    EstimatedEta = eta,
                    // §17 — carry the operation's own once-per-turn side-strike marker into the leg
                    // the executor will run. A fresh objective has no incumbent and therefore no
                    // marker, which is exactly right: it has taken no strike yet.
                    OpportunisticStrikeTurn =
                        incumbent?.Attack?.LastOpportunisticStrikeTurn ?? 0,
                };
                float ap = actor.HasActivatedThisTurn ? 0f
                    : projectedAp ?? actor.ActivationApCost;
                var proposal = new MissionProposal
                {
                    Kind = MissionKind.Attack,
                    Target = target,
                    BaseValue = score.Value,
                    LocalAdmissionScore = score.Value,
                    PreferredMoverArmyId = actor.ArmyId,
                    FromDurableIntent = incumbent != null,
                    DurableFundingTier = incumbent?.Funding ?? CommitmentTier.None,
                    Requirements = new MissionRequirements
                    {
                        MoverKnown = true,
                        RequiresArmy = true,
                        ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                        EtaTurns = eta, EstimatedDistance = distance,
                        CombatPowerMinimum = objective.TargetPower,
                        CombatPowerDesired = objective.TargetPower,
                    },
                    Explain = $"Attack {objective.Target.DiagnosticLabel} "
                        + $"task {F(score.Value)} win {F(plan.ProjectedWinChance)} "
                        + $"defenders {objective.DefenderCount} hexDef {F(hexBonus)} eta {eta}",
                };
                proposal.Axes.Value[DesireAxis.Aggression] = 1f;

                GroundCombatAdmissionRegistry.RecordAttack(proposal, snap, defenders, hexBonus, excluded);
                if (!GroundCombatAdmissionRegistry.TryGet(proposal, out HashSet<int> eligible)
                    || eligible.Count == 0)
                {
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                        $"[AI][V2][Attack][Admission] decision=SUPPRESS target={objective.Target.DiagnosticLabel} "
                        + "reason=no_ready_ground_actor_after_phaseA");
                    continue;
                }

                proposals.Add(proposal);
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                    $"[AI][V2][Attack][Admission] decision=PROPOSE target={objective.Target.DiagnosticLabel} "
                    + $"actor={actor.ArmyId} score={F(score.Value)} eligible=[{GroundCombatAdmissionRegistry.EligibleIds(proposal)}]");
            }
        }

        // §24/§47 — a walking-home leg (RecoveryReturn / SupportReturn). Lifecycle work, not fresh
        // strategic target scoring: its execution priority comes from the durable commitment, so the
        // intrinsic score stays neutral and cannot out-rank unrelated lanes.
        private static void AppendAttackWalkHome(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, AttackMissionPhase phase, int? moverArmyId, HexCoord? destination,
            List<MissionProposal> proposals)
        {
            if (!moverArmyId.HasValue || !destination.HasValue)
                return;
            ArmySnapshot actor = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == moverArmyId.Value);
            if (actor == null)
                return;

            int distance = HexGridMath.Distance(actor.Hex, destination.Value);
            int eta = AiV2Util.CeilDiv(distance, Mathf.Max(1, actor.MaxMovement));
            float ap = actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
            var target = new AttackMissionTarget
            {
                Phase = phase,
                Target = a.Target,
                PrimaryArmyId = a.PrimaryArmyId,
                SupportArmyId = a.SupportArmyId,
                DestinationHex = destination.Value,
                RecoveryBaseHex = a.RecoveryBaseHex,
                SupportReturnHex = a.SupportReturnHex,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = a.LastOpportunisticStrikeTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = 0f,
                LocalAdmissionScore = 0f,
                PreferredMoverArmyId = actor.ArmyId,
                FromDurableIntent = true,
                DurableFundingTier = intent.Funding,
                Requirements = new MissionRequirements
                {
                    MoverKnown = true, RequiresArmy = true,
                    ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                    EtaTurns = eta, EstimatedDistance = distance,
                },
                Explain = $"Attack {phase} actor #{actor.ArmyId} -> "
                    + $"({destination.Value.Q},{destination.Value.R})",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            proposals.Add(proposal);
        }

        // §46 — reinforcement uses the shared ground-combat reinforcement mechanics. When a support
        // army is already bound, this is the convoy/handoff leg. When none is bound the planner
        // proposes nothing and holds: asking for a NEW capability is the Demand layer's decision,
        // never the mission planner's (exactly the rule the Raid lane already follows).
        private static void AppendAttackReinforcement(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ISet<int> committed, List<MissionProposal> proposals, AiTurnContext ctx)
        {
            if (!a.PrimaryArmyId.HasValue)
                return;
            ArmySnapshot primary = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            if (primary == null)
                return;

            if (!a.SupportArmyId.HasValue)
            {
                // §46 — an EXISTING free army is an actor-contention decision, not a capability
                // request: it belongs in the SAME batch solve the assault legs run through, exactly
                // as the Raid lane's unpinned reinforcement leg already does. Without this leg the
                // operation sat in Reinforcement forever — the demand layer correctly answered
                // "an existing free army can solve this, materialise nothing", and nothing ever
                // proposed the join. Only when no free army exists at all does the planner hold and
                // let Aggression demand ask Production for one.
                AppendAttackUnpinnedReinforcement(snap, intent, a, primary, committed, proposals, ctx);
                return;
            }

            ArmySnapshot support = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.SupportArmyId.Value);
            if (support == null)
                return;

            int distance = HexGridMath.Distance(support.Hex, primary.Hex);
            int eta = AiV2Util.CeilDiv(distance, Mathf.Max(1, support.MaxMovement));
            float ap = support.HasActivatedThisTurn ? 0f : support.ActivationApCost;
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Reinforcement,
                Target = a.Target,
                PrimaryArmyId = a.PrimaryArmyId,
                SupportArmyId = a.SupportArmyId,
                DestinationHex = primary.Hex,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = a.LastOpportunisticStrikeTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = 0f,
                LocalAdmissionScore = 0f,
                PreferredMoverArmyId = support.ArmyId,
                FromDurableIntent = true,
                DurableFundingTier = intent.Funding,
                Requirements = new MissionRequirements
                {
                    MoverKnown = true, RequiresArmy = true,
                    ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                    EtaTurns = eta, EstimatedDistance = distance,
                },
                Explain = $"Attack Reinforcement support #{support.ArmyId} -> primary "
                    + $"#{a.PrimaryArmyId} at ({primary.Hex.Q},{primary.Hex.R})",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            proposals.Add(proposal);
        }

        // §46 — the UNPINNED reinforcement leg: "some existing free army should join this primary",
        // with the actor left to the one batch solve (PrepareGroundCombatAssignments) exactly as the
        // Raid lane leaves it. Nothing is picked here; the eligible set is published through the one
        // admission registry, and the AP envelope is priced off the candidate that same solve
        // prefers first (cheapest activation, then weakest, then lowest id) so funding matches the
        // actor it is most likely to bind.
        private static void AppendAttackUnpinnedReinforcement(WorldSnapshot snap,
            MissionIntent intent, AttackIntent a, ArmySnapshot primary, ISet<int> committed,
            List<MissionProposal> proposals, AiTurnContext ctx)
        {
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                AttackObjectiveEvaluator.KnownSiteDefenders(snap, a.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, a.Target.Hex);
            List<int> candidates = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                snap, a.PrimaryArmyId.Value, defenders, committed, hexBonus);
            if (candidates.Count == 0)
            {
                AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                    $"[AI][V2][Attack] decision=HOLD {intent.IntentKey}: primary #{a.PrimaryArmyId} "
                    + "waits; no existing free army improves the assault (Aggression demand owns "
                    + "the request for a new one)");
                return;
            }

            ArmySnapshot priced = snap.Self.Armies?
                .Where(x => x != null && candidates.Contains(x.ArmyId))
                .OrderBy(x => x.HasActivatedThisTurn ? 0 : x.ActivationApCost)
                .ThenBy(x => x.EffectiveArmyPower)
                .ThenBy(x => x.ArmyId)
                .FirstOrDefault();
            float ap = priced != null && !priced.HasActivatedThisTurn ? priced.ActivationApCost : 0f;
            int distance = priced == null ? 0 : HexGridMath.Distance(priced.Hex, primary.Hex);
            int eta = priced == null ? 1
                : AiV2Util.CeilDiv(distance, Mathf.Max(1, priced.MaxMovement));

            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Reinforcement,
                Target = a.Target,
                PrimaryArmyId = a.PrimaryArmyId,
                SupportArmyId = null,
                DestinationHex = primary.Hex,
                DefenderHexDefenseBonus = hexBonus,
                DefenderCount = defenders.Count,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = a.LastOpportunisticStrikeTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = 0f,
                LocalAdmissionScore = 0f,
                PreferredMoverArmyId = priced?.ArmyId,
                FromDurableIntent = true,
                DurableFundingTier = intent.Funding,
                Requirements = new MissionRequirements
                {
                    MoverKnown = priced != null, RequiresArmy = true,
                    ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                    EtaTurns = Mathf.Max(1, eta), EstimatedDistance = distance,
                },
                Explain = $"Attack {a.Target.DiagnosticLabel} Reinforcement: select an existing free "
                    + $"support for primary #{a.PrimaryArmyId} at ({primary.Hex.Q},{primary.Hex.R}); "
                    + $"{candidates.Count} candidate(s); Hard funding protection is allocator-owned",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            GroundCombatAdmissionRegistry.RecordReinforcement(proposal, snap);
            proposals.Add(proposal);
            AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                $"[AI][V2][Attack][Admission] decision=REINFORCE-SELECT {intent.IntentKey} "
                + $"primary={a.PrimaryArmyId} candidates={candidates.Count} "
                + $"eligible=[{GroundCombatAdmissionRegistry.EligibleIds(proposal)}]");
        }
    }
}
