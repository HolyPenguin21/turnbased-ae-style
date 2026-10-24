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
        // Locks result acknowledgements and the sequence of Explosion challenges, never playback.
        private bool _isPresentingAttack;
        private Coroutine _explosionAttackRoutine;
        private int _attackPresentationGeneration;
        private readonly List<(RectTransform Layer, float ExpiresAt)> _attackEffectLayers =
            new List<(RectTransform Layer, float ExpiresAt)>();

        private BattleGridCellUI FindUnitCell(UnitData unit)
        {
            if (unit == null) return null;
            foreach (var cell in _cells) if (cell != null && cell.Unit == unit) return cell;
            return null;
        }

        private void StartAttackPresentation(UnitData attacker, BattleAttackApplication application, Action onDone)
        {
            PlayAttackEffects(attacker, application);
            RefreshGrid();
            onDone?.Invoke();
        }

        private void PlayAttackEffects(UnitData attacker, BattleAttackApplication application)
        {
            if (!HasInteractiveParticipant || application == null) return;
            if (application.SecondaryHits.Count == 0 && !application.BerserkTriggered
                && application.InterceptedBy == null && !application.ShockTriggered) return;
            var parent = gridContainer != null ? gridContainer.parent as RectTransform : transform as RectTransform;
            if (parent == null) return;

            var go = new GameObject("BattleSkillEffects", typeof(RectTransform), typeof(LayoutElement));
            var layer = (RectTransform)go.transform;
            BattleDamageFlipbook.Stretch(layer, parent);
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            float duration = 0f;
            var anchors = new Dictionary<UnitData, RectTransform>();

            // Grid refresh destroys its cards on the same frame. Copy only their rectangles,
            // in normalized layer coordinates, so even lethal hits keep playing and resize with
            // the canvas. Nothing here retains a live card or delays the next combat action.
            RectTransform Anchor(UnitData unit)
            {
                if (unit == null) return null;
                if (anchors.TryGetValue(unit, out var existing)) return existing;
                var cell = FindUnitCell(unit);
                if (cell == null || cell.RectTransform == null) return null;
                var card = cell.RectTransform;
                var anchor = (RectTransform)new GameObject("EffectCard", typeof(RectTransform)).transform;
                anchor.SetParent(layer, false);
                anchor.anchorMin = Rect.PointToNormalized(layer.rect,
                    layer.InverseTransformPoint(card.TransformPoint(card.rect.min)));
                anchor.anchorMax = Rect.PointToNormalized(layer.rect,
                    layer.InverseTransformPoint(card.TransformPoint(card.rect.max)));
                anchor.offsetMin = anchor.offsetMax = Vector2.zero;
                anchors.Add(unit, anchor);
                return anchor;
            }
            void Overlay(UnitData unit, string sprite)
            {
                var anchor = Anchor(unit);
                if (anchor == null) return;
                BattleHealEffect.Show(anchor, Time.unscaledTime, sprite);
                duration = Mathf.Max(duration, BattleHealEffect.Duration);
            }

            if (application.BerserkTriggered) Overlay(application.Victim, BattleHealEffect.DrillSpritePath);
            if (application.InterceptedBy != null) Overlay(application.InterceptedBy, BattleHealEffect.BodyguardSpritePath);
            if (application.ShockTriggered) Overlay(attacker, BattleHealEffect.ShockSpritePath);
            var source = application.SecondaryHits.Count > 0 ? Anchor(application.Victim) : null;
            bool splashSourcePulse = true;
            foreach (var hit in application.SecondaryHits)
            {
                var target = Anchor(hit.Victim);
                if (source != null && target != null)
                {
                    bool fire = hit.Skill == UnitAbilities.Scorcher;
                    BattleDamageFlipbook.Show(layer, source, target, fire, fire || splashSourcePulse);
                    if (!fire) splashSourcePulse = false;
                    duration = Mathf.Max(duration, BattleSkillFlipbook.Duration(fire));
                }
                if (hit.BerserkTriggered) Overlay(hit.Victim, BattleHealEffect.DrillSpritePath);
            }
            if (duration > 0f) _attackEffectLayers.Add((layer, Time.unscaledTime + duration));
            else { go.SetActive(false); Destroy(go); }
        }

        private IEnumerator PresentExplosionHit(UnitData attacker, BattleAttackApplication application)
        {
            PlayAttackEffects(attacker, application);
            _isPresentingAttack = false;
            RefreshGrid();
            _isPresentingAttack = true;
            yield break;
        }

        private void RetireFinishedAttackEffects()
        {
            for (int i = _attackEffectLayers.Count - 1; i >= 0; i--)
            {
                var effect = _attackEffectLayers[i];
                if (effect.Layer != null && Time.unscaledTime < effect.ExpiresAt) continue;
                if (effect.Layer != null)
                { effect.Layer.gameObject.SetActive(false); Destroy(effect.Layer.gameObject); }
                _attackEffectLayers.RemoveAt(i);
            }
        }
        private void CancelAttackPresentation()
        {
            _attackPresentationGeneration++;
            if (_explosionAttackRoutine != null) StopCoroutine(_explosionAttackRoutine);
            _explosionAttackRoutine = null;
            _isPresentingAttack = false;
            foreach (var effect in _attackEffectLayers)
                if (effect.Layer != null)
                { effect.Layer.gameObject.SetActive(false); Destroy(effect.Layer.gameObject); }
            _attackEffectLayers.Clear();
        }
        private void OnDisable() => CancelAttackPresentation();
    }
}
