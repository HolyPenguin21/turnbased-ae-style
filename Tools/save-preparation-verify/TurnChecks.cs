using System;
using Game.Turns;
static class TurnChecks
{
    static GameTurnState Valid() => new GameTurnState { Round=4,PlayerOrder=new[]{"ai","human"},CurrentPlayerIndex=1,
        Completed=new[]{new PlayerTurnCount{PlayerId="ai",CompletedTurns=4},new PlayerTurnCount{PlayerId="human",CompletedTurns=3}},
        GreetingsShown=new[]{"human"},PendingMessages=Array.Empty<PlayerTurnMessages>() };
    static void Reject(Action<GameTurnState> edit) {var s=Valid();edit(s);try{s.Validate(new[]{"ai","human"},"human");}catch(ArgumentException){return;}throw new Exception("Invalid turn accepted");}
    static int Main() {Valid().Validate(new[]{"ai","human"},"human");Reject(s=>s.PlayerOrder=new[]{"human","human"});Reject(s=>s.PlayerOrder[0]="unknown");Reject(s=>s.CurrentPlayerIndex=0);Reject(s=>s.Completed[0].CompletedTurns=-1);Reject(s=>s.Completed[0].PlayerId="human");Reject(s=>s.GreetingsShown=null);Reject(s=>s.Round=0);Console.WriteLine("PASS: saved turn identity/history validation");return 0;}
}
