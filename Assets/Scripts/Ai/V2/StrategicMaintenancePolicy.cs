using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC MAINTENANCE POLICY
    // ===========================================================================================
    //  Actions that are strategically useful but are NOT "deploy a new capability" missions:
    //    1. place an internal Research/Production Facility already in hand;
    //    2. when that Facility is blocked specifically by Base slot capacity, upgrade a Base/
    //       Citadel to unlock the next slot;
    //    3. attach useful Equipment from hand to an existing live unit;
    //    4. use an immediately-ready Research/Production source to create a useful card.
    //
    //  These actions run after the bounded reaction pass but BEFORE zero-AP army reorganisation.
    //  CardDrawExecutor asks HasPriorityAction before converting AP into terminal Draws, so Draw
    //  is again the true fallback instead of consuming the budget that makes one of these actions
    //  possible. No movement/hero-positioning is invented here; GenerationSource remains the
    //  authority for whether a generator is usable RIGHT NOW.
    // ===========================================================================================
    internal static class StrategicMaintenancePolicy
    {
        private sealed class GenerationTurnState
        {
            public int Turn = -1;
            public int Attempts;
            public readonly HashSet<string> TriedCardKeys = new HashSet<string>();
        }

        private static readonly Dictionary<PlayerSetupData, GenerationTurnState> GenerationByPlayer =
            new Dictionary<PlayerSetupData, GenerationTurnState>();

        // Keep maintenance bounded. Facility upgrade + placement counts as two actions, generated
        // Equipment + attach as two more, which is enough to exercise the full intended chain in
        // one turn without turning housekeeping into a second unbounded StrategicManager.
        public const int MaxActionsPerTurn = 4;
        private const int MaxStandaloneGenerationAttemptsPerTurn = 1;

        public static bool HasPriorityAction(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, WorldSnapshot snapshot = null)
        {
            if (player == null || root == null || hand == null || ctx == null)
                return false;

            if (FindPlaceableInternalFacility(player, root, hand, ctx) != null)
                return true;
            if (FindCapacityUpgrade(player, root, hand, ctx) != null)
                return true;
            if (FindEquipmentAssignment(player, root, hand) != null)
                return true;
            if (FindGeneration(player, root, hand, ctx) != null)
                return true;
            return false;
        }

        public static bool TryExecuteBest(WorldSnapshot snapshot, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
        {
            if (player == null || root == null || hand == null || ctx == null)
                return false;

            FacilityPlacement facility = FindPlaceableInternalFacility(player, root, hand, ctx);
            if (facility != null)
                return ExecuteFacilityPlacement(player, root, hand, ctx, facility);

            CapacityUpgrade upgrade = FindCapacityUpgrade(player, root, hand, ctx);
            if (upgrade != null)
                return ExecuteCapacityUpgrade(player, root, ctx, upgrade);

            EquipmentAssignment equipment = FindEquipmentAssignment(player, root, hand);
            if (equipment != null)
                return ExecuteEquipment(player, root, hand, ctx, equipment);

            GenerationStep generation = FindGeneration(player, root, hand, ctx);
            if (generation != null)
                return ExecuteGeneration(player, root, hand, ctx, generation);

            return false;
        }

        // ---------------------------------------------------------------- internal Facilities ----

        private sealed class FacilityPlacement
        {
            public CardData Card;
            public BuildingData Building;
        }

        private static IEnumerable<CardData> InternalFacilityCards(AiHandData hand) =>
            hand?.Hand == null
                ? Enumerable.Empty<CardData>()
                : hand.Hand.Where(c => c?.Definition != null
                    && c.Definition.cardType == CardType.Facility
                    && c.Definition.grantedAbilities != null
                    && (c.Definition.grantedAbilities.Contains(UnitAbilities.Research)
                        || c.Definition.grantedAbilities.Contains(UnitAbilities.Production)))
                    .OrderByDescending(c => c.Definition.grantedAbilities.Contains(UnitAbilities.Production))
                    .ThenBy(c => c.Definition.displayName, System.StringComparer.Ordinal);

        private static FacilityPlacement FindPlaceableInternalFacility(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
        {
            foreach (CardData card in InternalFacilityCards(hand))
            {
                foreach (BuildingData building in BuildingRegistry.AllBuildings()
                    .Where(b => b != null && b.Owner == player && b.IsBase)
                    .OrderByDescending(b => b.IsStartingCitadel)
                    .ThenBy(b => b.Hex.Q).ThenBy(b => b.Hex.R))
                {
                    if (BuildingPlayExecutor.CanPlaceFacilityAt(player, hand, ctx, card, building.Hex, out _))
                        return new FacilityPlacement { Card = card, Building = building };
                }
            }
            return null;
        }

        private sealed class CapacityUpgrade
        {
            public BuildingData Building;
            public BaseUpgradeTier Tier;
        }

        private static CapacityUpgrade FindCapacityUpgrade(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
        {
            if (!InternalFacilityCards(hand).Any() || ctx.GameConfig?.baseUpgradeTiers == null)
                return null;

            List<BuildingData> bases = BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player && b.IsBase && b.HasTieredUnlock)
                .ToList();
            if (bases.Count == 0)
                return null;

            // If ANY owned Base already has an unlocked empty slot, the Facility is blocked by
            // something else (usually card affordability), not by capacity. Do not buy a fake
            // dependency upgrade in that case.
            if (bases.Any(b => b.FindFirstAvailableFacilitySlot() >= 0))
                return null;

            foreach (BuildingData b in bases
                .Where(x => x.UnlockedFacilitySlots < x.TotalFacilitySlots)
                .OrderByDescending(x => x.IsStartingCitadel)
                .ThenBy(x => x.Level)
                .ThenBy(x => x.Hex.Q).ThenBy(x => x.Hex.R))
            {
                int tierIndex = b.Level - 1;
                if (tierIndex < 0 || tierIndex >= ctx.GameConfig.baseUpgradeTiers.Length)
                    continue;
                BaseUpgradeTier tier = ctx.GameConfig.baseUpgradeTiers[tierIndex];
                if (tier == null || !root.CanSpendActionPoints(tier.apCost)
                    || (tier.cost != null && !tier.cost.CanAfford(root)))
                    continue;
                return new CapacityUpgrade { Building = b, Tier = tier };
            }
            return null;
        }

        private static bool ExecuteFacilityPlacement(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, FacilityPlacement pick)
        {
            V2PhaseActivity activity = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Main);
            activity.InfrastructureAttempts++;
            BuildingPlayResult r = BuildingPlayExecutor.PlayFacilityCard(
                player, root, hand, ctx, pick.Card, pick.Building.Hex);
            if (!r.Built)
            {
                AiDebugLog.Write($"[AI][V2] maintenance facility — FAIL {pick.Card.Definition.displayName} "
                    + $"@({pick.Building.Hex.Q},{pick.Building.Hex.R}): {r.FailReason}");
                return r.StateChanged;
            }

            activity.InfrastructureBuilt++;
            activity.CardsPlayed++;
            AiDebugLog.Write($"[AI][V2] maintenance facility — placed {pick.Card.Definition.displayName} "
                + $"into {pick.Building.Name} @({pick.Building.Hex.Q},{pick.Building.Hex.R}), ap -{r.ApSpent:0.##}");
            return true;
        }

        private static bool ExecuteCapacityUpgrade(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            CapacityUpgrade pick)
        {
            V2PhaseActivity activity = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Main);
            activity.InfrastructureAttempts++;
            BuildingData b = pick.Building;
            BaseUpgradeTier tier = pick.Tier;
            int apBefore = root.ActionPoints;
            if (b == null || tier == null || !root.CanSpendActionPoints(tier.apCost)
                || (tier.cost != null && !tier.cost.CanAfford(root)))
                return false;

            root.SpendActionPoints(tier.apCost);
            tier.cost?.PayFrom(root);
            b.Level++;
            b.Defense += tier.defenseGain;
            b.Resistance += tier.resistanceGain;

            activity.InfrastructureBuilt++;
            AiDebugLog.Write($"[AI][V2] maintenance capacity — upgraded {b.Name} "
                + $"@({b.Hex.Q},{b.Hex.R}) to level {b.Level}; facility slots {b.UnlockedFacilitySlots}/{b.TotalFacilitySlots}; "
                + $"ap {apBefore}->{root.ActionPoints}");
            return true;
        }

        // ---------------------------------------------------------------- Equipment ----

        private sealed class EquipmentAssignment
        {
            public CardData EquipmentCard;
            public UnitData Target;
            public float Benefit;
            public int ArmyId;
        }

        private static EquipmentAssignment FindEquipmentAssignment(PlayerSetupData player, PlayerRoot root,
            AiHandData hand)
        {
            if (hand?.Hand == null)
                return null;

            EquipmentAssignment best = null;
            foreach (CardData card in hand.Hand
                .Where(c => c?.Definition != null && c.Definition.cardType == CardType.Equipment)
                .OrderBy(c => c.Definition.displayName, System.StringComparer.Ordinal))
            {
                if (!FitsReservedResources(root, player, card.EffectivePlayResourceCost))
                    continue;

                foreach (ArmyData army in ArmyRegistry.AllForOwner(player)
                    .Where(a => a != null && !a.IsPrison && !a.IsAirfield)
                    .OrderBy(a => a.IsGarrison ? 1 : 0)
                    .ThenBy(a => a.Id))
                {
                    foreach (UnitData unit in army.Members.Where(u => u != null && !u.IsAviation))
                    {
                        if (!EquipmentSystem.CanAttach(card, unit, root, out _))
                            continue;
                        float benefit = EquipmentBenefit(card.Definition, unit);
                        if (benefit <= 0.01f)
                            continue;

                        // Valuable field units get first claim; benefit itself still dominates so
                        // a highly synergistic scout/hero item is not blindly forced onto a tank.
                        float hostValue = AiPower.UnitPower(unit) * (army.IsGarrison ? 0.35f : 0.75f);
                        float score = benefit + hostValue;
                        if (best == null || score > best.Benefit + AiConfigV2.allocatorSliceEpsilon
                            || (Mathf.Abs(score - best.Benefit) <= AiConfigV2.allocatorSliceEpsilon
                                && army.Id < best.ArmyId))
                        {
                            best = new EquipmentAssignment
                            {
                                EquipmentCard = card,
                                Target = unit,
                                Benefit = score,
                                ArmyId = army.Id,
                            };
                        }
                    }
                }
            }
            return best;
        }

        private static float EquipmentBenefit(CardDefinition equipment, UnitData unit)
        {
            EquipmentGrant g = equipment?.equipment;
            if (g == null || unit == null)
                return 0f;

            float score = 0f;
            if (g.addAbilities != null)
                score += 2.5f * g.addAbilities.Count(a => !string.IsNullOrEmpty(a) && !unit.Abilities.Contains(a));
            if (g.removeAbilities != null)
                score -= 3f * g.removeAbilities.Count(a => !string.IsNullOrEmpty(a) && unit.Abilities.Contains(a));
            if (g.clearAbilityFamilies != null)
                score -= 2f * g.clearAbilityFamilies.Count(f => f != AbilityFamily.None);

            if (g.statChanges != null)
                foreach (EquipmentStatChange c in g.statChanges)
                {
                    if (c == null) continue;
                    int before = StatValue(unit, c.stat);
                    int after = c.isOverride ? Mathf.Max(EquipmentSystem.FloorFor(c.stat), c.amount)
                        : Mathf.Max(EquipmentSystem.FloorFor(c.stat), before + c.amount);
                    int delta = after - before;
                    float weight = StatWeight(c.stat);
                    score += delta * weight;
                }
            return score;
        }

        private static int StatValue(UnitData u, EquipmentStat s)
        {
            switch (s)
            {
                case EquipmentStat.Attack: return u.Attack;
                case EquipmentStat.Defense: return u.Defense;
                case EquipmentStat.Resistance: return u.Resistance;
                case EquipmentStat.Range: return u.Range;
                case EquipmentStat.Initiative: return u.Initiative;
                case EquipmentStat.ActivationApCost: return u.ActivationApCost;
                case EquipmentStat.CommandRating: return u.CommandRating;
                case EquipmentStat.HitPoints: return u.HitPointsMax;
                case EquipmentStat.MoveMax: return u.MoveMax;
                case EquipmentStat.Fate: return u.FateMax;
                default: return 0;
            }
        }

        private static float StatWeight(EquipmentStat s)
        {
            switch (s)
            {
                case EquipmentStat.Attack: return 4f;
                case EquipmentStat.Defense: return 3f;
                case EquipmentStat.HitPoints: return 2f;
                case EquipmentStat.CommandRating: return 2f;
                case EquipmentStat.MoveMax: return 1.5f;
                case EquipmentStat.Range: return 1.5f;
                case EquipmentStat.ActivationApCost: return -2f; // lower activation cost is better
                default: return 1f;
            }
        }

        private static bool ExecuteEquipment(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, EquipmentAssignment pick)
        {
            V2PhaseActivity activity = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Main);
            activity.EquipmentAssignmentAttempts++;
            string equipmentName = pick.EquipmentCard?.Definition?.displayName ?? "?";
            string targetName = pick.Target?.Name ?? "?";
            if (!EquipmentSystem.TryAttach(pick.EquipmentCard, pick.Target, root, out string why))
            {
                AiDebugLog.Write($"[AI][V2] maintenance equipment — FAIL {equipmentName} -> {targetName}: {why}");
                return false;
            }

            hand.Hand.Remove(pick.EquipmentCard);
            activity.EquipmentAssignmentsSucceeded++;
            activity.CardsPlayed++;
            AiDebugLog.Write($"[AI][V2] maintenance equipment — attached {equipmentName} -> {targetName} "
                + $"in army #{pick.ArmyId} (utility {pick.Benefit:0.00})");
            return true;
        }

        // ---------------------------------------------------------------- Generation ----

        private static GenerationTurnState GenerationState(PlayerSetupData player, int turn)
        {
            if (!GenerationByPlayer.TryGetValue(player, out GenerationTurnState s))
                GenerationByPlayer[player] = s = new GenerationTurnState();
            if (s.Turn != turn)
            {
                s.Turn = turn;
                s.Attempts = 0;
                s.TriedCardKeys.Clear();
            }
            return s;
        }

        private static GenerationStep FindGeneration(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx)
        {
            if (!hand.HasFreeSlot)
                return null;
            GenerationTurnState s = GenerationState(player, ctx.TurnNumber);
            if (s.Attempts >= MaxStandaloneGenerationAttemptsPerTurn)
                return null;

            List<GenerationStep> options = GenerationSource.Enumerate(
                player, root, ctx, hand, claimedUseKeys: null, triedCardKeys: s.TriedCardKeys);
            return options
                .Where(g => g?.CardDef != null
                    && (g.CardDef.cardType == CardType.Unit || g.CardDef.cardType == CardType.Hero
                        || g.CardDef.cardType == CardType.Equipment))
                .Select(g => new { Step = g, Score = GenerationUtility(player, root, g) })
                .Where(x => x.Score > 0.01f)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Step.CardDef.displayName, System.StringComparer.Ordinal)
                .Select(x => x.Step)
                .FirstOrDefault();
        }

        private static float GenerationUtility(PlayerSetupData player, PlayerRoot root, GenerationStep g)
        {
            CardDefinition d = g.CardDef;
            float value;
            if (d.cardType == CardType.Equipment)
            {
                bool usefulTarget = ArmyRegistry.AllForOwner(player)
                    .Where(a => a != null && !a.IsPrison && !a.IsAirfield)
                    .SelectMany(a => a.Members)
                    .Any(u => u != null && !u.IsAviation
                        && EquipmentSystem.CanAttach(d, u, root, out _)
                        && EquipmentBenefit(d, u) > 0.01f);
                if (!usefulTarget)
                    return 0f;
                value = 18f;
            }
            else
            {
                value = AiPower.ToPowerUnit(d).BasePower;
                if (d.cardType == CardType.Hero)
                    value += 8f;
            }

            // A Challenge has no AP cost, but resources are real and are never refunded on a
            // failed roll. Success chance therefore scales the benefit instead of being ignored.
            return value * Mathf.Clamp01(g.SuccessChance);
        }

        private static bool ExecuteGeneration(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, GenerationStep g)
        {
            GenerationTurnState s = GenerationState(player, ctx.TurnNumber);
            s.Attempts++;
            s.TriedCardKeys.Add(g.CardKey);

            V2PhaseActivity activity = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Main);
            activity.GeneratedCardAttempts++;

            if (!ResearchProductionSystem.IsEligible(player, g.FacilityHex, g.Mode, out string why)
                || !ResearchProductionSystem.ActorStillQualifies(player, g.Hero, g.FacilityHex, g.Mode)
                || !hand.HasFreeSlot || !ResearchProductionSystem.CanAffordCard(root, g.CardDef))
            {
                AiDebugLog.Write($"[AI][V2] maintenance generation — FAIL {g.Mode} {g.CardDef.displayName}: "
                    + (why ?? "source/card no longer eligible"));
                return false;
            }

            bool wasHidden = g.Hero != null && g.Hero.IsHidden;
            int h0 = root.GetResource(ResourceType.Human), e0 = root.GetResource(ResourceType.Energy),
                m0 = root.GetResource(ResourceType.Materials), t0 = root.GetResource(ResourceType.Tech);

            ResearchProductionSystem.ApplyResearchReveal(g.Mode, g.Hero);
            ResearchProductionSystem.PayCardCost(root, g.CardDef);
            ResearchProductionSystem.ChallengeOutcome outcome = ResearchProductionSystem.RollChallenge(g.Hero, g.CardDef);
            if (!outcome.Success)
            {
                bool resourcesSpent = h0 != root.GetResource(ResourceType.Human)
                    || e0 != root.GetResource(ResourceType.Energy)
                    || m0 != root.GetResource(ResourceType.Materials)
                    || t0 != root.GetResource(ResourceType.Tech);
                AiDebugLog.Write($"[AI][V2] maintenance generation — {g.Mode} {g.CardDef.displayName} "
                    + $"FAILED challenge {outcome.Successes}/{outcome.Required}; resources remain spent");
                return resourcesSpent || (g.Mode == ResearchProductionMode.Research && wasHidden);
            }

            CardData generated = ResearchProductionSystem.MintCard(g.CardDef);
            hand.Hand.Add(generated);
            activity.GeneratedCardsSucceeded++;
            AiDebugLog.Write($"[AI][V2] maintenance generation — {g.Mode} produced \"{g.CardDef.displayName}\" "
                + $"@({g.FacilityHex.Q},{g.FacilityHex.R}) chance {g.SuccessChance:0.00}");
            return true;
        }

        private static bool FitsReservedResources(PlayerRoot root, PlayerSetupData player, ResourceCost cost)
        {
            if (cost == null)
                return true;
            foreach (ResourceType t in ResourceBundle.All)
            {
                int need = cost.Get(t);
                if (need > 0 && AiResourceReservation.Available(root, player, t) < need)
                    return false;
            }
            return true;
        }

        public static void Clear() => GenerationByPlayer.Clear();
    }
}
