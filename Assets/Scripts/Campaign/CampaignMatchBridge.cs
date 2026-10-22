using System;
using System.Collections.Generic;
using Game.Core;
using Game.Players;
using Game.Progression;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Campaign
{
    public sealed class CampaignMatchContext
    {
        public string CampaignId, OperationId, MatchId, SelectedHumanDeckId;
        public Faction AttackerFaction, DefenderFaction, HumanFaction;
        public int SourceRegionId, TargetRegionId;
    }
    // Authoritative whole-match result, produced only at GameTurnController's game-over boundary.
    public readonly struct CompletedMatchResult
    {
        public readonly string MatchId;
        public readonly Faction WinnerFaction, LoserFaction;
        public readonly bool Draw;
        public CompletedMatchResult(string matchId, Faction winner, Faction loser, bool draw)
        { MatchId = matchId; WinnerFaction = winner; LoserFaction = loser; Draw = draw; }
    }
    public static class CampaignMatchBridge
    {
        public static CampaignController Controller { get; private set; }
        public static string Notice { get; private set; }
        // A manual battle may commit in Game before CampaignUI exists. Keep one live receipt across
        // that scene handoff, consume it once, and never reconstruct it from saved battle history.
        private static CampaignRegionOwnershipChange? pendingOwnershipChange;
        public static CampaignStore Store => new CampaignStore(Application.persistentDataPath);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { SetController(null); pendingOwnershipChange = null; Notice = null; }
        private static void SetController(CampaignController controller)
        {
            if (Controller != null) Controller.RegionOwnershipChanged -= RememberOwnershipChange;
            Controller = controller;
            if (Controller != null) Controller.RegionOwnershipChanged += RememberOwnershipChange;
        }
        private static void RememberOwnershipChange(CampaignRegionOwnershipChange change) => pendingOwnershipChange = change;
        public static bool TryTakeOwnershipChange(out CampaignRegionOwnershipChange change)
        {
            var pending = pendingOwnershipChange;
            pendingOwnershipChange = null;
            change = pending.GetValueOrDefault();
            return pending.HasValue && change.Matches(Controller?.View);
        }
        public static bool Load(out string error)
        {
            error = null; SetController(null);
            try
            {
                var store = Store;
                var s = store.Load(out string notice); Notice = notice;
                SetController(s == null ? null : new CampaignController(s, store));
                if (pendingOwnershipChange.HasValue && !pendingOwnershipChange.Value.Matches(s)) pendingOwnershipChange = null;
                return s != null;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        public static void Create(Faction human)
        {
            var state = new CampaignMapGenerator().Generate(unchecked((int)DateTime.UtcNow.Ticks), human);
            var store = Store;
            store.Save(state); pendingOwnershipChange = null; SetController(new CampaignController(state, store));
        }
        public static bool PrepareMatch(GameConfig config, string deckId, out string error)
        {
            error = null;
            try
            {
                if (Controller == null) throw new InvalidOperationException("Campaign is not loaded.");
                var s = Controller.View; var op = s.PendingOperation;
                if (op == null || !op.Manual || op.TestAutoResolve || op.ResultRecorded) throw new InvalidOperationException("No pending manual battle.");
                if (!ProgressionContext.Initialize(config)) throw new InvalidOperationException(ProgressionContext.Error);
                var collection = ProgressionContext.Collection;
                var deck = collection.GetDeck(deckId, s.HumanFaction);
                if (deck == null) throw new InvalidOperationException("Select a saved deck of your faction.");
                var validation = collection.Rules.Validate(deck, collection.Owned);
                if (!validation.IsValid) throw new InvalidOperationException(string.Join("\n", validation.Errors));
                var enemy = op.AttackerFaction == s.HumanFaction ? op.DefenderFaction : op.AttackerFaction;
                var players = new List<PlayerSetupData>();
                foreach (var faction in new[] { op.AttackerFaction, op.DefenderFaction })
                    players.Add(new PlayerSetupData { Nickname = faction.ToString(), Faction = faction, ColorIndex = players.Count, IsHuman = faction == s.HumanFaction, SelectedDeckId = faction == s.HumanFaction ? deckId : null });
                var enemyPlayer = players.Find(p => !p.IsHuman);
                string savedEnemyName = enemy == op.AttackerFaction ? op.AttackerDeckName : op.DefenderDeckName;
                enemyPlayer.AiDeckName = CampaignDeck.AiSource(enemy, collection.Rules, op.RandomSeed, savedEnemyName)?.deckName;
                if (!GameSession.TryPrepareMatch(players, config, out error)) return false;
                // A retry reuses the saved MatchId, only a fresh tactical world is created.
                Controller.RegisterMatch(deckId, deck.name, enemyPlayer.AiDeckName ?? collection.Rules.Starting.GetDeck(enemy)?.deckName ?? "Standard " + enemy);
                GameSession.Players = players;
                GameSession.SetCampaignContext(new CampaignMatchContext { CampaignId = s.CampaignId, OperationId = op.OperationId,
                    MatchId = op.MatchId, AttackerFaction = op.AttackerFaction, DefenderFaction = op.DefenderFaction, HumanFaction = s.HumanFaction,
                    SourceRegionId = op.SourceRegionId, TargetRegionId = op.TargetRegionId, SelectedHumanDeckId = deckId });
                var target = s.Regions.Find(r => r.RegionId == op.TargetRegionId);
                GameSession.SelectedMapSize = null;
                GameSession.SelectedBiome = target.Biome;
                GameSession.SelectedResourceConditions = target.ResourceConditions;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        public static bool RecordFinalResult(out string error)
        {
            error = null;
            if (GameSession.CampaignContext == null) return true;
            try
            {
                var context = GameSession.CampaignContext;
                var final = GameSession.FinalResult ?? throw new InvalidOperationException("Match has not finished.");
                if (final.MatchId != context.MatchId) throw new InvalidOperationException("Match identity mismatch.");
                if (Controller == null && !Load(out error)) return false;
                var state = Controller.View; var op = state.PendingOperation;
                if (state.CampaignId != context.CampaignId || state.HumanFaction != context.HumanFaction) throw new InvalidOperationException("Campaign context mismatch.");
                if (op?.OperationId == context.OperationId)
                {
                    if (op.MatchId != context.MatchId || op.AttackerFaction != context.AttackerFaction || op.DefenderFaction != context.DefenderFaction
                        || op.SourceRegionId != context.SourceRegionId || op.TargetRegionId != context.TargetRegionId || op.SelectedHumanDeckId != context.SelectedHumanDeckId)
                        throw new InvalidOperationException("Pending battle context mismatch.");
                }
                else
                {
                    var receipt = state.BattleHistory.Find(h => h.OperationId == context.OperationId);
                    if (receipt == null || !receipt.Manual || receipt.MatchId != context.MatchId || receipt.Attacker != context.AttackerFaction
                        || receipt.Defender != context.DefenderFaction || receipt.TargetRegionId != context.TargetRegionId) throw new InvalidOperationException("Completed battle context mismatch.");
                }
                if (!final.Draw && final.WinnerFaction != context.AttackerFaction && final.WinnerFaction != context.DefenderFaction) throw new InvalidOperationException("Winner is not a participant.");
                if (final.Draw ? final.WinnerFaction != Faction.None : final.LoserFaction != (final.WinnerFaction == context.AttackerFaction ? context.DefenderFaction : context.AttackerFaction))
                    throw new InvalidOperationException("Inconsistent whole-match outcome.");
                var outcome = final.Draw ? CampaignOutcome.Draw : final.WinnerFaction == context.AttackerFaction ? CampaignOutcome.AttackerVictory : CampaignOutcome.DefenderVictory;
                Controller.RecordManualResult(context.CampaignId, context.OperationId, context.MatchId, outcome); return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        // Called on recovery before UI continuation. RewardService's MatchId receipt reconciles files.
        public static bool RecoverReward(GameConfig config, out string error)
        {
            error = null;
            try
            {
                var s = Controller.View; var op = s.PendingOperation;
                if (op?.ResultRecorded != true) return true;
                if (!op.Manual || op.RewardAcknowledged) { Controller.ApplyResult(); return true; }
                if (!ProgressionContext.Initialize(config)) throw new InvalidOperationException(ProgressionContext.Error);
                if (op.Outcome == CampaignOutcome.Draw) { Controller.AcknowledgeReward(op.MatchId); return true; }
                var player = new PlayerSetupData { Faction = s.HumanFaction, IsHuman = true };
                bool victory = op.Outcome == CampaignOutcome.AttackerVictory ? op.AttackerFaction == s.HumanFaction : op.DefenderFaction == s.HumanFaction;
                if (!ProgressionContext.Rewards.Record(new ParticipantResult(op.MatchId, player, victory ? MatchOutcome.Victory : MatchOutcome.Defeat), out error)) return false;
                var profile = ProgressionContext.Collection.Snapshot;
                // A claimed receipt with no pending display means Dismiss already committed.
                if (profile.claimedMatchIds.Contains(op.MatchId) && !profile.pendingRewards.Exists(r => r.matchId == op.MatchId)) Controller.AcknowledgeReward(op.MatchId);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        public static bool RewardDismissed(string matchId, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(matchId)) return true;
            try
            {
                if (Controller?.View.PendingOperation?.MatchId == matchId) Controller.AcknowledgeReward(matchId);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        public static string ReturnScene(string matchId) => !string.IsNullOrWhiteSpace(matchId)
            && (Controller?.View.PendingOperation?.MatchId == matchId || GameSession.CampaignContext?.MatchId == matchId) ? SceneNames.Campaign : SceneNames.MainMenu;
    }
}

