using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Units;

namespace Game.Saves
{
    [Serializable] internal sealed class MatchAiPlayerRef { public bool Present; public string Id; }
    [Serializable] internal sealed class MatchAiArmyRef { public bool Present; public int Id; }
    [Serializable] internal sealed class MatchAiUnitRef { public bool Present; public int Id; }
    [Serializable] internal sealed class MatchAiCardRef { public bool Present; public int Id; }

    [Serializable] internal sealed class MatchAiObjectiveData
    {
        public int Tag;
        public MatchAiScoutIntent146 Scout;
        public MatchAiRaidIntent148 Raid;
        public MatchAiEconomyIntent151 Economy;
        public MatchAiDevelopmentIntent156 Development;
        public MatchAiActiveDefenceIntent158 ActiveDefence;
        public MatchAiAttackIntent188 Attack;
    }

    // Disk boundary: concrete mutable DTOs, exact reconstruction and validation before owners mutate.
    internal static partial class MatchAiCodec
    {
        internal static MatchAiData Capture(Func<PlayerSetupData, string> playerId,
            Action<ArmyData> includeArmy, Action<UnitData> includeUnit, Action<CardData> includeCard)
        {
            if (playerId == null) throw new ArgumentNullException(nameof(playerId));
            var context = new Context(playerId, includeArmy, includeUnit, includeCard);
            var objectives = new Objectives(context);
            var snapshot = new AiPersistentState<MatchAiObjectiveData>(context.PlayerId, objectives, context.IncludeCard);
            // Sorties may retain an army already removed from the map registry.
            foreach (var player in context.Players.Values.ToArray())
                foreach (var sortie in AirSortieRegistry.For(player))
                    context.IncludeArmy(sortie.Army);
            var result = Write0(snapshot, context);
            CollectIdentities(snapshot, context);
            result.HistoricalArmyIds = context.ArmyIds.OrderBy(x => x).ToList();
            result.HistoricalUnitIds = context.UnitIds.OrderBy(x => x).ToList();
            result.HistoricalCardIds = context.CardIds.OrderBy(x => x).ToList();
            return result;
        }

        internal static void Validate(MatchAiData data, Func<string, PlayerSetupData> player,
            Func<int, ArmyData> army, Func<int, UnitData> unit, Func<int, CardData> card, GameConfig config)
        {
            if (ReferenceEquals(config, null)) throw new ArgumentNullException(nameof(config));
            var context = new Context(player, army, unit, card);
            var state = Read0(data, context);
            Required(state);
            ValidateState(state, context, config);
            CollectIdentities(state, context);
            CheckIdentityManifest(data.HistoricalArmyIds, context.ArmyIds);
            CheckIdentityManifest(data.HistoricalUnitIds, context.UnitIds);
            CheckIdentityManifest(data.HistoricalCardIds, context.CardIds);
        }

        internal static void Restore(MatchAiData data, Func<string, PlayerSetupData> player,
            Func<int, ArmyData> army, Func<int, UnitData> unit, Func<int, CardData> card, HexMap map)
        {
            if (!MatchRestoreScope.IsRestoring) throw new InvalidOperationException("AI restoration requires MatchRestoreScope");
            var context = new Context(player, army, unit, card);
            var state = Read0(data, context);
            Required(state);
            // Repeat all structural/reference checks before the first owner is replaced.
            ValidateState(state, context, null);
            CollectIdentities(state, context);
            CheckIdentityManifest(data.HistoricalArmyIds, context.ArmyIds);
            CheckIdentityManifest(data.HistoricalUnitIds, context.UnitIds);
            CheckIdentityManifest(data.HistoricalCardIds, context.CardIds);
            state.Restore(context.ReadPlayerId, context.ReadArmyId, context.ReadCardId, new Objectives(context), map);
        }

        internal sealed class Objectives : IMissionObjectiveStateCodec<MatchAiObjectiveData>
        {
            private readonly Context context;
            internal Objectives(Context context) { this.context = context; }
            internal Objectives(Func<PlayerSetupData, string> playerId, Action<UnitData> unit, Action<CardData> card)
                : this(new Context(playerId, null, unit, card)) { }
            internal Objectives(Func<string, PlayerSetupData> player, Func<int, ArmyData> army,
                Func<int, UnitData> unit, Func<int, CardData> card) : this(new Context(player, army, unit, card)) { }
            public MatchAiObjectiveData CaptureScout(ScoutIntent value) => new MatchAiObjectiveData { Tag = (int)MissionKind.Scout, Scout = Write146(value, context) };
            public MatchAiObjectiveData CaptureRaid(RaidIntent value) => new MatchAiObjectiveData { Tag = (int)MissionKind.Raid, Raid = Write148(value, context) };
            public MatchAiObjectiveData CaptureEconomy(EconomyIntent value) => new MatchAiObjectiveData { Tag = (int)MissionKind.Economy, Economy = Write151(value, context) };
            public MatchAiObjectiveData CaptureDevelopment(DevelopmentIntent value) => new MatchAiObjectiveData { Tag = (int)MissionKind.Development, Development = Write156(value, context) };
            public MatchAiObjectiveData CaptureActiveDefence(ActiveDefenceIntent value) => new MatchAiObjectiveData { Tag = (int)MissionKind.ActiveDefence, ActiveDefence = Write158(value, context) };
            public MatchAiObjectiveData CaptureAttack(AttackIntent value) => new MatchAiObjectiveData { Tag = (int)MissionKind.Attack, Attack = Write188(value, context) };
            public IMissionObjective Restore(MissionKind kind, MatchAiObjectiveData data)
            {
                Required(data);
                if (CheckedEnum<MissionKind>(data.Tag) != kind) throw Invalid("Mismatched mission objective tag");
                int payloads = (data.Scout?.HasData == true ? 1 : 0) + (data.Raid?.HasData == true ? 1 : 0)
                    + (data.Economy?.HasData == true ? 1 : 0) + (data.Development?.HasData == true ? 1 : 0)
                    + (data.ActiveDefence?.HasData == true ? 1 : 0) + (data.Attack?.HasData == true ? 1 : 0);
                if (payloads != 1) throw Invalid("Mission objective requires exactly one payload");
                switch (kind)
                {
                    case MissionKind.Scout: return Read146(data.Scout, context) ?? throw Invalid("Missing scout payload");
                    case MissionKind.Raid: return Read148(data.Raid, context) ?? throw Invalid("Missing raid payload");
                    case MissionKind.Economy: return Read151(data.Economy, context) ?? throw Invalid("Missing economy payload");
                    case MissionKind.Development: return Read156(data.Development, context) ?? throw Invalid("Missing development payload");
                    case MissionKind.ActiveDefence: return Read158(data.ActiveDefence, context) ?? throw Invalid("Missing defence payload");
                    case MissionKind.Attack: return Read188(data.Attack, context) ?? throw Invalid("Missing attack payload");
                    default: throw Invalid("Unknown mission objective tag");
                }
            }
        }

        private static MatchAiObjectiveData Write195(MatchAiObjectiveData data, Context c) => data;
        private static MatchAiObjectiveData Read195(MatchAiObjectiveData data, Context c)
        {
            Required(data);
            // Decode immediately, including nested finite numbers/enums/references, without changing any owner.
            new Objectives(c).Restore(CheckedEnum<MissionKind>(data.Tag), data);
            return data;
        }

        internal sealed class Context
        {
            private readonly Func<PlayerSetupData, string> capturePlayer;
            private readonly Func<string, PlayerSetupData> player;
            private readonly Func<int, ArmyData> army;
            private readonly Func<int, UnitData> unit;
            private readonly Func<int, CardData> card;
            private readonly Action<ArmyData> includeArmy;
            private readonly Action<UnitData> includeUnit;
            private readonly Action<CardData> includeCard;
            internal readonly Dictionary<string, PlayerSetupData> Players = new Dictionary<string, PlayerSetupData>(StringComparer.Ordinal);
            internal readonly HashSet<int> ArmyIds = new HashSet<int>();
            internal readonly HashSet<int> UnitIds = new HashSet<int>();
            internal readonly HashSet<int> CardIds = new HashSet<int>();
            private readonly Dictionary<int, ArmyData> captureArmies = new Dictionary<int, ArmyData>();
            private readonly Dictionary<int, CardData> captureCards = new Dictionary<int, CardData>();
            private readonly HashSet<int> includedArmies = new HashSet<int>();
            private readonly HashSet<int> includedCards = new HashSet<int>();
            private readonly Dictionary<int, UnitData> captureUnits = new Dictionary<int, UnitData>();
            internal Context(Func<PlayerSetupData, string> id, Action<ArmyData> includeArmy, Action<UnitData> includeUnit, Action<CardData> includeCard)
            {
                capturePlayer = id ?? throw new ArgumentNullException(nameof(id));
                this.includeArmy = includeArmy; this.includeUnit = includeUnit; this.includeCard = includeCard;
                foreach (var hex in ArmyRegistry.AllOccupiedHexes()) foreach (var a in ArmyRegistry.AllAt(hex)) {
                    captureArmies[a.Id] = a; foreach (var u in a.Members) captureUnits[u.RuntimeId] = u;
                }
            }
            internal Context(Func<string, PlayerSetupData> player, Func<int, ArmyData> army, Func<int, UnitData> unit, Func<int, CardData> card)
            {
                this.player = player ?? throw new ArgumentNullException(nameof(player));
                this.army = army ?? throw new ArgumentNullException(nameof(army));
                this.unit = unit ?? throw new ArgumentNullException(nameof(unit));
                this.card = card ?? throw new ArgumentNullException(nameof(card));
            }
            internal string PlayerId(PlayerSetupData value)
            {
                string id = capturePlayer != null ? AiPersistenceCollections.CapturePlayerId(value, capturePlayer) : value.PlayerId;
                if (capturePlayer == null && !ReferenceEquals(ReadPlayerId(id), value)) throw Invalid("Noncanonical player reference");
                if (Players.TryGetValue(id, out var previous) && !ReferenceEquals(previous, value)) throw Invalid("Duplicate player identity");
                Players[id] = value; return id;
            }
            internal PlayerSetupData ReadPlayerId(string id)
            {
                var bound = player != null ? AiPersistenceCollections.ResolvePlayer(id, player) : Players.TryGetValue(id, out var captured) ? captured : throw Invalid("Unresolved captured player");
                if (Players.TryGetValue(id, out var previous) && !ReferenceEquals(previous, bound)) throw Invalid("Unstable player resolver");
                Players[id] = bound; return bound;
            }
            internal ArmyData ReadArmyId(int id) { if (id < 0) throw Invalid("Negative army reference"); var result = army != null ? AiPersistenceCollections.ResolveArmy(id, army) : captureArmies.TryGetValue(id, out var a) ? a : throw Invalid("Unresolved captured army"); ArmyIds.Add(id); return result; }
            internal CardData ReadCardId(int id) { if (id < 0) throw Invalid("Negative card reference"); var result = card != null ? AiPersistenceCollections.ResolveCard(id, card) : captureCards.TryGetValue(id, out var a) ? a : throw Invalid("Unresolved captured card"); CardIds.Add(id); return result; }
            internal UnitData ReadUnitId(int id) { var result = unit != null ? unit(id) : captureUnits.TryGetValue(id, out var u) ? u : null; if (id < 0 || result == null || result.RuntimeId != id) throw Invalid("Unresolved unit identity"); UnitIds.Add(id); return result; }
            internal void IncludeArmy(ArmyData value) { if (value == null) return; captureArmies[value.Id] = value; foreach (var u in value.Members) captureUnits[u.RuntimeId] = u; ArmyIds.Add(value.Id); if (includedArmies.Add(value.Id)) includeArmy?.Invoke(value); }
            internal void IncludeCard(CardData value) { if (value == null) return; CardIds.Add(value.RuntimeId); captureCards[value.RuntimeId] = value; if (includedCards.Add(value.RuntimeId)) includeCard?.Invoke(value); }
            internal void RecordArmy(int id) { if (id < 0) return; if (captureArmies.TryGetValue(id, out var a)) IncludeArmy(a); else ArmyIds.Add(id); }
            internal void RecordUnit(int id) { if (id < 0) return; if (UnitIds.Add(id) && captureUnits.TryGetValue(id, out var u)) includeUnit?.Invoke(u); }
            internal void RecordCard(int id) { if (id >= 0) CardIds.Add(id); }
            internal MatchAiPlayerRef WritePlayer(PlayerSetupData value) => new MatchAiPlayerRef { Present = value != null, Id = value == null ? null : PlayerId(value) };
            internal MatchAiArmyRef WriteArmy(ArmyData value) { IncludeArmy(value); return new MatchAiArmyRef { Present = value != null, Id = value?.Id ?? 0 }; }
            internal MatchAiUnitRef WriteUnit(UnitData value) { if (value != null) { captureUnits[value.RuntimeId] = value; RecordUnit(value.RuntimeId); includeUnit?.Invoke(value); } return new MatchAiUnitRef { Present = value != null, Id = value?.RuntimeId ?? 0 }; }
            internal MatchAiCardRef WriteCard(CardData value) { IncludeCard(value); return new MatchAiCardRef { Present = value != null, Id = value?.RuntimeId ?? 0 }; }
            internal PlayerSetupData ReadPlayer(MatchAiPlayerRef value) { Required(value); return value.Present ? ReadPlayerId(value.Id) : null; }
            internal ArmyData ReadArmy(MatchAiArmyRef value) { Required(value); return value.Present ? ReadArmyId(value.Id) : null; }
            internal UnitData ReadUnit(MatchAiUnitRef value) { Required(value); return value.Present ? ReadUnitId(value.Id) : null; }
            internal CardData ReadCard(MatchAiCardRef value) { Required(value); return value.Present ? ReadCardId(value.Id) : null; }
        }

        private static void Owners<T>(IReadOnlyDictionary<string, T> rows, Context c)
        { Required(rows); foreach (string id in rows.Keys) c.ReadPlayerId(id); }

        private static void ValidateState(AiPersistentState<MatchAiObjectiveData> s, Context c, GameConfig config)
        {
            Required(s.Map); Owners(s.Map.Players, c);
            Owners(s.Missions, c); Owners(s.Sorties, c); Owners(s.Radar, c); Owners(s.ForceBaseline, c);
            Owners(s.PreparationDelivery, c); Required(s.Recon); Owners(s.Recon.Observations, c); Owners(s.Recon.ObservedTurns, c);
            Required(s.Intel); Owners(s.Intel.Observations, c); Owners(s.Intel.CurrentTurns, c); Owners(s.Patrols, c);
            Required(s.ReconSorties); Owners(s.ReconSorties.Players, c); Owners(s.ReconCoverage, c); Owners(s.ReconDeficits, c);
            Owners(s.ScoutTrails, c); Owners(s.Starvation, c); Owners(s.DevelopmentGate, c); Owners(s.Allocator, c);
            Owners(s.Initiative, c); Owners(s.PublicInitiative, c); Owners(s.MobilizationHold, c); Owners(s.DevelopmentHistory, c);
            Owners(s.ApPressure, c); Owners(s.LifecycleWaits, c);
            foreach (var radar in s.Radar.Values) { Required(radar); Required(radar.Smoothed); }
            foreach (var row in s.Intel.Observations.Values) Required(row);
            foreach (var row in s.Patrols.Values) Required(row);
            foreach (var row in s.ReconSorties.Players.Values) { Required(row); foreach (var sortie in row.Values) Required(sortie.Trail); }
            foreach (var row in s.ReconDeficits.Values) Required(row);
            foreach (var row in s.ReconCoverage.Values) { Required(row); foreach (var coverage in row.Values) Required(coverage); }
            foreach (var row in s.ScoutTrails.Values) { Required(row); foreach (var trail in row.Values) Required(trail.Recent); }
            foreach (var row in s.Starvation.Values) {
                Required(row); Required(row.Forecasts); Required(row.Pressure); Required(row.CurrentBlocks);
                Required(row.VerifiedPending); Required(row.LastPressureHitTurn); Unique(row.VerifiedPending);
                foreach (var forecast in row.Forecasts) { if (string.IsNullOrEmpty(forecast.Key)) throw Invalid("Missing forecast source"); Required(forecast.Value); Required(forecast.Value.Needs); }
            }
            foreach (var row in s.DevelopmentGate.Values) Required(row.Streak);
            foreach (var row in s.Initiative.Values) Required(row);
            foreach (var row in s.PublicInitiative.Values) Required(row);
            foreach (var row in s.ApPressure.Values) Required(row);
            foreach (var row in s.Map.Players.Values) {
                Required(row); Required(row.Guards); Required(row.Events); Required(row.StructureLosses); Unique(row.Resources.Select(x => x.Hex)); Unique(row.Enemies.Select(x => x.Observation.ArmyId));
                Unique(row.Aircraft.Select(x => x.Observation.ArmyId)); Unique(row.Buildings.Select(x => x.Observation.Hex));
                Unique(row.DestroyedArmies); Unique(row.RefutedCitadels);
                foreach (string id in row.Enemies.Select(x => x.OwnerId).Concat(row.Aircraft.Select(x => x.OwnerId)).Concat(row.Buildings.Select(x => x.OwnerId)))
                    if (id != null) c.ReadPlayerId(id);
            }
            foreach (var row in s.Recon.Observations.Values) foreach (var observation in row) {
                if (observation.Key != observation.Value.ArmyId) throw Invalid("Recon observation ID mismatch");
                if (observation.Value.OwnerId != null) c.ReadPlayerId(observation.Value.OwnerId);
            }
            var objectives = new Objectives(c);
            foreach (var owner in s.Missions) {
                Required(owner.Value);
                // Creates private detached stores only. It validates intent keys, references and duplicate retreat witnesses.
                MissionIntentState.RestorePersistentState(c.ReadPlayerId(owner.Key), owner.Value, objectives, c.ReadCardId, c.ReadPlayerId, reserveIdentities: false);
                foreach (var intent in owner.Value.Intents) {
                    CheckKey(intent.IntentKey); CheckKey(intent.LastAttemptKey);
                    var obj = objectives.Restore(intent.Kind, intent.Objective);
                    if (obj is RaidIntent raid && !raid.Target.HasValue) throw Invalid("Raid target is missing");
                    if (obj is AttackIntent attack) {
                        if (!attack.Target.HasValue) throw Invalid("Attack target is missing");
                        if (attack.TargetRoster != null) foreach (var slot in attack.TargetRoster) CardKey(slot.Key, config, true);
                        CardKey(attack.TargetRosterCommanderKey, config, false);
                        Unique(attack.GatherSupportArmyIds); Unique(attack.GatherReturns.Select(x => x.ArmyId));
                    }
                    if (obj is DevelopmentIntent development) CardKey(development.HeroKey, config, false);
                }
            }
            foreach (var owner in s.Sorties) {
                Required(owner.Value); Unique(owner.Value.Select(x => x.ArmyId));
                foreach (var sortie in owner.Value) if (!ReferenceEquals(c.ReadArmyId(sortie.ArmyId).Owner, c.ReadPlayerId(owner.Key))) throw Invalid("Sortie owner mismatch");
            }
            int high = s.ReconSorties.Players.Values.SelectMany(x => x.Values).Select(x => x.SortieId).DefaultIfEmpty(0).Max();
            if (s.ReconSorties.NextSortieId <= high) throw Invalid("Recon sortie sequence is invalid");
            foreach (var owner in s.PreparationDelivery.Values) foreach (var delivery in owner.Values) foreach (var key in delivery.CardKeys) CardKey(key, config, true);
            foreach (var owner in s.DevelopmentHistory.Values) foreach (var history in owner) CardKey(history.CardKey, config, true);
            foreach (var owner in s.Allocator.Values) foreach (var key in owner.Keys) CheckKey(key);
            foreach (var owner in s.LifecycleWaits.Values) foreach (var key in owner.Keys) CheckKey(key);
        }

        private static void CollectIdentities(AiPersistentState<MatchAiObjectiveData> s, Context c)
        {
            // Run explicit writers to collect named historical IDs inside nested snapshot/objective fields.
            Write0(s, c);
            foreach (var p in s.Allocator.Values) foreach (var key in p.Keys) RecordKey(key, c);
            foreach (var p in s.LifecycleWaits.Values) foreach (var key in p.Keys) RecordKey(key, c);
            foreach (var p in s.PreparationDelivery.Values) foreach (int id in p.Keys) c.RecordArmy(id);
            foreach (var p in s.Patrols.Values) foreach (int id in p.Keys) c.RecordArmy(id);
            foreach (var p in s.ReconSorties.Players.Values) foreach (int id in p.Keys) c.RecordArmy(id);
            foreach (var p in s.ScoutTrails.Values) foreach (int id in p.Keys) c.RecordArmy(id);
            foreach (var m in s.Missions.Values) {
                foreach (int id in m.Development.Keys) c.RecordCard(id);
                foreach (var key in m.Economy.BaseDelivery.Failures.Keys.Concat(m.Economy.BaseDelivery.SuppressedUntil.Keys).Concat(m.Economy.BaseDelivery.ProgressTurns.Keys)) c.RecordCard(key.CardId);
                foreach (int id in m.Economy.EndedOutboundBuilders.Keys) c.RecordArmy(id);
                foreach (var i in m.Intents) {
                    RecordKey(i.IntentKey, c); RecordKey(i.LastAttemptKey, c);
                    var obj = new Objectives(c).Restore(i.Kind, i.Objective);
                    if (obj is ScoutIntent a) Write146(a, c);
                    else if (obj is RaidIntent b) Write148(b, c);
                    else if (obj is EconomyIntent d) Write151(d, c);
                    else if (obj is DevelopmentIntent e) Write156(e, c);
                    else if (obj is ActiveDefenceIntent f) Write158(f, c);
                    else if (obj is AttackIntent g) Write188(g, c);
                }
            }
        }

        private static void CheckKey(MissionIntentKey key)
        {
            CheckedEnum<MissionKind>((int)key.Kind); CheckedEnum<RaidTargetKind>((int)key.TargetKind);
            switch (key.Kind) {
                case MissionKind.Scout: CheckedEnum<ScoutTargetKind>(key.SubKind); break;
                case MissionKind.Raid: if (key.SubKind != (int)AggressionObjectiveKind.Raid) throw Invalid("Unknown raid key"); break;
                case MissionKind.ActiveDefence: CheckedEnum<ActiveDefencePhase>(key.SubKind); break;
                case MissionKind.Economy: CheckedEnum<EconomyTaskKind>(key.SubKind); break;
                case MissionKind.Development: CheckedEnum<ResearchProductionMode>(key.SubKind); break;
                case MissionKind.Attack: if (key.SubKind != (int)AggressionObjectiveKind.Attack) throw Invalid("Unknown attack key"); break;
            }
        }
        private static void CheckKey(StableMissionKey key)
        {
            CheckedEnum<MissionKind>((int)key.Kind); CheckedEnum<RaidTargetKind>((int)key.TargetKind);
            switch (key.Kind) {
                case MissionKind.Scout: CheckedEnum<ScoutTargetKind>(key.SubKind); break;
                case MissionKind.Raid: CheckedEnum<RaidMissionPhase>(key.SubKind); break;
                case MissionKind.ActiveDefence: CheckedEnum<ActiveDefencePhase>(key.SubKind); break;
                case MissionKind.Economy: CheckedEnum<EconomyTaskKind>(key.SubKind); break;
                case MissionKind.Development: CheckedEnum<ResearchProductionMode>(key.SubKind); break;
                case MissionKind.Attack: CheckedEnum<AttackMissionPhase>(key.SubKind); break;
            }
        }
        private static void RecordKey(MissionIntentKey key, Context c)
        {
            if (key.Kind == MissionKind.Raid && key.TargetKind == RaidTargetKind.NeutralArmy
                || key.Kind == MissionKind.ActiveDefence
                || key.Kind == MissionKind.Economy && (key.SubKind == (int)EconomyTaskKind.ReturnBuilder || key.SubKind == (int)EconomyTaskKind.ReturnCollector)) c.RecordArmy(key.ObjectiveId);
        }
        private static void RecordKey(StableMissionKey key, Context c)
        {
            if (key.Kind == MissionKind.Raid && (key.SubKind != (int)RaidMissionPhase.Assault || key.TargetKind == RaidTargetKind.NeutralArmy)
                || key.Kind == MissionKind.ActiveDefence
                || key.Kind == MissionKind.Attack && key.SubKind != (int)AttackMissionPhase.Assault
                || key.Kind == MissionKind.Economy && (key.SubKind == (int)EconomyTaskKind.ReturnBuilder || key.SubKind == (int)EconomyTaskKind.ReturnCollector)) c.RecordArmy(key.TargetId);
            if (key.ActorId != 0) c.RecordArmy(key.ActorId);
        }
        private static void CardKey(string key, GameConfig config, bool required)
        {
            if (string.IsNullOrEmpty(key)) { if (required) throw Invalid("Missing authored card key"); return; }
            if (ReferenceEquals(config, null)) return; // Restore repeats structural validation; preflight Validate owns content compatibility.
            var catalog = config.collectionDeckCatalog;
            var definition = ReferenceEquals(catalog, null) ? null : FactionCardCatalog.ResolveAcross(catalog.catalogs, key, false, catalog);
            if (definition == null || !string.Equals(definition.authoredKey, key, StringComparison.Ordinal)) throw Invalid("Unknown authored AI card key: " + key);
        }
        private static void CheckIdentityManifest(List<int> ids, HashSet<int> used)
        {
            Required(ids); Unique(ids);
            if (ids.Any(x => x < 0) || !used.SetEquals(ids)) throw Invalid("AI historical identity manifest mismatch");
        }
        private static void Unique<T>(IEnumerable<T> values) { Required(values); var seen = new HashSet<T>(); foreach (var value in values) if (!seen.Add(value)) throw Invalid("Duplicate AI identity"); }
        private static void Required(object value) { if (value == null) throw Invalid("Missing AI payload"); }
        private static ArgumentException Invalid(string message) => new ArgumentException(message);
        private static float Finite(float value) { if (float.IsNaN(value) || float.IsInfinity(value)) throw Invalid("Nonfinite AI value"); return value; }
        private static double Finite(double value) { if (double.IsNaN(value) || double.IsInfinity(value)) throw Invalid("Nonfinite AI value"); return value; }
        private static T CheckedEnum<T>(int value) where T : struct, Enum { if (!Enum.IsDefined(typeof(T), value)) throw Invalid("Unknown AI enum value"); return (T)Enum.ToObject(typeof(T), value); }

        private static readonly string[] PredicateTags = { UnitAbilities.Scorcher, UnitAbilities.Pyrokinetic, UnitAbilities.Ionic, UnitAbilities.Drill, UnitAbilities.Heal, UnitAbilities.Transport };
        private static string WritePredicate(Func<WorthIt.DefenderProfile, bool> predicate)
        {
            if (predicate == null) return null;
            foreach (var tag in PredicateTags) if (predicate == ReadPredicate(tag)) return tag;
            throw Invalid("Unknown AI strategic predicate");
        }
        private static Func<WorthIt.DefenderProfile, bool> ReadPredicate(string tag)
        {
            if (tag == null) return null;
            if (!PredicateTags.Contains(tag)) throw Invalid("Unknown AI strategic predicate tag");
            return StrategicEffectRegistry.Resolve(new[] { tag }, 0).Single(x => x.EligiblePredicate != null).EligiblePredicate;
        }
    }
}
