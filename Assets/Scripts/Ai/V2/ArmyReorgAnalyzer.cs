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
    //  Turns the LIVE post-Phase-B world into the immutable LocalForceGroup projections the pure
    //  planner consumes, plus the back-maps the executor needs (ReorgUnit.Key -> UnitData,
    //  ArmyId -> ArmyData). It composes existing canonical predicates and never invents a second
    //  source of truth:
    //    · physical role   — AiArmyRoles.IsSoloRecce / AviationRules / ArmyData flags
    //    · protection      — ActorCommitments.IsArmyClaimed (the canonical V2 ownership view,
    //                        rebuilt from the reconciled intent registry before Phase B)
    //    · garrison floor  — AiConfig.secure*MinNonHeroUnits (same numbers CanSpareGarrisonMember
    //                        and IsBaseGarrisonSecure already use)
    //    · strength        — AiPower.UnitPower (the shared V2 ranking scalar; folds in the final
    //                        Equipment-enhanced unit state automatically — no re-derivation)
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

            // One deterministic key stream for the whole pass — units ordered inside each army by
            // (hero first, then descending power, then name) so identical rosters get identical
            // keys run to run.
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
                    var container = BuildContainer(player, army, commitments, citadelHex, unitByKey, ref nextKey);
                    containers.Add(container);
                }

                var lfg = new LocalForceGroup
                {
                    Q = hexGroup.Key.Q,
                    R = hexGroup.Key.R,
                    Containers = containers, // already ArmyId-ordered
                };
                if (lfg.WorthPlanning())
                    groups.Add(lfg);
            }

            return new ArmyReorgAnalysis { Groups = groups, UnitByKey = unitByKey, ArmyById = armyById };
        }

        private static ReorgContainer BuildContainer(PlayerSetupData player, ArmyData army, ActorCommitments commitments,
            HexCoord citadelHex, Dictionary<int, UnitData> unitByKey, ref int nextKey)
        {
            var container = new ReorgContainer { ArmyId = army.Id, IsGarrison = army.IsGarrison };

            List<UnitData> ordered = army.Members
                .OrderByDescending(m => m.IsHero)
                .ThenByDescending(AiPower.UnitPower)
                .ThenBy(m => m.Name)
                .ToList();
            foreach (UnitData u in ordered)
            {
                int key = nextKey++;
                unitByKey[key] = u;
                container.Units.Add(new ReorgUnit
                {
                    Key = key,
                    IsHero = u.IsHero,
                    CommandRating = u.CommandRating,
                    Power = AiPower.UnitPower(u),
                    Range = u.Range,
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
            // A garrison / empty shell can only ever RECEIVE (nothing structural to donate from a
            // shell; a garrison donates only under the floor rule, handled in the planner).
            container.CanReceive = mutable && !protectedOwner;
            container.CanDonate = container.CanChangeComposition
                && (container.Role == ReorgPhysicalRole.NormalFieldArmy || container.Role == ReorgPhysicalRole.Garrison);

            container.SingletonExempt = container.Role != ReorgPhysicalRole.NormalFieldArmy;

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
