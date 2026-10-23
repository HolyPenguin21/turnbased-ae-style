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
        }

        private static int Priority(TutorialHintId id)
        {
            switch (id)
            {
                case TutorialHintId.Fate: return 0;
                case TutorialHintId.CreateArmy: return 1;
                case TutorialHintId.Aircraft: return 2;
                case TutorialHintId.EndTurn: return 3;
                default: return 4;
            }
        }

        private void LateUpdate()
        {
            BindRoot();
            if (!TutorialHintProgress.Enabled || UIFocusUtility.IsGameplayInputBlocked)
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
            }
            Candidate next = null;
            foreach (var candidate in candidates)
            {
                if (TutorialHintProgress.HasSeen(candidate.Id) || candidate.Target == null
                    || !candidate.Target.gameObject.activeInHierarchy || !candidate.Relevant()) continue;
                var control = candidate.Target.GetComponent<Selectable>();
                if (control != null && (!control.isActiveAndEnabled || !control.IsInteractable())) continue;
                if (next == null || Priority(candidate.Id) < Priority(next.Id)) next = candidate;
            }
            if (next == null) { HideView(); return; }
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
            HideView();
        }
        internal static void Complete(TutorialHintId id)
        {
            if (instance == null || instance.current?.Id != id || instance.view == null
                || !instance.view.gameObject.activeInHierarchy) return;
            TutorialHintProgress.Complete(id);
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
