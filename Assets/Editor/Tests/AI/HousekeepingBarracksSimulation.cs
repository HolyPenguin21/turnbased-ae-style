#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using NUnit.Framework;

namespace Game.EditorTests
{
    // ===========================================================================================
    //  HOUSEKEEPING BARRACKS SIMULATION (2026-11-20, owner request after match Halden vs Orlan).
    //  A multi-turn walk of ONE own Base / Citadel hex with a garrison (barracks) and the strike
    //  fist standing on it, through the real ArmyReorganizationPlanner: before the mobilization
    //  gate opens (the fist is a FREE field army between muster legs), after it opens (the fist is
    //  the claimed Attack preparation host: inbound only, frozen target roster), with no threat, a
    //  far threat, a near weak threat and a near strong threat. Each turn applies the plan the
    //  planner returns and prints who moved where.
    //  What is scripted (not the live game): the garrison release floor
    //  (AiArmyRoles.GarrisonDefenceFloor = max(share, threat) - resident field power; the gathering
    //  host does not count as resident on the release side), the threat benchmarks and the turn
    //  phases. Muster Intake / preparation Assemble are Strategy steps outside Housekeeping and are
    //  not simulated: the table shows only what Housekeeping itself does on the hex.
    //  Category Extended: excluded from the routine gate; run it with
    //  -testFilter HousekeepingBarracksSimulation and read the test output.
    // ===========================================================================================
    [Category("Extended")]
    public sealed class HousekeepingBarracksSimulation
    {
        private enum Threat { None, FarWeak, NearWeak, NearStrong }

        private sealed class Turn
        {
            public int N;
            public bool GateOpen;
            public Threat Threat;
            public bool WalkInArrives;
            public string Note;
        }

        private static readonly Turn[] Script =
        {
            new Turn { N = 1, GateOpen = false, Threat = Threat.None, Note = "peace, fist gathers" },
            new Turn { N = 2, GateOpen = false, Threat = Threat.FarWeak, WalkInArrives = true,
                Note = "muster walk-in arrives; far weak enemy (eta 5)" },
            new Turn { N = 3, GateOpen = false, Threat = Threat.NearWeak, Note = "weak raider next door (eta 1)" },
            new Turn { N = 4, GateOpen = false, Threat = Threat.NearStrong, Note = "strong army next door (eta 1)" },
            new Turn { N = 5, GateOpen = false, Threat = Threat.None, Note = "threat gone" },
            new Turn { N = 6, GateOpen = true, Threat = Threat.None, Note = "gate open: fist = preparation host" },
            new Turn { N = 7, GateOpen = true, Threat = Threat.NearStrong, Note = "strong army next door" },
            new Turn { N = 8, GateOpen = true, Threat = Threat.FarWeak, Note = "threat recedes (eta 5)" },
        };

        private int _key = 50000;
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();

        private ReorgUnit Body(string name, float attack, float defence, float hp)
        {
            var u = new ReorgUnit
            {
                Key = _key++, Power = attack + defence + hp, IsGroundCombatant = true, IsGroundBattleBody = true,
                MoveCurrent = 3, MoveMax = 3, StrikeKey = name.ToLowerInvariant(),
                CombatProfile = new WorthIt.DefenderProfile(defence, false, attack: attack, hitPoints: hp),
            };
            _names[u.Key] = name;
            return u;
        }

        private ReorgUnit Hero(string name, int command, bool support)
        {
            var u = new ReorgUnit
            {
                Key = _key++, IsHero = true, CommandRating = command, IsGroundCombatant = false,
                HeroRole = support ? HeroOperationalRole.SupportOperator : HeroOperationalRole.CombatLeader,
                MoveCurrent = support ? 2 : 3, MoveMax = support ? 2 : 3,
                TypeTags = support ? new[] { UnitTypeTag.Hero, UnitTypeTag.Support } : new[] { UnitTypeTag.Hero },
            };
            _names[u.Key] = name + (support ? "(S)" : "*");
            return u;
        }

        private static WorthIt.DefenderProfile[] Attackers(int count, float attack, float defence, float hp) =>
            Enumerable.Range(0, count).Select(_ => new WorthIt.DefenderProfile(defence, false,
                attack: attack, hitPoints: hp)).ToArray();

        [TestCase("Citadel", 0.10f, 2f)]
        [TestCase("Base", 0.05f, 1f)]
        public void BarracksHex_OverSeveralTurns(string site, float share, float hexBonus)
        {
            const int Garrison = 1, Fist = 2, WalkIn = 3, Shell = 4;
            // The Halden Citadel of T24: a Support commander, two more Support heroes, five bodies;
            // the fist Nadia Thorne with three strong bodies; a heroless pair walks in by muster.
            var roster = new Dictionary<int, List<ReorgUnit>>
            {
                [Garrison] = new List<ReorgUnit>
                {
                    Hero("Vector", 8, true), Hero("Tessa", 4, true), Hero("Dorian", 6, false),
                    Body("MedInf", 5, 3, 6), Body("MedInf", 5, 3, 6), Body("DustMedic", 3, 2, 5),
                    Body("Duelist", 7, 3, 7), Body("Mantis", 7, 4, 8),
                },
                [Fist] = new List<ReorgUnit>
                {
                    Hero("Nadia", 6, false), Body("Claimjumper", 6, 4, 8), Body("Atlas", 6, 4, 8),
                },
                [WalkIn] = new List<ReorgUnit>(),
                [Shell] = new List<ReorgUnit>(),
            };
            List<ReorgUnit> walkInBodies = new List<ReorgUnit> { Body("LightInf", 4, 2, 5), Body("LightInf", 4, 2, 5) };
            // The fist's frozen target roster once the gate opens: the six strongest bodies on the hex.
            List<string> targetRoster = null;

            var log = new StringBuilder();
            log.AppendLine($"=== {site} (share {share:P0}, hex bonus {hexBonus}) ===");
            log.AppendLine("turn | gate | threat | release floor | housekeeping operations | garrison | fist | walk-in");
            float fistAfterFarThreat = -1f;
            var hostWeakenedAfterGate = new List<string>();

            foreach (Turn turn in Script)
            {
                if (turn.WalkInArrives)
                    roster[WalkIn].AddRange(walkInBodies);
                if (turn.GateOpen && targetRoster == null)
                    targetRoster = roster.Values.SelectMany(l => l).Where(u => !u.IsHero)
                        .OrderByDescending(u => u.Power).ThenBy(u => u.Key).Take(6)
                        .Select(u => u.StrikeKey).ToList();

                var threats = new List<ReorgThreatBenchmark>();
                float threatPower = 0f;
                int eta = 0;
                List<int> bodyKeys = roster.Values.SelectMany(l => l).Where(u => u.IsGroundCombatant)
                    .Select(u => u.Key).ToList();
                switch (turn.Threat)
                {
                    case Threat.FarWeak:
                        eta = 5; threatPower = 2 * (3 + 1 + 4);
                        threats.Add(new ReorgThreatBenchmark { ArmyId = 900, EtaToGroup = 5, EtaToNearestBase = 2,
                            Members = Attackers(2, 3, 1, 4), TargetableUnitKeys = new HashSet<int>(bodyKeys) });
                        break;
                    case Threat.NearWeak:
                        eta = 1; threatPower = 2 * (3 + 1 + 4);
                        threats.Add(new ReorgThreatBenchmark { ArmyId = 901, EtaToGroup = 1, EtaToNearestBase = 1,
                            Members = Attackers(2, 3, 1, 4), TargetableUnitKeys = new HashSet<int>(bodyKeys) });
                        break;
                    case Threat.NearStrong:
                        eta = 1; threatPower = 7 * (7 + 2 + 7);
                        threats.Add(new ReorgThreatBenchmark { ArmyId = 902, EtaToGroup = 1, EtaToNearestBase = 1,
                            Members = Attackers(7, 7, 2, 7), TargetableUnitKeys = new HashSet<int>(bodyKeys) });
                        break;
                }

                // Release floor stand-in (AiArmyRoles.GarrisonDefenceFloor): the share of the
                // force, raised to the threat (x1.3, only within the withdraw horizon eta <= 2),
                // minus the own field power that stands on the hex and does not leave on an
                // operation (a free fist does; the preparation host does not on the release side).
                float allPower = roster.Values.SelectMany(l => l).Where(u => !u.IsHero).Sum(u => u.Power);
                float threatFloor = eta > 0 && eta <= 2 ? 1.3f * threatPower : 0f;
                float resident = turn.GateOpen ? 0f
                    : roster[Fist].Concat(roster[WalkIn]).Where(u => !u.IsHero).Sum(u => u.Power);
                float floor = System.Math.Max(0f, System.Math.Max(share * allPower * 2f, threatFloor) - resident);

                var garrison = new ReorgContainer
                {
                    ArmyId = Garrison, Role = ReorgPhysicalRole.Garrison, IsGarrison = true,
                    CanReceive = true, CanDonate = true, CanChangeComposition = true, CanReorderCommander = true,
                    SingletonExempt = true, GarrisonNonHeroFloor = 1, GarrisonPowerFloor = floor,
                    Units = roster[Garrison].ToList(),
                };
                ReorgContainer fist = turn.GateOpen
                    ? new ReorgContainer
                    {
                        ArmyId = Fist, Role = ReorgPhysicalRole.ProtectedMissionArmy, MissionLabel = "Attack:PreparationHost",
                        CanReceive = true, IsMissionReceiver = true, CanDonate = false, CanChangeComposition = false,
                        CanReorderCommander = true, MayReleaseExcessHeroes = true,
                        PreparationTargetKeys = targetRoster, Units = roster[Fist].ToList(),
                    }
                    : new ReorgContainer
                    {
                        ArmyId = Fist, Role = ReorgPhysicalRole.NormalFieldArmy, CanReceive = true, CanDonate = true,
                        CanChangeComposition = true, CanReorderCommander = true, Units = roster[Fist].ToList(),
                    };
                var containers = new List<ReorgContainer> { garrison, fist };
                if (roster[WalkIn].Count > 0)
                    containers.Add(new ReorgContainer
                    {
                        ArmyId = WalkIn, Role = ReorgPhysicalRole.NormalFieldArmy, CanReceive = true, CanDonate = true,
                        CanChangeComposition = true, CanReorderCommander = true, Units = roster[WalkIn].ToList(),
                    });
                containers.Add(new ReorgContainer
                {
                    ArmyId = Shell, Role = ReorgPhysicalRole.EmptyReusableArmy, CanReceive = true, CanDonate = true,
                    CanChangeComposition = true, CanReorderCommander = true, Units = roster[Shell].ToList(),
                });
                var group = new LocalForceGroup { HexDefenseBonus = hexBonus, Containers = containers };
                group.ThreatBenchmarks.AddRange(threats);

                float fistPowerBefore = roster[Fist].Where(u => !u.IsHero).Sum(u => u.Power);
                ReorganizationPlan plan = ArmyReorganizationPlanner.Plan(group);

                // Apply the plan's membership (every container the planner saw).
                Dictionary<int, ReorgUnit> byKey = roster.Values.SelectMany(l => l).ToDictionary(u => u.Key);
                foreach (ReorgContainer c in containers)
                    if (plan.ExpectedMembership.TryGetValue(c.ArmyId, out List<int> keys))
                        roster[c.ArmyId] = keys.Select(k => byKey[k]).ToList();

                // The claimed host only receives, or exchanges a non-roster body 1-for-1 for a
                // missing roster position (0.86, owner 2026-11-16): its power never drops.
                float hostAfter = roster[Fist].Where(u => !u.IsHero).Sum(u => u.Power);
                if (turn.GateOpen && hostAfter + 0.01f < fistPowerBefore)
                    hostWeakenedAfterGate.Add($"T{turn.N}: {fistPowerBefore:0} -> {hostAfter:0}");
                if (turn.N == 2)
                    fistAfterFarThreat = roster[Fist].Where(u => !u.IsHero).Sum(u => u.Power);

                string ops = plan.IsEmpty ? "-" : string.Join("; ", plan.Transfers.Select(t =>
                    t.IsWholeFold ? $"fold #{t.FromArmyId}->#{t.ToArmyId}"
                    : t.IsSwap ? $"swap {Name(t.UnitKey)}#{t.FromArmyId}<->{Name(t.SwapUnitKey)}#{t.ToArmyId}"
                    : t.IsReorder ? $"commander {Name(t.UnitKey)}"
                    : $"{Name(t.UnitKey)} #{t.FromArmyId}->#{t.ToArmyId} ({t.Reason})"));
                log.AppendLine($"T{turn.N} | {(turn.GateOpen ? "open" : "closed")} | {turn.Threat} ({turn.Note}) | "
                    + $"{floor.ToString("0.#", CultureInfo.InvariantCulture)} | {ops} | {Roster(roster[Garrison])} | "
                    + $"{Roster(roster[Fist])} | {Roster(roster[WalkIn].Concat(roster[Shell]).ToList())}");
            }

            TestContext.WriteLine(log.ToString());
            TestContext.Progress.WriteLine(log.ToString());

            // The invariants the 2026-11-20 change is about (everything else is for reading).
            Assert.That(fistAfterFarThreat, Is.GreaterThanOrEqualTo(6 + 4 + 8 + 6 + 4 + 8 - 0.01f),
                "a far threat the hex holds anyway never empties the fist into the garrison\n" + log);
            Assert.That(hostWeakenedAfterGate, Is.Empty,
                "the claimed preparation host is never weakened once the gate is open\n" + log);
        }

        private string Name(int key) => _names.TryGetValue(key, out string n) ? n : "u" + key;

        private string Roster(List<ReorgUnit> units) =>
            units.Count == 0 ? "[]"
                : "[" + string.Join(",", units.Select(u => _names[u.Key])) + "] P="
                  + units.Where(u => !u.IsHero).Sum(u => u.Power).ToString("0", CultureInfo.InvariantCulture);
    }
}
#endif
