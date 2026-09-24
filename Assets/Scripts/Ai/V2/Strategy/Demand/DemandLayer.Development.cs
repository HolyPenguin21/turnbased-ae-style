using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // DevelopmentDemands and its private helpers.
    // File-split (mechanical, no behaviour change) from DemandLayer.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 4. Still exactly the DemandLayer
    // class; only this axis's slice moved to its own file.
    public static partial class DemandLayer
    {
        private static IEnumerable<AxisDemand> DevelopmentDemands(WorldSnapshot s, DesireBreakdown b,
            IReadOnlyList<DevelopmentOpportunity> devOpportunities,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player, AiTurnContext ctx, PlayerRoot root)
        {
            // Radar §E — AxisDemand.Value is the demand's OWN intrinsic merit, never pre-scaled by
            // radar. Radar is applied exactly once, at the point competing spends are compared
            // (MissionProposal.EffectiveValue / the allocator); Demand/urgency thresholds must not
            // shift just because the radar weight moved.
            if (s?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][Demand][Development] decision=NONE reason=no_self_snapshot");
                yield break;
            }

            AiHandData hand = AiHandRegistry.Peek(player);
            int operatorPrerequisites = 0;
            // Production amplifies an ALREADY-owned need (Recon/Economy/Raid); it never invents a
            // mission of its own (owner principle: Recon -> knowledge -> Economy -> budget ->
            // Attack/Defence -> need -> Production amplifies need). HasSupportedDevelopmentAxisDemand
            // is the one existing predicate for "is there a real, witnessed need this closes" —
            // EnumeratePreparation/Enumerate still own the catalog/recipient/operator/cost checks,
            // this only gates WHETHER an opportunity is allowed to compete at all.
            // One best prerequisite per pass; the next settled pass sees the completed stage.
            bool SupportsNeed(DevelopmentOpportunity op) => op != null && op.ExpectedGain > 0f
                && HasSupportedDevelopmentAxisDemand(op, formedDemands, activeIntents, player, s, ctx);
            DevelopmentOpportunity preparation = DevelopmentOpportunityEvaluator.EnumeratePreparation(
                s, player, root, hand, ctx, SupportsNeed,
                activeIntents).FirstOrDefault();
            if (preparation != null)
            {
                bool facilityReady = s.Development?.Facilities?.Any(f => f.Mode == preparation.Mode
                    && f.Hex.Equals(preparation.FacilityHex)) == true;
                operatorPrerequisites++;
                yield return new AxisDemand
                {
                    RequestingAxis = DesireAxis.Development,
                    Capability = facilityReady ? CapabilityKind.DevelopmentOperator
                        : CapabilityKind.DevelopmentInfrastructure,
                    DesiredAmount = 1,
                    TargetHex = preparation.FacilityHex,
                    DevelopmentOperatorMode = preparation.Mode,
                    DevOpportunity = preparation,
                    Value = preparation.BaseValue,
                    Explain = preparation.Explain,
                };
            }
            // Recipient legality (CanAttach, signed card utility) is filtered by the opportunity
            // evaluator itself before "best" is chosen. Orchestration already computed and
            // refreshed this list for the settled state; Enumerate here only for callers that did
            // not supply it (formedDemands/activeIntents exist here, so this fallback call can gate
            // on real need same as the primary path below).
            if (devOpportunities == null && root != null && hand != null)
                devOpportunities = DevelopmentOpportunityEvaluator.Enumerate(
                    s, player, root, hand, null, SupportsNeed);
            // An unstaffed mode must not suppress real opportunities from another ready mode.
            int emitted = 0;
            if (devOpportunities != null)
                foreach (DevelopmentOpportunity op in devOpportunities)
                {
                    // Orchestration's own Enumerate call (AiStrategyV2Pipeline step 3e) runs before
                    // activeIntents/formedDemands exist this turn, so it cannot gate at
                    // best-recipient selection time; re-check the SAME predicate here, the one
                    // place in the pipeline that has real demand/intent context, before this
                    // opportunity is ever allowed to become a competing AxisDemand.
                    if (op == null || op.BaseValue <= 0f || !SupportsNeed(op)) continue;
                    emitted++;
                    float supportStrength = SupportedNeedStrength(op, formedDemands, activeIntents, player, s);
                    var devScore = new TaskScore(
                        supportedNeedValue: TaskScoreEvaluator.SupportedNeedValue(supportStrength));
                    yield return new AxisDemand
                    {
                        RequestingAxis = DesireAxis.Development,
                        Capability = CapabilityKind.CardUpgrade,
                        DesiredAmount = 1,
                        RequiredTraits = TraitPreference.None,
                        MinimumFollowupAp = 0f,
                        TargetHex = op.FacilityHex,
                        WorldTaskScore = devScore,
                        Value = devScore.Value,
                        DevOpportunity = op,
                        Explain = $"{op.Explain} supportedNeed={supportStrength:0.##}",
                    };
                }

            if (emitted > 0)
                AiDebugLog.WriteDeduped("decision",
                    $"[AI][V2][Demand][Development] decision=UPGRADE count={emitted} "
                    + "reason=facility_ready_scored_opportunities");
            else if (operatorPrerequisites == 0)
                AiDebugLog.WriteDeduped("decision", "[AI][V2][Demand][Development] decision=SATISFIED "
                    + "reason=no_profitable_development_use");
        }


        // Production amplifies an already-owned need; never invent a mission. Recon and
        // Economy keep their existing evidence; a bound active GROUND-COMBAT operation can ALSO
        // witness equipment when its own primary combat roster improves against the known
        // defenders by WorthIt. ATK §42 — "ground-combat operation" is every lane that fights on
        // the ground, not Raid alone.
        // Independent reinforcement FieldCombatPower demands are NOT fulfilled by upgrading
        // their existing primary: only a separate deployable army can close those.
        internal static bool HasSupportedDevelopmentAxisDemand(DevelopmentOpportunity op,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player, WorldSnapshot snap = null, AiTurnContext ctx = null)
        {
            if (op == null)
                return false;

            bool hasReconDemand = formedDemands?.Any(d => d != null
                && d.RequestingAxis == DesireAxis.Recon
                && d.Capability == CapabilityKind.ScoutCapability) == true;
            if (op.RecipientKind == DevRecipientKind.HandCard)
                return hasReconDemand && ImprovesReconCapability(op);

            if (!TryResolveRecipientArmy(op, player, out ArmyData army))
                return false;

            // AI-03 — membership in an economic task is NECESSARY but NOT SUFFICIENT. Previously
            // this returned true the moment the recipient's army was the designated builder or
            // carried a live BuildExtraction/FoundBase intent, so ANY equipment with a positive
            // generic ExpectedGain was admitted as "production supports a need" without ever
            // asking whether it helps THAT economic project. The witness now only names the
            // specific obligation; the admission itself demands a concrete before/after proof on
            // the real executor (EquipmentSystem.Predict + the existing income/pathing/WorthIt
            // mechanisms). This stays a boolean gate — no new score, no new coefficient.
            foreach (EconomicObligation obligation in
                     EconomicObligationsOf(army, formedDemands, activeIntents))
                if (ImprovesEconomicObligation(op, army, obligation, player, snap, ctx))
                    return true;

            bool reconWitness = activeIntents?.Any(i => i != null
                && i.Status == IntentStatus.Active && i.Kind == MissionKind.Scout
                && i.PreferredMoverArmyId == army.Id) == true;
            if (reconWitness && ImprovesReconCapability(op))
                return true;

            // Only an ACTUAL owned ground-combat primary may justify strengthening its existing
            // unit. Research/Production mode has no bearing here: the offered output must be
            // Equipment. A potential future operation (or an independent reinforcement demand)
            // cannot create a generic "upgrade the strongest body" entitlement without a named
            // recipient.
            if (snap == null || op.RecipientUnit.IsHero || op.Card?.cardType != CardType.Equipment
                || op.Card.equipment == null || activeIntents == null)
                return false;
            foreach (MissionIntent intent in activeIntents)
            {
                if (!TryBoundGroundCombatFight(intent, army.Id, snap,
                        out IReadOnlyList<WorthIt.DefenderProfile> defenders,
                        out HexCoord fightHex))
                    continue;
                // The same defender-side structural bonus enters both immutable projections.
                // Terrain is not present in the snapshot, so this is a marginal signal, never a
                // substitute for the lane's final live WorthIt admission.
                // The target hex may well be fogged (that is the normal case for a
                // last-known position). WorthIt.HexDefenseBonus would read the live
                // BuildingRegistry there and leak a structure we have not observed; AiMapMemory
                // answers the same question from what this player actually knows.
                float hexBonus = AiMapMemory.KnownHexDefenseBonus(player, ctx?.Map, fightHex);
                if (ImprovesGroundCombatOutcome(op.RecipientUnit, army.Members,
                    op.Card.equipment, defenders, hexBonus))
                    return true;
            }
            return false;
        }

        // ATK §42 — the ONE witness translation from "this army is bound to a live ground-combat
        // operation" to the concrete fight that operation is committed to: which defenders, on
        // which hex. Every ground-combat lane answers the SAME question, so the equipment proof
        // below stays a single shared before/after comparison instead of one copy per lane
        // (ImprovesRaidCombatOutcome / ImprovesAttackCombatOutcome / ...). A leg that is walking
        // home rather than fighting (Raid Return/SupportReturn/RecoveryReturn, ActiveDefence
        // Return) is deliberately NOT a fight and witnesses nothing.
        internal static bool TryBoundGroundCombatFight(MissionIntent intent, int armyId,
            WorldSnapshot snap, out IReadOnlyList<WorthIt.DefenderProfile> defenders,
            out HexCoord fightHex)
        {
            defenders = System.Array.Empty<WorthIt.DefenderProfile>();
            fightHex = default;
            if (intent == null || intent.Status != IntentStatus.Active)
                return false;

            if (intent.Kind == MissionKind.Raid)
            {
                RaidIntent raid = intent.Raid;
                if (raid == null || !raid.Target.HasValue || raid.PrimaryArmyId != armyId
                    || (raid.Phase != RaidMissionPhase.Assault
                        && raid.Phase != RaidMissionPhase.Reinforcement))
                    return false;
                defenders = AiV2Util.KnownDefenders(snap, raid.Target);
                fightHex = raid.LastKnownHex;
                return defenders.Count > 0;
            }

            if (intent.Kind == MissionKind.ActiveDefence)
            {
                ActiveDefenceIntent defence = intent.ActiveDefence;
                if (defence == null || defence.Phase != ActiveDefencePhase.Intercept
                    || defence.PrimaryArmyId != armyId)
                    return false;
                defenders = AiV2Util.KnownDefenders(snap, defence.EnemyArmyId);
                fightHex = defence.LastKnownHex;
                return defenders.Count > 0;
            }

            // ATK §78 — Development may strengthen a committed Attack primary, but only on the same
            // proof every other lane must give: the SAME bound roster, the SAME known site
            // defenders, a real WorthIt before/after. "Equipment makes an army stronger" is not
            // enough, and a withdrawing or reinforcing-support leg is not a fight this actor is in.
            if (intent.Kind == MissionKind.Attack)
            {
                AttackIntent attack = intent.Attack;
                if (attack == null || !attack.Target.HasValue
                    || attack.PrimaryArmyId != armyId
                    || (attack.Phase != AttackMissionPhase.Assault
                        && attack.Phase != AttackMissionPhase.Reinforcement))
                    return false;
                defenders = AttackObjectiveEvaluator.KnownSiteDefenders(snap, attack.Target.Hex);
                fightHex = attack.Target.Hex;
                return defenders.Count > 0;
            }

            return false;
        }

        // =====================================================================================
        //  AI-03 — the ONE specific economic obligation an equipment recipient's army is bound to,
        //  and the proof that a specific grant actually improves it.
        // =====================================================================================

        // Identity of the live economic project the recipient's army is committed to. Carries only
        // facts the existing layers already own (Economy demand / EconomyIntent); nothing here is
        // recomputed or invented.
        private readonly struct EconomicObligation
        {
            public readonly EconomyTaskKind Kind;
            public readonly HexCoord TargetHex;
            public readonly ResourceType? Resource;
            // A Return leg is a still-necessary journey, never an outstanding build/extraction
            // obligation: only delivery and protection of that leg may justify equipment on it,
            // so a completed economy mission can never mint a fresh production need on its way home.
            public readonly bool IsReturnLegOnly;

            public EconomicObligation(EconomyTaskKind kind, HexCoord targetHex,
                ResourceType? resource, bool isReturnLegOnly)
            {
                Kind = kind;
                TargetHex = targetHex;
                Resource = resource;
                IsReturnLegOnly = isReturnLegOnly;
            }
        }

        // The old `economyWitness` boolean, now yielding WHICH obligations it witnessed. An army
        // may legitimately hold more than one (a committed intent plus a fresh demand), and a grant
        // that helps any ONE of them supports a real need, so every one is offered to the proof —
        // the gate must not depend on which happened to be found first. Live intents come first:
        // they are the committed projects. The demand path keeps the existing
        // EconomyPreferredBuilderArmyId contract.
        private static IEnumerable<EconomicObligation> EconomicObligationsOf(ArmyData army,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents)
        {
            if (army == null)
                yield break;

            foreach (MissionIntent i in activeIntents
                         ?? (IReadOnlyList<MissionIntent>)System.Array.Empty<MissionIntent>())
            {
                EconomyIntent e = i?.Economy;
                if (i == null || i.Status != IntentStatus.Active
                    || i.Kind != MissionKind.Economy || e == null)
                    continue;
                bool bound = i.PreferredMoverArmyId == army.Id
                    || e.BuilderArmyId == army.Id || e.CollectorArmyId == army.Id;
                if (!bound)
                    continue;
                bool returnLeg = e.Kind == EconomyTaskKind.ReturnBuilder
                    || e.Kind == EconomyTaskKind.ReturnCollector;
                HexCoord destination = returnLeg && e.SafeReturnHex.HasValue
                    ? e.SafeReturnHex.Value : e.TargetHex;
                yield return new EconomicObligation(e.Kind, destination, e.ResourceType, returnLeg);
            }

            foreach (AxisDemand d in formedDemands
                         ?? (IReadOnlyList<AxisDemand>)System.Array.Empty<AxisDemand>())
            {
                if (d == null || d.RequestingAxis != DesireAxis.Economy
                    || d.EconomyPreferredBuilderArmyId != army.Id || !d.TargetHex.HasValue)
                    continue;
                // A demand is always an outstanding build/collect project; a return leg only
                // ever exists as an intent.
                yield return new EconomicObligation(
                    d.EconomyResourceType.HasValue
                        ? EconomyTaskKind.BuildExtraction : EconomyTaskKind.FoundBase,
                    d.TargetHex.Value, d.EconomyResourceType, false);
            }
        }

        // Does THIS grant concretely improve THIS economic obligation? Exactly one of the four
        // admissible proofs must hold. Every one of them is a real before/after comparison on the
        // real executor through a system that already owns that rule — EquipmentSystem.Predict for
        // the stat/ability delta, ArmyData's own slowest-member/sum rules for the army-level
        // effect, SafeStepPathing for reachability, IncomeProjection for income and WorthIt for
        // combat. Nothing here scores; a true answer only lets the existing EV/TaskScore pipeline
        // proceed unchanged.
        private static bool ImprovesEconomicObligation(DevelopmentOpportunity op, ArmyData army,
            in EconomicObligation obligation, PlayerSetupData player, WorldSnapshot snap,
            AiTurnContext ctx)
        {
            EquipmentGrant grant = op?.Card?.equipment;
            UnitData recipient = op?.RecipientUnit;
            if (grant == null || recipient == null || army?.Members == null
                || !army.Members.Contains(recipient))
                return false;

            // Normalized before/after of the recipient. Predict(null, ...) is the BEFORE state so a
            // capability the host already effectively has (RapidReaction already zeroing activation
            // AP, a Collect ability already present) can never be credited to this grant.
            var stats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.MoveMax] = recipient.MoveMax,
                [EquipmentStat.ActivationApCost] = recipient.ActivationApCost,
            };
            PredictedEquipmentState before = EquipmentSystem.Predict(null, stats, recipient.Abilities);
            PredictedEquipmentState after = EquipmentSystem.Predict(grant, stats, recipient.Abilities);
            int beforeMove = before.Stats.TryGetValue(EquipmentStat.MoveMax, out int bMove)
                ? bMove : recipient.MoveMax;
            int afterMove = after.Stats.TryGetValue(EquipmentStat.MoveMax, out int aMove)
                ? aMove : beforeMove;
            int beforeActivation = before.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int bAp) ? bAp : recipient.ActivationApCost;
            int afterActivation = after.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int aAp) ? aAp : beforeActivation;

            if (ImprovesEconomicDelivery(army, recipient, obligation, ctx,
                    beforeMove, afterMove, beforeActivation, afterActivation))
                return true;

            // A return leg has no build/extraction obligation left to improve.
            if (!obligation.IsReturnLegOnly
                && ImprovesEconomicExtraction(obligation, army, recipient, snap,
                    before.Abilities, after.Abilities))
                return true;

            return ImprovesEconomicProtection(op, army, recipient, grant, obligation, player, snap, ctx);
        }

        // Delivery: the mission's actual journey gets cheaper, faster or newly possible.
        // "Faster" is the ARMY's shared movement (ArmyData's slowest-member rule), never the
        // recipient's own MoveMax — raising a fast unit's ceiling while a slower member still gates
        // the formation changes nothing about delivery and must not admit the grant.
        // "Cheaper" is the army's summed activation AP (ArmyData.ComputeActivationApCost), which is
        // also the AP this project's build action competes with out of the same pool.
        private static bool ImprovesEconomicDelivery(ArmyData army, UnitData recipient,
            in EconomicObligation obligation, AiTurnContext ctx,
            int beforeMove, int afterMove, int beforeActivation, int afterActivation)
        {
            if (army.Hex.Equals(obligation.TargetHex))
                return false;   // already there — nothing left to deliver

            int armyMoveBefore = int.MaxValue, armyMoveAfter = int.MaxValue;
            int armyApBefore = 0, armyApAfter = 0;
            foreach (UnitData u in army.Members)
            {
                if (u == null) continue;
                bool isRecipient = object.ReferenceEquals(u, recipient);
                armyMoveBefore = Mathf.Min(armyMoveBefore, isRecipient ? beforeMove : u.MoveMax);
                armyMoveAfter = Mathf.Min(armyMoveAfter, isRecipient ? afterMove : u.MoveMax);
                armyApBefore += isRecipient ? beforeActivation : u.ActivationApCost;
                armyApAfter += isRecipient ? afterActivation : u.ActivationApCost;
            }
            if (armyMoveBefore == int.MaxValue)
                return false;

            if (armyApAfter < armyApBefore)
                return true;                       // strictly cheaper reactivation for this route
            if (armyMoveAfter <= armyMoveBefore)
                return false;                      // bottleneck unchanged — no delivery effect

            // A raised bottleneck is only a real improvement if it shortens or unblocks THIS
            // mission's route. Same pathing rules, same blockers, same fog: only the movement
            // budget the question is asked with changes.
            if (ctx?.Map == null)
                return false;
            int costBefore = SafeStepPathing.FindSafePathCost(ctx.Map, army.Owner, army.Hex,
                obligation.TargetHex, armyMoveBefore);
            int costAfter = SafeStepPathing.FindSafePathCost(ctx.Map, army.Owner, army.Hex,
                obligation.TargetHex, armyMoveAfter);
            if (costAfter < costBefore)
                return true;                       // includes "was unreachable, now reachable"
            // Fewer turns on the same route is a genuine timeline improvement.
            if (costAfter != int.MaxValue
                && AiV2Util.CeilDiv(costAfter, Mathf.Max(1, armyMoveAfter))
                    < AiV2Util.CeilDiv(costBefore, Mathf.Max(1, armyMoveBefore)))
                return true;
            // Last resort: a planned step that was impossible for the current bottleneck (an
            // expensive-terrain hex costing more than MaxMovement) becomes possible.
            bool stepBefore = SafeStepPathing.FindNextSafeStep(ctx.Map, army, obligation.TargetHex,
                army.CurrentMovement, armyMoveBefore).HasValue;
            bool stepAfter = SafeStepPathing.FindNextSafeStep(ctx.Map, army, obligation.TargetHex,
                army.CurrentMovement, armyMoveAfter).HasValue;
            return stepAfter && !stepBefore;
        }

        // Extraction: a genuine MARGINAL income increase at this project's own hex, through the
        // canonical resource physics (IncomeProjection) applied to the snapshot's own frozen site
        // facts. An army collects a resource by carrying a unit with that resource's Collect
        // ability (WorldAnalysis.Self.CollectionCapacityOf), so the only way equipment can raise
        // extraction is by granting one the army does not already have — and even then, a hex whose
        // yield is already fully taken returns zero marginal gain and is rejected.
        private static bool ImprovesEconomicExtraction(in EconomicObligation obligation,
            ArmyData army, UnitData recipient, WorldSnapshot snap,
            IReadOnlyList<string> beforeAbilities, IReadOnlyList<string> afterAbilities)
        {
            if (!obligation.Resource.HasValue || snap?.Self?.Armies == null)
                return false;
            ResourceType type = obligation.Resource.Value;
            string collect = UnitAbilities.CollectAbilityFor(type);
            bool had = beforeAbilities != null && beforeAbilities.Contains(collect);
            bool has = afterAbilities != null && afterAbilities.Contains(collect);
            if (had || !has)
                return false;   // already had it, or the grant does not provide it

            EconomyExtractionOpportunity? site = null;
            foreach (EconomyExtractionOpportunity s in snap.Economy?.CollectorSites
                         ?? System.Array.Empty<EconomyExtractionOpportunity>())
                if (s.Hex.Equals(obligation.TargetHex) && s.ResourceType == type)
                { site = s; break; }
            if (site == null)
                return false;

            // Own collectors that will be standing on the target hex once this mission arrives,
            // exactly as WorldAnalysis sizes mobile collection: everyone ELSE already there, plus
            // this army's own existing capacity (which travels with it). Counting the army's other
            // collectors matters — if they already saturate the hex's remaining yield, the
            // recipient's new ability adds nothing and must be rejected. Own force only; no enemy
            // or hidden information is read here.
            int othersThere = 0;
            foreach (ArmySnapshot a in snap.Self.Armies)
                if (a != null && a.ArmyId != army.Id && a.Hex.Equals(obligation.TargetHex))
                    othersThere += Mathf.RoundToInt(a.CollectionCapacity.Get(type));
            int ownCapacity = 0;
            foreach (UnitData u in army.Members)
                if (u != null && u.HasAbility(collect)) ownCapacity++;
            int baseline = othersThere + ownCapacity;
            int marginal = IncomeProjection.OwnerCollectionAtHex(site.Value.EffectiveYield,
                    site.Value.CurrentBuildingCollection, baseline + 1, true)
                - IncomeProjection.OwnerCollectionAtHex(site.Value.EffectiveYield,
                    site.Value.CurrentBuildingCollection, baseline, true);
            return marginal > 0;
        }

        // Protection: a concrete threat the AI actually REMEMBERS (snapshot sightings only — never
        // an ArmyRegistry sweep for hidden enemies) standing on or beside this mission's own route,
        // whose outcome the grant demonstrably changes. WorthIt owns the outcome; this is the same
        // roster-level before/after comparison the ground-combat branch already uses.
        private static bool ImprovesEconomicProtection(DevelopmentOpportunity op, ArmyData army,
            UnitData recipient, EquipmentGrant grant, in EconomicObligation obligation,
            PlayerSetupData player, WorldSnapshot snap, AiTurnContext ctx)
        {
            if (snap == null || ctx?.Map == null || recipient.IsHero)
                return false;
            HexPath route = SafeStepPathing.FindSafePath(ctx.Map, player, army.Hex,
                obligation.TargetHex, army.MaxMovement);
            if (route == null || route.Hexes == null || route.Hexes.Count == 0)
                return false;
            IReadOnlyList<AiMapMemory.KnownEnemySighting> threats =
                WorldAnalysis.KnownThreatsAffectingEconomyRoute(snap, route.Hexes);
            foreach (AiMapMemory.KnownEnemySighting threat in threats)
            {
                IReadOnlyList<WorthIt.DefenderProfile> defenders = threat.Defenders;
                if (defenders == null || defenders.Count == 0)
                    continue;
                // The threats themselves come honestly from
                // WorldAnalysis.KnownThreatsAffectingEconomyRoute, so the hex's defence must be
                // knowledge-scoped too (never the live BuildingRegistry) — the same read as the
                // ground-combat branch above.
                float hexBonus = AiMapMemory.KnownHexDefenseBonus(player, ctx.Map, threat.Hex);
                if (ImprovesGroundCombatOutcome(recipient, army.Members, grant, defenders, hexBonus))
                    return true;
            }
            return false;
        }

        // The magnitude behind HasSupportedDevelopmentAxisDemand's gate, normalized onto the SAME
        // canonical urgency scale every other migrated axis's demand.Value already uses
        // (DemandUrgencyPolicy.NormalizedWorldValue) — so Development's Play-vs-Hold urgency
        // (AiConfigV2.taskScoreUrgencyRampLo/Hi) compares like for like against Hero/Scout/Unit
        // demands instead of a separate, unmigrated scale. Only meaningful for an op that already
        // passed the gate above. A fresh formedDemand this cycle carries its own scored Value; a
        // witness that is only a live MissionIntent (Economy/Recon already committed, or a
        // confirmed ground-combat improvement) counts as a fully-proven need — the running operation
        // itself is the evidence, not a candidate still being scored.
        internal static float SupportedNeedStrength(DevelopmentOpportunity op,
            IReadOnlyList<AxisDemand> formedDemands, IReadOnlyList<MissionIntent> activeIntents,
            PlayerSetupData player, WorldSnapshot snap)
        {
            if (op == null)
                return 0f;
            if (op.RecipientKind == DevRecipientKind.HandCard)
            {
                AxisDemand reconDemand = formedDemands?.FirstOrDefault(d => d != null
                    && d.RequestingAxis == DesireAxis.Recon
                    && d.Capability == CapabilityKind.ScoutCapability);
                return reconDemand != null
                    ? DemandUrgencyPolicy.NormalizedWorldValue(reconDemand.Value) : 1f;
            }
            if (!TryResolveRecipientArmy(op, player, out ArmyData army))
                return 0f;
            AxisDemand economyDemand = formedDemands?.FirstOrDefault(d => d != null
                && d.RequestingAxis == DesireAxis.Economy
                && d.EconomyPreferredBuilderArmyId == army.Id);
            return economyDemand != null
                ? DemandUrgencyPolicy.NormalizedWorldValue(economyDemand.Value) : 1f;
        }

        // One resolver for the live execution recipient. DevelopmentOpportunity captures the
        // stable army id when the recipient is selected; downstream demand checks revalidate that
        // exact identity rather than rediscovering ownership by scanning for UnitData membership.
        private static bool TryResolveRecipientArmy(DevelopmentOpportunity op,
            PlayerSetupData player, out ArmyData army)
        {
            army = null;
            if (op?.RecipientUnit == null || player == null || !op.RecipientArmyId.HasValue)
                return false;
            int armyId = op.RecipientArmyId.Value;
            army = ArmyRegistry.AllForOwner(player)
                .FirstOrDefault(a => a != null && a.Id == armyId
                    && a.Members.Contains(op.RecipientUnit));
            return army != null;
        }

        // WorthIt owns combat rules and simulation. EquipmentSystem owns the exact stat/ability
        // projection. Compare the SAME primary's roster before/after replacing only its recipient,
        // without mutating gameplay UnitData or pretending the grant created a new combat body.
        // ATK §42 — was ImprovesRaidCombatOutcome. Nothing in the body was ever Raid-specific: it
        // takes a recipient, a roster, a grant, defenders and a defender hex bonus, and asks WorthIt
        // whether the SAME army fights measurably better. It is shared by Raid, ActiveDefence,
        // Economy route protection and (from ATK) Attack — one proof, never a copy per lane.
        internal static bool ImprovesGroundCombatOutcome(UnitData recipient,
            IReadOnlyCollection<UnitData> members, EquipmentGrant grant,
            IReadOnlyCollection<WorthIt.DefenderProfile> defenders, float hexBonus = 0f)
        {
            if (recipient == null || recipient.IsHero || grant == null || members == null
                || defenders == null || defenders.Count == 0 || !members.Contains(recipient))
                return false;

            var before = new List<WorthIt.DefenderProfile>();
            var after = new List<WorthIt.DefenderProfile>();
            var stats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.Attack] = recipient.Attack,
                [EquipmentStat.Defense] = recipient.Defense,
                [EquipmentStat.HitPoints] = recipient.HitPointsMax,
                [EquipmentStat.Initiative] = recipient.Initiative,
            };
            PredictedEquipmentState predicted = EquipmentSystem.Predict(grant, stats, recipient.Abilities);
            int attack = predicted.Stats.TryGetValue(EquipmentStat.Attack, out int atk)
                ? atk : recipient.Attack;
            int defense = predicted.Stats.TryGetValue(EquipmentStat.Defense, out int def)
                ? def : recipient.Defense;
            int maxHp = predicted.Stats.TryGetValue(EquipmentStat.HitPoints, out int hp)
                ? hp : recipient.HitPointsMax;
            int currentHp = Mathf.Clamp(recipient.HitPointsCurrent
                + Mathf.Max(0, maxHp - recipient.HitPointsMax), 1, maxHp);
            int initiative = predicted.Stats.TryGetValue(EquipmentStat.Initiative, out int init)
                ? init : recipient.Initiative;
            var projected = new WorthIt.DefenderProfile(defense,
                predicted.Abilities.Contains(UnitAbilities.CeramicArmor), recipient.TypeTags.ToList(),
                attack, currentHp, initiative, predicted.Abilities, maxHp);

            foreach (UnitData unit in members)
            {
                // A non-combatant recipient's projected profile is built by hand above and would
                // otherwise default to a combatant — skip it on the domain rule, not a hero check.
                if (unit == null || !unit.IsGroundCombatant)
                    continue;
                before.Add(WorthIt.FromLiveUnit(unit));
                after.Add(object.ReferenceEquals(unit, recipient) ? projected : WorthIt.FromLiveUnit(unit));
            }
            bool coversBefore = WorthIt.CanDamageAll(before, defenders, hexBonus);
            bool coversAfter = WorthIt.CanDamageAll(after, defenders, hexBonus);
            if (!coversAfter)
                return false;
            if (!coversBefore)
                return true;

            WorthIt.BattleEstimate previous = WorthIt.Estimate(before, defenders, hexBonus);
            WorthIt.BattleEstimate improved = WorthIt.Estimate(after, defenders, hexBonus);
            return improved.WinChance > previous.WinChance
                || (improved.WinChance == previous.WinChance
                    && (improved.ExpectedSurvivingHpRatioOnWin > previous.ExpectedSurvivingHpRatioOnWin
                        || improved.CriticalAfterBattleChance < previous.CriticalAfterBattleChance));
        }

        private static bool ImprovesReconCapability(DevelopmentOpportunity op)
        {
            EquipmentGrant grant = op?.Card?.equipment;
            if (grant == null)
                return false;

            IEnumerable<string> beforeAbilities;
            int beforeMove;
            int beforeActivation;
            if (op.RecipientKind == DevRecipientKind.HandCard)
            {
                CardDefinition host = op.RecipientCard?.Definition;
                if (host == null)
                    return false;
                beforeAbilities = EquipmentSystem.EffectiveAbilities(
                    host.grantedAbilities, op.RecipientCard.Equipment?.equipment);
                beforeMove = host.moveMax;
                beforeActivation = host.activationApCost;
            }
            else
            {
                if (op.RecipientUnit == null)
                    return false;
                beforeAbilities = op.RecipientUnit.Abilities;
                beforeMove = op.RecipientUnit.MoveMax;
                beforeActivation = op.RecipientUnit.ActivationApCost;
            }

            var beforeStats = new Dictionary<EquipmentStat, int>
            {
                [EquipmentStat.MoveMax] = beforeMove,
                [EquipmentStat.ActivationApCost] = beforeActivation,
            };
            // Compare two states normalized by the same gameplay-owned predictor. In
            // particular, a host that already has RapidReaction already has effective activation
            // AP 0 before this grant; the grant must not receive credit for that existing ability.
            PredictedEquipmentState before = EquipmentSystem.Predict(
                null, beforeStats, beforeAbilities);
            PredictedEquipmentState after = EquipmentSystem.Predict(
                grant, beforeStats, beforeAbilities);
            int normalizedBeforeMove = before.Stats.TryGetValue(
                EquipmentStat.MoveMax, out int beforePredictedMove)
                ? beforePredictedMove : beforeMove;
            int normalizedBeforeActivation = before.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int beforePredictedActivation)
                ? beforePredictedActivation : beforeActivation;
            int afterMove = after.Stats.TryGetValue(EquipmentStat.MoveMax, out int move)
                ? move : normalizedBeforeMove;
            int afterActivation = after.Stats.TryGetValue(
                EquipmentStat.ActivationApCost, out int activation)
                ? activation : normalizedBeforeActivation;
            return AbilityParams.GetBestRecceRadius(after.Abilities)
                    > AbilityParams.GetBestRecceRadius(before.Abilities)
                || AbilityParams.GetBestRecceSpotStrength(after.Abilities)
                    > AbilityParams.GetBestRecceSpotStrength(before.Abilities)
                || BestStealthLevel(after.Abilities) > BestStealthLevel(before.Abilities)
                || afterMove > normalizedBeforeMove
                || afterActivation < normalizedBeforeActivation;
        }

        private static int BestStealthLevel(IEnumerable<string> abilities)
        {
            int best = 0;
            if (abilities == null)
                return best;
            foreach (string ability in abilities)
                if (AbilityParams.TryGetStealthLevel(ability, out int level))
                    best = System.Math.Max(best, level);
            return best;
        }

    }
}
