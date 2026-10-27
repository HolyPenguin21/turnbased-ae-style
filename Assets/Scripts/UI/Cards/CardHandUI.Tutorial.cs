using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public partial class CardHandUI
    {
        internal Button TutorialDrawButton => drawButton;
        internal RectTransform TutorialAircraftAnchor => handContainer;
        // Viewing tactical cards is not an action lock for End Turn or Create Army.
        internal bool TutorialActionInProgress => IsAttachMode || _tacticFlipping
            || _cards.Any(c => c != null && c.IsDragging)
            || _tacticCards.Any(c => c != null && c.IsDragging);
        internal bool TutorialInputReady => isActiveAndEnabled && CanDragCards()
            && !TutorialActionInProgress;
        internal bool TutorialTacticInputReady => isActiveAndEnabled && _showingTactics
            && !TutorialActionInProgress;

        internal void RefreshContextTutorials()
        {
            hexSelection?.RefreshContextTutorials();
            foreach (var id in new[] { TutorialHintId.Upgrade, TutorialHintId.BuildBase, TutorialHintId.BuildFacility })
                TutorialHintController.Withdraw(id);
            if (!TutorialHintController.HandInteractionReady || !TutorialInputReady) return;
            var root = FindHumanRoot();
            var owner = FindHumanPlayer();
            if (root == null || owner == null) return;
            // These searches only run after committed hand/world/resource changes, not per frame.
            foreach (var view in _cards)
            {
                var card = view != null ? view.Data : null;
                if (card?.Definition == null || !view.gameObject.activeInHierarchy) continue;
                var type = card.Definition.cardType;
                if (type == CardType.Equipment && !TutorialHintProgress.HasSeen(TutorialHintId.Upgrade))
                {
                    bool hasTarget = HumanHand.Any(host => host != card && EquipmentSystem.CanAttach(card, host, root, out _))
                        || ArmyRegistry.AllForOwner(owner).Where(a => !a.IsPrison).SelectMany(a => a.Members)
                            .Any(unit => EquipmentSystem.CanAttach(card, unit, root, out _));
                    if (hasTarget)
                    {
                        TutorialHintController.Offer(TutorialHintId.Upgrade, view.transform as RectTransform,
                            () => TutorialHintController.HandInteractionReady && TutorialInputReady
                                && _cards.Contains(view) && view.Data == card);
                    }
                }
                else if ((type == CardType.Base || type == CardType.Facility)
                    && CanOfferBuildingTutorial(card, owner, root))
                {
                    var id = type == CardType.Base ? TutorialHintId.BuildBase : TutorialHintId.BuildFacility;
                    if (TutorialHintProgress.HasSeen(id)) continue;
                    TutorialHintController.Offer(id, view.transform as RectTransform,
                        () => TutorialHintController.HandInteractionReady && TutorialInputReady
                            && _cards.Contains(view) && view.Data == card
                            && CanOfferBuildingTutorial(card, owner, root));
                }
            }
        }

        private bool CanOfferBuildingTutorial(CardData card, PlayerSetupData owner, PlayerRoot root)
        {
            if (card.Definition.cardType == CardType.Facility && baseViewerModal != null && baseViewerModal.IsShowing)
            {
                var building = baseViewerModal.CurrentBuilding;
                return building != null && building.Owner == owner && baseViewerModal.CanManageCurrentBuilding
                    && InfrastructureActions.CanPlaceFacility(card.Definition, building.Hex, owner,
                        card.EffectivePlayApCost, card.EffectivePlayResourceCost, out _);
            }
            return TutorialHintController.MapInteractionReady && hexSelection != null
                && hexSelection.CanOfferBuildTutorial(card, owner, root);
        }

        internal bool CanOfferDrawTutorial()
        {
            if (!TutorialInputReady || _humanHand.RemainingDeckCount == 0 || !_humanHand.HasFreeSlot)
                return false;
            var root = FindHumanRoot();
            var owner = FindHumanPlayer();
            if (root == null || owner == null || !root.CanSpendActionPoints(drawApCost))
                return false;

            // Use gameplay's read-only validators, never attempt a transaction as a probe.
            var armies = ArmyRegistry.AllForOwner(owner).ToList();
            foreach (CardData card in HumanHand)
            {
                if (card?.Definition == null) continue;
                switch (card.Definition.cardType)
                {
                    case CardType.Unit:
                    case CardType.Hero:
                        if (armies.Any(a => ArmyActions.CanDeployUnitFromCard(card, owner, a, root, hexSelection)))
                            return false;
                        break;
                    case CardType.Base:
                        if (armies.Any(a => InfrastructureActions.CanFoundBase(card.Definition, a.Hex, owner,
                                card.EffectivePlayApCost, card.EffectivePlayResourceCost, out _, hexSelection?.Map)))
                            return false;
                        break;
                    case CardType.Facility:
                        if (BuildingRegistry.AllBuildings().Any(b => b.Owner == owner
                            && InfrastructureActions.CanPlaceFacility(card.Definition, b.Hex, owner,
                                card.EffectivePlayApCost, card.EffectivePlayResourceCost, out _)))
                            return false;
                        break;
                    case CardType.Equipment:
                        if (HumanHand.Any(host => host != card && EquipmentSystem.CanAttach(card, host, root, out _))
                            || armies.Where(a => !a.IsPrison).SelectMany(a => a.Members)
                                .Any(unit => EquipmentSystem.CanAttach(card, unit, root, out _)))
                            return false;
                        break;
                }
            }
            return true;
        }
    }
}
