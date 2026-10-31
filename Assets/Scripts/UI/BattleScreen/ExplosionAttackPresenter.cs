using System;
using System.Collections;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.UI
{
    // Ground combat and aviation use the same sequential challenge presentation. No nested
    // attacks, extra battle screen, alternate tokens roller or per-victim equipment consumption.
    public static class ExplosionAttackPresenter
    {
        public static IEnumerator Run(ExplosionAttack attack, BattleAttackPopupUI popup,
            HexSelectionController selection = null, BattleEngine battle = null,
            bool fastResolve = false, bool tacticsCritical = false,
            Action<ExplosionAttack.Target, BattleAttackApplication> onHit = null, HexMap map = null,
            Func<ExplosionAttack.Target, BattleAttackApplication, IEnumerator> presentHit = null)
        {
            int index = 0;
            AbilityMagnitudes magnitudes = battle != null ? battle.Magnitudes
                : popup != null ? popup.Magnitudes : AbilityMagnitudes.Default;
            try
            {
                foreach (ExplosionAttack.Target target in attack.Targets)
                {
                    index++;
                    if (!target.IsAlive) continue;
                    UnitData defender = target.Combatant();
                    UnitData defenderHero = target.IsStructure ? defender : target.Hero;
                    ArmyData attackerArmy = attack.LauncherArmy;
                    UnitData attackerHero = battle == null || defender.Owner == attack.Actor.Owner ? null
                        : attackerArmy?.Members.Find(u => u.IsHero && u.HitPointsCurrent > 0);
                    var defense = battle != null && target.Unit != null
                        ? battle.GetDefenseBreakdown(target.Unit, map) : default;
                    bool resolved = false;
                    BattleAttackApplication resolvedApplication = null;
                    void Apply(BattleChallengeRollResult roll)
                    {
                        if (resolved) return;
                        resolved = true;
                        var application = attack.Resolve(target, defender, roll,
                            magnitudes,
                            attackerHero, defenderHero, battle);
                        resolvedApplication = application;
                        onHit?.Invoke(target, application);
                        resolved = true;
                    }
                    int defensePool = (defender.IsHero ? defender.FateMax : defender.Defense) + defense.TotalBonus;
                    using var challenge = new BattleChallengeSession(BattleChallengeMode.GroundCombat,
                        attack.Actor, defender, ExplosionAttack.AttackTokens, defensePool,
                        attackerHero?.Fate ?? 0, defenderHero?.Fate ?? 0, magnitudes,
                        rollTokens: battle != null ? (Func<int, bool[]>)battle.RollChallengeTokens
                            : n => BattleSimulationKernel.RollTokens(n, AviationCombatPresenter.HeadlessRng),
                        tacticsCritical: tacticsCritical);
                    if (popup != null)
                    {
                        popup.Begin(attack.Actor, attackerHero, defender, defenderHero, null, null, Apply,
                            attackerPoolSize: ExplosionAttack.AttackTokens, defenderPoolSize: defensePool,
                            automateHumanSides: fastResolve, tacticsCritical: tacticsCritical,
                            challengeTitle: $"EXPLOSION ({index}/{attack.Targets.Count})", session: challenge);
                        yield return new WaitUntil(() => resolved);
                    }
                    else
                    {
                        challenge.Roll();
                        challenge.ResolveAiDecisions();
                        challenge.TryAccept(out _);
                        Apply(challenge.ToRollResult());
                    }
                    if (presentHit != null) yield return presentHit(target, resolvedApplication);
                }
            }
            finally { attack.Complete(battle, selection); }
        }
    }
}
