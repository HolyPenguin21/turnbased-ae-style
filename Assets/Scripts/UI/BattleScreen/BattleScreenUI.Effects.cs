using Game.Presentation;
using System;
using System.Collections;
using System.Collections.Generic;
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

        private void StartAttackPresentation(BattleAttackApplication application)
        {
            PlayDestroyedCardEffects();
            PlayAttackEffects(application);
        }

        private void PlayDestroyedCardEffects(UnitData executedHero = null)
        {
            if (_presentationSuppressed || panelRoot == null || !panelRoot.activeInHierarchy || _grid == null
                || gridContainer == null || !(gridContainer.parent is RectTransform parent))
                return;

            RectTransform layer = null;
            foreach (BattleGridCellUI cell in _cells)
            {
                UnitData unit = cell != null ? cell.Unit : null;
                // Use the old rendered cell for the art, and the live grid for removal.
                // Capture/Kill can remove a hero without writing zero HP.
                if (unit == null || (unit.HitPointsCurrent > 0 && unit != executedHero)
                    || _grid.TryFindPosition(unit, out _, out _))
                    continue;

                Image source = cell.CardArt;
                if (source == null || !source.isActiveAndEnabled) continue;
                Sprite art = source.overrideSprite;
                var material = CardDissolveEffect.CreateMaterial(art, source.color, overlay: true);
                if (material == null) continue;
                if (layer == null)
                {
                    layer = CreateAttackEffectLayer(parent, "BattleCardDissolves");
                    layer.SetSiblingIndex(gridContainer.GetSiblingIndex() + 1);
                }

                var go = new GameObject("Destroyed Card", typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)go.transform;
                AnchorToCard(rect, source.rectTransform, layer);
                rect.pivot = source.rectTransform.pivot;
                var image = go.GetComponent<Image>();
                image.sprite = art;
                image.preserveAspect = source.preserveAspect;
                image.raycastTarget = false;
                image.maskable = false;
                image.material = material;
                CardDissolveEffect.Play(go, material);
            }

            if (layer != null)
                _attackEffectLayers.Add((layer, Time.unscaledTime + CardDissolveEffect.Duration));
        }

        private static RectTransform CreateAttackEffectLayer(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
            var layer = (RectTransform)go.transform;
            layer.SetParent(parent, false);
            layer.anchorMin = Vector2.zero;
            layer.anchorMax = Vector2.one;
            layer.offsetMin = layer.offsetMax = Vector2.zero;
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            return layer;
        }

        private static void AnchorToCard(RectTransform anchor, RectTransform card, RectTransform layer)
        {
            anchor.SetParent(layer, false);
            anchor.anchorMin = Rect.PointToNormalized(layer.rect,
                layer.InverseTransformPoint(card.TransformPoint(card.rect.min)));
            anchor.anchorMax = Rect.PointToNormalized(layer.rect,
                layer.InverseTransformPoint(card.TransformPoint(card.rect.max)));
            anchor.offsetMin = anchor.offsetMax = Vector2.zero;
        }

        private void PlayAttackEffects(BattleAttackApplication application)
        {
            if (!HasInteractiveParticipant || application == null) return;
            bool secondaryBerserk = false;
            foreach (var hit in application.SecondaryHits)
            {
                if (hit.BerserkTriggered) { secondaryBerserk = true; break; }
            }
            if (!secondaryBerserk && !application.BerserkTriggered &&
                application.InterceptedBy == null && !application.ShockTriggered) return;
            var parent = gridContainer != null ? gridContainer.parent as RectTransform : transform as RectTransform;
            if (parent == null) return;

            var layer = CreateAttackEffectLayer(parent, "BattleSkillEffects");
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
                AnchorToCard(anchor, card, layer);
                anchors.Add(unit, anchor);
                return anchor;
            }
            void Overlay(UnitData unit, BattleSkillEffect.Kind skill)
            {
                var anchor = Anchor(unit);
                if (anchor == null) return;
                BattleSkillEffect.Show(anchor, Time.unscaledTime, skill);
                duration = Mathf.Max(duration, BattleSkillEffect.Duration);
            }

            if (application.BerserkTriggered) Overlay(application.Victim, BattleSkillEffect.Kind.Berserk);
            if (application.InterceptedBy != null) Overlay(application.InterceptedBy, BattleSkillEffect.Kind.Bodyguard);
            if (application.ShockTriggered) Overlay(application.Victim, BattleSkillEffect.Kind.Shock);

            // Splash and Scorcher still deal secondary damage and retain their result
            // popups. Only Berserk reactions on their secondary victims show VFX.
            foreach (var hit in application.SecondaryHits)
                if (hit.BerserkTriggered) Overlay(hit.Victim, BattleSkillEffect.Kind.Berserk);
            if (duration > 0f) _attackEffectLayers.Add((layer, Time.unscaledTime + duration));
            else { layer.gameObject.SetActive(false); Destroy(layer.gameObject); }
        }

        private IEnumerator PresentExplosionHit(BattleAttackApplication application)
        {
            _isPresentingAttack = false;
            StartAttackPresentation(application);
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
            if (_explosionAttackRoutine != null) StopBattleRoutine(_explosionAttackRoutine);
            _explosionAttackRoutine = null;
            _isPresentingAttack = false;
            foreach (var effect in _attackEffectLayers)
                if (effect.Layer != null)
                { effect.Layer.gameObject.SetActive(false); Destroy(effect.Layer.gameObject); }
            _attackEffectLayers.Clear();
        }
        private void OnDisable()
        {
            UIFocusUtility.SetModal(this, null);
            CancelAiArrangement();
            CancelMovePresentation();
            CancelAttackPresentation();
        }
    }
}
