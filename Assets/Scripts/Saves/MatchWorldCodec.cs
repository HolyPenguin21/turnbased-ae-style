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
using Game.Setup;
using Game.Styles;
using Game.Units;
using UnityEngine;

namespace Game.Saves
{
    internal static class MatchWorldCodec
    {
        internal static MatchWorldGraph Decode(MatchWorldData data, GameConfig config, MatchIdentityState identity)
            => new MatchWorldGraph(data, config, identity);
    }
    internal sealed class MatchWorldGraph
    {
        private readonly Dictionary<string, PlayerSetupData> players = new Dictionary<string, PlayerSetupData>(StringComparer.Ordinal);
        private readonly Dictionary<int, ArmyData> armies = new Dictionary<int, ArmyData>();
        private readonly Dictionary<int, UnitData> units = new Dictionary<int, UnitData>();
        private readonly Dictionary<int, CardData> cards = new Dictionary<int, CardData>();
        private readonly Dictionary<HexCoord, BuildingData> buildings = new Dictionary<HexCoord, BuildingData>();
        private readonly Dictionary<PlayerSetupData, PlayerEconomyState> economies = new Dictionary<PlayerSetupData, PlayerEconomyState>();
        private readonly List<ArmyData> registered = new List<ArmyData>();
        private readonly Dictionary<ArmyData, MapObjectVisual> armyPrefabs = new Dictionary<ArmyData, MapObjectVisual>();
        private readonly Dictionary<BuildingData, MapObjectVisual> buildingPrefabs = new Dictionary<BuildingData, MapObjectVisual>();
        private readonly Dictionary<ArmyData, Sprite> armyIcons = new Dictionary<ArmyData, Sprite>();
        private readonly Dictionary<BuildingData, Sprite> buildingIcons = new Dictionary<BuildingData, Sprite>();
        private readonly List<HexResourceBonusState> bonuses = new List<HexResourceBonusState>();
        private readonly List<HexEventEntryState<HexEventRegistry.Entry>> events = new List<HexEventEntryState<HexEventRegistry.Entry>>();
        private readonly List<DelayedBattleState> battles = new List<DelayedBattleState>();
        private readonly AntiAirTurnState aa = new AntiAirTurnState();
        private readonly StealthState stealth;
        private readonly MatchIdentityState identity;
        private readonly GameConfig config;
        private readonly MatchContentResolver content;
        private bool worldRestored;
        internal IReadOnlyList<PlayerSetupData> Players { get; }
        internal PlayerSetupData Player(string id) => id != null && players.TryGetValue(id, out var p) ? p : null;
        internal ArmyData Army(int id) => armies.TryGetValue(id, out var a) ? a : null;
        internal UnitData Unit(int id) => units.TryGetValue(id, out var u) ? u : null;
        internal CardData Card(int id) => cards.TryGetValue(id, out var c) ? c : null;
        internal MatchWorldGraph(MatchWorldData data, GameConfig config, MatchIdentityState identity)
        {
            if (data == null || identity == null) throw Invalid("Missing world or identity.");
            identity.Validate(); this.identity = new MatchIdentityState { ArmyHighWaterMark = identity.ArmyHighWaterMark, UnitHighWaterMark = identity.UnitHighWaterMark, CardHighWaterMark = identity.CardHighWaterMark };
            this.config = config; content = new MatchContentResolver(config);
            Require(data.players); Require(data.cards); Require(data.armies); Require(data.units); Require(data.buildings); Require(data.bonuses); Require(data.events); Require(data.battles);
            var active = new List<PlayerSetupData>(); int humans = 0, neutrals = 0;
            foreach (var x in data.players)
            {
                if (x == null || string.IsNullOrWhiteSpace(x.id) || players.ContainsKey(x.id) || x.colorIndex < 0 || x.colorIndex >= PlayerColorPalette.Colors.Length
                    || !Enum.IsDefined(typeof(Faction), x.faction)) throw Invalid("Invalid player identity, faction or color.");
                if (x.neutral && (x.human || ++neutrals > 1)) throw Invalid("Invalid neutral owner.");
                if (x.human) humans++;
                var p = new PlayerSetupData
                {
                    PlayerId = x.id, Nickname = x.nickname, ColorIndex = x.colorIndex, Faction = x.faction,
                    IsHuman = x.human, IsNeutral = x.neutral, IsEliminated = x.eliminated, SelectedDeckId = x.selectedDeckId,
                    AiDeckName = x.aiDeckName, AiGrantedLeaderKey = x.aiGrantedLeaderKey,
                    CitadelHexQ = x.hasCitadel ? (int?)x.citadelQ : null, CitadelHexR = x.hasCitadel ? (int?)x.citadelR : null,
                };
                players.Add(x.id, p); if (!x.neutral) active.Add(p);
                if (x.hasAiBlueprints)
                { Require(x.aiBlueprints); p.AiBlueprints = new HashSet<string>(StringComparer.Ordinal); foreach (string key in x.aiBlueprints) { content.Card(key); if (!p.AiBlueprints.Add(key)) throw Invalid("Duplicate AI blueprint."); } }
                if (!string.IsNullOrEmpty(x.aiGrantedLeaderKey)) content.Card(x.aiGrantedLeaderKey);
                if (x.hasLoadout) p.MatchLoadout = MatchLoadout.FromState(x.loadout, new DeckRules(config.collectionDeckCatalog, config.collectionResearchCatalog));
                if (x.hasQuota)
                {
                    p.BlueprintQuota = BlueprintQuota.FromState(x.quota);
                    foreach (var key in x.quota.unlockedKeys) content.Card(key);
                }
                if (x.hasEconomy) { ValidateEconomy(x.economy); economies.Add(p, CopyEconomy(x.economy)); }
            }
            if (humans != 1 || active.Count < 2) throw Invalid("A local match requires one human and at least two players.");
            Players = active.AsReadOnly();
            foreach (var x in data.cards)
            {
                if (x == null || x.runtimeId <= 0 || x.runtimeId > identity.CardHighWaterMark || cards.ContainsKey(x.runtimeId) || x.acquiredTurn < -1) throw Invalid("Invalid or duplicate card identity.");
                cards.Add(x.runtimeId, newCard(x));
            }
            var heldCards = new HashSet<int>();
            foreach (var x in data.players)
            {
                var p = NeedPlayer(x.id); Require(x.tactics);
                if (x.hasHand)
                {
                    var h = x.hand;
                    if (h == null || h.capacity < 0 || h.currentTurn < 0 || h.mutationVersion < 0 || h.ai == p.IsHuman || p.IsNeutral) throw Invalid("Invalid hand owner or counters.");
                    Require(h.cards); Require(h.deck);
                    var state = new HandState { capacity = h.capacity, currentTurn = h.currentTurn, mutationVersion = h.mutationVersion, remainingDeckKeys = new List<string>(h.deck) };
                    foreach (int id in h.cards)
                    { if (!heldCards.Add(id)) throw Invalid("A card appears in multiple hand slots."); NeedCard(id); state.cards.Add(new CardState { runtimeId = id }); }
                    p.Hand = h.ai ? (PlayerHandData)new AiHandData(null, Faction.None, 0) : new PlayerHandData(null, 0);
                    p.Hand.RestoreCanonical(state, NeedCard, content.Card);
                }
                var tactics = new List<CardData>();
                foreach (int id in x.tactics) { if (!heldCards.Add(id)) throw Invalid("A card appears in multiple inventories."); tactics.Add(NeedCard(id)); }
                p.Tactics.RestoreCanonical(tactics, x.tacticsVersion);
                if (p.IsHuman && p.Hand == null) throw Invalid("The human hand is missing.");
            }
            foreach (var x in data.units)
            {
                if (x == null || x.id <= 0 || x.id > identity.UnitHighWaterMark || units.ContainsKey(x.id)) throw Invalid("Invalid or duplicate unit identity.");
                var u = UnitData.CreateDetached(x.id); units.Add(x.id, u); RestoreUnit(x, u);
            }
            foreach (var x in data.armies)
            {
                if (x == null || x.id < 0 || x.id > identity.ArmyHighWaterMark || armies.ContainsKey(x.id)) throw Invalid("Invalid or duplicate army identity.");
                var a = ArmyData.CreateDetached(x.id); armies.Add(x.id, a);
                a.Name = x.name; a.Hex = new HexCoord(x.q, x.r); a.Owner = NeedPlayer(x.owner);
                a.IsGarrison = x.garrison; a.IsPrison = x.prison; a.IsAirfield = x.airfield; a.IsAirArmy = x.airArmy; a.AirWingLanded = x.wingLanded;
                if (x.registered) registered.Add(a);
                var prefab = MatchSaveAssets.ResolvePrefab(x.markerPrefab); if (prefab != null) armyPrefabs.Add(a, prefab);
                var icon = MatchSaveAssets.ResolveSprite(x.markerIcon); if (icon != null) armyIcons.Add(a, icon);
            }
            var members = new HashSet<int>();
            foreach (var x in data.armies)
            {
                var a = NeedArmy(x.id); Require(x.members); Require(x.arrangement);
                a.CarrierUnit = OptionalUnit(x.carrierUnit); a.LaunchCarrier = OptionalUnit(x.launchCarrier);
                foreach (int id in x.members)
                {
                    var u = NeedUnit(id);
                    // Retained removed shells may still share stale member references with a live shell.
                    if (x.registered && (!members.Add(id) || u.Owner != a.Owner)) throw Invalid("Duplicate live membership or mismatched owner.");
                    if (a.Members.Contains(u)) throw Invalid("Duplicate army member."); a.Members.Add(u);
                }
                foreach (var entry in x.arrangement)
                {
                    if (entry == null || !BattleGrid.InBounds(entry.row, entry.col) || a.SavedArrangement.ContainsKey(NeedUnit(entry.unitId))) throw Invalid("Invalid saved battle arrangement.");
                    a.SavedArrangement.Add(NeedUnit(entry.unitId), (entry.row, entry.col));
                }
                a.RestoreActivationState(x.activation, NeedUnit);
                a.LastAirStrikeHex = x.hasLastStrike ? (HexCoord?)new HexCoord(x.lastStrikeQ, x.lastStrikeR) : null;
                a.LastAirStrikeAttacked = x.lastStrikeAttacked;
                if (x.hasStrikePolicy)
                {
                    if (!Enum.IsDefined(typeof(AirStrikePolicyKind), x.strikeKind) || x.minimumSurvivors < 0 || x.hasStrikeTarget && (x.strikeTarget < 0 || x.strikeTarget > identity.ArmyHighWaterMark)) throw Invalid("Invalid air strike policy.");
                    a.PendingAirStrikePolicy = new AirStrikePolicy((AirStrikePolicyKind)x.strikeKind, x.hasStrikeTarget ? (int?)x.strikeTarget : null, x.minimumSurvivors);
                }
            }
            foreach (var x in data.units)
            {
                var u = NeedUnit(x.id);
                if (x.transportCarrier < -1 || x.transportArmy < -1 || (x.transportCarrier >= 0) != (x.transportArmy >= 0)) throw Invalid("Invalid transport link.");
                u.RestoreMovementState(new UnitMovementState { PermanentMaximum = x.moveMax, LegacyCurrent = x.legacyMove, Spent = x.hasSpent ? (int?)x.spent : null, TransportCarrierId = x.transportCarrier < 0 ? null : (int?)x.transportCarrier, TransportArmyId = x.transportArmy < 0 ? null : (int?)x.transportArmy }, NeedUnit, NeedArmy);
                u.RestoreCarrierLinksState(new UnitCarrierLinksState { AirfieldArmyId = Link(x.airfieldArmy), FlightDeckArmyId = Link(x.flightDeckArmy) }, NeedArmy);
            }
            foreach (var a in armies.Values) a.RebindRestoredTransportRecipients();
            foreach (var x in data.buildings) RestoreBuilding(x);
            var bonusHexes = new HashSet<HexCoord>();
            foreach (var x in data.bonuses)
            {
                if (x == null || x.bonus == null || !bonusHexes.Add(new HexCoord(x.q, x.r))) throw Invalid("Invalid resource bonus.");
                ValidateYields(x.bonus); bonuses.Add(new HexResourceBonusState { Hex = new HexCoord(x.q, x.r), Bonus = MatchWorldCapture.CopyYields(x.bonus) });
            }
            var eventHexes = new HashSet<HexCoord>();
            foreach (var x in data.events)
            {
                if (x == null || !eventHexes.Add(new HexCoord(x.q, x.r))) throw Invalid("Invalid event location.");
                events.Add(new HexEventEntryState<HexEventRegistry.Entry> { Hex = new HexCoord(x.q, x.r), Payload = RestoreEvent(x) });
            }
            var battleHexes = new HashSet<HexCoord>();
            foreach (var x in data.battles)
            {
                if (x == null || !battleHexes.Add(new HexCoord(x.q, x.r))) throw Invalid("Duplicate delayed battle."); Require(x.armies);
                var ids = new HashSet<int>(); var battle = new DelayedBattleState { Hex = new HexCoord(x.q, x.r) };
                foreach (int id in x.armies) { NeedArmy(id); if (!ids.Add(id)) throw Invalid("Duplicate delayed participant."); battle.ParticipantArmyIds.Add(id); } battles.Add(battle);
            }
            RestoreAntiAir(data.antiAir);
            stealth = ValidateStealth(data.stealth);
            ValidateUnitLinks();
            ValidateMarkerContent();
        }
        private void ValidateUnitLinks()
        {
            var live = new HashSet<ArmyData>(registered);
            var transportPairs = new HashSet<(ArmyData, UnitData)>();
            foreach (var unit in units.Values)
            {
                ValidateContainer(unit, unit.CarrierAirfield, true);
                ValidateContainer(unit, unit.CarrierFlightDeck, false);
                if (unit.TransportArmy == null) continue;
                var army = unit.TransportArmy; var carrier = unit.AssignedTransportCarrier;
                if (carrier == unit || carrier == null || army.Owner != unit.Owner || carrier.Owner != army.Owner
                    || !army.Members.Contains(unit) || !army.Members.Contains(carrier)
                    || army.IsPrison || army.IsAirfield || army.IsAirArmy || army.IsCarrierContainer
                    || !transportPairs.Add((army, carrier))) throw Invalid("Invalid transport membership.");
            }
            foreach (var army in registered)
            {
                if (army.CarrierUnit == null) continue;
                var carrier = army.CarrierUnit;
                if ((army.IsAirfield ? carrier.CarrierAirfield : carrier.CarrierFlightDeck) != army)
                    throw Invalid("A live carrier container is missing its reciprocal unit link.");
                var host = registered.FirstOrDefault(a => a.Members.Contains(carrier));
                if (host == null || host.IsCarrierContainer || host.IsAirfield || host.IsPrison
                    || host.Owner != army.Owner || !host.Hex.Equals(army.Hex))
                    throw Invalid("A live carrier container is detached from its ground host.");
            }

            void ValidateContainer(UnitData carrier, ArmyData container, bool airfield)
            {
                if (container == null) return;
                if (container.CarrierUnit != carrier || container.Owner != carrier.Owner
                    || container.IsAirfield != airfield || container.IsGarrison || container.IsPrison)
                    throw Invalid("Invalid carrier container ownership, kind or reciprocal link.");
                // Removed historical shells may retain stale rosters. Only live containers
                // must still be bound back by their carrier; their host is checked above.
                if (live.Contains(container) && airfield && container.IsAirArmy)
                    throw Invalid("An airfield cannot also be a live air wing.");
            }
        }
        private CardData newCard(CardState x)
        {
            var c = CardData.CreateDetached(content.Card(x.definitionKey), x.runtimeId);
            c.Equipment = content.OptionalCard(x.equipmentKey); c.Mutator = content.OptionalCard(x.mutatorKey); c.AcquiredTurn = x.acquiredTurn; c.ResearchProductionCreated = x.researchProductionCreated; return c;
        }
        private void RestoreUnit(MatchUnitData x, UnitData u)
        {
            Require(x.abilities); Require(x.typeTags);
            if (x.moveMax < 0 || x.hasSpent && x.spent < 0 || x.hpMax < 0 || x.hp < 0 || x.hp > x.hpMax || x.fateMax < 0 || x.fate < 0 || x.fate > x.fateMax
                || x.activationApCost < 0 || x.apCost < 0 || x.airfieldCapacity < 0 || x.unlandedEnds < 0 || x.turnsWithoutRefuel < 0 || x.berserkStacks < 0 || x.berserkDefenseLost < 0 || x.drillAttackBonus < 0 || !Enum.IsDefined(typeof(BattleRow), x.row)) throw Invalid("Invalid unit stats.");
            u.Name = x.name; u.Owner = NeedPlayer(x.owner); u.CapturedFrom = OptionalPlayer(x.capturedFrom);
            u.OriginatingCard = content.OptionalCard(x.origin); u.Equipment = content.OptionalCard(x.equipment); u.Mutator = content.OptionalCard(x.mutator);
            u.Art = MatchSaveAssets.ResolveSprite(x.art); u.DetailArt = MatchSaveAssets.ResolveSprite(x.detailArt);
            AddAbilities(u.Abilities, x.abilities);
            foreach (var tag in x.typeTags) if (!Enum.IsDefined(typeof(UnitTypeTag), tag) || !u.TypeTags.Add(tag)) throw Invalid("Invalid unit type tag.");
            u.IsHidden = x.hidden; u.HasEnteredStealthThisTurn = x.enteredStealth; u.IsHero = x.hero; u.IsPrisoner = x.prisoner; u.IsSummoned = x.summoned; u.IsAviation = x.aviation;
            u.Row = x.row; u.Attack = x.attack; u.Defense = x.defense; u.Range = x.range; u.HitPointsMax = x.hpMax; u.HitPointsCurrent = x.hp;
            u.Initiative = x.initiative; u.FateMax = x.fateMax; u.Fate = x.fate; u.CommandRating = x.commandRating; u.ActivationApCost = x.activationApCost; u.ApCost = x.apCost;
            u.BerserkStacks = x.berserkStacks; u.BerserkDefenseLost = x.berserkDefenseLost; u.DrillAttackBonus = x.drillAttackBonus;
            u.LaunchEnergyCost = x.launchEnergyCost; u.TurnsWithoutRefuel = x.turnsWithoutRefuel; u.AntiAirRadius = x.antiAirRadius; u.AirfieldCapacity = x.airfieldCapacity; u.ConsecutiveUnlandedEnds = x.unlandedEnds;
            u.HasEmergencyFlightPenalty = x.emergencyPenalty; u.HasAirAttackedThisTurn = x.airAttacked; u.SortieLaunchPaid = x.sortiePaid;
            if (x.hasOriginalCost) { ValidateCost(x.originalCost); u.OriginalResourceCost = MatchWorldCapture.CopyCost(x.originalCost); }
            if (x.hasRepairCost) { ValidateCost(x.repairCost); u.RepairResourceCost = MatchWorldCapture.CopyCost(x.repairCost); }
            if (x.hasAttachmentBase) u.AttachmentBase = RestoreAttachment(x.attachmentBase);
            if (x.hasAttachmentApplied) u.AttachmentApplied = RestoreAttachment(x.attachmentApplied);
            if (x.hasAttachmentResources)
            {
                var r = x.attachmentResources; if (r == null || r.hpSpent < 0 || r.moveSpent < 0 || r.fateSpent < 0) throw Invalid("Invalid attachment spending.");
                u.AttachmentResources = new AttachmentResourceState(r.hpSpent, r.moveSpent, r.fateSpent, r.lastHp, r.lastMove, r.lastFate);
            }
        }
        private void RestoreBuilding(MatchBuildingData x)
        {
            if (x == null || x.level < 0 || x.hp < 0 || x.hpMax < 0 || x.hp > x.hpMax || x.airfieldCapacity < 0) throw Invalid("Invalid building state."); Require(x.facilities); Require(x.abilities);
            var hex = new HexCoord(x.q, x.r); if (buildings.ContainsKey(hex) || x.facilities.Count > 64) throw Invalid("Invalid building location or slot count.");
            var b = new BuildingData(x.facilities.Count)
            {
                Name = x.name, Hex = hex, Owner = NeedPlayer(x.owner), Art = MatchSaveAssets.ResolveSprite(x.art), DetailArt = MatchSaveAssets.ResolveSprite(x.detailArt),
                IsBase = x.isBase, IsStartingCitadel = x.startingCitadel, HasTieredUnlock = x.tiered, Level = x.level,
                StructurePointsCurrent = x.hp, StructurePointsMax = x.hpMax, Defense = x.defense, Fate = x.fate, AirfieldCapacity = x.airfieldCapacity,
            };
            AddAbilities(b.Abilities, x.abilities); buildings.Add(hex, b);
            var prefab = MatchSaveAssets.ResolvePrefab(x.markerPrefab); if (prefab != null) buildingPrefabs.Add(b, prefab);
            var icon = MatchSaveAssets.ResolveSprite(x.markerIcon); if (icon != null) buildingIcons.Add(b, icon);
            for (int i = 0; i < x.facilities.Count; i++)
            {
                var f = x.facilities[i]; if (f == null) throw Invalid("Missing facility slot."); if (!f.occupied) continue;
                if (f.upgradeLevel < 0 || f.hp < 0 || f.hpMax < 0 || f.hp > f.hpMax || f.antiAirRadius < 0) throw Invalid("Invalid facility stats."); Require(f.abilities);
                var facility = new FacilityData
                {
                    Name = f.name, Art = MatchSaveAssets.ResolveSprite(f.art), DetailArt = MatchSaveAssets.ResolveSprite(f.detailArt), UpgradeLevel = f.upgradeLevel,
                    StructurePointsCurrent = f.hp, StructurePointsMax = f.hpMax, Defense = f.defense, Fate = f.fate, Attack = f.attack, AntiAirRadius = f.antiAirRadius,
                };
                AddAbilities(facility.Abilities, f.abilities); b.FacilitySlots[i] = facility;
            }
        }
        private HexEventRegistry.Entry RestoreEvent(MatchEventData x)
        {
            Require(x.guard); Require(x.discovered); Require(x.known); Require(x.rewards); Require(x.selectedRewards); Require(x.resolvedRewards);
            var e = new HexEventRegistry.Entry
            {
                Definition = MatchSaveAssets.ResolveEvent(x.definition), GuardArmyName = x.guardName, GuardOwner = OptionalPlayer(x.guardOwner), ConsumedBy = OptionalPlayer(x.consumedBy),
                Consumed = x.consumed, Triggered = x.triggered, CollectionRewardPending = x.rewardPending, CollectionRewardError = x.rewardError, Skipped = x.skipped,
            };
            if (e.Definition == null) throw Invalid("Missing event definition.");
            foreach (var g in x.guard) { if (g == null || g.count <= 0) throw Invalid("Invalid event guard."); e.ResolvedGuardMembers.Add((content.Card(g.cardKey), g.count)); }
            foreach (var id in x.discovered) if (!e.DiscoveredBy.Add(NeedPlayer(id))) throw Invalid("Duplicate event discoverer.");
            foreach (var id in x.known) if (!e.KnownBy.Add(NeedPlayer(id))) throw Invalid("Duplicate event knowledge owner.");
            var rewardTable = new List<RewardEntry>();
            foreach (var r in x.rewards)
            {
                if (r == null || !Enum.IsDefined(typeof(RewardType), r.type)) throw Invalid("Invalid event reward.");
                if (r.type == RewardType.Resources) ValidateYields(r.resources);
                rewardTable.Add(new RewardEntry { type = r.type, cardKey = r.cardKey, resources = MatchWorldCapture.CopyYields(r.resources) });
            }
            foreach (int index in x.selectedRewards)
            { if (index < 0 || index >= rewardTable.Count) throw Invalid("Invalid selected reward reference."); e.SelectedRewards.Add(rewardTable[index]); }
            var resolved = new HashSet<int>();
            foreach (var r in x.resolvedRewards)
            {
                if (r == null || r.rewardIndex < 0 || r.rewardIndex >= rewardTable.Count || !resolved.Add(r.rewardIndex) || rewardTable[r.rewardIndex].type != RewardType.Card) throw Invalid("Invalid resolved event reward.");
                e.ResolvedCardRewards.Add((rewardTable[r.rewardIndex], content.Card(r.card)));
            }
            return e;
        }
        private void RestoreAntiAir(MatchAntiAirData x)
        {
            if (x == null) throw Invalid("Missing anti-air state."); Require(x.fired); Require(x.prompted); Require(x.gunners);
            var fired = new HashSet<int>(); foreach (int id in x.fired) { NeedUnit(id); if (!fired.Add(id)) throw Invalid("Duplicate fired unit."); aa.FiredUnitIds.Add(id); }
            var prompted = new HashSet<(int, int)>();
            foreach (var p in x.prompted)
            {
                if (p == null || p.AirArmyId < 0 || p.AirArmyId > identity.ArmyHighWaterMark || !prompted.Add((p.UnitId, p.AirArmyId))) throw Invalid("Invalid anti-air prompt.");
                NeedUnit(p.UnitId); aa.Prompted.Add(new AntiAirPromptState { UnitId = p.UnitId, AirArmyId = p.AirArmyId });
            }
            var slots = new HashSet<(int, int, int)>(); var gunners = new HashSet<int>();
            foreach (var g in x.gunners)
            {
                if (g == null || !buildings.TryGetValue(new HexCoord(g.q, g.r), out var b) || g.slot < 0 || g.slot >= b.FacilitySlots.Length || b.FacilitySlots[g.slot] == null
                    || !slots.Add((g.q, g.r, g.slot)) || !gunners.Add(g.unitId)) throw Invalid("Invalid facility gunner.");
                NeedUnit(g.unitId); aa.FacilityGunners.Add(new FacilityGunnerState { BuildingHex = b.Hex, Slot = g.slot, UnitId = g.unitId });
            }
        }
        private StealthState ValidateStealth(StealthState x)
        {
            if (x == null) throw Invalid("Missing stealth state."); Require(x.Detections); Require(x.Notices);
            var result = new StealthState(); var keys = new HashSet<(int, string)>();
            foreach (var d in x.Detections)
            {
                if (d == null || d.Expiry < 0 || !keys.Add((d.UnitId, d.ObserverId))) throw Invalid("Invalid stealth detection."); NeedUnit(d.UnitId); NeedPlayer(d.ObserverId);
                result.Detections.Add(new DetectionState { UnitId = d.UnitId, ObserverId = d.ObserverId, Expiry = d.Expiry });
            }
            var owners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in x.Notices)
            {
                if (n == null || !owners.Add(n.PlayerId)) throw Invalid("Invalid stealth notices."); NeedPlayer(n.PlayerId); Require(n.Notices);
                if (n.Notices.Any(s => s == null)) throw Invalid("Missing stealth notice.");
                result.Notices.Add(new DetectionNoticesState { PlayerId = n.PlayerId, Notices = new List<string>(n.Notices) });
            }
            return result;
        }
        internal void RestoreWorld(HexMap map)
        {
            if (map == null || worldRestored) throw Invalid("World restore requires a fresh graph and a map.");
            // Validate every physical location before the first mutation.
            foreach (var a in registered) RequireMapHex(map, a.Hex);
            foreach (var b in buildings.Values) RequireMapHex(map, b.Hex);
            foreach (var b in bonuses) RequireMapHex(map, b.Hex);
            foreach (var e in events) RequireMapHex(map, e.Hex);
            foreach (var b in battles) RequireMapHex(map, b.Hex);
            using (MatchRestoreScope.Begin())
            {
                identity.Reserve();
                foreach (var p in players.Values)
                {
                    var root = PlayerRoot.Create(p.IsNeutral ? null : p, p.IsNeutral ? "Neutral" : p.Nickname);
                    PlayerRootRegistry.Register(p, root); if (economies.TryGetValue(p, out var economy)) root.RestoreEconomyState(economy);
                    if (p.Hand is AiHandData aiHand) AiHandRegistry.RegisterRestored(p, aiHand);
                }
                foreach (var b in buildings.Values) BuildingRegistry.Register(b.Hex, b);
                foreach (var a in registered) ArmyRegistry.Register(a);
                foreach (var a in armies.Values) a.RebindRestoredTransportRecipients();
                HexResourceBonusRegistry.RestoreState(bonuses);
                HexEventRegistry.RestoreEntries(events, (hex, entry) => entry);
                DelayedBattleRegistry.RestoreState(battles, NeedArmy);
                CreateMarkers(map);
                worldRestored = true;
            }
        }
        internal void ValidateMap(HexMapLayout layout)
        {
            if (layout?.Cells == null) throw Invalid("Missing logical map layout.");
            var hexes = new HashSet<HexCoord>();
            foreach (var cell in layout.Cells)
                if (cell != null) hexes.Add(new HexCoord(cell.Q, cell.R));
            foreach (var a in registered) RequireLayoutHex(hexes, a.Hex);
            foreach (var b in buildings.Values) RequireLayoutHex(hexes, b.Hex);
            foreach (var b in bonuses) RequireLayoutHex(hexes, b.Hex);
            foreach (var e in events) RequireLayoutHex(hexes, e.Hex);
            foreach (var b in battles) RequireLayoutHex(hexes, b.Hex);
            foreach (var p in players.Values)
                if (p.CitadelHexQ.HasValue) RequireLayoutHex(hexes, new HexCoord(p.CitadelHexQ.Value, p.CitadelHexR.Value));
        }
        private static void RequireLayoutHex(HashSet<HexCoord> hexes, HexCoord hex)
        { if (!hexes.Contains(hex)) throw Invalid("Saved content lies outside the logical map: " + hex); }
        private void ValidateMarkerContent()
        {
            foreach (var b in buildings.Values)
            {
                var catalog = config.collectionDeckCatalog.GetCatalog(b.Owner.Faction);
                if (!buildingPrefabs.ContainsKey(b) && (b.IsBase ? (b.IsStartingCitadel ? catalog?.citadelPrefab : catalog?.basePrefab) : config.facilityMarkerPrefab) == null)
                    throw Invalid("Missing restored building marker prefab.");
            }
            foreach (var a in registered)
            {
                if (a.IsPrison || a.IsAirfield || a.IsCarrierContainer && a.Members.Count == 0) continue;
                var catalog = config.collectionDeckCatalog.GetCatalog(a.Owner.Faction);
                if (!armyPrefabs.ContainsKey(a) && (AviationRules.IsAirArmy(a) && catalog?.airArmyPrefab != null ? catalog.airArmyPrefab : catalog?.armyPrefab) == null)
                    throw Invalid("Missing restored army marker prefab.");
            }
        }
        internal void RestoreKnowledge()
        {
            if (!worldRestored) throw Invalid("World must be restored before knowledge.");
            using (MatchRestoreScope.Begin())
            { AntiAirState.RestoreState(aa, NeedUnit); StealthSystem.RestoreState(stealth, NeedUnit, NeedPlayer); }
        }
        private void CreateMarkers(HexMap map)
        {
            foreach (var b in buildings.Values)
            {
                var catalog = config.collectionDeckCatalog.GetCatalog(b.Owner.Faction);
                var prefab = buildingPrefabs.TryGetValue(b, out var savedPrefab) ? savedPrefab : b.IsBase ? (b.IsStartingCitadel ? catalog?.citadelPrefab : catalog?.basePrefab) : config.facilityMarkerPrefab;
                if (prefab == null) throw Invalid("Missing restored building marker prefab.");
                b.Visual = UnityEngine.Object.Instantiate(prefab); b.Visual.PersistenceSourcePrefab = prefab;
                ConfigureMarker(b.Visual, map, b.Hex, b.Owner, MapSortingOrder.BuildingCircle, MapSortingOrder.BuildingIcon);
                MapFlagAnimator.ApplyFaction(b.Visual, b.Owner.Faction);
                if (buildingIcons.TryGetValue(b, out var icon)) b.Visual.SetIcon(icon);
                if (b.IsStartingCitadel) PlayerRootRegistry.FindFor(b.Owner).SetCitadel(b.Visual);
            }
            foreach (var a in registered)
            {
                // A populated carrier deck is a selectable flight formation: TryLaunch and
                // transfers give it a controller before take-off, even while its marker hides.
                if (a.IsPrison || a.IsAirfield || a.IsCarrierContainer && a.Members.Count == 0) continue;
                var catalog = config.collectionDeckCatalog.GetCatalog(a.Owner.Faction);
                var prefab = armyPrefabs.TryGetValue(a, out var savedPrefab) ? savedPrefab : AviationRules.IsAirArmy(a) && catalog?.airArmyPrefab != null ? catalog.airArmyPrefab : catalog?.armyPrefab;
                if (prefab == null) throw Invalid("Missing restored army marker prefab.");
                var visual = UnityEngine.Object.Instantiate(prefab); visual.PersistenceSourcePrefab = prefab;
                if (armyIcons.TryGetValue(a, out var icon)) visual.SetIcon(icon);
                var controller = visual.gameObject.AddComponent<ArmyController>(); controller.SetData(a); a.Controller = controller;
                ConfigureMarker(visual, map, a.Hex, a.Owner, MapSortingOrder.ArmyCircle, MapSortingOrder.ArmyIcon); visual.SetVisible(false);
            }
        }
        private static void ConfigureMarker(MapObjectVisual visual, HexMap map, HexCoord hex, PlayerSetupData owner, int circleOrder, int iconOrder)
        {
            var root = PlayerRootRegistry.FindFor(owner); if (root != null) visual.transform.SetParent(root.transform, true);
            visual.transform.position = map.HexToWorld(hex); visual.SetColor(PlayerColorPalette.Colors[owner.ColorIndex]); visual.SetSortingOrder(circleOrder, iconOrder);
        }
        private static void RequireMapHex(HexMap map, HexCoord hex)
        { if (!map.TryGetTerrainAt(hex, out var terrain) || terrain == null) throw Invalid("Saved content lies outside the logical map: " + hex); }
        private PlayerSetupData NeedPlayer(string id) => Player(id) ?? throw Invalid("Unresolved player: " + id);
        private PlayerSetupData OptionalPlayer(string id) => string.IsNullOrEmpty(id) ? null : NeedPlayer(id);
        private UnitData NeedUnit(int id) => Unit(id) ?? throw Invalid("Unresolved unit: " + id);
        private UnitData OptionalUnit(int id) => id == -1 ? null : NeedUnit(id);
        private ArmyData NeedArmy(int id) => Army(id) ?? throw Invalid("Unresolved army: " + id);
        private CardData NeedCard(int id) => Card(id) ?? throw Invalid("Unresolved card: " + id);
        private static int? Link(int id) { if (id < -1) throw Invalid("Invalid optional identity."); return id == -1 ? null : (int?)id; }
        private static PredictedEquipmentState RestoreAttachment(MatchAttachmentData x)
        {
            if (x == null) throw Invalid("Missing attachment snapshot."); Require(x.stats); Require(x.abilities);
            var stats = new Dictionary<EquipmentStat, int>();
            foreach (var s in x.stats)
            { if (s == null || !Enum.IsDefined(typeof(EquipmentStat), s.stat) || stats.ContainsKey(s.stat)) throw Invalid("Invalid attachment stat."); stats.Add(s.stat, s.amount); }
            var abilities = new HashSet<string>(); AddAbilities(abilities, x.abilities);
            return new PredictedEquipmentState(stats, new List<string>(x.abilities));
        }
        private static void AddAbilities(HashSet<string> target, List<string> source)
        { foreach (string s in source) if (string.IsNullOrWhiteSpace(s) || !target.Add(s)) throw Invalid("Missing or duplicate ability."); }
        private static void ValidateCost(ResourceCost x)
        { if (x == null || x.human < 0 || x.energy < 0 || x.materials < 0 || x.tech < 0) throw Invalid("Invalid resource cost."); }
        private static void ValidateYields(Game.Economy.ResourceYields x)
        { if (x == null || x.human < 0 || x.energy < 0 || x.materials < 0 || x.tech < 0) throw Invalid("Invalid resource yield."); }
        private static void ValidateEconomy(PlayerEconomyState x)
        {
            if (x == null || x.ActionPoints < 0 || x.Human < 0 || x.Energy < 0 || x.Materials < 0 || x.Tech < 0) throw Invalid("Invalid player economy."); Require(x.InitiativeContributions);
            foreach (var r in x.InitiativeContributions) if (!Enum.IsDefined(typeof(Game.Economy.ResourceType), r)) throw Invalid("Invalid initiative resource.");
        }
        private static PlayerEconomyState CopyEconomy(PlayerEconomyState x) => new PlayerEconomyState
        {
            ActionPoints = x.ActionPoints, Human = x.Human, Energy = x.Energy, Materials = x.Materials, Tech = x.Tech,
            InitiativeContributions = new List<Game.Economy.ResourceType>(x.InitiativeContributions), LastApFromInitiative = x.LastApFromInitiative,
            LastApFromPrisonBonus = x.LastApFromPrisonBonus, LastApFromApBonus = x.LastApFromApBonus, LastApBonusSources = x.LastApBonusSources,
        };
        private static void Require<T>(ICollection<T> value) { if (value == null) throw Invalid("Missing world collection."); }
        private static InvalidOperationException Invalid(string message) => new InvalidOperationException(message);
    }
}
