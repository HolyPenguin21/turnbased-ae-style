using System;
using System.Collections.Generic;
using Game.Core;
using Game.Audio;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.Units;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Presentation only. Gameplay publishes committed changes; all legality queries are read-only.
    // Lives with the turn controller, so hiding the hand for a battle does not stop Fate hints.
    public sealed class TutorialHintController : MonoBehaviour
    {
        private sealed class Candidate
        {
            public TutorialHintId Id;
            public RectTransform Target;
            public Func<bool> Relevant;
            public bool IsDropTarget;
        }
        private static TutorialHintController instance;
        private readonly List<Candidate> candidates = new List<Candidate>();
        private readonly Dictionary<TutorialHintId, TutorialHintView> prefabs = new Dictionary<TutorialHintId, TutorialHintView>();
        private GameTurnController turn;
        private CardHandUI hand;
        private ArmyViewerModalUI armyViewer;
        private PlayerRoot humanRoot;
        private Candidate current;
        private TutorialHintView view;
        private bool dirty = true, canOfferDraw, aircraftPending;
        private float nextHintAt;
        private const float HintPause = 2f;

        internal static bool HumanTurnIsReady => instance != null && instance.HumanTurnReady;
        internal static bool HandInteractionReady => HumanTurnIsReady
            && !instance.turn.CardDraggingBlocked && instance.hand != null
            && !instance.hand.TutorialActionInProgress;
        internal static bool MapInteractionReady => HandInteractionReady && !instance.turn.InputBlocked;

        internal static bool TargetIsVisible(RectTransform target)
        {
            if (target == null || !target.gameObject.activeInHierarchy) return false;
            var canvas = target.GetComponentInParent<Canvas>();
            if (canvas == null) return false;
            Vector3 center = target.TransformPoint(target.rect.center);
            var canvasRect = canvas.rootCanvas.transform as RectTransform;
            if (canvasRect != null && !canvasRect.rect.Contains((Vector2)canvasRect.InverseTransformPoint(center))) return false;
            var scroll = target.GetComponentInParent<ScrollRect>();
            return scroll == null || scroll.viewport == null
                || scroll.viewport.rect.Contains((Vector2)scroll.viewport.InverseTransformPoint(center));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => instance = null;

        internal static void Install(GameTurnController turn, CardHandUI hand, ArmyViewerModalUI armyViewer)
        {
            if (turn == null || hand == null) return;
            var controller = turn.GetComponent<TutorialHintController>();
            if (controller == null) controller = turn.gameObject.AddComponent<TutorialHintController>();
            instance = controller;
            if (controller.turn != null) return;
            controller.turn = turn;
            controller.hand = hand;
            controller.armyViewer = armyViewer;
            turn.TurnStateChanged += controller.MarkDirty;
            turn.InputBlockedChanged += controller.OnBlockedChanged;
            turn.CardDraggingBlockedChanged += controller.OnBlockedChanged;
            if (armyViewer != null) armyViewer.VisibilityChanged += controller.MarkDirty;
            VisionSystem.VisibleContentChanged += controller.OnContentChanged;
            VisionSystem.VisibilityChanged += controller.OnVisibilityChanged;
            ArmyActions.UnitDeployed += controller.OnUnitDeployed;
            TutorialHintProgress.Changed += controller.OnProgressChanged;
            controller.BindRoot();
            Offer(TutorialHintId.Draw, hand.TutorialDrawButton?.transform as RectTransform,
                () => controller.MapInputReady && controller.canOfferDraw && hand.TutorialInputReady
                    && hand.TutorialDrawButton != null && hand.TutorialDrawButton.IsInteractable());
            Offer(TutorialHintId.EndTurn, turn.TutorialEndTurnButton?.transform as RectTransform,
                () => controller.MapInputReady && controller.humanRoot != null
                    && controller.humanRoot.ActionPoints <= 0 && !hand.TutorialActionInProgress
                    && turn.TutorialEndTurnButton != null && turn.TutorialEndTurnButton.IsInteractable());
            Offer(TutorialHintId.CreateArmy, armyViewer?.TutorialCreateArmyButton?.transform as RectTransform,
                () => armyViewer != null && armyViewer.CanOfferCreateArmyTutorial
                    && !turn.CardDraggingBlocked && !hand.TutorialActionInProgress);
        }

        private bool HumanTurnReady => turn != null && turn.CurrentPlayer != null
            && turn.CurrentPlayer == GameSession.FindHumanPlayer() && turn.TurnConfirmed;
        private bool MapInputReady => HumanTurnReady && !turn.InputBlocked;

        internal static void Invalidate() { if (instance != null) instance.dirty = true; }
        private void MarkDirty() => dirty = true;
        private void OnBlockedChanged(bool blocked) => MarkDirty();
        private void OnContentChanged(PlayerSetupData owner, HexCoord hex)
        {
            if (owner == GameSession.FindHumanPlayer()) MarkDirty();
        }
        private void OnVisibilityChanged(PlayerSetupData owner)
        {
            if (owner == GameSession.FindHumanPlayer()) MarkDirty();
        }
        private void BindRoot()
        {
            var root = GameSession.FindHumanRoot();
            if (humanRoot == root) return;
            if (humanRoot != null) humanRoot.ResourcesChanged -= MarkDirty;
            humanRoot = root;
            if (humanRoot != null) humanRoot.ResourcesChanged += MarkDirty;
            MarkDirty();
        }
        private void OnProgressChanged()
        {
            MarkDirty();
            if (!TutorialHintProgress.Enabled) HideView();
        }

        private void OnUnitDeployed(ArmyData army, UnitData unit)
        {
            MarkDirty();
            if (unit == null || !unit.IsAviation || army?.Owner != GameSession.FindHumanPlayer()
                || !TutorialHintProgress.Enabled || TutorialHintProgress.HasSeen(TutorialHintId.Aircraft)) return;
            aircraftPending = true;
            Offer(TutorialHintId.Aircraft, hand.TutorialAircraftAnchor,
                () => aircraftPending && HumanTurnReady && hand != null && hand.isActiveAndEnabled
                    && !turn.CardDraggingBlocked && !hand.TutorialActionInProgress
                    && (!turn.InputBlocked || (armyViewer != null && armyViewer.IsShowing
                        && !armyViewer.IsReadOnly && !armyViewer.IsRenamePopupShowing)));
        }

        // Replaces the same topic instead of queuing duplicates. Each candidate is revalidated
        // at presentation time; stale challenge closures never produce a hint in another popup.
        internal static void Offer(TutorialHintId id, RectTransform target, Func<bool> relevant)
        {
            if (instance == null || target == null || relevant == null) return;
            var candidate = instance.candidates.Find(c => c.Id == id);
            if (candidate == null)
            {
                candidate = new Candidate { Id = id };
                instance.candidates.Add(candidate);
            }
            candidate.Target = target;
            candidate.Relevant = relevant;
            candidate.IsDropTarget = false;
        }

        // A tactical slot accepts drops even while its click-to-place Button is disabled.
        internal static void OfferDropTarget(TutorialHintId id, RectTransform target, Func<bool> relevant)
        {
            Offer(id, target, relevant);
            var candidate = instance?.candidates.Find(c => c.Id == id);
            if (candidate != null) candidate.IsDropTarget = true;
        }

        internal static void Withdraw(TutorialHintId id)
        {
            if (instance == null) return;
            var candidate = instance.candidates.Find(c => c.Id == id);
            if (candidate != null) candidate.Target = null;
        }

        // A successful player action also teaches the topic when its bubble was never shown.
        // Keep Complete's visible-hint contract for the original five topics.
        internal static void Learned(TutorialHintId id)
        {
            TutorialHintProgress.Complete(id);
            if (instance != null && instance.current?.Id == id)
            {
                instance.nextHintAt = Time.unscaledTime + HintPause;
                instance.HideView();
            }
        }

        private static int Priority(TutorialHintId id)
        {
            switch (id)
            {
                case TutorialHintId.Fate: return 0;
                case TutorialHintId.Tactic: return 1;
                case TutorialHintId.EditTactic: return 1;
                case TutorialHintId.AttemptCost: return 1;
                case TutorialHintId.CreateArmy: return 2;
                case TutorialHintId.TransferUnit: return 3;
                case TutorialHintId.Repair: return 3;
                case TutorialHintId.Aircraft: return 4;
                case TutorialHintId.Upgrade: return 5;
                case TutorialHintId.MutagenProduction: return 6;
                case TutorialHintId.EquipmentProduction: return 6;
                case TutorialHintId.BuildBase: return 7;
                case TutorialHintId.BuildFacility: return 7;
                case TutorialHintId.EndTurn: return 8;
                default: return 9;
            }
        }

        private void LateUpdate()
        {
            BindRoot();
            if (!TutorialHintProgress.Enabled || UIFocusUtility.IsGameplayInputBlocked
                || (turn != null && turn.TutorialMessageShowing))
            {
                HideView();
                return;
            }
            // Resource, hand, turn and visible-world changes coalesce here after transactions
            // finish. Do not run card/world searches every frame, or query half-committed rosters.
            if (dirty)
            {
                dirty = false;
                canOfferDraw = !TutorialHintProgress.HasSeen(TutorialHintId.Draw)
                    && MapInputReady && hand != null && hand.CanOfferDrawTutorial();
                hand?.RefreshContextTutorials();
                armyViewer?.RefreshContextTutorials();
            }
            Candidate next = null;
            foreach (var candidate in candidates)
            {
                if (TutorialHintProgress.HasSeen(candidate.Id) || candidate.Target == null
                    || !candidate.Target.gameObject.activeInHierarchy || !candidate.Relevant()
                    || ((int)candidate.Id >= (int)TutorialHintId.TransferUnit && !TargetIsVisible(candidate.Target))) continue;
                var control = candidate.Target.GetComponent<Selectable>();
                if (!candidate.IsDropTarget && control != null
                    && (!control.isActiveAndEnabled || !control.IsInteractable())) continue;
                if (next == null || Priority(candidate.Id) < Priority(next.Id)) next = candidate;
            }
            if (next == null) { HideView(); return; }
            if (view == null && Time.unscaledTime < nextHintAt) return;
            if (current != next || view == null)
            {
                HideView();
                if (!prefabs.TryGetValue(next.Id, out var prefab))
                {
                    var asset = GameAssetCatalog.Load<GameObject>("UI/TutorialHints/" + next.Id + "Hint");
                    prefab = asset != null ? asset.GetComponent<TutorialHintView>() : null;
                    prefabs[next.Id] = prefab;
                }
                var canvas = next.Target.GetComponentInParent<Canvas>();
                if (prefab == null || canvas == null) return;
                current = next;
                view = Instantiate(prefab, canvas.rootCanvas.transform, false);
                SceneUIAudioBinder.BindCreatedRoot(view);
                view.Dismissed += OnDismissed;
            }
            view.Follow(next.Target);
        }

        private void OnDismissed()
        {
            if (current == null) return;
            TutorialHintProgress.Complete(current.Id);
            nextHintAt = Time.unscaledTime + HintPause;
            HideView();
        }
        internal static void Complete(TutorialHintId id)
        {
            if (instance == null || instance.current?.Id != id || instance.view == null
                || !instance.view.gameObject.activeInHierarchy) return;
            TutorialHintProgress.Complete(id);
            instance.nextHintAt = Time.unscaledTime + HintPause;
            instance.HideView();
        }
        private void HideView()
        {
            if (view != null)
            {
                view.Dismissed -= OnDismissed;
                view.gameObject.SetActive(false);
                Destroy(view.gameObject);
            }
            view = null;
            current = null;
        }
        private void OnDisable() => HideView();
        private void OnDestroy()
        {
            if (turn != null)
            {
                turn.TurnStateChanged -= MarkDirty;
                turn.InputBlockedChanged -= OnBlockedChanged;
                turn.CardDraggingBlockedChanged -= OnBlockedChanged;
            }
            if (armyViewer != null) armyViewer.VisibilityChanged -= MarkDirty;
            if (humanRoot != null) humanRoot.ResourcesChanged -= MarkDirty;
            VisionSystem.VisibleContentChanged -= OnContentChanged;
            VisionSystem.VisibilityChanged -= OnVisibilityChanged;
            ArmyActions.UnitDeployed -= OnUnitDeployed;
            TutorialHintProgress.Changed -= OnProgressChanged;
            HideView();
            if (instance == this) instance = null;
        }
    }
}
