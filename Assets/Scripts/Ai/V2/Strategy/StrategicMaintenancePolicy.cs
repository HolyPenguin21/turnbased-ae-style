using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
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
        private readonly UnitData _repairUnit;
        private readonly HexCoord _repairHex;

        internal StrategicSpendCandidate(BuildingData upgradeBuilding, BaseUpgradeTier upgradeTier)
        {
            _upgradeBuilding = upgradeBuilding;
            _upgradeTier = upgradeTier;
        }

        // AI-MGR-02 follow-up (project owner's own 2026-09-21 call) — restores the V1 standalone
        // "repair a wounded unit at its own Base" maintenance task (AiTaskKind.RepairUnit, dropped
        // with AiTask.cs in ARCH-01 and never ported to V2), as an ordinary Phase-B tempo spend
        // rather than something tied to any one mission. UnitRepair itself is unchanged — this is
        // just the strategic candidate wrapper around it.
        internal StrategicSpendCandidate(UnitData repairUnit, HexCoord repairHex)
        {
            _repairUnit = repairUnit;
            _repairHex = repairHex;
        }

        // Execute EXACTLY this candidate. No re-selection.
        public bool Execute(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            out bool stateChanged, out bool progressed)
        {
            bool ok = _repairUnit != null
                ? UnitRepair.TryRepair(_repairUnit, _repairHex, root, out _)
                : StrategicMaintenancePolicy.ExecuteCapacityUpgrade(
                    player, root, ctx, _upgradeBuilding, _upgradeTier);
            stateChanged = ok;
            progressed = ok;
            return ok;
        }
    }

    // ===========================================================================================
    //  STRATEGIC MAINTENANCE POLICY  (AI-MGR-02)
    // ===========================================================================================
    //  Non-card strategic actions live here: upgrading a Base/Citadel to unlock the next
    //  internal-Facility slot when a Facility already in hand is blocked SPECIFICALLY by slot
    //  capacity (not by affordability or by an already-open slot); and repairing a wounded unit at
    //  its own Base (the V1 AiTaskKind.RepairUnit management task, restored as an ordinary Phase-B
    //  tempo candidate — see StrategicSpendCandidate's own comment on the UnitData constructor).
    //
    //  Card execution remains with its existing owner. Research/Production facilities require a
    //  independent, profitable Development prerequisite; this policy unlocks their slot with the same
    //  concrete output/recipient-or-deployment witness. Other facilities keep the ordinary surplus path, scored
    //  by StrategicCardEvaluator through NonCombatCardPlayer (spec §5, one card scorer). There is deliberately NO second card
    //  scorer here and NO hidden "facility, then capacity, then equipment, then generation"
    //  priority chain: EnumerateCandidates returns every eligible non-card action and the arbiter
    //  ranks purely by utility.
    // ===========================================================================================
    internal static class StrategicMaintenancePolicy
    {
        // AI-MGR-02 §1/§3 — every eligible non-card strategic spend as an independent candidate.
        public static List<StrategicSpendCandidate> EnumerateCandidates(PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, WorldSnapshot snap,
            float? witnessedUsefulApDemand = null)
        {
            var list = new List<StrategicSpendCandidate>();
            if (player == null || root == null || hand == null || ctx == null)
                return list;

            foreach ((UnitData unit, HexCoord hex) in FindRepairCandidates(player))
            {
                float hpFraction = 1f - unit.HitPointsCurrent
                    / (float)Mathf.Max(1, unit.HitPointsMax);
                int apCost = UnitRepair.ApCost(unit);
                float apOpportunityCost = AiConfigV2.stratCardApCostWeight * apCost;
                // Restored combat power on AiPower's own per-unit scale (AiPower.UnitPower — the
                // same stat-line-times-ability-multiplier reading ForceGrowth/CombatBody already
                // use), weighted onto the shared utility scale by repairPowerValueWeight — a
                // first-cut constant (project owner's own 2026-09-21 call: repair should land
                // roughly at "half the cost of replaying an equivalent body", not calibrated
                // against a played log yet, unlike most other weights in this file).
                float utility = AiPower.UnitPower(unit) * hpFraction
                    * AiConfigV2.repairPowerValueWeight - apOpportunityCost;
                list.Add(new StrategicSpendCandidate(unit, hex)
                {
                    Label = $"repair {unit.Name} (#{unit.RuntimeId}) at ({hex.Q},{hex.R}): "
                        + $"{unit.HitPointsCurrent}/{unit.HitPointsMax} HP",
                    StableKey = "repair:" + unit.RuntimeId,
                    Utility = utility,
                    ApCost = apCost,
                    ResCost = UnitRepair.ResourceCost(unit),
                });
            }

            foreach (CapacityUpgrade up in FindCapacityUpgrades(
                snap, player, root, hand, ctx, witnessedUsefulApDemand))
            {
                float upgradeApOpportunityCost = AiConfigV2.stratCardApCostWeight
                    * (up.Tier != null ? up.Tier.apCost : 0f);
                float utility = up.FacilityUtility - upgradeApOpportunityCost;
                list.Add(new StrategicSpendCandidate(up.Building, up.Tier)
                {
                    Label = $"capacity upgrade {up.Building.Name} -> level {up.Building.Level + 1} "
                        + $"to unlock {up.Facility.Definition?.displayName} "
                        + $"(unlock {up.FacilityUtility:0.00} - upgradeAP {upgradeApOpportunityCost:0.00}; "
                        + $"{up.FacilityBreakdown})",
                    StableKey = "capacity:" + up.Building.Hex,
                    // The upgrade unlocks the OPTION to play this Facility; it does not consume the
                    // card now. Use TotalUseScore (which already prices the Facility's eventual AP
                    // and H/E/M/T cost), not NetScore (which additionally subtracts the value of
                    // keeping the still-owned card). Price the upgrade's own AP here exactly once;
                    // Phase B separately prices its exact persistent-resource vector.
                    Utility = utility,
                    ApCost = up.Tier != null ? up.Tier.apCost : 0f,
                    ResCost = up.Tier != null ? up.Tier.cost : null,
                });
            }
            return list;
        }

        // ------------------------------------------------------------------------- repair ----

        // Every wounded, non-prison, non-airfield own-army member currently sitting on this
        // player's own Base — garrison included (a garrison's hex IS a Base hex by definition).
        // Live ArmyRegistry, not the snapshot: the executed candidate must act on the exact same
        // UnitData UnitRepair.TryRepair mutates, the same "candidate carries its own live payload"
        // convention capacity-upgrade already uses above (BuildingData, not a snapshot DTO).
        private static IEnumerable<(UnitData Unit, HexCoord Hex)> FindRepairCandidates(
            PlayerSetupData player)
        {
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army == null || army.IsPrison || AviationRules.IsAirfield(army)
                    || !UnitRepair.CanRepairAt(army.Hex, player))
                    continue;
                foreach (UnitData unit in army.Members)
                    // A captured enemy hero can be mid-escort inside a normal (non-Prison) army;
                    // it is never ours to repair (UnitRepair.CanRepairAt checks unit.Owner, not
                    // army ownership, and would already refuse it — skip it here too so it never
                    // surfaces as a candidate that is guaranteed to fail on execution).
                    if (!unit.IsPrisoner && UnitRepair.IsWounded(unit))
                        yield return (unit, army.Hex);
            }
        }

        // ---------------------------------------------------------------- capacity upgrade ----

        internal sealed class CapacityUpgrade
        {
            public BuildingData Building;
            public BaseUpgradeTier Tier;
            public CardData Facility;
            public float FacilityUtility;
            public string FacilityBreakdown;
        }

        // Enumerate every Base/Citadel where buying the next tier would unlock a Facility slot AND
        // an internal Facility in hand is currently blocked by nothing but that missing slot.
        private static IEnumerable<CapacityUpgrade> FindCapacityUpgrades(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            float? witnessedUsefulApDemand)
        {
            if (hand?.Hand == null || ctx.GameConfig?.baseUpgradeTiers == null)
                yield break;

            List<BuildingData> bases = BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player && b.IsBase && b.HasTieredUnlock)
                .ToList();
            if (bases.Count == 0)
                yield break;

            var intents = MissionIntentRegistry.GetOrCreate(player).All.ToList();
            List<DevelopmentOpportunity> preparation = DevelopmentOpportunityEvaluator.EnumeratePreparation(
                snap, player, root, hand, ctx,
                op => op != null && op.ExpectedGain > 0f, intents);
            bool NeedsDevelopment(CardData card) => card?.Definition?.grantedAbilities != null
                && (card.Definition.grantedAbilities.Contains(ResearchProductionSystem.FacilityAbility(ResearchProductionMode.Research))
                    || card.Definition.grantedAbilities.Contains(ResearchProductionSystem.FacilityAbility(ResearchProductionMode.Production)));

            // Evaluate every Facility card, including economy / ApBonus Facilities. A card is an
            // upgrade consequence only when the authoritative placement check rejects it for the
            // capacity reason at every owned Base; an AP/resource/ownership failure must not be
            // disguised as a capacity dependency.
            var blockedFacilities = hand.Hand
                .Select((card, ordinal) => new { Card = card, Ordinal = ordinal })
                .Where(x => x.Card?.Definition != null
                    && x.Card.Definition.cardType == CardType.Facility)
                .Where(x => !NeedsDevelopment(x.Card)
                    || preparation.Any(op => op.PreparationFacilityCard == x.Card))
                .Where(x => IsBlockedOnlyByCapacity(x.Card, bases, player, hand, ctx))
                .Select(x => new
                {
                    x.Card,
                    x.Ordinal,
                    Evaluation = NonCombatCardPlayer.ScoreCapacityUnlock(
                        snap, player, root, ctx, x.Card, hand, witnessedUsefulApDemand),
                })
                .Where(x => x.Evaluation != null)
                .OrderByDescending(x => x.Evaluation.TotalUseScore)
                .ThenBy(x => x.Ordinal)
                .ToList();
            if (blockedFacilities.Count == 0)
                yield break;

            foreach (BuildingData b in bases
                .Where(x => x.UnlockedFacilitySlots < x.TotalFacilitySlots)
                .OrderByDescending(x => x.IsStartingCitadel)
                .ThenBy(x => x.Level)
                .ThenBy(x => x.Hex.Q).ThenBy(x => x.Hex.R))
            {
                var bestFacility = blockedFacilities.FirstOrDefault(x => !NeedsDevelopment(x.Card)
                    || preparation.Any(op => op.PreparationFacilityCard == x.Card
                        && op.FacilityHex.Equals(b.Hex)));
                if (bestFacility == null)
                    continue;
                int tierIndex = b.Level - 1;
                if (tierIndex < 0 || tierIndex >= ctx.GameConfig.baseUpgradeTiers.Length)
                    continue;
                BaseUpgradeTier tier = ctx.GameConfig.baseUpgradeTiers[tierIndex];
                if (tier == null)
                    continue;
                DevelopmentOpportunity witness = NeedsDevelopment(bestFacility.Card)
                    ? preparation.FirstOrDefault(op => op.PreparationFacilityCard == bestFacility.Card
                        && op.FacilityHex.Equals(b.Hex)
                        && !ResourceBundle.All.Any(t => (tier.cost?.Get(t) ?? 0)
                            + (op.PreparationFacilityCard?.EffectivePlayResourceCost?.Get(t) ?? 0)
                            + (op.PreparationOperatorCard?.EffectivePlayResourceCost?.Get(t) ?? 0)
                            + (op.PreparationOperatorGeneration?.GenerationResourceCost?.Get(t) ?? 0)
                            + (op.Card?.resourceCost?.Get(t) ?? 0)
                            > StrategicSpendability.SpendableAmount(player, root, ctx, t)))
                    : null;
                if (NeedsDevelopment(bestFacility.Card) && witness == null)
                    continue;
                yield return new CapacityUpgrade
                {
                    Building = b,
                    Tier = tier,
                    Facility = bestFacility.Card,
                    FacilityUtility = witness != null
                        ? Mathf.Min(bestFacility.Evaluation.TotalUseScore, witness.Ev)
                        : bestFacility.Evaluation.TotalUseScore,
                    FacilityBreakdown = bestFacility.Evaluation.Breakdown?.ToCompact() ?? "no breakdown",
                };
            }
        }

        private static bool IsBlockedOnlyByCapacity(CardData card, IReadOnlyList<BuildingData> bases,
            PlayerSetupData player, AiHandData hand, AiTurnContext ctx)
        {
            bool sawCapacityBlock = false;
            foreach (BuildingData b in bases)
            {
                if (BuildingPlayExecutor.CanPlaceFacilityAt(
                    player, hand, ctx, card, b.Hex, out string reason))
                    return false; // already playable: no dependency to buy
                if (reason != InfrastructureActions.NoFreeFacilitySlotReason)
                    return false; // affordability or another rule is also blocking it
                sawCapacityBlock = true;
            }
            return sawCapacityBlock;
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

