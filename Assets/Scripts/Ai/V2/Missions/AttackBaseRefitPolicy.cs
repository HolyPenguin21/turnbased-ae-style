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
    // An optional local service of a committed Attack. No new intent, production request,
    // waiting phase or spending authority. Materialization owns deployment; the shared
    // GroundCombat handoff owns every roster decision and its atomic execution.
    internal static class AttackBaseRefitPolicy
    {
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

        internal static AttackIntent Resolve(PlayerSetupData player, int id, HexCoord hex, int turn)
        {
            var a = MissionIntentRegistry.GetOrCreate(player).All.FirstOrDefault(i =>
                i.Status == IntentStatus.Active && i.Attack?.PrimaryArmyId == id)?.Attack;
            var building = BuildingRegistry.FindAt(hex);
            return a != null && a.AssaultStarted && a.RefitBaseHex.HasValue && a.RefitBaseHex.Value.Equals(hex)
                && a.RefitCaptureTurn >= 0 && turn >= a.RefitCaptureTurn && turn <= a.RefitCaptureTurn + 1
                && (a.Phase == AttackMissionPhase.Assault || a.Phase == AttackMissionPhase.Reinforcement)
                && building != null && building.IsBase && building.Owner == player ? a : null;
        }

        internal static List<MaterializationPlan> Enumerate(WorldSnapshot snap, PlayerSetupData player,
            AiHandData hand, AiTurnContext ctx, AxisDemand demand, ActorCommitments commitments,
            MaterializationReservation reservation, ISet<CardData> excluded, PlayerRoot root = null)
        {
            var plans = new List<MaterializationPlan>();
            if (player == null || hand == null || demand == null
                || !demand.AttackFistArmyId.HasValue || !demand.TargetHex.HasValue) return plans;
            var intent = MissionIntentRegistry.GetOrCreate(player).All.FirstOrDefault(i =>
                i.Status == IntentStatus.Active && i.Attack?.PrimaryArmyId == demand.AttackFistArmyId)?.Attack;
            if (!WindowOpen(snap, intent)) return plans;
            var primary = AiV2Util.ResolveArmy(player, demand.AttackFistArmyId.Value);
            if (primary == null || !primary.Hex.Equals(demand.TargetHex.Value)) return plans;
            // Reuse a legal empty local shell, otherwise price a canonical new container.
            var shell = ReusableArmySelector.FindReusableAt(player, primary.Hex, commitments);
            var placement = shell == null ? new PlacementOption(primary.Hex, DeploymentKind.NewArmy, null)
                : new PlacementOption(primary.Hex, DeploymentKind.ReusableShell, shell);
            bool Excluded(CardData card) => excluded?.Contains(card) == true
                || reservation?.ClaimsDevelopmentOperatorCard(card) == true;
            void AddCardPlan(CardData card, int index, CardData equipment = null, int equipmentIndex = -1)
            {
                var def = card.Definition;
                var abilities = MaterializationChainMatching.EffectiveAbilities(def, card.Equipment);
                var plan = MaterializationPlanFactory.MakeExistingPlan(
                    equipment == null ? MaterializationChainKind.Direct : MaterializationChainKind.AttachDeploy,
                    demand, card, index, equipment, equipmentIndex, placement,
                    equipment == null ? abilities
                        : EquipmentSystem.EffectiveAbilities(abilities.ToList(), equipment.Definition.equipment));
                plan.AttackRefitPrimaryId = primary.Id;
                plan.AttackRefitCaptureTurn = intent.RefitCaptureTurn;
                plan.AttackRefitRoster = RosterKey(primary);
                if (!Validate(plan, snap, player, out HandoffPlan handoff, out ArmyData support)) return;
                if (handoff.Displaced.Count == 0 && (handoff.Promote == null || primary.Commander == null)
                    && primary.CanFitAdditionalCard(def))
                {
                    plan.Deploy = new PlacementOption(primary.Hex, DeploymentKind.ExistingArmy, primary);
                    MaterializationPlanFactory.FillCostsAndKey(plan, def, card, equipment, index, equipmentIndex, -1);
                }
                else
                    plan.ApCost += GroundCombatReinforcement.HandoffApCost(handoff, primary, support);
                plan.StableKey += $"|refit:{primary.Id}:{intent.RefitCaptureTurn}:{plan.AttackRefitRoster}";
                plan.AttackRefitPromotesCommander = handoff.Promote != null;
                var roster = FinalRoster(primary, handoff);
                // The bank already protects a funded assault's activation; keep only the extra
                // charge here. Reinforcement/settled windows still protect the full activation.
                plan.AttackRefitFollowupAp = FollowupAp(player, primary, intent, roster, ctx, root);
                plans.Add(plan);
            }
            for (int index = 0; index < hand.Hand.Count; index++)
            {
                var card = hand.Hand[index];
                var def = card?.Definition;
                if (def == null || def.isAviation || (def.cardType != CardType.Unit && def.cardType != CardType.Hero)
                    || Excluded(card)
                    || !ArmyActions.HasRequiredGroundDeploymentBuilding(player, primary.Hex, def)) continue;
                AddCardPlan(card, index);
                if (card.Equipment != null) continue;
                for (int j = 0; j < hand.Hand.Count; j++)
                {
                    var equipment = hand.Hand[j];
                    var eqDef = equipment?.Definition;
                    if (j == index || eqDef?.cardType != CardType.Equipment || eqDef.equipment == null
                        || Excluded(equipment) || !MaterializationChainMatching.EquipmentDefFitsHostDef(eqDef, def)) continue;
                    AddCardPlan(card, index, equipment, j);
                }
            }
            return plans;
        }

        // Projection uses the same effective stat/ability line as every materialization.
        // It consumes neither runtime identity nor a game registry entry.
        private static UnitData Project(MaterializationPlan plan, PlayerSetupData owner)
        {
            var def = plan.BaseCardInHand?.Definition;
            var line = AiPower.ProjectMaterialization(plan);
            var u = UnitData.CreateProjection();
            u.Name = def.displayName; u.Owner = owner; u.OriginatingCard = def;
            u.IsHero = def.cardType == CardType.Hero; u.CommandRating = line.CommandRating;
            u.Attack = line.Attack; u.Defense = line.Defense; u.Resistance = line.Resistance;
            u.Range = line.Range; u.HitPointsMax = u.HitPointsCurrent = line.HitPoints;
            u.Initiative = line.Initiative; u.Fate = u.FateMax = line.Fate;
            u.MoveMax = u.MoveCurrent = line.MoveMax; u.ActivationApCost = CapabilityQualityEvaluator.ProjectedActivationApCost(plan);
            foreach (var ability in line.EffectiveAbilities) u.Abilities.Add(ability);
            foreach (var tag in def.unitTypeTags ?? new List<UnitTypeTag>()) u.TypeTags.Add(tag);
            return u;
        }

        internal static bool Validate(MaterializationPlan plan, WorldSnapshot snap, PlayerSetupData player,
            out HandoffPlan handoff, out ArmyData support)
        {
            handoff = null; support = null;
            if (plan?.AttackRefitPrimaryId == null || plan.BaseCardInHand?.Definition == null || snap == null)
                return false;
            var primary = AiV2Util.ResolveArmy(player, plan.AttackRefitPrimaryId.Value);
            var attack = Resolve(player, plan.AttackRefitPrimaryId.Value, plan.Deploy.Hex, snap.TurnNumber);
            if (primary == null || !primary.Hex.Equals(plan.Deploy.Hex) || attack == null
                || attack.RefitCaptureTurn != plan.AttackRefitCaptureTurn
                || RosterKey(primary) != plan.AttackRefitRoster) return false;
            support = ArmyData.CreateVisualSnapshot();
            support.Owner = player; support.Hex = primary.Hex;
            if (plan.Deploy.Army != null && plan.Deploy.Army != primary)
            {
                if (plan.Deploy.Army.Members.Count != 0 || plan.Deploy.Army.Owner != player
                    || !plan.Deploy.Army.Hex.Equals(primary.Hex)) return false;
                if (plan.Deploy.Army.HasActivatedThisTurn) support.MarkActivated();
            }
            support.Members.Add(Project(plan, player));
            handoff = GroundCombatReinforcement.PlanAttackHandoff(primary, support,
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, attack.Target.Hex),
                AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, snap.Map, attack.Target.Hex),
                false, out _, capacityIsProgress: true);
            return handoff != null && KeepsForce(primary, handoff) && KeepsMovement(primary, handoff)
                && KeepsCoverage(primary, handoff, snap, attack);
        }

        internal static List<UnitData> FinalRoster(ArmyData primary, HandoffPlan plan) =>
            primary.Members.Except(plan.Displaced).Concat(plan.Incoming).ToList();

        internal static bool KeepsMovement(ArmyData primary, HandoffPlan plan) =>
            ArmyData.ComputeMaxMovement(FinalRoster(primary, plan)) >= primary.MaxMovement
            && ArmyData.ComputeCurrentMovement(FinalRoster(primary, plan)) >= primary.CurrentMovement;

        internal static bool KeepsForce(ArmyData primary, HandoffPlan plan) =>
            AiPower.EffectiveArmyPower(FinalRoster(primary, plan)) >= AiPower.EffectiveArmyPower(primary.Members);

        internal static bool KeepsCoverage(ArmyData primary, HandoffPlan plan,
            WorldSnapshot snap, AttackIntent attack)
        {
            var opposition = AttackObjectiveEvaluator.KnownSiteOpposition(snap, attack.Target.Hex);
            float bonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, snap.Map, attack.Target.Hex);
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

        internal static bool FollowupStillCurrent(MaterializationPlan plan, PlayerSetupData player,
            AiTurnContext ctx, HandoffPlan handoff, PlayerRoot root = null)
        {
            var primary = AiV2Util.ResolveArmy(player, plan.AttackRefitPrimaryId.Value);
            var attack = Resolve(player, primary?.Id ?? -1, plan.Deploy.Hex, ctx.TurnNumber);
            return primary != null && attack != null
                && plan.AttackRefitFollowupAp == FollowupAp(player, primary, attack,
                    FinalRoster(primary, handoff), ctx, root);
        }

        // Preserve other operation actors' holds while replacing this primary's own credit
        // with the activation of its final roster, including when it was outside the prefix.
        internal static bool OnwardFunded(MaterializationPlan plan, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, float remainingChainCost, IEnumerable<UnitData> roster)
        {
            var primary = AiV2Util.ResolveArmy(player, plan.AttackRefitPrimaryId.Value);
            if (primary == null) return false;
            int activation = primary.ProjectedActivationApCost(roster);
            float otherOperations = StrategicSpendability.OperationContinuationHold(player, root, ctx)
                - StrategicSpendability.OperationContinuationCredit(player, root, ctx, primary.Id);
            return StrategicSpendability.SpendableAp(player, root, ctx,
                new SpendAuthority(TurnResourceBook.OperationContinuationOwner, false))
                >= remainingChainCost + activation + otherOperations;
        }

        internal static string RosterKey(ArmyData army) => army == null ? "-" : $"activated:{army.HasActivatedThisTurn}|" + string.Join(";",
            army.Members.Select(u => $"{u.RuntimeId}:{u.Attack}:{u.Defense}:{u.Resistance}:{u.Range}:"
                + $"{u.HitPointsCurrent}/{u.HitPointsMax}:{u.Initiative}:{u.Fate}/{u.FateMax}:{u.CommandRating}:"
                + $"{u.MoveCurrent}/{u.MoveMax}:{u.ActivationApCost}:{army.HasActivationCoverageFor(u)}:"
                + $"{string.Join(",", u.Abilities.OrderBy(x => x, StringComparer.Ordinal))}:"
                + $"{string.Join(",", u.TypeTags.OrderBy(x => x))}"));

        internal static string Fingerprint(PlayerSetupData player, int turn) => string.Join("|",
            MissionIntentRegistry.GetOrCreate(player).All.Where(i => i.Status == IntentStatus.Active
                && i.Attack?.RefitBaseHex.HasValue == true
                && turn >= i.Attack.RefitCaptureTurn && turn <= i.Attack.RefitCaptureTurn + 1)
                .OrderBy(i => i.IntentKey).Select(i =>
                $"{i.IntentKey}:{i.Attack.RefitBaseHex}:{i.Attack.RefitCaptureTurn}:{turn}:"
                + RosterKey(i.Attack.PrimaryArmyId.HasValue
                    ? AiV2Util.ResolveArmy(player, i.Attack.PrimaryArmyId.Value) : null)));
    }
}
