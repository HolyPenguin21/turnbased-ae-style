using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  INFRASTRUCTURE FULFILLMENT  (Strategy V2 — ECO / DEV demand consumer for buildings)
    // ===========================================================================================
    //  StrategicManager Phase A calls this for EconomicInfrastructure / DevelopmentInfrastructure
    //  demands, BEFORE the Unit/Hero MaterializationCandidateBuilder loop.
    //
    //  ADMISSION ORDER (spec §1): build a candidate WITHOUT touching game state -> compute its
    //  complete cost -> check the requesting axis's AxisBudgetLedger entitlement -> check live
    //  gameplay affordability -> ONLY THEN run the authoritative BuildingPlayExecutor transaction
    //  -> the caller debits the actual confirmed AP. A budget or affordability shortfall means the
    //  demand stays OPEN (nothing played, nothing spent) — Debit() is never used as after-the-fact
    //  permission.
    //
    //  CAPABILITY IDENTITY (spec §3, §4). "Built" means the demanded capability really exists:
    //    · ECO(resourceType) -> an extraction facility on a KNOWN, unbuilt, SAME-type resource
    //      site with a hero present. A generic Base somewhere else is NOT a valid fulfillment.
    //      If no such action is possible now the demand is left deferred.
    //    · DEV -> a CardType.Facility carrying Research/Production, placed into an owned Base slot
    //      (that is what WorldAnalysis.HasDevFacility actually checks — a filled slot, not a
    //      building-level ability, so a plain Base card would NOT satisfy it).
    //
    //  GAME-RULE PRECONDITION: founding a Base and building an extraction facility both need one
    //  of the player's own HERO-LED armies on the target hex. V2 has no economy/development mover
    //  mission yet, so this fires only when a hero is already in position.
    // ===========================================================================================
    internal sealed class InfraFulfillResult
    {
        public bool Built;
        public float ApSpent;
        public bool StateChanged;
        public string Detail;

        public static InfraFulfillResult No(string why) => new InfraFulfillResult { Detail = why };
    }

    internal static class InfrastructureFulfillment
    {
        public static bool Handles(CapabilityKind k) =>
            k == CapabilityKind.EconomicInfrastructure || k == CapabilityKind.DevelopmentInfrastructure;

        // One planned build: the authoritative action to run plus the cost to admit it against.
        private sealed class InfraCandidate
        {
            public float ApCost;
            public ResourceCost ResCost;
            public string Explain;
            public System.Func<BuildingPlayResult> Execute;
        }

        public static InfraFulfillResult TryFulfill(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand, AxisBudgetLedger ledger)
        {
            if (demand == null || ctx == null || root == null || player == null)
                return InfraFulfillResult.No("missing args");

            InfraCandidate cand =
                demand.Capability == CapabilityKind.EconomicInfrastructure
                    ? BuildEconomyCandidate(snap, player, root, hand, ctx, demand)
                    : demand.Capability == CapabilityKind.DevelopmentInfrastructure
                        ? BuildDevelopmentCandidate(player, root, hand, ctx)
                        : null;
            if (cand == null)
                return InfraFulfillResult.No($"{demand.Capability}: no legal authoritative build available now");

            // --- budget admission BEFORE any gameplay mutation (spec §1). A building is a large
            //     discrete commitment: require the requesting axis's OWN unreserved entitlement to
            //     cover it (Balance, not the cross-axis discrete-borrow headroom) so an infra build
            //     can never push an axis entitlement negative. ---
            if (ledger != null)
            {
                float axisRoom = ledger.Balance(demand.RequestingAxis)
                                 - ledger.ReservedFollowup(demand.RequestingAxis);
                if (cand.ApCost > axisRoom + AiConfigV2.allocatorSliceEpsilon)
                    return InfraFulfillResult.No(
                        $"{DesireAxes.Abbrev(demand.RequestingAxis)} axis entitlement {axisRoom:0.##} < cost {cand.ApCost:0.##}");
            }
            // --- live gameplay affordability (the executor re-checks; this keeps the demand open
            //     cleanly rather than letting a doomed transaction run) ---
            if (!root.CanSpendActionPoints(UnityEngine.Mathf.CeilToInt(cand.ApCost))
                || (cand.ResCost != null && !cand.ResCost.CanAfford(root)))
                return InfraFulfillResult.No($"{demand.Capability}: live AP/resources cannot cover {cand.Explain}");

            // --- authoritative transaction ---
            BuildingPlayResult r = cand.Execute();
            if (!r.Built)
            {
                AiDebugLog.Write($"[AI][V2]   infra — {demand.Capability} action rejected: {r.FailReason} ({cand.Explain})");
                return new InfraFulfillResult { Built = false, StateChanged = r.StateChanged, ApSpent = r.ApSpent, Detail = r.FailReason };
            }
            return new InfraFulfillResult { Built = true, ApSpent = r.ApSpent, StateChanged = r.StateChanged, Detail = cand.Explain };
        }

        // ECO — extraction facility for demand.EconomyResourceType on a same-type known unbuilt
        // site with a hero present. No generic-Base fallback (spec §4).
        private static InfraCandidate BuildEconomyCandidate(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand)
        {
            ResourceType? type = demand.EconomyResourceType ?? ResourceTypeAt(snap, demand.TargetHex);
            if (type == null)
                return null;
            CardDefinition facilityDef = ExtractionDef(ctx, type.Value);
            if (facilityDef == null)
                return null;

            foreach (HexCoord hex in CandidateEconomyHexes(snap, player, type.Value, demand.TargetHex))
            {
                if (!HexSelectionController.HasOwnHeroArmyAt(hex, player))
                    continue;
                HexCoord built = hex;
                return new InfraCandidate
                {
                    ApCost = facilityDef.apCost,
                    ResCost = facilityDef.resourceCost,
                    Explain = $"extraction {facilityDef.displayName} @({built.Q},{built.R}) for {type.Value}",
                    Execute = () => BuildingPlayExecutor.BuildExtractionFacility(player, root, ctx, facilityDef, built),
                };
            }
            return null;
        }

        // Known unbuilt resource sites of `type`, hero-preferred, deterministic order. The demand's
        // own TargetHex is tried first when it is a same-type site.
        private static IEnumerable<HexCoord> CandidateEconomyHexes(WorldSnapshot snap, PlayerSetupData player,
            ResourceType type, HexCoord? preferred)
        {
            if (snap?.Known?.ResourceHexes == null)
                yield break;
            var built = new HashSet<HexCoord>();
            if (snap.Known.Buildings != null)
                foreach (AiMapMemory.KnownBuilding kb in snap.Known.Buildings)
                    built.Add(kb.Hex);

            var sites = snap.Known.ResourceHexes
                .Where(kv => kv.Value == type && !built.Contains(kv.Key))
                .Select(kv => kv.Key)
                .OrderBy(h => preferred.HasValue && h.Equals(preferred.Value) ? 0 : 1)
                .ThenBy(h => h.Q).ThenBy(h => h.R)
                .ToList();
            foreach (HexCoord h in sites)
                yield return h;
        }

        // DEV — a CardType.Facility with Research/Production, into an owned Base slot.
        private static InfraCandidate BuildDevelopmentCandidate(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
        {
            if (hand?.Hand == null)
                return null;
            CardData card = hand.Hand
                .Where(c => c?.Definition != null && c.Definition.cardType == CardType.Facility
                    && c.Definition.grantedAbilities != null
                    && (c.Definition.grantedAbilities.Contains(UnitAbilities.Research)
                        || c.Definition.grantedAbilities.Contains(UnitAbilities.Production)))
                .OrderBy(c => c.Definition.displayName, System.StringComparer.Ordinal)
                .FirstOrDefault();
            if (card == null)
                return null;

            HexCoord? baseHex = BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player && b.IsBase)
                .Select(b => (HexCoord?)b.Hex)
                .OrderBy(h => h.Value.Q).ThenBy(h => h.Value.R)
                .FirstOrDefault(h => BuildingPlayExecutor.CanPlaceFacilityAt(player, hand, ctx, card, h.Value, out _));
            if (baseHex == null)
                return null;

            HexCoord at = baseHex.Value;
            return new InfraCandidate
            {
                ApCost = card.EffectivePlayApCost,
                ResCost = card.EffectivePlayResourceCost,
                Explain = $"Facility {card.Definition.displayName} into Base @({at.Q},{at.R})",
                Execute = () => BuildingPlayExecutor.PlayFacilityCard(player, root, hand, ctx, card, at),
            };
        }

        private static ResourceType? ResourceTypeAt(WorldSnapshot snap, HexCoord? hex)
        {
            if (snap?.Known?.ResourceHexes == null || hex == null)
                return null;
            foreach (KeyValuePair<HexCoord, ResourceType> kv in snap.Known.ResourceHexes)
                if (kv.Key.Equals(hex.Value))
                    return kv.Value;
            return null;
        }

        private static CardDefinition ExtractionDef(AiTurnContext ctx, ResourceType type)
        {
            CardDefinition[] arr = ctx?.GameConfig?.extractionFacilityCards;
            int i = (int)type;
            return arr != null && i >= 0 && i < arr.Length ? arr[i] : null;
        }
    }
}
