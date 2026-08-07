namespace Game.Combat
{
    // One resolved Challenge (see ChallengeResolver) — mirrors Game.Turns.DiceRollResult's
    // bool-array-of-hits shape so a future battle UI can reuse the same per-die "1"/"X" display
    // convention already established for turn-order dice (see DiceRowUI).
    public class ChallengeResult
    {
        public readonly bool[] AttackerDice;
        public readonly bool[] DefenderDice;

        public ChallengeResult(bool[] attackerDice, bool[] defenderDice)
        {
            AttackerDice = attackerDice;
            DefenderDice = defenderDice;
        }

        public int AttackerSuccesses => CountHits(AttackerDice);
        public int DefenderSuccesses => CountHits(DefenderDice);

        // The manual: "the number of success rolls for the defender is subtracted from the
        // number of success rolls for the attacker and the damage if any is the difference
        // between them. A zero or negative result means no damage is done."
        public int Damage => System.Math.Max(0, AttackerSuccesses - DefenderSuccesses);

        private static int CountHits(bool[] dice)
        {
            int hits = 0;
            foreach (bool hit in dice)
                if (hit) hits++;
            return hits;
        }
    }
}
