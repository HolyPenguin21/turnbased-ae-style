#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  TURN STAND FAMILIES: one family per Provisioning lane, four variants each, played over
    //  several turns in execution mode (TurnStandPlayTests). The variants of a family differ in one
    //  condition, so the digests show how the lane's behaviour changes with it, and each variant
    //  states what its turns must show. A deck that cannot stage a variant (no aircraft, no
    //  extraction card, no Research/Production facility) skips it.
    // ===========================================================================================
    internal enum TurnFamily
    {
        ScoutGround,    // ground Recon binding: field scout, AP, garrison Recce (shell / creation)
        ScoutAir,       // air Recon binding: stored aircraft, Energy, formed wing, enemy in sight
        Raid,           // Raid assault binding: weak / assembled / strong / far neutral target
        ActiveDefence,  // intercept, garrison sortie, safe withdrawal, garrison relief
        Attack,         // assault on an enemy Base: ready, under preparation, too strong, Citadel
        Economy,        // builder binding: field hero, garrison hero, founding a Base, no AP on turn 1
        Development,    // operator binding: garrison hero, field hero, no resources, remote facility
    }

    internal sealed class TurnFamilyCase
    {
        internal readonly TurnFamily Kind;
        internal readonly int Variant;
        internal readonly string Label;
        internal readonly int Turns;

        internal TurnFamilyCase(TurnFamily kind, int variant, string label, int turns = 3)
        {
            Kind = kind; Variant = variant; Label = label; Turns = turns;
        }

        internal string Name => $"{Kind}{Variant}_{Label}";
        internal int Seed => ((int)Kind + 1) * 104729 + Variant * 7919;
    }

    // The cards of the own deck a family stages with.
    internal sealed class FamilyKit
    {
        internal readonly StartingDeckCatalog Catalog;
        internal readonly List<CardDefinition> Bodies, FoeBodies;
        internal readonly CardDefinition Hero, Recce, Aircraft, DevelopmentFacility, DevelopmentHero, BaseCard;

        internal FamilyKit(StartingDeckCatalog catalog, List<CardDefinition> ownCards, List<CardDefinition> bodies,
            List<CardDefinition> foeBodies, CardDefinition hero, CardDefinition recce, PlayerSetupData owner)
        {
            Catalog = catalog; Bodies = bodies; FoeBodies = foeBodies; Hero = hero; Recce = recce;
            // Air Recon flies Recce aircraft.
            List<CardDefinition> aircraft = ownCards.Where(c => c.cardType == CardType.Unit && c.isAviation
                && c.airfieldCapacity <= 0).ToList();
            Aircraft = aircraft.FirstOrDefault(c => AbilityParams.UnitHasAnyRecce(UnitData.FromDefinition(c, owner)));
            DevelopmentFacility = ownCards.FirstOrDefault(ResearchProductionSystem.IsResearchProductionFacility);
            if (DevelopmentFacility != null)
            {
                ResearchProductionMode mode = DevelopmentFacility.grantedAbilities.Contains(UnitAbilities.Research)
                    ? ResearchProductionMode.Research : ResearchProductionMode.Production;
                DevelopmentHero = ownCards.FirstOrDefault(c => c.cardType == CardType.Hero
                    && UnitData.FromDefinition(c, owner).HasAbility(ResearchProductionSystem.RoleAbility(mode)));
            }
            BaseCard = catalog.GetCatalog(owner.Faction)?.ForType(CardType.Base).FirstOrDefault();
        }

        internal CardDefinition Weak => Bodies[0];
        internal CardDefinition Mid => Bodies[Bodies.Count / 2];
        internal CardDefinition Strong => Bodies[Bodies.Count - 1];
        internal CardDefinition FoeWeak => FoeBodies[0];
        internal CardDefinition FoeStrong => FoeBodies[FoeBodies.Count - 1];
    }

    internal sealed partial class TurnStandCase
    {
        private static readonly TurnFamilyCase[] FamilyCases =
        {
            new TurnFamilyCase(TurnFamily.ScoutGround, 1, "FieldScout"),
            new TurnFamilyCase(TurnFamily.ScoutGround, 2, "NoApTurn1"),
            new TurnFamilyCase(TurnFamily.ScoutGround, 3, "GarrisonRecceShell"),
            new TurnFamilyCase(TurnFamily.ScoutGround, 4, "GarrisonRecceCreate"),
            new TurnFamilyCase(TurnFamily.ScoutAir, 1, "StoredAircraft"),
            new TurnFamilyCase(TurnFamily.ScoutAir, 2, "StoredNoEnergy"),
            new TurnFamilyCase(TurnFamily.ScoutAir, 3, "FormedWing"),
            new TurnFamilyCase(TurnFamily.ScoutAir, 4, "FormedWingEnemySeen"),
            new TurnFamilyCase(TurnFamily.Raid, 1, "WeakNeutral"),
            new TurnFamilyCase(TurnFamily.Raid, 2, "WeakNeutralDonor"),
            new TurnFamilyCase(TurnFamily.Raid, 3, "StrongNeutral"),
            new TurnFamilyCase(TurnFamily.Raid, 4, "FarNeutral"),
            new TurnFamilyCase(TurnFamily.ActiveDefence, 1, "StrongNextToCitadel"),
            new TurnFamilyCase(TurnFamily.ActiveDefence, 2, "GarrisonSortie"),
            new TurnFamilyCase(TurnFamily.ActiveDefence, 3, "SafeWithdrawal"),
            new TurnFamilyCase(TurnFamily.ActiveDefence, 4, "GarrisonRelief"),
            new TurnFamilyCase(TurnFamily.Attack, 1, "WeakBaseNear"),
            new TurnFamilyCase(TurnFamily.Attack, 2, "WeakBasePreparation"),
            new TurnFamilyCase(TurnFamily.Attack, 3, "StrongBase"),
            new TurnFamilyCase(TurnFamily.Attack, 4, "WeakCitadel"),
            new TurnFamilyCase(TurnFamily.Economy, 1, "FieldBuilder"),
            new TurnFamilyCase(TurnFamily.Economy, 2, "GarrisonBuilder"),
            new TurnFamilyCase(TurnFamily.Economy, 3, "FoundBase"),
            new TurnFamilyCase(TurnFamily.Economy, 4, "NoApTurn1"),
            new TurnFamilyCase(TurnFamily.Development, 1, "GarrisonOperator"),
            new TurnFamilyCase(TurnFamily.Development, 2, "FieldOperator"),
            new TurnFamilyCase(TurnFamily.Development, 3, "NoResources"),
            new TurnFamilyCase(TurnFamily.Development, 4, "RemoteFacility"),
        };

        internal static IEnumerable<(string Own, string Enemy, TurnFamilyCase Family)> Families()
        {
            foreach (TurnFamilyCase family in FamilyCases)
                foreach ((string own, string enemy) in HousekeepingStand.DeckPairs)
                    yield return (own, enemy, family);
        }

        // ---- steps --------------------------------------------------------------------------------
        // Every executed mission step of the case, by turn: "<Kind>(<target>)" from the loop's
        // "[AI][V2][Loop] step=N task=<key> ..." line.
        internal readonly List<(int Turn, string Task)> Steps = new List<(int, string)>();

        private void CaptureStep(string message)
        {
            const string marker = "[AI][V2][Loop] step=";
            int at = message.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return;
            int task = message.IndexOf(" task=", at, StringComparison.Ordinal);
            if (task < 0) return;
            int end = message.IndexOf(" progress=", task, StringComparison.Ordinal);
            Steps.Add((Turn, end < 0 ? message.Substring(task + 6) : message.Substring(task + 6, end - task - 6)));
        }

        private bool StepOf(string kind, int byTurn, string contains = null) =>
            Steps.Any(s => s.Turn <= byTurn && s.Task.StartsWith(kind + "(", StringComparison.Ordinal)
                && (contains == null || s.Task.Contains(contains)));

        private bool StepOnTurn(string kind, int turn) =>
            Steps.Any(s => s.Turn == turn && s.Task.StartsWith(kind + "(", StringComparison.Ordinal));

        // ---- staging ------------------------------------------------------------------------------
        private PlayerSetupData _neutral;
        private CardDefinition _developmentHero;

        private PlayerSetupData Neutral()
        {
            if (_neutral != null) return _neutral;
            _neutral = new PlayerSetupData { PlayerId = "neutral", Nickname = "Neutral",
                Faction = Faction.Neutral, IsHuman = false, IsNeutral = true };
            NewRoot(_neutral);
            return _neutral;
        }

        private void Rich()
        {
            foreach (ResourceType type in ResourceBundle.All)
                Root.AddResource(type, 34);
        }

        private void Seen(ArmyData enemy) => AiMapMemory.ObserveForTest(Me, enemy);

        private ArmyData Airfield(HexCoord hex) =>
            ArmyRegistry.AllForOwner(Me).First(a => a != null && a.IsAirfield && a.Hex.Equals(hex));

        // `card` repeated until the army is at least `factor` times as strong as `than` (at most 8).
        private CardDefinition[] Outnumber(CardDefinition card, PlayerSetupData owner, float factor,
            params CardDefinition[] than)
        {
            float own = AiPower.EffectiveArmyPower(than.Select(c => UnitData.FromDefinition(c, Me)).ToList());
            var list = new List<CardDefinition> { card };
            while (list.Count < 8 && AiPower.EffectiveArmyPower(list.Select(c => UnitData.FromDefinition(c, owner)).ToList())
                   < factor * own)
                list.Add(card);
            return list.ToArray();
        }

        private bool Skip(string why)
        {
            Ignored = $"{OwnDeck}: {why}";
            return false;
        }

        private bool StageFamily(FamilyKit k)
        {
            var citadel = new HexCoord(0, 0);
            int v = Family.Variant;
            switch (Family.Kind)
            {
                case TurnFamily.ScoutGround:
                    if (k.Recce == null) return Skip("no ground Recce");
                    if (v <= 2)
                    {
                        Army(Me, citadel, true, k.Weak);
                        Army(Me, new HexCoord(1, 0), false, k.Hero, k.Mid);
                        Army(Me, new HexCoord(0, 1), false, k.Recce);
                        if (v == 2) Root.ActionPoints = 0;
                    }
                    else
                    {
                        Army(Me, citadel, true, k.Weak, k.Recce);
                        Army(Me, new HexCoord(1, 0), false, k.Hero, k.Mid);
                        if (v == 3) Army(Me, citadel, false);
                    }
                    return true;

                case TurnFamily.ScoutAir:
                    if (k.Aircraft == null) return Skip("no Recce aircraft");
                    Army(Me, citadel, true, k.Weak);
                    Army(Me, new HexCoord(1, 0), false, k.Hero, k.Mid);
                    {
                        ArmyData airfield = Airfield(citadel);
                        int ap = Root.ActionPoints;
                        if (!ArmyActions.DeployUnitFromCard(k.Aircraft, Me, airfield, Root, _selection, out string why))
                            return Skip("aircraft deploy refused: " + why);
                        if (v >= 3 && !AviationActions.TryLaunch(airfield, airfield.Members.Where(u => u.IsAviation).ToList(),
                                k.Catalog.GetCatalog(Me.Faction), _selection, out ArmyData wing, out why))
                            return Skip("wing launch refused: " + why);
                        Root.ActionPoints = ap;
                        Root.AddResource(ResourceType.Energy, 6 - Root.GetResource(ResourceType.Energy));
                        if (v == 2) Root.AddResource(ResourceType.Energy, -Root.GetResource(ResourceType.Energy));
                        if (v == 4) Seen(Army(Foe, new HexCoord(4, -2), false, k.FoeStrong, k.FoeStrong, k.FoeStrong));
                    }
                    return true;

                case TurnFamily.Raid:
                {
                    Army(Me, citadel, true, k.Weak);
                    // Variant 3: a weak raider against a neutral at full strength.
                    if (v == 3) Army(Me, new HexCoord(1, 0), false, k.Hero, k.Weak);
                    else Army(Me, new HexCoord(1, 0), false, k.Hero, k.Strong, k.Strong);
                    if (v == 2) Army(Me, new HexCoord(1, 0), false, k.Mid, k.Mid);
                    PlayerSetupData neutral = Neutral();
                    ArmyData target = v == 3 ? Army(neutral, new HexCoord(3, -1), false,
                            Outnumber(k.FoeStrong, neutral, 4f, k.Hero, k.Weak))
                        : v == 4 ? Army(neutral, new HexCoord(-3, 4), false, k.FoeWeak)
                        : Army(neutral, new HexCoord(3, -1), false, k.FoeWeak);
                    Seen(target);
                    return true;
                }

                case TurnFamily.ActiveDefence:
                    switch (v)
                    {
                        case 1:
                            Army(Me, citadel, true, k.Weak);
                            Army(Me, new HexCoord(1, 0), false, k.Hero, k.Strong, k.Strong);
                            Seen(Army(Foe, new HexCoord(1, -1), false, Outnumber(k.FoeStrong, Foe, 1.5f, k.Hero, k.Strong, k.Strong)));
                            break;
                        case 2:
                            Army(Me, citadel, true, k.Hero, k.Weak, k.Strong, k.Strong, k.Strong, k.Strong, k.Strong, k.Strong);
                            Seen(Army(Foe, new HexCoord(1, -1), false, k.FoeWeak));
                            break;
                        case 3:
                            Army(Me, citadel, true, k.Weak);
                            Army(Me, new HexCoord(3, 0), false, k.Hero, k.Weak);
                            Seen(Army(Foe, new HexCoord(4, -1), false, Outnumber(k.FoeStrong, Foe, 2f, k.Hero, k.Weak)));
                            break;
                        default:
                            // A Base two hexes from the Citadel, the threat five hexes beyond it and seven
                            // from the Citadel: the detachment arrives first, the Citadel stays a safe donor.
                            // Relief is the only answer: the hand holds no card to deploy there, and the
                            // Citadel garrison has bodies above its own floor.
                            Army(Me, citadel, true, k.Hero, k.Weak, k.Strong, k.Strong, k.Strong, k.Strong,
                                k.Strong, k.Strong);
                            Army(Me, citadel, false);
                            Army(Me, citadel, false);
                            Hand.Hand.Clear();
                            Base(k.Catalog, Me, new HexCoord(-2, 0), citadel: false);
                            Army(Me, new HexCoord(-2, 0), true, k.Weak);
                            Seen(Army(Foe, new HexCoord(-7, 4), false, Outnumber(k.FoeStrong, Foe, 3f, k.Strong, k.Strong)));
                            break;
                    }
                    return true;

                case TurnFamily.Attack:
                {
                    HexCoord target = v == 4 ? new HexCoord(6, 0) : new HexCoord(3, -1);
                    if (v != 4) Base(k.Catalog, Foe, target, citadel: false);
                    ArmyData foeGarrison = v == 3
                        ? Army(Foe, target, true, Outnumber(k.FoeStrong, Foe, 2f, k.Hero, k.Strong, k.Strong, k.Strong))
                        : Army(Foe, target, true, k.FoeWeak);
                    Army(Me, citadel, true, k.Weak, k.Mid, k.Mid);
                    HexCoord host = v == 4 ? new HexCoord(5, 0) : new HexCoord(2, -1);
                    if (v == 2)
                        Army(Me, citadel, false, k.Hero, k.Weak);
                    else
                        Army(Me, host, false, k.Hero, k.Strong, k.Strong, k.Strong);
                    if (k.Recce != null)
                        Army(Me, v == 2 ? new HexCoord(2, -1) : host, false, k.Recce);
                    Seen(foeGarrison);
                    AiMapMemory.RefreshVisibleForTest(Me);
                    Rich();
                    return true;
                }

                case TurnFamily.Economy:
                {
                    if (v == 3 && k.BaseCard == null) return Skip("no base card");
                    var site = new HexCoord(2, -1);
                    if (v != 3)
                        HexResourceBonusRegistry.Set(site, new ResourceYields { materials = 3 });
                    else
                    {
                        foreach (HexCoord hex in new[] { new HexCoord(3, -2), new HexCoord(3, -1), new HexCoord(2, -2) })
                            HexResourceBonusRegistry.Set(hex, new ResourceYields { materials = 2, energy = 1 });
                        Hand.AddCard(new CardData(k.BaseCard));
                    }
                    if (v == 2)
                    {
                        ArmyData garrison = Army(Me, citadel, true, k.Weak, k.Hero);
                        if (!AiArmyRoles.EconomyHeroCandidates(Me, garrison).Any())
                            return Skip("the garrison hero is not sparable");
                    }
                    else
                    {
                        Army(Me, citadel, true, k.Weak);
                        Army(Me, new HexCoord(1, 0), false, k.Hero, k.Mid);
                    }
                    if (k.Recce != null)
                        Army(Me, new HexCoord(2, -1), false, k.Recce);
                    AiMapMemory.RefreshVisibleForTest(Me);
                    Rich();
                    if (v == 4) Root.ActionPoints = 0;
                    return true;
                }

                default: // Development
                {
                    if (k.DevelopmentFacility == null || k.DevelopmentHero == null)
                        return Skip("no Research/Production facility or operator hero");
                    HexCoord facilityHex = v == 4 ? new HexCoord(-2, 0) : citadel;
                    if (v == 4)
                    {
                        Base(k.Catalog, Me, facilityHex, citadel: false);
                        Army(Me, facilityHex, true, k.Weak);
                    }
                    InfrastructureBuildOutcome placed = InfrastructureActions.TryPlaceFacility(
                        k.DevelopmentFacility, facilityHex, Me, 0, null);
                    if (!placed.Ok)
                        return Skip("facility placement refused: " + placed.FailReason);
                    if (v == 2)
                    {
                        Army(Me, citadel, true, k.Weak);
                        Army(Me, new HexCoord(2, 0), false, k.DevelopmentHero, k.Mid);
                    }
                    else
                        Army(Me, citadel, true, k.Weak, k.DevelopmentHero);
                    _developmentHero = k.DevelopmentHero;
                    if (v == 3)
                        foreach (ResourceType type in ResourceBundle.All)
                            Root.AddResource(type, -Root.GetResource(type));
                    else
                        Rich();
                    return true;
                }
            }
        }

        // ---- expectations -------------------------------------------------------------------------
        // What each variant's turns must show, besides the boundaries.
        private IEnumerable<string> FamilyExpectations()
        {
            int last = Family.Turns;
            bool RecceLeftGarrison() => ArmyRegistry.AllForOwner(Me).Any(a => a != null && !a.IsGarrison
                && a.Members.Any(u => u != null && AbilityParams.UnitHasAnyRecce(u)));
            switch (Family.Kind)
            {
                case TurnFamily.ScoutGround:
                    if (Family.Variant == 2)
                    {
                        if (StepOnTurn("Scout", 1)) yield return "a Scout step with 0 AP on turn 1";
                        if (!StepOf("Scout", last)) yield return "no Scout step once AP came with turn 2";
                    }
                    else if (!StepOf("Scout", Family.Variant == 1 ? 1 : last))
                        yield return $"no Scout step by turn {(Family.Variant == 1 ? 1 : last)}";
                    if (Family.Variant >= 3 && !RecceLeftGarrison())
                        yield return "the garrison Recce never left the garrison";
                    break;
                case TurnFamily.ScoutAir:
                    if (Family.Variant == 2 && StepOnTurn("Scout", 1) && StepOf("Scout", 1, "AirSweep"))
                        yield return "an air sortie with 0 Energy on turn 1";
                    if (Family.Variant != 2 && !StepOf("Scout", last, "AirSweep"))
                        yield return "no air Recon sortie";
                    break;
                case TurnFamily.Raid:
                    if (Family.Variant == 3 && StepOf("Raid", last))
                        yield return "a Raid against a neutral too strong to beat";
                    if (Family.Variant != 3 && !StepOf("Raid", last))
                        yield return "no Raid step";
                    break;
                case TurnFamily.ActiveDefence:
                    if (!StepOf("ActiveDefence", last))
                        yield return "no ActiveDefence step";
                    break;
                // An assault on a Base waits for its Disruption carrier (the entry gate): a Disruption
                // step, or the Recon demand for the carrier, is the operation going on.
                case TurnFamily.Attack:
                    bool assault = Lines.Any(l => l.Contains("attack provision") && l.Contains("- OK host #"));
                    if (Family.Variant == 3 && assault)
                        yield return "an assault on a Base too strong to take";
                    bool asksForCarrier = Lines.Any(l => l.Contains("[Demand][Recon]") && l.Contains("profile=disruption"));
                    if (Family.Variant != 3 && !StepOf("Attack", last) && !StepOf("Scout", last, "Disrupt") && !asksForCarrier)
                        yield return "neither an Attack step nor its Disruption (step or demand)";
                    break;
                case TurnFamily.Economy:
                    if (Family.Variant == 4 && StepOnTurn("Economy", 1))
                        yield return "an Economy step with 0 AP on turn 1";
                    if (!StepOf("Economy", last))
                        yield return "no Economy step";
                    break;
                default:
                    bool development = StepOf("Development", last) || Lines.Any(l => l.Contains("[Dev][Outcome]")
                        && System.Text.RegularExpressions.Regex.IsMatch(l, @"(challengesWon|challengesLost|upgradesAttached)=[1-9]"));
                    if (Family.Variant == 3 && development)
                        yield return "Development work without resources";
                    // An operator already at its facility is never taken away from it (whether a
                    // challenge is worth playing is the evaluator's call).
                    if (Family.Variant == 1 && !ArmyRegistry.AllForOwner(Me).Any(a => a != null
                            && a.Hex.Equals(new HexCoord(0, 0))
                            && a.Members.Any(u => u != null && u.IsHero && u.Name == _developmentHero.displayName)))
                        yield return "the operator left its facility";
                    if ((Family.Variant == 2 || Family.Variant == 4) && !development)
                        yield return "no Development work";
                    break;
            }
        }
    }
}
#endif
