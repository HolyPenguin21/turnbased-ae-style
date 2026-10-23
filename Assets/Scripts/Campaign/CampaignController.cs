using System;
using System.Linq;
using Game.Cards;
using Game.Players;

namespace Game.Campaign
{
    // All state mutations cross one save-before-publish boundary. A failed write consumes nothing.
    public sealed class CampaignController
    {
        private CampaignState state;
        private readonly CampaignStore store;
        public CampaignState Snapshot => state.Copy();
        // Read-only current state without a JSON round trip. Transact swaps in a new object and never edits
        // this one in place, so a held reference stays a frozen snapshot; callers must not mutate it.
        public CampaignState View => state;
        public CampaignPhase Phase => state.Phase;
        public bool IsAiTurn => state.CurrentFaction != state.HumanFaction;
        // Transient, post-commit fact. Loading a snapshot never publishes historical captures.
        public event Action<CampaignRegionOwnershipChange> RegionOwnershipChanged;
        public CampaignController(CampaignState initial, CampaignStore store) { CampaignStore.Validate(initial); state = initial.Copy(); this.store = store; }
        private void Transact(Action<CampaignState> edit)
        { var next = state.Copy(); edit(next); store.Save(next); state = next; }
        public void BeginAttack(int sourceId, int targetId, bool manual)
        {
            if (!CampaignRules.CanAttack(state, state.CurrentFaction, sourceId, targetId)) throw new InvalidOperationException("You cannot attack this region now.");
            var target = state.Regions.Find(r => r.RegionId == targetId);
            if (manual != (state.CurrentFaction == state.HumanFaction || target.OwnerFaction == state.HumanFaction)) throw new InvalidOperationException("This battle cannot use the selected mode.");
            Transact(s =>
            {
                s.PendingOperation = new CampaignOperation { OperationId = Guid.NewGuid().ToString("N"), MatchId = manual ? Guid.NewGuid().ToString("N") : null,
                    AttackerFaction = s.CurrentFaction, DefenderFaction = target.OwnerFaction, SourceRegionId = sourceId, TargetRegionId = targetId,
                    RandomSeed = CampaignRandom.Derive(s.Seed, s.RandomSequence++), Manual = manual };
                s.Phase = CampaignPhase.PreparingBattle;
            });
        }
        public void CancelHumanAttack()
        {
            if (state.Phase != CampaignPhase.PreparingBattle || state.PendingOperation?.AttackerFaction != state.HumanFaction) return;
            Transact(s => { s.PendingOperation = null; s.Phase = CampaignPhase.AwaitingFactionAction; });
        }
        public void RegisterMatch(string deckId, string humanDeckName, string enemyDeckName)
        {
            if (state.PendingOperation?.Manual != true || state.PendingOperation.TestAutoResolve || state.PendingOperation.ResultRecorded
                || (state.Phase != CampaignPhase.PreparingBattle && state.Phase != CampaignPhase.BattleInProgress)) throw new InvalidOperationException("No battle is available to resume.");
            Transact(s =>
            {
                var op = s.PendingOperation; op.SelectedHumanDeckId = deckId;
                op.AttackerDeckName = op.AttackerFaction == s.HumanFaction ? humanDeckName : enemyDeckName;
                op.DefenderDeckName = op.DefenderFaction == s.HumanFaction ? humanDeckName : enemyDeckName;
                s.Phase = CampaignPhase.BattleInProgress;
            });
        }
        public void RecordManualResult(string campaignId, string operationId, string matchId, CampaignOutcome outcome)
        {
            if (state.CampaignId != campaignId) throw new InvalidOperationException("This result does not belong to the current campaign battle.");
            // Continue clears the pending slot, but the durable receipt still identifies a
            // late duplicate callback. It must not interrupt the next faction's operation.
            var completed = state.BattleHistory.Find(h => h.OperationId == operationId);
            if (completed != null)
            {
                if (!completed.Manual || completed.MatchId != matchId || completed.Outcome != outcome) throw new InvalidOperationException("This battle already has a different saved result.");
                return;
            }
            var op = state.PendingOperation;
            if (op == null || !op.Manual || op.OperationId != operationId || op.MatchId != matchId) throw new InvalidOperationException("This result does not belong to the current campaign battle.");
            if (op.ResultRecorded) { if (op.Outcome != outcome) throw new InvalidOperationException("This battle already has a different saved result."); return; }
            if (state.Phase != CampaignPhase.BattleInProgress) throw new InvalidOperationException("Battle has not started.");
            Transact(s => { s.PendingOperation.Outcome = outcome; s.PendingOperation.ResultRecorded = true; s.Phase = CampaignPhase.BattleResolved; });
        }
        public void SelectTestDeck(string deckId)
        {
            if (state.Phase != CampaignPhase.PreparingBattle || state.PendingOperation?.Manual != true || string.IsNullOrWhiteSpace(deckId)) throw new InvalidOperationException("No battle is ready for deck selection.");
            Transact(s => s.PendingOperation.SelectedHumanDeckId = deckId);
        }
        public void ResolveHumanTest(CampaignDeck attacker, CampaignDeck defender, CampaignBattleResolver resolver)
        {
            if (state.Phase != CampaignPhase.PreparingBattle || state.PendingOperation?.Manual != true || state.PendingOperation.ResultRecorded)
                throw new InvalidOperationException("No player battle is ready to resolve.");
            Transact(s => s.PendingOperation.TestAutoResolve = true);
            ResolveFast(attacker, defender, resolver);
        }
        public void ResolveFast(CampaignDeck attacker, CampaignDeck defender, CampaignBattleResolver resolver)
        {
            var op = state.PendingOperation;
            if (op == null || (op.Manual && !op.TestAutoResolve) || state.Phase != CampaignPhase.PreparingBattle || op.ResultRecorded) throw new InvalidOperationException("No battle is ready to resolve automatically.");
            var result = resolver.Resolve(attacker, defender, op.RandomSeed);
            Transact(s =>
            {
                var p = s.PendingOperation; p.ResultRecorded = true; p.Outcome = result.Outcome;
                p.AttackerScore = result.Attacker.Total; p.DefenderScore = result.Defender.Total; p.WinChance = result.WinChance; p.Roll = result.Roll;
                p.AttackerDeckName = attacker.Name; p.DefenderDeckName = defender.Name; p.RewardAcknowledged = true;
                s.Phase = CampaignPhase.BattleResolved;
            });
            ApplyResult();
        }
        public void AcknowledgeReward(string matchId)
        {
            var op = state.PendingOperation;
            if (op == null || !op.ResultRecorded || op.MatchId != matchId) throw new InvalidOperationException("No completed campaign battle is available.");
            if (!op.RewardAcknowledged) Transact(s => s.PendingOperation.RewardAcknowledged = true);
            ApplyResult();
        }
        public void ApplyResult()
        {
            var op = state.PendingOperation;
            if (op?.ResultRecorded != true || (op.Manual && !op.RewardAcknowledged) || op.OwnershipApplied) return;
            var previousOwner = state.Regions.Find(r => r.RegionId == op.TargetRegionId).OwnerFaction;
            Transact(CampaignRules.ApplyRecordedResult);
            var newOwner = state.Regions.Find(r => r.RegionId == op.TargetRegionId).OwnerFaction;
            if (previousOwner != newOwner)
                RegionOwnershipChanged?.Invoke(new CampaignRegionOwnershipChange(state.CampaignId, op.OperationId,
                    op.SourceRegionId, op.TargetRegionId, previousOwner, newOwner));
        }
        public void Continue(string operationId)
        {
            if (state.Phase != CampaignPhase.ShowingResult || state.PendingOperation?.OperationId != operationId) return;
            Transact(s => { s.PendingOperation = null; s.PendingNotification = null; CampaignRules.Advance(s); });
        }
        public void EndTurn()
        {
            if (state.Phase != CampaignPhase.AwaitingFactionAction || state.PendingOperation != null || CampaignRules.HumanFinished(state)) throw new InvalidOperationException("Finish the current campaign action first.");
            Transact(CampaignRules.Advance);
        }
        // UI can present the already selected/persisted operation before resolution.
        // Default preserves the atomic convenience flow for existing callers/tests.
        public void TakeAiTurn(DeckRules rules, CampaignBattleResolver resolver, bool resolveImmediately = true)
        {
            if (state.CurrentFaction == state.HumanFaction) throw new InvalidOperationException("Wait for the player to finish their turn.");
            var attacks = CampaignRules.LegalAttacks(state).ToList();
            if (attacks.Count == 0) { EndTurn(); return; }
            var attacker = CampaignDeck.Standard(state.CurrentFaction, rules, state.Factions.Find(f => f.Faction == state.CurrentFaction).ActiveDeckReference);
            var rng = new CampaignRandom(CampaignRandom.Derive(state.Seed ^ unchecked((int)0x6a09e667), state.RandomSequence));
            // Public map + standard faction deck estimates only. No tactical reads or replanning.
            var selected = attacks.Select(pair =>
            {
                var target = state.Regions.Find(r => r.RegionId == pair.Target);
                double chance = resolver.Compare(attacker, CampaignDeck.Standard(target.OwnerFaction, rules, state.Factions.Find(f => f.Faction == target.OwnerFaction).ActiveDeckReference)).WinChance;
                double elimination = state.Regions.Count(r => r.OwnerFaction == target.OwnerFaction) == 1 ? .08 : 0;
                return (Pair: pair, Score: chance + elimination + rng.Value() * .06);
            }).OrderByDescending(a => a.Score).First().Pair;
            bool manual = state.Regions.Find(r => r.RegionId == selected.Target).OwnerFaction == state.HumanFaction;
            BeginAttack(selected.Source, selected.Target, manual);
            if (!manual && resolveImmediately)
            {
                var op = state.PendingOperation;
                ResolveFast(CampaignDeck.Ai(op.AttackerFaction, rules, op.RandomSeed, op.AttackerDeckName),
                    CampaignDeck.Ai(op.DefenderFaction, rules, op.RandomSeed, op.DefenderDeckName), resolver);
            }
        }
    }
}

