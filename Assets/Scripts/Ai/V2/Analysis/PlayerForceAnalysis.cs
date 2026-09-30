using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // Read-only match diagnostics, independent of AI turn snapshots and fog memory.
    // No hand creation, card draws, registry mutations or cached strategic decisions.
    public readonly struct PlayerForceAnalysis
    {
        public readonly float DeployedPower, TotalAvailablePower, StrongestArmyPower, GroundArmyPotential;
        public readonly ArmyData StrongestArmy;
        public float ReadinessPercent => GroundArmyPotential > 0f
            ? 100f * StrongestArmyPower / GroundArmyPotential : 0f;
        public bool ForceReady => AttackObjectiveEvaluator.ForceReady(StrongestArmyPower, GroundArmyPotential);

        private PlayerForceAnalysis(float deployed, float total, ArmyData army, float armyPower, float potential)
        {
            DeployedPower = deployed;
            TotalAvailablePower = total;
            StrongestArmy = army;
            StrongestArmyPower = armyPower;
            GroundArmyPotential = potential;
        }

        public static PlayerForceAnalysis Calculate(PlayerSetupData player, IEnumerable<ArmyData> armies,
            IEnumerable<CardData> hand, IEnumerable<CardDefinition> deck)
        {
            List<ArmyData> own = (armies ?? Enumerable.Empty<ArmyData>())
                .Where(a => a != null && a.Owner == player && !a.IsPrison).ToList();
            List<UnitData> live = own.SelectMany(a => a.Members)
                .Where(u => u != null && !u.IsPrisoner).Distinct().ToList();
            List<CardData> handCards = hand?.ToList() ?? new List<CardData>();
            List<CardDefinition> deckCards = deck?.ToList() ?? new List<CardDefinition>();
            float deployed = live.Sum(AiPower.UnitPower);
            float total = AiPower.MilitaryPool(live, handCards, deckCards, groundOnly: false)
                .Sum(u => u.BasePower);
            float potential = AiPower.TotalMilitaryPotential(AiPower.MilitaryPool(live, handCards, deckCards));

            ArmyData strongest = null;
            float strongestPower = 0f;
            foreach (ArmyData army in own)
            {
                // Same field-force eligibility as SelfSnapshot.FistPower. A garrison,
                // stored aircraft, scout or solo hero is not the fist sent to a hostile base.
                if (!AiArmyRoles.IsStructuralGroundCombatActor(army))
                    continue;
                float power = AiPower.EffectiveArmyPower(army.Members);
                if (strongest == null || power > strongestPower
                    || power == strongestPower && army.Id < strongest.Id)
                {
                    strongest = army;
                    strongestPower = power;
                }
            }
            return new PlayerForceAnalysis(deployed, total, strongest, strongestPower, potential);
        }
    }
}
