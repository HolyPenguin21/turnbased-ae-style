using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    public enum AirStrikePolicyKind { Standard, RaidSupport }

    // A transient policy supplied by the caller that owns the mission. Standard preserves the
    // ordinary endpoint strike exactly; RaidSupport pins one physical target and a survivor floor.
    public readonly struct AirStrikePolicy
    {
        public readonly AirStrikePolicyKind Kind;
        public readonly int? ExactTargetArmyId;
        public readonly int MinimumSurvivors;

        public AirStrikePolicy(AirStrikePolicyKind kind, int? exactTargetArmyId = null,
            int minimumSurvivors = 0)
        {
            Kind = kind;
            ExactTargetArmyId = exactTargetArmyId;
            MinimumSurvivors = Mathf.Max(0, minimumSurvivors);
        }

        public static AirStrikePolicy Standard => new AirStrikePolicy(AirStrikePolicyKind.Standard);
        public static AirStrikePolicy RaidSupport(int targetArmyId) =>
            new AirStrikePolicy(AirStrikePolicyKind.RaidSupport, targetArmyId, 1);
    }

    // Map/UI adapter for aviation combat. Ordinary ground contact and aviation share
    // ArmyController.MoveAlong's per-step resolution; AA and air strikes remain here.
    public class AviationCombatPresenter : MonoBehaviour
    {
        [SerializeField] private BattleAttackPopupUI attackPopup;
        [SerializeField] private AaChoicePopupUI aaChoicePopup;
        [SerializeField] private HexSelectionController hexSelection;

        public IEnumerator ResolveStep(ArmyData mover, HexCoord hex, ArmyController.StepResolutionOutcome outcome)
        {
            if (mover == null)
                yield break;

            if (AviationRules.IsAirArmy(mover))
                yield return ResolveAirArmyStep(mover, hex, outcome);
            else if (mover.Members.Exists(member => AntiAirRules.TryGetRadius(member, out _)))
                yield return ResolveGroundAaStep(mover, hex);
        }

        private IEnumerator ResolveAirArmyStep(ArmyData airArmy, HexCoord hex, ArmyController.StepResolutionOutcome outcome)
        {
            foreach (AaReaction reaction in AntiAirRules.CollectEntryReactions(airArmy, hex))
            {
                if (airArmy.Members.Count == 0)
                    break;
                yield return RunAaReaction(reaction);
            }

            if (airArmy.Members.Count == 0)
            {
                airArmy.PendingAirStrikePolicy = null;
                outcome.StopMovement = true;
                hexSelection?.DeleteArmyIfEmptied(airArmy);
                yield break;
            }

            // Passing through an occupied hex only exposes the air army to entry AA.
            // Air strikes are endpoint actions, not automatic attacks along the route.
            if (outcome == null || !outcome.IsTerminalStep)
                yield break;

            // The per-step hex is authoritative: ArmyData.Hex still points to the origin
            // while a multi-hex movement order is in progress.
            var result = new AirStrikeResult();
            AirStrikePolicy policy = airArmy.PendingAirStrikePolicy ?? AirStrikePolicy.Standard;
            yield return ResolveAirStrikeAtCurrentHex(airArmy, hex, policy, result);
            airArmy.PendingAirStrikePolicy = null;
            airArmy.LastAirStrikeHex = hex;
            airArmy.LastAirStrikeAttacked = result.Attacked;
        }

        public sealed class AirStrikeResult
        {
            public bool Attacked;
            public int? AttackedArmyId;
            public int DefenderCountBefore;
            public int DefenderCountAfter;
            public int DamageDealt;
        }

        // Shared endpoint strike for movement and stationary repeat strikes. Publish the
        // final defenders once after the entire aircraft group has finished resolving.
        public IEnumerator ResolveAirStrikeAtCurrentHex(ArmyData airArmy, HexCoord hex, AirStrikeResult result = null)
            => ResolveAirStrikeAtCurrentHex(airArmy, hex, AirStrikePolicy.Standard, result);

        public IEnumerator ResolveAirStrikeAtCurrentHex(ArmyData airArmy, HexCoord hex,
            AirStrikePolicy policy, AirStrikeResult result = null)
        {
            if (airArmy == null || airArmy.Members.Count == 0)
                yield break;
            List<ArmyData> targets = FindAirStrikeTargetsAt(hex, airArmy.Owner,
                policy.ExactTargetArmyId);
            if (targets.Count == 0)
                yield break;

            if (result != null)
                result.DefenderCountBefore = targets.Sum(a => a.Members.Count);
            yield return RunAirStrike(airArmy, targets, policy, result);
            if (result != null)
                result.DefenderCountAfter = targets.Sum(a => a.Members.Count);
            VisionSystem.NotifyContentChanged(hex);
        }

        private IEnumerator ResolveGroundAaStep(ArmyData groundArmy, HexCoord hex)
        {
            foreach (AaReaction reaction in AntiAirRules.CollectGroundOpportunities(groundArmy, hex))
                yield return RunAaReaction(reaction);
        }

        private IEnumerator RunAaReaction(AaReaction reaction)
        {
            ArmyData airArmy = reaction.AirArmy;
            if (airArmy.Members.Count == 0)
                yield break;

            bool attack = true;
            if (reaction.AaArmy.Owner != null && reaction.AaArmy.Owner.IsHuman && aaChoicePopup != null)
            {
                bool decided = false;
                aaChoicePopup.Show(reaction.AaUnit, airArmy,
                    onAttack: () => { attack = true; decided = true; },
                    onSkip: () => { attack = false; decided = true; });
                yield return new WaitUntil(() => decided);
            }

            AntiAirState.RecordPrompted(reaction.AaUnit, airArmy.Id, attack);
            if (!attack || attackPopup == null)
                yield break;

            UnitData target = PickRandomSurvivor(airArmy);
            if (target == null)
                yield break;

            UnitData aaHero = reaction.AaArmy.Members.Find(unit => unit.IsHero);
            bool resolved = false;
            attackPopup.Begin(reaction.AaUnit, aaHero, target, null, null, null,
                onResolved: (damage, died) =>
                {
                    resolved = true;
                    if (died)
                    {
                        airArmy.Members.Remove(target);
                        Game.Map.StealthSystem.OnUnitRemoved(target);
                    }
                },
                attackerPoolSize: reaction.AaUnit.Attack * 2);
            yield return new WaitUntil(() => resolved);
            hexSelection?.RestackArmiesOn(airArmy.Hex, null);

            // An AA hit on a stationary wing changes HP/roster without an Unregister.
            // During flight CurrentHex has advanced, but ArmyRegistry still indexes the
            // origin until the order completes. MoveArmy will publish both final hexes;
            // taking a snapshot before that atomic relocation would record false content.
            if (airArmy.Controller == null || !airArmy.Controller.IsMoving)
                VisionSystem.NotifyContentChanged(airArmy.Hex);
        }

        private IEnumerator RunAirStrike(ArmyData airArmy, List<ArmyData> targetArmies,
            AirStrikePolicy policy, AirStrikeResult result = null)
        {
            foreach (UnitData aircraft in airArmy.Members.ToList())
            {
                if (aircraft.HasAirAttackedThisTurn || airArmy.Members.Count == 0)
                    continue;

                if (targetArmies.Sum(a => a.Members.Count) <= policy.MinimumSurvivors)
                    break;

                List<(UnitData unit, ArmyData army)> pool = CollectStrikeTargets(targetArmies, airArmy.Owner);
                if (pool.Count == 0)
                    break;

                (UnitData target, ArmyData targetArmy) = pool[Random.Range(0, pool.Count)];
                aircraft.HasAirAttackedThisTurn = true;
                Game.Map.StealthSystem.ExitStealth(target);
                if (result != null)
                {
                    result.Attacked = true;
                    result.AttackedArmyId = targetArmy.Id;
                }

                UnitData defenderHero = target.IsHero ? target : targetArmy.Members.Find(unit => unit.IsHero);
                int? defenderPoolOverride = target.IsHero ? target.FateMax : (int?)null;

                bool resolved = false;
                attackPopup.Begin(aircraft, null, target, defenderHero, null, null,
                    onResolved: (damage, died) =>
                    {
                        resolved = true;
                        if (result != null)
                            result.DamageDealt += Mathf.Max(0, damage);
                        if (died)
                        {
                            targetArmy.Members.Remove(target);
                            Game.Map.StealthSystem.OnUnitRemoved(target);
                        }
                    },
                    defenderPoolSize: defenderPoolOverride);
                yield return new WaitUntil(() => resolved);

                hexSelection?.RestackArmiesOn(targetArmy.Hex, null);
                if (targetArmy.Members.Count == 0)
                    hexSelection?.DeleteArmyIfEmptied(targetArmy);
            }
        }

        private static List<(UnitData, ArmyData)> CollectStrikeTargets(List<ArmyData> targetArmies, PlayerSetupData striker)
        {
            var pool = new List<(UnitData, ArmyData)>();
            foreach (ArmyData army in targetArmies)
                if (army.Members.Count > 0)
                    foreach (UnitData unit in army.Members)
                        if (!Game.Map.StealthSystem.IsHiddenFrom(unit, striker))
                            pool.Add((unit, army));
            return pool;
        }

        // This query is shared with the move-arrow preview; event guards and aircraft
        // stored in an airfield are not strike targets.
        public static List<ArmyData> FindAirStrikeTargetsAt(HexCoord hex, PlayerSetupData owner)
            => FindAirStrikeTargetsAt(hex, owner, null);

        public static List<ArmyData> FindAirStrikeTargetsAt(HexCoord hex, PlayerSetupData owner,
            int? exactTargetArmyId)
        {
            var result = new List<ArmyData>();
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner == owner || army.Owner == null || army.IsPrison || army.Members.Count == 0)
                    continue;
                if (exactTargetArmyId.HasValue && army.Id != exactTargetArmyId.Value)
                    continue;
                if (AviationRules.IsAirfield(army))
                    continue;
                if (HexEventRegistry.IsEventGuardArmy(hex, army))
                    continue;
                if (!Game.Map.StealthSystem.HasAnyTargetableMember(army, owner))
                    continue;
                result.Add(army);
            }
            return result;
        }

        private static UnitData PickRandomSurvivor(ArmyData airArmy)
        {
            return airArmy.Members.Count > 0 ? airArmy.Members[Random.Range(0, airArmy.Members.Count)] : null;
        }
    }
}
