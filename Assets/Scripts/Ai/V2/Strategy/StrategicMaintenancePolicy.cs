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
    // A genuinely NON-CARD strategic spend the end-of-turn tempo arbiter can rank against
    // PlayCard / DrawCard / HoldResources / EndTurn in one comparable utility space. It carries
    // the EXACT action + target + full cost and its own executor payload, so the arbiter runs the
    // chosen candidate verbatim — it never asks the policy for "the best action" a second time
    // (which is how the old DescribeBest → TryExecuteBest split re-decided after arbitration).
    internal sealed class StrategicSpendCandidate
    {
        public string Label;
        public string StableKey;
        public float Utility;
        public float ApCost;
        public ResourceCost ResCost;         // persistent-resource cost vector (may be null)

        private readonly BuildingData _upgradeBuilding;
        private readonly BaseUpgradeTier _upgradeTier;

        internal StrategicSpendCandidate(BuildingData upgradeBuilding, BaseUpgradeTier upgradeTier)
        {
            _upgradeBuilding = upgradeBuilding;
            _upgradeTier = upgradeTier;
        }

        // Execute EXACTLY this candidate. No re-selection.
        public bool Execute(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            out bool stateChanged, out bool progressed)
        {
            bool ok = StrategicMaintenancePolicy.ExecuteCapacityUpgrade(
                player, root, ctx, _upgradeBuilding, _upgradeTier);
            stateChanged = ok;
            progressed = ok;
            return ok;
        }
    }

    // ===========================================================================================
    //  STRATEGIC MAINTENANCE POLICY  (AI-MGR-02)
    // ===========================================================================================
    //  ONLY non-card strategic actions live here now: upgrading a Base/Citadel to unlock the next
    //  internal-Facility slot when a Facility already in hand is blocked SPECIFICALLY by slot
    //  capacity (not by affordability or by an already-open slot).
    //
    //  Everything that is a card play — placing that internal Facility, attaching Equipment to a
    //  live unit, running a Research/Production Challenge — is an ordinary PlayCard candidate in
    //  the end-of-turn tempo arbiter, scored by the single StrategicCardEvaluator through
    //  NonCombatCardPlayer (spec §5, one card scorer). There is deliberately NO second card
    //  scorer here and NO hidden "facility, then capacity, then equipment, then generation"
    //  priority chain: EnumerateCandidates returns every eligible non-card action and the arbiter
    //  ranks purely by utility.
    // ===========================================================================================
    internal static class StrategicMaintenancePolicy
    {
        // AI-MGR-02 §1/§3 — every eligible non-card strategic spend as an independent candidate.
        public static List<StrategicSpendCandidate> EnumerateCandidates(PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            var list = new List<StrategicSpendCandidate>();
            if (player == null || root == null || hand == null || ctx == null)
                return list;

            foreach (CapacityUpgrade up in FindCapacityUpgrades(player, hand, ctx))
                list.Add(new StrategicSpendCandidate(up.Building, up.Tier)
                {
                    Label = $"capacity upgrade {up.Building.Name} -> level {up.Building.Level + 1} "
                        + "(unlock internal-facility slot blocked by capacity)",
                    StableKey = "capacity:" + up.Building.Hex,
                    Utility = AiConfigV2.tempoMaintenanceCapacityUpgradeValue,
                    ApCost = up.Tier != null ? up.Tier.apCost : 0f,
                    ResCost = up.Tier != null ? up.Tier.cost : null,
                });
            return list;
        }

        // ---------------------------------------------------------------- internal Facilities ----

        private static IEnumerable<CardData> InternalFacilityCards(AiHandData hand) =>
            hand?.Hand == null
                ? Enumerable.Empty<CardData>()
                : hand.Hand.Where(c => c?.Definition != null
                    && c.Definition.cardType == CardType.Facility
                    && c.Definition.grantedAbilities != null
                    && (c.Definition.grantedAbilities.Contains(UnitAbilities.Research)
                        || c.Definition.grantedAbilities.Contains(UnitAbilities.Production)));

        // ---------------------------------------------------------------- capacity upgrade ----

        internal sealed class CapacityUpgrade
        {
            public BuildingData Building;
            public BaseUpgradeTier Tier;
        }

        // Enumerate every Base/Citadel where buying the next tier would unlock a Facility slot AND
        // an internal Facility in hand is currently blocked by nothing but that missing slot.
        private static IEnumerable<CapacityUpgrade> FindCapacityUpgrades(PlayerSetupData player,
            AiHandData hand, AiTurnContext ctx)
        {
            if (!InternalFacilityCards(hand).Any() || ctx.GameConfig?.baseUpgradeTiers == null)
                yield break;

            List<BuildingData> bases = BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player && b.IsBase && b.HasTieredUnlock)
                .ToList();
            if (bases.Count == 0)
                yield break;

            // If ANY owned Base already has an unlocked empty slot, the Facility is blocked by
            // something else (usually card affordability), not by capacity. Do not buy a fake
            // dependency upgrade in that case.
            if (bases.Any(b => b.FindFirstAvailableFacilitySlot() >= 0))
                yield break;

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
                if (tier == null)
                    continue;
                yield return new CapacityUpgrade { Building = b, Tier = tier };
            }
        }

        internal static bool ExecuteCapacityUpgrade(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            BuildingData b, BaseUpgradeTier tier)
        {
            V2PhaseActivity activity = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Main);
            activity.InfrastructureAttempts++;
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
    }
}
