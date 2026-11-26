#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  HOUSEKEEPING STAND (owner request 2026-11-25)
    //  One own hex (the Citadel or a secondary Base) built from the AI players' authored decks, run
    //  through the real end-of-turn Housekeeping entry (HousekeepingManager.Run: Analyzer ->
    //  Planner -> Executor) one or more turns. A case places armies, gives some of them a task
    //  (the real MissionIntent -> ActorCommitments.FromIntents path), moves enemy armies in sight
    //  (the real AiMapMemory observation) and states what Housekeeping must do.
    //
    //  Every turn is also checked against what Housekeeping is FOR (keep the hex safe against a
    //  known or a sudden attack) and against its boundaries:
    //    * the hex hold against every known enemy (WorthIt.EstimateHexControl, the one hex-hold
    //      model) never drops;
    //    * without a known enemy the garrison keeps its defence floor;
    //    * no unit is lost, duplicated or moved off the hex; every army stays within capacity;
    //    * an army with a task keeps its contract (MissionActorPolicy): nothing leaves a claimed
    //      army, a closed one receives nothing, a route-bound one is never slowed, a preparation
    //      host is never weakened;
    //    * every operation succeeds, no AP is spent, the live result is the plan;
    //    * the next pass on the result plans nothing (no ping-pong).
    //  Then the night and the morning of the same turn (owner 2026-11-25): the night fold
    //  (HousekeepingNightFold.Fold) joins the hex into the fewest fighting formations — the hold
    //  never drops against the day, no AP, no unit lost, the garrison keeps a body — and the
    //  owner's next turn starts with Restore: every army with a task stands exactly as it did by
    //  day (members and commander), so the tasks leave with what the day gave them; a fold of the
    //  garrison and free armies stays (owner 2026-11-26). Case expectations read the day
    //  (StandTurn.After); ExpectNight* read the night.
    //  Units are materialized from the decks' cards (UnitData.FromDefinition); roles (combat /
    //  support hero, strong / weak body, ...) are picked with the game's own classifiers, so one
    //  case runs on every deck. A deck without the role a case needs ignores that case.
    // ===========================================================================================
    internal enum HeroPick { Combat, Combat2, Support, Support2, BigCommand, SmallCommand, ArmySupport }

    internal enum BodyPick { Strong, Mid, Weak, Slow, Fast, Recce, Healer, Air, Collector }

    internal enum EnemyForce
    {
        Raiders,   // two of the weakest bodies, no hero
        Company,   // a combat hero with three middle bodies
        Host,      // the best combat hero with its full capacity of the strongest bodies
    }

    internal enum StandSite { Citadel, Base }

    internal sealed class StandDeck
    {
        public readonly string Name;
        public readonly StartingDeck Deck;
        public readonly List<CardDefinition> Cards;

        public StandDeck(StartingDeck deck, StartingDeckCatalog catalog)
        {
            Name = deck.deckName;
            Deck = deck;
            Cards = deck.cards.Where(e => e != null && e.count > 0)
                .Select(e => catalog.ResolveCard(e.cardKey)).Where(c => c != null)
                .Distinct().ToList();
        }

        public override string ToString() => Name;
    }

    internal sealed class HousekeepingStand : IDisposable
    {
        // ---- decks --------------------------------------------------------------------------
        // Two authored AI decks of every faction; each fights the paired deck of another faction.
        internal static readonly (string Own, string Enemy)[] DeckPairs =
        {
            ("Dorian Kesh", "Hank Mercer"),
            ("Vector Vance", "Rusty Miller"),
            ("Hank Mercer", "Orr the Keeper"),
            ("Rusty Miller", "Nyx the Cartographer"),
            ("Orr the Keeper", "Dorian Kesh"),
            ("Nyx the Cartographer", "Vector Vance"),
        };

        private const string CatalogPath = "Assets/Cards/StartingDeckCatalog.asset";
        private static StartingDeckCatalog _catalog;

        internal static StartingDeckCatalog Catalog
        {
            get
            {
                if (_catalog == null)
                    _catalog = AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>(CatalogPath);
                return _catalog;
            }
        }

        private static StandDeck LoadDeck(string name)
        {
            StartingDeck deck = Catalog?.aiDecks?.FirstOrDefault(d => d != null && d.deckName == name);
            if (deck == null)
                throw new InvalidOperationException($"AI deck '{name}' is not in {CatalogPath}");
            return new StandDeck(deck, Catalog);
        }

        // ---- world --------------------------------------------------------------------------
        public static readonly HexCoord Home = new HexCoord(0, 0);
        public readonly StandDeck OwnDeck, EnemyDeck;
        public readonly PlayerSetupData Me, Foe;
        public readonly StandSite Site;
        public readonly AiTurnContext Ctx;
        public int Turn => Ctx.TurnNumber;
        private readonly PlayerRoot _root;
        private readonly Dictionary<UnitData, string> _labels = new Dictionary<UnitData, string>();
        private readonly List<ArmyData> _enemies = new List<ArmyData>();
        private readonly Dictionary<HeroPick, CardDefinition> _heroCache = new Dictionary<HeroPick, CardDefinition>();
        private readonly HashSet<CardDefinition> _heroesUsed = new HashSet<CardDefinition>();
        private readonly HashSet<CardDefinition> _enemyHeroesUsed = new HashSet<CardDefinition>();

        public string CaseName;
        public string CaseText;
        public readonly List<StandTurn> Turns = new List<StandTurn>();
        public StandTurn Last => Turns.Count > 0 ? Turns[Turns.Count - 1] : null;
        public readonly List<string> Failures = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public readonly Stopwatch HousekeepingTime = new Stopwatch();

        public HousekeepingStand(string ownDeck, string enemyDeck, StandSite site = StandSite.Citadel)
        {
            ClearWorld();
            OwnDeck = LoadDeck(ownDeck);
            EnemyDeck = LoadDeck(enemyDeck);
            Site = site;
            Me = new PlayerSetupData { Nickname = "Stand", ColorIndex = 0, Faction = OwnDeck.Deck.faction };
            Foe = new PlayerSetupData { Nickname = "Foe", ColorIndex = 1, Faction = EnemyDeck.Deck.faction };
            // A secondary Base: the Citadel stands far away, out of this hex's group.
            HexCoord citadel = site == StandSite.Citadel ? Home : new HexCoord(-12, 0);
            Me.CitadelHexQ = citadel.Q;
            Me.CitadelHexR = citadel.R;
            Foe.CitadelHexQ = 20;
            Foe.CitadelHexR = 0;
            Ctx = new AiTurnContext { StartingDeckCatalog = Catalog, TurnNumber = 7 };
            _root = PlayerRoot.Create(Me, "housekeeping stand");
            _root.ActionPoints = 10;
            PlayerRootRegistry.Register(Me, _root);

            // The building the game places: the faction's Base card, as CitadelSetupController does.
            CardDefinition baseCard = Catalog.GetCatalog(Me.Faction)?.ForType(CardType.Base).FirstOrDefault();
            BuildingData building = BuildingData.FromDefinition(baseCard, Home, Me)
                ?? new BuildingData { Hex = Home, Owner = Me, IsBase = true, Defense = 1 };
            building.IsStartingCitadel = site == StandSite.Citadel;
            BuildingRegistry.Register(Home, building);
            AiMapMemory.EnsureSubscribed();
        }

        public void Dispose()
        {
            if (_root != null)
                Object.DestroyImmediate(_root.gameObject);
            ClearWorld();
        }

        private static void ClearWorld()
        {
            HousekeepingNightFold.ClearAll();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            AiMapMemory.Clear();
            AiReconMemory.Clear();
            PlayerRootRegistry.Clear();
            AiHandRegistry.Clear();
            MissionIntentRegistry.Clear();
            StrategicCapabilityLeaseRegistry.ClearAll();
            ReconPatrolStateRegistry.ClearAll();
            StrategicResourceReservationLedger.ClearAll();
        }

        // ---- units --------------------------------------------------------------------------
        private static bool IsSupportTagged(CardDefinition c) =>
            c.unitTypeTags != null && c.unitTypeTags.Contains(UnitTypeTag.Support);

        private static bool Has(CardDefinition c, string ability) =>
            c.grantedAbilities != null && c.grantedAbilities.Contains(ability);

        private static bool HasAny(CardDefinition c, IEnumerable<string> abilities) =>
            abilities.Any(a => Has(c, a));

        private UnitData Sample(CardDefinition c, PlayerSetupData owner) => UnitData.FromDefinition(c, owner);

        private static float Power(UnitData u) => AiPower.ToPowerUnit(u).BasePower;

        private IEnumerable<CardDefinition> HeroCards(StandDeck deck) =>
            deck.Cards.Where(c => c.cardType == CardType.Hero);

        private CardDefinition PickHeroCard(StandDeck deck, HeroPick pick, PlayerSetupData owner,
            ISet<CardDefinition> used)
        {
            List<CardDefinition> heroes = HeroCards(deck).Where(c => !used.Contains(c))
                .OrderBy(c => c.displayName, StringComparer.Ordinal).ToList();
            List<CardDefinition> field = heroes.Where(c => !IsSupportTagged(c)).ToList();
            switch (pick)
            {
                case HeroPick.Combat:
                case HeroPick.Combat2:
                    // The best field leader by the game's own ranking: a non-support role, then
                    // combat leadership (CommandRating and pace).
                    return field.Where(c => HeroRoleEvaluator.Classify(c) != HeroOperationalRole.SupportOperator)
                        .OrderByDescending(HeroRoleEvaluator.CombatLeadershipScore).FirstOrDefault();
                case HeroPick.Support:
                case HeroPick.Support2:
                    return heroes.Where(IsSupportTagged).OrderByDescending(c => c.commandRating).FirstOrDefault();
                case HeroPick.BigCommand:
                    return field.OrderByDescending(c => c.commandRating).FirstOrDefault();
                case HeroPick.SmallCommand:
                    return field.OrderBy(c => c.commandRating).FirstOrDefault();
                case HeroPick.ArmySupport:
                    return field.FirstOrDefault(c => StrategicEffectRegistry.IsArmySupportCarrier(Sample(c, owner)));
            }
            return null;
        }

        // A hero of the own deck. Every pick is a different card (a deck holds each hero once).
        public UnitData Hero(HeroPick pick)
        {
            CardDefinition card = PickHeroCard(OwnDeck, pick, Me, _heroesUsed);
            if (card == null)
                NUnit.Framework.Assert.Ignore($"{OwnDeck.Name}: no {pick} hero left for this case");
            _heroesUsed.Add(card);
            return Make(card, Me, pick.ToString());
        }

        private IEnumerable<CardDefinition> PlainBodyCards(StandDeck deck, PlayerSetupData owner) =>
            deck.Cards.Where(c => c.cardType == CardType.Unit && !c.isAviation
                && !IsSupportTagged(c) && !HasAny(c, UnitAbilities.CollectAbilities)
                && c.airfieldCapacity <= 0)
                .Where(c =>
                {
                    UnitData u = Sample(c, owner);
                    return AiArmyRoles.IsGroundBattleBody(u) && !AbilityParams.UnitHasAnyRecce(u)
                        && !StrategicEffectRegistry.IsArmySupportCarrier(u) && u.MoveMax >= 1;
                });

        private CardDefinition PickBodyCard(StandDeck deck, BodyPick pick, PlayerSetupData owner)
        {
            List<CardDefinition> plain = PlainBodyCards(deck, owner)
                .OrderBy(c => Power(Sample(c, owner))).ThenBy(c => c.displayName, StringComparer.Ordinal).ToList();
            switch (pick)
            {
                case BodyPick.Strong: return plain.LastOrDefault();
                case BodyPick.Weak: return plain.FirstOrDefault();
                case BodyPick.Mid: return plain.Count == 0 ? null : plain[plain.Count / 2];
                case BodyPick.Slow:
                {
                    int fast = plain.Select(c => c.moveMax).DefaultIfEmpty(0).Max();
                    return plain.Where(c => c.moveMax < fast).OrderBy(c => c.moveMax)
                        .ThenByDescending(c => Power(Sample(c, owner))).FirstOrDefault();
                }
                case BodyPick.Fast:
                    return plain.OrderByDescending(c => c.moveMax).ThenByDescending(c => Power(Sample(c, owner)))
                        .FirstOrDefault();
                case BodyPick.Recce:
                    return deck.Cards.Where(c => c.cardType == CardType.Unit && !c.isAviation)
                        .FirstOrDefault(c => AbilityParams.UnitHasAnyRecce(Sample(c, owner)));
                case BodyPick.Healer:
                    return deck.Cards.Where(c => c.cardType == CardType.Unit && !c.isAviation)
                        .FirstOrDefault(c => Has(c, UnitAbilities.Heal));
                case BodyPick.Air:
                    return deck.Cards.FirstOrDefault(c => c.cardType == CardType.Unit && c.isAviation);
                case BodyPick.Collector:
                    return deck.Cards.FirstOrDefault(c => c.cardType == CardType.Unit && !c.isAviation
                        && HasAny(c, UnitAbilities.CollectAbilities));
            }
            return null;
        }

        // A body of the own deck (a deck card may be materialized more than once).
        public UnitData Body(BodyPick pick)
        {
            CardDefinition card = PickBodyCard(OwnDeck, pick, Me);
            if (card == null)
                NUnit.Framework.Assert.Ignore($"{OwnDeck.Name}: no {pick} body");
            return Make(card, Me, pick.ToString());
        }

        public UnitData[] Bodies(BodyPick pick, int count) =>
            Enumerable.Range(0, count).Select(_ => Body(pick)).ToArray();

        private UnitData Make(CardDefinition card, PlayerSetupData owner, string role)
        {
            UnitData u = UnitData.FromDefinition(card, owner);
            int same = _labels.Keys.Count(k => k.OriginatingCard == card && k.Owner == owner);
            _labels[u] = $"{card.displayName}{(same > 0 ? "#" + (same + 1) : "")}({role}"
                + (u.IsHero ? $",CR{u.CommandRating},{HeroRoleEvaluator.Classify(u)}" : $",P{Power(u):0.#},MP{u.MoveMax}")
                + ")";
            return u;
        }

        public string Label(UnitData u) => u == null ? "-" : _labels.TryGetValue(u, out string l) ? l : u.Name;

        // ---- armies -------------------------------------------------------------------------
        public ArmyData Garrison(params UnitData[] units) => Army(true, units);
        public ArmyData Field(params UnitData[] units) => Army(false, units);
        public ArmyData Shell() => Army(false);

        private ArmyData Army(bool garrison, params UnitData[] units)
        {
            var army = new ArmyData { Owner = Me, Hex = Home, IsGarrison = garrison };
            foreach (UnitData u in units)
                army.AddMemberSorted(u);
            ArmyRegistry.Register(army);
            return army;
        }

        // A garrison whose roster order is given as is (not normalized by CommandRating).
        public ArmyData GarrisonRaw(params UnitData[] units)
        {
            var army = new ArmyData { Owner = Me, Hex = Home, IsGarrison = true };
            army.Members.AddRange(units);
            ArmyRegistry.Register(army);
            return army;
        }

        // A prison on the hex holding an enemy captive (never Housekeeping's).
        public ArmyData Prison()
        {
            List<CardDefinition> plain = PlainBodyCards(EnemyDeck, Foe).ToList();
            if (plain.Count == 0)
                NUnit.Framework.Assert.Ignore($"{EnemyDeck.Name}: no enemy bodies");
            var army = new ArmyData { Owner = Me, Hex = Home, IsPrison = true };
            army.Members.Add(Make(plain[0], Foe, "captive"));
            ArmyRegistry.Register(army);
            return army;
        }

        public static void SpendMovement(UnitData u) => u.MoveCurrent = 0;

        public static void Wound(UnitData u) => u.HitPointsCurrent = Math.Max(1, u.HitPointsMax / 3);

        // An aircraft wing standing on the hex (Aviation: never Housekeeping's).
        public ArmyData AirWing(params UnitData[] aircraft)
        {
            var army = new ArmyData { Owner = Me, Hex = Home, IsAirArmy = true };
            foreach (UnitData u in aircraft)
                army.AddMemberSorted(u);
            ArmyRegistry.Register(army);
            return army;
        }

        // ---- enemies ------------------------------------------------------------------------
        // An enemy army `distance` hexes away, seen now by the own player.
        public ArmyData Enemy(int distance, EnemyForce force)
        {
            var army = new ArmyData { Owner = Foe, Hex = new HexCoord(distance, 0) };
            List<CardDefinition> plain = PlainBodyCards(EnemyDeck, Foe)
                .OrderBy(c => Power(Sample(c, Foe))).ThenBy(c => c.displayName, StringComparer.Ordinal).ToList();
            if (plain.Count == 0)
                NUnit.Framework.Assert.Ignore($"{EnemyDeck.Name}: no enemy bodies");
            switch (force)
            {
                case EnemyForce.Raiders:
                    army.AddMemberSorted(Make(plain[0], Foe, "enemy"));
                    army.AddMemberSorted(Make(plain[0], Foe, "enemy"));
                    break;
                case EnemyForce.Company:
                case EnemyForce.Host:
                {
                    CardDefinition heroCard = PickHeroCard(EnemyDeck, HeroPick.Combat, Foe, _enemyHeroesUsed)
                        ?? PickHeroCard(EnemyDeck, HeroPick.BigCommand, Foe, _enemyHeroesUsed);
                    if (heroCard == null)
                        NUnit.Framework.Assert.Ignore($"{EnemyDeck.Name}: no enemy hero left");
                    _enemyHeroesUsed.Add(heroCard);
                    UnitData hero = Make(heroCard, Foe, "enemy");
                    army.AddMemberSorted(hero);
                    int bodies = force == EnemyForce.Company ? 3 : Math.Max(1, hero.CommandRating - 1);
                    CardDefinition body = force == EnemyForce.Company ? plain[plain.Count / 2] : plain[plain.Count - 1];
                    for (int i = 0; i < bodies; i++)
                        army.AddMemberSorted(Make(body, Foe, "enemy"));
                    break;
                }
            }
            ArmyRegistry.Register(army);
            _enemies.Add(army);
            return army;
        }

        // The same army placed so that it reaches the hex in `eta` turns (its MaxMovement).
        public ArmyData EnemyAtEta(int eta, EnemyForce force)
        {
            ArmyData enemy = Enemy(1, force);
            MoveEnemy(enemy, Math.Max(1, eta * Math.Max(1, enemy.MaxMovement)));
            return enemy;
        }

        // A Disruption carrier of the enemy deck joins `enemy` (it strips the Base's building bonus).
        public void GiveDisruption(ArmyData enemy)
        {
            CardDefinition carrier = EnemyDeck.Cards.FirstOrDefault(c => c.cardType == CardType.Unit
                && !c.isAviation && Has(c, UnitAbilities.Disruption));
            if (carrier == null)
                NUnit.Framework.Assert.Ignore($"{EnemyDeck.Name}: no Disruption carrier");
            TestRoster.AddSorted(enemy, Make(carrier, Foe, "disruption"));
        }

        public void MoveEnemy(ArmyData enemy, int distance)
        {
            ArmyRegistry.Unregister(enemy);
            enemy.Hex = new HexCoord(distance, 0);
            ArmyRegistry.Register(enemy);
        }

        public void RemoveEnemy(ArmyData enemy)
        {
            ArmyRegistry.Unregister(enemy);
            _enemies.Remove(enemy);
            AiMapMemory.ForgetSightingsForTest(Me);
        }

        // ---- tasks --------------------------------------------------------------------------
        private MissionIntentState Intents => MissionIntentRegistry.GetOrCreate(Me);
        private int _targetSeed = 900;

        private MissionIntent Put(MissionKind kind, IMissionObjective objective, int? mover = null)
        {
            var intent = new MissionIntent { Kind = kind, Status = IntentStatus.Active, Objective = objective };
            if (mover.HasValue)
                intent.PreferredMoverArmyId = mover;
            intent.IntentKey = objective.IntentKey(mover);
            Intents.Put(intent);
            return intent;
        }

        public void EndTask(MissionIntent intent) => Intents.Remove(intent.IntentKey);

        // A return leg either passes through this hex towards another own base (`home` false:
        // the walk goes on) or has arrived here (`home` true: its destination is this hex).
        public static readonly HexCoord Elsewhere = new HexCoord(-6, 6);

        private static HexCoord Destination(bool home) => home ? Home : Elsewhere;

        public MissionIntent Raid(ArmyData primary, RaidMissionPhase phase, ArmyData support = null, bool home = false)
        {
            var raid = new RaidIntent
            {
                Target = RaidTargetRef.ForNeutralArmy(_targetSeed++), OperationStarted = true,
                Phase = phase, PrimaryArmyId = primary.Id, SupportArmyId = support?.Id,
                ReturnHex = Destination(home), RecoveryBaseHex = Destination(home),
                SupportReturnHex = Destination(home),
            };
            return Put(MissionKind.Raid, raid);
        }

        public MissionIntent Attack(ArmyData primary, AttackMissionPhase phase, bool assaultStarted = false,
            IEnumerable<ArmyData> gatherSupports = null, bool home = false)
        {
            var attack = new AttackIntent
            {
                Target = AttackTargetRef.For(new HexCoord(_targetSeed++, 5), Foe, AttackTargetKind.Base),
                Phase = phase, OperationStarted = true, AssaultStarted = assaultStarted,
                PrimaryArmyId = primary.Id, RecoveryBaseHex = Destination(home),
                SupportReturnHex = Destination(home),
            };
            if (gatherSupports != null)
                attack.GatherSupportArmyIds.AddRange(gatherSupports.Select(a => a.Id));
            return Put(MissionKind.Attack, attack);
        }

        // The Attack preparation host gathering on this hex: a Gather leg with a frozen target
        // roster (the cards' strike keys, Map-sourced).
        public MissionIntent Preparation(ArmyData host, params UnitData[] targetRoster)
        {
            var attack = new AttackIntent
            {
                Target = AttackTargetRef.For(new HexCoord(_targetSeed++, 5), Foe, AttackTargetKind.Base),
                Phase = AttackMissionPhase.Gather, Preparation = true, StagingHex = Home,
                PrimaryArmyId = host.Id,
                TargetRoster = targetRoster.Select(u => new StrikeRosterSlot(StrikeRoster.UnitKey(u), u.IsHero,
                    Power(u), ForceSource.Map)).ToList(),
            };
            return Put(MissionKind.Attack, attack);
        }

        public MissionIntent Defence(ArmyData primary, ActiveDefencePhase phase, ArmyData enemy = null,
            bool home = false)
        {
            var defence = new ActiveDefenceIntent
            {
                Phase = phase, EnemyArmyId = enemy?.Id ?? _targetSeed++, PrimaryArmyId = primary.Id,
                InterceptPurpose = ActiveDefenceInterceptPurpose.StrategicDefence,
                ReturnHex = phase == ActiveDefencePhase.Return ? Destination(home) : (HexCoord?)null,
                ProtectedAssetHex = Home,
            };
            return Put(MissionKind.ActiveDefence, defence);
        }

        // A garrison sortie under way (ActiveDefence with a sortie plan: fully protected).
        public MissionIntent Sortie(ArmyData detachment, ArmyData garrison, ArmyData enemy = null)
        {
            MissionIntent intent = Defence(detachment, ActiveDefencePhase.Intercept, enemy);
            intent.ActiveDefence.Sortie = new GarrisonSortiePlan(true, garrison.Id, 0,
                intent.ActiveDefence.EnemyArmyId, 1, null, Home, new HexCoord(2, 0),
                detachment.Members.Select(u => u.RuntimeId).ToList(), null, 0.8f, true);
            return intent;
        }

        public MissionIntent Economy(ArmyData actor, EconomyTaskKind kind, bool home = false)
        {
            bool collector = kind == EconomyTaskKind.MobileCollection || kind == EconomyTaskKind.ReturnCollector;
            var economy = new EconomyIntent
            {
                Kind = kind, TargetHex = home ? Home : new HexCoord(3, -3),
                BuilderArmyId = collector ? (int?)null : actor.Id,
                CollectorArmyId = collector ? actor.Id : (int?)null,
            };
            return Put(MissionKind.Economy, economy, actor.Id);
        }

        public MissionIntent Development(ArmyData actor, UnitData hero)
        {
            var development = new DevelopmentIntent
            {
                FacilityHex = new HexCoord(-3, 3), Mode = ResearchProductionMode.Research, Hero = hero,
            };
            return Put(MissionKind.Development, development, actor.Id);
        }

        // A dedicated scout with live recon work (ReconPatrolState: the SoloRole protection).
        public void ScoutWork(ArmyData scout) =>
            ReconPatrolStateRegistry.GetOrCreate(Me, scout.Id, Home, new HexCoord(6, -6), ReconMode.Explore, Turn);

        // A fresh materialized force leased to an operational capability this turn.
        public void Lease(ArmyData army) =>
            StrategicCapabilityLeaseRegistry.Mark(Me, Turn, DesireAxis.Aggression, CapabilityKind.FieldCombatPower,
                new[] { army.Id });

        // ---- the turn -----------------------------------------------------------------------
        public IEnumerable<ArmyData> OwnArmies =>
            ArmyRegistry.AllForOwner(Me).Where(a => a != null).OrderBy(a => a.Id);

        // The army `u` stood in by day (the last turn's day result), else the live one.
        public ArmyData ArmyOf(UnitData u)
        {
            StandArmy day = DayArmyOf(u);
            return day != null ? OwnArmies.FirstOrDefault(a => a.Id == day.Id)
                : OwnArmies.FirstOrDefault(a => a.Members.Contains(u));
        }

        private StandArmy DayArmyOf(UnitData u) =>
            Last?.After.Values.FirstOrDefault(a => a.Members.Contains(u));

        private IReadOnlyList<UnitData> DayMembers(ArmyData army) =>
            Last != null && Last.After.TryGetValue(army.Id, out StandArmy a) ? a.Members : army.Roster;

        private WorldSnapshot Snapshot()
        {
            IMissionIntentView intents = Intents;
            foreach (ArmyData enemy in _enemies)
                AiMapMemory.ObserveForTest(Me, enemy);
            return new WorldSnapshot
            {
                Observer = Me,
                TurnNumber = Turn,
                Intents = intents,
                KnowledgeVersion = AiMapMemory.KnowledgeVersionFor(Me),
                Self = new SelfSnapshot
                {
                    Citadel = AiTurnController.GarrisonHexFor(Me),
                    HoldsStartingCitadel = Site == StandSite.Citadel,
                    BaseHexes = new[] { Home },
                    Armies = OwnArmies.Select(a => WorldAnalysis.ToArmySnapshot(a, Me, true, 0, intents, Ctx)).ToList(),
                },
                TrueWorld = new TrueWorldSnapshot
                {
                    EnemyArmies = _enemies.Select(a => WorldAnalysis.ToArmySnapshot(a, Me, false, 0, null, Ctx)).ToList(),
                    NeutralArmies = Array.Empty<ArmySnapshot>(),
                    AllBuildings = Array.Empty<BuildingSnapshot>(),
                    Opponents = Array.Empty<OpponentSnapshot>(),
                },
            };
        }

        // Runs a paced AI routine to its end, nested routines included.
        internal static void Drain(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                if (top.Current is IEnumerator nested) stack.Push(nested);
            }
        }

        // The day plan / night plan of one hex, run to the end (tests of the pure planners).
        internal static ReorganizationPlan Plan(LocalForceGroup group)
        {
            var plan = new ReorganizationPlan();
            Drain(ArmyReorganizationPlanner.PlanRoutine(group, null, plan));
            return plan;
        }

        internal static NightFoldPlan PlanNight(LocalForceGroup group)
        {
            var plan = new NightFoldPlan();
            Drain(HousekeepingNightFold.PlanRoutine(group, null, plan));
            return plan;
        }

        // One owner's end-of-turn Housekeeping on the live world, with the standing checks.
        public StandTurn EndTurn(string note = null)
        {
            var turn = new StandTurn { Number = Turns.Count + 1, Note = note };
            WorldSnapshot snapshot = Snapshot();
            ActorCommitments commitments = ActorCommitments.FromIntents(snapshot.AllIntents, snapshot, null);
            turn.Before = Capture(commitments);
            turn.HoldBefore = Holds();
            bool isCitadel = Site == StandSite.Citadel;
            turn.GarrisonFloor = AiArmyRoles.GarrisonDefenceFloor(Me, Home, isCitadel);

            ArmyReorgAnalysis preview = ArmyReorgAnalyzer.Analyze(Me, commitments, snapshot, Ctx);
            turn.Plans = preview.Groups.Select(Plan).ToList();
            turn.Inputs = preview.Groups.SelectMany(g => g.Containers).Select(c =>
                $"#{c.ArmyId} {c.Role}{(c.MissionLabel != null ? "/" + c.MissionLabel : "")} recv={(c.CanReceive ? 1 : 0)} "
                + $"give={(c.CanDonate ? 1 : 0)} mp>={c.MovementFloor}"
                + (c.IsGarrison ? $" floor={c.GarrisonPowerFloor:0.#}" : "")
                + (c.PreparationTargetKeys != null ? $" roster=[{string.Join(",", c.PreparationTargetKeys)}]" : "")
                + $" units=[{string.Join(",", c.Units.Select(u => $"u{u.Key}:{u.StrikeKey}{(u.IsHero ? ":" + u.HeroRole : "")}"))}]")
                .ToList();
            turn.Threats = preview.Groups.SelectMany(g => g.ThreatBenchmarks).Select(t =>
                $"enemy#{t.ArmyId} eta {t.EtaToGroup}{(t.HiddenFromUs ? " hidden" : "")}").ToList();

            int apBefore = _root.ActionPoints;
            var result = new HousekeepingResult();
            HousekeepingTime.Start();
            Drain(HousekeepingManager.Run(snapshot, Me, _root, Ctx, commitments, result));
            HousekeepingTime.Stop();
            turn.Result = result;
            turn.ApSpent = apBefore - _root.ActionPoints;
            turn.After = Capture(commitments);
            turn.HoldAfter = Holds();

            CheckStandingRules(turn, preview, commitments);

            // The next pass on the result: it must never send a unit moved this turn back where it
            // came from (ping-pong). Any other further step is the layout converging over turns
            // (a unit moves once per plan) and is noted, not failed.
            WorldSnapshot again = Snapshot();
            ActorCommitments againCommitments = ActorCommitments.FromIntents(again.AllIntents, again, null);
            ArmyReorgAnalysis nextAnalysis = ArmyReorgAnalyzer.Analyze(Me, againCommitments, again, Ctx);
            foreach (ReorganizationPlan p in nextAnalysis.Groups.Select(Plan)
                         .Where(p => !p.IsEmpty))
            {
                var back = new List<string>();
                foreach (KeyValuePair<int, List<int>> kv in p.ExpectedMembership)
                    foreach (UnitData u in kv.Value.Select(k => nextAnalysis.UnitByKey[k]))
                    {
                        int from = turn.Before.Values.FirstOrDefault(a => a.Members.Contains(u))?.Id ?? -1;
                        int to = turn.After.Values.FirstOrDefault(a => a.Members.Contains(u))?.Id ?? -1;
                        if (from != to && kv.Key == from)
                            back.Add($"{Label(u)} #{to}->#{from}");
                    }
                if (back.Count > 0)
                    Fail(turn, $"ping-pong: the next pass moves back {string.Join(", ", back)} - {p.DebugSummary()}");
                else
                    Notes.Add($"T{turn.Number}: converges over turns, the next pass plans {p.DebugSummary()}");
            }

            // The night: the turn's last act joins the hex for whoever attacks before the owner's
            // next turn.
            WorldSnapshot night = Snapshot();
            ActorCommitments nightCommitments = ActorCommitments.FromIntents(night.AllIntents, night, null);
            int apNight = _root.ActionPoints;
            HousekeepingTime.Start();
            turn.NightFist = new HashSet<int>(ArmyReorgAnalyzer.Analyze(Me, nightCommitments, night, Ctx)
                    .Groups.SelectMany(g => g.Containers).Where(c => c.IsFist)
                .Select(c => c.ArmyId));
            var nightMoved = new int[1];
            Drain(HousekeepingNightFold.FoldRoutine(Me, Ctx, nightCommitments, night, null, nightMoved));
            turn.NightMoved = nightMoved[0];
            HousekeepingTime.Stop();
            turn.NightApSpent = apNight - _root.ActionPoints;
            turn.Night = Capture(nightCommitments);
            turn.HoldNight = Holds();
            CheckNight(turn);

            // The owner's next turn: BeginPlayer (movement, activation), then the restore.
            Ctx.TurnNumber++;
            foreach (ArmyData a in OwnArmies)
            {
                foreach (UnitData u in a.Members)
                    u.ReplenishMoveForNewTurn();
                a.ResetActivationForNewTurn();
            }
            turn.MorningMoved = HousekeepingNightFold.Restore(Me, Ctx);
            turn.Morning = Capture(commitments);
            CheckMorning(turn);

            Turns.Add(turn);
            return turn;
        }

        private void CheckNight(StandTurn t)
        {
            if (t.NightApSpent != 0)
                Fail(t, $"night: AP spent {t.NightApSpent}");
            List<UnitData> day = t.After.Values.SelectMany(a => a.Members).ToList();
            List<UnitData> night = t.Night.Values.SelectMany(a => a.Members).ToList();
            foreach (UnitData u in day.Where(u => !night.Contains(u)))
                Fail(t, $"night: {Label(u)} lost");
            foreach (UnitData u in night.GroupBy(u => u).Where(g => g.Count() > 1).Select(g => g.Key))
                Fail(t, $"night: {Label(u)} is in two armies");
            foreach (StandArmy a in t.Night.Values.Where(a => a.Members.Count > 0 && !a.Hex.Equals(Home)))
                Fail(t, $"night: army #{a.Id} left the hex");
            foreach (StandArmy a in t.Night.Values.Where(a => a.Members.Count > a.Capacity))
                Fail(t, $"night: army #{a.Id} over capacity {a.Members.Count}/{a.Capacity}");
            foreach (StandArmy d in t.After.Values)
            {
                StandArmy n = t.Night.TryGetValue(d.Id, out StandArmy x) ? x : null;
                List<UnitData> now = n?.Members ?? new List<UnitData>();
                if ((d.IsAir || d.IsPrison) && !new HashSet<UnitData>(d.Members).SetEquals(now))
                    Fail(t, $"night: #{d.Id} (aviation/prison) changed");
                // A Support hero leaves only for a hero-only bench on the hex (owner 2026-11-18).
                foreach (UnitData u in d.Members.Where(u => AiArmyRoles.IsGarrisonHero(u) && !now.Contains(u)))
                {
                    StandArmy at = t.Night.Values.FirstOrDefault(a => a.Members.Contains(u));
                    if (at == null || at.Members.Any(m => m.IsGroundCombatant))
                        Fail(t, $"night: Support hero {Label(u)} left #{d.Id} for a fighting army");
                }
            }
            // The garrison keeps a body at night when it had one by day (owner 2026-11-26).
            StandArmy dayGarrison = t.After.Values.FirstOrDefault(a => a.IsGarrison);
            if (dayGarrison != null && dayGarrison.Members.Any(u => u.IsGroundCombatant)
                && !(t.Night.TryGetValue(dayGarrison.Id, out StandArmy ng) && ng.Members.Any(u => u.IsGroundCombatant)))
                Fail(t, "night: the garrison was left without a body");
            int dayFormations = Formations(t.After), nightFormations = Formations(t.Night);
            if (nightFormations > dayFormations)
                Fail(t, $"night: {nightFormations} fighting formations, {dayFormations} by day");
            else if (nightFormations > 1)
                Notes.Add($"T{t.Number}: night keeps {nightFormations} fighting formations (commands full)");
            foreach (KeyValuePair<int, float> h in t.HoldAfter)
                if (t.HoldNight.TryGetValue(h.Key, out float now) && now < h.Value - HoldTolerance)
                    Fail(t, $"night: hex hold vs enemy#{h.Key} dropped {h.Value:0.00} -> {now:0.00}");
        }

        private void CheckMorning(StandTurn t)
        {
            foreach (StandArmy d in t.After.Values.Where(a => a.Contract != null || t.NightFist.Contains(a.Id)))
            {
                StandArmy m = t.Morning.TryGetValue(d.Id, out StandArmy x) ? x : null;
                List<UnitData> now = m?.Members ?? new List<UnitData>();
                if (!new HashSet<UnitData>(d.Members).SetEquals(now))
                    Fail(t, $"morning: #{d.Id} is not its day roster: day [{string.Join(", ", d.Members.Select(Label))}] "
                        + $"morning [{string.Join(", ", now.Select(Label))}]");
                else if (m != null && d.Commander != m.Commander)
                    Fail(t, $"morning: #{d.Id} commander {Label(d.Commander)} by day, {Label(m.Commander)} in the morning");
            }
            if (HousekeepingNightFold.HasRecord(Me))
                Fail(t, "morning: the night record is still open");
        }

        private static int Formations(Dictionary<int, StandArmy> armies) =>
            armies.Values.Count(a => !a.IsAir && !a.IsPrison && a.Hex.Equals(Home)
                && a.Members.Any(u => u.IsGroundCombatant));

        // ---- standing checks ----------------------------------------------------------------
        private const float HoldTolerance = 0.12f;
        private const int OracleTrials = 400;

        private Dictionary<int, StandArmy> Capture(ActorCommitments commitments)
        {
            var map = new Dictionary<int, StandArmy>();
            foreach (ArmyData a in OwnArmies)
                map[a.Id] = new StandArmy
                {
                    Id = a.Id, IsGarrison = a.IsGarrison, IsAir = AviationRules.IsAirArmy(a) || a.IsAirfield,
                    IsPrison = a.IsPrison, Hex = a.Hex,
                    Members = a.Members.ToList(), Commander = a.Commander,
                    Movement = a.Members.Count > 0 ? a.CurrentMovement : -1,
                    Capacity = a.Capacity,
                    Contract = ArmyReorgAnalyzer.MutationContractFor(Me, Turn, a, commitments)
                        ?? (a.IsGarrison ? null : MissionActorPolicy.PhysicalMutationContract(Me, Turn, a, commitments)),
                    Power = AiPower.EffectiveArmyPower(a.Members.Where(AiArmyRoles.IsGroundBattleBody).ToList()),
                };
            return map;
        }

        // The hex hold against every enemy in sight: the enemy fights every own formation on
        // the hex in turn (WorthIt.EstimateHexControl, the model ActiveDefence reads too).
        private Dictionary<int, float> Holds()
        {
            var holds = new Dictionary<int, float>();
            float hexBonus = WorthIt.HexDefenseBonus(Home, Ctx.Map, Me);
            float buildingBonus = DisruptionRules.KnownBuildingDefenseFor(Home, Me, null);
            var defenders = new List<WorthIt.DefendingArmy>();
            foreach (ArmyData a in OwnArmies)
            {
                if (a.IsPrison || a.IsAirfield || AviationRules.IsAirArmy(a) || !a.Hex.Equals(Home))
                    continue;
                List<WorthIt.DefenderProfile> bodies = a.Members.Where(u => u.IsGroundCombatant && !u.IsHidden)
                    .Select(WorthIt.FromLiveUnit).ToList();
                if (bodies.Count == 0)
                    continue;
                WorthIt.SideCommander commander = a.Commander != null ? WorthIt.SideCommander.Of(a.Commander) : default;
                defenders.Add(new WorthIt.DefendingArmy(bodies, commander, armyId: a.Id,
                    buildingDefenseBonus: buildingBonus));
            }
            foreach (ArmyData enemy in _enemies)
            {
                if (defenders.Count == 0) { holds[enemy.Id] = 0f; continue; }
                List<WorthIt.DefenderProfile> attackers = enemy.Members.Select(WorthIt.FromLiveUnit).ToList();
                WorthIt.SideCommander commander = enemy.Commander != null
                    ? WorthIt.SideCommander.Of(enemy.Commander) : default;
                float win = WorthIt.EstimateHexControl(attackers, commander, defenders, hexBonus,
                    environment: BattleEnvironment.At(Home, Ctx.Map), monteCarloTrials: OracleTrials).WinChance;
                holds[enemy.Id] = 1f - win;
            }
            return holds;
        }

        private void CheckStandingRules(StandTurn t, ArmyReorgAnalysis preview, ActorCommitments commitments)
        {
            // Every operation succeeded, no AP spent.
            if (t.Result.TransfersFailed > 0)
                Fail(t, $"{t.Result.TransfersFailed} operation(s) failed");
            if (t.Result.ApInvariantViolated || t.ApSpent != 0)
                Fail(t, $"AP spent by Housekeeping: {t.ApSpent}");

            // No unit lost or duplicated, none left the hex.
            List<UnitData> before = t.Before.Values.SelectMany(a => a.Members).ToList();
            List<UnitData> after = t.After.Values.SelectMany(a => a.Members).ToList();
            foreach (UnitData u in before.Where(u => !after.Contains(u)))
                Fail(t, $"{Label(u)} lost");
            foreach (UnitData u in after.GroupBy(u => u).Where(g => g.Count() > 1).Select(g => g.Key))
                Fail(t, $"{Label(u)} is in two armies");
            foreach (StandArmy a in t.After.Values.Where(a => a.Members.Count > 0 && !a.Hex.Equals(Home)))
                Fail(t, $"army #{a.Id} left the hex");

            // Capacity.
            foreach (StandArmy a in t.After.Values.Where(a => a.Members.Count > a.Capacity))
                Fail(t, $"army #{a.Id} over capacity {a.Members.Count}/{a.Capacity}");

            // The live result is the plan.
            foreach (ReorganizationPlan plan in t.Plans)
                foreach (KeyValuePair<int, List<int>> kv in plan.ExpectedMembership)
                {
                    var expected = new HashSet<UnitData>(kv.Value.Select(k => preview.UnitByKey[k]));
                    var live = new HashSet<UnitData>(t.After.TryGetValue(kv.Key, out StandArmy a)
                        ? a.Members : new List<UnitData>());
                    if (!expected.SetEquals(live))
                        Fail(t, $"army #{kv.Key} is not what the plan built: planned "
                            + $"[{string.Join(", ", expected.Select(Label))}] live [{string.Join(", ", live.Select(Label))}]");
                }

            // Contracts of the armies with a task or a protected role.
            foreach (StandArmy b in t.Before.Values)
            {
                StandArmy a = t.After.TryGetValue(b.Id, out StandArmy x) ? x : null;
                List<UnitData> now = a?.Members ?? new List<UnitData>();
                List<UnitData> left = b.Members.Where(u => !now.Contains(u)).ToList();
                List<UnitData> came = now.Where(u => !b.Members.Contains(u)).ToList();
                if (b.IsAir || b.IsPrison)
                {
                    if (left.Count + came.Count > 0)
                        Fail(t, $"#{b.Id} (aviation/prison) changed");
                    continue;
                }
                ArmyMutationContract c = b.Contract;
                if (c == null)
                    continue;
                string tag = $"#{b.Id} [{c.Label}]";
                if (!c.MayReceive && came.Count > 0)
                    Fail(t, $"{tag} received {string.Join(", ", came.Select(Label))}");
                if (c.MayReleaseExcessHeroes)
                {
                    // A preparation host only sheds heroes or exchanges a body 1-for-1: never weaker.
                    if (a == null || a.Power + 0.01f < b.Power)
                        Fail(t, $"{tag} weakened {b.Power:0.#} -> {a?.Power ?? 0f:0.#}");
                }
                else if (left.Count > 0)
                    Fail(t, $"{tag} gave away {string.Join(", ", left.Select(Label))}");
                if (!c.MayReorderCommander && a != null && b.Commander != a.Commander && left.Count == 0)
                    Fail(t, $"{tag} commander changed {Label(b.Commander)} -> {Label(a.Commander)}");
                if (c.KeepsMovement && b.Movement >= 0 && a != null && a.Members.Count > 0 && a.Movement < b.Movement)
                    Fail(t, $"{tag} slowed {b.Movement} -> {a.Movement}");
            }

            // Garrison: one body stays; without a known enemy it keeps its floor.
            StandArmy gb = t.Before.Values.FirstOrDefault(a => a.IsGarrison);
            StandArmy ga = gb != null && t.After.TryGetValue(gb.Id, out StandArmy g) ? g : null;
            if (gb != null && ga != null)
            {
                if (gb.Members.Any(AiArmyRoles.IsGroundBattleBody) && !ga.Members.Any(AiArmyRoles.IsGroundBattleBody))
                    Fail(t, "the garrison lost its last body");
                if (t.HoldBefore.Count == 0 && ga.Power + 0.01f < Math.Min(gb.Power, t.GarrisonFloor))
                    Fail(t, $"the garrison fell below its floor {t.GarrisonFloor:0.#}: {gb.Power:0.#} -> {ga.Power:0.#}");
            }

            // What Housekeeping is for: the hex hold never drops.
            foreach (KeyValuePair<int, float> h in t.HoldBefore)
                if (t.HoldAfter.TryGetValue(h.Key, out float now) && now < h.Value - HoldTolerance)
                    Fail(t, $"hex hold vs enemy#{h.Key} dropped {h.Value:0.00} -> {now:0.00}");
        }

        private void Fail(StandTurn t, string message) => Failures.Add($"T{t.Number}: {message}");

        // ---- case expectations (on the last turn) -----------------------------------------
        private StandTurn RequireTurn()
        {
            if (Last == null)
                throw new InvalidOperationException("EndTurn() before expectations");
            return Last;
        }

        private void Expect(bool ok, string message)
        {
            if (!ok)
                Failures.Add($"T{RequireTurn().Number}: expected {message}");
        }

        public void ExpectIn(UnitData u, ArmyData army, string why = null) =>
            Expect(DayMembers(army).Contains(u), $"{Label(u)} in #{army.Id}{Why(why)}");

        public void ExpectNotIn(UnitData u, ArmyData army, string why = null) =>
            Expect(!DayMembers(army).Contains(u), $"{Label(u)} not in #{army.Id}{Why(why)}");

        public void ExpectInGarrison(UnitData u, string why = null) =>
            Expect(DayArmyOf(u)?.IsGarrison == true, $"{Label(u)} in the garrison{Why(why)}");

        public void ExpectNotInGarrison(UnitData u, string why = null) =>
            Expect(DayArmyOf(u)?.IsGarrison != true, $"{Label(u)} outside the garrison{Why(why)}");

        public void ExpectTogether(UnitData a, UnitData b, string why = null) =>
            Expect(DayArmyOf(a) != null && DayArmyOf(a) == DayArmyOf(b), $"{Label(a)} with {Label(b)}{Why(why)}");

        public void ExpectApart(UnitData a, UnitData b, string why = null) =>
            Expect(DayArmyOf(a) != DayArmyOf(b), $"{Label(a)} apart from {Label(b)}{Why(why)}");

        // `hero` commands the army `body` stands in.
        public void ExpectCommands(UnitData hero, UnitData body, string why = null) =>
            Expect(DayArmyOf(body)?.Commander == hero, $"{Label(hero)} commands {Label(body)}{Why(why)}");

        public void ExpectUnchanged(ArmyData army, string why = null)
        {
            StandArmy before = RequireTurn().Before.TryGetValue(army.Id, out StandArmy b) ? b : null;
            Expect(before != null && new HashSet<UnitData>(before.Members).SetEquals(DayMembers(army)),
                $"#{army.Id} unchanged{Why(why)}");
        }

        public void ExpectEmpty(ArmyData army, string why = null) =>
            Expect(DayMembers(army).Count == 0, $"#{army.Id} empty{Why(why)}");

        public void ExpectNoOperations(string why = null) =>
            Expect(RequireTurn().Result.TransfersApplied == 0, $"no operation{Why(why)}");

        public void ExpectSomeOperation(string why = null) =>
            Expect(RequireTurn().Result.TransfersApplied > 0, $"an operation{Why(why)}");

        public void ExpectNotAlone(UnitData u, string why = null) =>
            Expect(DayArmyOf(u) != null && (DayArmyOf(u).IsGarrison || DayArmyOf(u).Members.Count > 1),
                $"{Label(u)} not left alone{Why(why)}");

        public void ExpectAlone(UnitData u, string why = null) =>
            Expect(DayArmyOf(u) != null && !DayArmyOf(u).IsGarrison && DayArmyOf(u).Members.Count == 1,
                $"{Label(u)} alone{Why(why)}");

        // `army` stands stronger after the day than before it.
        public void ExpectStrengthened(ArmyData army, string why = null)
        {
            StandTurn t = RequireTurn();
            float before = t.Before.TryGetValue(army.Id, out StandArmy b) ? b.Power : 0f;
            float after = t.After.TryGetValue(army.Id, out StandArmy a) ? a.Power : 0f;
            Expect(after > before + 0.01f, $"#{army.Id} strengthened ({before:0.#} -> {after:0.#}){Why(why)}");
        }

        public void ExpectHeroes(ArmyData army, int count, string why = null) =>
            Expect(DayMembers(army).Count(u => u.IsHero) == count, $"#{army.Id} with {count} hero(es){Why(why)}");

        // No field army was taken into use: the armies holding units are the ones that held them.
        public void ExpectNoArmyTakenIntoUse(string why = null)
        {
            StandTurn t = RequireTurn();
            List<int> fresh = t.After.Values.Where(a => a.Members.Count > 0
                && (!t.Before.TryGetValue(a.Id, out StandArmy b) || b.Members.Count == 0)).Select(a => a.Id).ToList();
            Expect(fresh.Count == 0, $"no empty army taken into use{Why(why)} (filled: {string.Join(",", fresh)})");
        }

        // The own formations on the hex that hold ground battle bodies (garrison included).
        public void ExpectFightingFormations(int count, string why = null)
        {
            int n = RequireTurn().After.Values.Count(a => !a.IsPrison && !a.IsAir
                && a.Members.Any(AiArmyRoles.IsGroundBattleBody));
            Expect(n == count, $"{count} fighting formation(s) on the hex, got {n}{Why(why)}");
        }

        // A Support hero never rides in a field army that fights (it serves from the garrison
        // or a hero-only bench on the hex).
        public void ExpectNoSupportHeroInFightingField(string why = null)
        {
            foreach (StandArmy a in RequireTurn().After.Values.Where(a => !a.IsGarrison))
                foreach (UnitData u in a.Members.Where(AiArmyRoles.IsGarrisonHero))
                    Expect(!a.Members.Any(AiArmyRoles.IsGroundBattleBody),
                        $"{Label(u)} not in the fighting field army #{a.Id}{Why(why)}");
        }

        // ---- the night (the fold of the last turn) ------------------------------------------
        private StandArmy NightArmyOf(UnitData u) =>
            RequireTurn().Night.Values.FirstOrDefault(a => a.Members.Contains(u));

        public void ExpectNightTogether(UnitData a, UnitData b, string why = null) =>
            Expect(NightArmyOf(a) != null && NightArmyOf(a) == NightArmyOf(b),
                $"at night {Label(a)} with {Label(b)}{Why(why)}");

        public void ExpectNightFormations(int count, string why = null)
        {
            int n = Formations(RequireTurn().Night);
            Expect(n == count, $"{count} fighting formation(s) at night, got {n}{Why(why)}");
        }

        public void ExpectNightCommands(UnitData hero, UnitData body, string why = null) =>
            Expect(NightArmyOf(body)?.Commander == hero, $"at night {Label(hero)} commands {Label(body)}{Why(why)}");

        private static string Why(string why) => string.IsNullOrEmpty(why) ? "" : " - " + why;

        // ---- report -------------------------------------------------------------------------
        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {CaseName}: {OwnDeck.Name} vs {EnemyDeck.Name} ({Site}) ===");
            if (!string.IsNullOrEmpty(CaseText))
                sb.AppendLine(CaseText);
            foreach (StandTurn t in Turns)
            {
                sb.AppendLine($"-- T{t.Number}{(t.Note != null ? " " + t.Note : "")}: threats "
                    + $"[{string.Join("; ", t.Threats)}] floor {t.GarrisonFloor.ToString("0.#", CultureInfo.InvariantCulture)} "
                    + $"applied {t.Result?.TransfersApplied} failed {t.Result?.TransfersFailed}");
                foreach (StandArmy a in t.Before.Values)
                    sb.AppendLine($"   before {Format(a)}");
                foreach (string i in t.Inputs)
                    sb.AppendLine($"   input  {i}");
                foreach (ReorganizationPlan p in t.Plans)
                    sb.AppendLine($"   plan   {p.DebugSummary()}");
                foreach (StandArmy a in t.After.Values)
                    sb.AppendLine($"   after  {Format(a)}");
                foreach (KeyValuePair<int, float> h in t.HoldBefore)
                    sb.AppendLine($"   hold vs enemy#{h.Key}: {h.Value:0.00} -> "
                        + $"{(t.HoldAfter.TryGetValue(h.Key, out float v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "?")}"
                        + $" (night {(t.HoldNight != null && t.HoldNight.TryGetValue(h.Key, out float nv) ? nv.ToString("0.00", CultureInfo.InvariantCulture) : "?")})");
                if (t.Night != null)
                {
                    sb.AppendLine($"   night  moved {t.NightMoved}");
                    foreach (StandArmy a in t.Night.Values.Where(a => a.Members.Count > 0))
                        sb.AppendLine($"   night  {Format(a)}");
                    sb.AppendLine($"   morning moved back {t.MorningMoved}");
                }
            }
            foreach (string n in Notes)
                sb.AppendLine("NOTE " + n);
            foreach (string f in Failures)
                sb.AppendLine("FAIL " + f);
            return sb.ToString();
        }

        private string Format(StandArmy a) =>
            $"#{a.Id}{(a.IsGarrison ? "/garrison" : a.IsAir ? "/air" : "")}"
            + (a.Contract != null ? $"[{a.Contract.Label}]" : "")
            + $" {a.Members.Count}/{a.Capacity} P{a.Power:0.#} [{string.Join(", ", a.Members.Select(Label))}]";
    }

    internal sealed class StandArmy
    {
        public int Id;
        public bool IsGarrison, IsAir, IsPrison;
        public HexCoord Hex;
        public List<UnitData> Members;
        public UnitData Commander;
        public int Movement;
        public int Capacity;
        public float Power;
        public ArmyMutationContract Contract;
    }

    internal sealed class StandTurn
    {
        public int Number;
        public string Note;
        public Dictionary<int, StandArmy> Before, After;
        public Dictionary<int, float> HoldBefore, HoldAfter;
        public float GarrisonFloor;
        public List<ReorganizationPlan> Plans;
        public List<string> Inputs = new List<string>();
        public List<string> Threats;
        public HousekeepingResult Result;
        public int ApSpent;
        public Dictionary<int, StandArmy> Night, Morning;
        public Dictionary<int, float> HoldNight;
        public int NightMoved, MorningMoved, NightApSpent;
        public HashSet<int> NightFist = new HashSet<int>();
    }
}
#endif
