using System;
using System.Collections.Generic;
using System.Linq;
using Game.Campaign;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Turns;
using UnityEngine;

namespace Game.Saves
{
    // Owns its decoded DTOs privately. No file buffers or caller-owned mutable DTOs survive
    // staging; exposed value records are copies. Entities become this timeline's canonical
    // graph when the coordinator consumes the source once.
    internal sealed class MatchFileRestoreSource : IMatchRestoreSource
    {
        private readonly MatchSaveData data;
        private readonly MatchWorldGraph graph;
        private readonly GameConfig config;
        private HexMap restoredMap;
        private bool consumed,worldRestored,knowledgeRestored,presentationRestored;
        internal MatchFileRestoreSource(MatchSaveData data, GameConfig config)
        {
            this.data = data;
            this.config = config;
            graph = MatchSaveCodec.ValidateData(data, config);
        }
        public string MatchId => data.matchId;
        public bool RewardsEligible => data.rewardsEligible;
        public IReadOnlyList<PlayerSetupData> Players => graph.Players;
        public CampaignMatchContext CampaignContext => data.hasCampaign ? data.campaign.Restore() : null;
        public HexMapLayout Map => data.map.Copy();
        public GameTurnState Turn => MatchSaveJson.Deserialize<GameTurnState>(MatchSaveJson.Serialize(data.turn));
        public MatchIdentityState Identity => new MatchIdentityState
        {
            ArmyHighWaterMark = data.identity.ArmyHighWaterMark, UnitHighWaterMark = data.identity.UnitHighWaterMark,
            CardHighWaterMark = data.identity.CardHighWaterMark,
        };
        public void Validate(GameConfig requestedConfig)
        {
            if (requestedConfig != config) throw new ArgumentException("Saved match configuration changed after validation.");
            if (consumed) throw new InvalidOperationException("This saved match source has already been consumed.");
            // Revalidate current campaign/profile compatibility before replacing the scene.
            if (!CampaignMatchBridge.CanRestoreContext(CampaignContext, out var error)) throw new ArgumentException(error);
        }
        public void RestoreWorld(HexMap map)
        {
            if (!MatchRestoreScope.IsRestoring || consumed) throw new InvalidOperationException("Invalid world restore stage.");
            consumed=true;
            graph.RestoreWorld(map); restoredMap = map; worldRestored = true;
        }
        public void RestoreKnowledge()
        {
            if (!worldRestored || knowledgeRestored || !MatchRestoreScope.IsRestoring) throw new InvalidOperationException("Invalid knowledge restore stage.");
            graph.RestoreKnowledge();
            MatchPresentationCodec.RestoreKnowledge(data.presentation, config, graph.Player);
            MatchAiCodec.Restore(data.ai, graph.Player, graph.Army, graph.Unit, graph.Card, restoredMap);
            knowledgeRestored = true;
        }
        public void RestorePresentation(HexSelectionController selection)
        {
            if (!knowledgeRestored || presentationRestored || !MatchRestoreScope.IsRestoring) throw new InvalidOperationException("Invalid presentation restore stage.");
            graph.BindPresentation(restoredMap);
            MatchPresentationCodec.RestorePresentation(data.presentation, selection, config, graph.Player);
            presentationRestored = true;
        }
    }
}
