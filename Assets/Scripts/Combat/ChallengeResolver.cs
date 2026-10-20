using UnityEngine;

namespace Game.Combat
{
    // The base tokens mechanic underlying every "Challenge" in the original game — combat,
    // retreat, observation, and so on (see the manual's "Basic Challenge Mechanics"). Six-sided
    // tokens, half the faces blank (miss) and half marked (hit); success count vs. success count.
    // Every specific challenge type hands this its own attacker/defender tokens-pool sizes — this
    // only knows how to roll and score them, same split as Game.Turns.TurnOrderResolver's own
    // RollTokens for the unrelated initiative tokens-off.
    public static class ChallengeResolver
    {
        public static ChallengeResult Resolve(int attackerTokenCount, int defenderTokenCount)
        {
            return new ChallengeResult(RollTokens(attackerTokenCount), RollTokens(defenderTokenCount));
        }

        // Exposed on its own — not every roll is an attacker/defender pair (see
        // TurnOrderResolver's initiative tokens-off, which just needs one player's pool at a
        // time) — same 50/50-per-die mechanic either way.
        public static bool[] RollTokens(int count)
        {
            count = Mathf.Max(0, count);
            var tokens = new bool[count];
            for (int i = 0; i < count; i++)
                tokens[i] = Random.value < 0.5f;
            return tokens;
        }
    }
}
