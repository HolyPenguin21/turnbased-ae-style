using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Aviation
{
    public class AviationCombatResult
    {
        public bool Attacked;
        public int? AttackedArmyId;
        public int DefenderCountBefore;
        public int DefenderCountAfter;
        public int DamageDealt;
    }

    // The iterator owns an aviation operation. Presentation answers requests; resuming the
    // iterator applies their outcomes. No popup, coroutine, animation or UI callback owns rules.
    public sealed class AviationCombatOperation
    {
        public event Action<ArmyData> StrikeCompleted;
        public abstract class Request { }
        public sealed class AaDecisionRequest : Request
        {
            public AaReaction Reaction { get; }
            public bool SuggestedFire { get; }
            public bool IsCompleted { get; private set; }
            public bool Fire { get; private set; }
            public bool IsCancelled { get; private set; }
            internal AaDecisionRequest(AaReaction reaction, bool suggestedFire)
            { Reaction = reaction; SuggestedFire = suggestedFire; }
            public void Complete(bool fire)
            {
                if (IsCompleted || IsCancelled) return;
                Fire = fire; IsCompleted = true;
            }
            public void Cancel() { if (!IsCompleted) IsCancelled = true; }
        }
        public sealed class ChallengeRequest : Request, IDisposable
        {
            public UnitData Attacker { get; }
            public UnitData AttackerHero { get; }
            public UnitData Defender { get; }
            public UnitData DefenderHero { get; }
            public BattleChallengeSession Session { get; }
            public bool TacticsCritical { get; }
            public string Title { get; }
            public HexCoord Hex { get; }
            public bool IsCompleted { get; private set; }
            public BattleChallengeRollResult Roll { get; private set; }
            internal ChallengeRequest(UnitData attacker, UnitData attackerHero, UnitData defender,
                UnitData defenderHero, BattleChallengeSession session, bool tacticsCritical, string title, HexCoord hex)
            {
                Attacker = attacker; AttackerHero = attackerHero; Defender = defender;
                DefenderHero = defenderHero; Session = session; TacticsCritical = tacticsCritical;
                Title = title; Hex = hex;
            }
            public void Complete(BattleChallengeRollResult roll)
            {
                if (IsCompleted) return;
                if (!Session.IsAccepted && !Session.TryAccept(out _))
                    throw new InvalidOperationException("The aviation challenge still has pending decisions.");
                Roll = Session.ToRollResult();
                IsCompleted = true;
            }
            public void Dispose() => Session.Dispose();
        }

        private readonly System.Random _rng;
        private readonly Func<int, bool[]> _rollTokens;
        private readonly AbilityMagnitudes _magnitudes;
        public AviationCombatOperation(System.Random rng = null, Func<int, bool[]> rollTokens = null,
            AbilityMagnitudes? magnitudes = null)
        {
            _rng = rng ?? new System.Random();
            _rollTokens = rollTokens ?? (n => BattleSimulationKernel.RollTokens(n, _rng));
            _magnitudes = magnitudes ?? AbilityMagnitudes.Default;
        }
        private ChallengeRequest Challenge(UnitData attacker, UnitData attackerHero, UnitData defender,
            UnitData defenderHero, int attackerPool, int defenderPool, bool critical, string title, HexCoord hex)
            => new ChallengeRequest(attacker, attackerHero, defender, defenderHero,
                new BattleChallengeSession(BattleChallengeMode.GroundCombat, attacker, defender,
                    attackerPool, defenderPool, attackerHero?.Fate ?? 0, defenderHero?.Fate ?? 0,
                    _magnitudes, rollTokens: _rollTokens, tacticsCritical: critical), critical, title, hex);

        private static void RequireCompleted(bool completed)
        {
            if (!completed) throw new InvalidOperationException("Answer the aviation request before advancing the operation.");
        }

        public IEnumerable<Request> ResolveStep(ArmyData mover, HexCoord hex, ArmyController.StepResolutionOutcome outcome)
        {
            if (mover == null) yield break;
            if (mover.Members.Count == 0)
            {
                mover.PendingAirStrikePolicy = null;
                if (outcome != null) outcome.StopMovement = true;
                ArmyActions.DeleteArmyIfEmptied(mover);
                yield break;
            }
            if (!AviationRules.IsAirArmy(mover))
            {
                if (mover.Members.Exists(member => AntiAirRules.TryGetRadius(member, out _)))
                    foreach (var request in ResolveGroundAaStep(mover, hex)) yield return request;
                yield break;
            }
            foreach (AaReaction reaction in AntiAirRules.CollectEntryReactions(mover, hex))
            {
                if (mover.Members.Count == 0) break;
                foreach (var request in ResolveAaReaction(reaction)) yield return request;
            }
            if (mover.Members.Count == 0)
            {
                mover.PendingAirStrikePolicy = null;
                if (outcome != null) outcome.StopMovement = true;
                ArmyActions.DeleteArmyIfEmptied(mover);
                yield break;
            }
            if (mover.Members.Exists(member => AntiAirRules.TryGetRadius(member, out _)))
                foreach (var request in ResolveGroundAaStep(mover, hex)) yield return request;
            if (outcome == null || !outcome.IsTerminalStep) yield break;
            var result = new AviationCombatResult();
            var policy = mover.PendingAirStrikePolicy ?? AirStrikePolicy.Standard;
            foreach (var request in ResolveAirStrikeAtCurrentHex(mover, hex, policy, result)) yield return request;
            mover.PendingAirStrikePolicy = null;
            mover.LastAirStrikeHex = hex;
            mover.LastAirStrikeAttacked = result.Attacked;
        }

        private IEnumerable<Request> ResolveGroundAaStep(ArmyData army, HexCoord hex)
        {
            foreach (var reaction in AntiAirRules.CollectGroundOpportunities(army, hex))
                foreach (var request in ResolveAaReaction(reaction)) yield return request;
        }
        public IEnumerable<Request> ResolveTurnStartAntiAir(PlayerSetupData owner)
        {
            foreach (var reaction in AntiAirRules.CollectTurnStartOpportunities(owner))
            {
                if (reaction.AirArmy.Members.Count == 0
                    || !AntiAirState.CanReact(reaction.AaUnit, reaction.AirArmy.Id)) continue;
                foreach (var request in ResolveAaReaction(reaction)) yield return request;
                if (reaction.AirArmy.Members.Count == 0
                    && (reaction.AirArmy.Controller == null || !reaction.AirArmy.Controller.IsMoving))
                    ArmyActions.DeleteArmyIfEmptied(reaction.AirArmy);
            }
        }

        public IEnumerable<Request> ResolveAaReaction(AaReaction reaction)
        {
            ArmyData airArmy = reaction.AirArmy;
            if (airArmy == null || airArmy.Members.Count == 0
                || !AntiAirState.CanReact(reaction.AaUnit, airArmy.Id)
                || !StealthSystem.HasAnyTargetableMember(airArmy, reaction.AaOwner)) yield break;
            HexCoord airHex = airArmy.Controller != null ? airArmy.Controller.CurrentHex : airArmy.Hex;
            bool fire = !(reaction.AaUnit.Owner != null && !reaction.AaUnit.Owner.IsHuman
                && ExplosionAttack.TargetLimit(reaction.AaUnit.Abilities) > 0
                && ExplosionAttack.ExpectedNetValue(reaction.AaUnit,
                    ExplosionAttack.KnownTargets(airHex, reaction.AaUnit.Owner, airArmy)) <= 0);
            var decision = new AaDecisionRequest(reaction, fire);
            yield return decision;
            RequireCompleted(decision.IsCompleted);
            AntiAirState.RecordPrompted(reaction.AaUnit, airArmy.Id, decision.Fire);
            if (!decision.Fire) yield break;
            bool critical = reaction.AaArmy != null
                ? TacticBattleContext.IsCritical(airHex, reaction.AaArmy, airArmy)
                : TacticBattleContext.IsCritical(reaction.AaHex, reaction.AaOwner, airArmy);
            if (ExplosionAttack.TargetLimit(reaction.AaUnit.Abilities) > 0)
            {
                foreach (var request in ResolveExplosion(new ExplosionAttack(reaction.AaUnit, airHex, _rng, airArmy),
                    critical, null)) yield return request;
                yield break;
            }
            var visible = StealthSystem.TargetableMembersFor(airArmy, reaction.AaOwner).ToList();
            if (visible.Count == 0) yield break;
            UnitData target = visible[_rng.Next(visible.Count)];
            StealthSystem.ExitStealth(target);
            UnitData hero = reaction.AaArmy?.Members.Find(unit => unit.IsHero);
            using var challenge = Challenge(reaction.AaUnit, hero, target, null, reaction.AaUnit.Attack * 2,
                target.IsHero ? target.FateMax : target.Defense, critical, "ANTI AIR ATTACK", airHex);
            yield return challenge;
            RequireCompleted(challenge.IsCompleted);
            using (VisionSystem.BeginMutation())
            {
                var application = BattleEngine.ResolveStandaloneAttack(reaction.AaUnit, target,
                    challenge.Roll, _magnitudes, hero, null);
                if (application.DefenderDied)
                {
                    ArmyActions.RemoveMember(airArmy, target);
                    if (airArmy.Controller == null || !airArmy.Controller.IsMoving)
                        ArmyActions.DeleteArmyIfEmptied(airArmy);
                }
                if (reaction.AaArmy != null) ArmyActions.DeleteArmyIfEmptied(reaction.AaArmy);
                VisionSystem.NotifyContentChanged(airArmy.Hex);
            }
        }

        public IEnumerable<Request> ResolveAirStrikeAtCurrentHex(ArmyData airArmy, HexCoord hex,
            AirStrikePolicy policy, AviationCombatResult result = null)
        {
            if (!policy.AllowsStrike || airArmy == null || airArmy.Members.Count == 0) yield break;
            var targets = FindAirStrikeTargetsAt(hex, airArmy.Owner, policy.ExactTargetArmyId);
            if (targets.Count == 0 && !CanExplosionStrikeAt(airArmy, hex)) yield break;
            result = result ?? new AviationCombatResult();
            result.DefenderCountBefore = targets.Sum(a => a.Members.Count);
            foreach (var aircraft in airArmy.Members.ToList())
            {
                if (aircraft.HitPointsCurrent <= 0 || !airArmy.Members.Contains(aircraft)
                    || aircraft.HasAirAttackedThisTurn || airArmy.Members.Count == 0) continue;
                if (ExplosionAttack.TargetLimit(aircraft.Abilities) > 0)
                {
                    if (policy.MinimumSurvivors > 0 || !CanExplosionStrikeAt(airArmy, hex)) continue;
                    if (airArmy.Owner != null && !airArmy.Owner.IsHuman
                        && ExplosionAttack.ExpectedNetValue(aircraft,
                            ExplosionAttack.KnownTargets(hex, airArmy.Owner, airArmy)) <= 0) continue;
                    aircraft.HasAirAttackedThisTurn = true;
                    foreach (var request in ResolveExplosion(new ExplosionAttack(aircraft, hex, _rng, airArmy),
                        TacticBattleContext.IsCritical(hex, airArmy, targets.FirstOrDefault()), result)) yield return request;
                    continue;
                }
                if (targets.Sum(a => a.Members.Count) <= policy.MinimumSurvivors) break;
                var pool = CollectStrikeTargets(targets, airArmy.Owner);
                if (pool.Count == 0) break;
                (UnitData target, ArmyData targetArmy) = pool[_rng.Next(pool.Count)];
                aircraft.HasAirAttackedThisTurn = true;
                StealthSystem.ExitStealth(target);
                UnitData hero = target.IsHero ? target : targetArmy.Members.Find(unit => unit.IsHero);
                using var challenge = Challenge(aircraft, null, target, hero, aircraft.Attack,
                    target.IsHero ? target.FateMax : target.Defense,
                    TacticBattleContext.IsCritical(targetArmy.Hex, airArmy, targetArmy), "AIR ATTACK", targetArmy.Hex);
                yield return challenge;
                RequireCompleted(challenge.IsCompleted);
                using (VisionSystem.BeginMutation())
                {
                    var application = BattleEngine.ResolveStandaloneAttack(aircraft, target, challenge.Roll,
                        _magnitudes, null, hero);
                    if (result != null)
                    {
                        result.Attacked = true;
                        result.AttackedArmyId = targetArmy.Id;
                        result.DamageDealt += Mathf.Max(0, application.Damage);
                    }
                    if (application.DefenderDied)
                    {
                        ArmyActions.RemoveMember(targetArmy, target);
                        ArmyActions.DeleteArmyIfEmptied(targetArmy);
                    }
                }
            }
            ArmyActions.DeleteArmyIfEmptied(airArmy);
            if (result != null) result.DefenderCountAfter = targets.Sum(a => a.Members.Count);
            VisionSystem.NotifyContentChanged(hex);
            if (result.Attacked) StrikeCompleted?.Invoke(airArmy);
        }

        private IEnumerable<Request> ResolveExplosion(ExplosionAttack attack, bool critical, AviationCombatResult result)
        {
            int index = 0;
            try
            {
                foreach (var target in attack.Targets)
                {
                    index++;
                    if (!target.IsAlive) continue;
                    var defender = target.Combatant();
                    var hero = target.IsStructure ? defender : target.Hero;
                    using var challenge = Challenge(attack.Actor, null, defender, hero, ExplosionAttack.AttackTokens,
                        defender.IsHero ? defender.FateMax : defender.Defense, critical,
                        $"EXPLOSION ({index}/{attack.Targets.Count})", attack.Hex);
                    yield return challenge;
                    RequireCompleted(challenge.IsCompleted);
                    var application = attack.Resolve(target, defender, challenge.Roll, _magnitudes, null, hero);
                    if (result != null)
                    {
                        result.Attacked = true;
                        if (target.Army != null && target.Army.Owner != attack.Launcher.Owner)
                            result.AttackedArmyId = target.Army.Id;
                        result.DamageDealt += Mathf.Max(0, application.Damage);
                    }
                }
            }
            finally { attack.Complete(); }
        }

        public static List<(UnitData, ArmyData)> CollectStrikeTargets(List<ArmyData> armies, PlayerSetupData striker)
        {
            var pool = new List<(UnitData, ArmyData)>();
            foreach (ArmyData army in armies)
                if (army.Members.Count > 0)
                    foreach (UnitData unit in army.Members)
                        if (AviationRules.CanTargetFromAir(unit, army) && !StealthSystem.IsHiddenFrom(unit, striker))
                            pool.Add((unit, army));
            return pool;
        }
        public static bool CanExplosionStrikeAt(ArmyData airArmy, HexCoord hex)
        {
            if (airArmy == null || !airArmy.Members.Any(u => u.HitPointsCurrent > 0
                && !u.HasAirAttackedThisTurn && ExplosionAttack.TargetLimit(u.Abilities) > 0)) return false;
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building != null && building.Owner != airArmy.Owner && building.StructurePointsCurrent > 0) return true;
            return ArmyRegistry.AllAt(hex).Any(a => a.Owner != airArmy.Owner && a.Members.Any(u =>
                AviationRules.CanTargetFromAir(u, a) && !StealthSystem.IsHiddenFrom(u, airArmy.Owner)));
        }
        public static List<ArmyData> FindAirStrikeTargetsAt(HexCoord hex, PlayerSetupData owner,
            int? exactTargetArmyId = null)
        {
            var result = new List<ArmyData>();
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
            {
                if (army.Owner == owner || army.Owner == null || army.IsPrison || army.Members.Count == 0) continue;
                if (exactTargetArmyId.HasValue && army.Id != exactTargetArmyId.Value) continue;
                if (AviationRules.IsAirfield(army) || army.IsCarrierContainer || HexEventRegistry.IsEventGuardArmy(hex, army)) continue;
                if (!army.Members.Any(u => AviationRules.CanTargetFromAir(u, army) && !StealthSystem.IsHiddenFrom(u, owner))) continue;
                result.Add(army);
            }
            return result;
        }
    }

    public static class AviationCombatHeadlessAdapter
    {
        public static void Resolve(AviationCombatOperation.ChallengeRequest challenge)
        {
            challenge.Session.Roll();
            challenge.Session.ResolveAiDecisions();
            challenge.Complete(challenge.Session.ToRollResult());
        }
        public static void Run(IEnumerable<AviationCombatOperation.Request> requests,
            Func<AviationCombatOperation.AaDecisionRequest, bool> decide = null)
        {
            foreach (var request in requests)
            {
                if (request is AviationCombatOperation.AaDecisionRequest aa) aa.Complete(decide?.Invoke(aa) ?? aa.SuggestedFire);
                else if (request is AviationCombatOperation.ChallengeRequest challenge) Resolve(challenge);
            }
        }
    }
}
