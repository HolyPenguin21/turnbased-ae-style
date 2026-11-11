using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Game.Campaign;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Turns;
using UnityEngine;

namespace Game.Saves
{
    [Serializable] internal sealed class MatchSaveEnvelope
    {
        public int version;
        // schemaHash identifies the file structure (MatchSaveSchema); content values never do.
        public string format, schemaHash, matchId, savedUtc, summary, payloadSha256, payload;
    }
    [Serializable] internal sealed class MatchCampaignData
    {
        public string campaignId, operationId, matchId, selectedHumanDeckId;
        public Game.Players.Faction attacker, defender, human;
        public int sourceRegion, targetRegion;
        internal static MatchCampaignData Capture(CampaignMatchContext context) => context == null ? new MatchCampaignData() : new MatchCampaignData
        {
            campaignId = context.CampaignId, operationId = context.OperationId, matchId = context.MatchId,
            selectedHumanDeckId = context.SelectedHumanDeckId, attacker = context.AttackerFaction,
            defender = context.DefenderFaction, human = context.HumanFaction,
            sourceRegion = context.SourceRegionId, targetRegion = context.TargetRegionId,
        };
        internal CampaignMatchContext Restore() => new CampaignMatchContext
        {
            CampaignId = campaignId, OperationId = operationId, MatchId = matchId, SelectedHumanDeckId = selectedHumanDeckId,
            AttackerFaction = attacker, DefenderFaction = defender, HumanFaction = human,
            SourceRegionId = sourceRegion, TargetRegionId = targetRegion,
        };
    }
    [Serializable] internal sealed class MatchSaveData
    {
        public bool settledHumanTurn;
        public string matchId;
        public bool rewardsEligible, hasCampaign;
        public MatchCampaignData campaign;
        public HexMapLayout map;
        public GameTurnState turn;
        public MatchIdentityState identity;
        public MatchWorldData world;
        public MatchAiData ai;
        public MatchPresentationData presentation;
    }

    internal static class MatchSaveCodec
    {
        // Version 2: structure hash in place of the content fingerprint. Later structure changes
        // are detected by the hash alone; the number moves only with the envelope itself.
        internal const int SchemaVersion = 2;
        internal const string DifferentFormatMessage = "This save was created by a different version of the game and cannot be loaded.";
        internal const string Format = "turnbased-ae-match";
        private static UTF8Encoding Utf8 => new UTF8Encoding(false, true);

        internal static byte[] Capture(GameConfig config, HexMap map, GameTurnController turns, HexSelectionController selection)
        {
            if (config == null || map == null || turns == null || selection == null) throw new ArgumentException("Missing match capture owners.");
            if (!turns.CanSaveMatch(out string reason)) throw new InvalidOperationException(reason);
            var world = new MatchWorldCapture(config, GameSession.Players);
            var ai = MatchAiCodec.Capture(p => { world.IncludePlayer(p); return p.PlayerId; }, world.IncludeArmy, world.IncludeUnit, world.IncludeCard);
            var presentation = MatchPresentationCodec.Capture(selection, config, world.IncludeArmy, world.IncludeUnit, world.IncludeCard, world.IncludePlayer);
            var data = new MatchSaveData
            {
                settledHumanTurn = true, matchId = GameSession.MatchId, rewardsEligible = GameSession.RewardsEligible,
                hasCampaign = GameSession.CampaignContext != null, campaign = MatchCampaignData.Capture(GameSession.CampaignContext),
                map = map.Layout ?? throw new InvalidOperationException("The map layout is not available."),
                turn = turns.CaptureTurnState(), world = world.Finish(), ai = ai, presentation = presentation,
                identity = MatchIdentityState.Capture(),
            };
            string payload = MatchSaveJson.Serialize(data);
            var envelope = new MatchSaveEnvelope
            {
                version = SchemaVersion, format = Format, schemaHash = MatchSaveSchema.Hash,
                matchId = data.matchId, savedUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                summary = SlotSummary(data.turn.Round, turns.CurrentPlayer, GameSession.Players),
                payload = payload, payloadSha256 = Sha256(payload),
            };
            byte[] bytes = Utf8.GetBytes(MatchSaveJson.Serialize(envelope));
            // Exercise the real serializer and entire decoder before a slot can be replaced.
            Decode(bytes, config);
            if (!turns.CanSaveMatch(out reason)) throw new InvalidOperationException(reason);
            return bytes;
        }

        internal static IMatchRestoreSource Decode(byte[] bytes, GameConfig config)
        {
            var envelope = ReadEnvelope(bytes);
            // The checksum proves the payload is exactly what a game build wrote. If it still
            // fails here, it is incompatible with this build (content removed since, changed
            // rules), not damaged: like a newer version it must neither be quarantined nor be
            // silently replaced by an older backup (AtomicJsonFile).
            try
            {
                MatchSaveJson.Validate(envelope.payload, typeof(MatchSaveData));
                var data = MatchSaveJson.Deserialize<MatchSaveData>(envelope.payload);
                if (data == null || data.matchId != envelope.matchId) throw new ArgumentException("Saved match identity mismatch.");
                return new MatchFileRestoreSource(data, config);
            }
            catch (Exception ex) when (!(ex is NotSupportedException))
            {
                throw new NotSupportedException("This save cannot be loaded by this version of the game. " + ex.Message, ex);
            }
        }

        internal static MatchSaveEnvelope ReadEnvelope(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MatchSaveStore.MaximumFileBytes)
                throw new ArgumentException("The saved match file has an invalid size.");
            string json = Utf8.GetString(bytes);
            int version = MatchSaveJson.Version(json);
            if (version > SchemaVersion) throw new NotSupportedException("This save was created by a newer game version.");
            if (version != SchemaVersion) throw new NotSupportedException(DifferentFormatMessage);
            // Before the strict shape check: another structure is unsupported, not corrupt.
            if (MatchSaveJson.TopLevelText(json, "schemaHash") != MatchSaveSchema.Hash)
                throw new NotSupportedException(DifferentFormatMessage);
            MatchSaveJson.Validate(json, typeof(MatchSaveEnvelope));
            var envelope = MatchSaveJson.Deserialize<MatchSaveEnvelope>(json);
            if (envelope == null || envelope.format != Format || string.IsNullOrWhiteSpace(envelope.matchId)
                || envelope.schemaHash != MatchSaveSchema.Hash
                || string.IsNullOrEmpty(envelope.payload) || envelope.payloadSha256 != Sha256(envelope.payload)
                || !DateTimeOffset.TryParseExact(envelope.savedUtc, "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
                throw new ArgumentException("Invalid match save header or payload checksum.");
            return envelope;
        }
        internal static string Sha256(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Utf8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        // "Round N · <current human>" (+ " · Hotseat" with several humans).
        internal static string SlotSummary(int round, PlayerSetupData current, IEnumerable<PlayerSetupData> players)
        {
            string text = "Round " + round + " · " + (current?.Nickname ?? "Human");
            return players != null && players.Count(p => p != null && p.IsHuman) > 1 ? text + " · Hotseat" : text;
        }

        internal static MatchWorldGraph ValidateData(MatchSaveData data, GameConfig config)
        {
            if (data == null || config == null || !data.settledHumanTurn || string.IsNullOrWhiteSpace(data.matchId)
                || data.map == null || data.turn == null || data.identity == null || data.world == null || data.ai == null || data.presentation == null
                || data.campaign == null) throw new ArgumentException("Incomplete or unsettled saved match.");
            data.identity.Validate();
            HexMapGenerator.ValidateLayout(data.map, config);
            var graph = MatchWorldCodec.Decode(data.world, config, data.identity);
            graph.ValidateMap(data.map);
            // Hotseat saves carry several humans (the campaign exactly one). The settled turn is
            // a living human's; the saved perspective is that current human's.
            var humans = graph.Players.Where(p => p.IsHuman).ToArray();
            if (humans.Length == 0 || (data.hasCampaign && humans.Length != 1) || humans.Any(h => h.Hand == null))
                throw new ArgumentException("The save needs a living human and prepared human hands.");
            foreach (var player in graph.Players)
                if (!data.world.players.Single(p => p.id == player.PlayerId).hasEconomy)
                    throw new ArgumentException("A match participant is missing its saved economy.");
            var current = data.turn.ValidateHumanTurn(graph.Players);
            if (data.presentation.CurrentViewerId != current.PlayerId || data.presentation.Vision == null
                || humans.Any(h => !data.presentation.Vision.Any(v => v != null && v.PlayerId == h.PlayerId)))
                throw new ArgumentException("The saved human perspective and vision are missing or mismatched.");
            foreach (var human in humans)
            {
                if (human.MatchLoadout == null || human.BlueprintQuota == null || human.MatchLoadout.Faction != human.Faction)
                    throw new ArgumentException("The saved human loadout and blueprint quota are missing or mismatched.");
                var quota = human.BlueprintQuota.CaptureState();
                var expectedBlueprints = new Game.Cards.BlueprintQuota(human.MatchLoadout).CaptureState().unlockedKeys;
                if (quota.pendingKeys.Count != 0 || !quota.unlockedKeys.OrderBy(k => k, StringComparer.Ordinal)
                    .SequenceEqual(expectedBlueprints.OrderBy(k => k, StringComparer.Ordinal)))
                    throw new ArgumentException("The saved blueprint quota is unsettled or differs from the frozen loadout.");
            }
            // Eliminated slots can remain in this round's order. Every living participant
            // must still have a slot, and a finished match cannot claim a settled turn.
            var living = graph.Players.Where(p => !p.IsEliminated).ToArray();
            if (living.Length < 2 || living.Any(p => !data.turn.PlayerOrder.Contains(p.PlayerId)))
                throw new ArgumentException("The saved turn omits a living participant or the match has finished.");
            MatchAiCodec.Validate(data.ai, graph.Player, graph.Army, graph.Unit, graph.Card, config);
            ValidateHighWater(data.ai.HistoricalArmyIds, data.identity.ArmyHighWaterMark, 0);
            ValidateHighWater(data.ai.HistoricalUnitIds, data.identity.UnitHighWaterMark, 1);
            ValidateHighWater(data.ai.HistoricalCardIds, data.identity.CardHighWaterMark, 1);
            MatchPresentationCodec.Validate(data.presentation, config, graph.Player, data.map, data.identity);
            var campaign = data.hasCampaign ? data.campaign.Restore() : null;
            if (campaign != null && campaign.MatchId != data.matchId) throw new ArgumentException("Campaign match identity mismatch.");
            // Durable campaign progress can make an intact tactical save unavailable.
            // Do not classify that as corruption and load a different match from .bak.
            if (!CampaignMatchBridge.CanRestoreContext(campaign, out var error)) throw new NotSupportedException(error);
            return graph;
        }
        private static void ValidateHighWater(System.Collections.Generic.IEnumerable<int> ids, int high, int low)
        {
            if (ids == null || ids.Any(id => id < low || id > high)) throw new ArgumentException("Historical identity exceeds the saved issued range.");
        }
    }
}
