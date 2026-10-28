namespace Game.UI
{
    public partial class CardHandUI
    {
        internal bool HasPendingCardOperation => IsAttachMode || _tacticChallengeActive || _tacticFlipping
            || _cards.Exists(card => card != null && card.IsDragging)
            || _tacticCards.Exists(card => card != null && card.IsDragging);
    }
}
