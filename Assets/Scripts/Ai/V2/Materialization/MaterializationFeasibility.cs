using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    internal readonly struct MaterializationDeliveryAvailability
    {
        public readonly int RawCandidates;
        public readonly int PreflightCandidates;
        public readonly int OperationalCandidates;

        public MaterializationDeliveryAvailability(int rawCandidates,
            int preflightCandidates, int operationalCandidates)
        {
            RawCandidates = rawCandidates;
            PreflightCandidates = preflightCandidates;
            OperationalCandidates = operationalCandidates;
        }

        public bool ConfirmedBlocked => PreflightCandidates > 0
            && OperationalCandidates == 0;
    }

    // ARCH-02 §9 / DoD "Feasibility отделена от enumeration/scoring" — the per-chain feasibility
    // stage. It takes the RAW shapes MaterializationChainEnumerator produced and admits the ones
    // that are legally playable now (CardPlayExecutor.Preflight), fit the requesting axis's
    // discrete AP entitlement / real AP after the housekeeping reserve / the owner-aware spendable
    // persistent resources / a free hand slot (Phase A), or pass the reserves-after-chain guard
    // and the unresolved strategic-claim delivery gate (Phase B). No enumeration, no scoring.
    internal static class MaterializationFeasibility
    {
        // Phase A — admit the raw shapes for one demand, returning the scored-stage input tuple
        // (plan, followupAp, projected traits).
        internal static List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> FilterForDemand(
            IReadOnlyList<MaterializationPlan> raw, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, AxisDemand demand, PhaseAApBudget apBudget, float reservedFollowupAp,
            WorldSnapshot snapshot = null)
        {
            float eps = AiConfigV2.allocatorSliceEpsilon;
            float axisBudget = apBudget.DiscreteAdmissionBudget();
            int stealthSurcharge = (demand.RequiredTraits & TraitPreference.Stealth) != 0
                ? AiConfigV2.scoutOptionalStealthAp : 0;

            var sink = new List<(MaterializationPlan plan, float followupAp, TraitPreference proj)>();
            foreach (MaterializationPlan p in raw)
            {
                if (p == null) continue;
                if (!PreflightIfExisting(player, root, hand, ctx, p))
                    continue;
                CardDefinition baseDef = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                AddIfFeasibleA(sink, p, demand, baseDef, stealthSurcharge,
                    reservedFollowupAp - (demand.AttackLocalRefit && p.AttackRefitPrimaryId.HasValue
                        ? apBudget.ActorFollowup(p.AttackRefitPrimaryId.Value) : 0f),
                    axisBudget, eps, root, hand, player, ctx, snapshot);
            }
            return sink;
        }

        // Read-only explanation seam for callers that must distinguish an operational-delivery
        // dead end from ordinary AP/resource/hand affordability. It reuses the same preflight and
        // canonical delivery policy as FilterForDemand and never scores or selects a plan.
        internal static MaterializationDeliveryAvailability AssessOperationalDelivery(
            IReadOnlyList<MaterializationPlan> raw, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, AxisDemand demand, WorldSnapshot snapshot = null)
        {
            int rawCount = 0;
            int preflightCount = 0;
            int operationalCount = 0;
            foreach (MaterializationPlan plan in raw ?? System.Array.Empty<MaterializationPlan>())
            {
                if (plan == null)
                    continue;
                rawCount++;
                if (!PreflightIfExisting(player, root, hand, ctx, plan))
                    continue;
                preflightCount++;
                if (plan.Kind == MaterializationChainKind.GenerateAttachUpgrade
                    || MaterializationDeliveryPolicy.CanDeliverDemandOperationally(
                        plan, demand, snapshot, player, ctx))
                    operationalCount++;
            }
            return new MaterializationDeliveryAvailability(
                rawCount, preflightCount, operationalCount);
        }

        // Phase B — admit the raw surplus shapes: legally playable now, not blocked by an
        // unresolved strategic claim on its base card, hand-slot-feasible, reserves still OK.
        internal static List<MaterializationPlan> FilterSurplus(IReadOnlyList<MaterializationPlan> raw,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            MaterializationReservation reservation, WorldSnapshot snapshot = null)
        {
            var sink = new List<MaterializationPlan>();
            foreach (MaterializationPlan p in raw)
            {
                if (p == null) continue;
                if (!PreflightIfExisting(player, root, hand, ctx, p))
                    continue;
                // §2 — if this card is still strategically relevant to an unresolved capability
                // demand, Phase B may only spend it on a placement that would actually deliver that
                // capability. Otherwise it stays in hand until Phase A resolves the demand.
                AxisDemand strategicClaim = UnresolvedClaimFor(reservation, p.FinalCapability, p.ProjectedAbilities);
                if (strategicClaim != null
                    && !MaterializationDeliveryPolicy.CanDeliverDemandOperationally(p, strategicClaim, snapshot, player, ctx))
                    continue;
                // A Recce hero has two legal Phase-B shapes: solo Scout or Hero joining a real
                // formation. The latter must not evade an unresolved Scout demand merely because
                // its resulting army is no longer a solo scout. Reuse the existing claim owner;
                // only a ScoutCapability placement can satisfy that claim.
                if (p.FinalCapability == CapabilityKind.Hero
                    && AbilityParams.AbilitiesHaveAnyRecce(p.ProjectedAbilities)
                    && UnresolvedClaimFor(reservation, CapabilityKind.ScoutCapability,
                        p.ProjectedAbilities) != null)
                    continue;
                if (p.HandSlotsNeededAtPeak > 0 && !hand.HasFreeSlot)
                    continue;
                if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, p, player))
                    continue;
                sink.Add(p);
            }
            return sink;
        }

        // Direct / AttachDeploy carry an existing hand card and a fully-resolved placement — those
        // must still legally play right now. A Generate* shape has no card to preflight pre-mint
        // (MaterializationExecutor re-validates the placement after minting).
        private static bool PreflightIfExisting(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, MaterializationPlan p)
        {
            if (p.Generation != null || p.BaseCardInHand == null)
                return true;
            return CardPlayExecutor.Preflight(player, root, hand, ctx, p.Deploy.Bind(p.BaseCardInHand), out _);
        }

        internal static void AddIfFeasibleA(
            List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> sink,
            MaterializationPlan p, AxisDemand demand, CardDefinition baseDef, int stealthSurcharge,
            float reservedFollowupAp, float axisBudget, float eps, PlayerRoot root, AiHandData hand,
            PlayerSetupData player, AiTurnContext ctx, WorldSnapshot snapshot = null)
        {
            bool upgrade = p != null && p.Kind == MaterializationChainKind.GenerateAttachUpgrade;
            // A global-source carrier is delivered by being in play: no post-deploy movement.
            bool noFollowup = upgrade || demand?.Capability == CapabilityKind.GlobalResourceCarrier;
            // Upgrade delivery is the attachment itself and has no post-deploy follow-up. Every
            // deploy chain keeps the canonical operational-delivery gate.
            if (!upgrade && !MaterializationDeliveryPolicy.CanDeliverDemandOperationally(p, demand, snapshot, player, ctx))
                return;

            if (demand.AttackLocalRefit
                && (!AttackBaseRefitPolicy.Validate(p, snapshot, player, out var refitHandoff, out _)
                    || !AttackBaseRefitPolicy.FollowupStillCurrent(p, player, ctx, refitHandoff, root)
                    || !AttackBaseRefitPolicy.OnwardFunded(p, player, root, ctx, p.ApCost,
                        AttackBaseRefitPolicy.FinalRoster(AiV2Util.ResolveArmy(player,
                            p.AttackRefitPrimaryId.Value), refitHandoff)))) return;
            float activationAp = p != null && !upgrade
                ? CapabilityQualityEvaluator.ProjectedActivationApCost(p)
                : (baseDef != null ? baseDef.activationApCost : AiConfigV2.scoutNotionalActivationAp);
            float followupAp = demand.AttackLocalRefit ? p.AttackRefitFollowupAp
                : noFollowup ? 0f
                : activationAp + stealthSurcharge + demand.MinimumFollowupAp;
            float need = p.ApCost + reservedFollowupAp + followupAp;
            if (need > axisBudget + eps) return;
            // Admission measures AP on the SAME owner-aware pool MaterializationExecutor re-checks
            // with (StrategicSpendability.ReservesOkAfterChain): another owner's AP hold (an Economy
            // completion, a reaction envelope) and unpaid air-recovery activation are not
            // available, so a chain is never admitted that execution must refuse.
            // The demand's ONE spend authority (AxisDemand.SpendAuthority): a builder-hero chain
            // may use its own pending build's hold, an Economy action that completes now is senior
            // to other builds' deferred holds; every other hold still counts.
            SpendAuthority authority = demand != null ? demand.SpendAuthority : default;
            if (StrategicSpendability.SpendableAp(player, root, ctx, authority)
                    - need < -eps) return;
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, p.ResCost,
                    authority)) return;
            if (p.HandSlotsNeededAtPeak > 0 && !hand.HasFreeSlot) return;
            sink.Add((p, followupAp, p.ExpectedTraits));
        }

        // The best still-unresolved strategic demand a surplus card would be relevant to, or null.
        internal static AxisDemand UnresolvedClaimFor(MaterializationReservation reservation,
            CapabilityKind cap, IReadOnlyList<string> projectedAbilities)
        {
            if (reservation == null || reservation.UnresolvedDemands.Count == 0)
                return null;
            TraitPreference projTraits = MaterializationChainMatching.TraitsOf(projectedAbilities);
            // See MaterializationReservation.BestUnresolvedDemandFor — a still-deferred persistence
            // demand must not grant the strategic-claim ap/resource affordability relaxation either.
            // Nor does an Economy new-hero alternative claim a card: its build either stays with
            // the ready hero or, without the alternative, would not be offered at all — a card
            // none of whose placements beats that ready hero must stay spendable.
            return reservation.UnresolvedDemands
                .Where(d => d != null && !d.IsPersistenceDeferred && !d.IsEconomyNewHeroAlternative
                    && !d.StructurallyUndeliverable
                    && d.DesiredAmount > 0f && d.Capability == cap
                    && (projTraits & d.RequiredTraits) == d.RequiredTraits)
                .OrderByDescending(d => d.Value)
                .ThenBy(d => (int)d.RequestingAxis)
                .FirstOrDefault();
        }
    }
}
