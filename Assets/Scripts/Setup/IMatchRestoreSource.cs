using System.Collections.Generic;
using Game.Campaign;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Turns;

namespace Game.Setup
{
    // Boundary for the future file codec. Construct a detached graph and validate every
    // reference BEFORE staging this source. It must be immutable once staged. This is not
    // a disk format: entity/objective codecs remain responsible for authored content and IDs.
    public interface IMatchRestoreSource
    {
        string MatchId { get; }
        bool RewardsEligible { get; }
        IReadOnlyList<PlayerSetupData> Players { get; }
        CampaignMatchContext CampaignContext { get; }
        HexMapLayout Map { get; }
        GameTurnState Turn { get; }
        MatchIdentityState Identity { get; }
        // Must be read-only: validate all world/AI references, identity highwater marks,
        // content keys, owner state and completed operations without registering anything.
        void Validate(GameConfig config);
        // Silent reconstruction only. Create roots, units, armies, buildings, events and
        // resource bonuses; restore relations/containers explicitly, not via gameplay actions.
        void RestoreWorld(HexMap map);
        // Restore fog, stealth, human sightings and AI history after all referenced entities
        // exist. Never infer historical knowledge by observing the reconstructed live map.
        void RestoreKnowledge();
        // Rebind frozen last-seen visuals after knowledge, including appearances of buildings
        // destroyed/captured behind fog. Never reconstruct these from current hidden entities.
        void RestorePresentation(HexSelectionController selection);
    }
}
