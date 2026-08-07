using UnityEngine;

namespace Game.Combat
{
    // The base dice mechanic underlying every "Challenge" in the original game — combat,
    // retreat, observation, and so on (see the manual's "Basic Challenge Mechanics"). Six-sided
    // dice, half the faces blank (miss) and half marked (hit); success count vs. success count.
    // Every specific challenge type hands this its own attacker/defender dice-pool sizes — this
    // only knows how to roll and score them, same split as Game.Turns.TurnOrderResolver's own
    // RollDice for the unrelated initiative dice-off.
    public static class ChallengeResolver
    {
        public static ChallengeResult Resolve(int attackerDiceCount, int defenderDiceCount)
        {
            return new ChallengeResult(RollDice(attackerDiceCount), RollDice(defenderDiceCount));
        }

        // Exposed on its own — not every roll is an attacker/defender pair (see
        // TurnOrderResolver's initiative dice-off, which just needs one player's pool at a
        // time) — same 50/50-per-die mechanic either way.
        public static bool[] RollDice(int count)
        {
            count = Mathf.Max(0, count);
            var dice = new bool[count];
            for (int i = 0; i < count; i++)
                dice[i] = Random.value < 0.5f;
            return dice;
        }
    }
}
