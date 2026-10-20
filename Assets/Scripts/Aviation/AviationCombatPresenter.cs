using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    public enum AirStrikePolicyKind { Standard, RaidSupport, DefenceSupport, Transit }

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

        public bool AllowsStrike => Kind != AirStrikePolicyKind.Transit;
        public static AirStrikePolicy Transit => new AirStrikePolicy(AirStrikePolicyKind.Transit);
        public static AirStrikePolicy Standard => new AirStrikePolicy(AirStrikePolicyKind.Standard);
        public static AirStrikePolicy RaidSupport(int targetArmyId) =>
            new AirStrikePolicy(AirStrikePolicyKind.RaidSupport, targetArmyId, 1);
        // ActiveDefence: the one threatening army the defence answers, struck to destruction.
        public static AirStrikePolicy DefenceSupport(int targetArmyId) =>
            new AirStrikePolicy(AirStrikePolicyKind.DefenceSupport, targetArmyId, 0);
    }

    // Map/UI adapter for aviation combat. Ordinary ground contact and aviation share
    // ArmyController.MoveAlong's per-step resolution; AA and air strikes remain here.
    public class AviationCombatPresenter : MonoBehaviour
    {
        public static event System.Action<ArmyData> OperationCompleted;
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

            // Airborne AA carriers use the same reaction rules as ground AA. A moving escort
            // can intercept a stationary enemy wing; the per-pair reaction state prevents repeats.
            if (airArmy.Members.Exists(member => AntiAirRules.TryGetRadius(member, out _)))
                yield return ResolveGroundAaStep(airArmy, hex);

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

        // Tokens source of the headless strike path only (never used while a popup exists).
        public static System.Random HeadlessRng = new System.Random();

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
        public IEnumerator ResolveAirStrikeAtCurrentHex(ArmyData airArmy, HexCoord hex,
            AirStrikePolicy policy, AirStrikeResult result = null)
        {
            if (!policy.AllowsStrike || airArmy == null || airArmy.Members.Count == 0)
                yield break;
            List<ArmyData> targets = FindAirStrikeTargetsAt(hex, airArmy.Owner,
                policy.ExactTargetArmyId);
            if (targets.Count == 0 && !CanExplosionStrikeAt(airArmy, hex))
                yield break;

            if (result != null)
                result.DefenderCountBefore = targets.Sum(a => a.Members.Count);
            // Reuse the resolved strike result; selecting targets alone is not a completed attack.
            AirStrikeResult operationResult = result ?? (OperationCompleted != null ? new AirStrikeResult() : null);
            yield return RunAirStrike(airArmy, hex, targets, policy, operationResult);
            if (result != null)
                result.DefenderCountAfter = targets.Sum(a => a.Members.Count);
            VisionSystem.NotifyContentChanged(hex);
            if (operationResult != null && operationResult.Attacked) OperationCompleted?.Invoke(airArmy);
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

            if (reaction.AaUnit.Owner != null && !reaction.AaUnit.Owner.IsHuman
                && ExplosionAttack.TargetLimit(reaction.AaUnit.Abilities) > 0
                && ExplosionAttack.ExpectedNetValue(reaction.AaUnit,
                    ExplosionAttack.KnownTargets(airArmy.Hex, reaction.AaUnit.Owner, airArmy)) <= 0)
                attack = false;
            AntiAirState.RecordPrompted(reaction.AaUnit, airArmy.Id, attack);
            if (!attack || attackPopup == null)
                yield break;

            if (ExplosionAttack.TargetLimit(reaction.AaUnit.Abilities) > 0)
            {
                var explosion = new ExplosionAttack(reaction.AaUnit, airArmy.Hex, HeadlessRng, airArmy);
                yield return ExplosionAttackPresenter.Run(explosion, attackPopup, hexSelection,
                    tacticsCritical: TacticBattleContext.IsCritical(airArmy.Hex, reaction.AaArmy, airArmy));
                yield break;
            }

            UnitData target = PickRandomSurvivor(airArmy);
            if (target == null)
                yield break;

            UnitData aaHero = reaction.AaArmy.Members.Find(unit => unit.IsHero);
            bool resolved = false;
            attackPopup.Begin(reaction.AaUnit, aaHero, target, null, null, null,
                onResolved: roll =>
                {
                    BattleAttackApplication application = BattleEngine.ResolveStandaloneAttack(
                        reaction.AaUnit, target, roll,
                        attackPopup != null ? attackPopup.Magnitudes : AbilityMagnitudes.Default,
                        aaHero, null);
                    resolved = true;
                    if (application.DefenderDied)
                    {
                        airArmy.Members.Remove(target);
                        Game.Map.StealthSystem.OnUnitRemoved(target);
                    }
                },
                attackerPoolSize: reaction.AaUnit.Attack * 2,
                tacticsCritical: TacticBattleContext.IsCritical(reaction.AaArmy.Hex, reaction.AaArmy, airArmy));
            yield return new WaitUntil(() => resolved);
            hexSelection?.RestackArmiesOn(airArmy.Hex, null);
            hexSelection?.DeleteArmyIfEmptied(reaction.AaArmy);

            // An AA hit on a stationary wing changes HP/roster without an Unregister.
            // During flight CurrentHex has advanced, but ArmyRegistry still indexes the
            // origin until the order completes. MoveArmy will publish both final hexes;
            // taking a snapshot before that atomic relocation would record false content.
            if (airArmy.Controller == null || !airArmy.Controller.IsMoving)
                VisionSystem.NotifyContentChanged(airArmy.Hex);
        }

        private IEnumerator RunAirStrike(ArmyData airArmy, HexCoord hex, List<ArmyData> targetArmies,
            AirStrikePolicy policy, AirStrikeResult result = null)
        {
            foreach (UnitData aircraft in airArmy.Members.ToList())
            {
                if (aircraft.HitPointsCurrent <= 0 || !airArmy.Members.Contains(aircraft)
                    || aircraft.HasAirAttackedThisTurn || airArmy.Members.Count == 0)
                    continue;

                if (ExplosionAttack.TargetLimit(aircraft.Abilities) > 0)
                {
                    // Random area damage cannot guarantee the survivor floor of a capture raid.
                    if (policy.MinimumSurvivors > 0 || !CanExplosionStrikeAt(airArmy, hex)) continue;
                    if (airArmy.Owner != null && !airArmy.Owner.IsHuman
                        && ExplosionAttack.ExpectedNetValue(aircraft, ExplosionAttack.KnownTargets(hex, airArmy.Owner, airArmy)) <= 0) continue;
                    aircraft.HasAirAttackedThisTurn = true;
                    var explosion = new ExplosionAttack(aircraft, hex, HeadlessRng, airArmy);
                    yield return ExplosionAttackPresenter.Run(explosion, attackPopup, hexSelection,
                        tacticsCritical: TacticBattleContext.IsCritical(hex, airArmy, targetArmies.FirstOrDefault()),
                        onHit: (target, application) =>
                        {
                            if (result == null) return;
                            result.Attacked = true;
                            if (target.Army?.Owner != airArmy.Owner && target.Army != null)
                                result.AttackedArmyId = target.Army.Id;
                            result.DamageDealt += Mathf.Max(0, application.Damage);
                        });
                    continue;
                }

                if (targetArmies.Sum(a => a.Members.Count) <= policy.MinimumSurvivors)
                    break;

                List<(UnitData unit, ArmyData army)> pool = CollectStrikeTargets(targetArmies, airArmy.Owner);
                if (pool.Count == 0)
                    break;

                (UnitData target, ArmyData targetArmy) = pool[attackPopup != null
                    ? Random.Range(0, pool.Count) : HeadlessRng.Next(pool.Count)];
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
                void Apply(BattleChallengeRollResult roll)
                {
                    BattleAttackApplication application = BattleEngine.ResolveStandaloneAttack(
                        aircraft, target, roll,
                        attackPopup != null ? attackPopup.Magnitudes : AbilityMagnitudes.Default,
                        null, defenderHero);
                    resolved = true;
                    if (result != null)
                        result.DamageDealt += Mathf.Max(0, application.Damage);
                    if (application.DefenderDied)
                    {
                        targetArmy.Members.Remove(target);
                        targetArmy.RefreshTransport();
                        Game.Map.StealthSystem.OnUnitRemoved(target);
                    }
                }
                if (attackPopup != null)
                {
                    attackPopup.Begin(aircraft, null, target, defenderHero, null, null,
                        onResolved: Apply, defenderPoolSize: defenderPoolOverride,
                        tacticsCritical: TacticBattleContext.IsCritical(targetArmy.Hex, airArmy, targetArmy));
                    yield return new WaitUntil(() => resolved);
                }
                else
                {
                    // Headless (no popup in the scene, e.g. automated tests): the same pools the
                    // popup rolls — aircraft Attack vs the target's Defense (a hero's FateMax) —
                    // through the same ResolveStandaloneAttack; tokens and the AI Fate duel from HeadlessRng.
                    int defensePool = defenderPoolOverride ?? target.Defense;
                    var challenge = new BattleChallengeSession(BattleChallengeMode.GroundCombat,
                        aircraft, target, aircraft.Attack, defensePool, 0, defenderHero?.Fate ?? 0,
                        AbilityMagnitudes.Default,
                        rollTokens: count => BattleSimulationKernel.RollTokens(count, HeadlessRng),
                        tacticsCritical: TacticBattleContext.IsCritical(targetArmy.Hex, airArmy, targetArmy));
                    challenge.Roll();
                    challenge.ResolveAiDecisions();
                    Apply(challenge.ToRollResult());
                }

                hexSelection?.RestackArmiesOn(targetArmy.Hex, null);
                if (targetArmy.Members.Count == 0)
                    hexSelection?.DeleteArmyIfEmptied(targetArmy);
            }
            hexSelection?.DeleteArmyIfEmptied(airArmy);
        }

        internal static List<(UnitData, ArmyData)> CollectStrikeTargets(List<ArmyData> targetArmies, PlayerSetupData striker)
        {
            var pool = new List<(UnitData, ArmyData)>();
            foreach (ArmyData army in targetArmies)
                if (army.Members.Count > 0)
                    foreach (UnitData unit in army.Members)
                        if (AviationRules.CanTargetFromAir(unit, army) && !Game.Map.StealthSystem.IsHiddenFrom(unit, striker))
                            pool.Add((unit, army));
            return pool;
        }

        public static bool CanExplosionStrikeAt(ArmyData airArmy, HexCoord hex)
        {
            if (airArmy == null || !airArmy.Members.Any(u => u.HitPointsCurrent > 0
                && !u.HasAirAttackedThisTurn && ExplosionAttack.TargetLimit(u.Abilities) > 0)) return false;
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building != null && building.Owner != airArmy.Owner && building.StructurePointsCurrent > 0) return true;
            return ArmyRegistry.AllAt(hex).Any(a => a.Owner != airArmy.Owner
                && a.Members.Any(u => AviationRules.CanTargetFromAir(u, a) && !StealthSystem.IsHiddenFrom(u, airArmy.Owner)));
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
                if (AviationRules.IsAirfield(army) || army.IsCarrierContainer)
                    continue;
                if (HexEventRegistry.IsEventGuardArmy(hex, army))
                    continue;
                if (!army.Members.Any(u => AviationRules.CanTargetFromAir(u, army)
                    && !Game.Map.StealthSystem.IsHiddenFrom(u, owner)))
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
