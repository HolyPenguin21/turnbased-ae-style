using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // The "resource manager" the orchestrator draws from — turn-scoped, constructed fresh in
    // AiTurnController.RunTurn, wrapping the same four things the project owner named: the
    // player's own stockpiled resources/AP (Root), cards in hand (Hand), cards already deployed
    // into the garrison (GarrisonUnits, read-only — see its own comment), and armies (claimable,
    // so two goals competing in the same turn can't both grab the same idle actor).
    //
    // Deliberately goal-agnostic — no Economy-specific members — so Recon and Economy, which run
    // as independent AiTaskCategory pools over the same turn's armies rather than competing for
    // one pick (see AiTask's own class comment), can share this one pool instead of each keeping
    // a parallel claim-tracker: Decide claims every in-flight AiTask's own army up front, and each
    // "start a NEW task" candidate-gatherer (TryStartVisitCandidates, TryStartScoutResource
    // Candidates) reads AvailableArmies() to decide who's even eligible to propose a candidate for
    // — the actual ClaimArmy call itself is deferred to Decide's own Commit step, only for
    // whichever single candidate wins the step's arbitration (see AiTurnController's own Commit
    // comment on why generating a candidate must never itself claim anything). TryStartEconomy
    // Candidates' own preemption path is the one deliberate exception — it reaches past this
    // pool's claims entirely on purpose (see AiEconomyPlanner.FindNearestHero and
    // AiTurnController.TryStartEconomyCandidates's own comment). The card/garrison surface goes
    // mostly unused by all three today; it exists so the pool's shape doesn't need reworking once
    // something else needs it.
    public class AiResourcePool
    {
        private readonly HashSet<ArmyData> _claimedArmies = new HashSet<ArmyData>();
        private readonly HashSet<CardData> _claimedCards = new HashSet<CardData>();

        public PlayerSetupData Player { get; }
        public PlayerRoot Root { get; }
        public AiHandData Hand { get; }

        public AiResourcePool(PlayerSetupData player, PlayerRoot root, AiHandData hand)
        {
            Player = player;
            Root = root;
            Hand = hand;
        }

        public bool IsClaimed(ArmyData army) => army != null && _claimedArmies.Contains(army);

        public void ClaimArmy(ArmyData army)
        {
            if (army != null)
                _claimedArmies.Add(army);
        }

        public IEnumerable<ArmyData> AvailableArmies() =>
            ArmyRegistry.AllForOwner(Player).Where(a => !IsClaimed(a));

        public bool IsClaimed(CardData card) => card != null && _claimedCards.Contains(card);

        public void ClaimCard(CardData card)
        {
            if (card != null)
                _claimedCards.Add(card);
        }

        public IEnumerable<CardData> AvailableHandCards() =>
            Hand != null ? Hand.Hand.Where(c => !IsClaimed(c)) : Enumerable.Empty<CardData>();

        // Read-only visibility into whatever's already stockpiled in the garrison — nothing
        // claims or pulls from this yet, since no mechanic exists anywhere in the project to
        // move a unit out of one army and into another (confirmed absent — creating one is out
        // of scope for this pass: a bare hero is already a valid Экономика · Задача 1
        // composition, see AiArmyRoles.IsHeroLed).
        public IEnumerable<UnitData> GarrisonUnits() =>
            ArmyRegistry.AllForOwner(Player).Where(a => a.IsGarrison).SelectMany(a => a.Members);
    }
}
