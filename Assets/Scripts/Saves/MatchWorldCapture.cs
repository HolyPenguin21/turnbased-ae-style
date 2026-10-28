using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Saves
{
    internal sealed class MatchWorldCapture
    {
        private readonly MatchWorldData data = new MatchWorldData();
        private readonly Dictionary<string, PlayerSetupData> players = new Dictionary<string, PlayerSetupData>(StringComparer.Ordinal);
        private readonly Dictionary<int, ArmyData> armies = new Dictionary<int, ArmyData>();
        private readonly Dictionary<int, UnitData> units = new Dictionary<int, UnitData>();
        private readonly Dictionary<int, CardData> cards = new Dictionary<int, CardData>();
        private readonly MatchContentResolver content;
        private bool finished;
        internal MatchWorldCapture(GameConfig config, IReadOnlyList<PlayerSetupData> matchPlayers)
        {
            content = new MatchContentResolver(config);
            if (matchPlayers == null) throw new ArgumentNullException(nameof(matchPlayers));
            foreach (var player in matchPlayers) IncludePlayer(player);
            foreach (var army in ArmyRegistry.AllArmies()) IncludeArmy(army);
            foreach (var building in BuildingRegistry.AllBuildings())
            {
                IncludePlayer(building.Owner);
                var saved = new MatchBuildingData
                {
                    q = building.Hex.Q, r = building.Hex.R, name = building.Name, owner = PlayerId(building.Owner),
                    markerPrefab = MatchSaveAssets.PrefabKey(building.Visual?.PersistenceSourcePrefab),
                    markerIcon = MatchSaveAssets.SpriteKey(building.Visual?.PersistenceIcon),
                    art = MatchSaveAssets.SpriteKey(building.Art), detailArt = MatchSaveAssets.SpriteKey(building.DetailArt),
                    // Some ability consumers select the first matching entry (for example AA/AA2).
                    abilities = building.Abilities.ToList(), isBase = building.IsBase,
                    startingCitadel = building.IsStartingCitadel, tiered = building.HasTieredUnlock, level = building.Level,
                    hp = building.StructurePointsCurrent, hpMax = building.StructurePointsMax, defense = building.Defense,
                    fate = building.Fate, airfieldCapacity = building.AirfieldCapacity,
                };
                foreach (var f in building.FacilitySlots)
                    saved.facilities.Add(f == null ? new MatchFacilityData() : new MatchFacilityData
                    {
                        occupied = true, name = f.Name, art = MatchSaveAssets.SpriteKey(f.Art), detailArt = MatchSaveAssets.SpriteKey(f.DetailArt),
                        upgradeLevel = f.UpgradeLevel, hp = f.StructurePointsCurrent, hpMax = f.StructurePointsMax,
                        defense = f.Defense, fate = f.Fate, attack = f.Attack, antiAirRadius = f.AntiAirRadius,
                        abilities = f.Abilities.ToList(),
                    });
                data.buildings.Add(saved);
            }
            foreach (var bonus in HexResourceBonusRegistry.CaptureState()) data.bonuses.Add(new MatchBonusData { q = bonus.Hex.Q, r = bonus.Hex.R, bonus = bonus.Bonus });
            foreach (var e in HexEventRegistry.CaptureEntries((hex, entry) => CaptureEvent(hex, entry))) data.events.Add(e.Payload);
            foreach (var battle in DelayedBattleRegistry.CaptureState(IncludeArmy)) data.battles.Add(new MatchBattleData { q = battle.Hex.Q, r = battle.Hex.R, armies = battle.ParticipantArmyIds });
            var aa = AntiAirState.CaptureState(IncludeUnit);
            data.antiAir.fired.AddRange(aa.FiredUnitIds); data.antiAir.prompted.AddRange(aa.Prompted);
            foreach (var gunner in aa.FacilityGunners) data.antiAir.gunners.Add(new MatchGunnerData { q = gunner.BuildingHex.Q, r = gunner.BuildingHex.R, slot = gunner.Slot, unitId = gunner.UnitId });
            data.stealth = StealthSystem.CaptureState(IncludeUnit);
        }
        private static string PlayerId(PlayerSetupData player) => player == null ? null : StateReference.PlayerId(player);
        internal void IncludePlayer(PlayerSetupData player)
        {
            if (player == null) throw new InvalidOperationException("An entity has no owner.");
            string id = StateReference.PlayerId(player);
            if (players.TryGetValue(id, out var previous))
            { if (!ReferenceEquals(previous, player)) throw new InvalidOperationException("Non-canonical player: " + id); return; }
            players.Add(id, player);
            var saved = new MatchPlayerData
            {
                id = id, nickname = player.Nickname, colorIndex = player.ColorIndex, faction = player.Faction,
                human = player.IsHuman, neutral = player.IsNeutral, eliminated = player.IsEliminated,
                selectedDeckId = player.SelectedDeckId, aiDeckName = player.AiDeckName, aiGrantedLeaderKey = player.AiGrantedLeaderKey,
                hasCitadel = player.CitadelHexQ.HasValue, citadelQ = player.CitadelHexQ ?? 0, citadelR = player.CitadelHexR ?? 0,
                hasAiBlueprints = player.AiBlueprints != null, aiBlueprints = player.AiBlueprints?.OrderBy(x => x, StringComparer.Ordinal).ToList() ?? new List<string>(),
                hasLoadout = player.MatchLoadout != null, loadout = player.MatchLoadout?.CaptureState(),
                hasQuota = player.BlueprintQuota != null, quota = player.BlueprintQuota?.CaptureState(), tacticsVersion = player.Tactics.Version,
            };
            if (player.CitadelHexQ.HasValue != player.CitadelHexR.HasValue) throw new InvalidOperationException("Partial citadel coordinate.");
            data.players.Add(saved);
            var hand = player.IsHuman ? player.Hand : AiHandRegistry.Peek(player);
            if (!player.IsHuman && player.Hand != null && !ReferenceEquals(player.Hand, hand)) throw new InvalidOperationException("AI has conflicting hand owners.");
            if (hand != null)
            {
                saved.hasHand = true;
                saved.hand = new MatchHandData { ai = hand is AiHandData, capacity = hand.Capacity, currentTurn = hand.CurrentTurn, mutationVersion = hand.MutationVersion };
                foreach (var card in hand.Hand) { IncludeCard(card); saved.hand.cards.Add(card.RuntimeId); }
                foreach (var card in hand.RemainingDeck) saved.hand.deck.Add(content.Key(card));
            }
            foreach (var card in player.Tactics.Cards) { IncludeCard(card); saved.tactics.Add(card.RuntimeId); }
            var root = PlayerRootRegistry.FindFor(player);
            saved.hasEconomy = root != null; saved.economy = root?.CaptureEconomyState();
        }
        internal void IncludeCard(CardData card)
        {
            if (card == null) throw new InvalidOperationException("Missing referenced card.");
            if (cards.TryGetValue(card.RuntimeId, out var old))
            { if (!ReferenceEquals(old, card)) throw new InvalidOperationException("Non-canonical card identity."); return; }
            cards.Add(card.RuntimeId, card);
            var state = CardState.Capture(card);
            content.Card(state.definitionKey); if (!string.IsNullOrEmpty(state.equipmentKey)) content.Card(state.equipmentKey); if (!string.IsNullOrEmpty(state.mutatorKey)) content.Card(state.mutatorKey);
            data.cards.Add(state);
        }
        internal void IncludeArmy(ArmyData army)
        {
            if (army == null) throw new InvalidOperationException("Missing referenced army.");
            if (armies.TryGetValue(army.Id, out var old))
            { if (!ReferenceEquals(old, army)) throw new InvalidOperationException("Non-canonical army identity."); return; }
            armies.Add(army.Id, army); IncludePlayer(army.Owner);
            var saved = new MatchArmyData
            {
                id = army.Id, q = army.Hex.Q, r = army.Hex.R, name = army.Name, owner = PlayerId(army.Owner), registered = ArmyRegistry.IsRegistered(army),
                markerPrefab = MatchSaveAssets.PrefabKey(army.Controller?.Visual?.PersistenceSourcePrefab),
                markerIcon = MatchSaveAssets.SpriteKey(army.Controller?.Visual?.PersistenceIcon),
                garrison = army.IsGarrison, prison = army.IsPrison, airfield = army.IsAirfield, airArmy = army.IsAirArmy, wingLanded = army.AirWingLanded,
                carrierUnit = army.CarrierUnit?.RuntimeId ?? -1, launchCarrier = army.LaunchCarrier?.RuntimeId ?? -1,
                hasLastStrike = army.LastAirStrikeHex.HasValue, lastStrikeQ = army.LastAirStrikeHex?.Q ?? 0, lastStrikeR = army.LastAirStrikeHex?.R ?? 0,
                lastStrikeAttacked = army.LastAirStrikeAttacked, hasStrikePolicy = army.PendingAirStrikePolicy.HasValue,
            };
            data.armies.Add(saved);
            if (army.PendingAirStrikePolicy.HasValue)
            {
                var p = army.PendingAirStrikePolicy.Value; saved.strikeKind = (int)p.Kind; saved.hasStrikeTarget = p.ExactTargetArmyId.HasValue;
                saved.strikeTarget = p.ExactTargetArmyId ?? 0; saved.minimumSurvivors = p.MinimumSurvivors;
            }
            if (army.CarrierUnit != null) IncludeUnit(army.CarrierUnit);
            if (army.LaunchCarrier != null) IncludeUnit(army.LaunchCarrier);
            foreach (var member in army.Members) { IncludeUnit(member); saved.members.Add(member.RuntimeId); }
            foreach (var pair in army.SavedArrangement)
            { IncludeUnit(pair.Key); saved.arrangement.Add(new MatchArrangementData { unitId = pair.Key.RuntimeId, row = pair.Value.row, col = pair.Value.col }); }
            saved.activation = army.CaptureActivationState(IncludeUnit);
        }
        internal void IncludeUnit(UnitData unit)
        {
            if (unit == null) throw new InvalidOperationException("Missing referenced unit.");
            if (units.TryGetValue(unit.RuntimeId, out var old))
            { if (!ReferenceEquals(old, unit)) throw new InvalidOperationException("Non-canonical unit identity."); return; }
            units.Add(unit.RuntimeId, unit); IncludePlayer(unit.Owner); if (unit.CapturedFrom != null) IncludePlayer(unit.CapturedFrom);
            var movement = unit.CaptureMovementState(); var links = unit.CaptureCarrierLinksState();
            data.units.Add(new MatchUnitData
            {
                id = unit.RuntimeId, name = unit.Name, owner = PlayerId(unit.Owner), capturedFrom = PlayerId(unit.CapturedFrom),
                origin = content.OptionalKey(unit.OriginatingCard), equipment = content.OptionalKey(unit.Equipment), mutator = content.OptionalKey(unit.Mutator),
                art = MatchSaveAssets.SpriteKey(unit.Art), detailArt = MatchSaveAssets.SpriteKey(unit.DetailArt), abilities = unit.Abilities.ToList(), typeTags = unit.TypeTags.OrderBy(x => x).ToList(),
                hidden = unit.IsHidden, enteredStealth = unit.HasEnteredStealthThisTurn, hero = unit.IsHero, prisoner = unit.IsPrisoner, summoned = unit.IsSummoned, aviation = unit.IsAviation,
                row = unit.Row, attack = unit.Attack, defense = unit.Defense, range = unit.Range, hpMax = unit.HitPointsMax, hp = unit.HitPointsCurrent,
                initiative = unit.Initiative, fateMax = unit.FateMax, fate = unit.Fate, commandRating = unit.CommandRating,
                activationApCost = unit.ActivationApCost, apCost = unit.ApCost, berserkStacks = unit.BerserkStacks, berserkDefenseLost = unit.BerserkDefenseLost, drillAttackBonus = unit.DrillAttackBonus,
                moveMax = movement.PermanentMaximum, legacyMove = movement.LegacyCurrent, hasSpent = movement.Spent.HasValue, spent = (int?)movement.Spent ?? 0,
                transportCarrier = (int?)movement.TransportCarrierId ?? -1, transportArmy = (int?)movement.TransportArmyId ?? -1, airfieldArmy = (int?)links.AirfieldArmyId ?? -1, flightDeckArmy = (int?)links.FlightDeckArmyId ?? -1,
                launchEnergyCost = unit.LaunchEnergyCost, turnsWithoutRefuel = unit.TurnsWithoutRefuel, antiAirRadius = unit.AntiAirRadius, airfieldCapacity = unit.AirfieldCapacity, unlandedEnds = unit.ConsecutiveUnlandedEnds,
                emergencyPenalty = unit.HasEmergencyFlightPenalty, airAttacked = unit.HasAirAttackedThisTurn, sortiePaid = unit.SortieLaunchPaid,
                hasOriginalCost = unit.OriginalResourceCost != null, originalCost = CopyCost(unit.OriginalResourceCost), hasRepairCost = unit.RepairResourceCost != null, repairCost = CopyCost(unit.RepairResourceCost),
                hasAttachmentBase = unit.AttachmentBase.HasValue, attachmentBase = CaptureAttachment(unit.AttachmentBase), hasAttachmentApplied = unit.AttachmentApplied.HasValue, attachmentApplied = CaptureAttachment(unit.AttachmentApplied),
                hasAttachmentResources = unit.AttachmentResources.HasValue, attachmentResources = CaptureResources(unit.AttachmentResources),
            });
            if (unit.AssignedTransportCarrier != null) IncludeUnit(unit.AssignedTransportCarrier);
            if (unit.TransportArmy != null) IncludeArmy(unit.TransportArmy);
            if (unit.CarrierAirfield != null) IncludeArmy(unit.CarrierAirfield);
            if (unit.CarrierFlightDeck != null) IncludeArmy(unit.CarrierFlightDeck);
        }
        private MatchEventData CaptureEvent(HexCoord hex, HexEventRegistry.Entry entry)
        {
            if (entry.GuardOwner != null) IncludePlayer(entry.GuardOwner); if (entry.ConsumedBy != null) IncludePlayer(entry.ConsumedBy);
            var result = new MatchEventData
            {
                q = hex.Q, r = hex.R, definition = MatchSaveAssets.EventKey(entry.Definition), guardName = entry.GuardArmyName, guardOwner = PlayerId(entry.GuardOwner),
                consumedBy = PlayerId(entry.ConsumedBy), consumed = entry.Consumed, triggered = entry.Triggered, rewardPending = entry.CollectionRewardPending,
                rewardError = entry.CollectionRewardError, skipped = entry.Skipped,
            };
            foreach (var pair in entry.ResolvedGuardMembers) result.guard.Add(new DeckCardEntry { cardKey = content.Key(pair.card), count = pair.count });
            foreach (var p in entry.DiscoveredBy) { IncludePlayer(p); result.discovered.Add(PlayerId(p)); }
            foreach (var p in entry.KnownBy) { IncludePlayer(p); result.known.Add(PlayerId(p)); }
            var rewardIndices = new Dictionary<RewardEntry, int>();
            int RewardIndex(RewardEntry reward)
            {
                if (reward == null) throw new InvalidOperationException("Missing event reward.");
                if (rewardIndices.TryGetValue(reward, out int index)) return index;
                index = result.rewards.Count; rewardIndices.Add(reward, index);
                result.rewards.Add(new RewardEntry { type = reward.type, cardKey = reward.cardKey, resources = CopyYields(reward.resources) });
                return index;
            }
            foreach (var reward in entry.SelectedRewards) result.selectedRewards.Add(RewardIndex(reward));
            foreach (var pair in entry.ResolvedCardRewards)
            {
                // A claimed Library creates a private receipt reward outside SelectedRewards.
                result.resolvedRewards.Add(new MatchResolvedRewardData { rewardIndex = RewardIndex(pair.reward), card = content.Key(pair.card) });
            }
            return result;
        }
        internal MatchWorldData Finish()
        {
            if (finished) throw new InvalidOperationException("World capture is already complete.");
            finished = true; return data;
        }
        internal static ResourceCost CopyCost(ResourceCost x) => x == null ? null : new ResourceCost { human = x.human, energy = x.energy, materials = x.materials, tech = x.tech };
        internal static Game.Economy.ResourceYields CopyYields(Game.Economy.ResourceYields x) => x == null ? null : new Game.Economy.ResourceYields { human = x.human, energy = x.energy, materials = x.materials, tech = x.tech };
        private static MatchAttachmentData CaptureAttachment(PredictedEquipmentState? state)
        {
            if (!state.HasValue) return null;
            var saved = new MatchAttachmentData { abilities = state.Value.Abilities.ToList() };
            foreach (var pair in state.Value.Stats.OrderBy(x => x.Key)) saved.stats.Add(new EquipmentStatChange { stat = pair.Key, amount = pair.Value });
            return saved;
        }
        private static MatchAttachmentResourceData CaptureResources(AttachmentResourceState? state)
        {
            if (!state.HasValue) return null;
            var x = state.Value; return new MatchAttachmentResourceData { hpSpent = x.HpSpent, moveSpent = x.MoveSpent, fateSpent = x.FateSpent, lastHp = x.LastHp, lastMove = x.LastMove, lastFate = x.LastFate };
        }
    }
    internal sealed class MatchContentResolver
    {
        private readonly Dictionary<string, CardDefinition> cards = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
        internal MatchContentResolver(GameConfig config)
        {
            if (config == null || config.collectionDeckCatalog == null) throw new InvalidOperationException("Match save requires the configured collection catalog.");
            foreach (var catalog in config.collectionDeckCatalog.catalogs)
                if (catalog != null) foreach (var card in catalog.cards) Add(card);
            // Extractor definitions predate authored keys and are never runtime card instances.
            // Their placed facilities persist the complete mutated stat/art block instead.
            if (config.extractionFacilityCards != null) foreach (var card in config.extractionFacilityCards)
                if (card != null && !string.IsNullOrWhiteSpace(card.authoredKey)) Add(card);
        }
        private void Add(CardDefinition card)
        {
            if (card == null) return;
            if (string.IsNullOrWhiteSpace(card.authoredKey)) throw new InvalidOperationException("Content has no authored card identity.");
            if (cards.TryGetValue(card.authoredKey, out var previous))
            { if (!ReferenceEquals(previous, card)) throw new InvalidOperationException("Duplicate authored card: " + card.authoredKey); return; }
            cards.Add(card.authoredKey, card);
        }
        internal CardDefinition Card(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !cards.TryGetValue(key, out var card)) throw new InvalidOperationException("Unknown saved card: " + key);
            return card;
        }
        internal CardDefinition OptionalCard(string key) => string.IsNullOrEmpty(key) ? null : Card(key);
        internal string Key(CardDefinition card)
        { if (card == null || !ReferenceEquals(Card(card.authoredKey), card)) throw new InvalidOperationException("Non-canonical authored card."); return card.authoredKey; }
        internal string OptionalKey(CardDefinition card) => card == null ? null : Key(card);
    }
}
