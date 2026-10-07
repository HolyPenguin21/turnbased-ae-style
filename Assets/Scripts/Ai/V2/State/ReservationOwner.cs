using System;
using Game.HexGrid;

namespace Game.Ai.V2
{
    // A typed owner over the existing identity and bank token, never a new operation id.
    public sealed class ReservationOwner : IEquatable<ReservationOwner>
    {
        public MissionIntentKey? Operation { get; }
        public string Token { get; }
        private ReservationOwner(MissionIntentKey? operation, string token)
        { Operation = operation; Token = token; }

        public static ReservationOwner ForOperation(MissionIntentKey key) =>
            new ReservationOwner(key, key.Kind == MissionKind.Economy
                ? EconomyMissionPlanner.OwnerKey(StableMissionKey.ForEconomy(
                    (EconomyTaskKind)key.SubKind, key.ObjectiveId, new HexCoord(key.Q, key.R)))
                : key.ToString());
        public static ReservationOwner ForPass(string token) =>
            string.IsNullOrEmpty(token) ? null : new ReservationOwner(null, token);

        // Compatibility for existing bank/test callers. Production operation writers pass
        // ForOperation; pass holds intentionally have no durable mission identity.
        public static implicit operator ReservationOwner(string token) => ForPass(token);
        public static implicit operator string(ReservationOwner owner) => owner?.Token;
        public bool Equals(ReservationOwner other) => other != null
            && StringComparer.Ordinal.Equals(Token, other.Token);
        public override bool Equals(object obj) => obj is ReservationOwner owner ? Equals(owner)
            : obj is string token && StringComparer.Ordinal.Equals(Token, token);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Token);
        public override string ToString() => Token;
    }
}
