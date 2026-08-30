using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ARMY REORG ANALYZER  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  LIVE post-Phase-B world -> immutable LocalForceGroup projections + executor back-maps.
    //  Reuses canonical signals instead of inventing a second source of truth:
    //    · role/protection   — AiArmyRoles / AviationRules / ActorCommitments
    //    · garrison safety   — AiConfig secure* floors, rechecked by AiArmyRoles at execution
    //    · strength/compo    — AiPower.PowerUnit from final live UnitData (Equipment already applied)
    //    · capacity ordering — the live ArmyData.Members order, because FIRST hero CommandRating wins
    //    · AP legality       — HasActivatedThisTurn + each member's effective ActivationApCost
    // ===========================================================================================
    public sealed class ArmyReorgAnalysis
    {
        public IReadOnlyList<LocalForceGroup> Groups;
        public IReadOnlyDictionary<int, UnitData> UnitByKey;
        public IReadOnlyDictionary<int, ArmyData> ArmyById;
    }

    public static class ArmyReorgAnalyzer
    {
        public static ArmyReorgAnalysis Analyze(PlayerSetupData player, ActorCommitments commitments)
        {
            var unitByKey = new Dictionary<int, UnitData>();
            var armyById = new Dictionary<int, ArmyData>();
            var groups = new List<LocalForceGroup>();
            if (player == null)
                return new ArmyReorgAnalysis { Groups = groups, UnitByKey = unitByKey, ArmyById = armyById };

            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            int nextKey = 0;

            var byHex = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null)
                .GroupBy(a => (a.Hex.Q, a.Hex.R))
                .OrderBy(g => g.Key.Q).ThenBy(g => g.Key.R);

            foreach (var hexGroup in byHex)
            {
                var containers = new List<ReorgContainer>();
                foreach (ArmyData army in hexGroup.OrderBy(a => a.Id))
                {
                    armyById[army.Id] = army;
                    containers.Add(BuildContainer(player, army, commitments, citadelHex, unitByKey, ref nextKey));
                }

                var lfg = new LocalForceGroup
                {
                    Q = hexGroup.Key.Q,
                    R = hexGroup.Key.R,
                    Containers = containers,
                };
                if (lfg.WorthPlanning())
                    groups.Add(lfg);
            }

            return new ArmyReorgAnalysis { Groups = groups, UnitByKey = unitByKey, ArmyById = armyById };
        }

        private static ReorgContainer BuildContainer(PlayerSetupData player, ArmyData army, ActorCommitments commitments,
            HexCoord citadelHex, Dictionary<int, UnitData> unitByKey, ref int nextKey)
        {
            // Preserve the canonical live roster order. ArmyData.ComputeCapacity uses the FIRST hero,
            // and AddMemberSorted deliberately preserves hero insertion order; re-sorting heroes by
            // power here can therefore make the pure planner disagree with gameplay capacity.
            var container = new ReorgContainer
            {
                ArmyId = army.Id,
                IsGarrison = army.IsGarrison,
                HasActivatedThisTurn = army.HasActivatedThisTurn,
            };

            foreach (UnitData u in army.Members)
            {
                int key = nextKey++;
                unitByKey[key] = u;
                AiPower.PowerUnit pu = AiPower.ToPowerUnit(u);
                container.Units.Add(new ReorgUnit
                {
                    Key = key,
                    IsHero = u.IsHero,
                    CommandRating = u.CommandRating,
                    Power = pu.BasePower,
                    Range = pu.Range,
                    TypeTags = pu.Tags.ToList(),
                    ActivationApCost = u.ActivationApCost,
                    HasRecce = AbilityParams.UnitHasAnyRecce(u),
                    IsAviation = u.IsAviation,
                    IsCommitted = false,
                });
            }

            container.Role = ClassifyRole(army, commitments);

            bool protectedOwner = container.Role == ReorgPhysicalRole.ProtectedMissionArmy;
            bool mutable = container.Role == ReorgPhysicalRole.NormalFieldArmy
                || container.Role == ReorgPhysicalRole.EmptyReusableArmy
                || container.Role == ReorgPhysicalRole.Garrison;

            container.CanChangeComposition = mutable && !protectedOwner;
            container.CanReceive = mutable && !protectedOwner;
            container.CanDonate = container.CanChangeComposition
                && (container.Role == ReorgPhysicalRole.NormalFieldArmy || container.Role == ReorgPhysicalRole.Garrison);

            // Empty reusable field containers are not permanently singleton-exempt: once the
            // virtual plan fills one, it is an ordinary field formation and must satisfy the same
            // structural rules as every other field army.
            container.SingletonExempt = container.Role != ReorgPhysicalRole.NormalFieldArmy
                && container.Role != ReorgPhysicalRole.EmptyReusableArmy;

            if (army.IsGarrison)
            {
                bool isCitadel = army.Hex.Equals(citadelHex);
                container.GarrisonNonHeroFloor = isCitadel
                    ? AiConfig.secureCitadelMinNonHeroUnits
                    : AiConfig.secureBaseMinNonHeroUnits;
            }

            return container;
        }

        private static ReorgPhysicalRole ClassifyRole(ArmyData army, ActorCommitments commitments)
        {
            if (army.IsGarrison)
                return ReorgPhysicalRole.Garrison;
            if (army.IsPrison)
                return ReorgPhysicalRole.SpecialExcludedContainer;
            if (AviationRules.IsAirfield(army) || AviationRules.IsAirArmy(army))
                return ReorgPhysicalRole.Aviation;
            if (commitments != null && commitments.IsArmyClaimed(army.Id))
                return ReorgPhysicalRole.ProtectedMissionArmy;
            if (AiArmyRoles.IsSoloRecce(army))
                return ReorgPhysicalRole.SoloRecce;
            if (army.Members.Count == 0)
                return ReorgPhysicalRole.EmptyReusableArmy;
            return ReorgPhysicalRole.NormalFieldArmy;
        }
    }
}
