using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // What an admitted infrastructure build does, as data: InfrastructureFulfillment (Strategy)
    // chooses it, InfrastructureBuildExecutor runs it. Each kind is one canonical gameplay action.
    internal enum InfraBuildKind
    {
        BaseCard,             // a Base card on the target hex (optionally with its garrison defender)
        ExtractionFacility,   // an extraction facility on the built site
        FacilityCard,         // a Facility card into an own Base
        FacilityAfterUpgrade, // buy the Base's next level when a slot must be unlocked, then the Facility
        // An operator card onto its facility hex: the garrison, or a hero-only bench there
        // (2026-11-20, PlacementSelector.FacilityOperatorPlacements).
        OperatorOnFacilityHex,
    }

    internal sealed class InfraBuildAction
    {
        internal InfraBuildKind Kind;
        internal CardData Card;
        internal HexCoord Hex;
        internal int? BuilderArmyId;
        internal CardData Defender;
        internal CardDefinition FacilityDef;
        internal BuildingData UpgradeBuilding;
        internal BaseUpgradeTier UpgradeTier;
        internal PlacementOption Placement;

        internal static InfraBuildAction BaseCard(CardData card, HexCoord hex, int? builderArmyId, CardData defender) =>
            new InfraBuildAction { Kind = InfraBuildKind.BaseCard, Card = card, Hex = hex,
                BuilderArmyId = builderArmyId, Defender = defender };

        internal static InfraBuildAction ExtractionFacility(CardDefinition facilityDef, HexCoord hex) =>
            new InfraBuildAction { Kind = InfraBuildKind.ExtractionFacility, FacilityDef = facilityDef, Hex = hex };

        internal static InfraBuildAction FacilityCard(CardData card, HexCoord hex) =>
            new InfraBuildAction { Kind = InfraBuildKind.FacilityCard, Card = card, Hex = hex };

        internal static InfraBuildAction FacilityAfterUpgrade(CardData card, HexCoord hex,
            BuildingData building, BaseUpgradeTier tier) =>
            new InfraBuildAction { Kind = InfraBuildKind.FacilityAfterUpgrade, Card = card, Hex = hex,
                UpgradeBuilding = building, UpgradeTier = tier };

        internal static InfraBuildAction OperatorOnFacilityHex(CardData card, PlacementOption placement) =>
            new InfraBuildAction { Kind = InfraBuildKind.OperatorOnFacilityHex, Card = card,
                Hex = placement.Hex, Placement = placement };
    }

    // ===========================================================================================
    //  INFRASTRUCTURE EXECUTION (2026-10-15: moved out of Strategy's InfrastructureFulfillment and
    //  StrategicMaintenancePolicy). Runs exactly the admitted action through the canonical gameplay
    //  primitives and reports what really happened; it never re-selects a card, hex or tier.
    // ===========================================================================================
    internal static class InfrastructureBuildExecutor
    {
        internal static BuildingPlayResult Run(InfraBuildAction a, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
        {
            switch (a?.Kind)
            {
                case InfraBuildKind.BaseCard:
                    return BuildingPlayExecutor.PlayBaseCard(player, root, hand, ctx, a.Card, a.Hex,
                        a.BuilderArmyId, a.Defender);
                case InfraBuildKind.ExtractionFacility:
                    return BuildingPlayExecutor.BuildExtractionFacility(player, root, ctx, a.FacilityDef, a.Hex);
                case InfraBuildKind.FacilityCard:
                    return BuildingPlayExecutor.PlayFacilityCard(player, root, hand, ctx, a.Card, a.Hex);
                case InfraBuildKind.FacilityAfterUpgrade:
                    return PlaceFacilityAfterOptionalUpgrade(player, root, hand, ctx, a.Card, a.Hex,
                        a.UpgradeBuilding, a.UpgradeTier);
                case InfraBuildKind.OperatorOnFacilityHex:
                {
                    CardPlayResult r = CardPlayExecutor.Play(player, root, hand, ctx, a.Placement.Bind(a.Card));
                    return new BuildingPlayResult
                    {
                        Built = r.Deployed,
                        ApSpent = r.ApSpent,
                        StateChanged = r.StateChanged,
                        CardConsumed = r.Deployed,
                        StateVersionAfter = r.StateVersionAfter,
                        FailReason = r.FailReason,
                    };
                }
                default:
                    return BuildingPlayResult.Fail("no infrastructure action");
            }
        }

        // One action: buy `tier` on `building` (when a slot must be unlocked first), then place the
        // Facility. Shared by every infrastructure path whose facility carries its own capacity
        // prerequisite (global resource source, Development facility stage).
        private static BuildingPlayResult PlaceFacilityAfterOptionalUpgrade(PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, CardData card, HexCoord hex,
            BuildingData building, BaseUpgradeTier tier)
        {
            if (tier == null)
                return BuildingPlayExecutor.PlayFacilityCard(player, root, hand, ctx, card, hex);
            int apBefore = root.ActionPoints;
            int rateBeforeFunding = root.TradeRate;
            if (!UpgradeBaseCapacity(player, root, ctx, building, tier))
                return new BuildingPlayResult { FailReason = "capacity upgrade refused",
                    StateChanged = root.TradeRate != rateBeforeFunding,
                    StateVersionAfter = root.TradeRate != rateBeforeFunding ? WorldDeltaLifecycle.Current : -1 };
            int upgradeVersion = WorldDeltaLifecycle.CommitMutation();
            BuildingPlayResult placed = BuildingPlayExecutor.PlayFacilityCard(
                player, root, hand, ctx, card, hex);
            // The upgrade is a real mutation even if the placement then fails.
            placed.StateChanged = true;
            placed.ApSpent = V2ActionOutcome.ApSpentSince(apBefore, root);
            if (placed.StateVersionAfter < upgradeVersion)
                placed.StateVersionAfter = upgradeVersion;
            return placed;
        }

        // A stand-alone Base level (Development CapacityUnlock): one payment + one mutation by the
        // gameplay primitive, then ONE world receipt. No authority: the payment may not reach into
        // anyone else's hold. A prior conversion remains committed when the upgrade refuses.
        internal static BaseUpgradeOutcome UnlockCapacity(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, BuildingData building, int expectedLevel, HexCoord hex, out int stateVersionAfter)
        {
            ReservationInvariants.SpendProbe probe = ReservationInvariants.BeginSpend(
                player, root, ctx, "phaseA CapacityUnlock");
            stateVersionAfter = WorldDeltaLifecycle.Current;
            if (building == null || building.Owner != player) return BaseUpgradeOutcome.Fail("Base owner changed.");
            if (!InfrastructureActions.CanUpgradeBase(building, ctx.GameConfig?.baseUpgradeTiers,
                    expectedLevel, out var tier, out string reason, requireCurrentResources: false))
                return BaseUpgradeOutcome.Fail(reason);
            if (!TradeFunding.Fund(player, root, ctx, tier.cost))
                return BaseUpgradeOutcome.Fail("Capacity funding changed.");
            BaseUpgradeOutcome outcome = InfrastructureActions.TryUpgradeBase(
                building, ctx.GameConfig?.baseUpgradeTiers, expectedLevel);
            ReservationInvariants.EndSpend(player, root, ctx, probe);
            stateVersionAfter = WorldDeltaLifecycle.Current;
            if (!outcome.Ok)
                return outcome;
            // ONE top-level mutation, one receipt; the placement later is its own mutation.
            stateVersionAfter = WorldDeltaLifecycle.CommitMutation();
            WorldDeltaLifecycle.Publish(player, ctx.TurnNumber,
                StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Capability,
                hexes: new[] { hex });
            return outcome;
        }

        // Buy exactly the planned next tier of `b` (maintenance capacity, a facility's slot
        // prerequisite). The payment and the mutation belong to the gameplay primitive (the human UI
        // uses it too); the planned tier must still be the Base's next one, so a stale plan buys nothing.
        internal static bool UpgradeBaseCapacity(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            BuildingData b, BaseUpgradeTier tier)
        {
            V2TurnActivity activity = V2TurnActivityTelemetry.For(player, ctx.TurnNumber);
            activity.InfrastructureAttempts++;
            int apBefore = root.ActionPoints;
            BaseUpgradeTier[] tiers = ctx.GameConfig?.baseUpgradeTiers;
            if (b == null || b.Owner != player || tier == null
                || !InfrastructureActions.CanUpgradeBase(b, tiers, null, out BaseUpgradeTier next, out _, requireCurrentResources: false)
                || !ReferenceEquals(next, tier))
                return false;
            if (!TradeFunding.Fund(player, root, ctx, tier.cost)
                || !InfrastructureActions.TryUpgradeBase(b, tiers).Ok)
                return false;

            activity.InfrastructureBuilt++;
            AiDebugLog.Write($"[AI][V2] maintenance capacity - upgraded {b.Name} "
                + $"@({b.Hex.Q},{b.Hex.R}) to level {b.Level}; facility slots {b.UnlockedFacilitySlots}/{b.TotalFacilitySlots}; "
                + $"ap {apBefore}->{root.ActionPoints}");
            return true;
        }

        // The maintenance repair of a wounded unit at an owned repair point.
        internal static bool RepairUnit(UnitData unit, HexCoord hex, PlayerRoot root) =>
            UnitRepair.TryRepair(unit, hex, root, out _);
    }
}

