using System;

namespace Game.Setup
{
    // Only surrounds synchronous reconstruction. Owners suppress gameplay notifications while
    // data is incomplete. Disposal deliberately does not teach vision or execute new-turn rules.
    public sealed class MatchRestoreScope : IDisposable
    {
        private static int depth;
        private bool disposed;
        public static bool IsRestoring => depth > 0;
        private MatchRestoreScope() { depth++; }
        public static MatchRestoreScope Begin() => new MatchRestoreScope();
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            depth--;
        }
    }
}
