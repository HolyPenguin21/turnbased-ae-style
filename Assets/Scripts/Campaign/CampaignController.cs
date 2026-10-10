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
        public CampaignPhase Phase => state.Phase;
        public bool IsAiTurn => state.CurrentFaction != state.HumanFaction;
        public CampaignController(CampaignState initial, CampaignStore store) { CampaignStore.Validate(initial); state = initial.Copy(); this.store = store; }
        private void Transact(Action<CampaignState> edit)
        { var next = state.Copy(); edit(next); store.Save(next); state = next; }
        public void BeginAttack(int sourceId, int targetId, bool manual)
        {
            if (!CampaignRules.CanAttack(state, state.CurrentFaction, sourceId, targetId)) throw new InvalidOperationException("Illegal campaign attack.");
            var target = state.Regions.Find(r => r.RegionId == targetId);
            if (manual != (state.CurrentFaction == state.HumanFaction || target.OwnerFaction == state.HumanFaction)) throw new InvalidOperationException("Battle mode mismatch.");
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
                || (state.Phase != CampaignPhase.PreparingBattle && state.Phase != CampaignPhase.BattleInProgress)) throw new InvalidOperationException("No interrupted or prepared battle.");
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
            if (state.CampaignId != campaignId) throw new InvalidOperationException("Result identity mismatch.");
            // Continue clears the pending slot, but the durable receipt still identifies a
            // late duplicate callback. It must not interrupt the next faction's operation.
            var completed = state.BattleHistory.Find(h => h.OperationId == operationId);
            if (completed != null)
            {
                if (!completed.Manual || completed.MatchId != matchId || completed.Outcome != outcome) throw new InvalidOperationException("Conflicting completed match result.");
                return;
            }
            var op = state.PendingOperation;
            if (op == null || !op.Manual || op.OperationId != operationId || op.MatchId != matchId) throw new InvalidOperationException("Result identity mismatch.");
            if (op.ResultRecorded) { if (op.Outcome != outcome) throw new InvalidOperationException("Conflicting match result."); return; }
            if (state.Phase != CampaignPhase.BattleInProgress) throw new InvalidOperationException("Battle has not started.");
            Transact(s => { s.PendingOperation.Outcome = outcome; s.PendingOperation.ResultRecorded = true; s.Phase = CampaignPhase.BattleResolved; });
        }
        public void SelectTestDeck(string deckId)
        {
            if (state.Phase != CampaignPhase.PreparingBattle || state.PendingOperation?.Manual != true || string.IsNullOrWhiteSpace(deckId)) throw new InvalidOperationException("No test deck to select.");
            Transact(s => s.PendingOperation.SelectedHumanDeckId = deckId);
        }
        public void ResolveHumanTest(CampaignDeck attacker, CampaignDeck defender, CampaignBattleResolver resolver)
        {
            if (state.Phase != CampaignPhase.PreparingBattle || state.PendingOperation?.Manual != true || state.PendingOperation.ResultRecorded)
                throw new InvalidOperationException("No human battle prepared for test resolution.");
            Transact(s => s.PendingOperation.TestAutoResolve = true);
            ResolveFast(attacker, defender, resolver);
        }
        public void ResolveFast(CampaignDeck attacker, CampaignDeck defender, CampaignBattleResolver resolver)
        {
            var op = state.PendingOperation;
            if (op == null || (op.Manual && !op.TestAutoResolve) || state.Phase != CampaignPhase.PreparingBattle || op.ResultRecorded) throw new InvalidOperationException("No fast operation to resolve.");
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
            if (op == null || !op.ResultRecorded || op.MatchId != matchId) throw new InvalidOperationException("No completed campaign match.");
            if (!op.RewardAcknowledged) Transact(s => s.PendingOperation.RewardAcknowledged = true);
            ApplyResult();
        }
        public void ApplyResult()
        {
            var op = state.PendingOperation;
            if (op?.ResultRecorded != true || (op.Manual && !op.RewardAcknowledged) || op.OwnershipApplied) return;
            Transact(CampaignRules.ApplyRecordedResult);
        }
        public void Continue(string operationId)
        {
            if (state.Phase != CampaignPhase.ShowingResult || state.PendingOperation?.OperationId != operationId) return;
            Transact(s => { s.PendingOperation = null; s.PendingNotification = null; CampaignRules.Advance(s); });
        }
        public void EndTurn()
        {
            if (state.Phase != CampaignPhase.AwaitingFactionAction || state.PendingOperation != null || CampaignRules.HumanFinished(state)) throw new InvalidOperationException("Turn is blocked.");
            Transact(CampaignRules.Advance);
        }
        public void TakeAiTurn(DeckRules rules, CampaignBattleResolver resolver)
        {
            if (state.CurrentFaction == state.HumanFaction) throw new InvalidOperationException("Human turn.");
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
            if (!manual) ResolveFast(attacker, CampaignDeck.Standard(state.PendingOperation.DefenderFaction, rules, state.Factions.Find(f => f.Faction == state.PendingOperation.DefenderFaction).ActiveDeckReference), resolver);
        }
    }
}
