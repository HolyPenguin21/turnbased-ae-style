using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // An optional local service of a pinned recipient. No new intent, production request,
    // waiting phase or spending authority. Materialization owns deployment; the shared
    // GroundCombat handoff owns every roster decision and its atomic execution.
    //
    // A local refit lands a card beside an own army on an own Base and hands it over through the
    // shared handoff (fill a free slot, else exchange the weakest body). One mechanism for every
    // recipient a capability demand is pinned to; each window only names its recipient, the fight
    // it is judged against and its limits:
    //   AttackAssault     — the committed assault's primary on a just-captured Base (original);
    //   AttackPreparation — a live Attack preparation's host standing on an own Base;
    //   AttackFist        — the free strike army an unbound Attack demand strengthens;
    //   GarrisonFloor     — the garrison of an own Base below its defence floor.
    // Playtest 2026-10-22 (Kryll T19-T23): host #17 was full 7/7, every pinned demand needed the
    // card inside it, so Heavy Tank never reached it and the preparation stalled for 5 turns.
    internal static class AttackBaseRefitPolicy
    {
        internal enum RefitWindow { AttackAssault, AttackPreparation, AttackFist, GarrisonFloor }

        internal readonly struct RefitContext
        {
            internal readonly RefitWindow Window;
            internal readonly ArmyData Primary;
            // The owning Attack operation (assault / preparation); null for a free fist or garrison.
            internal readonly AttackIntent Attack;
            // The fight the handoff is judged against; null when the recipient has none (garrison).
            internal readonly HexCoord? TargetHex;
            internal readonly int Stamp;

            internal RefitContext(RefitWindow window, ArmyData primary, AttackIntent attack,
                HexCoord? targetHex, int stamp)
            {
                Window = window; Primary = primary; Attack = attack; TargetHex = targetHex; Stamp = stamp;
            }

            internal bool IsAssault => Window == RefitWindow.AttackAssault;
        }

        internal static bool WindowOpen(WorldSnapshot snap, AttackIntent attack)
        {
            if (snap?.Self == null || attack == null || !attack.AssaultStarted
                || !attack.PrimaryArmyId.HasValue || !attack.RefitBaseHex.HasValue
                || attack.RefitCaptureTurn < 0 || snap.TurnNumber < attack.RefitCaptureTurn
                || snap.TurnNumber > attack.RefitCaptureTurn + 1
                || (attack.Phase != AttackMissionPhase.Assault
                    && attack.Phase != AttackMissionPhase.Reinforcement))
                return false;
            return snap.Self.BaseHexes?.Contains(attack.RefitBaseHex.Value) == true
                && snap.Self.Armies?.Any(a => a != null && a.ArmyId == attack.PrimaryArmyId
                    && a.IsStructuralRaidActor && a.Hex.Equals(attack.RefitBaseHex.Value)) == true;
        }

        // `intents` — the owner's durable intents (MissionIntentState.All).
        internal static AttackIntent Resolve(IEnumerable<MissionIntent> intents, PlayerSetupData player,
            int id, HexCoord hex, int turn)
        {
            var a = intents?.FirstOrDefault(i =>
                i.Status == IntentStatus.Active && i.Attack?.PrimaryArmyId == id)?.Attack;
            var building = OwnWorld.Of(player).BuildingAt(hex);
            return a != null && a.AssaultStarted && a.RefitBaseHex.HasValue && a.RefitBaseHex.Value.Equals(hex)
                && a.RefitCaptureTurn >= 0 && turn >= a.RefitCaptureTurn && turn <= a.RefitCaptureTurn + 1
                && (a.Phase == AttackMissionPhase.Assault || a.Phase == AttackMissionPhase.Reinforcement)
                && building != null && building.IsBase && building.Owner == player ? a : null;
        }

        // The window a pinned FieldCombatPower demand is served through, if any.
        internal static RefitWindow? WindowFor(AxisDemand demand)
        {
            if (demand == null)
                return null;
            if (demand.AttackLocalRefit)
                return RefitWindow.AttackAssault;
            if (demand.Capability != CapabilityKind.FieldCombatPower)
                return null;
            if (demand.AttackFistArmyId.HasValue)
                return demand.AttackFistIsPreparationHost ? RefitWindow.AttackPreparation : RefitWindow.AttackFist;
            return demand.DeliveryShape == CapabilityDeliveryShape.Garrison
                ? RefitWindow.GarrisonFloor : (RefitWindow?)null;
        }

        // The live recipient of `window` at `hex`, or false when the window is closed. `primaryId`
        // is null only for a garrison (found on the hex). `targetHex` names the fight of a free
        // fist; an Attack operation supplies its own target.
        internal static bool TryResolveContext(WorldSnapshot snap, PlayerSetupData player,
            IEnumerable<MissionIntent> intents, RefitWindow window, int? primaryId, HexCoord hex,
            int turn, HexCoord? targetHex, out RefitContext context)
        {
            context = default;
            if (player == null)
                return false;
            var building = OwnWorld.Of(player).BuildingAt(hex);
            if (building == null || !building.IsBase || building.Owner != player)
                return false;
            ArmyData primary = primaryId.HasValue ? OwnWorld.Of(player).Army(primaryId.Value)
                : window == RefitWindow.GarrisonFloor
                    ? OwnWorld.Of(player).Armies.FirstOrDefault(a => a != null && a.IsGarrison && a.Hex.Equals(hex))
                    : null;
            if (primary == null || !primary.Hex.Equals(hex) || primary.IsAirArmy || primary.IsAirfield
                || primary.IsPrison || primary.Members.Count == 0)
                return false;
            switch (window)
            {
                case RefitWindow.AttackAssault:
                {
                    AttackIntent a = Resolve(intents, player, primary.Id, hex, turn);
                    if (a == null) return false;
                    context = new RefitContext(window, primary, a, a.Target.Hex, a.RefitCaptureTurn);
                    return true;
                }
                case RefitWindow.AttackPreparation:
                {
                    AttackIntent a = intents?.FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                        && i.Attack?.PrimaryArmyId == primary.Id)?.Attack;
                    if (a == null || !a.Preparation || a.AssaultStarted || a.Phase != AttackMissionPhase.Gather
                        || primary.IsGarrison)
                        return false;
                    context = new RefitContext(window, primary, a, a.Target.Hex, -1);
                    return true;
                }
                case RefitWindow.AttackFist:
                {
                    // Free: no live operation owns it (the same claim set every planner reads).
                    var active = (intents ?? Enumerable.Empty<MissionIntent>())
                        .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
                    if (primary.IsGarrison
                        || ActorCommitments.FromIntents(active, snap, null).IsArmyClaimed(primary.Id)
                        || snap?.Self?.Armies?.Any(a => a != null && a.ArmyId == primary.Id
                            && a.IsStructuralRaidActor) != true)
                        return false;
                    context = new RefitContext(window, primary, null, targetHex, -1);
                    return true;
                }
                case RefitWindow.GarrisonFloor:
                    if (!primary.IsGarrison) return false;
                    context = new RefitContext(window, primary, null, null, -1);
                    return true;
            }
            return false;
        }

        // The context a built plan was priced in, re-read live.
        private static bool TryResolveContext(MaterializationPlan plan, WorldSnapshot snap,
            PlayerSetupData player, IEnumerable<MissionIntent> intents, int turn, out RefitContext context)
        {
            context = default;
            return plan?.AttackRefitPrimaryId != null
                && TryResolveContext(snap, player, intents, plan.AttackRefitWindow, plan.AttackRefitPrimaryId,
                    plan.Deploy.Hex, turn, plan.AttackRefitTargetHex, out context)
                && context.Primary.Id == plan.AttackRefitPrimaryId.Value
                && context.Stamp == plan.AttackRefitCaptureTurn;
        }

        internal static List<MaterializationPlan> Enumerate(WorldSnapshot snap, PlayerSetupData player,
            AiHandData hand, AiTurnContext ctx, AxisDemand demand, ActorCommitments commitments,
            MaterializationReservation reservation, ISet<CardData> excluded, IEnumerable<MissionIntent> intents,
            PlayerRoot root = null)
        {
            var plans = new List<MaterializationPlan>();
            RefitWindow? window = WindowFor(demand);
            if (player == null || hand == null || snap == null || !window.HasValue || !demand.TargetHex.HasValue
                || (window.Value != RefitWindow.GarrisonFloor && !demand.AttackFistArmyId.HasValue)) return plans;
            if (!TryResolveContext(snap, player, intents, window.Value, demand.AttackFistArmyId,
                    demand.TargetHex.Value, snap.TurnNumber, demand.AttackObjectiveHex, out RefitContext refit)
                || (refit.IsAssault && !WindowOpen(snap, refit.Attack)))
                return plans;
            var primary = refit.Primary;
            // Reuse a legal empty local shell, otherwise price a canonical new container.
            var shell = ReusableArmySelector.FindReusableAt(player, primary.Hex, commitments);
            var placement = shell == null ? new PlacementOption(primary.Hex, DeploymentKind.NewArmy, null)
                : new PlacementOption(primary.Hex, DeploymentKind.ReusableShell, shell);
            bool Excluded(CardData card) => excluded?.Contains(card) == true
                || reservation?.ClaimsDevelopmentOperatorCard(card) == true;
            void AddCardPlan(CardData card, int index, CardData equipment = null, int equipmentIndex = -1)
            {
                var def = card.Definition;
                // Outside the assault the ordinary placement already deploys a card that fits the
                // recipient directly; the refit serves only the recipient that has no slot for it.
                if (!refit.IsAssault && CardPlayExecutor.CanFitAfterDeploy(primary, def)) return;
                var abilities = MaterializationChainMatching.EffectiveAbilities(def, card.Equipment, card.Mutator);
                var plan = MaterializationPlanFactory.MakeExistingPlan(
                    equipment == null ? MaterializationChainKind.Direct : MaterializationChainKind.AttachDeploy,
                    demand, card, index, equipment, equipmentIndex, placement,
                    equipment == null ? abilities
                        : EquipmentSystem.EffectiveAbilities(card, equipment.Definition));
                plan.AttackRefitPrimaryId = primary.Id;
                plan.AttackRefitWindow = refit.Window;
                plan.AttackRefitTargetHex = refit.TargetHex;
                plan.AttackRefitCaptureTurn = refit.Stamp;
                plan.AttackRefitRoster = RosterKey(primary);
                if (!Validate(plan, snap, player, intents, out HandoffPlan handoff, out ArmyData support)) return;
                if (handoff.Displaced.Count == 0 && (handoff.Promote == null || primary.Commander == null)
                    && primary.CanFitAdditionalCard(def))
                {
                    plan.Deploy = new PlacementOption(primary.Hex, DeploymentKind.ExistingArmy, primary);
                    MaterializationPlanFactory.FillCostsAndKey(plan, def, card, equipment, index, equipmentIndex, -1);
                }
                else
                    plan.ApCost += GroundCombatReinforcement.HandoffApCost(handoff, primary, support);
                plan.StableKey += refit.IsAssault
                    ? $"|refit:{primary.Id}:{refit.Stamp}:{plan.AttackRefitRoster}"
                    : $"|refit:{refit.Window}:{primary.Id}:{plan.AttackRefitRoster}";
                plan.AttackRefitPromotesCommander = handoff.Promote != null;
                var roster = FinalRoster(primary, handoff);
                // The bank already protects a funded assault's activation; keep only the extra
                // charge here. Reinforcement/settled windows still protect the full activation.
                plan.AttackRefitFollowupAp = FollowupAp(player, refit, roster, ctx, root);
                plans.Add(plan);
            }
            for (int index = 0; index < hand.Hand.Count; index++)
            {
                var card = hand.Hand[index];
                var def = card?.Definition;
                if (def == null || def.isAviation || (def.cardType != CardType.Unit && def.cardType != CardType.Hero)
                    || (refit.Window == RefitWindow.GarrisonFloor && def.cardType == CardType.Hero)
                    || Excluded(card)
                    || !ArmyActions.HasRequiredGroundDeploymentBuilding(player, primary.Hex, def)) continue;
                AddCardPlan(card, index);
                if (card.Equipment != null && card.Mutator != null) continue;
                for (int j = 0; j < hand.Hand.Count; j++)
                {
                    var equipment = hand.Hand[j];
                    var eqDef = equipment?.Definition;
                    if (j == index || eqDef?.cardType != CardType.Equipment || eqDef.equipment == null
                        || EquipmentSystem.GetAttachment(card, eqDef) != null
                        || Excluded(equipment) || !MaterializationChainMatching.EquipmentDefFitsHostDef(eqDef, def)) continue;
                    AddCardPlan(card, index, equipment, j);
                }
            }
            return plans;
        }

        // Projection uses the same effective stat/ability line as every materialization.
        // It consumes neither runtime identity nor a game registry entry.
        private static UnitData Project(MaterializationPlan plan, PlayerSetupData owner) =>
            Project(plan.BaseCardInHand?.Definition, AiPower.ProjectMaterialization(plan),
                CapabilityQualityEvaluator.ProjectedActivationApCost(plan), owner);

        private static UnitData Project(CardDefinition def, AiPower.ProjectedStrategicLine line,
            int activationApCost, PlayerSetupData owner)
        {
            var u = UnitData.CreateProjection();
            u.Name = def.displayName; u.Owner = owner; u.OriginatingCard = def;
            u.IsHero = def.cardType == CardType.Hero; u.CommandRating = line.CommandRating;
            u.Attack = line.Attack; u.Defense = line.Defense;
            u.Range = line.Range; u.HitPointsMax = u.HitPointsCurrent = line.HitPoints;
            u.Initiative = line.Initiative; u.Fate = u.FateMax = line.Fate;
            u.MoveMax = u.MoveCurrent = line.MoveMax; u.ActivationApCost = activationApCost;
            foreach (var ability in line.EffectiveAbilities) u.Abilities.Add(ability);
            foreach (var tag in def.unitTypeTags ?? new List<UnitTypeTag>()) u.TypeTags.Add(tag);
            return u;
        }

        // The not-yet-deployed container the card would land in beside the recipient.
        private static ArmyData ProjectedSupport(ArmyData primary, UnitData card, PlayerSetupData player,
            ArmyData deployInto)
        {
            var support = ArmyData.CreateVisualSnapshot();
            support.Owner = player; support.Hex = primary.Hex;
            if (deployInto != null && deployInto != primary && deployInto.HasActivatedThisTurn)
                support.MarkActivated();
            support.Members.Add(card);
            return support;
        }

        // THE handoff of one window: Attack windows run the Attack handoff (command handover,
        // power or coverage), a garrison takes bodies only and must gain power.
        private static HandoffPlan PlanRefitHandoff(RefitContext refit, ArmyData support,
            WorldSnapshot snap, bool requireChargeNow)
        {
            ArmyData primary = refit.Primary;
            if (refit.Window == RefitWindow.GarrisonFloor)
            {
                HandoffPlan h = GroundCombatReinforcement.PlanHandoff(primary, support, null, 0f, out _,
                    requireChargeNow);
                return h != null && AiPower.EffectiveArmyPower(FinalRoster(primary, h))
                    > AiPower.EffectiveArmyPower(primary.Members) ? h : null;
            }
            IReadOnlyList<WorthIt.DefendingArmy> opposition = refit.TargetHex.HasValue
                ? KnownOpposition.OnHex(snap, refit.TargetHex.Value)
                : (IReadOnlyList<WorthIt.DefendingArmy>)Array.Empty<WorthIt.DefendingArmy>();
            float bonus = refit.TargetHex.HasValue
                ? AiMapMemory.KnownHexDefenseBonus(snap?.Observer, snap?.Map, refit.TargetHex.Value) : 0f;
            return GroundCombatReinforcement.PlanAttackHandoff(primary, support, opposition, bonus,
                requireChargeNow, out _, capacityIsProgress: true);
        }

        // What a window may not lose: an assault keeps force, movement and coverage (it marches
        // now); a preparation or free fist keeps force and coverage; a garrison only gains power.
        private static bool KeepsWindowLimits(RefitContext refit, HandoffPlan plan, WorldSnapshot snap) =>
            refit.Window == RefitWindow.GarrisonFloor
            || KeepsForce(refit.Primary, plan)
                && (!refit.IsAssault || KeepsMovement(refit.Primary, plan))
                && KeepsCoverageAt(refit.Primary, plan, snap, refit.TargetHex);

        internal static bool Validate(MaterializationPlan plan, WorldSnapshot snap, PlayerSetupData player,
            IEnumerable<MissionIntent> intents, out HandoffPlan handoff, out ArmyData support)
        {
            handoff = null; support = null;
            if (plan?.AttackRefitPrimaryId == null || plan.BaseCardInHand?.Definition == null || snap == null)
                return false;
            if (!TryResolveContext(plan, snap, player, intents, snap.TurnNumber, out RefitContext refit)
                || RosterKey(refit.Primary) != plan.AttackRefitRoster) return false;
            var primary = refit.Primary;
            if (plan.Deploy.Army != null && plan.Deploy.Army != primary
                && (plan.Deploy.Army.Members.Count != 0 || plan.Deploy.Army.Owner != player
                    || !plan.Deploy.Army.Hex.Equals(primary.Hex))) return false;
            support = ProjectedSupport(primary, Project(plan, player), player, plan.Deploy.Army);
            handoff = PlanRefitHandoff(refit, support, snap, requireChargeNow: false);
            return handoff != null && KeepsWindowLimits(refit, handoff, snap);
        }

        // Read-only witness for a demand that waits on a held/generated/undrawn card: would this
        // card, landed beside the full recipient, be exchanged into it and raise its power? The
        // same projection and handoff rule the refit chain later runs.
        internal static bool ExchangeRaisesRecipient(ArmyData primary, CardDefinition def,
            AiPower.ProjectedStrategicLine line, PlayerSetupData player)
        {
            if (primary == null || def == null || def.isAviation || player == null)
                return false;
            var building = OwnWorld.Of(player).BuildingAt(primary.Hex);
            if (building == null || !building.IsBase || building.Owner != player)
                return false;
            var support = ProjectedSupport(primary, Project(def, line, Math.Max(0, def.activationApCost), player),
                player, null);
            HandoffPlan h = GroundCombatReinforcement.PlanHandoff(primary, support, null, 0f, out _,
                requireChargeNow: false);
            return h != null && AiPower.EffectiveArmyPower(FinalRoster(primary, h))
                > AiPower.EffectiveArmyPower(primary.Members);
        }

        // Does this plan serve this demand: the demand's window and its exact recipient, still
        // valid live, and — for a coverage-gap preparation — closing more of the target's coverage.
        internal static bool ServesDemand(MaterializationPlan plan, AxisDemand demand, WorldSnapshot snap,
            PlayerSetupData player, IEnumerable<MissionIntent> intents)
        {
            RefitWindow? window = WindowFor(demand);
            if (plan?.AttackRefitPrimaryId == null || !window.HasValue || plan.AttackRefitWindow != window.Value
                || (demand.AttackFistArmyId.HasValue && demand.AttackFistArmyId.Value != plan.AttackRefitPrimaryId.Value)
                || (demand.TargetHex.HasValue && !demand.TargetHex.Value.Equals(plan.Deploy.Hex))
                // A recipient another axis materialized this turn is that axis's (the same rule
                // MaterializationDeliveryPolicy applies to a direct deploy).
                || StrategicCapabilityLeaseRegistry.LeasedAxis(player, snap?.TurnNumber ?? -1,
                    plan.AttackRefitPrimaryId.Value) is DesireAxis leased && leased != demand.RequestingAxis
                || !Validate(plan, snap, player, intents, out HandoffPlan handoff, out _))
                return false;
            if (!demand.AttackCoverageGap || !demand.AttackCoverageTargetHex.HasValue)
                return true;
            var primary = OwnWorld.Of(player).Army(plan.AttackRefitPrimaryId.Value);
            HexCoord target = demand.AttackCoverageTargetHex.Value;
            return MaterializationDeliveryPolicy.UncoveredDefenderCount(snap, FinalRoster(primary, handoff), null, target)
                < MaterializationDeliveryPolicy.UncoveredDefenderCount(snap, primary.Members, null, target);
        }

        internal static List<UnitData> FinalRoster(ArmyData primary, HandoffPlan plan) =>
            primary.Members.Except(plan.Displaced).Concat(plan.Incoming).ToList();

        internal static bool KeepsMovement(ArmyData primary, HandoffPlan plan) =>
            ArmyData.ComputeMaxMovement(FinalRoster(primary, plan)) >= primary.MaxMovement
            && ArmyData.ComputeCurrentMovement(FinalRoster(primary, plan)) >= primary.CurrentMovement;

        internal static bool KeepsForce(ArmyData primary, HandoffPlan plan) =>
            AiPower.EffectiveArmyPower(FinalRoster(primary, plan)) >= AiPower.EffectiveArmyPower(primary.Members);

        internal static bool KeepsCoverage(ArmyData primary, HandoffPlan plan,
            WorldSnapshot snap, AttackIntent attack) =>
            KeepsCoverageAt(primary, plan, snap, attack?.Target.Hex);

        private static bool KeepsCoverageAt(ArmyData primary, HandoffPlan plan, WorldSnapshot snap,
            HexCoord? target)
        {
            if (!target.HasValue)
                return true;
            var opposition = KnownOpposition.OnHex(snap, target.Value);
            float bonus = AiMapMemory.KnownHexDefenseBonus(snap?.Observer, snap?.Map, target.Value);
            return !WorthIt.CanDamageAll(primary.Members.Select(WorthIt.FromLiveUnit).ToList(), opposition, bonus)
                || WorthIt.CanDamageAll(FinalRoster(primary, plan).Select(WorthIt.FromLiveUnit).ToList(), opposition, bonus);
        }

        internal static int FollowupAp(PlayerSetupData player, ArmyData primary,
            AttackIntent attack, IEnumerable<UnitData> roster, AiTurnContext ctx, PlayerRoot root = null)
        {
            int total = primary.ProjectedActivationApCost(roster);
            if (attack == null || ctx == null || OperationContinuationWindow.IsSettled(player, ctx.TurnNumber)
                || attack.Phase != AttackMissionPhase.Assault || primary.CurrentMovement <= 0)
                return total;
            return Math.Max(0, total - (int)StrategicSpendability.OperationContinuationCredit(
                player, root, ctx, primary.Id));
        }

        // Only the assault marches on this activation; a preparation host, a free fist and a
        // garrison receive the body and stay — no onward activation is promised for them.
        internal static int FollowupAp(PlayerSetupData player, RefitContext refit,
            IEnumerable<UnitData> roster, AiTurnContext ctx, PlayerRoot root = null) =>
            refit.IsAssault ? FollowupAp(player, refit.Primary, refit.Attack, roster, ctx, root) : 0;

        internal static bool FollowupStillCurrent(MaterializationPlan plan, PlayerSetupData player,
            IEnumerable<MissionIntent> intents, AiTurnContext ctx, HandoffPlan handoff, PlayerRoot root = null)
        {
            if (plan.AttackRefitWindow != RefitWindow.AttackAssault)
                return plan.AttackRefitFollowupAp == 0f;
            var primary = OwnWorld.Of(player).Army(plan.AttackRefitPrimaryId.Value);
            var attack = Resolve(intents, player, primary?.Id ?? -1, plan.Deploy.Hex, ctx.TurnNumber);
            return primary != null && attack != null
                && plan.AttackRefitFollowupAp == FollowupAp(player, primary, attack,
                    FinalRoster(primary, handoff), ctx, root);
        }

        // Preserve other operation actors' holds while replacing this primary's own credit
        // with the activation of its final roster, including when it was outside the prefix.
        internal static bool OnwardFunded(MaterializationPlan plan, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, float remainingChainCost, IEnumerable<UnitData> roster)
        {
            var primary = OwnWorld.Of(player).Army(plan.AttackRefitPrimaryId.Value);
            if (primary == null) return false;
            int activation = plan.AttackRefitWindow == RefitWindow.AttackAssault
                ? primary.ProjectedActivationApCost(roster) : 0;
            float otherOperations = StrategicSpendability.OperationContinuationHold(player, root, ctx)
                - StrategicSpendability.OperationContinuationCredit(player, root, ctx, primary.Id);
            return StrategicSpendability.SpendableAp(player, root, ctx,
                new SpendAuthority(TurnResourceBook.OperationContinuationOwner, false))
                >= remainingChainCost + activation + otherOperations;
        }

        // The executed half: after the card landed in `support`, run the window's handoff against
        // the live world (the same rule Validate priced), or report false and leave the card at base.
        internal static bool TryPlanExecution(MaterializationPlan plan, PlayerSetupData player,
            AiTurnContext ctx, WorldSnapshot snap, IEnumerable<MissionIntent> intents, ArmyData support,
            out RefitContext refit, out HandoffPlan handoff)
        {
            handoff = null;
            if (!TryResolveContext(plan, snap, player, intents, ctx.TurnNumber, out refit) || support == null
                || (support != refit.Primary && RosterKey(refit.Primary) != plan.AttackRefitRoster))
                return false;
            if (support == refit.Primary)
                return true;
            handoff = PlanRefitHandoff(refit, support, snap, requireChargeNow: true);
            return handoff != null && KeepsWindowLimits(refit, handoff, snap);
        }

        internal static string RosterKey(ArmyData army) => army == null ? "-" : $"activated:{army.HasActivatedThisTurn}|" + string.Join(";",
            army.Members.Select(u => $"{u.RuntimeId}:{u.Attack}:{u.Defense}:{u.Range}:"
                + $"{u.HitPointsCurrent}/{u.HitPointsMax}:{u.Initiative}:{u.Fate}/{u.FateMax}:{u.CommandRating}:"
                + $"{u.MoveCurrent}/{u.MoveMax}:{u.ActivationApCost}:{army.HasActivationCoverageFor(u)}:"
                + $"{string.Join(",", u.Abilities.OrderBy(x => x, StringComparer.Ordinal))}:"
                + $"{string.Join(",", u.TypeTags.OrderBy(x => x))}"));

        internal static string Fingerprint(PlayerSetupData player, IEnumerable<MissionIntent> intents,
            int turn) => string.Join("|",
            (intents ?? Enumerable.Empty<MissionIntent>()).Where(i => i.Status == IntentStatus.Active
                && i.Attack?.RefitBaseHex.HasValue == true
                && turn >= i.Attack.RefitCaptureTurn && turn <= i.Attack.RefitCaptureTurn + 1)
                .OrderBy(i => i.IntentKey).Select(i =>
                $"{i.IntentKey}:{i.Attack.RefitBaseHex}:{i.Attack.RefitCaptureTurn}:{turn}:"
                + RosterKey(i.Attack.PrimaryArmyId.HasValue
                    ? OwnWorld.Of(player).Army(i.Attack.PrimaryArmyId.Value) : null)));
    }
}
