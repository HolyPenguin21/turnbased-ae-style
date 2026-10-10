using System;
using System.IO;
using System.Linq;
using Game.Cards;
using UnityEngine;

namespace Game.Campaign
{
    public sealed class CampaignStore
    {
        private readonly string path;
        public CampaignStore(string directory) { path = Path.Combine(directory, "campaign-v1.json"); }
        public CampaignState Load(out string notice)
        {
            notice = null;
            if (!File.Exists(path) && !File.Exists(path + ".bak")) return null;
            try { return Read(path); }
            catch (NotSupportedException) { throw; }
            catch (Exception ex)
            {
                var restored = Read(path + ".bak");
                notice = "Campaign recovered from backup: " + ex.Message;
                if (File.Exists(path)) File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                // Keep the known-good backup during recovery's next atomic write.
                return restored;
            }
        }
        private static CampaignState Read(string file)
        {
            if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Campaign file too large.");
            var state = CampaignState.Deserialize(File.ReadAllText(file)); Validate(state); return state;
        }
        public static void Validate(CampaignState s)
        {
            if (s == null || !Guid.TryParseExact(s.CampaignId, "N", out _) || string.IsNullOrWhiteSpace(s.PlanetName)) throw new InvalidDataException("Missing campaign identity.");
            if (s.SchemaVersion > 1) throw new NotSupportedException("Campaign needs a newer game version.");
            if (s.SchemaVersion != 1 || !CampaignRules.IsPlayable(s.HumanFaction) || !Enum.IsDefined(typeof(CampaignPhase), s.Phase)
                || s.RoundNumber < 1 || s.RandomSequence < 0 || s.TurnOrder == null || s.TurnOrder.Count < 1 || s.TurnOrder.Count > 3
                || s.TurnOrder.Distinct().Count() != s.TurnOrder.Count || s.TurnOrder.Any(f => !CampaignRules.IsPlayable(f)) || s.CurrentTurnIndex < 0 || s.CurrentTurnIndex >= s.TurnOrder.Count
                || s.Factions == null || s.Factions.Count != 3 || s.Factions.Any(f => f == null || !CampaignRules.IsPlayable(f.Faction) || f.ActiveDeckReference != "standard")
                || s.Factions.Select(f => f.Faction).Distinct().Count() != 3 || s.BattleHistory == null || s.BattleHistory.Count > 100000) throw new InvalidDataException("Invalid campaign state.");
            if (s.Factions.Where(f => !f.Eliminated).Any(f => !s.TurnOrder.Contains(f.Faction))) throw new InvalidDataException("Active faction missing from turn order.");
            CampaignGeometry.Validate(s.Regions);
            foreach (var f in s.Factions) if (f.Eliminated == s.Regions.Any(r => r.OwnerFaction == f.Faction)) throw new InvalidDataException("Elimination mismatch.");
            if (s.BattleHistory.Any(h => h == null || !Guid.TryParseExact(h.OperationId, "N", out _) || h.Round < 1 || h.Round > s.RoundNumber
                || (h.Manual ? !Guid.TryParseExact(h.MatchId, "N", out _) : !string.IsNullOrEmpty(h.MatchId))
                || string.IsNullOrWhiteSpace(h.RegionName) || string.IsNullOrWhiteSpace(h.AttackerDeckName) || string.IsNullOrWhiteSpace(h.DefenderDeckName)
                || !s.Regions.Any(r => r.RegionId == h.TargetRegionId) || !CampaignRules.IsPlayable(h.Attacker) || !CampaignRules.IsPlayable(h.Defender)
                || h.Attacker == h.Defender || !Enum.IsDefined(typeof(CampaignOutcome), h.Outcome) || h.Captured != (h.Outcome == CampaignOutcome.AttackerVictory)
                || double.IsNaN(h.WinChance) || double.IsInfinity(h.WinChance) || h.WinChance < 0 || h.WinChance > 1)
                || s.BattleHistory.Select(h => h.OperationId).Distinct().Count() != s.BattleHistory.Count
                || s.BattleHistory.Where(h => h.Manual).Select(h => h.MatchId).Distinct().Count() != s.BattleHistory.Count(h => h.Manual)) throw new InvalidDataException("Invalid history.");
            bool finished = CampaignRules.HumanFinished(s);
            if ((s.Phase == CampaignPhase.CampaignFinished && !finished)
                || (finished && s.Phase != CampaignPhase.ShowingResult && s.Phase != CampaignPhase.CampaignFinished)
                || (s.Phase == CampaignPhase.AwaitingFactionAction && (finished || s.Factions.Find(f => f.Faction == s.CurrentFaction).Eliminated)))
                throw new InvalidDataException("Campaign completion/turn mismatch.");
            var op = s.PendingOperation;
            bool pendingPhase = s.Phase != CampaignPhase.AwaitingFactionAction && s.Phase != CampaignPhase.CampaignFinished;
            if (pendingPhase != (op != null)) throw new InvalidDataException("Phase/operation mismatch.");
            if (op == null)
            {
                if (!string.IsNullOrEmpty(s.PendingNotification)) throw new InvalidDataException("Notification has no operation.");
                return;
            }
            var source = s.Regions.Find(r => r.RegionId == op.SourceRegionId); var target = s.Regions.Find(r => r.RegionId == op.TargetRegionId);
            if (!Guid.TryParseExact(op.OperationId, "N", out _) || (op.Manual ? !Guid.TryParseExact(op.MatchId, "N", out _) : !string.IsNullOrEmpty(op.MatchId))
                || !CampaignRules.IsPlayable(op.AttackerFaction) || !CampaignRules.IsPlayable(op.DefenderFaction) || op.AttackerFaction == op.DefenderFaction
                || source == null || target == null || source.OwnerFaction != op.AttackerFaction || !source.NeighborIds.Contains(target.RegionId)
                || op.AttackerFaction != s.CurrentFaction || (op.TestAutoResolve && (!op.Manual || string.IsNullOrWhiteSpace(op.SelectedHumanDeckId))) || op.Manual != (op.AttackerFaction == s.HumanFaction || op.DefenderFaction == s.HumanFaction)
                || !Enum.IsDefined(typeof(CampaignOutcome), op.Outcome) || (op.OwnershipApplied && !op.ResultRecorded)
                || (op.RewardAcknowledged && !op.ResultRecorded)
                || (s.Phase == CampaignPhase.BattleInProgress && (!op.Manual || op.TestAutoResolve || string.IsNullOrWhiteSpace(op.SelectedHumanDeckId)
                    || string.IsNullOrWhiteSpace(op.AttackerDeckName) || string.IsNullOrWhiteSpace(op.DefenderDeckName)))
                || (op.OwnershipApplied && s.Phase != CampaignPhase.ShowingResult)
                || target.OwnerFaction != (op.OwnershipApplied && op.Outcome == CampaignOutcome.AttackerVictory ? op.AttackerFaction : op.DefenderFaction)
                || (op.ResultRecorded && (double.IsNaN(op.WinChance) || double.IsInfinity(op.WinChance) || op.WinChance < 0 || op.WinChance > 1 || double.IsNaN(op.Roll) || op.Roll < 0 || op.Roll >= 1
                    || double.IsNaN(op.AttackerScore) || double.IsInfinity(op.AttackerScore) || double.IsNaN(op.DefenderScore) || double.IsInfinity(op.DefenderScore)
                    || string.IsNullOrWhiteSpace(op.AttackerDeckName) || string.IsNullOrWhiteSpace(op.DefenderDeckName)))
                || op.OwnershipApplied != s.BattleHistory.Any(h => h.OperationId == op.OperationId)
                || (s.Phase == CampaignPhase.ShowingResult && (string.IsNullOrWhiteSpace(s.PendingNotification) || !op.OwnershipApplied || (op.Manual && !op.RewardAcknowledged)))
                || ((s.Phase == CampaignPhase.PreparingBattle || s.Phase == CampaignPhase.BattleInProgress) && op.ResultRecorded)
                || (s.Phase == CampaignPhase.BattleResolved && !op.ResultRecorded)) throw new InvalidDataException("Invalid pending operation.");
            var receipt = s.BattleHistory.Find(h => h.OperationId == op.OperationId);
            if (receipt != null && (receipt.MatchId != op.MatchId || receipt.Round != s.RoundNumber || receipt.TargetRegionId != op.TargetRegionId
                || receipt.Attacker != op.AttackerFaction || receipt.Defender != op.DefenderFaction || receipt.Outcome != op.Outcome
                || receipt.Manual != op.Manual || receipt.WinChance != op.WinChance || receipt.RegionName != target.Name
                || receipt.AttackerDeckName != op.AttackerDeckName || receipt.DefenderDeckName != op.DefenderDeckName)) throw new InvalidDataException("History/operation receipt mismatch.");
        }
        public void Save(CampaignState s)
        {
            Validate(s); Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + ".tmp";
            try
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(s, true));
                if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Campaign save exceeds the supported size.");
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
