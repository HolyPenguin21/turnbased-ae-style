using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  REUSABLE ARMY SELECTOR  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  V2-native reusable empty-army model. An empty ArmyData is a PAID, REUSABLE asset
    //  (ArmyActions.CreateArmy costs AP) for the rest of the turn; at the end of the owner's turn
    //  the game disbands it unless it stands on the owner's own Barracks hex
    //  (HexSelectionController.DisbandEmptyArmiesOf). This does NOT use V1 GarrisonReorgTask.DisposableEmptyArmies and does not carry the
    //  V1 "disposable army" concept: an army's role is read from its CURRENT composition + mission
    //  ownership (ActorCommitments), and a shell is only ever used where game rules already allow
    //  it — at the hex it currently sits on. A shell that previously served another role is fully
    //  reusable; ArmyId does not bind purpose.
    //
    //  PLACEMENT RULE: the shell is part of a COMPLETE placement candidate (card + hex + shell).
    //  The caller never picks a hex and then reuses an unrelated shell elsewhere — a shell at
    //  base A cannot be "used at base B".
    // ===========================================================================================
    public static class ReusableArmySelector
    {
        public static bool IsReusableShell(ArmyData army, PlayerSetupData player, ActorCommitments commitments)
        {
            if (army == null || army.Owner != player || army.Controller == null)
                return false;
            if (army.Members.Count != 0 || army.IsGarrison || army.IsPrison || army.IsCarrierContainer)
                return false;
            // Airfield containers / air armies are semantically distinct from a ground shell — the
            // model exposes these predicates, so this is not a hardcoded flag.
            if (AviationRules.IsAirfield(army) || AviationRules.IsAirArmy(army))
                return false;
            return commitments == null || !commitments.IsArmyClaimed(army.Id);
        }

        public static IReadOnlyList<ArmyData> ReusableShells(PlayerSetupData player, ActorCommitments commitments) =>
            player == null
                ? (IReadOnlyList<ArmyData>)Array.Empty<ArmyData>()
                : ArmyRegistry.AllForOwner(player)
                    .Where(a => IsReusableShell(a, player, commitments))
                    .OrderBy(a => a.Id)
                    .ToList();

        internal static bool IsReusableShell(ArmySnapshot a, HexCoord hex, ISet<int> committed) =>
            a != null && a.Hex.Equals(hex) && a.MemberCount == 0 && !a.IsGarrison && !a.IsPrison
            && !a.IsAir && !a.IsAirfield && !a.IsCarrierContainer && !a.HasActivatedThisTurn
            && a.Capacity > 0 && (committed == null || !committed.Contains(a.ArmyId));

        // Priority-1 pick: a reusable shell ALREADY sitting on `hex`. null if none.
        public static ArmyData FindReusableAt(PlayerSetupData player, HexCoord hex, ActorCommitments commitments) =>
            ReusableShells(player, commitments).FirstOrDefault(a => a.Hex.Equals(hex));

        // Where a sparable garrison hero goes to become a mover: the container choice read by
        // Analysis (builder routes), Provisioning (the funded plan) and the executors (the pinned
        // plan they apply).
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

        // Three tiers, tried in order: Shell (an empty reusable army on the garrison hex), Host (the
        // smaller unclaimed non-shell army there), Create (a new army). A hero who already leads a
        // field army needs none of them; no sparable hero at all is an honest NoMoverExists.
        // Pure: never touches game state. A container's AP cost (activation charge on an
        // already-acted shell/host, or CreateArmyApCost for a fresh one) is checked against BOTH
        // the ECO-axis envelope and the raw AP pool before it is accepted, so a candidate this
        // pass genuinely cannot afford is skipped in favour of the next one rather than accepted
        // and left to fail downstream.
        internal static GarrisonExtractionCandidate ResolveGarrisonExtraction(
            PlayerSetupData player, ArmyData garrison, ActorCommitments commitments,
            ICollection<int> claimedThisPass, PlayerRoot root, float ecoApEnvelopeRemaining,
            UnitData exactDevelopmentHero = null,
            ResearchProductionMode? exactDevelopmentMode = null,
            System.Func<ResearchProductionMode, HexCoord?> preparationSite = null,
            int? exactEconomyHeroId = null)
        {
            UnitData sparable = exactDevelopmentHero == null
                ? AiArmyRoles.EconomyHeroCandidates(player, garrison, preparationSite)
                    .FirstOrDefault(u => !exactEconomyHeroId.HasValue || u.RuntimeId == exactEconomyHeroId.Value)
                // Development detaches a second hero of a free field army (the same Shell -> Host
                // -> Create ladder); the source is then not a garrison.
                : !garrison.IsGarrison
                    ? (AiArmyRoles.IsDetachedFieldDelivery(garrison, exactDevelopmentHero)
                        ? exactDevelopmentHero : null)
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
            if (shell != null && (claimedThisPass == null || !claimedThisPass.Contains(shell.Id))
                && ArmyActions.CanTransferMembers(
                    new[] { sparable }, garrison, shell, out _))
            {
                float apCost = shell.RequiresActivationCharge(sparable) ? sparable.ActivationApCost : 0f;
                if (Affordable(apCost))
                    return GarrisonExtractionCandidate.Yes(GarrisonExtractionTier.Shell, sparable, shell, apCost);
                TrackUnaffordable(apCost);
            }

            foreach (ArmyData host in EconomyHostCandidates(player, garrison, commitments, claimedThisPass))
            {
                if (!ArmyActions.CanTransferMembers(new[] { sparable }, garrison, host, out _))
                    continue;
                float apCost = host.RequiresActivationCharge(sparable) ? sparable.ActivationApCost : 0f;
                if (Affordable(apCost))
                    return GarrisonExtractionCandidate.Yes(GarrisonExtractionTier.Host, sparable, host, apCost);
                TrackUnaffordable(apCost);
            }

            // The first member must leave a legal source AND fit its destination, exactly as
            // CreateArmyWithMember requires. Preview allocates no real id or registered army.
            ArmyData fresh = ArmyData.CreateVisualSnapshot();
            fresh.Owner = player;
            fresh.Hex = garrison.Hex;
            if (!ArmyActions.CanTransferMembers(new[] { sparable }, garrison, fresh, out string whyCreate))
                return GarrisonExtractionCandidate.No(whyCreate, cheapestUnaffordable ?? 0f);
            if (Affordable(ArmyActions.CreateArmyApCost))
                return GarrisonExtractionCandidate.Yes(
                    GarrisonExtractionTier.Create, sparable, null, ArmyActions.CreateArmyApCost);
            TrackUnaffordable(ArmyActions.CreateArmyApCost);

            return GarrisonExtractionCandidate.No(
                "no free shell, no eligible host army, and no ECO-axis room left to create one",
                cheapestUnaffordable ?? ArmyActions.CreateArmyApCost);
        }

        // The Host tier: a populated, heroless own field army on the garrison hex that no operation
        // (`commitments`) and no mission of this pass (`claimedThisPass`) claims, smallest first.
        // Populated armies only — the empty case is ReusableArmySelector's own, separate
        // responsibility; this never overlaps it (Members.Count > 0 here). An army that already
        // has a hero is excluded: it is itself a potential direct Economy mover (case 1.2a), not a
        // container to extract a SECOND hero into — ArmyData.AddMemberSorted inserts a new hero
        // after any existing ones, so a "which hero did we actually just extract" ambiguity is a
        // structural risk this exclusion removes at the source rather than downstream.
        private static IEnumerable<ArmyData> EconomyHostCandidates(
            PlayerSetupData player, ArmyData garrison, ActorCommitments commitments,
            ICollection<int> claimedThisPass)
        {
            return ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a != garrison && a.Hex.Equals(garrison.Hex)
                    && !a.IsGarrison && !a.IsPrison
                    && !AviationRules.IsAirfield(a) && !AviationRules.IsAirArmy(a)
                    && a.Members.Count > 0 && !a.Members.Any(u => u != null && u.IsHero)
                    && (commitments == null || !commitments.IsArmyClaimed(a.Id))
                    && (claimedThisPass == null || !claimedThisPass.Contains(a.Id)))
                .OrderBy(a => a.Members.Count)
                .ThenBy(a => a.Id);
        }
    }
}
