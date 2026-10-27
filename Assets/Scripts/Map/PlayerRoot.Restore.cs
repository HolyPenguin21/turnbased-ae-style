using System;
using System.Collections.Generic;
using Game.Economy;

namespace Game.Map
{
    [Serializable] public sealed class PlayerEconomyState
    {
        public int ActionPoints;
        public int Human, Energy, Materials, Tech;
        public List<ResourceType> InitiativeContributions = new List<ResourceType>();
        public int LastApFromInitiative, LastApFromPrisonBonus, LastApFromApBonus;
        public string LastApBonusSources;
    }
    public partial class PlayerRoot
    {
        public PlayerEconomyState CaptureEconomyState() => new PlayerEconomyState
        {
            ActionPoints = _actionPoints,
            Human = _resources[ResourceType.Human], Energy = _resources[ResourceType.Energy],
            Materials = _resources[ResourceType.Materials], Tech = _resources[ResourceType.Tech],
            InitiativeContributions = new List<ResourceType>(_initiativeUnitContributions),
            LastApFromInitiative = LastApFromInitiative, LastApFromPrisonBonus = LastApFromPrisonBonus,
            LastApFromApBonus = LastApFromApBonus, LastApBonusSources = LastApBonusSources,
        };
        // Set stored facts directly: purchases would debit resources again; events would expose
        // a partially restored player. The caller refreshes UI after the complete world is bound.
        public void RestoreEconomyState(PlayerEconomyState state)
        {
            if (state?.InitiativeContributions == null) throw new ArgumentException("Missing economy state.");
            foreach (var resource in state.InitiativeContributions)
                if (!_resources.ContainsKey(resource)) throw new ArgumentException("Unknown initiative resource.");
            var contributions = new List<ResourceType>(state.InitiativeContributions);
            _actionPoints = state.ActionPoints;
            _resources[ResourceType.Human] = state.Human; _resources[ResourceType.Energy] = state.Energy;
            _resources[ResourceType.Materials] = state.Materials; _resources[ResourceType.Tech] = state.Tech;
            _initiativeUnitContributions.Clear(); _initiativeUnitContributions.AddRange(contributions);
            LastApFromInitiative = state.LastApFromInitiative; LastApFromPrisonBonus = state.LastApFromPrisonBonus;
            LastApFromApBonus = state.LastApFromApBonus; LastApBonusSources = state.LastApBonusSources ?? string.Empty;
        }
    }
}
