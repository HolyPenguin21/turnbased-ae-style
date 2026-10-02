using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // The plain-words state of a player's Attack operation for the data panel (no target
    // coordinates — the owner only wants to know what the attack army is doing). Read-only: it
    // peeks at the continuity state and never creates or advances it.
    public static class AttackIntentLabel
    {
        public const string Idle = "no operation (waits for a task)";

        public static string Describe(PlayerSetupData player)
        {
            MissionIntentState state = MissionIntentRegistry.Peek(player);
            if (state == null)
                return Idle;
            List<string> parts = state.All
                .Where(i => i != null && i.Attack != null)
                .OrderBy(i => i.CreatedTurn)
                .Select(Describe)
                .Distinct()
                .ToList();
            return parts.Count == 0 ? Idle : string.Join("; ", parts);
        }

        public static string Describe(MissionIntent intent)
        {
            AttackIntent attack = intent?.Attack;
            if (attack == null)
                return Idle;
            string state = Phase(attack);
            return intent.Status == IntentStatus.Suspended ? state + " (suspended)" : state;
        }

        private static string Phase(AttackIntent attack)
        {
            switch (attack.Phase)
            {
                case AttackMissionPhase.Gather:
                    return attack.Preparation ? "gathering the strike force" : "gathering support";
                case AttackMissionPhase.GatherReturn:
                case AttackMissionPhase.SupportReturn:
                case AttackMissionPhase.RecoveryReturn:
                    return "returning to base";
                case AttackMissionPhase.Reinforcement:
                    return "reinforcing the main army";
                case AttackMissionPhase.AirSupport:
                    return "air support";
                default:
                    return attack.AssaultStarted ? "marching to the target" : "ready to march";
            }
        }
    }
}
