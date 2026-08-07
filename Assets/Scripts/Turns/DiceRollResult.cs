namespace Game.Turns
{
    // One roll of a 5-die pool — each die is an independent 50/50 coin flip. A hit scores 1
    // point; a miss shows as "X" and scores 0. Used for turn-order rolls now, likely other
    // dice-pool mechanics later (same "1d1 pool, count the hits" pattern from the original
    // game).
    public class DiceRollResult
    {
        public readonly bool[] Dice;

        public DiceRollResult(bool[] dice)
        {
            Dice = dice;
        }

        public int Score
        {
            get
            {
                int score = 0;
                foreach (bool hit in Dice)
                    if (hit) score++;
                return score;
            }
        }
    }
}
