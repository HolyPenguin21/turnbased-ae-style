#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // Calibration probe, not a regression gate: plays plain (ability-less) rosters on the REAL battle
    // layout (BattleGrid.FromArmies + BattleAi.ArrangeArmy + BattleAi.ChooseAction + the kernel's exchange)
    // and prints the real attacker win rate next to WorthIt.EstimateHexControl. Measured 2026-11-18:
    // equal sizes agree (5v5 melee 0.55/0.52); a side beyond the 5-cell front row is overrated by the
    // forecast (7 melee vs 5 ranged: real 0.46, forecast 0.92; 5 ranged vs 7 melee: real 0.51, forecast 0.04).
    // About 15 minutes for the full sweep, hence Extended.
    [Category("Extended")]
    public sealed class GridVsForecastCalibration
    {
        struct S { public int a, d, hp, init, range; public S(int a, int d, int hp, int init, int range) { this.a = a; this.d = d; this.hp = hp; this.init = init; this.range = range; } }

        static int _id = 1;
        static ArmyData Army(PlayerSetupData owner, S[] units)
        {
            var army = ArmyData.WithIdentity(_id++); army.Owner = owner;
            foreach (var s in units)
            {
                UnitData u = UnitData.CreateProjection(_id++);
                u.Owner = owner; u.Name = "u" + u.RuntimeId; u.Attack = s.a; u.Defense = s.d;
                u.HitPointsCurrent = u.HitPointsMax = s.hp; u.Initiative = s.init; u.Range = s.range;
                army.Members.Add(u);
            }
            return army;
        }

        // returns attacker win fraction, real grid
        static float RealGrid(S[] att, S[] def, int bonus, int trials, out float avgRounds)
        {
            int wins = 0; long rounds = 0;
            var A = new PlayerSetupData { Nickname = "A" }; var D = new PlayerSetupData { Nickname = "D" };
            for (int t = 0; t < trials; t++)
            {
                ArmyData atk = Army(A, att), dfn = Army(D, def);
                var grid = BattleGrid.FromArmies(atk, dfn);
                var m = AbilityMagnitudes.Default;
                BattleAi.ArrangeArmy(grid, dfn, BattleGrid.DefenderFrontRow, BattleGrid.DefenderBackRow, atk, m, dfn, bonus);
                BattleAi.ArrangeArmy(grid, atk, BattleGrid.AttackerFrontRow, BattleGrid.AttackerBackRow, dfn, m, dfn, bonus);
                var rng = new System.Random(1000 + t);
                var waits = new Dictionary<UnitData, int>();
                var prev = new Dictionary<UnitData, Vector2Int>();
                int round = 1;
                for (; round <= 50; round++)
                {
                    var order = BattleTurnOrder.BuildOrder(grid, atk, dfn, rng.Next());
                    for (int i = 0; i < order.Count; i++)
                    {
                        UnitData actor = order[i];
                        if (actor.HitPointsCurrent <= 0) continue;
                        if (!atk.Members.Any(u => u.HitPointsCurrent > 0) || !dfn.Members.Any(u => u.HitPointsCurrent > 0)) break;
                        ArmyData own = atk.Members.Contains(actor) ? atk : dfn, enemy = own == atk ? dfn : atk;
                        var act = BattleAi.ChooseAction(grid, actor, waits, own, enemy, m, order, i, false, dfn, bonus, prev, null);
                        if (act.Kind == BattleAi.AiActionKind.Move)
                        {
                            grid.TryFindPosition(actor, out int r, out int c);
                            prev[actor] = new Vector2Int(r, c);
                            grid.Set(r, c, null); grid.Set(act.Row, act.Col, actor);
                        }
                        else if (act.Kind == BattleAi.AiActionKind.Attack && act.Target != null)
                        {
                            int f1 = 0, f2 = 0;
                            int defPool = act.Target.Defense + (dfn.Members.Contains(act.Target) ? bonus : 0);
                            var o = BattleSimulationKernel.ResolveExchange(actor.Attack, defPool, actor.Abilities,
                                act.Target.TypeTags, act.Target.Abilities, ref f1, ref f2, act.Target.HitPointsCurrent, m, rng);
                            act.Target.HitPointsCurrent = Mathf.Max(0, act.Target.HitPointsCurrent - o.Damage);
                            if (act.Target.HitPointsCurrent <= 0 && grid.TryFindPosition(act.Target, out int tr, out int tc)) grid.Set(tr, tc, null);
                        }
                    }
                    if (!atk.Members.Any(u => u.HitPointsCurrent > 0) || !dfn.Members.Any(u => u.HitPointsCurrent > 0)) break;
                }
                rounds += round;
                bool aAlive = atk.Members.Any(u => u.HitPointsCurrent > 0), dAlive = dfn.Members.Any(u => u.HitPointsCurrent > 0);
                if (aAlive && !dAlive) wins++;
            }
            avgRounds = rounds / (float)trials;
            return wins / (float)trials;
        }

        static WorthIt.DefenderProfile P(S s) => new WorthIt.DefenderProfile(s.d, false, new List<UnitTypeTag>(), s.a, s.hp, s.init,
            new List<string>(), s.hp, true, false, 0, false, s.range);

        [Test]
        public void Probe()
        {
            var sb = new System.Text.StringBuilder();
            var scen = new List<(string name, S[] att, S[] def, int bonus)>();
            foreach (int n in new[] { 5, 6, 7, 8 })
            {
                scen.Add(($"{n} melee vs 5 r2", Rep(n, new S(4,2,5,1,1)), Rep(5, new S(4,2,5,1,2)), 0));
                scen.Add(($"{n} melee vs 5 melee", Rep(n, new S(4,2,5,1,1)), Rep(5, new S(4,2,5,1,1)), 0));
                scen.Add(($"{n} r2 vs 5 r2", Rep(n, new S(4,2,5,1,2)), Rep(5, new S(4,2,5,1,2)), 0));
                scen.Add(($"5 r2 vs {n} melee", Rep(5, new S(4,2,5,1,2)), Rep(n, new S(4,2,5,1,1)), 0));
            }
            foreach (var sc in scen)
            {
                float real = RealGrid(sc.att, sc.def, sc.bonus, 60, out float rounds);
                float fc = WorthIt.EstimateHexControl(sc.att.Select(P).ToList(), default,
                    new[] { new WorthIt.DefendingArmy(sc.def.Select(P).ToList(), default, sc.bonus) }, sc.bonus).WinChance;
                sb.AppendLine($"{sc.name,-36} real={real:0.00} (rounds {rounds:0.0})  forecast={fc:0.00}");
            }
            TestContext.WriteLine(sb.ToString());
            Assert.Pass(sb.ToString());
        }
        static S[] Rep(int n, S s) => Enumerable.Repeat(s, n).ToArray();
    }
}
#endif
