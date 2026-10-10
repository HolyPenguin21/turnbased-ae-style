using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Players;

namespace Game.Campaign
{
    public static class CampaignRules
    {
        public static bool IsPlayable(Faction faction) => DeckRules.PlayableFactions.Contains(faction);
        public static bool HumanFinished(CampaignState state) => state.Factions.Find(f => f.Faction == state.HumanFaction).Eliminated || state.Factions.Count(f => !f.Eliminated) == 1;
        public static bool CanAttack(CampaignState state, Faction faction, int sourceId, int targetId)
        {
            if (state == null || state.Phase != CampaignPhase.AwaitingFactionAction || state.PendingOperation != null
                || HumanFinished(state) || state.CurrentFaction != faction || state.Factions.Find(f => f.Faction == faction)?.Eliminated != false) return false;
            return ValidPair(state, faction, sourceId, targetId);
        }
        internal static bool ValidPair(CampaignState state, Faction faction, int sourceId, int targetId)
        {
            var source = state.Regions.Find(r => r.RegionId == sourceId);
            var target = state.Regions.Find(r => r.RegionId == targetId);
            return source != null && target != null && source.OwnerFaction == faction && target.OwnerFaction != faction
                && source.NeighborIds.Contains(targetId) && state.Factions.Find(f => f.Faction == target.OwnerFaction)?.Eliminated == false;
        }
        public static IEnumerable<(int Source, int Target)> LegalAttacks(CampaignState state)
        {
            foreach (var source in state.Regions.Where(r => r.OwnerFaction == state.CurrentFaction))
                foreach (int target in source.NeighborIds.OrderBy(i => i))
                    if (CanAttack(state, state.CurrentFaction, source.RegionId, target)) yield return (source.RegionId, target);
        }
        // Controller transaction is the only caller. A receipt prevents a second capture or history entry.
        internal static void ApplyRecordedResult(CampaignState state)
        {
            var op = state.PendingOperation;
            if (op == null || !op.ResultRecorded || op.OwnershipApplied) return;
            var target = state.Regions.Find(r => r.RegionId == op.TargetRegionId);
            if (target.OwnerFaction != op.DefenderFaction) throw new InvalidOperationException("Operation owner mismatch.");
            if (op.Outcome == CampaignOutcome.AttackerVictory) target.OwnerFaction = op.AttackerFaction;
            op.OwnershipApplied = true;
            foreach (var faction in state.Factions)
                if (!state.Regions.Any(r => r.OwnerFaction == faction.Faction)) faction.Eliminated = true;
            state.BattleHistory.Add(new CampaignBattleRecord { OperationId = op.OperationId, MatchId = op.MatchId,
                Round = state.RoundNumber, TargetRegionId = target.RegionId, RegionName = target.Name,
                Attacker = op.AttackerFaction, Defender = op.DefenderFaction, Outcome = op.Outcome,
                AttackerDeckName = op.AttackerDeckName, DefenderDeckName = op.DefenderDeckName,
                WinChance = op.WinChance, Manual = op.Manual, Captured = op.Outcome == CampaignOutcome.AttackerVictory });
            state.PendingNotification = op.Outcome == CampaignOutcome.AttackerVictory
                ? $"{op.AttackerFaction} captured {target.Name}." : op.Outcome == CampaignOutcome.Draw
                ? $"Draw at {target.Name}. Ownership unchanged." : $"{op.DefenderFaction} defended {target.Name}.";
            var eliminated = state.Factions.Where(f => f.Eliminated && (f.Faction == op.AttackerFaction || f.Faction == op.DefenderFaction));
            foreach (var f in eliminated) state.PendingNotification += $"\n{f.Faction} have been eliminated.";
            state.Phase = CampaignPhase.ShowingResult;
        }
        internal static void Advance(CampaignState state)
        {
            if (HumanFinished(state)) { state.Phase = CampaignPhase.CampaignFinished; return; }
            do
            {
                state.CurrentTurnIndex++;
                if (state.CurrentTurnIndex >= state.TurnOrder.Count)
                {
                    state.RoundNumber++; state.CurrentTurnIndex = 0;
                    SetRoundOrder(state);
                }
            } while (state.Factions.Find(f => f.Faction == state.CurrentFaction).Eliminated);
            state.Phase = CampaignPhase.AwaitingFactionAction;
        }
        internal static void SetRoundOrder(CampaignState state)
        {
            state.TurnOrder = state.Factions.Where(f => !f.Eliminated).Select(f => f.Faction).ToList();
            var rng = new CampaignRandom(CampaignRandom.Derive(state.Seed, state.RandomSequence++));
            for (int i = state.TurnOrder.Count - 1; i > 0; i--) { int j = rng.Range(i + 1); var f = state.TurnOrder[i]; state.TurnOrder[i] = state.TurnOrder[j]; state.TurnOrder[j] = f; }
        }
    }
}
