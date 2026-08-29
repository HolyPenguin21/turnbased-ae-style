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
    //              enumerate every legal placement -> reject the infeasible (RequiredTraits, axis
    //              entitlement AND real-AP room for the demand's MinimumFollowupAp + housekeeping
    //              reserve, resource cost) -> rank the survivors by usefulness -> play the best
    //              via CardPlayExecutor -> debit the requesting axis by the REAL AP spent.
    //              Bounded by maxDemandFulfillmentActionsPerTurn. The follow-up AP is RESERVED,
    //              not spent — Phase A must not create a capability the mission allocator can no
    //              longer fund this turn.
    //
    //    Phase B — UseSurplus (AFTER mission execution + operational refresh). Bounded greedy over
    //              GENUINELY remaining real AP/resources: enumerate -> reject candidates that
    //              would breach a configured reserve -> rank survivors by FutureUtility -> play
    //              the best while it clears surplusUtilityThreshold. The operational snapshot is
    //              refreshed after every successful play so scarcity is recomputed honestly.
    //
    //  Reusable-army policy: an empty ArmyData is a paid, reusable asset. For a solo (Recce /
    //  ScoutCapability) card only a shell-at-hex or a fresh army is legal; for a plain Unit/Hero
    //  an existing suitable army / garrison with room is preferred over paying CreateArmy AP
    //  (the "one unit per army" pathology AiArmyRoles already documents).
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

            // ACCUMULATIVE follow-up reservation, per axis, across every demand this phase. Each
            // prepared executor needs its own follow-up AP left in the axis entitlement / real AP;
            // reserving only for "the next one" starves the mission allocator when DesiredAmount > 1.
            var reservedFollowupByAxis = new Dictionary<DesireAxis, float>();

            int actions = 0;
            foreach (AxisDemand demand in demands
                .OrderByDescending(d => d.Value)
                .ThenBy(d => (int)d.RequestingAxis))
            {
                float deficit = demand.DesiredAmount;
                while (deficit > 0f && actions < AiConfigV2.maxDemandFulfillmentActionsPerTurn)
                {
                    reservedFollowupByAxis.TryGetValue(demand.RequestingAxis, out float reserved);
                    (CardPlayPlan plan, float followupAp)? pick = CardCandidateEvaluator.BestForDemand(
                        snap, player, root, hand, ctx, demand, ledger, commitments, reserved);
                    if (pick == null)
                    {
                        AiDebugLog.Write($"[AI][V2]   strat.A — {demand}: no feasible useful card "
                            + $"({DesireAxes.Abbrev(demand.RequestingAxis)} entitlement "
                            + $"{F(ledger.Balance(demand.RequestingAxis))}, followup reserved {F(reserved)})");
                        break;
                    }

                    CardPlayResult play = CardPlayExecutor.Play(player, root, hand, ctx, pick.Value.plan);
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

                    reservedFollowupByAxis[demand.RequestingAxis] = reserved + pick.Value.followupAp;
                    actions++;
                    result.CardsPlayed++;
                    deficit -= 1f;
                    AiDebugLog.Write($"[AI][V2]   strat.A — {demand}: played \"{pick.Value.plan.Card.Definition?.displayName}\" "
                        + $"@{pick.Value.plan.DeploymentHex.Q},{pick.Value.plan.DeploymentHex.R} "
                        + $"(ap {F(play.ApSpent)} -> {DesireAxes.Abbrev(demand.RequestingAxis)}, {pick.Value.plan.Kind}, "
                        + $"followup {F(pick.Value.followupAp)}ap reserved)");
                }
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.A — {result.CardsPlayed} card(s), ledger now " + ledger.DebugLine());
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
                    AiDebugLog.Write($"[AI][V2]   strat.B — best feasible utility {F(pick.Value.utility)} < threshold "
                        + $"{F(AiConfigV2.surplusUtilityThreshold)}, stop");
                    break;
                }

                bool handWasFull = !hand.HasFreeSlot;
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
                    + $"util {F(pick.Value.utility)} (ap {F(play.ApSpent)}, {pick.Value.plan.Kind})");

                // Cycle ONLY when the play actually relieved hand pressure and the AP reserve
                // (housekeeping + surplus) still holds after the draw.
                if (AiConfigV2.surplusAllowDraw && handWasFull && hand.HasFreeSlot
                    && root.ActionPoints - ctx.DrawApCost
                        >= AiConfigV2.housekeepingApReserve + AiConfigV2.surplusApReserve
                    && CardDrawExecutor.TryCycle(root, hand, ctx))
                {
                    result.StateChanged = true;
                }

                // Refresh own operational state so the next iteration's CapabilityInventory sees
                // the unit/army just deployed (scarcity / oversupply must not re-fire stale).
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.B — {result.CardsPlayed} surplus card(s) played");
            return result;
        }

        internal static bool ReservesOkAfter(PlayerRoot root, CardPlayPlan plan)
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
    //  PLACEMENT SELECTOR — every legal CardPlayPlan for a card. A solo (Recce) card only ever
    //  gets a shell-at-hex or a fresh army; a plain Unit/Hero also gets an existing suitable army
    //  / garrison with room at that hex (preferred over paying CreateArmy AP). A shell is used
    //  only at its own hex; "create here" is always a separate alternative.
    // ===========================================================================================
    internal static class PlacementSelector
    {
        public static List<CardPlayPlan> BuildPlans(WorldSnapshot snap, PlayerSetupData player,
            CardData card, ActorCommitments commitments, bool soloOnly)
        {
            var plans = new List<CardPlayPlan>();
            CardDefinition def = card?.Definition;
            if (def == null || snap?.Self?.BaseHexes == null || player == null)
                return plans;

            bool isUnit = def.cardType == CardType.Unit;
            List<ArmyData> own = ArmyRegistry.AllForOwner(player).Where(a => a != null).ToList();

            foreach (HexCoord hex in snap.Self.BaseHexes)
            {
                if (!PlacementRules.HasRequiredBuilding(player, hex, def))
                    continue;

                ArmyData shell = ReusableArmySelector.FindReusableAt(player, hex, commitments);
                if (shell != null)
                    plans.Add(CardPlayPlan.Into(card, hex, DeploymentKind.ReusableShell, shell));
                plans.Add(CardPlayPlan.NewArmyAt(card, hex));

                if (soloOnly)
                    continue;

                foreach (ArmyData a in own)
                {
                    if (!a.Hex.Equals(hex) || a.IsPrison)
                        continue;
                    if (a.IsGarrison)
                    {
                        // stricter than HasRoom — keep garrisonReservedSlots free for ops / reorg.
                        if (PlacementRules.CanDepositIntoGarrison(a))
                            plans.Add(CardPlayPlan.Into(card, hex, DeploymentKind.Garrison, a));
                        continue;
                    }
                    if (!a.HasRoom || a.Members.Count == 0)
                        continue; // an empty non-garrison army is a shell — handled above
                    bool ok = AiArmyRoles.IsPlainReserveArmy(a)
                        || (isUnit && AiArmyRoles.IsHeroLedCombatArmy(a));
                    if (ok)
                        plans.Add(CardPlayPlan.Into(card, hex, DeploymentKind.ExistingArmy, a));
                }
            }
            return plans;
        }
    }

    // ===========================================================================================
    //  CARD CANDIDATE EVALUATOR — matches available cards against a demand (Phase A) or against
    //  future utility (Phase B). ENUMERATE -> REJECT INFEASIBLE -> RANK -> CHOOSE (never rank
    //  everything and stop because the top pick is infeasible). Never "first affordable card":
    //  survivors are scored on capability match, preferred-trait match, target/location fit,
    //  AP + resource cost, existing supply and (Phase B) hand pressure / oversupply.
    // ===========================================================================================
    internal static class CardCandidateEvaluator
    {
        // ---- Phase A: best FEASIBLE (plan, followupAp) to satisfy one demand, or null ----
        //  reservedFollowupAp = follow-up AP already reserved for executors this demand's axis
        //  prepared earlier this phase — the check is cumulative, not "room for one more".
        public static (CardPlayPlan plan, float followupAp)? BestForDemand(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            AxisBudgetLedger ledger, ActorCommitments commitments, float reservedFollowupAp)
        {
            float eps = AiConfigV2.allocatorSliceEpsilon;
            float axisBudget = ledger.Balance(demand.RequestingAxis);
            bool soloOnly = demand.Capability == CapabilityKind.ScoutCapability;
            int stealthPortion = (demand.RequiredTraits & TraitPreference.Stealth) != 0
                ? AiConfigV2.scoutOptionalStealthAp : 0;

            (CardPlayPlan plan, float followupAp)? best = null;
            float bestScore = float.NegativeInfinity;

            foreach (CardData card in hand.Hand.ToList())
            {
                if (!MatchesCapability(card, demand.Capability))
                    continue;
                if (!MatchesRequiredTraits(card, demand.RequiredTraits))   // HARD constraint
                    continue;
                float traitBonus = TraitBonus(card, demand.PreferredTraits);

                // Real follow-up cost of THIS specific executor: the deployed unit's own
                // activation AP + the demand's action surcharge (stealth) + the demand's FIXED
                // actor-independent overhead. Not a global notional, and not floored by one — a
                // 0-activation unit on a no-surcharge job reserves 0.
                float followupAp = (card.Definition?.activationApCost ?? AiConfigV2.scoutNotionalActivationAp)
                    + stealthPortion + demand.MinimumFollowupAp;

                foreach (CardPlayPlan plan in PlacementSelector.BuildPlans(snap, player, card, commitments, soloOnly))
                {
                    if (!CardPlayExecutor.Preflight(player, root, hand, ctx, plan, out _))
                        continue;

                    // Feasibility BEFORE ranking: preparation cost + ALL follow-up AP (already
                    // reserved + this one) must survive, in the axis entitlement AND in real AP on
                    // top of the housekeeping reserve. Reserved, not spent.
                    float needWithFollowup = plan.TotalApCost + reservedFollowupAp + followupAp;
                    if (needWithFollowup > axisBudget + eps)
                        continue;
                    if (root.ActionPoints - needWithFollowup - AiConfigV2.housekeepingApReserve < -eps)
                        continue;

                    float fit = TargetFit(plan.DeploymentHex, demand.TargetHex);
                    float costFactor = 1f + AiConfigV2.stratCardApCostWeight * plan.TotalApCost;
                    float score = (1f + traitBonus) * (0.5f + 0.5f * fit) / Mathf.Max(0.0001f, costFactor);
                    score += PlacementBonus(plan.Kind);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = (plan, followupAp);
                    }
                }
            }
            return best;
        }

        // ---- Phase B: highest FutureUtility plan among reserve-safe candidates, or null ----
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
                float oversupply = recce
                    && inv.ReadyScouts + inv.ReserveScouts >= AiConfigV2.surplusScoutOversupplyAt
                    ? AiConfigV2.surplusOversupplyPenalty : 0f;
                float resCost = ResourceOpportunityCost(card);

                foreach (CardPlayPlan plan in PlacementSelector.BuildPlans(snap, player, card, commitments, recce))
                {
                    if (!CardPlayExecutor.Preflight(player, root, hand, ctx, plan, out _))
                        continue;
                    if (!StrategicManager.ReservesOkAfter(root, plan))   // reject BEFORE ranking
                        continue;

                    float util = scarcity + versatility + traits + handPressure
                        - AiConfigV2.surplusApCostWeight * plan.TotalApCost
                        - resCost - oversupply
                        + PlacementBonus(plan.Kind);

                    if (best == null || util > best.Value.utility)
                        best = (plan, util);
                }
            }
            return best;
        }

        // ---------------------------------------------------------------- helpers ----
        // Graded placement preference (V1 principle): fill an existing suitable army / the
        // garrison before founding a fresh one. Garrison > existing army > reusable shell > new.
        private static float PlacementBonus(DeploymentKind k)
        {
            switch (k)
            {
                case DeploymentKind.Garrison:      return AiConfigV2.stratPlacementGarrisonBonus;
                case DeploymentKind.ExistingArmy:  return AiConfigV2.stratPlacementExistingArmyBonus;
                case DeploymentKind.ReusableShell: return AiConfigV2.stratPlacementReusableShellBonus;
                default:                           return 0f; // NewArmy
            }
        }

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

        // HARD trait constraint. A stealth-Required demand needs a card whose granted abilities
        // actually carry stealth — read from the card, never its name or hand order.
        private static bool MatchesRequiredTraits(CardData card, TraitPreference required)
        {
            if (required == TraitPreference.None)
                return true;
            CardDefinition d = card?.Definition;
            if (d == null)
                return false;
            if ((required & TraitPreference.Stealth) != 0 && !AbilityParams.AbilitiesHaveAnyStealth(d.grantedAbilities))
                return false;
            // AntiArmour / Ranged / Melee: no snapshot-safe classifier yet — a demand that sets
            // one as Required will simply match nothing until that classifier lands.
            if ((required & (TraitPreference.AntiArmour | TraitPreference.Ranged | TraitPreference.Melee)) != 0)
                return false;
            return true;
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
            {
                // ReserveScouts is a real next-turn capability — a scout used up THIS turn (now a
                // reserve) does not mean "no scouts".
                if (inv.TotalScouts <= 0)
                    return AiConfigV2.surplusScarcityHigh;
                if (inv.ReadyScouts + inv.ReserveScouts <= 1)
                    return AiConfigV2.surplusScarcityMed;
                return AiConfigV2.surplusScarcityLow;
            }
            if (hero)
                return inv.AvailableHeroes <= 0 ? AiConfigV2.surplusScarcityMed : AiConfigV2.surplusScarcityLow;
            return AiConfigV2.surplusScarcityLow;
        }

        private static float ResourceOpportunityCost(CardData card)
        {
            ResourceCost c = AiCardCost.PlayResources(card);
            if (c == null)
                return 0f;
            // First pass: flat sum. TODO extend with per-resource scarcity / expected future demand.
            return AiConfigV2.surplusResourceCostWeight * (c.human + c.energy + c.materials + c.tech);
        }
    }
}
