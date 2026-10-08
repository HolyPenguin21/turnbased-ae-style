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
        // Development CapacityUnlock: Utility is already the NET score of the whole step (its AP and
        // resources priced once on the one table), so Phase B must not subtract the marginal resource cost again.
        public bool FullyPriced;
        // Development CapacityUnlock: the live re-check run immediately before payment (null = none).
        public System.Func<bool> Revalidate;
        private readonly UnitData _repairUnit;
        private readonly HexCoord _repairHex;

        internal StrategicSpendCandidate(BuildingData upgradeBuilding, BaseUpgradeTier upgradeTier)
        {
            _upgradeBuilding = upgradeBuilding;
            _upgradeTier = upgradeTier;
        }

        // The standalone "repair a wounded unit at its own Base" maintenance task, as an ordinary
        // Phase-B tempo spend rather than something tied to any one mission. UnitRepair does the
        // repair; this is just the strategic candidate wrapper around it.
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
                : (Revalidate == null || Revalidate())
                    && StrategicMaintenancePolicy.ExecuteCapacityUpgrade(
                        player, root, ctx, _upgradeBuilding, _upgradeTier);
            stateChanged = ok;
            progressed = ok;
            return ok;
        }
    }

    // ===========================================================================================
    //  STRATEGIC MAINTENANCE POLICY
    // ===========================================================================================
    //  Non-card strategic actions live here: upgrading a Base/Citadel to unlock the next
    //  internal-Facility slot when a Facility already in hand is blocked SPECIFICALLY by slot
    //  capacity (not by affordability or by an already-open slot); and repairing a wounded unit at
    //  its own Base (an ordinary Phase-B tempo candidate — see StrategicSpendCandidate's own
    //  comment on the UnitData constructor).
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
        // Every eligible non-card strategic spend as an independent candidate.
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
                float apOpportunityCost = ActionPrice.ToCardScore(apCost);
                // The unit's own stat line: a hero carries no army combat power
                // (AiPower.ToPowerUnit) but its hit points are still worth restoring.
                float unitPower = AiPower.StatLinePower(unit);
                float restoredPower = unitPower * hpFraction;
                float weighted = restoredPower * AiConfigV2.repairPowerValueWeight;
                // Restored combat power on AiPower's own per-unit scale (AiPower.UnitPower — the
                // same stat-line-times-ability-multiplier reading ForceGrowth/CombatBody use),
                // weighted onto the shared utility scale by repairPowerValueWeight (target: roughly
                // "half the cost of replaying an equivalent body"; tune against the calibration
                // line below).
                float utility = weighted - apOpportunityCost;
                ResourceCost resCost = UnitRepair.ResourceCost(unit);
                // Calibration line: every repair
                // candidate considered this turn, not just the one the arbiter picks — so
                // repairPowerValueWeight can be judged/retuned against real play instead of guessed.
                AiDebugLog.Write($"[AI][V2][Maintenance][Repair] cand unit={unit.Name}(#{unit.RuntimeId}) "
                    + $"hex=({hex.Q},{hex.R}) hp={unit.HitPointsCurrent:0.#}/{unit.HitPointsMax:0.#} "
                    + $"hpFraction={hpFraction:0.00} unitPower={unitPower:0.00} "
                    + $"restoredPower={restoredPower:0.00} weight={AiConfigV2.repairPowerValueWeight:0.00} "
                    + $"weighted={weighted:0.00} apCost={apCost} apOpportunityCost={apOpportunityCost:0.00} "
                    + $"resCost=[H{resCost.human} E{resCost.energy} M{resCost.materials} T{resCost.tech}] "
                    + $"utility={utility:0.00}");
                list.Add(new StrategicSpendCandidate(unit, hex)
                {
                    Label = $"repair {unit.Name} (#{unit.RuntimeId}) at ({hex.Q},{hex.R}): "
                        + $"{unit.HitPointsCurrent}/{unit.HitPointsMax} HP "
                        + $"(power {unitPower:0.0} x hpFrac {hpFraction:0.00} x w{AiConfigV2.repairPowerValueWeight:0.0} "
                        + $"= {weighted:0.0} - apOpp {apOpportunityCost:0.0})",
                    StableKey = "repair:" + unit.RuntimeId,
                    Utility = utility,
                    ApCost = apCost,
                    ResCost = resCost,
                });
            }

            foreach (CapacityUpgrade up in FindCapacityUpgrades(
                snap, player, root, hand, ctx, witnessedUsefulApDemand))
            {
                float upgradeApOpportunityCost = ActionPrice.ToCardScore(
                    up.Tier != null ? up.Tier.apCost : 0f);
                // A Development CapacityUnlock arrives fully priced; every other upgrade is the unlocked
                // Facility value minus its AP (Phase B prices its resources separately).
                float utility = up.FullyPriced ? up.FacilityUtility : up.FacilityUtility - upgradeApOpportunityCost;
                list.Add(new StrategicSpendCandidate(up.Building, up.Tier)
                {
                    FullyPriced = up.FullyPriced,
                    Revalidate = up.Revalidate,
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
            public bool FullyPriced;
            public System.Func<bool> Revalidate;
        }

        // Enumerate every Base/Citadel where buying the next tier would unlock a Facility slot AND
        // an internal Facility in hand is currently blocked by nothing but that missing slot.
        private static IEnumerable<CapacityUpgrade> FindCapacityUpgrades(WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            float? witnessedUsefulApDemand)
        {
            if (hand?.Hand == null || ctx.GameConfig?.baseUpgradeTiers == null)
                yield break;

            List<BuildingData> bases = TieredOwnBases(player);
            if (bases.Count == 0)
                yield break;

            // Research/Production facilities unlock only through the SAME admitted CapacityUnlock step
            // Development itself would act on (current-step window/bank/cost), never a looser predicate:
            // the opportunity IS the candidate, with the evaluator own fully priced score.
            var intents = MissionIntentRegistry.GetOrCreate(player).All.ToList();
            foreach (DevelopmentOpportunity op in DevelopmentOpportunityEvaluator.Enumerate(
                    snap, player, root, hand, ctx, intents)
                .Where(o => o.PreparationKind == DevelopmentPreparationKind.CapacityUnlock
                    && o.PreparationCapacityTier != null && o.PreparationFacilityCard != null
                    && o.PreparationCardScore.HasValue))
            {
                BuildingData building = BuildingRegistry.FindAt(op.FacilityHex);
                BaseUpgradeTier planned = op.PreparationCapacityTier;
                if (building == null || building.Owner != player || !building.IsBase)
                    continue;
                yield return new CapacityUpgrade
                {
                    Building = building,
                    Tier = planned,
                    Facility = op.PreparationFacilityCard,
                    FacilityUtility = op.PreparationCardScore.Value,
                    FacilityBreakdown = op.Explain,
                    FullyPriced = true,
                    // Live re-check right before payment: the SAME confirmation Phase A runs (base, level, tier,
                    // closed slot, witness card, window, operator path, no facility of the mode elsewhere).
                    Revalidate = () => DevelopmentOpportunityEvaluator.ConfirmCapacityUnlock(
                            op, snap, player, root, hand, ctx, intents, out BuildingData confirmed,
                            out BaseUpgradeTier confirmedTier)
                        && ReferenceEquals(confirmed, building) && ReferenceEquals(confirmedTier, planned),
                };
            }
            bool NeedsDevelopment(CardData card) => card?.Definition?.grantedAbilities != null
                && (card.Definition.grantedAbilities.Contains(ResearchProductionSystem.FacilityAbility(ResearchProductionMode.Research))
                    || card.Definition.grantedAbilities.Contains(ResearchProductionSystem.FacilityAbility(ResearchProductionMode.Production)));

            // Evaluate every OTHER Facility card, including economy / ApBonus Facilities. A card is an
            // upgrade consequence only when the authoritative placement check rejects it for the
            // capacity reason at every owned Base; an AP/resource/ownership failure must not be
            // disguised as a capacity dependency.
            var blockedFacilities = hand.Hand
                .Select((card, ordinal) => new { Card = card, Ordinal = ordinal })
                .Where(x => x.Card?.Definition != null
                    && x.Card.Definition.cardType == CardType.Facility)
                .Where(x => !NeedsDevelopment(x.Card))
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

            foreach (BuildingData b in UnlockableBasesInOrder(bases))
            {
                var bestFacility = blockedFacilities.FirstOrDefault();
                if (bestFacility == null)
                    continue;
                BaseUpgradeTier tier = CapacityUnlockTierAt(b, ctx);
                if (tier == null)
                    continue;
                yield return new CapacityUpgrade
                {
                    Building = b,
                    Tier = tier,
                    Facility = bestFacility.Card,
                    FacilityUtility = bestFacility.Evaluation.TotalUseScore,
                    FacilityBreakdown = bestFacility.Evaluation.Breakdown?.ToCompact() ?? "no breakdown",
                };
            }
        }

        // The ONE "which Base / which tier unlocks the next Facility slot" rule, shared by the
        // Phase-B maintenance candidate above and the Economy global-source placement
        // (InfrastructureFulfillment), so both buy the same upgrade for the same card.
        private static List<BuildingData> TieredOwnBases(PlayerSetupData player) =>
            BuildingRegistry.AllBuildings()
                .Where(b => b != null && b.Owner == player && b.IsBase && b.HasTieredUnlock)
                .ToList();

        private static IEnumerable<BuildingData> UnlockableBasesInOrder(IEnumerable<BuildingData> bases) =>
            bases.Where(x => x.UnlockedFacilitySlots < x.TotalFacilitySlots)
                .OrderByDescending(x => x.IsStartingCitadel)
                .ThenBy(x => x.Level)
                .ThenBy(x => x.Hex.Q).ThenBy(x => x.Hex.R);

        private static BaseUpgradeTier NextTier(BuildingData b, AiTurnContext ctx)
        {
            BaseUpgradeTier[] tiers = ctx?.GameConfig?.baseUpgradeTiers;
            int tierIndex = b.Level - 1;
            return tiers != null && tierIndex >= 0 && tierIndex < tiers.Length ? tiers[tierIndex] : null;
        }

        // A non-Development Facility in hand that is blocked ONLY by slot capacity at every owned
        // Base: the Base/tier whose upgrade unlocks it (same order as FindCapacityUpgrades).
        // Development facilities keep their preparation-witness path above.
        // Structural form of the same unlock rule for ONE site: the tier that opens a Facility slot
        // on `b` when every unlocked slot is taken. No AP/resource test — a Development facility
        // stage prices it into its own stage cost; Execution re-checks affordability.
        internal static BaseUpgradeTier CapacityUnlockTierAt(BuildingData b, AiTurnContext ctx)
        {
            if (b == null || !b.IsBase || !b.HasTieredUnlock
                || b.FindFirstAvailableFacilitySlot() >= 0
                || !UnlockableBasesInOrder(new[] { b }).Any())
                return null;
            return NextTier(b, ctx);
        }

        // The one price of buying a capacity tier as a facility's own prerequisite, on the
        // canonical AP/resource table.
        internal static float CapacityTierPrice(BaseUpgradeTier tier, WorldSnapshot snap) =>
            tier == null ? 0f
                : ActionPrice.ToCardScore(ActionPrice.Ap(tier.apCost)
                    + ActionPrice.Resources(tier.cost, snap));

        internal static bool TryFindCapacityUnlock(CardData facility, PlayerSetupData player,
            AiHandData hand, AiTurnContext ctx, out BuildingData building, out BaseUpgradeTier tier)
        {
            building = null;
            tier = null;
            if (facility?.Definition == null || facility.Definition.cardType != CardType.Facility)
                return false;
            List<BuildingData> bases = TieredOwnBases(player);
            if (bases.Count == 0 || !IsBlockedOnlyByCapacity(facility, bases, player, hand, ctx))
                return false;
            foreach (BuildingData b in UnlockableBasesInOrder(bases))
            {
                BaseUpgradeTier next = CapacityUnlockTierAt(b, ctx);
                if (next == null)
                    continue;
                building = b;
                tier = next;
                return true;
            }
            return false;
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
            // The payment and the mutation belong to the gameplay primitive (the human UI uses it too);
            // the planned tier must still be the Base's next one, so a stale plan buys nothing.
            BaseUpgradeTier[] tiers = ctx.GameConfig?.baseUpgradeTiers;
            if (b == null || tier == null
                || !InfrastructureActions.CanUpgradeBase(b, tiers, null, out BaseUpgradeTier next, out _)
                || !ReferenceEquals(next, tier))
                return false;
            if (!InfrastructureActions.TryUpgradeBase(b, tiers).Ok)
                return false;

            activity.InfrastructureBuilt++;
            AiDebugLog.Write($"[AI][V2] maintenance capacity — upgraded {b.Name} "
                + $"@({b.Hex.Q},{b.Hex.R}) to level {b.Level}; facility slots {b.UnlockedFacilitySlots}/{b.TotalFacilitySlots}; "
                + $"ap {apBefore}->{root.ActionPoints}");
            return true;
        }
    }
}

