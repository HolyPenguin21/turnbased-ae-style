using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC MANAGER  (Strategy V2 — centralized card play + capability preparation)
    // ===========================================================================================
    //  NOT a DesireAxis, NO radar slice. The single owner of V2 strategic Unit/Hero/Recce
    //  card-play decisions. Strategic axes only expose AxisDemand[] — they never choose a card,
    //  create an army, pick a placement, or touch the hand. Two phases:
    //
    //    Phase A — FulfillDemands (BEFORE mission planning). For each demand, in value order:
    //              find the most USEFUL affordable (card, placement, shell/create) plan, check
    //              the requesting axis's AP entitlement AND real AP/Human/Energy/Materials/Tech,
    //              preflight the whole sequence, play it via CardPlayExecutor, debit the
    //              requesting axis on the AxisBudgetLedger. Bounded by
    //              maxDemandFulfillmentActionsPerTurn.
    //
    //    Phase B — UseSurplus (AFTER mission execution). Bounded greedy loop over GENUINELY
    //              remaining real AP/resources: play the highest FutureUtility legal card while
    //              it clears surplusUtilityThreshold and every configured reserve still holds,
    //              then optionally cycle the hand (CardDrawExecutor). No look-ahead simulation.
    //
    //  Reusable-army policy: an empty ArmyData is a paid, reusable asset. A plan prefers a shell
    //  already on the deployment hex, then any create; a failed deploy after CreateArmy keeps the
    //  shell (StateChanged stays true so the operational refresh still runs).
    // ===========================================================================================
    public sealed class StrategicPhaseResult
    {
        public bool StateChanged;
        public int CardsPlayed;
        public readonly Dictionary<DesireAxis, float> ApDebited = new Dictionary<DesireAxis, float>();

        public void AddDebit(DesireAxis a, float ap)
        {
            ApDebited.TryGetValue(a, out float cur);
            ApDebited[a] = cur + ap;
        }
    }

    public static class StrategicManager
    {
        // ----------------------------------------------------------------- PHASE A ----
        public static StrategicPhaseResult FulfillDemands(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisBudgetLedger ledger,
            IReadOnlyList<AxisDemand> demands, ActorCommitments commitments)
        {
            var result = new StrategicPhaseResult();
            if (demands == null || demands.Count == 0 || player == null || root == null || hand == null || ledger == null)
                return result;

            int actions = 0;
            foreach (AxisDemand demand in demands
                .OrderByDescending(d => d.Value)
                .ThenBy(d => (int)d.RequestingAxis))
            {
                float deficit = demand.DesiredAmount;
                while (deficit > 0f && actions < AiConfigV2.maxDemandFulfillmentActionsPerTurn)
                {
                    CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
                    CardPlayPlan? plan = CardCandidateEvaluator.BestForDemand(
                        snap, player, root, hand, ctx, demand, inv, commitments);
                    if (plan == null)
                    {
                        AiDebugLog.Write($"[AI][V2]   strat.A — {demand}: no useful affordable card");
                        break;
                    }

                    float axisBudget = ledger.Balance(demand.RequestingAxis);
                    if (plan.Value.TotalApCost > axisBudget + AiConfigV2.allocatorSliceEpsilon)
                    {
                        AiDebugLog.Write($"[AI][V2]   strat.A — {demand}: card needs {plan.Value.TotalApCost} AP, "
                            + $"{DesireAxes.Abbrev(demand.RequestingAxis)} entitlement {F(axisBudget)}");
                        break;
                    }

                    CardPlayResult play = CardPlayExecutor.Play(player, root, hand, ctx, plan.Value);
                    if (play.StateChanged)
                        result.StateChanged = true;
                    if (play.ApSpent > 0f)
                    {
                        ledger.Debit(demand.RequestingAxis, play.ApSpent);
                        result.AddDebit(demand.RequestingAxis, play.ApSpent);
                    }

                    if (!play.Deployed)
                    {
                        AiDebugLog.Write($"[AI][V2]   strat.A — {demand}: play failed ({play.FailReason}); "
                            + $"{(play.ArmyCreated ? "kept the empty army as a reusable asset" : "no change")}");
                        break;
                    }

                    actions++;
                    result.CardsPlayed++;
                    deficit -= 1f;
                    AiDebugLog.Write($"[AI][V2]   strat.A — {demand}: played \"{plan.Value.Card.Definition?.displayName}\" "
                        + $"@{plan.Value.DeploymentHex.Q},{plan.Value.DeploymentHex.R} "
                        + $"(ap {F(play.ApSpent)} -> {DesireAxes.Abbrev(demand.RequestingAxis)}, "
                        + $"{(plan.Value.RequiresCreateArmy ? "new army" : "reused shell")})");
                }
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.A — {result.CardsPlayed} card(s), ledger now "
                    + ledger.DebugLine());
            return result;
        }

        // ----------------------------------------------------------------- PHASE B ----
        public static StrategicPhaseResult UseSurplus(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments)
        {
            var result = new StrategicPhaseResult();
            if (player == null || root == null || hand == null || ctx == null)
                return result;

            for (int i = 0; i < AiConfigV2.maxSurplusActionsPerTurn; i++)
            {
                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
                (CardPlayPlan plan, float utility)? pick = CardCandidateEvaluator.BestSurplus(
                    snap, player, root, hand, ctx, inv, commitments);
                if (pick == null)
                    break;
                if (pick.Value.utility < AiConfigV2.surplusUtilityThreshold)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — best utility {F(pick.Value.utility)} < threshold "
                        + $"{F(AiConfigV2.surplusUtilityThreshold)}, stop");
                    break;
                }
                if (!ReservesOkAfter(root, pick.Value.plan))
                {
                    AiDebugLog.Write("[AI][V2]   strat.B — resource/AP reserves would be violated, stop");
                    break;
                }

                CardPlayResult play = CardPlayExecutor.Play(player, root, hand, ctx, pick.Value.plan);
                if (play.StateChanged)
                    result.StateChanged = true;
                if (!play.Deployed)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — play failed ({play.FailReason}); stop");
                    break;
                }
                result.CardsPlayed++;
                AiDebugLog.Write($"[AI][V2]   strat.B — played \"{pick.Value.plan.Card.Definition?.displayName}\" "
                    + $"util {F(pick.Value.utility)} (ap {F(play.ApSpent)}, "
                    + $"{(pick.Value.plan.RequiresCreateArmy ? "new army" : "reused shell")})");

                // Optional cycle — only if a slot is now free and cycling still leaves the AP
                // reserve (housekeeping + surplus) intact.
                if (AiConfigV2.surplusAllowDraw && hand.HasFreeSlot
                    && root.ActionPoints - ctx.DrawApCost
                        >= AiConfigV2.housekeepingApReserve + AiConfigV2.surplusApReserve
                    && CardDrawExecutor.TryCycle(root, hand, ctx))
                {
                    result.StateChanged = true;
                }
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.B — {result.CardsPlayed} surplus card(s) played");
            return result;
        }

        private static bool ReservesOkAfter(PlayerRoot root, CardPlayPlan plan)
        {
            float apAfter = root.ActionPoints - plan.TotalApCost;
            if (apAfter < AiConfigV2.housekeepingApReserve + AiConfigV2.surplusApReserve)
                return false;

            ResourceCost cost = AiCardCost.PlayResources(plan.Card);
            if (cost == null)
                return true;
            return root.GetResource(ResourceType.Human) - cost.human >= AiConfigV2.surplusHumanReserve
                && root.GetResource(ResourceType.Energy) - cost.energy >= AiConfigV2.surplusEnergyReserve
                && root.GetResource(ResourceType.Materials) - cost.materials >= AiConfigV2.surplusMaterialsReserve
                && root.GetResource(ResourceType.Tech) - cost.tech >= AiConfigV2.surplusTechReserve;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // ===========================================================================================
    //  PLACEMENT SELECTOR — every legal (hex, shell/create) option for a card, as COMPLETE
    //  CardPlayPlan alternatives. A shell is used only at its own hex; "create here" is always a
    //  separate alternative so the caller can weigh reuse-at-A vs. create-at-B as whole plans.
    // ===========================================================================================
    internal static class PlacementSelector
    {
        public static List<CardPlayPlan> BuildPlans(WorldSnapshot snap, PlayerSetupData player,
            CardData card, ActorCommitments commitments)
        {
            var plans = new List<CardPlayPlan>();
            CardDefinition def = card?.Definition;
            if (def == null || snap?.Self?.BaseHexes == null)
                return plans;

            foreach (HexCoord hex in snap.Self.BaseHexes)
            {
                if (!PlacementRules.HasRequiredBuilding(player, hex, def))
                    continue;

                ArmyData shell = ReusableArmySelector.FindReusableAt(player, hex, commitments);
                if (shell != null)
                    plans.Add(new CardPlayPlan(card, hex, shell));      // priority 1 — reuse at this hex
                plans.Add(new CardPlayPlan(card, hex, null));           // alternative — create here
            }
            return plans;
        }
    }

    // ===========================================================================================
    //  CARD CANDIDATE EVALUATOR — matches available cards against a demand (Phase A) or against
    //  future utility (Phase B). Never "first affordable card": every candidate is scored on
    //  capability match, preferred-trait match, target/location fit, AP + resource cost, existing
    //  supply and (Phase B) hand pressure / oversupply.
    // ===========================================================================================
    internal static class CardCandidateEvaluator
    {
        // ---- Phase A: best plan to satisfy one demand ----
        public static CardPlayPlan? BestForDemand(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            CapabilityInventory inv, ActorCommitments commitments)
        {
            CardPlayPlan? best = null;
            float bestScore = float.NegativeInfinity;

            foreach (CardData card in hand.Hand.ToList())
            {
                if (!MatchesCapability(card, demand.Capability))
                    continue;
                float traitBonus = TraitBonus(card, demand.PreferredTraits);

                foreach (CardPlayPlan plan in PlacementSelector.BuildPlans(snap, player, card, commitments))
                {
                    if (!CardPlayExecutor.Preflight(player, root, hand, ctx, plan, out _))
                        continue;

                    float fit = TargetFit(plan.DeploymentHex, demand.TargetHex);
                    float costFactor = 1f + AiConfigV2.stratCardApCostWeight * plan.TotalApCost;
                    float score = (1f + traitBonus) * (0.5f + 0.5f * fit) / Mathf.Max(0.0001f, costFactor);
                    if (!plan.RequiresCreateArmy)
                        score += AiConfigV2.stratReuseShellBonus;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = plan;
                    }
                }
            }
            return best;
        }

        // ---- Phase B: highest future-utility legal card play, or null ----
        public static (CardPlayPlan plan, float utility)? BestSurplus(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            CapabilityInventory inv, ActorCommitments commitments)
        {
            (CardPlayPlan plan, float utility)? best = null;

            foreach (CardData card in hand.Hand.ToList())
            {
                CardDefinition d = card.Definition;
                if (d == null || d.isAviation)
                    continue;
                bool recce = AbilityParams.AbilitiesHaveAnyRecce(d.grantedAbilities);
                bool hero = d.cardType == CardType.Hero;
                if (!recce && d.cardType != CardType.Unit && !hero)
                    continue;

                float scarcity = SurplusScarcity(inv, recce, hero);
                float versatility = hero ? AiConfigV2.surplusHeroVersatility : AiConfigV2.surplusUnitVersatility;
                float traits = AbilityParams.AbilitiesHaveAnyStealth(d.grantedAbilities)
                    ? AiConfigV2.stratTraitMatchBonus : 0f;
                float handPressure = hand.HasFreeSlot ? 0f : AiConfigV2.surplusHandPressureBonus;
                float oversupply = recce && inv.ReadyScouts >= AiConfigV2.surplusScoutOversupplyAt
                    ? AiConfigV2.surplusOversupplyPenalty : 0f;
                float resCost = ResourceOpportunityCost(card);

                foreach (CardPlayPlan plan in PlacementSelector.BuildPlans(snap, player, card, commitments))
                {
                    if (!CardPlayExecutor.Preflight(player, root, hand, ctx, plan, out _))
                        continue;

                    float util = scarcity + versatility + traits + handPressure
                        - AiConfigV2.surplusApCostWeight * plan.TotalApCost
                        - resCost - oversupply;
                    if (!plan.RequiresCreateArmy)
                        util += AiConfigV2.stratReuseShellBonus;

                    if (best == null || util > best.Value.utility)
                        best = (plan, util);
                }
            }
            return best;
        }

        // ---------------------------------------------------------------- helpers ----
        private static bool MatchesCapability(CardData card, CapabilityKind kind)
        {
            CardDefinition d = card?.Definition;
            if (d == null || d.isAviation)
                return false;
            bool recce = AbilityParams.AbilitiesHaveAnyRecce(d.grantedAbilities);
            switch (kind)
            {
                case CapabilityKind.ScoutCapability:
                    return recce;
                case CapabilityKind.Hero:
                    return d.cardType == CardType.Hero && !recce;
                case CapabilityKind.FieldCombatPower:
                case CapabilityKind.GarrisonCombatPower:
                    return !recce && (d.cardType == CardType.Unit || d.cardType == CardType.Hero);
                default:
                    return false;
            }
        }

        private static float TraitBonus(CardData card, TraitPreference pref)
        {
            if (pref == TraitPreference.None)
                return 0f;
            CardDefinition d = card?.Definition;
            if (d == null)
                return 0f;
            float b = 0f;
            if ((pref & TraitPreference.Stealth) != 0 && AbilityParams.AbilitiesHaveAnyStealth(d.grantedAbilities))
                b += AiConfigV2.stratTraitMatchBonus;
            // AntiArmour / Ranged / Melee: no snapshot-safe classifier yet — added when a demand needs it.
            return b;
        }

        private static float TargetFit(HexCoord deployHex, HexCoord? target)
        {
            if (!target.HasValue)
                return 0.5f;
            int d = HexGridMath.Distance(deployHex, target.Value);
            return Mathf.Clamp01(1f - d / Mathf.Max(1f, (float)AiConfigV2.stratTargetFitRange));
        }

        private static float SurplusScarcity(CapabilityInventory inv, bool recce, bool hero)
        {
            if (recce)
                return inv.ReadyScouts + inv.CommittedScouts <= 0 ? AiConfigV2.surplusScarcityHigh
                    : inv.ReadyScouts <= 1 ? AiConfigV2.surplusScarcityMed
                    : AiConfigV2.surplusScarcityLow;
            if (hero)
                return inv.AvailableHeroes <= 0 ? AiConfigV2.surplusScarcityMed : AiConfigV2.surplusScarcityLow;
            return AiConfigV2.surplusScarcityLow;
        }

        private static float ResourceOpportunityCost(CardData card)
        {
            ResourceCost c = AiCardCost.PlayResources(card);
            if (c == null)
                return 0f;
            return AiConfigV2.surplusResourceCostWeight * (c.human + c.energy + c.materials + c.tech);
        }
    }
}
