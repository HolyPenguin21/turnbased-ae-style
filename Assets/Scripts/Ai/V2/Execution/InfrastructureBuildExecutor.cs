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
        OperatorGeneration,   // mint a missing operator through a staffed generator's Challenge
        CapacityUnlock,       // buy one Base level on its own; the Facility is a later action
    }

    // What one infrastructure action really did.
    internal sealed class InfraFulfillResult
    {
        public bool ResourceConverted;
        public bool Built;
        // A paid stand-alone Base-level step (Development CapacityUnlock): a real, successful action that
        // is NOT a facility, a played card or a new building. It never satisfies a "facility is ready" demand.
        public bool CapacityUnlocked;
        public float ApSpent;
        public bool StateChanged;
        // DEV path plays a CardType.Facility card out of hand; the ECO extraction path is a
        // hero-built site with NO hand card.
        public bool CardPlayed;
        public int AdditionalCardsConsumed;
        // An operator may be manufactured by a DIFFERENT already staffed facility.
        // This is a single Challenge step, not a completed operator deployment.
        public bool GenerationAttempted;
        public bool Generated;
        public GenerationStep Generation;
        public CardData GeneratedOperatorCard;
        public int? BuilderArmyId;
        public int StateVersionAfter = -1;
        public string Detail;

        public static InfraFulfillResult No(string why) => new InfraFulfillResult { Detail = why };
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
        internal GenerationStep Generation;
        internal int CapacityExpectedLevel;
        // Set at admission: what a build funds through Trade first and how the log names the action.
        internal ResourceCost FundingCost;
        internal string Label;

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

        internal static InfraBuildAction OperatorGeneration(GenerationStep generation) =>
            new InfraBuildAction { Kind = InfraBuildKind.OperatorGeneration, Generation = generation };

        internal static InfraBuildAction CapacityUnlock(BuildingData building, int expectedLevel, HexCoord hex) =>
            new InfraBuildAction { Kind = InfraBuildKind.CapacityUnlock, UpgradeBuilding = building,
                CapacityExpectedLevel = expectedLevel, Hex = hex };
    }

    // ===========================================================================================
    //  INFRASTRUCTURE EXECUTION (2026-10-15: moved out of Strategy's InfrastructureFulfillment and
    //  StrategicMaintenancePolicy). Runs exactly the admitted action through the canonical gameplay
    //  primitives and reports what really happened; it never re-selects a card, hex or tier.
    // ===========================================================================================
    internal static class InfrastructureBuildExecutor
    {
        // One admitted infrastructure action (Phase A's infrastructure lane): a Challenge for a
        // missing operator, a stand-alone Base level, or a build funded through Trade first.
        internal static InfraFulfillResult Execute(InfraBuildAction a, SpendAuthority authority,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (a == null || player == null || root == null || ctx == null)
                return InfraFulfillResult.No("no infrastructure action");
            if (a.Kind == InfraBuildKind.OperatorGeneration)
            {
                int rateBefore = root.TradeRate;
                MaterializationResult g = MaterializationExecutor.ExecuteGeneration(a.Generation, player, root, hand, ctx);
                return new InfraFulfillResult
                {
                    ResourceConverted = root.TradeRate != rateBefore,
                    GenerationAttempted = g.GenerationAttempted, Generated = g.Generated,
                    Generation = a.Generation, GeneratedOperatorCard = g.GeneratedCard,
                    ApSpent = g.ApSpent, StateChanged = g.StateChanged, StateVersionAfter = g.StateVersionAfter,
                    Detail = g.Generated
                        ? $"operator {a.Generation.CardDef.displayName} generated into hand; deploy next pass"
                        : g.FailReason,
                };
            }
            // One payment + one mutation by the gameplay primitive, then ONE world receipt. No card
            // is played, no facility exists yet - the placement is a later action.
            if (a.Kind == InfraBuildKind.CapacityUnlock)
                return ExecuteCapacityUnlock(a, player, root, ctx);

            int rateBeforeFunding = root.TradeRate;
            if (!TradeFunding.Fund(player, root, ctx, a.FundingCost, authority))
                return new InfraFulfillResult { Detail = "infrastructure funding changed",
                    ResourceConverted = root.TradeRate != rateBeforeFunding,
                    StateChanged = root.TradeRate != rateBeforeFunding,
                    StateVersionAfter = root.TradeRate != rateBeforeFunding ? WorldDeltaLifecycle.Current : -1 };
            BuildingPlayResult r = Run(a, player, root, hand, ctx);
            bool converted = root.TradeRate != rateBeforeFunding;
            if (converted) { r.StateChanged = true; r.StateVersionAfter = WorldDeltaLifecycle.Current; }
            if (!r.Built)
            {
                AiDebugLog.Write($"[AI][V2]   infra - action rejected: {r.FailReason} ({a.Label})");
                return new InfraFulfillResult { ResourceConverted = converted, Built = false, StateChanged = r.StateChanged, ApSpent = r.ApSpent,
                    CardPlayed = r.CardConsumed,
                    StateVersionAfter = r.StateVersionAfter, Detail = r.FailReason };
            }
            return new InfraFulfillResult { ResourceConverted = converted, Built = true, ApSpent = r.ApSpent, StateChanged = r.StateChanged,
                CardPlayed = r.CardConsumed,
                AdditionalCardsConsumed = r.AdditionalCardsConsumed,
                BuilderArmyId = a.BuilderArmyId,
                StateVersionAfter = r.StateVersionAfter, Detail = a.Label };
        }

        private static InfraFulfillResult ExecuteCapacityUnlock(InfraBuildAction a, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx)
        {
            int rateBefore = root.TradeRate;
            BaseUpgradeOutcome outcome = UnlockCapacity(player, root, ctx,
                a.UpgradeBuilding, a.CapacityExpectedLevel, a.Hex, out int version);
            if (!outcome.Ok)
            {
                // The upgrade refused; preserve any conversion already committed before it.
                AiDebugLog.Write($"[AI][V2]   infra - CapacityUnlock refused: {outcome.FailReason} ({a.Label})");
                return new InfraFulfillResult { Detail = outcome.FailReason,
                    ResourceConverted = root.TradeRate != rateBefore, StateChanged = root.TradeRate != rateBefore,
                    StateVersionAfter = root.TradeRate != rateBefore ? WorldDeltaLifecycle.Current : -1 };
            }
            return new InfraFulfillResult
            {
                Built = false, CapacityUnlocked = true, CardPlayed = false,
                ApSpent = outcome.ApSpent,
                StateChanged = true, StateVersionAfter = version,
                Detail = $"{a.Label} -> level {outcome.LevelAfter}, facility slots {outcome.UnlockedSlotsAfter}",
            };
        }

        private static BuildingPlayResult Run(InfraBuildAction a, PlayerSetupData player, PlayerRoot root,
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
        private static BaseUpgradeOutcome UnlockCapacity(PlayerSetupData player, PlayerRoot root,
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

