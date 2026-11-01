namespace Game.Core
{
    // Outstanding gameplay input retains its lifetime independently of panel visibility.
    public sealed class MatchDecision
    {
        private readonly long generation = GameSession.Generation;
        private bool pending = true;
        public bool IsPending => pending && GameSession.IsCurrent(generation);
        public bool TryComplete() { if (!IsPending) return false; pending = false; return true; }
        public void Cancel() => pending = false;
    }
}
