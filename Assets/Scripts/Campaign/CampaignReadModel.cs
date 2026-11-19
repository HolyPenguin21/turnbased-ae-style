using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Players;
using Game.Terrain;
using UnityEngine;

namespace Game.Campaign
{
    // A read model over a committed state. The controller always replaces that state on
    // commit, so old views remain stable and there is no mutable reference back to it.
    public sealed class CampaignReadModel
    {
        private readonly CampaignState state;
        private IReadOnlyList<CampaignRegionView> regions;
        private IReadOnlyList<CampaignBattleView> battles;
        private IReadOnlyList<CampaignFactionView> factions;

        internal CampaignReadModel(CampaignState state)
        {
            this.state = state;
            PendingOperation = state.PendingOperation == null ? null : new CampaignOperationView(state.PendingOperation);
            TurnOrder = state.TurnOrder.AsReadOnly();
        }

        public string CampaignId => state.CampaignId;
        public string PlanetName => state.PlanetName;
        public string PendingNotification => state.PendingNotification;
        public int BackgroundId => state.BackgroundId;
        public int RoundNumber => state.RoundNumber;
        public int RandomSequence => state.RandomSequence;
        public Faction HumanFaction => state.HumanFaction;
        public Faction CurrentFaction => state.CurrentFaction;
        public CampaignPhase Phase => state.Phase;
        public IReadOnlyList<Faction> TurnOrder { get; }
        public CampaignOperationView PendingOperation { get; }
        public IReadOnlyList<CampaignRegionView> Regions =>
            regions ??= state.Regions.ConvertAll(region => new CampaignRegionView(region)).AsReadOnly();
        public IReadOnlyList<CampaignBattleView> BattleHistory =>
            battles ??= state.BattleHistory.ConvertAll(battle => new CampaignBattleView(battle)).AsReadOnly();
        public IReadOnlyList<CampaignFactionView> Factions =>
            factions ??= state.Factions.ConvertAll(faction => new CampaignFactionView(faction)).AsReadOnly();

        public CampaignRegionView FindRegion(int id) => Regions.FirstOrDefault(region => region.RegionId == id);
        public CampaignBattleView FindBattle(string operationId)
        {
            var battle = state.BattleHistory.Find(entry => entry.OperationId == operationId);
            return battle == null ? null : new CampaignBattleView(battle);
        }

        public bool CanAttack(Faction faction, int sourceId, int targetId) =>
            CampaignRules.CanAttack(state, faction, sourceId, targetId);

        internal bool Matches(CampaignRegionOwnershipChange change) => change.Matches(state);
        internal IReadOnlyList<RegionState> CopyMapRegions() => state.Regions.ConvertAll(region => region.Copy());
    }

    public sealed class CampaignRegionView
    {
        private readonly RegionState region;
        internal CampaignRegionView(RegionState region)
        {
            this.region = region;
            PolygonVertices = region.PolygonVertices.AsReadOnly();
            NeighborIds = region.NeighborIds.AsReadOnly();
        }
        public IReadOnlyList<Vector2> PolygonVertices { get; }
        public IReadOnlyList<int> NeighborIds { get; }
        internal RegionState Copy() => region.Copy();
        public int RegionId => region.RegionId;
        public string Name => region.Name;
        public Faction OwnerFaction => region.OwnerFaction;
        public Biome Biome => region.Biome;
        public ResourceSpecialization Specialization => region.Specialization;
        public ResourceRichness Richness => region.Richness;
        public MatchWeatherProfile Weather => region.Weather;
        public RegionResourceConditions ResourceConditions => region.ResourceConditions;
    }

    public sealed class CampaignOperationView
    {
        private readonly CampaignOperation operation;
        internal CampaignOperationView(CampaignOperation operation) => this.operation = operation;
        public string OperationId => operation.OperationId;
        public string MatchId => operation.MatchId;
        public string SelectedHumanDeckId => operation.SelectedHumanDeckId;
        public string AttackerDeckName => operation.AttackerDeckName;
        public string DefenderDeckName => operation.DefenderDeckName;
        public Faction AttackerFaction => operation.AttackerFaction;
        public Faction DefenderFaction => operation.DefenderFaction;
        public int SourceRegionId => operation.SourceRegionId;
        public int TargetRegionId => operation.TargetRegionId;
        public int RandomSeed => operation.RandomSeed;
        public bool Manual => operation.Manual;
        public bool TestAutoResolve => operation.TestAutoResolve;
        public bool ResultRecorded => operation.ResultRecorded;
        public bool OwnershipApplied => operation.OwnershipApplied;
        public bool RewardAcknowledged => operation.RewardAcknowledged;
        public CampaignOutcome Outcome => operation.Outcome;
        public double AttackerScore => operation.AttackerScore;
        public double DefenderScore => operation.DefenderScore;
        public double WinChance => operation.WinChance;
        public double Roll => operation.Roll;
        public MatchWeatherProfile Weather => operation.Weather;
        public MatchWeatherConditions WeatherConditions => operation.WeatherConditions;
    }

    public sealed class CampaignBattleView
    {
        private readonly CampaignBattleRecord battle;
        internal CampaignBattleView(CampaignBattleRecord battle) => this.battle = battle;
        public string OperationId => battle.OperationId;
        public string MatchId => battle.MatchId;
        public string RegionName => battle.RegionName;
        public string AttackerDeckName => battle.AttackerDeckName;
        public string DefenderDeckName => battle.DefenderDeckName;
        public int Round => battle.Round;
        public int TargetRegionId => battle.TargetRegionId;
        public Faction Attacker => battle.Attacker;
        public Faction Defender => battle.Defender;
        public CampaignOutcome Outcome => battle.Outcome;
        public double WinChance => battle.WinChance;
        public bool Manual => battle.Manual;
        public bool Captured => battle.Captured;
    }

    public sealed class CampaignFactionView
    {
        private readonly CampaignFactionState faction;
        internal CampaignFactionView(CampaignFactionState faction) => this.faction = faction;
        public Faction Faction => faction.Faction;
        public bool Eliminated => faction.Eliminated;
        public string ActiveDeckReference => faction.ActiveDeckReference;
    }
}
