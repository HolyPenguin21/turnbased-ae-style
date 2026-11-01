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
namespace Game.Turns
{
    public static class StrategicTurnEffects
    {
        private static readonly ResourceType[] AllResourceTypes = { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };
        private const int PrisonBonusActionPointsPerHero = 2;
        private const int ApBonusPerSource = UnitAbilities.ApBonusActionPointsPerSource;
        public static List<PlayerSetupData> PrepareRound(HexMap map, GameConfig config, List<PlayerSetupData> players)
        {
            var active = InitiativeRules.ActivePlayers(players);
            CollectResourceIncome(map, config); GrantProduceResourceIncome(players);
            foreach (var player in active) PlayerRootRegistry.FindFor(player)?.ResetBonusInitiativeTokens();
            return active;
        }
        public static void ApplyTurnOrder(List<PlayerSetupData> order, List<PlayerSetupData> players)
        {
            foreach (var player in players)
            {
                var root = PlayerRootRegistry.FindFor(player);
                if (root != null) InitiativePublicHistory.RecordFinalBonusTokens(player, root.BonusInitiativeTokens);
            }
            AllocateActionPoints(order); GrantPrisonBonusActionPoints(order); GrantApBonusActionPoints(order);
        }
        public static List<string> EndPlayer(PlayerSetupData player, HexSelectionController selection)
        {
            var messages = AviationTurnLifecycle.ResolveEndOfTurn(player, selection);
            if (player != null) { ArmyActions.DisbandEmptyArmiesOf(player); RegenerateForOwner(player); }
            return messages;
        }
        public static bool Eliminate(PlayerSetupData player, HexSelectionController selection)
        {
            if (player == null || player.IsEliminated) return false;
            player.IsEliminated = true; Game.Ai.AiMatchStats.RecordElimination(player);
            BuildingRegistry.DestroyFacilitiesOf(player, selection);
            if (player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue)
                ArmyActions.ReleasePrisoners(player, new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value));
            return true;
        }
        public static void CollectResourceIncome(HexMap map, GameConfig gameConfig)
        {
            if (map == null || gameConfig == null)
                return;
            IncomeProjection.ForEachHexCollectionGrant(map, (recipient, type, amount) =>
            {
                if (recipient != null && (recipient.Setup == null || !recipient.Setup.IsEliminated))
                    recipient.AddResource(type, amount);
            });
        }

        public static void GrantProduceResourceIncome(IReadOnlyList<PlayerSetupData> players)
        {
            if (players == null)
                return;
            foreach (PlayerSetupData player in players)
            {
                if (player == null || player.IsEliminated)
                    continue;
                PlayerRoot root = PlayerRootRegistry.FindFor(player);
                if (root == null)
                    continue;
                foreach (ResourceType type in AllResourceTypes)
                {
                    int sources = IncomeProjection.CountInPlayAbilitySources(
                        player, UnitAbilities.ProduceAbilityFor(type));
                    if (sources > 0)
                        root.AddResource(type, sources);
                }
            }
        }

        public static void AllocateActionPoints(List<PlayerSetupData> order)
        {
            for (int i = 0; i < order.Count; i++)
            {
                PlayerRoot root = PlayerRootRegistry.FindFor(order[i]);
                if (root != null)
                {
                    int rankAp = InitiativeRules.ApForRank(i);
                    root.ActionPoints = rankAp;
                    root.SetLastApFromInitiative(rankAp);
                }
            }
        }

        public static void GrantPrisonBonusActionPoints(List<PlayerSetupData> order)
        {
            foreach (PlayerSetupData player in order)
            {
                PlayerRoot root = PlayerRootRegistry.FindFor(player);
                if (root == null)
                    continue;
                int prisonerCount = ArmyRegistry.AllForOwner(player).Where(a => a.IsPrison).Sum(a => a.Members.Count);
                int bonus = PrisonBonusActionPointsPerHero * prisonerCount;
                root.SetLastApFromPrisonBonus(bonus);
                if (bonus > 0)
                    root.ActionPoints += bonus;
            }
        }

        public static void GrantApBonusActionPoints(List<PlayerSetupData> order)
        {
            foreach (PlayerSetupData player in order)
            {
                PlayerRoot root = PlayerRootRegistry.FindFor(player);
                if (root == null)
                    continue;

                int sources = 0;
                List<string> breakdown = new List<string>();
                foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
                {
                    if (army.IsPrison)
                        continue;
                    foreach (UnitData unit in army.Members)
                    {
                        if (!unit.HasAbility(UnitAbilities.ApBonus))
                            continue;
                        sources++;
                        breakdown.Add($"{unit.Name} +{ApBonusPerSource}");
                    }
                }
                foreach (BuildingData building in BuildingRegistry.AllBuildings())
                {
                    if (building.Owner != player)
                        continue;
                    if (building.HasAbility(UnitAbilities.ApBonus))
                    {
                        sources++;
                        breakdown.Add($"Base at ({building.Hex.Q},{building.Hex.R}) +{ApBonusPerSource}");
                    }
                    foreach (FacilityData facility in building.FacilitySlots)
                        if (facility != null && facility.HasAbility(UnitAbilities.ApBonus))
                        {
                            sources++;
                            breakdown.Add($"{facility.Name} at ({building.Hex.Q},{building.Hex.R}) +{ApBonusPerSource}");
                        }
                }

                int bonus = ApBonusPerSource * sources;
                root.SetLastApFromApBonus(bonus);
                root.SetLastApBonusSources(string.Join(", ", breakdown));
                if (bonus > 0)
                    root.ActionPoints += bonus;
            }
        }

        public static void RegenerateForOwner(PlayerSetupData player)
        {
            if (player == null)
                return;
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army.IsPrison)
                    continue;
                foreach (UnitData unit in army.Members)
                    if (unit.HasAbility(UnitAbilities.Regeneration)
                        && unit.HitPointsCurrent > 0 && unit.HitPointsCurrent < unit.HitPointsMax)
                    {
                        unit.HitPointsCurrent++;
                        VisionSystem.NotifyContentChanged(army.Hex);
                    }
            }
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != player)
                    continue;
                if (building.HasAbility(UnitAbilities.Regeneration)
                    && building.StructurePointsCurrent > 0 && building.StructurePointsCurrent < building.StructurePointsMax)
                {
                    building.StructurePointsCurrent++;
                    VisionSystem.NotifyContentChanged(building.Hex);
                }
            }
        }

        public static void BeginPlayer(PlayerSetupData player)
        {
            using var mutation = VisionSystem.BeginMutation();
            AntiAirRules.SyncFacilityGunnerOwners();
            AntiAirState.ResetForOwner(player);
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                foreach (UnitData unit in army.Members)
                {
                    unit.ReplenishMoveForNewTurn();
                    unit.HasEnteredStealthThisTurn = false;
                    if (unit.IsAviation)
                        unit.HasAirAttackedThisTurn = false;
                }
                army.ResetActivationForNewTurn();
                army.RefreshTransport();
                VisionSystem.NotifyContentChanged(army.Hex);
            }
            // Memory may have expired at this player's preceding turn end even when no enemy
            // moved. Re-observe the complete visible area after turn mutations, before UI refresh.
            VisionSystem.PublishAfterMutation(() => MapObservationSystem.ObserveVisible(player));
        }

        public static bool StartingCitadelLost(PlayerSetupData player)
        {
            if (!player.CitadelHexQ.HasValue || !player.CitadelHexR.HasValue)
                return false;
            var hex = new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value);
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building == null || building.Owner != player;
        }
    }
}
