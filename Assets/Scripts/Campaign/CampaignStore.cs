using System;
using System.IO;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Terrain;
using UnityEngine;

namespace Game.Campaign
{
    public sealed class CampaignStore
    {
        private readonly string path;
        public CampaignStore(string directory) { path = Path.Combine(directory, "campaign-v1.json"); }
        public bool Exists => AtomicJsonFile.Exists(path);
        private sealed class ObsoleteCampaignException : NotSupportedException
        { public ObsoleteCampaignException() : base("Obsolete campaign schema. Start a new campaign.") { } }
        public CampaignState Load(out string notice)
        {
            try
            {
                var campaign = AtomicJsonFile.Load(path, Read, "Campaign restored from backup: ", out notice);
                if (!string.IsNullOrEmpty(notice))
                {
                    Debug.LogWarning(notice);
                    notice = "Campaign restored from backup.";
                }
                return campaign;
            }
            catch (ObsoleteCampaignException)
            {
                // Never recover an old schema from backup. Preserve a future-version file even
                // if the other file is obsolete. Only this campaign's pair may be discarded.
                CampaignState currentBackup = null;
                foreach (string file in new[] { path, path + ".bak" })
                    if (File.Exists(file))
                    {
                        int version;
                        try { version = SchemaOf(ReadJson(file)); }
                        catch (InvalidDataException) { continue; }
                        if (version > CampaignState.CurrentSchema)
                            throw new NotSupportedException("Campaign needs a newer game version.");
                        if (file == path + ".bak" && version == CampaignState.CurrentSchema)
                        {
                            try { currentBackup = Read(file); }
                            catch (InvalidDataException) { /* corrupt current backup cannot recover */ }
                        }
                    }
                if (currentBackup != null)
                {
                    // The obsolete primary does not authorize deleting a valid NEW-schema backup.
                    // Leave the backup intact for the next atomic save, as ordinary recovery does.
                    if (File.Exists(path)) File.Delete(path);
                    notice = "Campaign restored from backup.";
                    return currentBackup;
                }
                foreach (string file in new[] { path, path + ".bak" }) if (File.Exists(file)) File.Delete(file);
                notice = "Old campaign is incompatible. Start a new campaign. Your collection and decks are kept.";
                return null;
            }
        }
        [Serializable] private sealed class SchemaHeader { public int SchemaVersion; }
        private static string ReadJson(string file)
        {
            if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Campaign file too large.");
            return File.ReadAllText(file);
        }
        private static int SchemaOf(string json)
        {
            SchemaHeader header;
            try { header = JsonUtility.FromJson<SchemaHeader>(json); }
            catch (Exception ex) { throw new InvalidDataException("Invalid campaign JSON header.", ex); }
            if (header == null || header.SchemaVersion == 0) throw new InvalidDataException("Missing campaign schema.");
            return header.SchemaVersion;
        }
        private static void ValidateSchema(int version)
        {
            if (version > CampaignState.CurrentSchema) throw new NotSupportedException("Campaign needs a newer game version.");
            if (version == 1) throw new ObsoleteCampaignException();
            if (version != CampaignState.CurrentSchema) throw new InvalidDataException("Invalid campaign schema.");
        }
        private static CampaignState Read(string file)
        {
            string json = ReadJson(file);
            // Check the header BEFORE deserializing fields whose future/obsolete shape is unknown.
            ValidateSchema(SchemaOf(json));
            var state = CampaignState.Deserialize(json); Validate(state); return state;
        }
        public static void Validate(CampaignState s)
        {
            if (s == null) throw new InvalidDataException("Missing campaign state.");
            ValidateSchema(s.SchemaVersion);
            if (!Guid.TryParseExact(s.CampaignId, "N", out _) || string.IsNullOrWhiteSpace(s.PlanetName)) throw new InvalidDataException("Missing campaign identity.");
            if (!CampaignRules.IsPlayable(s.HumanFaction) || !Enum.IsDefined(typeof(CampaignPhase), s.Phase)
                || s.BackgroundId < 0 || s.BackgroundId > CampaignMapGenerator.BackgroundCount
                || s.RoundNumber < 1 || s.RandomSequence < 0 || s.TurnOrder == null || s.TurnOrder.Count < 1 || s.TurnOrder.Count > 3
                || s.TurnOrder.Distinct().Count() != s.TurnOrder.Count || s.TurnOrder.Any(f => !CampaignRules.IsPlayable(f)) || s.CurrentTurnIndex < 0 || s.CurrentTurnIndex >= s.TurnOrder.Count
                || s.Factions == null || s.Factions.Count != 3 || s.Factions.Any(f => f == null || !CampaignRules.IsPlayable(f.Faction) || f.ActiveDeckReference != "standard")
                || s.Factions.Select(f => f.Faction).Distinct().Count() != 3 || s.BattleHistory == null || s.BattleHistory.Count > 100000) throw new InvalidDataException("Invalid campaign state.");
            if (s.Factions.Where(f => !f.Eliminated).Any(f => !s.TurnOrder.Contains(f.Faction))) throw new InvalidDataException("Active faction missing from turn order.");
            CampaignGeometry.Validate(s.Regions);
            foreach (var region in s.Regions)
                if (!Enum.IsDefined(typeof(Biome), region.Biome)
                    || !Enum.IsDefined(typeof(ResourceSpecialization), region.Specialization)
                    || !Enum.IsDefined(typeof(ResourceRichness), region.Richness))
                    throw new InvalidDataException("Invalid region conditions.");
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
                || (op.Manual && s.BattleHistory.Any(h => h.MatchId == op.MatchId && h.OperationId != op.OperationId))
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
            Validate(s);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(s, true));
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Campaign save exceeds the supported size.");
            AtomicJsonFile.Save(path, bytes);
        }
    }
}
