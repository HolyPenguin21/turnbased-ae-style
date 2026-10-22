using System.Linq;
using Game.Cards;
using Game.Map;
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

        internal bool CanOfferDrawTutorial()
        {
            if (!TutorialInputReady || _remainingDeck.Count == 0 || _cards.Count >= maxHandSize)
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
