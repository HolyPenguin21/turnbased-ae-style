using System.Collections.Generic;

namespace Game.Combat
{
    public static partial class WorthIt
    {
        // The forecast has no grid, but the battlefield has one hard capacity: a side's front row is
        // BattleGrid.Columns cells wide. Only melee bodies (NeedsApproach) must stand in it to fight;
        // a melee body beyond the fifth waits in the back row until a front cell is freed, and then
        // spends a turn stepping up. Playing every body from round one made a 6-7 body melee side look
        // far stronger than on the grid (GridVsForecastCalibration, 2026-11-18: 7 melee vs 5 ranged,
        // real 0.46, forecast 0.92). Ranged bodies and rosters of unknown range (-1) are unaffected.
        private static class MeleeReserve
        {
            internal const byte Active = 0, Reserve = 1, SteppedUp = 2;

            // Marks the melee bodies beyond the front row (list order, which is canonical).
            internal static void Deploy(List<BattleUnit> side, ref byte[] stance)
            {
                if (stance.Length < side.Count) stance = new byte[System.Math.Max(side.Count, 16)];
                else System.Array.Clear(stance, 0, stance.Length);
                int melee = 0;
                for (int i = 0; i < side.Count; i++)
                    if (!side[i].IsHero && side[i].Hp > 0f && side[i].NeedsApproach
                        && ++melee > BattleGrid.Columns)
                        stance[i] = Reserve;
            }

            // Round start: a reserve body takes each front cell a fallen melee body left free. It acts
            // from the next round (this one is its step up).
            internal static void StepUp(List<BattleUnit> side, byte[] stance)
            {
                int front = 0, reserve = 0;
                for (int i = 0; i < side.Count && i < stance.Length; i++)
                {
                    if (stance[i] == SteppedUp) stance[i] = Active;
                    if (side[i].IsHero || side[i].Hp <= 0f || !side[i].NeedsApproach) continue;
                    if (stance[i] == Reserve) reserve++; else front++;
                }
                if (reserve == 0) return;
                for (int i = 0; i < side.Count && i < stance.Length && front < BattleGrid.Columns; i++)
                    if (stance[i] == Reserve && side[i].Hp > 0f)
                    {
                        stance[i] = SteppedUp;
                        front++;
                    }
            }

            internal static bool Acts(byte[] stance, int index) =>
                index >= stance.Length || stance[index] == Active;
        }
    }
}
