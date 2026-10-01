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
        // Share of the whole additive force already played onto the map; the Attack mobilization
        // trigger reads exactly this pair (AttackObjectiveEvaluator.MobilizationOpen).
        public float DeployedPercent => TotalAvailablePower > 0f
            ? 100f * DeployedPower / TotalAvailablePower : 0f;
        public bool MobilizationOpen => AttackObjectiveEvaluator.MobilizationOpen(DeployedPower, TotalAvailablePower);
        // Mobilization start (B): the strongest stack the field bodies can already form
        // (WorldAnalysis.FieldStrikePotential — no lone scouts, aviation, heroes' own power or
        // garrison defence floor) against the strongest army the whole deck can form.
        public readonly float FieldStrikePotential;
        public float FieldStrikePercent => GroundArmyPotential > 0f
            ? 100f * FieldStrikePotential / GroundArmyPotential : 0f;
        public bool FieldStrikeReady =>
            AttackObjectiveEvaluator.FieldStrikeForceReady(FieldStrikePotential, GroundArmyPotential);

        private PlayerForceAnalysis(float deployed, float total, ArmyData army, float armyPower, float potential,
            float fieldStrike)
        {
            FieldStrikePotential = fieldStrike;
            DeployedPower = deployed;
            TotalAvailablePower = total;
            StrongestArmy = army;
            StrongestArmyPower = armyPower;
            GroundArmyPotential = potential;
        }

        public static PlayerForceAnalysis Calculate(PlayerSetupData player, IEnumerable<ArmyData> armies,
            IEnumerable<CardData> hand, IEnumerable<CardDefinition> deck)
        {
            List<ArmyData> own = OwnLive(player, armies, out List<UnitData> live);
            List<CardData> handCards = hand?.ToList() ?? new List<CardData>();
            List<CardDefinition> deckCards = deck?.ToList() ?? new List<CardDefinition>();
            Additive(live, handCards, deckCards, out float deployed, out float total);
            float potential = AiPower.NestedPotentials(live, handCards, deckCards).Total;

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
            float fieldStrike = WorldAnalysis.FieldStrikePotential(player, own, total);
            return new PlayerForceAnalysis(deployed, total, strongest, strongestPower, potential, fieldStrike);
        }

        // The additive pair alone (deployed / available) — the Attack mobilization trigger's
        // input on SelfSnapshot, without the strongest-army and ground-peak work of Calculate.
        public static void AdditivePower(PlayerSetupData player, IEnumerable<ArmyData> armies,
            IEnumerable<CardData> hand, IEnumerable<CardDefinition> deck,
            out float deployed, out float available)
        {
            OwnLive(player, armies, out List<UnitData> live);
            Additive(live, hand?.ToList() ?? new List<CardData>(),
                deck?.ToList() ?? new List<CardDefinition>(), out deployed, out available);
        }

        private static List<ArmyData> OwnLive(PlayerSetupData player, IEnumerable<ArmyData> armies,
            out List<UnitData> live)
        {
            List<ArmyData> own = (armies ?? Enumerable.Empty<ArmyData>())
                .Where(a => a != null && a.Owner == player && !a.IsPrison).ToList();
            live = own.SelectMany(a => a.Members)
                .Where(u => u != null && !u.IsPrisoner).Distinct().ToList();
            return own;
        }

        // Ground force only (user decision 2026-09-30): aviation never joins the ground stack the
        // Attack threshold is measured on, so it neither delays nor opens the mobilization share.
        private static void Additive(List<UnitData> live, List<CardData> hand,
            List<CardDefinition> deck, out float deployed, out float available)
        {
            deployed = live.Where(u => !u.IsAviation).Sum(AiPower.UnitPower);
            available = AiPower.MilitaryPool(live, hand, deck, groundOnly: true)
                .Sum(u => u.BasePower);
        }
    }
}
