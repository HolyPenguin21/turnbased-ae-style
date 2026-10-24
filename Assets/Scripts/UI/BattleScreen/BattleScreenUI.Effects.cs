using System;
using System.Collections;
using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using Game.Units;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public partial class BattleScreenUI
    {
        private bool _isPresentingAttack;
        private Coroutine _attackPresentationRoutine, _explosionAttackRoutine;
        private RectTransform _attackEffectsRoot, _attackNumbersRoot;
        private int _attackPresentationGeneration;
        private readonly Dictionary<(UnitData Unit, string Sprite), float> _combatEffectStarts =
            new Dictionary<(UnitData Unit, string Sprite), float>();

        private BattleGridCellUI FindUnitCell(UnitData unit)
        {
            if (unit == null) return null;
            foreach (var cell in _cells) if (cell != null && cell.Unit == unit) return cell;
            return null;
        }
        private void RestoreCombatEffects(BattleGridCellUI cell, UnitData unit)
        {
            foreach (var pair in _combatEffectStarts)
                if (pair.Key.Unit == unit && Time.unscaledTime - pair.Value < BattleHealEffect.Duration)
                    BattleHealEffect.Show(cell.transform, pair.Value, pair.Key.Sprite);
        }
        private void ShowCombatEffect(UnitData unit, string sprite)
        {
            var cell = FindUnitCell(unit);
            if (cell == null) return;
            float started = Time.unscaledTime;
            _combatEffectStarts[(unit,sprite)] = started;
            BattleHealEffect.Show(cell.transform, started, sprite);
        }
        private void ShowIncomingHit(UnitData victim, int damage, bool wasHit, bool berserk)
        {
            var cell = FindUnitCell(victim);
            if (cell == null) return;
            cell.RefreshCombatStats();
            BattleHitEffect.Show(cell.RectTransform, damage, wasHit, _attackNumbersRoot);
            if (berserk) ShowCombatEffect(victim, BattleHealEffect.DrillSpritePath);
        }
        private void StartAttackPresentation(UnitData attacker, BattleAttackApplication application, Action onDone)
        {
            if (!HasInteractiveParticipant)
            { RefreshGrid(); onDone?.Invoke(); return; }
            _attackPresentationRoutine = StartCoroutine(PresentAttackThen(attacker, application, onDone));
        }
        private IEnumerator PresentAttackThen(UnitData attacker, BattleAttackApplication application, Action onDone)
        {
            int generation = _attackPresentationGeneration;
            _isPresentingAttack = true;
            if (passButton != null) passButton.interactable = false;
            try { yield return PlayAttackEffects(attacker, application); }
            finally
            {
                if (generation == _attackPresentationGeneration)
                { _isPresentingAttack = false; _attackPresentationRoutine = null; DestroyTrailLayer(); }
            }
            if (generation != _attackPresentationGeneration) yield break;
            RefreshGrid();
            onDone?.Invoke();
        }
        private IEnumerator PlayAttackEffects(UnitData attacker, BattleAttackApplication application)
        {
            if (!HasInteractiveParticipant || application == null || FindUnitCell(application.Victim) == null)
                yield break;
            // Model damage/removal already happened. Keep the original cells as visual victims.
            var parent = gridContainer != null ? gridContainer.parent as RectTransform : null;
            if (application.SecondaryHits.Count > 0 && parent != null)
            {
                var go = new GameObject("BattleHitEffects",typeof(RectTransform),typeof(LayoutElement));
                _attackEffectsRoot = (RectTransform)go.transform;
                BattleDamageFlipbook.Stretch(_attackEffectsRoot,parent);
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                var numbers = new GameObject("IncomingDamageLabels",typeof(RectTransform),typeof(LayoutElement));
                _attackNumbersRoot = (RectTransform)numbers.transform;
                BattleDamageFlipbook.Stretch(_attackNumbersRoot,_attackEffectsRoot);
                numbers.GetComponent<LayoutElement>().ignoreLayout = true;
            }
            ShowIncomingHit(application.Victim,application.Damage,application.Outcome.WasHit,application.BerserkTriggered);
            if (application.InterceptedBy != null) ShowCombatEffect(application.InterceptedBy,BattleHealEffect.BodyguardSpritePath);
            if (application.ShockTriggered) ShowCombatEffect(attacker,BattleHealEffect.ShockSpritePath);
            float beforeSecondary = 0f;
            if (application.SecondaryHits.Count > 0)
            {
                yield return new WaitForSecondsRealtime(.15f);
                var source = FindUnitCell(application.Victim);
                if (_attackEffectsRoot != null && source != null)
                {
                    bool splashSourcePulse = true;
                    foreach (var hit in application.SecondaryHits)
                    {
                        var target = FindUnitCell(hit.Victim);
                        if (target == null) continue;
                        bool fire = hit.Skill == UnitAbilities.Scorcher;
                        BattleDamageFlipbook.Show(_attackEffectsRoot,source.RectTransform,target.RectTransform,
                            fire,fire || splashSourcePulse);
                        if (!fire) splashSourcePulse = false;
                    }
                    _attackNumbersRoot.SetAsLastSibling();
                }
                yield return new WaitForSecondsRealtime(BattleSkillFlipbook.Arrival);
                beforeSecondary = .15f + BattleSkillFlipbook.Arrival;
                foreach (var hit in application.SecondaryHits)
                    ShowIncomingHit(hit.Victim,hit.Damage,true,hit.BerserkTriggered);
            }
            bool removedOverlay = application.DefenderDied
                && (application.InterceptedBy != null || application.BerserkTriggered);
            removedOverlay |= application.ShockTriggered && attacker != null && attacker.HitPointsCurrent <= 0;
            float hold = removedOverlay ? Mathf.Max(BattleHitEffect.Duration,BattleHealEffect.Duration-beforeSecondary)
                : BattleHitEffect.Duration;
            foreach (var hit in application.SecondaryHits)
                if (hit.Died && hit.BerserkTriggered) hold = Mathf.Max(hold,BattleHealEffect.Duration);
            yield return new WaitForSecondsRealtime(hold);
            DestroyTrailLayer();
        }
        private IEnumerator PresentExplosionHit(UnitData attacker, BattleAttackApplication application)
        {
            yield return PlayAttackEffects(attacker,application);
            _isPresentingAttack = false;
            RefreshGrid();
            _isPresentingAttack = true;
        }
        private void DestroyTrailLayer()
        {
            if (_attackEffectsRoot != null)
            { _attackEffectsRoot.gameObject.SetActive(false); Destroy(_attackEffectsRoot.gameObject); }
            _attackEffectsRoot = _attackNumbersRoot = null;
        }
        private void CancelAttackPresentation()
        {
            _attackPresentationGeneration++;
            if (_attackPresentationRoutine != null) StopCoroutine(_attackPresentationRoutine);
            if (_explosionAttackRoutine != null) StopCoroutine(_explosionAttackRoutine);
            _attackPresentationRoutine = _explosionAttackRoutine = null;
            _isPresentingAttack = false;
            _combatEffectStarts.Clear();
            DestroyTrailLayer();
        }
        private void OnDisable() => CancelAttackPresentation();
    }
}
