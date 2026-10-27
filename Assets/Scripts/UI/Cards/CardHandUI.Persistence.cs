namespace Game.UI
{
    public partial class CardHandUI
    {
        internal bool HasPendingCardOperation => IsAttachMode || _cards.Exists(card => card != null && card.IsDragging);
    }
}
