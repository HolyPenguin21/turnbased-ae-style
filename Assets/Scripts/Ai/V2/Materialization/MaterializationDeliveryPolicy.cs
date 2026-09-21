using System.Linq;
using Game.Combat;
using UnityEngine;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ARCH-02 §16/§47 — the ONE owner of "can this materialization / army operationally satisfy a
    // capability demand" for FieldCombatPower / Hero / ScoutCapability / CollectorCapability.
    // Before ARCH-02 the same switch lived three times: MaterializationCandidateBuilder
    // (CanDeliverDemandOperationally, plan-level, unclassified => deliverable), StrategicManager
    // (CanDeliverResidualOperationally, plan-level, unclassified => NOT deliverable) and
    // CapabilityDeliveryEvaluator (IsOperationalForDemand, army-level). They are unified here; the
    // former StrategicManager copy's "unclassified => false" default was unreachable (a
    // materialization plan's FinalCapability is never an infrastructure kind and
    // BestUnresolvedDemandFor filters on FinalCapability), so folding it onto this one loses no
    // behaviour.
    internal static class MaterializationDeliveryPolicy
    {
        // Plan-level: would executing this chain move the live capability inventory for `demand`?
        // (A garrison deposit is preparation, not Field/Hero delivery; a lone Hero shell is
        // reserve-only until it has an escort; a Scout placement always counts.)
        internal enum DeliveryFailureReason
        {
            None,
            MissingPlanOrDemand,
            WrongPlacement,
            MissingWorldContext,
            MissingTarget,
            RecipientMissing,
            RecipientAlreadyHasHero,
            NoSafeRoute,
            InsufficientSafeEscort,
        }

        internal readonly struct DeliveryAssessment
        {
            internal readonly bool CanDeliver;
            internal readonly DeliveryFailureReason FailureReason;
            internal readonly string Detail;

            internal DeliveryAssessment(bool canDeliver, DeliveryFailureReason failureReason,
                string detail = null)
            {
                CanDeliver = canDeliver;
                FailureReason = failureReason;
                Detail = detail;
            }

            internal static DeliveryAssessment Ok =>
                new DeliveryAssessment(true, DeliveryFailureReason.None);
            internal static DeliveryAssessment No(DeliveryFailureReason reason,
                string detail = null) => new DeliveryAssessment(false, reason, detail);

            public override string ToString() => CanDeliver
                ? "ok"
                : string.IsNullOrEmpty(Detail)
                    ? FailureReason.ToString()
                    : $"{FailureReason}:{Detail}";
        }

        internal static bool CanDeliverDemandOperationally(MaterializationPlan p, AxisDemand demand,
            WorldSnapshot snapshot = null, PlayerSetupData player = null, AiTurnContext ctx = null)
            => AssessDemandOperationally(p, demand, snapshot, player, ctx).CanDeliver;

        // The same canonical decision as the bool facade, with a stable reason for diagnostics and
        // retry policy. No caller re-derives route, placement or escort eligibility.
        internal static DeliveryAssessment AssessDemandOperationally(MaterializationPlan p,
            AxisDemand demand, WorldSnapshot snapshot = null, PlayerSetupData player = null,
            AiTurnContext ctx = null)
        {
            if (p == null || demand == null)
                return DeliveryAssessment.No(DeliveryFailureReason.MissingPlanOrDemand);
            switch (demand.Capability)
            {
                case CapabilityKind.ScoutCapability:
                    return DeliveryAssessment.Ok;
                case CapabilityKind.CollectorCapability:
                    // Collection is a separate solo army, not an attachment to combat forces or
                    // garrisons. The enumerator supplies these two shapes; policy also enforces
                    // that invariant for every feasibility caller. Actual site/route selection
                    // remains WorldAnalysis.Economy + EconomyMissionPlanner's responsibility.
                    if (!demand.EconomyResourceType.HasValue || !demand.TargetHex.HasValue)
                        return DeliveryAssessment.No(DeliveryFailureReason.MissingTarget);
                    return p.Deploy.Kind == DeploymentKind.NewArmy
                        || p.Deploy.Kind == DeploymentKind.ReusableShell
                            ? DeliveryAssessment.Ok
                            : DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                                p.Deploy.Kind.ToString());
                case CapabilityKind.Hero:
                    // Economy does not need a combat-ready hero stack: its canonical builder shape
                    // is AiArmyRoles.IsHeroLed, so a legal field placement may create a solo hero.
                    // Keep the escort rule unchanged for every non-Economy Hero demand.
                    if (IsEconomyHeroDemand(demand))
                    {
                        bool field = p.Deploy.Kind == DeploymentKind.NewArmy
                            || p.Deploy.Kind == DeploymentKind.ReusableShell
                            || p.Deploy.Kind == DeploymentKind.ExistingArmy;
                        if (!field)
                            return DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                                p.Deploy.Kind.ToString());
                        if (snapshot == null || snapshot.Self?.Armies == null
                            || player == null || ctx == null)
                            return DeliveryAssessment.No(DeliveryFailureReason.MissingWorldContext);
                        if (!demand.TargetHex.HasValue)
                            return DeliveryAssessment.No(DeliveryFailureReason.MissingTarget);

                        // A legal Hero placement is not yet a delivered builder. Project its roster
                        // and reuse Analysis routing and Demand's authoritative escort assessment.
                        ArmySnapshot recipient = p.Deploy.Army == null ? null
                            : snapshot.Self.Armies.FirstOrDefault(a => a.ArmyId == p.Deploy.Army.Id);
                        if (p.Deploy.Army != null && recipient == null)
                            return DeliveryAssessment.No(DeliveryFailureReason.RecipientMissing,
                                $"army#{p.Deploy.Army.Id}");
                        // IsHeroLed requires exactly one hero; never project a second leader as
                        // a usable Economy actor even if a generic placement accepted the card.
                        if (recipient?.HasHero == true)
                            return DeliveryAssessment.No(
                                DeliveryFailureReason.RecipientAlreadyHasHero,
                                $"army#{recipient.ArmyId}");

                        int heroMove = CapabilityQualityEvaluator.ProjectedMoveMax(p);
                        int heroAp = CapabilityQualityEvaluator.ProjectedActivationApCost(p);
                        var projected = new ArmySnapshot
                        {
                            ArmyId = recipient?.ArmyId ?? -1, Owner = player, Hex = p.Deploy.Hex,
                            HasHero = true, IsMobileEconomyBuilder = true,
                            Members = recipient?.Members ?? System.Array.Empty<WorthIt.DefenderProfile>(),
                            NonHeroActivationApCosts = recipient?.NonHeroActivationApCosts ?? System.Array.Empty<int>(),
                            NonHeroMoveMax = recipient?.NonHeroMoveMax ?? System.Array.Empty<int>(),
                            NonHeroIsAviation = recipient?.NonHeroIsAviation ?? System.Array.Empty<bool>(),
                            HeroMoveMax = heroMove, HeroActivationApCost = heroAp,
                            MaxMovement = recipient?.MemberCount > 0 ? Mathf.Min(heroMove, recipient.MaxMovement) : heroMove,
                            ActivationApCost = heroAp + (recipient?.NonHeroActivationApCosts?.Sum() ?? 0),
                            MemberCount = (recipient?.MemberCount ?? 0) + 1,
                        };
                        projected.CurrentMovement = projected.MaxMovement;
                        var routes = WorldAnalysis.EconomyBuilderRoutes(
                            snapshot, player, ctx, demand.TargetHex.Value, projected);
                        if (routes.Count == 0)
                            return DeliveryAssessment.No(DeliveryFailureReason.NoSafeRoute,
                                $"from=({projected.Hex.Q},{projected.Hex.R}) target=({demand.TargetHex.Value.Q},{demand.TargetHex.Value.R})");

                        var choice = DemandLayer.AssessEconomyArmy(snapshot,
                            demand.TargetHex.Value, routes[0], projected,
                            demand.EconomyBuildApCost,
                            includeReturn: demand.EconomyBuildCard?.Definition?.cardType
                                != CardType.Base);
                        return choice.Suitability != DemandLayer.EconomyArmySuitability.Ineligible
                            ? DeliveryAssessment.Ok
                            : DeliveryAssessment.No(
                                DeliveryFailureReason.InsufficientSafeEscort,
                                choice.IneligibleReason);
                    }
                    return p.Deploy.Kind == DeploymentKind.ExistingArmy
                        && p.Deploy.Army != null
                        && p.Deploy.Army.Members.Any(u => u != null && !u.IsHero && !u.IsAviation)
                            ? DeliveryAssessment.Ok
                            : DeliveryAssessment.No(DeliveryFailureReason.InsufficientSafeEscort);
                case CapabilityKind.FieldCombatPower:
                {
                    if (p.Deploy.Kind == DeploymentKind.Garrison)
                        return DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                            p.Deploy.Kind.ToString());
                    // AGG-RAID §7 — an IndependentFieldArmy demand (Raid reinforcement) must
                    // arrive as its OWN mobile container. FORBIDDEN: attaching onto the consumer's
                    // own (remote) primary army, any garrison deposit, and any actor already
                    // committed to another mission. ALLOWED: a free ready field army, a reusable
                    // empty shell, a brand-new army — i.e. an independent mobile support actor.
                    if (demand.DeliveryShape == CapabilityDeliveryShape.IndependentFieldArmy)
                    {
                        if (p.Deploy.Kind == DeploymentKind.ExistingArmy && p.Deploy.Army != null)
                        {
                            if (snapshot?.Self?.Armies != null)
                            {
                                ArmySnapshot host = snapshot.Self.Armies
                                    .FirstOrDefault(a => a != null && a.ArmyId == p.Deploy.Army.Id);
                                if (host == null || !host.IsStructuralRaidActor)
                                    return DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                                        $"independent_support_requires_mobile_field_army#{p.Deploy.Army.Id}");
                            }
                            if (IsConsumerPrimaryOrCommitted(player, demand, p.Deploy.Army.Id))
                                return DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                                    $"independent_support_may_not_reuse_committed_actor#{p.Deploy.Army.Id}");
                        }
                    }
                    CardDefinition d = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                    bool hero = d != null && d.cardType == CardType.Hero;
                    if (!hero)
                        return DeliveryAssessment.Ok;
                    return p.Deploy.Kind == DeploymentKind.ExistingArmy
                        && p.Deploy.Army != null
                        && p.Deploy.Army.Members.Any(u => u != null && !u.IsHero && !u.IsAviation)
                            ? DeliveryAssessment.Ok
                            : DeliveryAssessment.No(DeliveryFailureReason.InsufficientSafeEscort);
                }
                default:
                    return DeliveryAssessment.Ok;
            }
        }

        // Army-level: is this already-existing army an operational instance of `demand`'s capability
        // (used to lease armies that satisfied a live strategic demand to Housekeeping).
        internal static bool IsArmyOperationalForDemand(ArmySnapshot army, AxisDemand demand)
        {
            if (army == null || demand == null)
                return false;
            if (demand.Capability == CapabilityKind.CollectorCapability)
            {
                // Collection ability is resource-specific. Reuse Analysis's frozen, effective
                // CollectionCapacity instead of reading card definitions/abilities a second time.
                // Target distance is NOT an actor-shape gate: collectors deploy at our Base and
                // EconomyMissionPlanner owns the subsequent travel and target admission.
                return demand.RequestingAxis == DesireAxis.Economy
                    && demand.EconomyResourceType.HasValue
                    && !army.IsPrison && !army.IsGarrison && !army.IsAir && !army.IsAirfield
                    && army.MemberCount == 1
                    && army.CollectionCapacity.Get(demand.EconomyResourceType.Value) > 0f;
            }
            return IsEconomyHeroDemand(demand)
                ? army.IsMobileEconomyBuilder
                : IsArmyOperationalForCapability(army, demand.Capability, demand.RequiredTraits);
        }

        // AGG-RAID §7 — is this army the demand's own consumer primary, or an actor some other
        // durable mission already owns? Either disqualifies it as an INDEPENDENT support actor.
        private static bool IsConsumerPrimaryOrCommitted(PlayerSetupData player, AxisDemand demand, int armyId)
        {
            if (player == null)
                return false;
            var intents = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
            foreach (MissionIntent i in intents)
            {
                if (i.PreferredMoverArmyId == armyId)
                    return true;
                if (i.Raid != null && (i.Raid.PrimaryArmyId == armyId || i.Raid.SupportArmyId == armyId))
                    return true;
            }
            return false;
        }

        internal static bool IsEconomyHeroDemand(AxisDemand demand)
            => demand != null
                && demand.RequestingAxis == DesireAxis.Economy
                && demand.Capability == CapabilityKind.Hero;

        // Capability-level form used when Phase B deliberately creates useful surplus without an
        // AxisDemand object. Keeping it here prevents lease bookkeeping from growing a second
        // definition of what an operational Scout is.
        internal static bool IsArmyOperationalForCapability(ArmySnapshot army,
            CapabilityKind capability, TraitPreference requiredTraits)
        {
            if (army == null)
                return false;
            switch (capability)
            {
                case CapabilityKind.FieldCombatPower:
                    return army.IsStructuralRaidActor;
                case CapabilityKind.Hero:
                    return army.HasHero && army.IsStructuralRaidActor;
                case CapabilityKind.ScoutCapability:
                    if (!army.IsSoloRecce || army.CurrentMovement <= 0)
                        return false;
                    return (requiredTraits & TraitPreference.Stealth) == 0
                        || army.IsHidden || army.CanEnterStealth;
                default:
                    return false;
            }
        }
    }
}
