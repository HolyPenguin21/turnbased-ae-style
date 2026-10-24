// Compile alongside the game's EditMode tests. No scene or asset loading is needed.
// Both armies are committed to fighting: this audits tactical participation, not retreat policy.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    [NonParallelizable]
    public sealed class BattleScenarioAudit
    {
        private sealed class Spec
        {
            public string Name; public int Attack, Defense, Hp, Range, Initiative, Fate;
            public bool Hero; public string[] Abilities; public string Tags;
            public Spec(string name, int a, int d, int h, int r, int i, bool hero = false,
                int fate = 0, string tags = "", params string[] abilities)
            { Name=name; Attack=a; Defense=d; Hp=h; Range=r; Initiative=i; Hero=hero; Fate=fate; Tags=tags; Abilities=abilities; }
        }

        // Snapshot of the two faction catalogs at upstream 82f0b967, excluding aviation.
        private static readonly Spec[] Specs = {
            new Spec("Light Infantry",3,1,4,2,1,false,0,"0,3"),
            new Spec("Ash Crossbowman",3,1,5,2,1,false,0,"0,3"),
            new Spec("Hazard Pioneer",3,2,4,1,1,false,0,"0,3","Counterattack"),
            new Spec("Ash Drifter",3,1,5,1,2,false,0,"0,3","Bodyguard","Counterattack"),
            new Spec("AT Infantry",4,2,4,2,1,false,0,"0,3","Hyperkinetic"),
            new Spec("Light Tank",5,3,5,2,1,false,0,"1,2"),
            new Spec("Rust Tank",4,3,5,2,1,false,0,"1,2,4"),
            new Spec("Heavy Tank",6,4,6,2,1,false,0,"1,4,2","CeramicArmor"),
            new Spec("Artillery Cannon",4,1,2,3,2,false,0,"7","ShockAttack"),
            new Spec("Scrap Mortar",4,1,2,3,2,false,0,"1,4"),
            new Spec("Artillery Tank",7,2,5,4,2,false,0,"1,4","Splash"),
            new Spec("Ash Howitzer",7,2,3,4,2,false,0,"1,4","ShockAttack","Splash"),
            new Spec("Trench Guardian",3,4,4,1,1,false,0,"0,3","Bodyguard"),
            new Spec("Rad Brute",4,3,6,1,2,false,0,"0,3","Regeneration","Berserk"),
            new Spec("Dust Medic",1,2,4,1,1,false,0,"0,7,3","Heal"),
            new Spec("Boatswain",4,4,6,2,1,false,0,"1,4,2","Drill"),
            new Spec("Siege Grenadier",4,3,4,2,1,false,0,"0,3","Splash"),
            new Spec("Flamer",4,2,4,1,2,false,0,"0,3","Pyrokinetic","Scorcher"),
            new Spec("Kessa Vrail",0,0,6,0,1,true,4,"0,8","Heal"),
            new Spec("Hank Mercer",0,0,7,0,2,true,5,"0,8","Drill"),
            new Spec("Nadia Thorne",0,0,6,0,2,true,5,"0,8","RapidReaction"),
        };

        private sealed class Case
        {
            public string Name; public string[] A,B;
            public Case(string name,string a,string b){Name=name;A=a.Split('|');B=b.Split('|');}
        }
        private static readonly Case[] Cases = {
            new Case("01_two_r2","Light Infantry|Light Infantry","Ash Crossbowman|Ash Crossbowman"),
            new Case("02_melee","Hazard Pioneer|Hazard Pioneer","Ash Drifter|Ash Drifter"),
            new Case("03_melee_vs_shooters","Hazard Pioneer|Hazard Pioneer","Ash Crossbowman|Ash Crossbowman"),
            new Case("04_tanks","Light Tank|Light Tank","Rust Tank|Rust Tank"),
            new Case("05_infantry_tank","Light Infantry|Light Infantry|Light Tank","Ash Crossbowman|Ash Crossbowman|Rust Tank"),
            new Case("06_artillery_r3","Artillery Cannon|Artillery Cannon","Scrap Mortar|Scrap Mortar"),
            new Case("07_artillery_r4","Artillery Tank|Artillery Tank","Ash Howitzer|Ash Howitzer"),
            new Case("08_range_disadvantage","Artillery Cannon|Artillery Cannon","Ash Howitzer|Ash Howitzer"),
            new Case("09_tank_artillery","Heavy Tank|Artillery Cannon|Artillery Tank","Rust Tank|Scrap Mortar|Ash Howitzer"),
            new Case("10_guard_artillery","Trench Guardian|Artillery Cannon|Artillery Cannon","Rad Brute|Scrap Mortar|Scrap Mortar"),
            new Case("11_medic","Light Infantry|Light Infantry|Dust Medic","Ash Crossbowman|Ash Crossbowman|Ash Drifter"),
            new Case("12_drill","Boatswain|Light Infantry|Artillery Cannon","Rust Tank|Ash Crossbowman|Scrap Mortar"),
            new Case("13_heroes","Kessa Vrail|Light Infantry|Light Tank|Artillery Cannon","Hank Mercer|Ash Crossbowman|Rust Tank|Scrap Mortar"),
            new Case("14_multiple_heroes","Kessa Vrail|Nadia Thorne|Light Infantry|Light Tank|Artillery Cannon","Hank Mercer|Ash Crossbowman|Ash Crossbowman|Rust Tank|Scrap Mortar"),
            new Case("15_fragile_artillery_overflow","Kessa Vrail|Artillery Cannon|Artillery Cannon|Artillery Cannon|Artillery Cannon|Artillery Tank","Hank Mercer|Ash Crossbowman|Rust Tank|Rust Tank|Scrap Mortar|Scrap Mortar"),
            new Case("16_full_frontline","Nadia Thorne|Light Infantry|Light Infantry|AT Infantry|Light Tank|Heavy Tank","Hank Mercer|Ash Crossbowman|Ash Crossbowman|Rust Tank|Ash Drifter|Rad Brute"),
            new Case("17_splash","Siege Grenadier|Flamer|Artillery Tank","Ash Drifter|Rad Brute|Ash Howitzer"),
            new Case("18_damaged_shooter","Light Infantry|Light Infantry","Ash Crossbowman|Ash Crossbowman"),
            new Case("19_stronger_opponent","Light Infantry|Light Infantry","Rust Tank|Rust Tank"),
            new Case("20_heavy_battle","Nadia Thorne|Heavy Tank|Light Tank|AT Infantry|Artillery Tank|Artillery Cannon|Dust Medic","Hank Mercer|Rust Tank|Rad Brute|Ash Drifter|Ash Crossbowman|Ash Howitzer|Scrap Mortar"),
            new Case("21_armor_and_soft_target","Dust Medic|Light Infantry","Heavy Tank|Ash Crossbowman"),
        };

        [Test]
        public void ExportMatchedTacticalScenarios()
        {
            string output = Environment.GetEnvironmentVariable("TACTICS_OUTPUT");
            if (string.IsNullOrEmpty(output)) Assert.Ignore("Set TACTICS_OUTPUT to export the comparison.");
            var lines = new List<string> { "case\tseed\tmirrored\tfrontA\tfrontB\treadyA\treadyB\tmoves\twastedMoves\tavoidablePasses\tsupportHolds\timpossibleShots\tfirstTwoShots\tunits\theroesLost\trounds\twinner\thpA\thpB\tarrangeMs\tlayoutA\tlayoutB" };
            var traces = new List<string>();
            int seeds = int.TryParse(Environment.GetEnvironmentVariable("TACTICS_SEEDS"),out int n) ? n : 4;
            foreach (Case c in Cases)
            {
                string only = Environment.GetEnvironmentVariable("TACTICS_CASE");
                if (!string.IsNullOrEmpty(only) && !c.Name.StartsWith(only)) continue;
                for (int seed = 0; seed < seeds; seed++)
                    for (int mirrored = 0; mirrored < 2; mirrored++)
                        lines.Add(Run(c,seed,mirrored != 0,traces));
                Console.WriteLine("Completed " + c.Name);
            }
            File.WriteAllLines(output,lines);
            File.WriteAllLines(output+".trace.txt",traces);
            Assert.That(lines.Count,Is.GreaterThan(1));
        }

        private static ArmyData Army(string[] names,int id,int identity, PlayerSetupData owner)
        {
            var army = ArmyData.WithIdentity(id); army.Owner=owner; army.Name="Side "+id;
            foreach(string name in names)
            {
                Spec s=Specs.First(x=>x.Name==(name=="Artillery Tank"?"Artillery Tank":name));
                UnitData u=UnitData.CreateProjection(identity++);
                u.Name=s.Name; u.Owner=owner; u.Attack=s.Attack; u.Defense=s.Defense;
                u.HitPointsCurrent=u.HitPointsMax=s.Hp; u.Range=s.Range; u.Initiative=s.Initiative;
                u.IsHero=s.Hero; u.Fate=u.FateMax=s.Fate; u.CommandRating=8;
                foreach(string tag in s.Tags.Split(','))
                    if(!string.IsNullOrEmpty(tag))u.TypeTags.Add((UnitTypeTag)int.Parse(tag));
                foreach(string ability in s.Abilities)u.Abilities.Add(ability);
                army.Members.Add(u);
            }
            return army;
        }

        private static string Run(Case c,int seed,bool mirrored,List<string> traces)
        {
            var ownerA=new PlayerSetupData{Nickname="A"};var ownerB=new PlayerSetupData{Nickname="B"};
            var a=Army(mirrored?c.B:c.A,10+seed*2,100+seed*40,ownerA);
            var b=Army(mirrored?c.A:c.B,11+seed*2,120+seed*40,ownerB);
            if(c.Name.StartsWith("18")) { a.Members.Last().HitPointsCurrent=1;b.Members.Last().HitPointsCurrent=1; }
            var all=a.Members.Concat(b.Members).ToList();
            var grid=BattleGrid.FromArmies(a,b);
            var watch=System.Diagnostics.Stopwatch.StartNew();
            BattleAi.ArrangeArmy(grid,a,3,4,b,AbilityMagnitudes.Default,b);
            BattleAi.ArrangeArmy(grid,b,1,0,a,AbilityMagnitudes.Default,b);
            watch.Stop();
            int frontA=Front(grid,a,3),frontB=Front(grid,b,1),readyA=Ready(grid,a),readyB=Ready(grid,b);
            string layoutA=Layout(grid,a),layoutB=Layout(grid,b);
            var state=new BattleState(default,a,b,grid,7919+seed);
            var engine=new BattleEngine(state,AbilityMagnitudes.Default,new System.Random(7919+seed));
            var rng=new System.Random(seed*131+73);
            var wait=new Dictionary<UnitData,int>();var previous=new Dictionary<UnitData,Vector2Int>();
            int moves=0,wasted=0,passes=0,supportHolds=0,impossibleShots=0;var shots=new HashSet<UnitData>();
            if(seed==0)traces.Add($"{c.Name} mirror={mirrored} START A: {layoutA} B: {layoutB}");
            int iterations=0;
            while(state.Round<=12 && !engine.EvaluateBattleEnd().Ended)
            {
                Assert.That(++iterations,Is.LessThan(40),"audit round controller stalled in "+c.Name);
                engine.StartRound();
                if(state.TurnOrder.Count==0)break;
                bool nextRound=false;
                while(!nextRound && !engine.EvaluateBattleEnd().Ended)
                {
                    if(state.TurnIndex<0||state.TurnIndex>=state.TurnOrder.Count)break;
                    UnitData actor=state.TurnOrder[state.TurnIndex];
                    ArmyData own=state.OwningArmy(actor),enemy=state.OpposingArmy(own);
                    grid.TryFindPosition(actor,out int row,out int col);
                    int distance=Distance(grid,actor,row,col);
                    var action=BattleAi.ChooseAction(grid,actor,wait,own,enemy,AbilityMagnitudes.Default,
                        state.TurnOrder,state.TurnIndex,false,b,0,previous,engine.Reactions);
                    if(seed==0)traces.Add($"{c.Name} mirror={mirrored} R{state.Round} {actor.RuntimeId}:{actor.Name}@{row},{col}: {action.Kind} {(action.Kind==BattleAi.AiActionKind.Attack?action.Target?.Name:$"{action.Row},{action.Col}")}");
                    if(action.Kind==BattleAi.AiActionKind.Attack)
                    {
                        Assert.That(engine.CanGroundAttack(actor,action.Target),Is.True);
                        if(state.Round<=2)shots.Add(actor);
                        if(!CanHurt(actor,action.Target))impossibleShots++;
                        engine.CommitGroundAttack(actor);
                        Attack(engine,actor,action.Target,rng,false);
                    }
                    else if(action.Kind==BattleAi.AiActionKind.Move)
                    {
                        moves++;
                        if(Distance(grid,actor,action.Row,action.Col)>=distance)wasted++;
                        Assert.That(engine.TryMoveUnit(actor,row,col,action.Row,action.Col),Is.True);
                    }
                    else if(distance>0 && distance<26)
                    {
                        if(SupportsEngagedAlly(grid,actor,own))supportHolds++;
                        else passes++;
                    }
                    var advance=engine.AdvanceTurn();
                    nextRound=advance==BattleTurnAdvance.NextRound||advance==BattleTurnAdvance.NoTurn;
                }
            }
            var end=engine.EvaluateBattleEnd();
            string winner=!end.Ended?"draw":end.AttackerAlive?"A":"B";
            int heroesLost=all.Count(u=>u.IsHero&&u.HitPointsCurrent<=0);
            return string.Join("\t",new object[]{c.Name,seed,mirrored?1:0,frontA,frontB,readyA,readyB,moves,wasted,passes,supportHolds,impossibleShots,
                shots.Count,all.Count(u=>!u.IsHero),heroesLost,Math.Min(state.Round,12),winner,
                a.Members.Where(u=>!u.IsHero).Sum(u=>u.HitPointsCurrent),b.Members.Where(u=>!u.IsHero).Sum(u=>u.HitPointsCurrent),
                watch.Elapsed.TotalMilliseconds.ToString("F3",CultureInfo.InvariantCulture),layoutA,layoutB});
        }

        private static void Attack(BattleEngine engine,UnitData actor,UnitData target,System.Random rng,bool counter)
        {
            BattleState state=engine.State;
            UnitData hero=BattleTurnOrder.LivingCommanderOnGrid(state.Grid,state.OwningArmy(actor));
            UnitData enemyHero=BattleTurnOrder.LivingCommanderOnGrid(state.Grid,state.OwningArmy(target));
            int defense=(target.IsHero?target.FateMax:target.Defense)
                +BattleRules.GetGuardedDefenseBonus(state.Grid,target,state.Defender);
            var duel=new BattleChallengeSession(BattleChallengeMode.GroundCombat,actor,target,actor.Attack,defense,
                hero?.Fate??0,enemyHero?.Fate??0,AbilityMagnitudes.Default,
                rollTokens:n=>BattleSimulationKernel.RollTokens(n,rng));
            duel.Roll();
            while(duel.TryNextFateTurn(out bool defending))
            {
                bool spent=false;
                while(duel.ShouldAiSpend(defending))
                { if(!duel.TrySpend(defending,out _,out bool hit))break;spent=true;if(!hit)break; }
                duel.ReportFateTurn(spent);
            }
            var hitResult=engine.ResolveAndApplyGroundAttack(actor,target,duel.ToRollResult(),hero,enemyHero,counter);
            if(hitResult.Counterattacker!=null && actor.HitPointsCurrent>0)
                Attack(engine,hitResult.Counterattacker,actor,rng,true);
        }

        private static int Front(BattleGrid grid,ArmyData army,int front)=>army.Members.Count(u=>!u.IsHero&&grid.TryFindPosition(u,out int row,out _)&&row==front);
        private static int Ready(BattleGrid grid,ArmyData army)=>army.Members.Count(u=>!u.IsHero&&grid.TryFindPosition(u,out int r,out int c)&&grid.AllUnits().Any(t=>t.Owner!=u.Owner&&CanHurt(u,t)&&grid.TryFindPosition(t,out int tr,out int tc)&&BattleGrid.IsInRange(r,c,tr,tc,u.Range)));
        private static string Layout(BattleGrid grid,ArmyData army)=>string.Join(";",army.Members.Select(u=>{grid.TryFindPosition(u,out int r,out int c);return u.Name+"@"+r+","+c;}));

        // Independent audit metric: BFS over empty cells to this actor's square attack radius.
        private static int Distance(BattleGrid grid,UnitData actor,int row,int col)
        {
            var q=new Queue<(int r,int c,int d)>();var seen=new HashSet<(int,int)>();q.Enqueue((row,col,0));seen.Add((row,col));
            int[] dr={-1,1,0,0},dc={0,0,-1,1};
            while(q.Count>0)
            {
                var v=q.Dequeue();
                foreach(UnitData target in grid.AllUnits())
                    if(target.Owner!=actor.Owner&&CanHurt(actor,target)&&grid.TryFindPosition(target,out int r,out int c)&&BattleGrid.IsInRange(v.r,v.c,r,c,actor.Range))return v.d;
                for(int i=0;i<4;i++)
                {int r=v.r+dr[i],c=v.c+dc[i];if(!BattleGrid.InBounds(r,c)||seen.Contains((r,c))||(grid.Get(r,c)!=null&&grid.Get(r,c)!=actor))continue;seen.Add((r,c));q.Enqueue((r,c,v.d+1));}
            }
            return 26;
        }

        private static bool CanHurt(UnitData actor,UnitData target)
            => ChallengeResult.ApplyAbilityModifiers(actor.Attack,actor,target,AbilityMagnitudes.Default)>0;

        private static bool SupportsEngagedAlly(BattleGrid grid,UnitData actor,ArmyData army)
        {
            foreach(UnitData ally in army.Members)
            {
                if(ally==actor||ally.IsHero||!grid.TryFindPosition(ally,out int r,out int c)||Distance(grid,ally,r,c)!=0)continue;
                if((actor.HasAbility(UnitAbilities.Heal)&&ally.TypeTags.Contains(UnitTypeTag.Bio))||actor.HasAbility(UnitAbilities.Drill))return true;
                if(actor.HasAbility(UnitAbilities.Bodyguard)&&grid.TryFindPosition(actor,out int ar,out int ac)&&BattleGrid.IsOrthogonallyAdjacent(ar,ac,r,c))return true;
            }
            return false;
        }
    }
}
