using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // Economy/Development garrison-extraction sizing. A mechanical partial of
    // ProvisioningManager, not a second extraction owner. Consumed by ProvisionEconomy and
    // ProvisionDevelopment here, and externally by TaskExecutor (which performs the actual
    // mutation) and DemandLayer.Economy (which re-uses the same pure solver).

    internal static partial class ProvisioningManager
    {
        // Economy garrison-extraction never materializes during Provisioning.
        // ResolveGarrisonExtractionCandidate (pure) decides the tier here only to produce a
        // conservative pre-mutation cost ESTIMATE for the funding envelope; the actual
        // ArmyActions.CreateArmy/ TransferMember mutation (ApplyGarrisonExtraction) runs from
        // TaskExecutor.MaterializeEconomyGarrisonBuilder, inside that step's own
        // beforeStep/afterStep observation window — the same synthetic-actor-id pattern as
        // ScoutExecutorKind.AirLaunch (see SyntheticGarrisonExtractionActorId below and
        // MoverArmyId's field comment on ProvisionedMission).
        internal static int SyntheticGarrisonExtractionActorId(int garrisonArmyId) =>
            -(3_000_000 + (garrisonArmyId & 0xFFFFFF));

        // The garrison-extraction container choice is a pure, read-only RESOLVE step
        // (GarrisonExtractionCandidate) that every caller — the Provisioning-time estimate above,
        // TaskExecutor's real materialization, and the FoundBase diagnostic TRACE — shares. The
        // TRACE prints this struct's fields verbatim instead of re-deriving its own copy of the
        // eligibility logic (the previous copy drifted out of sync with the real gates: it never
        // accounted for ArmyData.RequiresActivationCharge, an existing hero already in a "host"
        // candidate, or ProvisioningSession.ClaimedArmyIds — see
        // Docs/ai-economy-mover-materialization-decision-tree.md).
        internal enum GarrisonExtractionTier { None, Shell, Host, Create }

        internal readonly struct GarrisonExtractionCandidate
        {
            public readonly GarrisonExtractionTier Tier;
            public readonly UnitData Hero;         // the EXACT UnitData that would be extracted
            public readonly ArmyData Container;    // existing shell/host; null for Create (nothing exists yet)
            // Shell/Host: TransferMember's activation charge, if any. Create: CreateArmyApCost.
            // For Tier == None this is the cheapest structurally-legal tier's AP cost when one
            // exists but exceeded the envelope (0f only when nothing exists, e.g. no sparable hero
            // at all), so a caller can report the real shortfall instead of a generic NoMoverExists
            // with no price.
            public readonly float ApCost;
            public readonly string Reason;          // set only when Tier == None

            private GarrisonExtractionCandidate(GarrisonExtractionTier tier, UnitData hero,
                ArmyData container, float apCost, string reason)
            {
                Tier = tier; Hero = hero; Container = container; ApCost = apCost; Reason = reason;
            }

            public static GarrisonExtractionCandidate No(string reason, float requiredAp = 0f) =>
                new GarrisonExtractionCandidate(GarrisonExtractionTier.None, null, null, requiredAp, reason);
            public static GarrisonExtractionCandidate Yes(GarrisonExtractionTier tier, UnitData hero,
                ArmyData container, float apCost) =>
                new GarrisonExtractionCandidate(tier, hero, container, apCost, null);
        }

        // Three tiers, tried in order, matching the decision tree in
        // Docs/ai-economy-mover-materialization-decision-tree.md (case 1.3 / two 1.4 extensions).
        // Pure: never touches game state. A container's AP cost (activation charge on an
        // already-acted shell/host, or CreateArmyApCost for a fresh one) is checked against BOTH
        // the ECO-axis envelope and the raw AP pool before it is accepted, so a candidate this
        // pass genuinely cannot afford is skipped in favour of the next one rather than accepted
        // and left to fail downstream.
        internal static GarrisonExtractionCandidate ResolveGarrisonExtractionCandidate(
            PlayerSetupData player, ArmyData garrison, ActorCommitments commitments,
            ProvisioningSession session, PlayerRoot root, float ecoApEnvelopeRemaining,
            UnitData exactDevelopmentHero = null,
            ResearchProductionMode? exactDevelopmentMode = null)
        {
            UnitData sparable = exactDevelopmentHero == null
                ? AiArmyRoles.BestSparableEconomyHero(player, garrison)
                : exactDevelopmentMode.HasValue
                    ? AiArmyRoles.BestSparableDevelopmentHero(player, garrison,
                        ResearchProductionSystem.RoleAbility(exactDevelopmentMode.Value))
                    : null;
            if (exactDevelopmentHero != null && !ReferenceEquals(sparable, exactDevelopmentHero))
                return GarrisonExtractionCandidate.No("specific Development hero is no longer sparable");
            if (sparable == null)
                return GarrisonExtractionCandidate.No("no sparable hero in garrison");

            bool Affordable(float apCost) =>
                apCost <= ecoApEnvelopeRemaining + AiConfigV2.allocatorSliceEpsilon
                && (root == null || root.CanSpendActionPoints(Mathf.CeilToInt(apCost)));

            // Track the cheapest structurally-legal tier's cost even when it is rejected as
            // unaffordable THIS turn, so a caller with the real envelope can report
            // EnvelopeTooSmall(requiredAp) instead of a generic NoMoverExists whenever a container
            // exists and only the budget was too small. Bookkeeping only; it does not change the
            // Shell -> Host -> Create order or any gate.
            float? cheapestUnaffordable = null;
            void TrackUnaffordable(float apCost) => cheapestUnaffordable =
                cheapestUnaffordable.HasValue ? Mathf.Min(cheapestUnaffordable.Value, apCost) : apCost;

            ArmyData shell = ReusableArmySelector.FindReusableAt(player, garrison.Hex, commitments);
            if (shell != null && (session == null || !session.ClaimedArmyIds.Contains(shell.Id))
                && ArmyActions.CanTransferMembers(
                    new[] { sparable }, garrison, shell, out _))
            {
                float apCost = shell.RequiresActivationCharge(sparable) ? sparable.ActivationApCost : 0f;
                if (Affordable(apCost))
                    return GarrisonExtractionCandidate.Yes(GarrisonExtractionTier.Shell, sparable, shell, apCost);
                TrackUnaffordable(apCost);
            }

            foreach (ArmyData host in EconomyHostCandidates(player, garrison, commitments, session))
            {
                if (!ArmyActions.CanTransferMembers(new[] { sparable }, garrison, host, out _))
                    continue;
                float apCost = host.RequiresActivationCharge(sparable) ? sparable.ActivationApCost : 0f;
                if (Affordable(apCost))
                    return GarrisonExtractionCandidate.Yes(GarrisonExtractionTier.Host, sparable, host, apCost);
                TrackUnaffordable(apCost);
            }

            // A fresh empty army always has room for the first member (CardPlayExecutor.Preflight
            // makes the same assumption for its own NewArmy path), so no CanTransferMembers probe
            // is possible or needed here — there is no ArmyData yet to probe against.
            if (Affordable(ArmyActions.CreateArmyApCost))
                return GarrisonExtractionCandidate.Yes(
                    GarrisonExtractionTier.Create, sparable, null, ArmyActions.CreateArmyApCost);
            TrackUnaffordable(ArmyActions.CreateArmyApCost);

            return GarrisonExtractionCandidate.No(
                "no free shell, no eligible host army, and no ECO-axis room left to create one",
                cheapestUnaffordable ?? ArmyActions.CreateArmyApCost);
        }

        // Builds a READ-ONLY preview of what the deferred garrison-extraction container will look
        // like immediately after the (real) hero transfer, so PlanEconomyCompletion can compute the
        // FULL composition/donor/AP decision at Provisioning time against ArmyData's own real
        // Members/MaxMovement/CurrentMovement/HasActivatedThisTurn projections — no duplicated
        // math, no live mutation. ArmyData.CreateVisualSnapshot() never touches ArmyRegistry or
        // burns a real Id (Id stays -1), which is why callers pass `identityArmyId` instead (see
        // PlanEconomyArmyLightening).
        private static ArmyData BuildGarrisonExtractionPreview(
            PlayerSetupData player, ArmyData garrison, GarrisonExtractionCandidate plan)
        {
            ArmyData preview = ArmyData.CreateVisualSnapshot();
            preview.Hex = garrison.Hex;
            preview.Owner = player;
            if (plan.Container != null)
            {
                preview.Members.AddRange(plan.Container.Members);
                // Propagate the REAL container's activation state so the projected AP/charge math
                // (EconomyMissionClaimedAp, ArmyActions.RequiresActivationCharge-style reasoning)
                // matches what Execution will actually see once the hero is really transferred in —
                // MarkActivated covers exactly the pre-existing members, matching a container that
                // already moved this turn; the hero appended below is deliberately NOT covered, same
                // as any brand-new join into an activated army.
                if (plan.Container.HasActivatedThisTurn)
                    preview.MarkActivated();
            }
            preview.Members.Add(plan.Hero);
            return preview;
        }

        // Applies the resolved extraction through canonical domain actions.
        internal static ArmyData ApplyGarrisonExtraction(PlayerSetupData player, ArmyData garrison,
            GarrisonExtractionCandidate candidate, AiTurnContext ctx)
        {
            if (candidate.Tier == GarrisonExtractionTier.None)
                return null;

            ArmyData container = candidate.Container;
            if (candidate.Tier == GarrisonExtractionTier.Create)
            {
                FactionCardCatalog catalog = ctx.StartingDeckCatalog?.GetCatalog(player.Faction);
                container = ArmyActions.CreateArmyWithMember(player, garrison.Hex, catalog,
                    garrison, candidate.Hero, ctx.HexSelection, out string whyCreate);
                if (container == null)
                    return null;
                AiDebugLog.Write($"[AI][V2][Economy] extracted idle hero {candidate.Hero.Name} "
                    + $"from garrison #{garrison.Id} into #{container.Id} ({candidate.Tier}, "
                    + $"ap {candidate.ApCost:0.##}) for economy mobile_hero duty");
                return container;
            }

            if (!ArmyActions.TransferMember(candidate.Hero, garrison, container,
                    ctx.HexSelection, out string why))
                return null;
            AiDebugLog.Write($"[AI][V2][Economy] extracted idle hero {candidate.Hero.Name} "
                + $"from garrison #{garrison.Id} into #{container.Id} ({candidate.Tier}, "
                + $"ap {candidate.ApCost:0.##}) for economy mobile_hero duty");
            return container;
        }

        // 1.1.1 (not claimed by ANY other active mission — durable intents via `commitments`,
        // AND missions already provisioned earlier in this same batch pass via
        // session.ClaimedArmyIds, which ActorCommitments does not see) + 1.1.2 (smallest first).
        // Populated armies only — the empty case is ReusableArmySelector's own, separate
        // responsibility; this never overlaps it (Members.Count > 0 here). An army that already
        // has a hero is excluded: it is itself a potential direct Economy mover (case 1.2a), not a
        // container to extract a SECOND hero into — ArmyData.AddMemberSorted inserts a new hero
        // after any existing ones, so a "which hero did we actually just extract" ambiguity is a
        // structural risk this exclusion removes at the source rather than downstream.
        private static IEnumerable<ArmyData> EconomyHostCandidates(
            PlayerSetupData player, ArmyData garrison, ActorCommitments commitments,
            ProvisioningSession session)
        {
            return ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a != garrison && a.Hex.Equals(garrison.Hex)
                    && !a.IsGarrison && !a.IsPrison
                    && !AviationRules.IsAirfield(a) && !AviationRules.IsAirArmy(a)
                    && a.Members.Count > 0 && !a.Members.Any(u => u != null && u.IsHero)
                    && (commitments == null || !commitments.IsArmyClaimed(a.Id))
                    && (session == null || !session.ClaimedArmyIds.Contains(a.Id)))
                .OrderBy(a => a.Members.Count)
                .ThenBy(a => a.Id);
        }
    }
}
