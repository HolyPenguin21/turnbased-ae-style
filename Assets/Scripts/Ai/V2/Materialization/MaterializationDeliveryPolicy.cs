using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using UnityEngine;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;

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
            DeliveryExceedsSiteValue,
            NotCheaperThanReadyHero,
            AttackFistNotStrengthened,
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
            if (demand.AttackLocalRefit)
                return p.AttackRefitPrimaryId == demand.AttackFistArmyId
                    && AttackBaseRefitPolicy.Validate(p, snapshot, player, out _, out _)
                    ? DeliveryAssessment.Ok : DeliveryAssessment.No(DeliveryFailureReason.AttackFistNotStrengthened);
            switch (demand.Capability)
            {
                case CapabilityKind.ScoutCapability:
                    return DeliveryAssessment.Ok;
                case CapabilityKind.CollectorCapability:
                {
                    // A collector is a separate solo field army. Reuse the SAME SafeStepPathing
                    // oracle and fog-honest threat witness as Economy's real mobile-collection
                    // admission BEFORE paying for a new army. Its later task/actor selection still
                    // belongs exclusively to WorldAnalysis.Economy + EconomyMissionPlanner.
                    if (!demand.EconomyResourceType.HasValue || !demand.TargetHex.HasValue)
                        return DeliveryAssessment.No(DeliveryFailureReason.MissingTarget);
                    if (p.Deploy.Kind != DeploymentKind.NewArmy
                        && p.Deploy.Kind != DeploymentKind.ReusableShell)
                        return DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                            p.Deploy.Kind.ToString());
                    if (snapshot?.Self?.BaseHexes == null
                        || snapshot.Self.BaseHexes.Count == 0 || player == null || ctx?.Map == null)
                        return DeliveryAssessment.No(DeliveryFailureReason.MissingWorldContext);
                    if (WorldAnalysis.KnownHostileAtHex(snapshot, demand.TargetHex.Value))
                        return DeliveryAssessment.No(DeliveryFailureReason.NoSafeRoute,
                            "collector_target_occupied");
                    int moveMax = CapabilityQualityEvaluator.ProjectedMoveMax(p);
                    if (moveMax <= 0)
                        return DeliveryAssessment.No(DeliveryFailureReason.NoSafeRoute,
                            "collector_cannot_move");
                    var route = SafeStepPathing.FindSafePath(ctx.Map, player,
                        p.Deploy.Hex, demand.TargetHex.Value, moveMax);
                    if (route == null)
                        return DeliveryAssessment.No(DeliveryFailureReason.NoSafeRoute,
                            "collector_outbound");
                    if (WorldAnalysis.KnownThreatsAffectingEconomyRoute(
                            snapshot, route.Hexes).Count > 0)
                        return DeliveryAssessment.No(DeliveryFailureReason.NoSafeRoute,
                            "collector_route_threat");
                    if (SafeStepPathing.FindNearestBaseReturnCost(ctx.Map, player,
                            demand.TargetHex.Value, snapshot.Self.BaseHexes, moveMax)
                        == int.MaxValue)
                        return DeliveryAssessment.No(DeliveryFailureReason.NoSafeRoute,
                            "collector_no_safe_return");
                    return DeliveryAssessment.Ok;
                }
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
                            // The recipient has no hero, so the played hero becomes its commander.
                            Commander = WorthIt.SideCommander.Of(
                                p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef),
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
                        if (choice.Suitability == DemandLayer.EconomyArmySuitability.Ineligible)
                            return DeliveryAssessment.No(
                                DeliveryFailureReason.InsufficientSafeEscort,
                                choice.IneligibleReason);
                        return EconomyNewHeroWorthIt(p, demand, choice, snapshot.TurnNumber);
                    }
                    return p.Deploy.Kind == DeploymentKind.ExistingArmy
                        && p.Deploy.Army != null
                        && p.Deploy.Army.Members.Any(u => AiArmyRoles.IsGroundBattleBody(u))
                            ? DeliveryAssessment.Ok
                            : DeliveryAssessment.No(DeliveryFailureReason.InsufficientSafeEscort);
                case CapabilityKind.FieldCombatPower:
                {
                    if (demand.AttackFistArmyId.HasValue)
                    {
                        int fistId = demand.AttackFistArmyId.Value;
                        // T01 — a preparation host may be weak or an empty claimed shell: the card
                        // must land in that exact own ground field container (ExistingArmy, or the
                        // shell itself), not in a structural combat actor elsewhere.
                        bool fistShapeOk = demand.AttackFistIsPreparationHost
                            ? (p.Deploy.Kind == DeploymentKind.ExistingArmy
                                    || p.Deploy.Kind == DeploymentKind.ReusableShell)
                                && snapshot?.Self?.Armies?.Any(a => a != null && a.ArmyId == fistId
                                    && !a.IsGarrison && !a.IsPrison && !a.IsAir) == true
                            : p.Deploy.Kind == DeploymentKind.ExistingArmy
                                && snapshot?.Self?.Armies?.Any(a => a != null
                                    && a.ArmyId == fistId && a.IsStructuralRaidActor) == true;
                        if (!fistShapeOk || p.Deploy.Army == null || p.Deploy.Army.Id != fistId)
                            return DeliveryAssessment.No(DeliveryFailureReason.AttackFistNotStrengthened);
                        CardDefinition card = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                        if (card == null || card.isAviation)
                            return DeliveryAssessment.No(DeliveryFailureReason.AttackFistNotStrengthened);
                        if (!StrengthensArmy(p.Deploy.Army.Members, card, AiPower.ProjectMaterialization(p)))
                            return DeliveryAssessment.No(DeliveryFailureReason.AttackFistNotStrengthened);
                        // The host already clears the power bar and lacks target coverage: power
                        // alone is no delivery — the body must let it damage more known defenders.
                        if (demand.AttackCoverageGap && demand.AttackCoverageTargetHex.HasValue
                            && !ClosesTargetCoverage(snapshot, p.Deploy.Army.Members, card,
                                demand.AttackCoverageTargetHex.Value))
                            return DeliveryAssessment.No(DeliveryFailureReason.AttackFistNotStrengthened,
                                "card_does_not_improve_target_coverage");
                    }
                    if (demand.DeliveryShape == CapabilityDeliveryShape.Garrison)
                    {
                        // The garrison floor counts non-hero bodies only: a hero placed there
                        // spends the card and never reduces the shortage.
                        CardDefinition gd = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                        if (gd != null && gd.cardType == CardType.Hero)
                            return DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                                "garrison_floor_counts_non_hero_bodies");
                        return p.Deploy.Kind == DeploymentKind.Garrison && demand.TargetHex.HasValue
                            && p.Deploy.Hex.Equals(demand.TargetHex.Value)
                                ? DeliveryAssessment.Ok
                                : DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                                    $"garrison_at_target_required:{p.Deploy.Kind}");
                    }
                    if (p.Deploy.Kind == DeploymentKind.Garrison)
                        return DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                            p.Deploy.Kind.ToString());
                    // An IndependentFieldArmy demand (Raid reinforcement) must
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
                    // Every FieldCombatPower demand asks for FREE field power: the post-play
                    // measurement (CapabilityDeliveryEvaluator -> CapabilityInventory
                    // .RaidAvailableFieldPower) excludes armies another durable mission owns. A body
                    // joined to such an army therefore always measures 0 — reject it here, where the
                    // plan is chosen, instead of paying the card and discovering it afterwards.
                    else if (p.Deploy.Kind == DeploymentKind.ExistingArmy && p.Deploy.Army != null
                        && IsConsumerPrimaryOrCommitted(player, demand, p.Deploy.Army.Id))
                        return DeliveryAssessment.No(DeliveryFailureReason.WrongPlacement,
                            $"committed_actor_adds_no_free_field_power#{p.Deploy.Army.Id}");
                    CardDefinition d = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                    bool hero = d != null && d.cardType == CardType.Hero;
                    if (!hero)
                        return DeliveryAssessment.Ok;
                    return p.Deploy.Kind == DeploymentKind.ExistingArmy
                        && p.Deploy.Army != null
                        && p.Deploy.Army.Members.Any(u => AiArmyRoles.IsGroundBattleBody(u))
                            ? DeliveryAssessment.Ok
                            : DeliveryAssessment.No(DeliveryFailureReason.InsufficientSafeEscort);
                }
                default:
                    return DeliveryAssessment.Ok;
            }
        }

        // Army-level: is this already-existing army an operational instance of `demand`'s capability
        // (used to lease armies that satisfied a live strategic demand to Housekeeping).
        // The one "does this card strengthen that exact army" answer: the army's power with the
        // card's projected line added, against its power now. A pinned Attack fist's delivery and
        // the preparation host's card witness (AggressionDemandEvaluator.PreparationHostCardSource)
        // both read it, so a WAIT never names a card this policy would refuse.
        internal static bool StrengthensArmy(IEnumerable<UnitData> members, CardDefinition card,
            AiPower.ProjectedStrategicLine line)
        {
            if (card == null || card.isAviation)
                return false;
            float beforePower = AiPower.EffectiveArmyPower((members ?? Enumerable.Empty<UnitData>())
                .Where(u => u != null).Select(AiPower.ToPowerUnit).ToList());
            return ProjectedArmyPower(members, card, line) > beforePower;
        }

        // How many known defender units of `hex` none of `members` (plus the optional `extra` card
        // body) can damage. Coverage itself stays WorthIt.CanDamageAll's — asked once per defender
        // unit here only so a card that closes some of several gaps still counts as progress.
        internal static int UncoveredDefenderCount(WorldSnapshot snapshot, IEnumerable<UnitData> members,
            CardDefinition extra, HexCoord hex)
        {
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snapshot, hex);
            if (opposition == null || opposition.Count == 0)
                return 0;
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snapshot, null, hex);
            var attackers = (members ?? Enumerable.Empty<UnitData>())
                .Where(u => u != null).Select(WorthIt.FromLiveUnit).ToList();
            if (extra != null && !extra.isAviation)
                attackers.Add(AiPower.ToDefenderProfile(extra));
            int uncovered = 0;
            foreach (WorthIt.DefendingArmy army in opposition)
                foreach (WorthIt.DefenderProfile unit in army.Units)
                    if (!WorthIt.CanDamageAll(attackers, new[] { unit }, army.DefenseBonus(hexBonus)))
                        uncovered++;
            return uncovered;
        }

        // The card lets the host damage strictly more of the target's known defenders.
        internal static bool ClosesTargetCoverage(WorldSnapshot snapshot, IEnumerable<UnitData> members,
            CardDefinition card, HexCoord hex) =>
            UncoveredDefenderCount(snapshot, members, card, hex)
                < UncoveredDefenderCount(snapshot, members, null, hex);

        // The army's AiPower once this card has joined it.
        internal static float ProjectedArmyPower(IEnumerable<UnitData> members, CardDefinition card,
            AiPower.ProjectedStrategicLine line)
        {
            var projected = (members ?? Enumerable.Empty<UnitData>())
                .Where(u => u != null).Select(AiPower.ToPowerUnit).ToList();
            AiPower.PowerUnit unit = AiPower.ToPowerUnit(card);
            projected.Add(new AiPower.PowerUnit(line.BasePower, unit.Tags,
                line.Range, unit.IsHero, line.CommandRating));
            return AiPower.EffectiveArmyPower(projected);
        }

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

        // Is this army the demand's own consumer primary, or an actor some other
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
                // Attack convoys / gather supports: the same held-support list ActorCommitments
                // claims, so this plan-level gate cannot admit an actor the post-play inventory
                // already counts as committed.
                if (GroundCombatLegs.HeldGroundSupportArmyIds(i).Contains(armyId))
                    return true;
            }
            return false;
        }

        // Is delivering this build with THIS new hero worth it? Priced in the same TaskScore units
        // Demand prices a ready hero with: delivery = extra activation AP beyond the build card.
        //  · the new hero's own delivery must stay under the site's value (no loss-making walk);
        //  · for a new-hero alternative (demand.EconomyReadyDeliveryCost set), the chain's card
        //    price plus that delivery must be lower than the ready hero's cost — a tie keeps the
        //    ready hero, which spends no card.
        private static DeliveryAssessment EconomyNewHeroWorthIt(MaterializationPlan p,
            AxisDemand demand, DemandLayer.EconomyBuilderChoice choice, int turnNumber)
        {
            float newDelivery = TaskScoreEvaluator.Price(
                Mathf.Max(0f, choice.TotalAssignmentApCost - demand.EconomyBuildApCost));
            string card = p.BaseCardInHand?.Definition?.displayName
                ?? p.GeneratedBaseDef?.displayName ?? "?";
            float tolerance = IdleBuilderDeliveryTolerance(demand, turnNumber);
            float siteAllowance = demand.EconomySiteValue * (1f + tolerance);
            if (demand.EconomySiteValue > AiConfigV2.allocatorSliceEpsilon
                && newDelivery >= siteAllowance)
                return Decide(false, DeliveryFailureReason.DeliveryExceedsSiteValue,
                    $"delivery={newDelivery:0.##} site={demand.EconomySiteValue:0.##} tol={tolerance:0.##}");
            if (!demand.EconomyReadyDeliveryCost.HasValue)
                return DeliveryAssessment.Ok;
            float newCost = TaskScoreEvaluator.Price(ActionPrice.Ap(p.ApCost)
                    + ActionPrice.Resources(p.ResCost))
                + newDelivery;
            return Decide(newCost + AiConfigV2.allocatorSliceEpsilon
                    < demand.EconomyReadyDeliveryCost.Value,
                DeliveryFailureReason.NotCheaperThanReadyHero,
                $"new={newCost:0.##} ready={demand.EconomyReadyDeliveryCost.Value:0.##}");

            DeliveryAssessment Decide(bool ok, DeliveryFailureReason reason, string detail)
            {
                AiDebugLog.WriteDeduped($"hero-vs-ready|{demand.TargetHex}|{card}|{p.Deploy.Hex}",
                    $"[ECO][HeroVsReady] site=({demand.TargetHex?.Q},{demand.TargetHex?.R}) "
                    + $"card={card} deploy=({p.Deploy.Hex.Q},{p.Deploy.Hex.R}) {detail} "
                    + $"decision={(ok ? "NEW_HERO" : reason.ToString())}");
                return ok ? DeliveryAssessment.Ok : DeliveryAssessment.No(reason, detail);
            }
        }

        // Extra fraction of the site value a NEW builder's delivery may cost: grows with how long
        // the Base card of this demand has lain in hand (same idle ramp as every card), 0 for a
        // non-Base build or a fresh card.
        internal static float IdleBuilderDeliveryTolerance(AxisDemand demand, int turnNumber)
        {
            CardData baseCard = demand?.EconomyBuildCard;
            if (baseCard?.Definition?.cardType != CardType.Base)
                return 0f;
            float ramp = IdleCardPressure.Bonus(AiHandData.AgeInTurns(baseCard, turnNumber), 1f)
                / Mathf.Max(0.01f, AiConfigV2.idleCardBonusCap);
            return AiConfigV2.economyIdleBuilderDeliveryTolerance * ramp;
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
